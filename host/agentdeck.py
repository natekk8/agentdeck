"""AgentDeck Windows companion. Run with Windows Python 3.12 or newer."""

import argparse
from collections import Counter, OrderedDict
from concurrent.futures import ThreadPoolExecutor
from contextlib import contextmanager
import hashlib
import json
import os
from pathlib import Path
import queue
import re
import secrets
import subprocess
import sys
import threading
import time

from i18n import DEFAULT_LANG, SUPPORTED_LANGS, normalize_lang, tr
from links import Links, desktop_server, protected, unprotected
from providers import (
    ProviderError,
    agy_account,
    agy_catalog,
    agy_conversation,
    agy_projects,
    agy_remote_control,
    agy_run,
    capture,
    codex_account,
    codex_catalog,
    codex_login_launch,
    codex_projects,
    codex_remote_control,
    codex_run,
    executable,
    friendly_error,
    model_choices,
    model_name,
    named_models,
)
from reporter import collect_diagnostics, resolve_dsn, send_glitchtip_report
from updater import Updater, check_for_update
from version import APP_VERSION

ROOT = Path(__file__).resolve().parent.parent
FRAME = b"AD1 "
FRAME_LIMIT = 16384
PAGE_ROWS = 8
ACTIVE = {"queued", "running", "stopping", "waiting_input"}
PROVIDERS = {"codex": (codex_catalog, codex_run), "agy": (agy_catalog, agy_run)}
MARKERS = (".git", "pyproject.toml", "package.json", "platformio.ini", "Cargo.toml",
           "go.mod", "AGENTS.md", "Gemfile", "CMakeLists.txt", "pom.xml")
SKIP = {"node_modules", "build", "dist", "venv", "vendor", "AppData", "Windows", "Users"}
ID = re.compile(r"[a-f0-9]{24}\Z")
CONVERSATION_ID = re.compile(r"[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}\Z", re.I)
MUTATIONS = {"start", "stop", "power", "retry", "create_project", "switch_account", "answer"}
INVALID_FOLDER_CHARS = re.compile(r'[<>:"/\\|?*\x00-\x1f]')

ADJECTIVES = (
    "solar", "amber", "cobalt", "lunar", "velvet", "cedar", "prism", "misty",
    "crimson", "nordic", "silver", "coral", "emerald", "polar", "golden", "indigo",
    "arctic", "ivory", "copper", "scarlet", "azure", "alpine", "jade", "crimson",
    "silent", "swift", "bright", "vivid", "calm", "stellar", "nova", "quartz",
    "obsidian", "kinetic", "echo", "frost", "horizon", "nimbus", "pixel", "zenith",
)
NOUNS = (
    "star", "river", "pine", "peak", "forge", "spark", "orbit", "harbor",
    "reef", "glow", "crest", "pulse", "vault", "trail", "cloud", "beacon",
    "canyon", "meadow", "ridge", "bloom", "wave", "bridge", "delta", "atlas",
    "compass", "anchor", "prism", "cove", "summit", "brook", "grove", "tower",
    "lantern", "horizon", "breeze", "stream", "comet", "island", "garden", "lens",
)


def as_json(value):
    return json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8")


def atomic_json(path, value):
    temporary = path.with_suffix(".tmp")
    temporary.write_bytes(as_json(value))
    temporary.replace(path)


def default_projects_root():
    if Path("D:/").exists():
        return Path("D:/Projects")
    return Path.home() / "Projects"


_AUTOSTART_REG_PATH = r"Software\Microsoft\Windows\CurrentVersion\Run"
_AUTOSTART_VALUE = "AgentDeck"


def autostart_command(data_dir):
    exe = Path(sys.executable)
    pythonw = exe.with_name("pythonw.exe")
    runner = str(pythonw if pythonw.is_file() else exe)
    script = str((ROOT / "host" / "agentdeck.py").resolve())
    return f'"{runner}" "{script}" --data "{Path(data_dir).resolve()}" --allow-shutdown'


def autostart_status():
    if os.name != "nt":
        return False
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, _AUTOSTART_REG_PATH, 0, winreg.KEY_READ) as key:
            value, _ = winreg.QueryValueEx(key, _AUTOSTART_VALUE)
            return bool(str(value).strip())
    except OSError:
        return False


def set_autostart(data_dir, enabled):
    if os.name != "nt":
        return False
    import winreg
    with winreg.CreateKeyEx(winreg.HKEY_CURRENT_USER, _AUTOSTART_REG_PATH, 0, winreg.KEY_SET_VALUE) as key:
        if enabled:
            winreg.SetValueEx(key, _AUTOSTART_VALUE, 0, winreg.REG_SZ, autostart_command(data_dir))
        else:
            try:
                winreg.DeleteValue(key, _AUTOSTART_VALUE)
            except FileNotFoundError:
                pass
    return autostart_status()


def random_project_name(root_dir):
    existing = set()
    if root_dir.is_dir():
        try:
            existing = {p.name.casefold() for p in root_dir.iterdir()}
        except OSError:
            existing = set()
    for _ in range(120):
        candidate = f"{secrets.choice(ADJECTIVES)}-{secrets.choice(NOUNS)}"
        if candidate.casefold() not in existing:
            return candidate
    return f"{secrets.choice(ADJECTIVES)}-{secrets.choice(NOUNS)}-{secrets.token_hex(2)}"


@contextmanager
def instance_lock(path):
    import msvcrt
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a+b") as handle:
        handle.seek(0)
        try:
            msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
        except OSError as error:
            raise SystemExit(tr(DEFAULT_LANG, "bridge_running")) from error
        try:
            yield
        finally:
            handle.seek(0)
            msvcrt.locking(handle.fileno(), msvcrt.LK_UNLCK, 1)


def available_serial(port=None):
    from serial.tools.list_ports import comports
    return next((i.device for i in comports() if
                 (i.device.casefold() == port.casefold() if port else
                  i.vid == 0x303A and i.pid == 0x1001 and
                  ("JTAG" not in i.description or "serial" in i.description.lower()))), None)


def _normalize_user_projects(raw):
    normalized = {}
    if not isinstance(raw, dict):
        return normalized
    for key, val in raw.items():
        if isinstance(val, str):
            normalized[key] = {
                "agy_project": val,
                "label": "",
                "provider": "",
                "created_on_stick": False,
            }
        elif isinstance(val, dict):
            normalized[key] = {
                "agy_project": str(val.get("agy_project") or ""),
                "label": str(val.get("label") or ""),
                "provider": str(val.get("provider") or ""),
                "created_on_stick": bool(val.get("created_on_stick")),
            }
    return normalized


