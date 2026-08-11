"""Secret-free review/commit coordinator for configured MCP servers.

The dashboard may edit only non-secret fields. Existing env/header credentials
stay server-side and no API in this module accepts replacement secret values.
"""
from __future__ import annotations

import hashlib
import json
import re
import secrets
import threading
import time
from dataclasses import dataclass
from typing import Any, Dict, Optional
from urllib.parse import urlsplit, urlunsplit

from hermes_cli.mcp_security import validate_mcp_server_entry

CONTRACT_VERSION = 3
REVIEW_TTL_SECONDS = 120
MAX_REVISION_RECORDS = 256
MAX_REVIEW_RECORDS = 32

_ENVIRONMENT_NAME = re.compile(r"^[A-Za-z_][A-Za-z0-9_]{0,127}$")
_CONTROL_OR_DIRECTIONAL = re.compile(
    r"[\x00-\x1f\x7f-\x9f\u200b-\u200f\u202a-\u202e\u2060-\u2069\ufeff]"
)
_INLINE_CREDENTIAL = re.compile(
    r"(?:^|[\s\"'=])(?:--?|/)(?:api[-_]?key|token|secret|password|authorization|credential)(?:=|:|\s|$)|\bbearer\s+\S+",
    re.IGNORECASE,
)
_CREDENTIAL_ASSIGNMENT = re.compile(
    r"(?:^|[\s,;{])['\"]?(?:[A-Za-z][A-Za-z0-9_.-]{0,63}[._-])?"
    r"(?:api[-_]?key|access[-_]?token|refresh[-_]?token|auth[-_]?token|token|secret|password|authorization|credential)"
    r"['\"]?\s*[:=]\s*['\"]?\S+",
    re.IGNORECASE,
)
_HTTP_URL_IN_TEXT = re.compile(r"https?://[^\s\"']+", re.IGNORECASE)


class McpEditorError(ValueError):
    def __init__(self, reason: str):
        super().__init__(reason)
        self.reason = reason


@dataclass(frozen=True)
class ReviewRecord:
    handle: str
    scope: str
    name: str
    revision: str
    expires_at: float
    risk: str
    next_config: Dict[str, Any]


_lock = threading.RLock()
_revision_records: Dict[tuple[str, str], tuple[str, str]] = {}
_review_records: Dict[str, ReviewRecord] = {}
_target_handles: Dict[tuple[str, str], str] = {}


def _raw_fingerprint(config: Dict[str, Any]) -> str:
    encoded = json.dumps(config, sort_keys=True, separators=(",", ":"), default=str).encode()
    return hashlib.sha256(encoded).hexdigest()


def server_revision(scope: Optional[str], name: str, config: Dict[str, Any]) -> str:
    """Return a random opaque revision for one exact raw config fingerprint."""
    key = (scope or "", name)
    fingerprint = _raw_fingerprint(config)
    with _lock:
        current = _revision_records.get(key)
        if current and current[0] == fingerprint:
            return current[1]
        revision = f"mcp-rev:{secrets.token_urlsafe(24)}"
        _revision_records[key] = (fingerprint, revision)
        while len(_revision_records) > MAX_REVISION_RECORDS:
            _revision_records.pop(next(iter(_revision_records)))
        return revision


def renderer_safe_url(value: Any) -> tuple[str, bool]:
    raw = str(value or "").strip()
    if not raw:
        return "", True
    try:
        parsed = urlsplit(raw)
    except ValueError:
        return "", False
    if parsed.scheme not in {"http", "https"} or not parsed.hostname:
        return "", False
    if parsed.username or parsed.password or parsed.query or parsed.fragment:
        return urlunsplit((parsed.scheme, parsed.netloc.split("@")[-1], "", "", "")), False
    return raw, True


def contains_inline_credential(value: Any) -> bool:
    text = str(value or "")
    if _INLINE_CREDENTIAL.search(text) or _CREDENTIAL_ASSIGNMENT.search(text):
        return True
    for candidate in _HTTP_URL_IN_TEXT.findall(text):
        try:
            parsed = urlsplit(candidate.rstrip(").,;"))
        except ValueError:
            return True
        if parsed.username or parsed.password or parsed.query or parsed.fragment:
            return True
    return False


def renderer_safe_tool_names(value: Any) -> tuple[Optional[list[str]], bool]:
    """Project configured tool selection without returning nested/raw values."""
    if value is None:
        return None, True
    if not isinstance(value, list) or len(value) > 256:
        return [], False
    names: list[str] = []
    for item in value:
        if (
            not isinstance(item, str)
            or not item
            or len(item) > 256
            or _CONTROL_OR_DIRECTIONAL.search(item)
            or contains_inline_credential(item)
        ):
            return [], False
        names.append(item)
    return names, True


