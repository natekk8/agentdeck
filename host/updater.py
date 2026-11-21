"""GitHub Releases checker, background installer downloader, and self-cleaning silent updater."""

import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import threading
from urllib.error import HTTPError, URLError
from urllib.parse import urlsplit
from urllib.request import Request, urlopen

from i18n import DEFAULT_LANG, tr
from version import APP_VERSION, GITHUB_REPO

_VER_RE = re.compile(r"(\d+)(?:\.(\d+))?(?:\.(\d+))?")
_SAFE_FILENAME = re.compile(r"[^A-Za-z0-9._\-]")


def parse_version(raw):
    text = str(raw or "").strip()
    if text.lower().startswith("v"):
        text = text[1:]
    match = _VER_RE.search(text)
    if not match:
        return (0, 0, 0)
    parts = [int(match.group(i) or 0) for i in (1, 2, 3)]
    return tuple(parts)


def is_newer(candidate, current=APP_VERSION):
    return parse_version(candidate) > parse_version(current)


def _fetch_json(url, timeout=8):
    req = Request(
        url,
        headers={
            "Accept": "application/vnd.github+json",
            "User-Agent": f"AgentDeck/{APP_VERSION}",
        },
    )
    with urlopen(req, timeout=timeout) as resp:
        return json.loads(resp.read().decode("utf-8"))


def pick_installer_asset(release):
    assets = release.get("assets") or []
    exe_assets = [
        a for a in assets
        if isinstance(a, dict) and str(a.get("name") or "").lower().endswith(".exe")
        and str(a.get("browser_download_url") or "").startswith("https://")
    ]
    if not exe_assets:
        return None
    for a in exe_assets:
        if "setup" in a["name"].lower() or "installer" in a["name"].lower():
            return a
    return exe_assets[0]


def check_for_update(current_version=APP_VERSION, repo=GITHUB_REPO, timeout=8):
    api_base = f"https://api.github.com/repos/{repo}"
    release = None
    try:
        release = _fetch_json(f"{api_base}/releases/latest", timeout=timeout)
    except HTTPError as err:
        if err.code == 404:
            try:
                releases = _fetch_json(f"{api_base}/releases?per_page=3", timeout=timeout)
                if isinstance(releases, list):
                    release = next((r for r in releases if not r.get("draft")), None)
            except (OSError, URLError, HTTPError, ValueError):
                release = None
        else:
            release = None
    except (OSError, URLError, ValueError):
        release = None

    if not isinstance(release, dict):
        return {
            "available": False,
            "current_version": current_version,
            "latest_version": current_version,
        }

    tag = str(release.get("tag_name") or "").strip()
    name = str(release.get("name") or "").strip()
    candidate = tag or name
    parsed_latest = parse_version(candidate)
    latest_label = (
        f"{parsed_latest[0]}.{parsed_latest[1]}.{parsed_latest[2]}"
        if parsed_latest != (0, 0, 0)
        else candidate
    )
    asset = pick_installer_asset(release)
    available = is_newer(candidate, current_version) and asset is not None
    return {
        "available": available,
        "current_version": current_version,
        "latest_version": latest_label,
        "tag": tag,
        "name": name or tag,
        "notes": str(release.get("body") or "")[:1500],
        "published_at": str(release.get("published_at") or ""),
        "asset_name": str(asset.get("name") or "") if asset else "",
        "asset_url": str(asset.get("browser_download_url") or "") if asset else "",
        "asset_size": int(asset.get("size") or 0) if asset else 0,
    }


def _allowed_download_url(url):
    parsed = urlsplit(str(url or ""))
    if parsed.scheme == "https" and parsed.hostname:
        host = parsed.hostname.lower()
        return host == "github.com" or host.endswith(".github.com") or host.endswith(".githubusercontent.com")
    return False


