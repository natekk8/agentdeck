"""Local adapters for Codex App Server and Antigravity CLI."""

import json
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
    if isinstance(credits, (int, float)):
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


def extract_choice_question(text, lang=DEFAULT_LANG):
    """Detect a multiple-choice question (A/B/C/D or 1/2/3/4) at the end of an agent response."""
    tail = (text or "").strip()[-1600:]
    if not tail:
        return None
    lines = [line.strip() for line in tail.splitlines()]
    # Search backwards for a contiguous block of 2..4 options near the end
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
        # Allow one short trailing prompt line after options
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
    for candidate in reversed(context_before):
        if QUESTION_HINT.search(candidate):
            prompt_line = candidate
            break
    if not prompt_line:
        for candidate in context_after:
            if QUESTION_HINT.search(candidate):
                prompt_line = candidate
                break
    if not prompt_line:
        # For explicit A)/B) blocks, allow if there's any "?" near the end of text
        if best_kind == "let" and "?" in tail[-600:]:
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

    def handle_request(rpc_inst, message):
        method = message.get("method", "")
        req_id = message["id"]
        params = message.get("params") or {}
        if method == "item/tool/requestUserInput":
            if not ask_user:
                return False
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
        if method.endswith("/requestApproval"):
            if mode == "read" or not ask_user:
                rpc_inst.send({"id": req_id, "result": {"decision": "decline"}})
                return True
            q_text = params.get("command") or params.get("reason") or (
                tr(lang, "approval_prompt_cmd") if "commandExecution" in method else tr(lang, "approval_prompt_edit")
            )
            chosen = ask_user({
                "id": secrets.token_hex(6),
                "header": "Approval",
                "question": str(q_text)[:160],
                "options": [
                    {"label": tr(lang, "approve"), "description": "", "value": "accept"},
                    {"label": tr(lang, "decline"), "description": "", "value": "decline"},
                ],
            })
            decision = "accept" if chosen and chosen.get("value") == "accept" else "decline"
            rpc_inst.send({"id": req_id, "result": {"decision": decision}})
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
        update(tr(lang, "codex_starting"), conversation=thread)
        turn = rpc.call("turn/start", {
            "threadId": thread, "input": [{"type": "text", "text": prompt}],
            **({"effort": effort} if effort else {}),
        })["turn"]["id"]
        interrupted = False
        item_order = []
        item_buffers = {}
        fallback_chunks = []
        preview = ""
        cancel_deadline = None

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
            return "\n\n".join(blocks).strip()

        while True:
            event = rpc.deferred.pop(0) if rpc.deferred else rpc.receive()
            data = event.get("params") or {}
            kind = event.get("method")
            if kind == "turn/completed" and data.get("turn", {}).get("id") == turn:
                result = data["turn"]
                full_out = _collect_output()
                if result.get("status") == "completed":
                    return "finished", full_out or tr(lang, "codex_finished")
                if result.get("status") == "interrupted":
                    return "cancelled", full_out or tr(lang, "codex_interrupted")
                error = result.get("error") or {}
                raise ProviderError(error.get("message") or tr(lang, "codex_turn_failed"))
            if kind == "item/agentMessage/delta":
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
                    preview = (preview + delta)[-180:]
                    update(tr(lang, "codex_reply"), preview=preview)
            if kind == "item/completed":
                item = data.get("item") or {}
                if item.get("type") == "agentMessage":
                    item_id = str(item.get("id") or "")
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
                        preview = completed_text[-180:]
                        update(tr(lang, "codex_reply"), preview=preview)
            if kind == "item/started":
                item_type = data.get("item", {}).get("type")
                labels = {
                    "commandExecution": tr(lang, "codex_cmd"),
                    "reasoning": tr(lang, "codex_reasoning"),
                    "fileChange": tr(lang, "codex_file_change"),
                    "mcpToolCall": tr(lang, "codex_mcp"),
                }
                if item_type in labels:
                    update(labels[item_type])
            if stop.is_set() and not interrupted:
                rpc.call("turn/interrupt", {"threadId": thread, "turnId": turn}, seconds=8)
                interrupted = True
                cancel_deadline = time.monotonic() + 12
            if cancel_deadline is not None and time.monotonic() >= cancel_deadline:
                kill_tree(rpc.proc)
                raise ProviderError(tr(lang, "codex_no_interrupt"))


