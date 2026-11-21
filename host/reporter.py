"""Bug Report & Feature Request reporter backed by GlitchTip (Sentry-compatible store API)."""

from datetime import datetime, timezone
import json
import os
from pathlib import Path
import platform
import re
import secrets
from urllib.error import HTTPError, URLError
from urllib.parse import urlsplit
from urllib.request import Request, urlopen

from i18n import DEFAULT_LANG, tr
from version import APP_VERSION, DEFAULT_GLITCHTIP_DSN

VALID_KINDS = ("bug", "feature", "other")
_USER_PATH_WIN = re.compile(r"([A-Za-z]:\\Users\\)([^\\/\r\n]+)", re.I)
_USER_PATH_POSIX = re.compile(r"(/(?:home|Users)/)([^/\r\n]+)")
_BEARER_RE = re.compile(r"(Bearer\s+)[A-Za-z0-9._\-]{12,}", re.I)


def resolve_dsn(settings=None):
    env_dsn = str(os.environ.get("AGENTDECK_GLITCHTIP_DSN") or "").strip()
    if env_dsn:
        return env_dsn
    if isinstance(settings, dict):
        cfg_dsn = str(settings.get("glitchtip_dsn") or "").strip()
        if cfg_dsn:
            return cfg_dsn
    return DEFAULT_GLITCHTIP_DSN.strip()


def parse_dsn(dsn, lang=DEFAULT_LANG):
    raw = str(dsn or "").strip()
    if not raw:
        raise ValueError(tr(lang, "glitchtip_not_configured"))
    parsed = urlsplit(raw)
    if parsed.scheme not in ("https", "http") or not parsed.netloc or not parsed.username:
        raise ValueError(tr(lang, "glitchtip_invalid_dsn"))
    path_parts = [p for p in parsed.path.split("/") if p]
    if not path_parts or not path_parts[-1].isdigit():
        raise ValueError(tr(lang, "glitchtip_invalid_dsn"))
    project_id = path_parts[-1]
    prefix = "/" + "/".join(path_parts[:-1]) if len(path_parts) > 1 else ""
    host = parsed.hostname
    if parsed.port:
        host = f"{host}:{parsed.port}"
    base_url = f"{parsed.scheme}://{host}{prefix}".rstrip("/")
    store_url = f"{base_url}/api/{project_id}/store/"
    auth_header = (
        f"Sentry sentry_version=7, sentry_client=agentdeck/{APP_VERSION}, "
        f"sentry_key={parsed.username}"
    )
    return {"store_url": store_url, "auth_header": auth_header, "project_id": project_id}


def sanitize_text(value):
    text = str(value or "")
    text = _USER_PATH_WIN.sub(r"\1<user>", text)
    text = _USER_PATH_POSIX.sub(r"\1<user>", text)
    text = _BEARER_RE.sub(r"\1<redacted>", text)
    return text


def collect_diagnostics(data_dir, max_chars=3600):
    data_path = Path(data_dir)
    chunks = []
    for name in ("bridge-errors.log", "bridge.log"):
        log_file = data_path / name
        if not log_file.is_file():
            continue
        try:
            text = log_file.read_text(encoding="utf-8", errors="replace")
            if text.strip():
                tail = text[-1600:]
                chunks.append(f"=== {name} ===\n{sanitize_text(tail)}")
        except OSError:
            continue
    combined = "\n\n".join(chunks)
    return combined[:max_chars]


def send_glitchtip_report(
    dsn,
    kind,
    title,
    description,
    contact="",
    diagnostics="",
    extra_tags=None,
    lang=DEFAULT_LANG,
    timeout=12,
):
    kind_clean = str(kind or "bug").strip().lower()
    if kind_clean not in VALID_KINDS:
        kind_clean = "other"
    title_clean = sanitize_text(str(title or "").strip())
    desc_clean = sanitize_text(str(description or "").strip())
    contact_clean = str(contact or "").strip()[:120]

    if len(title_clean) < 3 or len(title_clean) > 140:
        raise ValueError(tr(lang, "report_title_invalid"))
    if len(desc_clean) < 5 or len(desc_clean) > 8000:
        raise ValueError(tr(lang, "report_body_invalid"))

    parsed = parse_dsn(dsn, lang=lang)
    event_id = secrets.token_hex(16)
    kind_tag = {"bug": "Bug Report", "feature": "Feature Request", "other": "Feedback"}[kind_clean]
    tags = {
        "report_kind": kind_clean,
        "report_label": kind_tag,
        "app_version": APP_VERSION,
        "os": f"{platform.system()} {platform.release()}",
        "lang": lang,
    }
    if isinstance(extra_tags, dict):
        for k, v in extra_tags.items():
            if v is not None and str(v).strip():
                tags[str(k)[:32]] = sanitize_text(str(v)[:128])

    extra = {
        "kind": kind_clean,
        "title": title_clean,
        "description": desc_clean,
    }
    if contact_clean:
        extra["contact"] = contact_clean
    if diagnostics:
        extra["diagnostics"] = sanitize_text(diagnostics[:4000])

    payload = {
        "event_id": event_id,
        "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "platform": "python",
        "level": "error" if kind_clean == "bug" else "info",
        "logger": f"agentdeck.{kind_clean}",
        "release": f"agentdeck@{APP_VERSION}",
        "environment": "production",
        "message": {"formatted": f"[{kind_tag}] {title_clean}"},
        "tags": tags,
        "extra": extra,
    }
    if contact_clean:
        payload["user"] = {"email": contact_clean} if "@" in contact_clean else {"username": contact_clean}

    body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    req = Request(
        parsed["store_url"],
        data=body,
        method="POST",
        headers={
            "Content-Type": "application/json; charset=utf-8",
            "X-Sentry-Auth": parsed["auth_header"],
            "User-Agent": f"AgentDeck/{APP_VERSION}",
        },
    )
    try:
        with urlopen(req, timeout=timeout) as resp:
            raw = resp.read().decode("utf-8", errors="replace")
            if resp.status not in (200, 201, 202):
                raise ValueError(tr(lang, "report_send_failed", detail=f"HTTP {resp.status}"))
    except HTTPError as error:
        raise ValueError(tr(lang, "report_send_failed", detail=f"HTTP {error.code}")) from error
    except (OSError, URLError) as error:
        raise ValueError(tr(lang, "report_send_failed", detail=str(error))) from error

    returned_id = event_id
    try:
        data = json.loads(raw) if raw.strip() else {}
        if isinstance(data, dict) and data.get("id"):
            returned_id = str(data["id"])
    except ValueError:
        pass
    return {"sent": True, "event_id": returned_id, "kind": kind_clean}