def scan(roots, explicit, entries):
    output = {}
    resolved_roots = [p.resolve() for p in roots if p.is_dir()]

    def add(path, is_explicit=False, label="", provider="", agy_project="", codex_id=""):
        try:
            resolved = Path(path).resolve(strict=True)
            if not resolved.is_dir() or not (is_explicit or any(
                    resolved.is_relative_to(root) for root in resolved_roots)):
                return
            ident = hashlib.sha256(os.path.normcase(str(resolved)).encode()).hexdigest()[:16]
            git = (resolved / ".git").exists()
            existing = output.get(ident)
            if existing:
                if provider:
                    existing["providers"].add(provider)
                if label and not existing.get("label"):
                    existing["label"] = label
                if agy_project and not existing.get("agy_project"):
                    existing["agy_project"] = agy_project
                if codex_id and not existing.get("codex_id"):
                    existing["codex_id"] = codex_id
                return
            output[ident] = {
                "id": ident,
                "label": (label or resolved.name).strip() or resolved.name,
                "path": resolved,
                "git": git,
                "providers": {provider} if provider else set(),
                "agy_project": agy_project,
                "codex_id": codex_id,
            }
        except (OSError, RuntimeError, ValueError):
            return

    for p in explicit:
        add(p, True)
    for root in resolved_roots:
        remaining = [root]
        for depth in range(3):
            children = []
            for path in remaining[:256]:
                if any((path / mark).exists() for mark in MARKERS):
                    add(path)
                    continue
                if depth == 2:
                    continue
                try:
                    children.extend(i for i in path.iterdir() if not i.name.startswith(".")
                                    and i.name not in SKIP and i.is_dir() and not i.is_symlink()
                                    and not i.is_junction())
                except OSError:
                    continue
            remaining = children
    for item in entries:
        if isinstance(item, dict):
            path = Path(item.get("path", ""))
            if path.is_absolute() and path != Path.home() and path.parent != path:
                add(
                    path,
                    True,
                    label=str(item.get("label") or ""),
                    provider=str(item.get("provider") or ""),
                    agy_project=str(item.get("agy_project") or ""),
                    codex_id=str(item.get("codex_id") or ""),
                )
        else:
            path = Path(item)
            if path.is_absolute() and path != Path.home() and path.parent != path:
                add(path, True)
    return dict(sorted(output.items(), key=lambda pair: pair[1]["label"].casefold()))