def agy_run(cwd, model, prompt, mode, stop, update, native_project="", effort="", lang=DEFAULT_LANG, ask_user=None):
    # Use --print <prompt> --output-format stream-json so agy waits for ALL
    # background subagents and multi-step turns before emitting the final result.
    # --mode auto + --dangerously-skip-permissions + --sandbox prevents silent
    # headless tool denials while keeping sandbox terminal restrictions active.
    exe = executable("agy", lang=lang)
    transcript = []
    conversation_id = ""
    current_prompt = prompt

    for _turn in range(6):
        if stop.is_set():
            return "cancelled", tr(lang, "agy_cancelled")
        args = [exe, "--print", current_prompt, "--model", model, "--mode", "auto",
                "--dangerously-skip-permissions", "--sandbox",
                "--disable-slash-commands", "--output-format", "stream-json"]
        if conversation_id:
            args.extend(["--conversation", conversation_id])
        if effort and str(effort).lower() in ("low", "medium", "high", "xhigh", "max"):
            args.extend(["--effort", str(effort).lower()])
        if native_project:
            args.append("--project=" + native_project)

        with spawn(args, cwd=cwd, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                   stderr=subprocess.PIPE) as proc:
            cancellation = threading.Event()

            def cancel():
                while proc.poll() is None:
                    if stop.wait(0.2) and proc.poll() is None:
                        try:
                            kill_tree(proc)
                            cancellation.set()
                        except OSError:
                            pass
                        return

            errors = []
            cancel_worker = threading.Thread(target=cancel, daemon=True)
            cancel_worker.start()
            stderr_worker = threading.Thread(target=lambda: errors.extend(proc.stderr), daemon=True)
            stderr_worker.start()

            result = None
            streamed_by_step = {}
            step_order = []
            for line in proc.stdout:
                try:
                    event = json.loads(line)
                except ValueError:
                    continue
                kind = event.get("event")
                if kind == "init":
                    cid = event.get("conversation_id", "")
                    if cid:
                        conversation_id = cid
                    update(tr(lang, "agy_started"), conversation=conversation_id)
                elif kind == "step_update":
                    item = event.get("step_update") or {}
                    step = item.get("step_type") or ""
                    if step != "user_input":
                        delta = str(item.get("text_delta") or "")
                        if step == "agent_response" and delta:
                            sidx = item.get("step_index", -1)
                            if sidx not in streamed_by_step:
                                step_order.append(sidx)
                                streamed_by_step[sidx] = []
                            streamed_by_step[sidx].append(delta)
                        tool_name = str(item.get("tool_name") or "")
                        label = tr(lang, "agy_reply") if step == "agent_response" else (
                            f"{tr(lang, 'agy_working')} ({tool_name})" if tool_name else tr(lang, "agy_working")
                        )
                        preview_src = delta or tool_name
                        if preview_src:
                            update(label, preview=preview_src[-180:])
                        else:
                            update(label)
                elif kind == "result":
                    result = event.get("result") or {}
                    if result.get("conversation_id"):
                        conversation_id = result["conversation_id"]
                        update(tr(lang, "agy_finishing"), conversation=conversation_id)

            code = proc.wait()
            cancel_worker.join(timeout=6)
            stderr_worker.join(timeout=1)

            if cancellation.is_set():
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
            turn_text = response_text if len(response_text) >= len(streamed_text) else streamed_text
            denied = result.get("denied_actions") or []
            if not turn_text and denied:
                raise ProviderError(" ".join(errors).strip()[-480:] or "Antigravity headless tool permission denied.")
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