def renderer_safe_environment(value: Any) -> tuple[Dict[str, str], bool]:
    """Return environment names only, never malformed keys or any values."""
    if value is None:
        return {}, True
    if not isinstance(value, dict) or len(value) > 64:
        return {}, False
    names: Dict[str, str] = {}
    for key in value:
        if not isinstance(key, str) or not _ENVIRONMENT_NAME.fullmatch(key):
            return {}, False
        names[key] = ""
    return names, True


def renderer_safe_auth(config: Dict[str, Any]) -> tuple[Optional[str], bool]:
    raw_auth = config.get("auth")
    if raw_auth not in (None, "header", "oauth"):
        return None, False
    headers = config.get("headers")
    if headers is not None and not isinstance(headers, dict):
        return None, False
    has_authorization = isinstance(headers, dict) and any(
        isinstance(key, str) and key.lower() == "authorization" for key in headers
    )
    if has_authorization:
        if raw_auth not in {None, "header"}:
            return None, False
        return "header", True
    return raw_auth, True


def renderer_safe_server(scope: Optional[str], name: str, config: Dict[str, Any]) -> Dict[str, Any]:
    # Legacy Hermes configuration values have no provenance that proves they are
    # non-secret. A credential may be stored in a positional argument, URL path,
    # tool name, or even an environment-variable name without matching any
    # credential denylist. Consequently the Workbench projection is deliberately
    # metadata-only until a trusted broker can attest a separately typed safe
    # representation. The raw configuration remains untouched and fully usable by
    # standalone Hermes; it simply never crosses into renderer memory.
    return {
        "url": "",
        "command": "",
        "args": [],
        "tools": [],
        "env": {},
        "auth": None,
        "revision": server_revision(scope, name, config),
        "editable": False,
        "editor_block_reason": "requires-trusted-reentry",
    }


def _validate_edit(edit: Any) -> Dict[str, Any]:
    transport = str(getattr(edit, "transport", ""))
    url = str(getattr(edit, "url", "")).strip()
    command = str(getattr(edit, "command", "")).strip()
    args = [str(item) for item in getattr(edit, "args", [])]
    env_names = [str(item).strip() for item in getattr(edit, "environment_variable_names", [])]
    auth = getattr(edit, "auth", None)
    enabled = bool(getattr(edit, "enabled", True))
    if transport not in {"http", "stdio"} or auth not in {None, "header", "oauth"}:
        raise McpEditorError("validation-error")
    if len(args) > 64 or len(env_names) > 64 or len(set(env_names)) != len(env_names):
        raise McpEditorError("validation-error")
    if any(not _ENVIRONMENT_NAME.fullmatch(name) for name in env_names):
        raise McpEditorError("validation-error")
    if any(len(item) > 1024 or _CONTROL_OR_DIRECTIONAL.search(item) or contains_inline_credential(item) for item in args):
        raise McpEditorError("validation-error")
    if transport == "http":
        safe_url, safe = renderer_safe_url(url)
        if not safe or not safe_url or len(url) > 4096 or command or args or env_names:
            raise McpEditorError("validation-error")
    else:
        if not command or len(command) > 1024 or _CONTROL_OR_DIRECTIONAL.search(command) or contains_inline_credential(command):
            raise McpEditorError("validation-error")
        if url or auth is not None:
            raise McpEditorError("validation-error")
    return {
        "transport": transport,
        "url": url,
        "command": command,
        "args": args,
        "environment_variable_names": env_names,
        "auth": auth,
        "enabled": enabled,
    }


def _auth_mode(config: Dict[str, Any]) -> Optional[str]:
    auth = config.get("auth")
    if auth in {"oauth", "header"}:
        return str(auth)
    headers = config.get("headers")
    if isinstance(headers, dict) and any(str(key).lower() == "authorization" for key in headers):
        return "header"
    return None


def _risk(current: Dict[str, Any], edit: Dict[str, Any]) -> str:
    current_transport = "http" if current.get("url") else "stdio"
    if current_transport != edit["transport"]:
        return "transport"
    if edit["transport"] == "stdio" and (
        str(current.get("command") or "") != edit["command"]
        or [str(item) for item in (current.get("args") or [])] != edit["args"]
        or sorted(str(key) for key in (current.get("env") or {})) != sorted(edit["environment_variable_names"])
    ):
        return "command"
    if str(current.get("url") or "") != edit["url"] or _auth_mode(current) != edit["auth"]:
        return "source"
    return "none"


