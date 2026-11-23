"""Local adapters for Codex App Server and Antigravity CLI."""

import json
import math
import os
from pathlib import Path
import queue
import re
import secrets
import shutil
import sqlite3
import subprocess
import threading
import time
from datetime import datetime
from urllib.parse import urlsplit, unquote

from i18n import DEFAULT_LANG, tr


class ProviderError(Exception):
    pass


windows_tools = {}
_AGY_SUB_CACHE = {"at": 0.0, "data": None}
_AGY_SUB_LOCK = threading.Lock()

EFFORT = re.compile(r"\s*\((minimal|low|medium|high|xhigh|max|ultra)\)\s*$", re.I)
EFFORT_ORDER = ("default", "medium", "low", "minimal", "high", "xhigh", "max", "ultra")


def model_name(model):
    return EFFORT.sub("", model["label"]).strip()


def named_models(models):
    """Keep one executable provider model ID per displayed model name."""
    names = {}
    for model in models:
        match = EFFORT.search(model["label"])
        label = model_name(model)
        effort = match[1].lower() if match else "default"
        rank = EFFORT_ORDER.index(effort)
        key = label.casefold()
        if key not in names or rank < names[key][0]:
            names[key] = (rank, {"id": model["id"], "label": label})
    return [row for _, row in names.values()]


def model_choices(models, model):
    """Map the advertised effort choices to executable model IDs."""
    effort = EFFORT.search(model["label"])
    if effort:
        name = model_name(model).casefold()
        choices = {}
        for variant in models:
            match = EFFORT.search(variant["label"])
            if match and model_name(variant).casefold() == name:
                choices.setdefault(match[1].lower(), variant["id"])
        order = ("minimal", "low", "medium", "high", "xhigh", "max", "ultra")
        default = "medium" if "medium" in choices else effort[1].lower()
        return {key: choices[key] for key in order if key in choices}, default
    choices = {key: model["id"] for key in model.get("efforts", [])}
    if not choices:
        return {"default": model["id"]}, "default"
    default = model.get("default_effort")
    return choices, default if default in choices else next(iter(choices))


def _file_uri_to_path(uri):
    try:
        parsed = urlsplit(str(uri))
    except ValueError:
        return ""
    if parsed.scheme != "file" or parsed.netloc not in ("", "localhost"):
        return ""
    local = unquote(parsed.path)
    if os.name == "nt" and re.match(r"^/[a-z]:/", local, re.I):
        local = local[1:]
    try:
        resolved = Path(local).resolve(strict=True)
        if resolved.is_dir():
            return str(resolved)
    except (OSError, RuntimeError, ValueError):
        pass
    return ""


def _is_scratch_workspace(path_obj):
    parts = [part.casefold() for part in path_obj.parts]
    if any(skip in parts for skip in ("appdata", "temp", ".codex", ".gemini", "worktrees", "agentdeck-tools", "agentdeck-data")):
        return True
    if "documents" in parts and "codex" in parts:
        return True
    if "openbot" in parts:
        return True
    return False


def agy_workspaces():
    """Read workspace paths only from existing IDE, CLI and ACP conversation indexes."""
    gemini = Path.home() / ".gemini"
    workspaces = set()
    profiles = [gemini / name for name in ("antigravity", "antigravity-cli", "antigravity-acp")]
    profiles.extend(Path(os.environ[name]) / "Antigravity"
                    for name in ("APPDATA", "LOCALAPPDATA") if os.environ.get(name))
    for profile in profiles:
        index = profile / "conversation_summaries.db"
        if not index.is_file():
            continue
        try:
            with sqlite3.connect(index.as_uri() + "?mode=ro", uri=True, timeout=0.25) as db:
                for (uris,) in db.execute("SELECT workspace_uris FROM conversation_summaries "
                                          "WHERE workspace_uris != '' ORDER BY last_modified_time DESC LIMIT 500"):
                    try:
                        for uri in json.loads(uris):
                            local = _file_uri_to_path(uri)
                            if local:
                                workspaces.add(local)
                    except (TypeError, ValueError):
                        continue
        except (OSError, sqlite3.Error):
            continue
    return sorted(workspaces)


def _read_agy_app_storage():
    appdata = os.environ.get("APPDATA", "")
    if not appdata:
        return {}
    path = Path(appdata) / "Antigravity" / "app_storage.json"
    if not path.is_file():
        return {}
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
        return data if isinstance(data, dict) else {}
    except (OSError, ValueError):
        return {}


def agy_projects():
    """Read projects registered in Antigravity IDE and recent IDE/CLI workspaces."""
    config_dir = Path.home() / ".gemini" / "config" / "projects"
    storage = _read_agy_app_storage()
    order_raw = storage.get("projectsOrder")
    if isinstance(order_raw, str):
        try:
            order_raw = json.loads(order_raw)
        except ValueError:
            order_raw = []
    order = order_raw if isinstance(order_raw, list) else []
    order_pos = {str(pid): idx for idx, pid in enumerate(order)}

    entries = {}
    if config_dir.is_dir():
        for file_path in config_dir.glob("*.json"):
            try:
                doc = json.loads(file_path.read_text(encoding="utf-8"))
            except (OSError, ValueError):
                continue
            if not isinstance(doc, dict) or doc.get("sidecar"):
                continue
            pid = str(doc.get("id") or file_path.stem).strip()
            if not pid or pid == "default-cli-project":
                continue
            resources = (doc.get("projectResources") or {}).get("resources") or []
            folder = ""
            for res in resources:
                if not isinstance(res, dict):
                    continue
                uri = res.get("folderUri") or (res.get("gitFolder") or {}).get("folderUri") or ""
                folder = _file_uri_to_path(uri)
                if folder:
                    break
            if not folder:
                continue
            key = os.path.normcase(folder)
            if key not in entries:
                entries[key] = {
                    "path": folder,
                    "label": str(doc.get("name") or Path(folder).name).strip() or Path(folder).name,
                    "provider": "agy",
                    "agy_project": pid,
                    "_order": order_pos.get(pid, len(order_pos) + 1000),
                }

    ordered = sorted(entries.values(), key=lambda row: (row["_order"], row["label"].casefold()))
    result = [{k: v for k, v in row.items() if k != "_order"} for row in ordered]
    known_paths = {os.path.normcase(row["path"]) for row in result}

    for ws in agy_workspaces():
        norm = os.path.normcase(ws)
        if norm in known_paths:
            continue
        p = Path(ws)
        if p == Path.home() or p.parent == p or _is_scratch_workspace(p):
            continue
        known_paths.add(norm)
        result.append({
            "path": ws,
            "label": p.name or ws,
            "provider": "agy",
            "agy_project": "",
        })
    return result