class Deck:
    def __init__(self, args):
        self.args = args
        self.directory = args.data.resolve()
        self.directory.mkdir(parents=True, exist_ok=True)
        self.settings_path = self.directory / "settings.json"
        self.settings = self._load_settings()
        if not args.demo:
            dsn = resolve_dsn(self.settings)
            if dsn:
                try:
                    import sentry_sdk
                    from sentry_sdk.integrations.logging import LoggingIntegration
                    sentry_sdk.init(
                        dsn=dsn,
                        release=f"agentdeck@{APP_VERSION}",
                        traces_sample_rate=1.0,
                        integrations=[LoggingIntegration(capture_sentry_logs=True)],
                    )
                except Exception:
                    pass
        self.state_path = self.directory / ("demo-jobs.json" if args.demo else "jobs.json")
        self.jobs = {}
        if self.state_path.exists():
            self.jobs = json.loads(self.state_path.read_bytes())
        for job in self.jobs.values():
            if job["state"] in ACTIVE:
                job.update(state="orphaned", detail=tr(self.language(), "bridge_restarted"),
                           finished=int(time.time()))
        self.lock = threading.RLock()
        self.stops = {}
        self.started_at = {}
        self.pending_questions = {}
        self.prompt_path = self.directory / "project-prompts.json"
        self.project_path = self.directory / "project-folders.json"
        self.accounts_path = self.directory / "accounts.json"
        self.presets = json.loads(self.prompt_path.read_bytes()) if self.prompt_path.is_file() else []
        raw_user_projects = json.loads(self.project_path.read_bytes()) if self.project_path.is_file() else {}
        self.user_projects = _normalize_user_projects(raw_user_projects)
        self.accounts = self._load_accounts()
        self.account_cache = {}
        self.dismissed_auth_hashes = set()
        self.job_prompts = {}
        args.project.extend(Path(p) for p in self.user_projects)
        initial_entries = []
        if not args.demo:
            initial_entries.extend(codex_projects())
            initial_entries.extend(agy_projects())
        self.projects = scan(args.roots, args.project, initial_entries)
        self._apply_user_project_metadata()
        self.catalogs = {}
        self.pending = set()
        self.catalog_pool = ThreadPoolExecutor(max_workers=2)
        self.updater = Updater(ROOT, self.directory)
        self.power_armed = False
        self.deadline = None
        self.power_sent = False
        self.request_cache = OrderedDict()
        self.save()
        for provider in PROVIDERS:
            self.refresh(provider)

    def _load_settings(self):
        defaults = {
            "language": DEFAULT_LANG,
            "projects_root": str(default_projects_root()),
            "onboarded": False,
            "sentry_dsn": "",
            "glitchtip_dsn": "",
        }
        if self.settings_path.is_file():
            try:
                loaded = json.loads(self.settings_path.read_bytes())
                if isinstance(loaded, dict):
                    defaults["language"] = normalize_lang(loaded.get("language"))
                    root = str(loaded.get("projects_root") or "").strip()
                    if root:
                        defaults["projects_root"] = root
                    defaults["onboarded"] = bool(loaded.get("onboarded", False))
                    dsn = str(loaded.get("sentry_dsn") or loaded.get("glitchtip_dsn") or "").strip()
                    defaults["sentry_dsn"] = dsn
                    defaults["glitchtip_dsn"] = dsn
                    return defaults
            except (OSError, ValueError):
                pass
        atomic_json(self.settings_path, defaults)
        return defaults

    def _load_accounts(self):
        defaults = {"codex": [], "agy": []}
        if self.accounts_path.is_file():
            try:
                loaded = json.loads(self.accounts_path.read_bytes())
                if isinstance(loaded, dict):
                    for key in ("codex", "agy"):
                        if isinstance(loaded.get(key), list):
                            defaults[key] = loaded[key]
                    return defaults
            except (OSError, ValueError):
                pass
        return defaults

    def _save_accounts(self):
        atomic_json(self.accounts_path, self.accounts)

    def _apply_user_project_metadata(self):
        for ident, project in self.projects.items():
            meta = self.user_projects.get(str(project["path"]))
            if not meta:
                continue
            if meta.get("label"):
                project["label"] = meta["label"]
            if meta.get("agy_project"):
                project["agy_project"] = meta["agy_project"]
            if meta.get("provider") in PROVIDERS:
                project["providers"].add(meta["provider"])
            project["created_on_stick"] = bool(meta.get("created_on_stick"))

    def language(self, data=None):
        if isinstance(data, dict) and data.get("lang") in SUPPORTED_LANGS:
            return data["lang"]
        return normalize_lang(self.settings.get("language"))

    def save(self):
        atomic_json(self.state_path, self.jobs)

    def _codex_auth_path(self):
        return Path.home() / ".codex" / "auth.json"

    def _codex_auth_hash(self):
        auth = self._codex_auth_path()
        if not auth.is_file():
            return ""
        try:
            return hashlib.sha256(auth.read_bytes()).hexdigest()
        except OSError:
            return ""

    def _account_listing(self, provider, fetch_live=True, auto_save=False, force=False):
        lang = self.language()
        if provider == "codex":
            auth_hash = self._codex_auth_hash()
            live = {}
            if fetch_live and not self.args.demo:
                live = codex_account(lang=lang)
                if live:
                    self.account_cache["codex"] = live
            else:
                live = self.account_cache.get("codex", {})
            entries = self.accounts.get("codex", [])
            if auto_save and auth_hash and auth_hash not in self.dismissed_auth_hashes:
                already = any(
                    e.get("auth_hash") == auth_hash or (live.get("user_id") and e.get("user_id") == live.get("user_id"))
                    for e in entries
                )
                if not already:
                    try:
                        self.save_codex_account(live_info=live)
                        entries = self.accounts.get("codex", [])
                    except (OSError, ValueError):
                        pass
            profiles = []
            for item in entries:
                is_active = bool(auth_hash and item.get("auth_hash") == auth_hash)
                if not is_active and live.get("user_id") and item.get("user_id") == live.get("user_id"):
                    is_active = True
                detail_parts = [p for p in (item.get("plan"), item.get("email")) if p]
                profiles.append({
                    "id": item["id"],
                    "label": item.get("label") or item.get("email") or "Codex",
                    "email": item.get("email", ""),
                    "plan": item.get("plan", ""),
                    "active": is_active,
                    "detail": ("Active" if lang == "en" else "Aktywne") if is_active else " · ".join(detail_parts)[:36],
                })
            return {"active": live, "profiles": profiles, "has_auth_file": bool(auth_hash)}
        if provider == "agy":
            live = agy_account(lang=lang, force=force) if (fetch_live and not self.args.demo) else self.account_cache.get("agy", {})
            if live:
                self.account_cache["agy"] = live
            active_email = live.get("email", "")
            profiles = []
            for item in self.accounts.get("agy", []):
                is_active = bool(active_email and item.get("email", "").casefold() == active_email.casefold())
                profiles.append({
                    "id": item["id"],
                    "label": item.get("label") or item.get("email") or "Antigravity",
                    "email": item.get("email", ""),
                    "active": is_active,
                    "detail": ("Active" if lang == "en" else "Aktywne") if is_active else item.get("email", ""),
                })
            if not profiles and active_email:
                profiles.append({
                    "id": "current-agy",
                    "label": active_email,
                    "email": active_email,
                    "active": True,
                    "detail": "Active" if lang == "en" else "Aktywne",
                })
            return {"active": live, "profiles": profiles}
        raise ValueError(tr(lang, "unknown_provider"))

    def save_codex_account(self, label="", live_info=None):
        lang = self.language()
        auth_path = self._codex_auth_path()
        if not auth_path.is_file():
            raise ValueError(tr(lang, "account_no_auth_file"))
        raw = auth_path.read_bytes()
        auth_hash = hashlib.sha256(raw).hexdigest()
        self.dismissed_auth_hashes.discard(auth_hash)
        live = live_info if isinstance(live_info, dict) else ({} if self.args.demo else codex_account(lang=lang))
        email = str(live.get("email") or "").strip()
        plan = str(live.get("plan") or "").strip()
        user_id = str(live.get("user_id") or "").strip()
        chosen_label = label.strip() or email or (f"Codex ({plan})" if plan else f"Codex {auth_hash[:6]}")
        if len(chosen_label) > 64:
            chosen_label = chosen_label[:64]
        enc = protected(raw)
        blob = enc.decode("ascii") if isinstance(enc, bytes) else enc
        entries = list(self.accounts.get("codex", []))
        existing = next(
            (e for e in entries if e.get("auth_hash") == auth_hash or (user_id and e.get("user_id") == user_id)),
            None,
        )
        if existing:
            existing.update(
                label=chosen_label, email=email, plan=plan,
                user_id=user_id, auth_hash=auth_hash, auth_blob=blob,
            )
            profile_id = existing["id"]
        else:
            profile_id = secrets.token_hex(12)
            entries.append({
                "id": profile_id,
                "label": chosen_label,
                "email": email,
                "plan": plan,
                "user_id": user_id,
                "auth_hash": auth_hash,
                "auth_blob": blob,
            })
        self.accounts["codex"] = entries
        self._save_accounts()
        if live:
            self.account_cache["codex"] = live
        return {"id": profile_id, "label": chosen_label, "email": email, "plan": plan}

    def switch_account(self, provider, account_id, lang=None):
        lang = lang or self.language()
        if provider == "agy":
            raise ProviderError(tr(lang, "account_agy_pc_only"))
        if provider != "codex":
            raise ValueError(tr(lang, "unknown_provider"))
        entries = self.accounts.get("codex", [])
        target = next((e for e in entries if e["id"] == account_id), None)
        if not target:
            raise ProviderError(tr(lang, "account_not_found"))
        auth_path = self._codex_auth_path()
        # Preserve current active auth before overwriting if not already backed up in vault
        current_hash = self._codex_auth_hash()
        if current_hash and not any(e.get("auth_hash") == current_hash for e in entries):
            try:
                self.save_codex_account()
            except (OSError, ValueError):
                pass
        raw = unprotected(target["auth_blob"])
        auth_path.parent.mkdir(parents=True, exist_ok=True)
        temp = auth_path.with_suffix(".tmp")
        temp.write_bytes(raw)
        temp.replace(auth_path)
        self.account_cache.pop("codex", None)
        if self.catalogs.get("codex"):
            self.catalogs["codex"]["expiry"] = 0
        self.refresh("codex")
        return {
            "switched": True,
            "provider": "codex",
            "id": target["id"],
            "label": target.get("label") or target.get("email") or "Codex",
            "message": tr(lang, "account_switched", label=target.get("label") or target.get("email") or "Codex"),
        }

    def create_project(self, name="", provider="", from_stick=False, lang=None):
        lang = lang or self.language()
        root = Path(self.settings.get("projects_root") or default_projects_root()).expanduser()
        root.mkdir(parents=True, exist_ok=True)
        clean_name = name.strip()
        if clean_name:
            if len(clean_name) > 64 or clean_name in (".", "..") or INVALID_FOLDER_CHARS.search(clean_name):
                raise ValueError(tr(lang, "invalid_project_name"))
            slug = clean_name
        else:
            slug = random_project_name(root)
        target = (root / slug).resolve()
        if target.exists():
            raise ValueError(tr(lang, "project_exists"))
        target.mkdir(parents=True, exist_ok=False)
        try:
            git = executable("git", lang=lang)
            capture([git, "init"], cwd=target, timeout=10, lang=lang)
        except (OSError, ProviderError):
            pass
        prov = provider if provider in PROVIDERS else ""
        meta = {
            "agy_project": "",
            "label": target.name,
            "provider": prov,
            "created_on_stick": bool(from_stick),
        }
        updated = dict(self.user_projects, **{str(target): meta})
        atomic_json(self.project_path, updated)
        self.user_projects = updated
        if target not in self.args.project:
            self.args.project.append(target)
        scanned = scan([], [target], [])
        self.projects.update(scanned)
        self._apply_user_project_metadata()
        proj = next(p for p in self.projects.values() if p["path"] == target)
        return {
            "id": proj["id"],
            "label": proj["label"],
            "path": str(proj["path"]),
            "git": proj["git"],
            "providers": sorted(proj.get("providers") or []),
            "created_on_stick": bool(from_stick),
        }

    def rename_project(self, project_id, new_name, lang=None):
        lang = lang or self.language()
        project = self.projects.get(project_id)
        if not project:
            raise ValueError(tr(lang, "select_project_provider"))
        clean = str(new_name or "").strip()
        if not clean or len(clean) > 64 or clean in (".", "..") or INVALID_FOLDER_CHARS.search(clean):
            raise ValueError(tr(lang, "invalid_project_name"))
        old_path = project["path"]
        if any(j["state"] in ACTIVE and j.get("project") == str(old_path) for j in self.jobs.values()):
            raise ValueError(tr(lang, "project_active_jobs"))
        new_path = old_path.parent / clean
        if new_path != old_path:
            case_only = os.path.normcase(str(new_path)) == os.path.normcase(str(old_path))
            if new_path.exists() and not case_only:
                raise ValueError(tr(lang, "project_exists"))
            try:
                old_path.rename(new_path)
            except OSError as error:
                raise ValueError(tr(lang, "rename_failed", error=error)) from error
        resolved_new = new_path.resolve()
        new_id = hashlib.sha256(os.path.normcase(str(resolved_new)).encode()).hexdigest()[:16]

        old_meta = self.user_projects.get(str(old_path), {
            "agy_project": project.get("agy_project", ""),
            "label": clean,
            "provider": next(iter(project.get("providers") or []), ""),
            "created_on_stick": False,
        })
        old_meta["label"] = clean
        old_meta["created_on_stick"] = False
        updated_user = {k: v for k, v in self.user_projects.items() if k != str(old_path)}
        updated_user[str(resolved_new)] = old_meta
        atomic_json(self.project_path, updated_user)
        self.user_projects = updated_user

        self.args.project = [resolved_new if p == old_path else p for p in self.args.project]

        if new_id != project_id:
            updated_presets = []
            changed_presets = False
            for preset in self.presets:
                if preset.get("project") == project_id:
                    updated_presets.append({**preset, "project": new_id})
                    changed_presets = True
                else:
                    updated_presets.append(preset)
            if changed_presets:
                self.write_prompts(updated_presets)

        for job in self.jobs.values():
            if job.get("project") == str(old_path):
                job["project"] = str(resolved_new)
                job["project_label"] = clean
        self.save()

        self.projects.pop(project_id, None)
        providers_set = set(project.get("providers") or set())
        self.projects[new_id] = {
            "id": new_id,
            "label": clean,
            "path": resolved_new,
            "git": (resolved_new / ".git").exists(),
            "providers": providers_set,
            "agy_project": old_meta.get("agy_project", ""),
            "codex_id": project.get("codex_id", ""),
            "created_on_stick": False,
        }
        return {"id": new_id, "label": clean, "path": str(resolved_new)}

    def desktop(self, operation, request):
        lang = self.language()
        if operation == "update_check":
            return check_for_update()
        if operation == "update_download":
            return self.updater.start_download(
                request.get("asset_url", ""),
                request.get("asset_name", ""),
                request.get("version", ""),
                lang=lang,
            )
        if operation == "update_status":
            return self.updater.snapshot()
        if operation == "update_apply":
            return self.updater.apply_update(lang=lang)
        if operation == "report_submit":
            with self.lock:
                dsn = resolve_dsn(self.settings)
                include_logs = bool(request.get("include_logs"))
                diag = collect_diagnostics(self.directory) if include_logs else ""
                if dsn_override := str(request.get("dsn") or "").strip():
                    dsn = dsn_override
            return send_glitchtip_report(
                dsn=dsn,
                kind=request.get("kind", "bug"),
                title=request.get("title", ""),
                description=request.get("description", ""),
                contact=request.get("contact", ""),
                diagnostics=diag,
                lang=lang,
            )
        with self.lock:
            if operation == "settings_get":
                configured = bool(resolve_dsn(self.settings))
                return dict(self.settings) | {
                    "autostart": autostart_status(),
                    "version": APP_VERSION,
                    "sentry_configured": configured,
                    "glitchtip_configured": configured,
                }
            if operation == "settings_save":
                changed = False
                if "language" in request:
                    self.settings["language"] = normalize_lang(request.get("language"))
                    changed = True
                if "projects_root" in request:
                    raw_root = str(request.get("projects_root") or "").strip()
                    if raw_root:
                        root = Path(raw_root).expanduser()
                        root.mkdir(parents=True, exist_ok=True)
                        self.settings["projects_root"] = str(root.resolve())
                        changed = True
                if "onboarded" in request:
                    self.settings["onboarded"] = bool(request.get("onboarded"))
                    changed = True
                if "sentry_dsn" in request or "glitchtip_dsn" in request:
                    dsn = str(request.get("sentry_dsn") if "sentry_dsn" in request else request.get("glitchtip_dsn") or "").strip()
                    self.settings["sentry_dsn"] = dsn
                    self.settings["glitchtip_dsn"] = dsn
                    changed = True
                if "autostart" in request:
                    set_autostart(self.directory, bool(request.get("autostart")))
                if changed:
                    atomic_json(self.settings_path, self.settings)
                configured = bool(resolve_dsn(self.settings))
                return dict(self.settings) | {
                    "autostart": autostart_status(),
                    "version": APP_VERSION,
                    "sentry_configured": configured,
                    "glitchtip_configured": configured,
                }
            if operation == "autostart_get":
                return {"autostart": autostart_status()}
            if operation == "autostart_set":
                return {"autostart": set_autostart(self.directory, bool(request.get("enabled")))}
            if operation == "project_target":
                project = self.projects.get(request.get("project"))
                provider = request.get("provider")
                if not project or provider not in PROVIDERS:
                    raise ValueError(tr(lang, "select_project_provider"))
                return {"project": project["id"], "label": project["label"], "git": project["git"],
                        "provider": provider}
            if operation == "projects":
                provider_filter = request.get("provider", "")
                items = []
                for p in self.projects.values():
                    provs = sorted(p.get("providers") or set())
                    if provider_filter in PROVIDERS and provs and provider_filter not in provs:
                        continue
                    agy_id = p.get("agy_project") or self.user_projects.get(str(p["path"]), {}).get("agy_project", "")
                    items.append({
                        "id": p["id"],
                        "label": p["label"],
                        "path": str(p["path"]),
                        "git": p["git"],
                        "providers": provs,
                        "agy_project": agy_id,
                        "created_on_stick": bool(p.get("created_on_stick")),
                    })
                return {"items": items}
            if operation == "create_project":
                return self.create_project(
                    name=str(request.get("name") or ""),
                    provider=str(request.get("provider") or ""),
                    from_stick=False,
                    lang=lang,
                )
            if operation == "rename_project":
                return self.rename_project(
                    request.get("project"),
                    request.get("name", ""),
                    lang=lang,
                )
            if operation == "recent_sessions":
                return self.listing("sessions", {"limit": 30}) | self.overview()[0]
            if operation == "session_details":
                ident = request.get("job")
                job = self.jobs.get(ident)
                if not job:
                    raise ValueError(tr(lang, "select_stick_session"))
                output = self.result_path(job["id"])
                try:
                    with output.open(encoding="utf-8") as file:
                        answer = file.read(100_001)
                except FileNotFoundError:
                    output, answer = None, ""
                conversation = job.get("conversation") or ""
                can_open = (job["provider"] == "agy" and not self.args.demo and
                            bool(CONVERSATION_ID.fullmatch(conversation)))
                title, native_project = agy_conversation(conversation) if can_open else ("", "")
                return self.visible_job(job) | {
                    "answer": answer[:100_000], "answer_truncated": len(answer) > 100_000,
                    "output_path": str(output) if output else "", "project": job["project"],
                    "conversation": conversation, "conversation_title": title,
                    "cli_project": native_project == "default-cli-project",
                    "can_open_conversation": can_open and job["state"] not in ACTIVE,
                }
            if operation == "open_conversation":
                job = self.jobs.get(request.get("job"))
                ident = (job.get("conversation") or "") if job else ""
                if not job or job["provider"] != "agy" or self.args.demo or not CONVERSATION_ID.fullmatch(ident):
                    raise ValueError(tr(lang, "no_agy_conv"))
                if job["state"] in ACTIVE:
                    raise ValueError(tr(lang, "wait_session"))
                cwd = Path(job["project"])
                if not cwd.is_dir():
                    raise ValueError(tr(lang, "conv_dir_unavailable"))
                subprocess.Popen([executable("agy", lang=lang), "--conversation", ident, "--mode", "plan", "--sandbox"],
                                 cwd=cwd, creationflags=getattr(subprocess, "CREATE_NEW_CONSOLE", 0))
                return {}
            if operation == "add_project":
                folder = Path(request["path"]).resolve(strict=True)
                if not folder.is_dir():
                    raise ValueError(tr(lang, "select_existing_folder"))
                native = request.get("agy_project", "").strip()
                if len(native) > 120 or any(c in native for c in "\r\n\x00"):
                    raise ValueError(tr(lang, "invalid_agy_project"))
                existing = self.user_projects.get(str(folder), {})
                prov = request.get("provider", existing.get("provider", ""))
                if prov not in ("", *PROVIDERS):
                    prov = ""
                updated = dict(self.user_projects, **{
                    str(folder): {
                        "agy_project": native,
                        "label": existing.get("label") or folder.name,
                        "provider": prov,
                        "created_on_stick": existing.get("created_on_stick", False),
                    }
                })
                atomic_json(self.project_path, updated)
                self.user_projects = updated
                if folder not in self.args.project:
                    self.args.project.append(folder)
                self.projects.update(scan([], [folder], []))
                self._apply_user_project_metadata()
                return {"id": next(p["id"] for p in self.projects.values() if p["path"] == folder)}
            if operation == "prompts":
                target = request.get("project")
                return {"items": [p for p in self.presets if p["project"] == target]}
            if operation == "save_prompt":
                project = request.get("project")
                provider = request.get("provider", "")
                label, body = request["label"].strip(), request["text"].strip()
                description = str(request.get("description") or "").strip()[:160]
                if (project != "*" and project not in self.projects) or provider not in ("", *PROVIDERS):
                    raise ValueError(tr(lang, "select_project_provider"))
                if not 1 <= len(label) <= 64 or not 1 <= len(body) <= 32000:
                    raise ValueError(tr(lang, "prompt_validation"))
                ident = request.get("id") or secrets.token_hex(12)
                if not ID.fullmatch(ident):
                    raise ValueError(tr(lang, "invalid_prompt_id"))
                previous = next((p for p in self.presets if p["id"] == ident), None)
                if previous and previous["project"] != project:
                    raise ValueError(tr(lang, "prompt_wrong_project"))
                mode = request.get("mode", "read")
                if mode not in ("read", "write", "plan_execute"):
                    mode = "read"
                preset = {"id": ident, "project": project, "provider": provider,
                          "label": label, "description": description, "text": body, "mode": mode}
                self.write_prompts([p for p in self.presets if p["id"] != ident] + [preset])
                return {"item": preset}
            if operation == "answer_question":
                return self.answer_question(request.get("job"), request.get("choice"), lang=lang)
            if operation == "delete_prompt":
                self.write_prompts([p for p in self.presets if not (
                    p["id"] == request["id"] and p["project"] == request["project"]
                )])
                return {}
            if operation == "accounts":
                force = bool(request.get("force"))
                codex_data = self._account_listing("codex", fetch_live=not self.args.demo, auto_save=True, force=force)
                agy_data = self._account_listing("agy", fetch_live=not self.args.demo, force=force)
                return {"codex": codex_data, "agy": agy_data}
            if operation == "save_account":
                provider = request.get("provider", "codex")
                label = str(request.get("label") or "")
                if provider == "codex":
                    return {"item": self.save_codex_account(label)}
                if provider == "agy":
                    live = agy_account(lang=lang) if not self.args.demo else {}
                    email = str(live.get("email") or label).strip()
                    if not email:
                        raise ValueError(tr(lang, "account_label_required"))
                    entries = list(self.accounts.get("agy", []))
                    existing = next((e for e in entries if e.get("email", "").casefold() == email.casefold()), None)
                    if existing:
                        existing["label"] = label.strip() or email
                        pid = existing["id"]
                    else:
                        pid = secrets.token_hex(12)
                        entries.append({"id": pid, "label": label.strip() or email, "email": email})
                    self.accounts["agy"] = entries
                    self._save_accounts()
                    return {"item": {"id": pid, "label": label.strip() or email, "email": email}}
                raise ValueError(tr(lang, "unknown_provider"))
            if operation == "switch_account":
                return self.switch_account(request.get("provider", "codex"), request.get("account", ""), lang=lang)
            if operation == "delete_account":
                provider = request.get("provider", "codex")
                ident = request.get("account", "")
                if provider not in ("codex", "agy"):
                    raise ValueError(tr(lang, "unknown_provider"))
                removed = [e for e in self.accounts.get(provider, []) if e.get("id") == ident]
                for item in removed:
                    if item.get("auth_hash"):
                        self.dismissed_auth_hashes.add(item["auth_hash"])
                self.accounts[provider] = [e for e in self.accounts.get(provider, []) if e.get("id") != ident]
                self._save_accounts()
                return {}
            if operation == "login_account":
                provider = request.get("provider", "codex")
                if provider == "codex":
                    self.dismissed_auth_hashes.clear()
                    codex_login_launch(lang=lang)
                    return {"launched": True}
                raise ValueError(tr(lang, "account_agy_pc_only"))
            if operation == "remote_control_status":
                res = {}
                try:
                    res["codex"] = codex_remote_control("status", lang=lang)
                except (OSError, ProviderError) as err:
                    res["codex"] = {"provider": "codex", "running": False, "error": str(err)}
                try:
                    res["agy"] = agy_remote_control("status", lang=lang)
                except (OSError, ProviderError) as err:
                    res["agy"] = {"provider": "agy", "running": False, "registered": False, "error": str(err)}
                return res
            if operation == "remote_control_action":
                provider = request.get("provider", "")
                action = request.get("action", "status")
                name = str(request.get("name") or "")
                if provider == "codex":
                    return {"status": codex_remote_control(action, lang=lang)}
                if provider == "agy":
                    return {"status": agy_remote_control(action, name=name, lang=lang)}
                raise ValueError(tr(lang, "rc_invalid_provider"))
        raise ValueError(tr(lang, "unknown_desktop_op"))

    def write_prompts(self, presets):
        atomic_json(self.prompt_path, presets)
        self.presets = presets

    def refresh(self, provider):
        if provider in self.pending:
            return
        if self.catalogs.get(provider, {}).get("expiry", 0) > time.monotonic():
            return
        self.pending.add(provider)
        self.catalog_pool.submit(self.load_catalog, provider)

    def load_catalog(self, provider):
        lang = self.language()
        try:
            if self.args.demo:
                models, limits, projects = (
                    [{"id": "demo", "label": tr(lang, "demo_model")}],
                    [{"group": "Demo", "window": "5h", "remaining": 80,
                      "reset": int(time.time()) + 3400}], [],
                )
            else:
                models, limits, projects = PROVIDERS[provider][0](lang=lang)
            found = scan(self.args.roots, self.args.project, projects)
            with self.lock:
                # Preserve user-assigned provider tags and refresh live IDE provider tags
                for entry in self.projects.values():
                    entry.setdefault("providers", set()).discard(provider)
                for ident, new_entry in found.items():
                    if ident in self.projects:
                        existing = self.projects[ident]
                        existing["git"] = new_entry["git"]
                        if new_entry["providers"]:
                            existing.setdefault("providers", set()).update(new_entry["providers"])
                        if new_entry.get("agy_project") and not existing.get("agy_project"):
                            existing["agy_project"] = new_entry["agy_project"]
                        if new_entry.get("codex_id") and not existing.get("codex_id"):
                            existing["codex_id"] = new_entry["codex_id"]
                    else:
                        self.projects[ident] = new_entry
                self._apply_user_project_metadata()
                self.catalogs[provider] = {"models": models, "limits": limits, "asof": int(time.time()),
                                           "expiry": time.monotonic() + 300}
        except (OSError, ProviderError, ValueError) as error:
            with self.lock:
                old = self.catalogs.setdefault(provider, {"models": [], "limits": [], "asof": 0})
                old.update(error=friendly_error(error, lang=lang)[1], expiry=time.monotonic() + 20)
                print(f"{provider}: {old['error']}", flush=True)
        finally:
            with self.lock:
                self.pending.discard(provider)

    def visible_job(self, job):
        end = job.get("finished") or int(time.time())
        elapsed = max(0, end - job["started"]) if job["started"] else 0
        start = self.started_at.get(job["id"])
        if start is not None and job["state"] in ACTIVE:
            elapsed = max(0, int(time.monotonic() - start))
        keys = ("id", "label", "provider", "project_label", "model_label", "state", "detail",
                "preview", "mode", "effort")
        payload = {k: job.get(k, "") for k in keys} | {"elapsed": elapsed}
        q = job.get("question")
        if isinstance(q, dict):
            payload["question"] = {
                "id": str(q.get("id") or ""),
                "header": str(q.get("header") or "")[:32],
                "question": str(q.get("question") or "")[:160],
                "options": [
                    {
                        "label": str(opt.get("label") or "")[:44],
                        "description": str(opt.get("description") or "")[:48],
                    }
                    for opt in (q.get("options") or [])[:4]
                ],
            }
        return payload

    def answer_question(self, job_id, choice_index, lang=DEFAULT_LANG):
        entry = self.pending_questions.get(job_id)
        job = self.jobs.get(job_id)
        if not entry or not job:
            raise ProviderError(tr(lang, "no_pending_question"))
        options = entry["question"].get("options") or []
        try:
            idx = int(choice_index)
        except (TypeError, ValueError) as error:
            raise ValueError(tr(lang, "invalid_choice")) from error
        if not 0 <= idx < len(options):
            raise ValueError(tr(lang, "invalid_choice"))
        entry["answer"] = options[idx]
        job.pop("question", None)
        if job["state"] == "waiting_input":
            job["state"] = "running"
        job["detail"] = tr(lang, "answer_sent")
        entry["event"].set()
        return {"job": self.visible_job(job)}

    def overview(self):
        jobs = sorted(reversed(list(self.jobs.values())), key=lambda j: j["state"] not in ACTIVE)
        return {
            "active": sum(j["state"] in ACTIVE for j in jobs), "demo": self.args.demo,
            "armed": self.power_armed, "power_sent": self.power_sent,
            "power_allowed": self.args.allow_shutdown or self.args.demo,
            "cooldown": max(0, int(self.deadline - time.monotonic() + .999)) if self.deadline else None,
            "lang": self.language(),
        }, jobs

    def request(self, data):
        with self.lock:
            lang = self.language(data)
            if not isinstance(data, dict) or not ID.fullmatch(str(data.get("id", ""))):
                return {"ok": False, "error": tr(lang, "invalid_request")}
            ident, operation = data["id"], data.get("op")
            if not isinstance(operation, str):
                return {"ok": False, "id": ident, "error": tr(lang, "invalid_operation")}
            if operation in MUTATIONS and ident in self.request_cache:
                return self.request_cache[ident]
            try:
                response = {"ok": True, "id": ident} | self.route(operation, data, lang)
            except (ProviderError, KeyError, OSError, ValueError, TypeError) as error:
                response = {"ok": False, "id": ident, "error": friendly_error(error, lang=lang)[1]}
            if operation in MUTATIONS:
                self.request_cache[ident] = response
                if len(self.request_cache) > 512:
                    self.request_cache.popitem(last=False)
            return response

    def route(self, operation, data, lang=DEFAULT_LANG):
        if operation == "clock":
            return {"time": int(time.time())}
        if operation == "status":
            values, _ = self.overview()
            return values
        if operation == "sync_settings":
            new_lang = normalize_lang(data.get("lang"))
            if new_lang != self.settings.get("language"):
                self.settings["language"] = new_lang
                atomic_json(self.settings_path, self.settings)
            return {"lang": self.settings["language"]}
        if operation in {"projects", "models", "efforts", "prompts", "sessions", "limits", "accounts"}:
            return self.listing(operation, data, lang=lang)
        if operation == "create_project":
            provider = data.get("provider", "")
            created = self.create_project(name="", provider=provider, from_stick=True, lang=lang)
            return {"project": created}
        if operation == "switch_account":
            return self.switch_account(data.get("provider", ""), data.get("account", ""), lang=lang)
        if operation == "job":
            job = self.jobs[data["job"]]
            result = self.overview()[0] | {"job": self.visible_job(job)}
            if job["state"] == "finished" and "output_page" in data:
                page = max(0, min(4096, int(data["output_page"])))
                path = self.result_path(job["id"])
                if path.is_file():
                    page_size = 420
                    with path.open(encoding="utf-8") as output:
                        text = output.read(page * page_size + page_size + 1)[page * page_size:]
                    result["output"] = {"page": page, "text": " ".join(text[:page_size].split()),
                                        "more": len(text) > page_size}
            return result
        if operation == "start":
            return self.start(data, lang=lang)
        if operation == "answer":
            return self.answer_question(data.get("job"), data.get("choice"), lang=lang)
        if operation == "retry":
            prior = self.jobs[data["job"]]
            if prior["state"] in ACTIVE:
                raise ProviderError(tr(lang, "wait_session"))
            project_id = next((key for key, project in self.projects.items()
                               if str(project["path"]) == prior["project"]), "")
            return self.start({"id": data["id"], "project": project_id, "effort": prior.get("effort", ""), **{
                k: prior[k] for k in ("provider", "model", "prompt", "mode")
            }}, lang=lang)
        if operation == "stop":
            job = self.jobs[data["job"]]
            if job["state"] == "queued":
                job.update(state="cancelled", detail=tr(lang, "cancelled_in_queue"), finished=int(time.time()))
                self.job_prompts.pop(job["id"], None)
                self.save()
            elif job["state"] in ("running", "waiting_input"):
                job.update(state="stopping", detail=tr(lang, "stopping"))
                job.pop("question", None)
                self.stops[job["id"]].set()
                if pending := self.pending_questions.get(job["id"]):
                    pending["event"].set()
                self.save()
            return {"job": self.visible_job(job)}
        if operation == "power":
            armed = data.get("armed")
            if type(armed) is not bool:
                raise ValueError(tr(lang, "invalid_power_mode"))
            if armed and data.get("confirm") != "manual":
                raise ProviderError(tr(lang, "confirm_manual_shutdown"))
            if armed and not (self.args.allow_shutdown or self.args.demo):
                raise ProviderError(tr(lang, "shutdown_disabled"))
            if armed and any(j["state"] in ACTIVE | {"orphaned"} for j in self.jobs.values()):
                raise ProviderError(tr(lang, "check_active_sessions"))
            self.power_armed = armed
            self.deadline = time.monotonic() + 30 if armed else None
            self.power_sent = False
            return self.overview()[0]
        raise ValueError(tr(lang, "unknown_operation"))

    def listing(self, operation, data, lang=DEFAULT_LANG):
        provider = data.get("provider")
        result = {}
        if operation in {"models", "efforts", "limits"}:
            if provider not in PROVIDERS:
                raise ValueError(tr(lang, "unknown_provider"))
            self.refresh(provider)
            catalog = self.catalogs.get(provider, {})
            result = {key: catalog[key] for key in ("asof", "error") if key in catalog}
            result["now"] = int(time.time())
            result["loading"] = provider in self.pending
            limits = catalog.get("limits", [])
            if operation == "models":
                rows = named_models(catalog.get("models", []))
            elif operation == "efforts":
                models = catalog.get("models", [])
                model = next((item for item in models if item["id"] == data.get("model")), None)
                if not model and not result["loading"]:
                    raise ProviderError(tr(lang, "model_unavailable"))
                choices, default = model_choices(models, model) if model else ({}, "")
                default_label = tr(lang, "effort_default")
                labels = {"default": default_label, "xhigh": "XHigh"}
                rows = [{"id": key, "label": labels.get(key, key.capitalize()),
                         "detail": default_label if key == default and key != "default" else ""}
                        for key in choices]
            else:
                rows = [{"id": str(i), "label": f"{m['group']} {m['window']}",
                         "detail": tr(lang, "remaining_pct", pct=m["remaining"]), **m}
                        for i, m in enumerate(limits)]
        elif operation == "projects":
            if provider in PROVIDERS:
                self.refresh(provider)
                result["loading"] = provider in self.pending
            recent = self.recent_for(provider, "project")
            positions = {path: i for i, path in enumerate(recent)}
            filtered = [
                entry for entry in self.projects.values()
                if provider not in PROVIDERS or not entry.get("providers") or provider in entry["providers"]
            ]
            projects = sorted(filtered, key=lambda entry: positions.get(str(entry["path"]), len(recent)))
            names = Counter(p["label"].casefold() for p in projects)
            rows = [{k: p[k] for k in ("id", "label", "git")} | {
                "detail": str(p["path"].parent) if names[p["label"].casefold()] > 1 else "",
            } for p in projects]
        elif operation == "prompts":
            target_project = data.get("project")
            rows = [{"id": p["id"], "label": p["label"], "write": p.get("mode") in ("write", "plan_execute"),
                     "mode": p.get("mode", "read"),
                     "description": str(p.get("description") or "")[:120],
                     "detail": "*" if p["project"] == "*" else ""}
                    for p in self.presets
                    if p["project"] in (target_project, "*") and p["provider"] in ("", provider)]
            rows.sort(key=lambda row: row["label"].casefold())
        elif operation == "accounts":
            if provider not in PROVIDERS:
                raise ValueError(tr(lang, "unknown_provider"))
            info = self._account_listing(provider, fetch_live=False)
            rows = [{"id": p["id"], "label": p["label"], "detail": p["detail"],
                     "state": "active" if p.get("active") else ""}
                    for p in info["profiles"]]
        else:
            result, _ = self.overview()
            limit = int(data.get("limit") or 5)
            rows = [{"id": j["id"], "label": j["label"], "detail": j["project_label"],
                     "state": j["state"]} for j in reversed(self.jobs.values())
                    if j.get("source", "stick") == "stick"][:limit]
            if "limit" in data:
                offset = max(0, min(len(rows), int(data.get("offset", 0))))
                items = rows[offset:offset + limit]
                return result | {"items": items, "offset": offset, "total": len(rows), "page_size": limit}
        offset = max(0, min(len(rows), int(data.get("offset", 0))))
        items = rows[offset:offset + PAGE_ROWS]
        return result | {"items": items, "offset": offset, "total": len(rows), "page_size": PAGE_ROWS}

    def recent_for(self, provider, field):
        return list(dict.fromkeys(job[field] for job in reversed(self.jobs.values())
                                  if job["provider"] == provider))

    def start(self, data, lang=DEFAULT_LANG):
        ident = data["id"]
        if ident in self.jobs:
            return {"job": self.visible_job(self.jobs[ident])}
        provider = data["provider"]
        if provider not in PROVIDERS:
            raise ValueError(tr(lang, "unknown_provider"))
        if self.power_sent:
            raise ProviderError(tr(lang, "shutdown_sent"))
        if sum(j["state"] in ACTIVE for j in self.jobs.values()) >= 12:
            raise ProviderError(tr(lang, "queue_full"))
        project = self.projects.get(data.get("project"))
        models = self.catalogs.get(provider, {}).get("models", [])
        model = next((m for m in models if m["id"] == data.get("model")), None)
        preset = next((p for p in self.presets if p["id"] == data.get("prompt")
                       and p["project"] in (data.get("project"), "*")
                       and p["provider"] in ("", provider)), None)
        if not (project and model and preset):
            raise ProviderError(tr(lang, "select_project_model_prompt"))
        path = project["path"]
        if path.resolve(strict=True) != path or not path.is_dir():
            raise ProviderError(tr(lang, "project_path_changed"))
        mode = data.get("mode", "read")
        if mode not in {"read", "write", "plan_execute"}:
            raise ValueError(tr(lang, "unknown_session_mode"))
        effort = data.get("effort", "")
        choices, _ = model_choices(models, model)
        if not isinstance(effort, str) or (effort and effort not in choices):
            raise ProviderError(tr(lang, "effort_unavailable"))
        job = {"id": ident, "source": "stick", "label": preset["label"], "provider": provider,
               "project_label": project["label"], "project": str(path), "model": choices.get(effort, model["id"]),
               "model_label": model_name(model), "effort": effort, "prompt": preset["id"], "state": "queued",
               "detail": tr(lang, "queued"), "preview": "", "mode": mode, "started": 0, "finished": 0}
        self.jobs[ident] = job
        self.job_prompts[ident] = preset["text"]
        self.power_armed = False
        self.deadline = None
        self.save()
        return {"job": self.visible_job(job)}

    def result_path(self, ident):
        return self.directory / ("demo-results" if self.args.demo else "results") / f"{ident}.txt"

    def run(self, job):
        ident = job["id"]
        stop = self.stops[ident]
        lang = self.language()

        def update(detail, preview="", **extra):
            with self.lock:
                if job["state"] == "running":
                    job["detail"] = detail[:180]
                if preview:
                    job["preview"] = " ".join(preview.split())[:180]
                job.update(extra)

        def ask_user(question_dict):
            ev = threading.Event()
            with self.lock:
                if stop.is_set():
                    return None
                self.pending_questions[ident] = {"event": ev, "question": question_dict, "answer": None}
                job["state"] = "waiting_input"
                job["question"] = question_dict
                job["detail"] = str(question_dict.get("question") or "")[:180]
                self.save()
            while not stop.is_set() and not ev.is_set():
                ev.wait(0.2)
            with self.lock:
                entry = self.pending_questions.pop(ident, None)
                job.pop("question", None)
                if job.get("state") == "waiting_input":
                    job["state"] = "running"
                self.save()
            return entry["answer"] if entry and not stop.is_set() else None

        try:
            path = Path(job["project"])
            if path.resolve(strict=True) != path:
                raise ProviderError(tr(lang, "project_path_changed"))
            if job["mode"] in ("write", "plan_execute") and job["provider"] == "codex" and not self.args.demo and (path / ".git").exists():
                git = executable("git", lang=lang)
                try:
                    actual = Path(capture([git, "-C", str(path), "rev-parse", "--show-toplevel"], lang=lang).strip()).resolve()
                    has_head = True
                    try:
                        capture([git, "-C", str(path), "rev-parse", "--verify", "HEAD"], lang=lang)
                    except ProviderError:
                        has_head = False
                    if actual == path and has_head:
                        worktree = self.directory / "worktrees" / ident
                        worktree.parent.mkdir(exist_ok=True)
                        update(tr(lang, "creating_worktree"))
                        capture([git, "-C", str(path), "worktree", "add", "--detach", str(worktree), "HEAD"],
                                timeout=90, lang=lang)
                        path = worktree
                        update(tr(lang, "isolated_worktree"), worktree=str(worktree))
                except ProviderError:
                    pass
            body = self.job_prompts[ident]
            guard = tr(lang, "guard_base")
            if job["mode"] == "read":
                guard += tr(lang, "guard_read")
            elif job["mode"] == "write":
                guard += tr(lang, "guard_write") + tr(lang, "guard_questions")
            else:
                guard += tr(lang, "guard_plan_execute") + tr(lang, "guard_questions")
            if self.args.demo:
                for step in range(6):
                    update(tr(lang, "demo_step", step=step + 1), tr(lang, "demo_preview"))
                    if stop.wait(1):
                        break
                status = "cancelled" if stop.is_set() else "finished"
                detail = tr(lang, "demo_result")
            elif stop.is_set():
                status, detail = "cancelled", tr(lang, "cancelled_before_start")
            else:
                native = self.projects.get(
                    hashlib.sha256(os.path.normcase(job["project"]).encode()).hexdigest()[:16], {}
                ).get("agy_project") or self.user_projects.get(job["project"], {}).get("agy_project", "")
                options = {"native_project": native, "lang": lang, "ask_user": ask_user} if job["provider"] == "agy" else {"lang": lang, "ask_user": ask_user}
                if job["provider"] == "agy" and not native:
                    options.pop("native_project", None)
                if job["provider"] == "codex" and job.get("effort") not in ("", "default", None):
                    options["effort"] = job["effort"]
                status, detail = PROVIDERS[job["provider"]][1](
                    path, job["model"], guard + body, job["mode"], stop, update, **options)
        except Exception as error:
            status, detail = friendly_error(error, lang=lang)
        with self.lock:
            self.pending_questions.pop(ident, None)
            job.pop("question", None)
            self.job_prompts.pop(ident, None)
            if status == "finished":
                output = self.result_path(ident)
                try:
                    output.parent.mkdir(exist_ok=True)
                    output.write_text(detail, encoding="utf-8")
                except OSError as error:
                    status, detail = friendly_error(error, lang=lang)
            job.update(state=status, detail=(tr(lang, "completed") if status == "finished" else detail[:400]),
                       preview=" ".join(detail.split())[:180], finished=int(time.time()))
            if self.catalogs.get(job["provider"]):
                self.catalogs[job["provider"]]["expiry"] = 0
            self.stops.pop(ident, None)
            self.started_at.pop(ident, None)
            self.save()
        print(f"{ident} {status} {job['label']}", flush=True)

    def tick(self):
        lang = self.language()
        with self.lock:
            running = len(self.stops)
            if not self.power_sent:
                for job in self.jobs.values():
                    if running >= 2:
                        break
                    if job["state"] != "queued":
                        continue
                    job.update(state="running", started=int(time.time()), detail=tr(lang, "starting"))
                    self.started_at[job["id"]] = time.monotonic()
                    self.stops[job["id"]] = threading.Event()
                    self.save()
                    threading.Thread(target=self.run, args=(job,), daemon=True).start()
                    running += 1
            if self.deadline is None:
                return
            now = time.monotonic()
            if now < self.deadline:
                return
            self.power_armed = False
            self.deadline = None
            if not self.args.demo:
                result = subprocess.run(
                    ["shutdown.exe", "/s", "/t", "0"],
                    check=False,
                    stdin=subprocess.DEVNULL,
                    stdout=subprocess.DEVNULL,
                    stderr=subprocess.DEVNULL,
                    creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
                )
                if result.returncode:
                    print("Windows rejected shutdown.", flush=True)
                    return
            self.power_sent = True
            print("Shutdown command sent" if not self.args.demo else "Simulated shutdown",
                  flush=True)