class Updater:
    def __init__(self, install_dir, data_dir):
        self.install_dir = Path(install_dir).resolve()
        self.data_dir = Path(data_dir).resolve()
        self.lock = threading.RLock()
        self.state = {
            "status": "idle",  # idle | downloading | ready | error
            "version": "",
            "downloaded": 0,
            "total": 0,
            "percent": 0,
            "installer_path": "",
            "error": "",
        }
        self._thread = None

    def snapshot(self):
        with self.lock:
            return dict(self.state)

    def start_download(self, asset_url, asset_name, target_version="", lang=DEFAULT_LANG):
        url = str(asset_url or "").strip()
        if not _allowed_download_url(url):
            raise ValueError(tr(lang, "update_invalid_url"))
        clean_name = _SAFE_FILENAME.sub("_", str(asset_name or "AgentDeck-Setup.exe"))
        if not clean_name.lower().endswith(".exe"):
            clean_name += ".exe"

        with self.lock:
            if self.state["status"] == "downloading":
                return dict(self.state)
            temp_dir = Path(tempfile.gettempdir()) / "AgentDeck-Update"
            temp_dir.mkdir(parents=True, exist_ok=True)
            dest = temp_dir / clean_name
            part = temp_dir / (clean_name + ".part")
            self.state.update(
                status="downloading",
                version=str(target_version or ""),
                downloaded=0,
                total=0,
                percent=0,
                installer_path=str(dest),
                error="",
            )

        def worker():
            try:
                req = Request(url, headers={"User-Agent": f"AgentDeck/{APP_VERSION}"})
                with urlopen(req, timeout=30) as resp:
                    total = int(resp.headers.get("Content-Length") or 0)
                    with self.lock:
                        self.state["total"] = total
                    downloaded = 0
                    with part.open("wb") as handle:
                        while True:
                            chunk = resp.read(65536)
                            if not chunk:
                                break
                            handle.write(chunk)
                            downloaded += len(chunk)
                            pct = int(min(100, downloaded * 100 / total)) if total > 0 else 0
                            with self.lock:
                                self.state["downloaded"] = downloaded
                                self.state["percent"] = pct
                part.replace(dest)
                with self.lock:
                    self.state["status"] = "ready"
                    self.state["percent"] = 100
                    self.state["installer_path"] = str(dest)
            except (OSError, URLError, HTTPError, ValueError) as error:
                part.unlink(missing_ok=True)
                with self.lock:
                    self.state["status"] = "error"
                    self.state["error"] = str(error)[:300]

        self._thread = threading.Thread(target=worker, daemon=True)
        self._thread.start()
        return self.snapshot()

    def apply_update(self, lang=DEFAULT_LANG):
        with self.lock:
            installer = Path(self.state.get("installer_path") or "")
            ready = self.state.get("status") == "ready" and installer.is_file()
        if not ready:
            raise ValueError(tr(lang, "update_not_ready"))

        temp_dir = installer.parent
        script = temp_dir / "apply-update.ps1"
        install_dir_str = str(self.install_dir).replace("'", "''")
        installer_str = str(installer).replace("'", "''")
        temp_dir_str = str(temp_dir).replace("'", "''")
        stop_script = (self.install_dir / "host" / "agentdeck.py").resolve()
        stop_str = str(stop_script).replace("'", "''")

        ps_body = f"""$ErrorActionPreference = 'SilentlyContinue'
$installDir = '{install_dir_str}'
$installer = '{installer_str}'
$tempDir = '{temp_dir_str}'

# Wait briefly for AgentDeck UI & bridge to close gracefully
for ($i = 0; $i -lt 30; $i++) {{
    $ui = Get-Process -Name 'AgentDeck' -ErrorAction SilentlyContinue
    $br = Get-CimInstance Win32_Process -Filter "CommandLine LIKE '%agentdeck.py%'" | Where-Object {{ $_.CommandLine -like "*{stop_str}*" }}
    if (-not $ui -and -not $br) {{ break }}
    Start-Sleep -Milliseconds 350
}}
Get-Process -Name 'AgentDeck' -ErrorAction SilentlyContinue | Stop-Process -Force
Get-CimInstance Win32_Process | Where-Object {{ $_.CommandLine -like '*agentdeck.py*' -and $_.CommandLine -like '*--data*' }} | ForEach-Object {{ Stop-Process -Id $_.ProcessId -Force }}
Start-Sleep -Milliseconds 400

if (Test-Path -LiteralPath $installer) {{
    $proc = Start-Process -FilePath $installer -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CLOSEAPPLICATIONS',("/DIR=`"" + $installDir + "`"")) -Wait -PassThru
    Start-Sleep -Milliseconds 600
    Remove-Item -LiteralPath $installer -Force -ErrorAction SilentlyContinue
}}

$exe = Join-Path $installDir 'AgentDeck.exe'
if (Test-Path -LiteralPath $exe) {{
    Start-Process -FilePath $exe -WorkingDirectory $installDir
}}
Start-Sleep -Seconds 2
Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
"""
        script.write_text(ps_body, encoding="utf-8")

        ps_exe = Path(os.environ.get("SystemRoot", "C:/Windows")) / "System32/WindowsPowerShell/v1.0/powershell.exe"
        flags = getattr(subprocess, "CREATE_NO_WINDOW", 0) | getattr(subprocess, "DETACHED_PROCESS", 0) | getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0)
        subprocess.Popen(
            [str(ps_exe), "-NoProfile", "-WindowStyle", "Hidden", "-ExecutionPolicy", "Bypass", "-File", str(script)],
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            close_fds=True,
            creationflags=flags,
        )
        return {"launching_installer": True}