def codex_projects(thread_cwds=()):
    """Read projects registered in Codex IDE global state plus recent Codex thread working dirs."""
    state_path = Path.home() / ".codex" / ".codex-global-state.json"
    result = []
    known = set()
    if state_path.is_file():
        try:
            state = json.loads(state_path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            state = {}
        local_projects = state.get("local-projects") or {}
        if isinstance(local_projects, dict):
            order = [str(x) for x in (state.get("project-order") or [])]
            ordered_ids = order + [pid for pid in local_projects if pid not in order]
            for pid in ordered_ids:
                proj = local_projects.get(pid)
                if not isinstance(proj, dict):
                    continue
                roots = proj.get("rootPaths") or []
                chosen = ""
                for candidate in roots:
                    try:
                        resolved = Path(str(candidate)).resolve(strict=True)
                        if resolved.is_dir():
                            chosen = str(resolved)
                            break
                    except (OSError, RuntimeError, ValueError):
                        continue
                if not chosen:
                    continue
                norm = os.path.normcase(chosen)
                if norm in known:
                    continue
                known.add(norm)
                label = str(proj.get("name") or Path(chosen).name).strip() or Path(chosen).name
                result.append({
                    "path": chosen,
                    "label": label,
                    "provider": "codex",
                    "codex_id": str(proj.get("id") or pid),
                })

    for cwd in thread_cwds:
        try:
            resolved = Path(str(cwd)).resolve(strict=True)
            if not resolved.is_dir() or resolved == Path.home() or resolved.parent == resolved or _is_scratch_workspace(resolved):
                continue
        except (OSError, RuntimeError, ValueError):
            continue
        chosen = str(resolved)
        norm = os.path.normcase(chosen)
        if norm in known:
            continue
        known.add(norm)
        result.append({
            "path": chosen,
            "label": resolved.name or chosen,
            "provider": "codex",
            "codex_id": "",
        })
    return result


def agy_conversation(ident):
    index = Path.home() / ".gemini/antigravity/conversation_summaries.db"
    try:
        with sqlite3.connect(index.as_uri() + "?mode=ro", uri=True, timeout=0.25) as db:
            row = db.execute("SELECT title, project_id FROM conversation_summaries WHERE conversation_id=?",
                             (ident,)).fetchone()
    except (OSError, sqlite3.Error):
        row = None
    return row if row else ("", "")


def executable(name, lang=DEFAULT_LANG):
    path = shutil.which(name)
    if path:
        return path
    if os.name == "nt":
        remembered = windows_tools.get(name)
        if remembered and remembered.is_file():
            return str(remembered)
        local_apps = Path(os.environ.get("LOCALAPPDATA", ""))
        candidate = None
        if name == "agy":
            candidate = local_apps / "agy" / "bin" / "agy.exe"
        elif name == "git":
            candidate = Path(os.environ.get("ProgramFiles", "C:/Program Files")) / "Git" / "cmd" / "git.exe"
        elif name == "codex":
            powershell = (Path(os.environ.get("SystemRoot", "C:/Windows")) /
                          "System32/WindowsPowerShell/v1.0/powershell.exe")
            try:
                installed = capture([str(powershell), "-NoProfile", "-NonInteractive", "-Command",
                                     "Get-AppxPackage -Name OpenAI.Codex | Sort-Object Version -Descending | "
                                     "Select-Object -First 1 -ExpandProperty InstallLocation"],
                                    timeout=8, lang=lang).strip()
            except (OSError, ProviderError):
                installed = ""
            if installed:
                candidate = Path(installed) / "app" / "resources" / "codex.exe"
        if candidate and candidate.is_file():
            windows_tools[name] = candidate
            return str(candidate)
    raise ProviderError(tr(lang, "tool_not_found", name=name))


def spawn(args, **options):
    return subprocess.Popen(
        args, encoding="utf-8", errors="replace", text=True, bufsize=1,
        creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
        **options,
    )


def capture(args, cwd=None, timeout=20, lang=DEFAULT_LANG):
    with spawn(args, cwd=cwd, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
               stderr=subprocess.PIPE) as proc:
        try:
            out, err = proc.communicate(timeout=timeout)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.communicate()
            raise ProviderError(tr(lang, "provider_timeout")) from None
        if proc.returncode:
            raise ProviderError((err or out).strip()[-480:] or tr(lang, "provider_error"))
        return out


def kill_tree(proc):
    if proc.poll() is not None:
        return
    if os.name == "nt":
        try:
            capture(["taskkill.exe", "/PID", str(proc.pid), "/T", "/F"], timeout=5)
            return
        except ProviderError:
            if proc.poll() is not None:
                return
    proc.kill()


class Rpc:
    def __init__(self, config=(), lang=DEFAULT_LANG, on_request=None):
        self.lang = lang
        self.on_request = on_request
        overrides = [part for setting in config for part in ("-c", setting)]
        self.proc = spawn(
            [executable("codex", lang=lang), "app-server", "--listen", "stdio://", *overrides],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
        )
        self.inbox = queue.Queue()
        self.deferred = []
        self.next_id = 0
        threading.Thread(target=self.read, daemon=True).start()

    def read(self):
        try:
            for line in self.proc.stdout:
                try:
                    self.inbox.put(json.loads(line))
                except ValueError:
                    continue
        finally:
            self.inbox.put(None)

    def send(self, message):
        try:
            self.proc.stdin.write(json.dumps(message, ensure_ascii=True) + "\n")
            self.proc.stdin.flush()
        except (BrokenPipeError, OSError) as error:
            raise ProviderError(tr(self.lang, "codex_closed")) from error

    def receive(self, timeout=0.25):
        try:
            message = self.inbox.get(timeout=timeout)
        except queue.Empty:
            return {}
        if message is None:
            raise ProviderError(tr(self.lang, "codex_exited"))
        if "id" in message and "method" in message:
            if self.on_request and self.on_request(self, message):
                return {}
            if message["method"].endswith("/requestApproval"):
                self.send({"id": message["id"], "result": {"decision": "decline"}})
            else:
                self.send({"id": message["id"], "error": {"code": -32601,
                           "message": "AgentDeck only supports non-interactive sessions."}})
            return {}
        return message

    def call(self, method, params=None, seconds=15):
        self.next_id += 1
        call_id = self.next_id
        self.send({"method": method, "id": call_id, "params": params or {}})
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            message = self.receive()
            if message.get("id") == call_id:
                if "error" in message:
                    raise ProviderError(str(message["error"].get("message", tr(self.lang, "codex_error"))))
                return message.get("result") or {}
            if message:
                self.deferred.append(message)
        raise ProviderError(tr(self.lang, "codex_no_reply", method=method))

    def __enter__(self):
        try:
            self.call("initialize", {"clientInfo": {
                "name": "agentdeck", "title": "AgentDeck", "version": "0.5.0",
            }})
            self.send({"method": "initialized"})
        except Exception:
            self.__exit__()
            raise
        return self

    def __exit__(self, *unused):
        try:
            self.proc.stdin.close()
        except OSError:
            pass
        try:
            self.proc.wait(timeout=2)
        except subprocess.TimeoutExpired:
            kill_tree(self.proc)
            self.proc.wait(timeout=5)


def codex_account(lang=DEFAULT_LANG):
    try:
        with Rpc(lang=lang) as rpc:
            data = rpc.call("account/read", seconds=6)
    except (OSError, ProviderError):
        return {}
    acc = data.get("account") or {}
    routing = data.get("workspaceRouting") or {}
    if not acc and not data.get("userId"):
        return {}
    return {
        "email": str(acc.get("email") or ""),
        "plan": str(acc.get("planType") or ""),
        "type": str(acc.get("type") or ""),
        "workspace": str(acc.get("workspaceName") or ""),
        "user_id": str(data.get("userId") or ""),
        "account_id": str(routing.get("chatgptAccountId") or ""),
    }


def parse_agy_subscription(usage_payload=None, credits_payload=None):
    """Extract Antigravity subscription plan, credits, and quota percentages from CLI JSON."""
    usage_data = (usage_payload or {}).get("command", {}).get("data", {}) if isinstance(usage_payload, dict) else {}
    if not usage_data and isinstance(usage_payload, dict) and "groups" in usage_payload:
        usage_data = usage_payload
    credits_data = (credits_payload or {}).get("command", {}).get("data", {}) if isinstance(credits_payload, dict) else {}
    if not credits_data and isinstance(credits_payload, dict) and "remaining_credits" in credits_payload:
        credits_data = credits_payload

    groups = usage_data.get("groups") or []
    plan = str(usage_data.get("plan") or usage_data.get("tier") or "").strip()
    gemini_weekly = None
    third_party_weekly = None
    argon_weekly = None
    has_separate_argon = False
    gemini_has_argon = False

    for group in groups:
        if not isinstance(group, dict):
            continue
        name = str(group.get("name") or "")
        desc = str(group.get("description") or "")
        low_name = name.lower()
        low_desc = desc.lower()
        buckets = group.get("buckets") or []
        if "argon" in low_name:
            has_separate_argon = True
            for b in buckets:
                val = b.get("remaining_fraction")
                if isinstance(val, (int, float)):
                    argon_weekly = max(0, min(100, round(val * 100)))
        elif "gemini" in low_name:
            if "argon" in low_desc:
                gemini_has_argon = True
            for b in buckets:
                if b.get("window") == "weekly" and isinstance(b.get("remaining_fraction"), (int, float)):
                    gemini_weekly = max(0, min(100, round(b["remaining_fraction"] * 100)))
        elif "claude" in low_name or "gpt" in low_name or "3p" in low_name:
            for b in buckets:
                if b.get("window") == "weekly" and isinstance(b.get("remaining_fraction"), (int, float)):
                    third_party_weekly = max(0, min(100, round(b["remaining_fraction"] * 100)))

    if not plan:
        if has_separate_argon:
            plan = "Pro"
        elif gemini_has_argon:
            plan = "Ultra"
        elif groups or credits_data:
            plan = "Standard"

    credits = credits_data.get("remaining_credits")
    if isinstance(credits, (int, float)) and not isinstance(credits, bool) and math.isfinite(credits):
        credits = int(credits) if credits == int(credits) else credits
    else:
        credits = None
    upgrade_uri = str(credits_data.get("upgrade_uri") or "").strip()

    parts = []
    if gemini_weekly is not None:
        parts.append(f"Gemini {gemini_weekly}%")
    if third_party_weekly is not None:
        parts.append(f"Claude/GPT {third_party_weekly}%")
    if argon_weekly is not None and has_separate_argon:
        parts.append(f"Argon {argon_weekly}%")
    return {
        "plan": plan,
        "credits": credits,
        "upgrade_uri": upgrade_uri,
        "gemini_weekly": gemini_weekly,
        "third_party_weekly": third_party_weekly,
        "argon_weekly": argon_weekly,
        "quota_summary": " · ".join(parts),
    }


def agy_account(lang=DEFAULT_LANG, force=False):
    storage = _read_agy_app_storage()
    email = str(storage.get("jetski.onboarding.lastLoginUsername") or "").strip()
    if not email:
        return {}
    now = time.monotonic()
    with _AGY_SUB_LOCK:
        cached = _AGY_SUB_CACHE.get("data")
        if not force and cached and now - _AGY_SUB_CACHE["at"] < 60:
            return {"email": email, **cached}
    usage_payload, credits_payload = {}, {}
    try:
        agy_bin = executable("agy", lang=lang)
        try:
            usage_payload = json.loads(capture([agy_bin, "-p", "/usage", "--output-format", "json"], timeout=8, lang=lang))
        except (OSError, ProviderError, ValueError):
            usage_payload = {}
        try:
            credits_payload = json.loads(capture([agy_bin, "-p", "/credits", "--output-format", "json"], timeout=8, lang=lang))
        except (OSError, ProviderError, ValueError):
            credits_payload = {}
    except (OSError, ProviderError):
        pass
    sub = parse_agy_subscription(usage_payload, credits_payload)
    if sub.get("plan") or sub.get("quota_summary") or sub.get("credits") is not None:
        with _AGY_SUB_LOCK:
            _AGY_SUB_CACHE["at"] = time.monotonic()
            _AGY_SUB_CACHE["data"] = sub
    return {"email": email, **sub}


def codex_login_launch(lang=DEFAULT_LANG):
    codex_bin = executable("codex", lang=lang)
    subprocess.Popen(
        [codex_bin, "login"],
        creationflags=getattr(subprocess, "CREATE_NEW_CONSOLE", 0),
    )


def _agy_ide_serve_running(lang=DEFAULT_LANG):
    if os.name != "nt":
        return False
    ps = Path(os.environ.get("SystemRoot", "C:/Windows")) / "System32/WindowsPowerShell/v1.0/powershell.exe"
    try:
        out = capture([
            str(ps), "-NoProfile", "-NonInteractive", "-Command",
            "Get-CimInstance Win32_Process -Filter \"Name='agy.exe' OR Name='agy.EXE'\" | "
            "Select-Object -ExpandProperty CommandLine"
        ], timeout=5, lang=lang)
        return any("remote-control" in line and "serve" in line for line in out.splitlines())
    except (OSError, ProviderError):
        return False


def agy_remote_control(action="status", name="", lang=DEFAULT_LANG):
    if action not in {"status", "start", "stop"}:
        raise ProviderError(tr(lang, "rc_unknown_action"))
    agy_bin = executable("agy", lang=lang)
    ide_serve = _agy_ide_serve_running(lang=lang)
    if action == "start" and ide_serve and not name.strip():
        action = "status"
    args = [agy_bin, "remote-control", action]
    if action == "start":
        args.append("--session")
        if name.strip():
            args.extend(["--name", name.strip()[:48]])
    output = capture(args, timeout=15, lang=lang).strip()
    if action != "status":
        # Re-read authoritative status after start/stop
        try:
            status_out = capture([agy_bin, "remote-control", "status"], timeout=10, lang=lang).strip()
        except ProviderError:
            status_out = output
        ide_serve = _agy_ide_serve_running(lang=lang)
    else:
        status_out = output
    low = status_out.lower()
    not_registered = "not registered" in low
    registered = not not_registered
    daemon_running = registered and not any(tok in low for tok in ("stopped", "not running", "inactive", "offline"))
    running = daemon_running or ide_serve
    instance = ""
    for line in status_out.splitlines():
        if line.lower().startswith("instance name:"):
            val = line.split(":", 1)[1].strip()
            if val.lower().startswith("not assigned"):
                continue
            if "(" in val:
                val = val.split("(", 1)[0].strip()
            instance = val
    return {
        "provider": "agy",
        "registered": registered or ide_serve,
        "running": running,
        "ide_serve": ide_serve,
        "instance": instance,
        "raw": status_out or output,
    }


def codex_remote_control(action="status", lang=DEFAULT_LANG):
    if action not in {"status", "start", "stop", "pair"}:
        raise ProviderError(tr(lang, "rc_unknown_action"))
    codex_bin = executable("codex", lang=lang)

    if action == "status":
        running = False
        desktop_remote = False
        sock_exists = (Path.home() / ".codex" / "app-server-control" / "app-server-control.sock").exists()
        env_id = ""
        state_path = Path.home() / ".codex" / ".codex-global-state.json"
        if state_path.is_file():
            try:
                state = json.loads(state_path.read_text(encoding="utf-8"))
                env_id = str(state.get("electron-local-remote-control-environment-id") or "")
            except (OSError, ValueError):
                pass
        if os.name == "nt":
            ps = Path(os.environ.get("SystemRoot", "C:/Windows")) / "System32/WindowsPowerShell/v1.0/powershell.exe"
            try:
                out = capture([
                    str(ps), "-NoProfile", "-NonInteractive", "-Command",
                    "Get-CimInstance Win32_Process -Filter \"Name='codex.exe'\" | "
                    "Select-Object -ExpandProperty CommandLine"
                ], timeout=6, lang=lang)
                for line in out.splitlines():
                    if "remote-control" in line or "app-server-daemon" in line:
                        running = True
                    if "exec-server" in line and "--remote" in line:
                        desktop_remote = True
            except (OSError, ProviderError):
                pass
        if sock_exists:
            running = True
        return {
            "provider": "codex",
            "running": running or desktop_remote,
            "daemon_running": running,
            "desktop_remote": desktop_remote,
            "environment_id": env_id,
            "raw": "",
        }

    args = [codex_bin, "remote-control", action, "--json"]
    with spawn(args, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE) as proc:
        try:
            out, err = proc.communicate(timeout=15)
        except subprocess.TimeoutExpired:
            # If `start` unexpectedly runs foreground without detaching, read what arrived or inform cleanly
            kill_tree(proc)
            proc.communicate()
            raise ProviderError(tr(lang, "provider_timeout")) from None
    combined = (out or err or "").strip()
    if proc.returncode:
        raise ProviderError(combined[-480:] or tr(lang, "provider_error"))
    parsed = {}
    for line in (out or "").splitlines():
        line = line.strip()
        if not line:
            continue
        try:
            obj = json.loads(line)
            if isinstance(obj, dict):
                parsed.update(obj)
        except ValueError:
            continue
    status_info = codex_remote_control("status", lang=lang)
    code = str(parsed.get("code") or parsed.get("pairingCode") or parsed.get("pairing_code") or "")
    url = str(parsed.get("url") or parsed.get("pairingUrl") or "")
    if not code and action == "pair" and combined:
        m = re.search(r"\b([A-Z0-9]{4,}(?:-[A-Z0-9]{4,})+)\b", combined)
        if m:
            code = m.group(1)
    return status_info | {
        "raw": combined,
        "code": code,
        "url": url,
        "data": parsed,
    }


def codex_catalog(lang=DEFAULT_LANG):
    with Rpc(lang=lang) as rpc:
        models, cursor = [], None
        for _ in range(4):
            params = {"limit": 100}
            if cursor:
                params["cursor"] = cursor
            result = rpc.call("model/list", params)
            models.extend({
                "id": m.get("model") or m["id"], "label": m.get("displayName") or m["id"],
                "efforts": [option["reasoningEffort"] for option in m.get("supportedReasoningEfforts", [])
                            if isinstance(option.get("reasoningEffort"), str)],
                "default_effort": m.get("defaultReasoningEffort"),
            } for m in result.get("data", []) if not m.get("hidden"))
            cursor = result.get("nextCursor")
            if not cursor:
                break
        limits, thread_cwds = [], []
        try:
            rate = rpc.call("account/rateLimits/read", seconds=8)
            buckets = rate.get("rateLimitsByLimitId") or {"codex": rate.get("rateLimits") or {}}
            for key, bucket in buckets.items():
                for metric in ("primary", "secondary"):
                    value = bucket.get(metric) or {}
                    if isinstance(value.get("usedPercent"), (int, float)):
                        mins = value.get("windowDurationMins")
                        window = (f"{mins // 1440}d" if mins and mins % 1440 == 0 else
                                  f"{mins // 60}h" if mins and mins % 60 == 0 else
                                  f"{mins}m" if mins else metric)
                        limits.append({"group": bucket.get("limitName") or key, "window": window,
                                       "remaining": max(0, min(100, 100 - round(value["usedPercent"]))),
                                       "reset": value.get("resetsAt") or 0})
        except ProviderError:
            pass
        try:
            result = rpc.call("thread/list", {"limit": 100, "archived": False,
                                              "sortKey": "updated_at"}, seconds=6)
            thread_cwds = [t["cwd"] for t in result.get("data", []) if t.get("cwd")]
        except ProviderError:
            pass
        return models, limits, codex_projects(thread_cwds)


def agy_catalog(lang=DEFAULT_LANG):
    agy = executable("agy", lang=lang)
    result = capture([agy, "models"], lang=lang)
    models = [{"id": fields[0], "label": fields[1]} for line in result.splitlines()
              if len(fields := line.strip().split("\t", 1)) == 2 and fields[0] and fields[1]]
    if not models:
        raise ProviderError(tr(lang, "agy_no_models"))
    limits = []
    try:
        result = json.loads(capture([agy, "-p", "/usage", "--output-format", "json"], timeout=15, lang=lang))
        for group in result.get("command", {}).get("data", {}).get("groups", []):
            for bucket in group.get("buckets", []):
                value = bucket.get("remaining_fraction")
                if not isinstance(value, (float, int)) or not 0 <= value <= 1:
                    continue
                try:
                    reset = int(datetime.fromisoformat(bucket.get("reset_time") or "").timestamp())
                except ValueError:
                    reset = 0
                limits.append({"group": group["name"], "window": bucket.get("window") or "",
                               "remaining": round(100 * value), "reset": reset})
    except (ProviderError, ValueError):
        pass
    return models, limits, agy_projects()


LETTER_OPT = re.compile(r"^(?:[-*]\s*)?(?:\*{1,2})?([A-D])(?:\*{1,2})?[).:\-]\s+(?:\*{1,2})?(.+?)(?:\*{1,2})?\s*$")
NUM_OPT = re.compile(r"^(?:[-*]\s*)?(?:\*{1,2})?([1-4])(?:\*{1,2})?[).]\s+(?:\*{1,2})?(.+?)(?:\*{1,2})?\s*$")
QUESTION_HINT = re.compile(
    r"(\?\s*$|wybierz|któr|czy chcesz|co mam|jak mam|choose|which option|select one|which approach|prefer|would you like)",
    re.I,
)
EXPLICIT_CHOICE_HINT = re.compile(
    r"(wybierz|któr|czy chcesz|co mam|jak mam|choose|which option|select one|which approach|would you like)",
    re.I,
)
COMPLETION_SUMMARY_TAIL = re.compile(
    r"(all green|completed|done\.|summary|ukończono|podsumowanie|gotowe\.|wszystkie testy)",
    re.I,
)


def _classify_tool_phase(name, extra_text=""):
    """Map an Antigravity or Codex tool/step name into a normalized UI activity phase."""
    raw = f"{name or ''} {extra_text or ''}".strip().lower()
    if any(tok in raw for tok in ("browser", "navigate", "click_", "screenshot", "read_url", "search_web", "websearch", "web_search")):
        return "browser"
    if any(tok in raw for tok in ("subagent", "invoke_subagent", "swarm", "send_message", "run_workflow", "manage_subagents", "collab")):
        return "subagent"
    if any(tok in raw for tok in ("run_command", "commandexecution", "command_status", "terminal", "bash", "powershell", "exec_command", "execcommand", "command")):
        return "command"
    if any(tok in raw for tok in ("edit_file", "write_file", "create_file", "replace_file", "multi_replace", "filechange", "apply_patch", "patch")):
        return "file_edit"
    if any(tok in raw for tok in ("mcp", "call_mcp_tool", "mcptoolcall")):
        return "mcp"
    if any(tok in raw for tok in ("thinking", "reasoning", "plan")):
        return "thinking"
    if any(tok in raw for tok in ("sleep", "wait", "schedule")):
        return "waiting"
    return "tool"


def _make_activity(phase, title, detail="", status="running"):
    return {
        "ts": int(time.time()),
        "phase": str(phase or "tool"),
        "title": " ".join(str(title or "").split())[:120],
        "detail": " ".join(str(detail or "").split())[:240],
        "status": str(status or "running"),
    }


def _summarize_tool_args(tool_name, args):
    """Extract a concise human-readable target (command, path, URL, subagent role) from tool arguments."""
    if isinstance(args, str):
        try:
            args = json.loads(args)
        except ValueError:
            return " ".join(args.split())[:140]
    if not isinstance(args, dict):
        return ""
    for key in (
        "CommandLine", "commandLine", "command", "cmd",
        "Path", "path", "TargetFile", "targetFile", "AbsolutePath", "file_path", "filePath", "directory_path", "directoryPath",
        "Url", "url", "query", "Query", "Role", "role", "TypeName", "typeName", "Description", "description",
        "ToolName", "toolName", "ServerName", "serverName", "Prompt", "prompt",
    ):
        val = args.get(key)
        if isinstance(val, str) and val.strip():
            return " ".join(val.split())[:140]
    return ""


def _phase_label(phase, lang=DEFAULT_LANG):
    mapping = {
        "thinking": tr(lang, "phase_thinking"),
        "command": tr(lang, "phase_command"),
        "tool": tr(lang, "phase_tool"),
        "file_edit": tr(lang, "phase_file_edit"),
        "subagent": tr(lang, "phase_subagent"),
        "browser": tr(lang, "phase_browser"),
        "mcp": tr(lang, "phase_mcp"),
        "reply": tr(lang, "phase_reply"),
        "waiting": tr(lang, "phase_waiting"),
        "retrying": tr(lang, "phase_retrying"),
        "finishing": tr(lang, "phase_finishing"),
    }
    return mapping.get(phase, tr(lang, "phase_tool"))


def extract_choice_question(text, lang=DEFAULT_LANG):
    """Detect a multiple-choice question (A/B/C/D or 1/2/3/4) at the end of an agent response."""
    tail = (text or "").strip()[-1600:]
    if not tail:
        return None
    lines = [line.strip() for line in tail.splitlines()]
    best_opts = []
    best_kind = ""
    opt_start = -1
    opt_end = -1
    i = len(lines) - 1
    while i >= 0:
        if not lines[i]:
            if best_opts:
                break
            i -= 1
            continue
        m_let = LETTER_OPT.match(lines[i])
        m_num = NUM_OPT.match(lines[i]) if not m_let else None
        if m_let and (not best_kind or best_kind == "let"):
            best_kind = "let"
            best_opts.append((m_let.group(1).upper(), m_let.group(2).strip()))
            if opt_end < 0:
                opt_end = i
            opt_start = i
            i -= 1
            continue
        if m_num and (not best_kind or best_kind == "num"):
            best_kind = "num"
            best_opts.append((m_num.group(1), m_num.group(2).strip()))
            if opt_end < 0:
                opt_end = i
            opt_start = i
            i -= 1
            continue
        if best_opts:
            break
        if len(lines) - i <= 3:
            i -= 1
            continue
        break
    best_opts.reverse()
    if not 2 <= len(best_opts) <= 4:
        return None
    if best_kind == "let":
        expected = [chr(ord("A") + idx) for idx in range(len(best_opts))]
    else:
        expected = [str(idx + 1) for idx in range(len(best_opts))]
    if [tag for tag, _ in best_opts] != expected:
        return None

    context_before = [ln for ln in lines[max(0, opt_start - 4):opt_start] if ln]
    context_after = [ln for ln in lines[opt_end + 1:] if ln]
    prompt_line = ""
    search_before = context_before[-2:] if best_kind == "num" else context_before
    for candidate in reversed(search_before):
        if QUESTION_HINT.search(candidate):
            prompt_line = candidate
            break
    if not prompt_line:
        for candidate in context_after:
            if QUESTION_HINT.search(candidate):
                prompt_line = candidate
                break
    if best_kind == "num":
        if not prompt_line:
            return None
        if "?" not in prompt_line and not EXPLICIT_CHOICE_HINT.search(prompt_line):
            return None
        after_text = " ".join(context_after)
        if after_text and "?" not in after_text and COMPLETION_SUMMARY_TAIL.search(after_text):
            return None
    elif not prompt_line:
        if "?" in tail[-600:]:
            prompt_line = context_before[-1] if context_before else ""
        else:
            return None
    prompt_line = re.sub(r"[*_`#]+", "", prompt_line).strip()
    options = []
    for idx, (tag, body) in enumerate(best_opts):
        clean_body = re.sub(r"[*_`]+", "", body).strip()
        letter = chr(ord("A") + idx)
        options.append({
            "label": f"{letter}) {clean_body[:42]}",
            "description": clean_body[:140],
            "value": f"{tag}) {clean_body}",
        })
    return {
        "id": secrets.token_hex(6),
        "header": "Choice" if lang == "en" else "Wybór",
        "question": prompt_line[:160] or ("Choose an option" if lang == "en" else "Wybierz opcję"),
        "options": options,
    }


def codex_run(cwd, model, prompt, mode, stop, update, effort="", lang=DEFAULT_LANG, ask_user=None):
    windows = os.name == "nt"
    config = ("features.prefer_mxc=true", 'windows.sandbox="unelevated"') if windows else ()
    live_stream = []
    active_items = {}
    active_subagents = set()
    activity_log = []
    thread_status = ["active"]
    last_label = [tr(lang, "codex_starting")]
    last_phase = ["thinking"]
    last_preview = [""]
    last_event_at = [time.monotonic()]
    last_heartbeat_at = [time.monotonic()]

    def emit(detail, preview="", phase=None, activity=None, append_live="", heartbeat=False, **extra):
        if phase:
            last_phase[0] = phase
        if detail:
            last_label[0] = detail
        if preview:
            last_preview[0] = " ".join(str(preview).split())[:180]
        if not heartbeat:
            last_event_at[0] = time.monotonic()
        last_heartbeat_at[0] = time.monotonic()
        if append_live:
            live_stream.append(append_live)
            joined = "".join(live_stream)
            if len(joined) > 48000:
                live_stream[:] = [joined[-40000:]]
            extra["live_output"] = "".join(live_stream)[-40000:]
        if activity is not None:
            extra["activity_event"] = activity
        tools = [entry["label"] for entry in list(active_items.values())[-4:]]
        extra.setdefault("phase", last_phase[0])
        extra.setdefault("active_tools", tools)
        update(last_label[0], preview=last_preview[0], **extra)

    def handle_request(rpc_inst, message):
        method = message.get("method", "")
        req_id = message["id"]
        params = message.get("params") or {}
        if method == "item/tool/requestUserInput":
            if not ask_user:
                rpc_inst.send({"id": req_id, "result": {"answers": {}}})
                return True
            answers = {}
            for q in params.get("questions") or []:
                options = []
                for opt in (q.get("options") or [])[:4]:
                    lbl = str(opt.get("label") or "").strip()
                    desc = str(opt.get("description") or "").strip()
                    if lbl:
                        options.append({"label": lbl, "description": desc, "value": lbl})
                if not options:
                    options = [
                        {"label": tr(lang, "approve"), "description": "", "value": "Yes"},
                        {"label": tr(lang, "decline"), "description": "", "value": "No"},
                    ]
                chosen = ask_user({
                    "id": str(q.get("id") or secrets.token_hex(6)),
                    "header": str(q.get("header") or ""),
                    "question": str(q.get("question") or ""),
                    "options": options,
                })
                if chosen is None:
                    rpc_inst.send({"id": req_id, "result": {"answers": answers}})
                    return True
                answers[str(q.get("id") or "")] = {
                    "answers": [str(chosen.get("value") or chosen.get("label") or "")]
                }
            rpc_inst.send({"id": req_id, "result": {"answers": answers}})
            return True
        if method in ("item/commandExecution/requestApproval", "item/fileChange/requestApproval"):
            allow = mode in ("write", "plan_execute")
            decision = "accept" if allow else "decline"
            target = params.get("command") or params.get("reason") or method
            phase = "command" if "commandExecution" in method else "file_edit"
            emit(
                _phase_label(phase, lang),
                preview=str(target)[:180],
                phase=phase,
                activity=_make_activity(phase, f"{'Approved' if allow else 'Declined'} {method.split('/')[1]}", str(target), "done" if allow else "error"),
            )
            rpc_inst.send({"id": req_id, "result": {"decision": decision}})
            return True
        if method == "item/permissions/requestApproval":
            allow = mode in ("write", "plan_execute")
            granted = (params.get("permissions") or {}) if allow else {}
            emit(
                _phase_label("tool", lang),
                phase="tool",
                activity=_make_activity("tool", "Permissions request", "granted" if allow else "declined", "done" if allow else "error"),
            )
            rpc_inst.send({"id": req_id, "result": {"permissions": granted, "scope": "session" if allow else "turn"}})
            return True
        if method in ("execCommandApproval", "applyPatchApproval"):
            allow = mode in ("write", "plan_execute")
            decision = "approved" if allow else {"denied": {"rejection": "Read-only mode"}}
            phase = "command" if method == "execCommandApproval" else "file_edit"
            emit(
                _phase_label(phase, lang),
                phase=phase,
                activity=_make_activity(phase, method, "approved" if allow else "denied", "done" if allow else "error"),
            )
            rpc_inst.send({"id": req_id, "result": {"decision": decision}})
            return True
        if method == "mcpServer/elicitation/request":
            rpc_inst.send({"id": req_id, "result": {"action": "decline"}})
            return True
        return False

    with Rpc(config, lang=lang, on_request=handle_request) as rpc:
        if stop.is_set():
            return "cancelled", tr(lang, "cancelled_before_start")
        if windows and rpc.call("windowsSandbox/readiness").get("status") != "ready":
            raise ProviderError(tr(lang, "codex_sandbox_unavailable"))
        sandbox = "workspace-write" if mode in ("write", "plan_execute") else "read-only"
        thread = rpc.call("thread/start", {
            "cwd": str(cwd), "model": model, "approvalPolicy": "never",
            "sandbox": sandbox,
        })["thread"]["id"]
        emit(tr(lang, "codex_starting"), phase="thinking", conversation=thread,
             activity=_make_activity("thinking", "Codex session started", str(cwd), "done"))
        turn = rpc.call("turn/start", {
            "threadId": thread, "input": [{"type": "text", "text": prompt}],
            **({"effort": effort} if effort else {}),
        })["turn"]["id"]
        interrupted = False
        item_order = []
        item_buffers = {}
        fallback_chunks = []
        cancel_deadline = None
        turn_completed = None
        turn_completed_at = None

        def _extract_item_text(item):
            if not isinstance(item, dict):
                return ""
            direct = item.get("text")
            if isinstance(direct, str) and direct.strip():
                return direct
            parts = []
            for block in item.get("content") or []:
                if isinstance(block, dict):
                    txt = block.get("text") or block.get("content") or ""
                    if isinstance(txt, str) and txt:
                        parts.append(txt)
                elif isinstance(block, str) and block:
                    parts.append(block)
            return "".join(parts)

        def _collect_output():
            blocks = []
            for key in item_order:
                chunk = "".join(item_buffers.get(key) or []).strip()
                if chunk:
                    blocks.append(chunk)
            if fallback_chunks:
                tail = "".join(fallback_chunks).strip()
                if tail and (not blocks or tail not in blocks[-1]):
                    blocks.append(tail)
            text = "\n\n".join(blocks).strip()
            if not text and activity_log:
                text = "\n".join(activity_log[-20:])
            return text

        while True:
            try:
                event = rpc.deferred.pop(0) if rpc.deferred else rpc.receive()
            except ProviderError:
                if turn_completed is not None:
                    break
                raise
            data = event.get("params") or {}
            kind = event.get("method")
            now = time.monotonic()

            if not kind:
                if turn_completed is not None:
                    no_active = not active_subagents and not active_items and thread_status[0] != "active"
                    if no_active or (now - last_event_at[0] >= 4.0) or (now - turn_completed_at >= 12.0):
                        break
                elif now - last_heartbeat_at[0] >= 3.0:
                    emit(last_label[0], preview=last_preview[0], phase=last_phase[0], heartbeat=True)

            if kind == "thread/status/changed":
                st = (data.get("status") or {}).get("type")
                if st:
                    thread_status[0] = st
            elif kind == "error":
                err_info = data.get("error") or {}
                err_msg = str(err_info.get("message") or data.get("message") or tr(lang, "codex_error"))
                if data.get("willRetry"):
                    emit(_phase_label("retrying", lang), preview=err_msg, phase="retrying",
                         activity=_make_activity("retrying", "Retrying after error", err_msg, "running"))
                else:
                    emit(err_msg[:180], preview=err_msg, phase="tool",
                         activity=_make_activity("tool", "Codex error", err_msg, "error"))
            elif kind == "turn/completed" and data.get("turn", {}).get("id") == turn:
                turn_completed = data["turn"]
                turn_completed_at = now
                st = turn_completed.get("status")
                if st != "completed" or (not active_subagents and not active_items and thread_status[0] != "active"):
                    break
                emit(_phase_label("finishing", lang), phase="finishing",
                     activity=_make_activity("finishing", "Waiting for background tasks/subagents", "", "running"))
            elif kind == "item/agentMessage/delta":
                delta = data.get("delta", "")
                if delta:
                    item_id = str(data.get("itemId") or data.get("item_id") or "")
                    if item_id:
                        if item_id not in item_buffers:
                            item_order.append(item_id)
                            item_buffers[item_id] = []
                        item_buffers[item_id].append(delta)
                    else:
                        fallback_chunks.append(delta)
                    emit(tr(lang, "codex_reply"), preview=(last_preview[0] + delta)[-180:], phase="reply", append_live=delta)
            elif kind in ("item/commandExecution/outputDelta", "command/exec/outputDelta", "process/outputDelta"):
                delta = str(data.get("delta") or "")
                item_id = str(data.get("itemId") or data.get("item_id") or "")
                if delta:
                    if item_id and item_id in active_items:
                        active_items[item_id]["output"].append(delta)
                    tail = " ".join(delta.split())[-180:]
                    emit(tr(lang, "codex_cmd"), preview=tail, phase="command", append_live=delta)
            elif kind in ("item/fileChange/outputDelta", "item/fileChange/patchUpdated", "turn/diff/updated"):
                delta = str(data.get("delta") or data.get("diff") or data.get("patch") or "")
                if delta:
                    emit(tr(lang, "codex_file_change"), preview=delta.strip().splitlines()[-1][:180], phase="file_edit", append_live=delta + "\n")
            elif kind == "item/mcpToolCall/progress":
                msg = str(data.get("message") or "")
                if msg:
                    emit(tr(lang, "codex_mcp"), preview=msg[:180], phase="mcp",
                         activity=_make_activity("mcp", "MCP progress", msg, "running"))
            elif kind in ("item/reasoning/summaryTextDelta", "item/reasoning/textDelta", "item/plan/delta"):
                delta = str(data.get("delta") or "")
                if delta:
                    emit(_phase_label("thinking", lang), preview=(last_preview[0] + delta)[-180:], phase="thinking")
            elif kind == "turn/plan/updated":
                steps = data.get("plan") or []
                done_n = sum(1 for s in steps if s.get("status") == "completed")
                in_prog = next((s.get("step") for s in steps if s.get("status") == "inProgress"), "")
                summary = f"Plan {done_n}/{len(steps)}" + (f": {in_prog}" if in_prog else "")
                emit(_phase_label("thinking", lang), preview=summary, phase="thinking",
                     activity=_make_activity("thinking", summary, str(data.get("explanation") or ""), "running"))
            elif kind in ("hook/started", "hook/completed"):
                status_h = "done" if kind == "hook/completed" else "running"
                emit(_phase_label("tool", lang), phase="tool",
                     activity=_make_activity("tool", kind, str(data.get("hookName") or data.get("name") or ""), status_h))
            elif kind == "item/started":
                item = data.get("item") or {}
                item_id = str(item.get("id") or secrets.token_hex(4))
                item_type = str(item.get("type") or "")
                phase = _classify_tool_phase(item_type)
                label = _phase_label(phase, lang)
                title = item_type
                detail_txt = ""
                if item_type == "commandExecution":
                    cmd = str(item.get("command") or "")
                    label = f"$ {cmd[:48]}" if cmd else tr(lang, "codex_cmd")
                    title = f"$ {cmd}" if cmd else "Command"
                    detail_txt = str(item.get("cwd") or "")
                elif item_type == "fileChange":
                    paths = [str(c.get("path") or "") for c in (item.get("changes") or []) if isinstance(c, dict) and c.get("path")]
                    label = f"Edit {', '.join(Path(p).name for p in paths[:2])}" if paths else tr(lang, "codex_file_change")
                    title = "Edit files"
                    detail_txt = ", ".join(paths)
                elif item_type == "mcpToolCall":
                    srv, tl = str(item.get("server") or ""), str(item.get("tool") or "")
                    label = f"MCP: {srv}/{tl}".strip("/")
                    title = label
                elif item_type == "dynamicToolCall":
                    ns, tl = str(item.get("namespace") or ""), str(item.get("tool") or "")
                    phase = _classify_tool_phase(tl, ns)
                    label = f"{ns}:{tl}".strip(":") or _phase_label(phase, lang)
                    title = label
                elif item_type == "collabAgentToolCall":
                    active_subagents.add(item_id)
                    tl = str(item.get("tool") or "subagent")
                    mdl = str(item.get("model") or "")
                    label = f"Subagent: {tl}"
                    title = label + (f" ({mdl})" if mdl else "")
                    detail_txt = str(item.get("prompt") or "")
                elif item_type == "subAgentActivity":
                    sub_id = str(item.get("agentThreadId") or item_id)
                    active_subagents.add(sub_id)
                    ap = str(item.get("agentPath") or item.get("kind") or "subagent")
                    label = f"Subagent: {ap}"
                    title = label
                    detail_txt = str(item.get("model") or "")
                elif item_type == "webSearch":
                    q_txt = str(item.get("query") or "")
                    label = f"Web: {q_txt[:42]}" if q_txt else _phase_label("browser", lang)
                    title = "Web search"
                    detail_txt = q_txt
                elif item_type == "sleep":
                    dur = item.get("durationMs") or 0
                    label = f"{_phase_label('waiting', lang)} ({int(dur/1000)}s)" if dur else _phase_label("waiting", lang)
                    title = label
                if item_type not in ("userMessage", "hookPrompt", "agentMessage"):
                    active_items[item_id] = {"type": item_type, "phase": phase, "label": label[:48], "output": []}
                    live_line = f"\n[{title}] {detail_txt}\n".strip() + "\n"
                    emit(label, preview=detail_txt or title, phase=phase,
                         activity=_make_activity(phase, title, detail_txt, "running"),
                         append_live=live_line if item_type in ("commandExecution", "fileChange", "collabAgentToolCall", "webSearch", "mcpToolCall") else "")
            elif kind == "item/completed":
                item = data.get("item") or {}
                item_id = str(item.get("id") or "")
                item_type = str(item.get("type") or "")
                tracked = active_items.pop(item_id, None)
                if item_type == "collabAgentToolCall":
                    active_subagents.discard(item_id)
                elif item_type == "subAgentActivity":
                    active_subagents.discard(str(item.get("agentThreadId") or item_id))
                if item_type == "agentMessage":
                    completed_text = _extract_item_text(item)
                    if completed_text:
                        if item_id:
                            if item_id not in item_buffers:
                                item_order.append(item_id)
                                item_buffers[item_id] = [completed_text]
                            else:
                                existing = "".join(item_buffers[item_id])
                                if len(completed_text) > len(existing):
                                    item_buffers[item_id] = [completed_text]
                        else:
                            fallback_chunks.append("\n\n" + completed_text)
                        emit(tr(lang, "codex_reply"), preview=completed_text[-180:], phase="reply", append_live="\n" + completed_text + "\n")
                elif item_type in ("commandExecution", "fileChange", "mcpToolCall", "dynamicToolCall", "collabAgentToolCall", "webSearch", "plan"):
                    phase = _classify_tool_phase(item_type, str(item.get("tool") or ""))
                    exit_code = item.get("exitCode")
                    st_item = str(item.get("status") or "done")
                    ev_status = "error" if (isinstance(exit_code, int) and exit_code != 0) or st_item in ("failed", "declined") else "done"
                    summary = ""
                    if item_type == "commandExecution":
                        cmd = str(item.get("command") or "")
                        agg = str(item.get("aggregatedOutput") or "")
                        summary = f"$ {cmd} (exit {exit_code if exit_code is not None else 0})"
                        tail_out = " ".join(agg.split())[-180:]
                        activity_log.append(f"- `{cmd}` -> exit {exit_code if exit_code is not None else 0}")
                        emit(_phase_label(phase, lang), preview=tail_out or summary, phase=phase,
                             activity=_make_activity(phase, summary, tail_out, ev_status),
                             append_live=(agg[-2000:] + "\n") if agg and not (tracked and tracked["output"]) else "")
                    elif item_type == "fileChange":
                        paths = [str(c.get("path") or "") for c in (item.get("changes") or []) if isinstance(c, dict) and c.get("path")]
                        summary = f"Edited {', '.join(Path(p).name for p in paths[:4])}" if paths else "File changes applied"
                        activity_log.append(f"- {summary}")
                        emit(_phase_label(phase, lang), preview=summary, phase=phase,
                             activity=_make_activity(phase, summary, ", ".join(paths), ev_status))
                    elif item_type == "collabAgentToolCall":
                        states = item.get("agentsStates") or {}
                        summary = f"Subagent {item.get('tool') or ''} finished ({len(states)} agent(s))"
                        activity_log.append(f"- {summary}")
                        emit(_phase_label(phase, lang), preview=summary, phase=phase,
                             activity=_make_activity(phase, summary, str(item.get("prompt") or ""), ev_status))
                    elif item_type == "webSearch":
                        summary = f"Web: {item.get('query') or ''}"
                        activity_log.append(f"- {summary}")
                        emit(_phase_label(phase, lang), preview=summary, phase=phase,
                             activity=_make_activity(phase, summary, "", ev_status))
                    elif item_type in ("mcpToolCall", "dynamicToolCall"):
                        summary = f"{item.get('server') or item.get('namespace') or ''} {item.get('tool') or ''}".strip()
                        activity_log.append(f"- Tool `{summary}` ({ev_status})")
                        emit(_phase_label(phase, lang), preview=summary, phase=phase,
                             activity=_make_activity(phase, summary, str(item.get("error") or ""), ev_status))

            if stop.is_set() and not interrupted:
                rpc.call("turn/interrupt", {"threadId": thread, "turnId": turn}, seconds=8)
                interrupted = True
                cancel_deadline = time.monotonic() + 12
            if cancel_deadline is not None and time.monotonic() >= cancel_deadline:
                kill_tree(rpc.proc)
                raise ProviderError(tr(lang, "codex_no_interrupt"))

        result = turn_completed or {}
        full_out = _collect_output()
        if result.get("status") == "completed":
            return "finished", full_out or tr(lang, "codex_finished")
        if result.get("status") == "interrupted":
            return "cancelled", full_out or tr(lang, "codex_interrupted")
        error = result.get("error") or {}
        raise ProviderError(error.get("message") or tr(lang, "codex_turn_failed"))


def _find_agy_transcript(conversation_id):
    if not conversation_id:
        return None
    for base in ("antigravity", "antigravity-cli", "antigravity-acp"):
        candidate = Path.home() / ".gemini" / base / "brain" / conversation_id / ".system_generated" / "logs" / "transcript.jsonl"
        if candidate.is_file():
            return candidate
    return None


def agy_run(cwd, model, prompt, mode, stop, update, native_project="", effort="", lang=DEFAULT_LANG, ask_user=None):
    exe = executable("agy", lang=lang)
    transcript = []
    conversation_id = ""
    current_prompt = prompt
    live_stream = []
    activity_log = []
    last_label = [tr(lang, "agy_started")]
    last_phase = ["thinking"]
    last_preview = [""]
    active_tools_map = {}

    def emit(detail, preview="", phase=None, activity=None, append_live="", **extra):
        if phase:
            last_phase[0] = phase
        if detail:
            last_label[0] = detail
        if preview:
            last_preview[0] = " ".join(str(preview).split())[:180]
        if append_live:
            live_stream.append(append_live)
            joined = "".join(live_stream)
            if len(joined) > 48000:
                live_stream[:] = [joined[-40000:]]
            extra["live_output"] = "".join(live_stream)[-40000:]
        if activity is not None:
            extra["activity_event"] = activity
        extra.setdefault("phase", last_phase[0])
        extra.setdefault("active_tools", list(active_tools_map.values())[-4:])
        update(last_label[0], preview=last_preview[0], **extra)

    for _turn in range(6):
        if stop.is_set():
            return "cancelled", tr(lang, "agy_cancelled")
        agy_mode = "plan" if mode == "read" else "auto"
        args = [
            exe, "--print", current_prompt, "--model", model, "--mode", agy_mode,
            "--print-timeout", "0", "--disable-slash-commands", "--output-format", "stream-json",
        ]
        if mode == "read":
            args.append("--sandbox")
        else:
            args.append("--dangerously-skip-permissions")
        if conversation_id:
            args.extend(["--conversation", conversation_id])
        if effort and str(effort).lower() in ("low", "medium", "high", "xhigh", "max"):
            args.extend(["--effort", str(effort).lower()])
        if native_project:
            args.append("--project=" + native_project)

        with spawn(args, cwd=cwd, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                   stderr=subprocess.PIPE) as proc:
            cancellation = threading.Event()
            tail_stop = threading.Event()
            ev_queue = queue.Queue()
            seen_transcript_steps = set()
            transcript_replies = []
            transcript_lock = threading.Lock()
            transcript_offset = [0]
            transcript_last_status = [""]

            def cancel():
                while proc.poll() is None:
                    if stop.wait(0.2) and proc.poll() is None:
                        try:
                            kill_tree(proc)
                            cancellation.set()
                        except OSError:
                            pass
                        return

            def read_stdout():
                try:
                    for raw_line in proc.stdout:
                        ev_queue.put(("line", raw_line))
                finally:
                    ev_queue.put(("eof", None))

            def tail_transcript_once():
                with transcript_lock:
                    cid = conversation_id
                tpath = _find_agy_transcript(cid)
                if not tpath:
                    return
                try:
                    size = tpath.stat().st_size
                    if size <= transcript_offset[0]:
                        return
                    with tpath.open("r", encoding="utf-8", errors="replace") as fh:
                        fh.seek(transcript_offset[0])
                        chunk = fh.read()
                        transcript_offset[0] = fh.tell()
                except OSError:
                    return
                for raw in chunk.splitlines():
                    raw = raw.strip()
                    if not raw:
                        continue
                    try:
                        entry = json.loads(raw)
                    except ValueError:
                        continue
                    if not isinstance(entry, dict):
                        continue
                    sidx = entry.get("step_index", -1)
                    stype = str(entry.get("type") or "")
                    sstat = str(entry.get("status") or "")
                    transcript_last_status[0] = sstat
                    key = (sidx, stype, sstat)
                    if key in seen_transcript_steps:
                        continue
                    seen_transcript_steps.add(key)
                    if stype == "PLANNER_RESPONSE":
                        for tc_i, tc in enumerate(entry.get("tool_calls") or []):
                            if not isinstance(tc, dict):
                                continue
                            tc_key = (sidx, "tc", tc_i)
                            if tc_key in seen_transcript_steps:
                                continue
                            seen_transcript_steps.add(tc_key)
                            tname = str(tc.get("name") or tc.get("tool_name") or "tool")
                            targs = tc.get("args") or tc.get("arguments") or {}
                            tsum = _summarize_tool_args(tname, targs)
                            phase = _classify_tool_phase(tname, tsum)
                            active_tools_map[f"{sidx}:{tc_i}"] = f"{tname}: {tsum[:24]}".strip(": ")
                            activity_log.append(f"- `{tname}` {tsum}".strip())
                            emit(
                                f"{_phase_label(phase, lang)} ({tname})",
                                preview=tsum or tname,
                                phase=phase,
                                activity=_make_activity(phase, tname, tsum, "running"),
                                append_live=f"\n> {tname} {tsum}\n",
                            )
                        content_txt = str(entry.get("content") or "").strip()
                        if content_txt and sstat == "DONE":
                            transcript_replies.append(content_txt)
                    elif stype == "GENERIC":
                        content_txt = str(entry.get("content") or "").strip()
                        if content_txt:
                            phase = _classify_tool_phase(content_txt[:40])
                            ev_st = "error" if sstat == "ERROR" else ("running" if sstat == "RUNNING" else "done")
                            snippet = " ".join(content_txt.split())[:180]
                            emit(
                                _phase_label(phase, lang),
                                preview=snippet,
                                phase=phase,
                                activity=_make_activity(phase, f"Step #{sidx} ({sstat or 'DONE'})", snippet, ev_st),
                                append_live=(content_txt[-1200:] + "\n") if sstat in ("RUNNING", "DONE", "ERROR") else "",
                            )
                    elif stype == "SYSTEM_MESSAGE":
                        content_txt = str(entry.get("content") or "").strip()
                        if content_txt:
                            phase = _classify_tool_phase("subagent", content_txt)
                            snippet = " ".join(content_txt.split())[:180]
                            emit(
                                _phase_label(phase, lang),
                                preview=snippet,
                                phase=phase,
                                activity=_make_activity(phase, "Background task update", snippet, "done"),
                            )
                    elif stype == "ERROR_MESSAGE":
                        err_txt = str(entry.get("error") or entry.get("content") or "").strip()
                        if err_txt:
                            emit("Error", preview=err_txt[:180], phase="tool",
                                 activity=_make_activity("tool", "Antigravity step error", err_txt, "error"))

            def tail_worker():
                while not tail_stop.wait(1.0):
                    if proc.poll() is not None:
                        break
                    tail_transcript_once()

            errors = []
            cancel_worker = threading.Thread(target=cancel, daemon=True)
            cancel_worker.start()
            stderr_worker = threading.Thread(target=lambda: errors.extend(proc.stderr), daemon=True)
            stderr_worker.start()
            stdout_worker = threading.Thread(target=read_stdout, daemon=True)
            stdout_worker.start()
            tailer = threading.Thread(target=tail_worker, daemon=True)
            tailer.start()

            result = None
            streamed_by_step = {}
            step_order = []
            last_heartbeat = time.monotonic()

            while True:
                try:
                    msg_kind, payload = ev_queue.get(timeout=0.25)
                except queue.Empty:
                    if time.monotonic() - last_heartbeat >= 3.0 and proc.poll() is None:
                        last_heartbeat = time.monotonic()
                        emit(last_label[0], preview=last_preview[0], phase=last_phase[0])
                    continue
                if msg_kind == "eof":
                    break
                try:
                    event = json.loads(payload)
                except ValueError:
                    continue
                if not isinstance(event, dict):
                    continue
                last_heartbeat = time.monotonic()
                kind = event.get("event")
                if kind == "init":
                    cid = event.get("conversation_id", "")
                    if cid:
                        with transcript_lock:
                            conversation_id = cid
                    emit(tr(lang, "agy_started"), phase="thinking", conversation=conversation_id,
                         activity=_make_activity("thinking", "Antigravity session started", conversation_id, "done"))
                elif kind == "step_update":
                    item = event.get("step_update") or {}
                    step = str(item.get("step_type") or "")
                    sidx = item.get("step_index", -1)
                    if step == "user_input":
                        continue
                    delta = str(item.get("text_delta") or "")
                    thinking_delta = str(item.get("thinking_delta") or "")
                    tool_name = str(item.get("tool_name") or item.get("type_name") or "")
                    tool_info = item.get("tool_info") or ""
                    status_s = str(item.get("status") or item.get("state") or "")
                    # Detect rich action payload dicts if present
                    extra_detail = ""
                    for action_key in ("run_command", "invoke_subagent", "edit_file", "create_file", "view_file",
                                       "search_web", "read_url_content", "mcp_tool", "custom_tool", "browser_action"):
                        if isinstance(item.get(action_key), dict):
                            if not tool_name:
                                tool_name = action_key
                            extra_detail = _summarize_tool_args(tool_name, item[action_key])
                            break
                    if not extra_detail and tool_info:
                        extra_detail = _summarize_tool_args(tool_name, tool_info)

                    if step == "agent_response" and (delta or item.get("text")):
                        chunk = delta or str(item.get("text") or "")
                        if sidx not in streamed_by_step:
                            step_order.append(sidx)
                            streamed_by_step[sidx] = []
                        streamed_by_step[sidx].append(chunk)
                        emit(tr(lang, "agy_reply"), preview=chunk[-180:], phase="reply", append_live=chunk)
                    elif thinking_delta or item.get("thinking"):
                        td = thinking_delta or str(item.get("thinking") or "")
                        emit(_phase_label("thinking", lang), preview=td[-180:], phase="thinking")
                    else:
                        phase = _classify_tool_phase(tool_name or step, extra_detail)
                        label = f"{_phase_label(phase, lang)} ({tool_name})" if tool_name else (
                            _phase_label(phase, lang) if phase != "tool" else tr(lang, "agy_working")
                        )
                        is_done = "DONE" in status_s.upper() or "COMPLETED" in status_s.upper()
                        is_err = "ERROR" in status_s.upper() or bool(item.get("error_message"))
                        if tool_name:
                            if is_done or is_err:
                                active_tools_map.pop(str(sidx), None)
                            else:
                                active_tools_map[str(sidx)] = f"{tool_name}: {extra_detail[:24]}".strip(": ")
                        ev_status = "error" if is_err else ("done" if is_done else "running")
                        ev_detail = str(item.get("error_message") or extra_detail or delta or "")
                        emit(
                            label,
                            preview=ev_detail or tool_name,
                            phase=phase,
                            activity=_make_activity(phase, tool_name or step or "Step", ev_detail, ev_status) if (tool_name or ev_detail) else None,
                            append_live=f"\n> {tool_name} {ev_detail}\n" if (tool_name and not is_done and ev_detail) else "",
                        )
                elif kind == "tool_call":
                    tc = event.get("tool_call") or event
                    tname = str(tc.get("tool_name") or tc.get("name") or "tool")
                    tsum = _summarize_tool_args(tname, tc.get("args") or tc.get("arguments") or tc)
                    phase = _classify_tool_phase(tname, tsum)
                    emit(f"{_phase_label(phase, lang)} ({tname})", preview=tsum or tname, phase=phase,
                         activity=_make_activity(phase, tname, tsum, "running"))
                elif kind == "error":
                    err_msg = str(event.get("error") or event.get("message") or "")
                    if err_msg:
                        emit("Error", preview=err_msg[:180], phase="tool",
                             activity=_make_activity("tool", "Stream error", err_msg, "error"))
                elif kind == "result":
                    result = event.get("result") or {}
                    if result.get("conversation_id"):
                        with transcript_lock:
                            conversation_id = result["conversation_id"]
                        emit(tr(lang, "agy_finishing"), phase="finishing", conversation=conversation_id)

            code = proc.wait()
            tail_stop.set()
            cancel_worker.join(timeout=6)
            stderr_worker.join(timeout=1)
            stdout_worker.join(timeout=1)
            tailer.join(timeout=1)

            # Flush any trailing transcript steps or subagent finishes before closing turn
            tpath = _find_agy_transcript(conversation_id)
            if tpath and code == 0 and not cancellation.is_set() and not stop.is_set():
                tail_transcript_once()
                settle_deadline = time.monotonic() + 4.5
                while time.monotonic() < settle_deadline and not stop.is_set():
                    try:
                        age = time.time() - tpath.stat().st_mtime
                    except OSError:
                        break
                    if transcript_last_status[0] == "RUNNING" or age < 1.5:
                        time.sleep(0.35)
                        tail_transcript_once()
                    else:
                        break
                tail_transcript_once()

            if cancellation.is_set() or stop.is_set():
                return "cancelled", tr(lang, "agy_cancelled")
            if result and result.get("status") == "ERROR":
                errors.append(str(result.get("error") or result.get("response") or ""))
                raise ProviderError(" ".join(errors).strip()[-480:] or tr(lang, "agy_exit_code", code=code))
            if not (result and result.get("status") == "SUCCESS" and code == 0):
                raise ProviderError(" ".join(errors).strip()[-480:] or tr(lang, "agy_exit_code", code=code))

            streamed_text = "\n\n".join(
                "".join(streamed_by_step[s]).strip() for s in step_order if "".join(streamed_by_step[s]).strip()
            ).strip()
            response_text = str(result.get("response") or "").strip()
            transcript_text = transcript_replies[-1] if transcript_replies else ""
            turn_text = max((response_text, streamed_text, transcript_text), key=len)
            denied = result.get("denied_actions") or []
            if not turn_text and denied:
                raise ProviderError(" ".join(errors).strip()[-480:] or "Antigravity headless tool permission denied.")
            if not turn_text and activity_log:
                turn_text = "\n".join(activity_log[-20:])
            if turn_text:
                transcript.append(turn_text)

            if ask_user and conversation_id and turn_text and not stop.is_set():
                q = extract_choice_question(turn_text, lang=lang)
                if q:
                    q["options"].append({
                        "label": tr(lang, "finish_session"),
                        "description": "",
                        "value": "__finish__",
                    })
                    chosen = ask_user(q)
                    if chosen and chosen.get("value") not in (None, "__finish__") and not stop.is_set():
                        current_prompt = str(chosen["value"])
                        continue
            break

    full_text = "\n\n".join(transcript).strip()
    return "finished", full_text or tr(lang, "agy_finished")


_IDE_STATUS_CACHE = {"at": 0.0, "data": None}
_IDE_STATUS_LOCK = threading.Lock()
_IDE_STATUS_REFRESHING = False


def _refresh_ide_status_bg():
    global _IDE_STATUS_REFRESHING
    try:
        ide_status(force=False)
    finally:
        with _IDE_STATUS_LOCK:
            _IDE_STATUS_REFRESHING = False


def ide_status_cached():
    """Return cached IDE status immediately without blocking on subprocesses; refresh in background if stale."""
    global _IDE_STATUS_REFRESHING
    now = time.monotonic()
    with _IDE_STATUS_LOCK:
        data = _IDE_STATUS_CACHE["data"]
        stale = data is None or (now - _IDE_STATUS_CACHE["at"] >= 20)
        if stale and not _IDE_STATUS_REFRESHING:
            _IDE_STATUS_REFRESHING = True
            threading.Thread(target=_refresh_ide_status_bg, daemon=True).start()
        if data is not None:
            return dict(data)
    return {
        "agy": {"id": "agy", "name": "Antigravity CLI", "installed": True, "path": "", "version": ""},
        "codex": {"id": "codex", "name": "ChatGPT Codex", "installed": True, "path": "", "version": ""},
    }


def ide_status(force=False):
    now = time.monotonic()
    with _IDE_STATUS_LOCK:
        if not force and _IDE_STATUS_CACHE["data"] is not None and now - _IDE_STATUS_CACHE["at"] < 20:
            return dict(_IDE_STATUS_CACHE["data"])
    if force:
        windows_tools.clear()
    result = {}
    for key, label in (("agy", "Antigravity CLI"), ("codex", "ChatGPT Codex")):
        path = ""
        version = ""
        installed = False
        try:
            path = executable(key)
            installed = bool(path)
        except ProviderError:
            installed = False
        if installed:
            try:
                proc = subprocess.run(
                    [path, "--version"],
                    capture_output=True, text=True, timeout=6,
                    creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
                )
                out = (proc.stdout or proc.stderr or "").strip().splitlines()
                if out:
                    version = out[0].strip()[:64]
            except Exception:
                version = ""
        result[key] = {"id": key, "name": label, "installed": installed, "path": path, "version": version}
    with _IDE_STATUS_LOCK:
        _IDE_STATUS_CACHE["at"] = time.monotonic()
        _IDE_STATUS_CACHE["data"] = dict(result)
    return result


def ide_install(provider, lang=DEFAULT_LANG):
    if provider not in ("agy", "codex"):
        raise ProviderError("Unsupported IDE provider.")
    flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    if provider == "codex":
        npm = shutil.which("npm.cmd") or shutil.which("npm")
        if not npm:
            raise ProviderError(
                "Node.js / npm is required to install ChatGPT Codex CLI (nodejs.org)."
                if lang != "pl" else
                "Do instalacji ChatGPT Codex CLI wymagany jest Node.js / npm (nodejs.org)."
            )
        proc = subprocess.run(
            [npm, "install", "-g", "@openai/codex"],
            capture_output=True, text=True, timeout=180, creationflags=flags,
        )
        if proc.returncode != 0:
            detail = (proc.stderr or proc.stdout or "").strip()[-240:]
            raise ProviderError(detail or f"npm exited with code {proc.returncode}")
    else:
        ps = Path(os.environ.get("SystemRoot", "C:/Windows")) / "System32/WindowsPowerShell/v1.0/powershell.exe"
        proc = subprocess.run(
            [str(ps), "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command",
             "irm https://antigravity.google/install.ps1 | iex"],
            capture_output=True, text=True, timeout=180, creationflags=flags,
        )
        if proc.returncode != 0:
            detail = (proc.stderr or proc.stdout or "").strip()[-240:]
            raise ProviderError(detail or f"Antigravity installer exited with code {proc.returncode}")
    return ide_status(force=True)


def friendly_error(error, lang=DEFAULT_LANG):
    detail = " ".join(str(error).split())[:400]
    if re.search(r"rate.?limit|quota|resource.exhausted|too many requests|\b429\b", detail, re.I):
        return "limit", detail
    if re.search(r"authenticat|unauthorized|zaloguj|sign in|\b401\b", detail, re.I):
        return "error", tr(lang, "login_on_pc", detail=detail)
    return "error", detail or type(error).__name__