def serial_loop(deck, port, done, links):
    import serial

    while not done.is_set():
        device = available_serial(port)
        if device is None:
            done.wait(1)
            continue
        action = None
        try:
            with serial.Serial(device, 115200, timeout=.10, write_timeout=1) as link:
                print(f"AgentDeck USB: {device}", flush=True)
                buffer = bytearray()
                status_id = ""
                next_status = 0
                last_contact = 0
                while not done.is_set():
                    now = time.monotonic()
                    if not action:
                        try:
                            action = links.usb.get_nowait()
                        except queue.Empty:
                            pass
                        else:
                            if now < action.expires:
                                link.write(FRAME + as_json(action.request) + b"\n")
                                action_expiry = now + 3
                            else:
                                action = None
                    elif now > action_expiry:
                        action.ready.set()
                        action = None
                    if not action and now >= next_status:
                        status_id = secrets.token_hex(12)
                        link.write(FRAME + as_json({"op": "device_control", "action": "status",
                                                    "id": status_id}) + b"\n")
                        next_status = now + .90
                    if last_contact and now - last_contact > 4:
                        links.usb_ready = False
                    buffer.extend(link.read(max(1, min(FRAME_LIMIT, link.in_waiting))))
                    while (end := buffer.find(b"\n")) >= 0:
                        frame = bytes(buffer[:end]).strip()
                        del buffer[:end + 1]
                        if not frame.startswith(FRAME) or len(frame) > FRAME_LIMIT:
                            continue
                        try:
                            message = json.loads(frame[len(FRAME):])
                        except (ValueError, UnicodeError):
                            continue
                        if not isinstance(message, dict):
                            continue
                        if message.get("id") == status_id and "op" not in message:
                            links.observe(message.get("snapshot"), usb=True)
                            last_contact = time.monotonic()
                            continue
                        if action and message.get("id") == action.request["id"] and "op" not in message:
                            action.answer = message
                            action.ready.set()
                            action = None
                            last_contact = time.monotonic()
                            continue
                        if "op" not in message:
                            continue
                        response = deck.request(message)
                        link.write(FRAME + as_json(response) + b"\n")
                        if response.get("ok") and not links.usb_ready:
                            print(f"StickS3 answered on {device}.", flush=True)
                            links.usb_ready = True
                        if response.get("ok"):
                            last_contact = time.monotonic()
                    if len(buffer) > FRAME_LIMIT:
                        buffer.clear()
        except (serial.SerialException, OSError) as error:
            print(f"USB: {error}", flush=True)
        finally:
            with links.lock:
                links.usb_ready = False
                links.usb_device = ""
            if action:
                action.ready.set()
        done.wait(1)