def _next_config(name: str, current: Dict[str, Any], edit: Dict[str, Any]) -> Dict[str, Any]:
    current_transport = "http" if current.get("url") else "stdio"
    current_auth = _auth_mode(current)
    if edit["transport"] != current_transport or edit["auth"] != current_auth:
        raise McpEditorError("secret-rebind-required")
    # Start from the raw, unexpanded config and change only fields represented
    # by the safe editor. Unknown and secret-bearing fields remain untouched.
    next_config = dict(current)
    if edit["transport"] == "http":
        next_config["url"] = edit["url"]
    else:
        next_config["command"] = edit["command"]
        if edit["args"]:
            next_config["args"] = list(edit["args"])
        else:
            next_config.pop("args", None)
        existing_env = current.get("env") if isinstance(current.get("env"), dict) else {}
        requested = edit["environment_variable_names"]
        if set(requested) != set(existing_env):
            raise McpEditorError("secret-rebind-required")
        if existing_env:
            next_config["env"] = dict(existing_env)
        else:
            next_config.pop("env", None)
    if edit["enabled"]:
        next_config.pop("enabled", None)
    else:
        next_config["enabled"] = False
    issues = validate_mcp_server_entry(name, next_config)
    if issues:
        raise McpEditorError("validation-error")
    return next_config


def _purge_expired(now: float) -> None:
    for handle, record in list(_review_records.items()):
        if record.expires_at <= now:
            _review_records.pop(handle, None)
            key = (record.scope, record.name)
            if _target_handles.get(key) == handle:
                _target_handles.pop(key, None)


def prepare_review(scope: Optional[str], name: str, expected_revision: str, edit: Any, current: Dict[str, Any]) -> Dict[str, Any]:
    scope_key = scope or ""
    if not expected_revision or expected_revision != server_revision(scope_key, name, current):
        raise McpEditorError("stale-revision")
    safe = renderer_safe_server(scope_key, name, current)
    if not safe["editable"]:
        raise McpEditorError("unavailable")
    validated = _validate_edit(edit)
    next_config = _next_config(name, current, validated)
    risk = _risk(current, validated)
    now = time.monotonic()
    with _lock:
        _purge_expired(now)
        key = (scope_key, name)
        previous = _target_handles.pop(key, None)
        if previous:
            _review_records.pop(previous, None)
        while len(_review_records) >= MAX_REVIEW_RECORDS:
            oldest = next(iter(_review_records))
            old = _review_records.pop(oldest)
            _target_handles.pop((old.scope, old.name), None)
        handle = f"mcp-review:{secrets.token_urlsafe(32)}"
        record = ReviewRecord(handle, scope_key, name, expected_revision, now + REVIEW_TTL_SECONDS, risk, next_config)
        _review_records[handle] = record
        _target_handles[key] = handle
    return {"contract_version": CONTRACT_VERSION, "status": "ready", "reason": "ready", "review_handle": handle, "risk": risk}


def consume_review(scope: Optional[str], name: str, handle: str, risk_confirmed: bool, current: Dict[str, Any]) -> ReviewRecord:
    if not re.fullmatch(r"mcp-review:[A-Za-z0-9_-]{32,128}", handle or ""):
        raise McpEditorError("unknown-review")
    now = time.monotonic()
    with _lock:
        _purge_expired(now)
        record = _review_records.pop(handle, None)
        if record:
            key = (record.scope, record.name)
            if _target_handles.get(key) == handle:
                _target_handles.pop(key, None)
    if not record:
        raise McpEditorError("unknown-review")
    if record.expires_at <= now:
        raise McpEditorError("expired-review")
    if record.scope != (scope or "") or record.name != name:
        raise McpEditorError("unknown-review")
    if record.risk != "none" and not risk_confirmed:
        raise McpEditorError("risk-confirmation-required")
    if record.revision != server_revision(record.scope, name, current):
        raise McpEditorError("stale-revision")
    return record


def discard_review(scope: Optional[str], name: str, handle: str) -> None:
    if not re.fullmatch(r"mcp-review:[A-Za-z0-9_-]{32,128}", handle or ""):
        return
    with _lock:
        record = _review_records.get(handle)
        if not record or record.scope != (scope or "") or record.name != name:
            return
        _review_records.pop(handle, None)
        if _target_handles.get((record.scope, record.name)) == handle:
            _target_handles.pop((record.scope, record.name), None)