def cli():
    parser = argparse.ArgumentParser(description="AgentDeck Windows companion")
    parser.add_argument("--port", help="Serial port, e.g. COM3")
    parser.add_argument("--root", dest="roots", action="append", type=Path)
    parser.add_argument("--project", action="append", type=Path, default=[])
    parser.add_argument("--data", type=Path, default=Path("D:/AgentDeck-data") if Path("D:/").exists()
                        else Path(os.environ.get("LOCALAPPDATA", ROOT)) / "AgentDeck")
    parser.add_argument("--quit", action="store_true", help="Stop bridge after checking active sessions")
    parser.add_argument("--demo", action="store_true")
    parser.add_argument("--allow-shutdown", action="store_true")
    args = parser.parse_args()
    args.data = args.data.resolve()
    if args.demo:
        args.data = args.data / "demo"
    stop_file = args.data / "stop-request"
    alive_file = args.data / "bridge-alive.json"
    lock_file = args.data / "bridge.lock"
    if args.quit:
        if alive_file.is_file():
            atomic_json(stop_file, {"requested": int(time.time())})
        deadline = time.monotonic() + 8
        while time.monotonic() < deadline:
            try:
                with instance_lock(lock_file):
                    alive_file.unlink(missing_ok=True)
                    stop_file.unlink(missing_ok=True)
                    return
            except SystemExit:
                pass
            time.sleep(.15)
        raise SystemExit(tr(DEFAULT_LANG, "bridge_still_running"))
    args.roots = args.roots or []
    args.project.append(ROOT)
    with instance_lock(lock_file):
        if sys.stdout is None:
            sys.stdout = (args.data / "bridge.log").open("a", encoding="utf-8", buffering=1)
        if sys.stderr is None:
            sys.stderr = (args.data / "bridge-errors.log").open("a", encoding="utf-8", buffering=1)
        stop_file.unlink(missing_ok=True)
        (args.data / "lan.json").unlink(missing_ok=True)
        (args.data / "lan.key").unlink(missing_ok=True)
        run_bridge(args, stop_file, alive_file)


def run_bridge(args, stop_file, alive_file):
    deck = Deck(args)
    done = threading.Event()
    links = Links(deck.directory, deck, done)
    server, desktop_marker = desktop_server(deck, links)
    usb = threading.Thread(target=serial_loop, args=(deck, args.port, done, links), daemon=True)
    lan = threading.Thread(target=links.run, daemon=True)
    try:
        usb.start()
        lan.start()
        atomic_json(alive_file, {"pid": os.getpid()})
        print(f"AgentDeck {'DEMO ' if args.demo else ''}ready. Projects: {len(deck.projects)}.", flush=True)
        while not done.wait(.2):
            if stop_file.is_file():
                stop_file.unlink()
                with deck.lock:
                    if not any(j["state"] in ACTIVE for j in deck.jobs.values()):
                        return
            deck.tick()
    except KeyboardInterrupt:
        pass
    finally:
        done.set()
        usb.join(timeout=1.5)
        links.local_relay_stop()
        server.shutdown()
        server.server_close()
        deck.catalog_pool.shutdown(wait=False, cancel_futures=True)
        desktop_marker.unlink(missing_ok=True)
        alive_file.unlink(missing_ok=True)


if __name__ == "__main__":
    cli()
