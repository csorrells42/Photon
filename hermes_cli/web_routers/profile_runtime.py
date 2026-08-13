"""Renderer-safe, revisioned Profile Runtime API.

This router deliberately does not reuse the dashboard's broad profile DTOs:
those contain native paths and credential-presence metadata.  Every response
here is projected from an allowlist and bound to a profile, correlation id,
and server revision.
"""

from __future__ import annotations

import hashlib
import inspect
import json
import secrets
import threading
import time
from collections import OrderedDict
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Callable

from fastapi import APIRouter, Query, Request
from fastapi.responses import JSONResponse


router = APIRouter()

CONTRACT = "hermes-profile-runtime/v1"
PREFIX = "/api/workbench/profile-runtime/v1"
MAX_BODY_BYTES = 128 * 1024
MAX_SOUL_CHARS = 65_536
MAX_CORRELATIONS = 4_096
MAX_PREVIEWS = 256
PREVIEW_TTL_SECONDS = 60
PROFILE_ID_CHARS = frozenset("abcdefghijklmnopqrstuvwxyz0123456789-_")
MODEL_ID_CHARS = frozenset("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_./:")
TERMINALS = (
    ("local", "Local", "Run commands on this machine."),
    ("docker", "Docker", "Run commands in the configured Docker environment."),
    ("singularity", "Singularity / Apptainer", "Run commands in an HPC container."),
    ("modal", "Modal", "Run commands in a configured Modal sandbox."),
    ("daytona", "Daytona", "Run commands in a configured Daytona workspace."),
    ("ssh", "SSH", "Run commands through the configured SSH backend."),
)
TERMINAL_IDS = {row[0] for row in TERMINALS}


class ApiFailure(Exception):
    def __init__(self, status: int, code: str, message: str) -> None:
        super().__init__(message)
        self.status = status
        self.code = code
        self.message = message


@dataclass
class PreviewBinding:
    profile_id: str
    revision: int
    mutation_json: str
    expires_at: float
    confirmation: str | None
    consumed: bool = False


@dataclass(frozen=True)
class CommittedResult:
    value: Any
    revision: int


_state_lock = threading.RLock()
_correlations: OrderedDict[str, None] = OrderedDict()
_previews: OrderedDict[str, PreviewBinding] = OrderedDict()


def _profiles_module():
    from hermes_cli import profiles

    return profiles


def _safe_profile_id(value: Any) -> str:
    if not isinstance(value, str):
        raise ApiFailure(400, "invalid-request", "Profile identity is malformed.")
    value = value.strip().lower()
    if not value or len(value) > 64 or any(ch not in PROFILE_ID_CHARS for ch in value):
        raise ApiFailure(400, "invalid-request", "Profile identity is malformed.")
    profiles = _profiles_module()
    try:
        profiles.validate_profile_name(value)
    except ValueError:
        raise ApiFailure(400, "invalid-request", "Profile identity is malformed.") from None
    return value


def _safe_correlation(value: Any) -> str:
    if not isinstance(value, str) or not (1 <= len(value) <= 128):
        raise ApiFailure(400, "invalid-request", "Correlation identity is malformed.")
    if any(not (ch.isalnum() or ch in "-_:.") for ch in value):
        raise ApiFailure(400, "invalid-request", "Correlation identity is malformed.")
    return value


def _safe_revision(value: Any) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value < 0 or value > 2_147_483_647:
        raise ApiFailure(400, "invalid-request", "Revision precondition is malformed.")
    return value


def _identity(source: dict[str, Any], *, require_revision: bool) -> tuple[str, str, int | None]:
    if source.get("contract") != CONTRACT:
        raise ApiFailure(400, "invalid-contract", "Profile Runtime contract is incompatible.")
    profile_id = _safe_profile_id(source.get("profileId"))
    correlation_id = _safe_correlation(source.get("correlationId"))
    expected = source.get("expectedRevision")
    if require_revision and expected is None:
        raise ApiFailure(409, "stale-revision", "A revision-bound write is required.")
    revision = None if expected is None else _safe_revision(expected)
    return profile_id, correlation_id, revision


def _claim_correlation(correlation_id: str) -> None:
    with _state_lock:
        if correlation_id in _correlations:
            raise ApiFailure(409, "correlation-replay", "Correlation identity was already used.")
        _correlations[correlation_id] = None
        while len(_correlations) > MAX_CORRELATIONS:
            _correlations.popitem(last=False)


def _profile_info(profile_id: str):
    for row in _profiles_module().list_profiles():
        if row.name == profile_id:
            return row
    raise ApiFailure(404, "profile-not-found", "The selected profile does not exist.")


def _read_raw_config(profile_dir: Path) -> dict[str, Any]:
    from hermes_cli.config import read_user_config_raw

    path = profile_dir / "config.yaml"
    if not path.exists():
        return {}
    try:
        value = read_user_config_raw(path)
    except Exception:
        raise ApiFailure(409, "profile-unavailable", "The profile configuration cannot be read safely.") from None
    if not isinstance(value, dict):
        raise ApiFailure(409, "profile-unavailable", "The profile configuration is malformed.")
    return value


def _read_soul(profile_dir: Path) -> str:
    path = profile_dir / "SOUL.md"
    if not path.exists():
        return ""
    try:
        text = path.read_text(encoding="utf-8")
    except (OSError, UnicodeError):
        raise ApiFailure(409, "profile-unavailable", "The SOUL document cannot be read safely.") from None
    if len(text) > MAX_SOUL_CHARS:
        raise ApiFailure(409, "profile-unavailable", "The SOUL document exceeds the renderer-safe limit.")
    return text


def _model_identity(config: dict[str, Any]) -> tuple[str | None, str | None, str | None]:
    raw = config.get("model")
    provider: str | None = None
    model: str | None = None
    if isinstance(raw, str):
        model = raw.strip() or None
    elif isinstance(raw, dict):
        p = raw.get("provider")
        m = raw.get("default") or raw.get("model")
        provider = p.strip() if isinstance(p, str) and p.strip() else None
        model = m.strip() if isinstance(m, str) and m.strip() else None
    identity = f"{provider}::{model}" if provider and model else model
    if identity is not None and len(identity) > 256:
        identity = None
    return provider, model, identity


def _terminal_rows(config: dict[str, Any]) -> list[dict[str, Any]]:
    terminal = config.get("terminal")
    terminal = terminal if isinstance(terminal, dict) else {}
    selected = terminal.get("backend")
    selected = selected.strip().lower() if isinstance(selected, str) else "local"
    if selected not in TERMINAL_IDS:
        selected = "local"
    return [
        {
            "id": backend_id,
            "label": label,
            "description": description,
            "selected": backend_id == selected,
            "availability": "ready" if backend_id == "local" else ("partial" if backend_id == selected else "unavailable"),
            "detail": "Ready." if backend_id == "local" else ("Configured; readiness is verified when used." if backend_id == selected else "Not selected for this profile."),
        }
        for backend_id, label, description in TERMINALS
    ]


def _safe_snapshot(profile_id: str) -> dict[str, Any]:
    profiles = _profiles_module()
    rows = profiles.list_profiles()
    info = next((row for row in rows if row.name == profile_id), None)
    if info is None:
        raise ApiFailure(404, "profile-not-found", "The selected profile does not exist.")
    try:
        active = profiles.get_active_profile() or "default"
    except Exception:
        active = "default"
    config = _read_raw_config(info.path)
    _provider, _model, model_id = _model_identity(config)
    snapshot: dict[str, Any] = {
        "contract": CONTRACT,
        "profileId": profile_id,
        "revision": 0,
        "availability": "ready",
        "detail": "Renderer-safe profile settings are ready. Active changes apply to future sessions.",
        "profiles": [
            {
                "id": row.name,
                "name": row.name,
                "description": (row.description or "")[:512],
                "isActive": row.name == active,
                "isDeleteProtected": bool(row.is_default),
            }
            for row in rows
        ],
        "documents": [{"kind": "soul", "text": _read_soul(info.path), "maximumCharacters": MAX_SOUL_CHARS}],
        "intent": {"modelId": model_id, "projectPath": None, "worktreePath": None, "note": ""},
        "terminalBackends": _terminal_rows(config),
        "computerUse": {"availability": "unavailable", "detail": "Permission changes are managed outside this surface.", "permissions": []},
        "providers": [],
        "configurationSchema": [],
        "configuration": {},
    }
    canonical = json.dumps(snapshot, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
    digest = hashlib.sha256(canonical).hexdigest()
    # Keep the content-derived token inside the renderer contract's signed
    # 31-bit revision bound while preserving restart stability.
    revision = (int(digest[:8], 16) & 0x7FFF_FFFF) or 1
    snapshot["revision"] = revision
    return snapshot


def _require_current(profile_id: str, expected: int) -> dict[str, Any]:
    snapshot = _safe_snapshot(profile_id)
    if snapshot["revision"] != expected:
        raise ApiFailure(409, "stale-revision", "The profile changed after it was loaded.")
    return snapshot


def _envelope(profile_id: str, correlation_id: str, expected: int | None, revision: int, value: Any) -> dict[str, Any]:
    result = {"contract": CONTRACT, "profileId": profile_id, "correlationId": correlation_id, "revision": revision, "value": value}
    if expected is not None:
        result["expectedRevision"] = expected
    return result


def _error(profile_id: Any, correlation_id: Any, expected: Any, failure: ApiFailure) -> JSONResponse:
    body: dict[str, Any] = {
        "contract": CONTRACT,
        "profileId": profile_id if isinstance(profile_id, str) else "unknown",
        "correlationId": correlation_id if isinstance(correlation_id, str) else "unknown",
        "code": failure.code,
        "message": failure.message[:256],
    }
    if expected is not None:
        body["expectedRevision"] = expected
    return JSONResponse(body, status_code=failure.status)


async def _json_body(request: Request, allowed: set[str]) -> dict[str, Any]:
    length = request.headers.get("content-length")
    if length:
        try:
            if int(length) > MAX_BODY_BYTES:
                raise ApiFailure(413, "request-too-large", "The request exceeds the bounded limit.")
        except ValueError:
            raise ApiFailure(400, "invalid-request", "Content length is malformed.") from None
    raw = await request.body()
    if len(raw) > MAX_BODY_BYTES:
        raise ApiFailure(413, "request-too-large", "The request exceeds the bounded limit.")
    try:
        value = json.loads(raw)
    except (UnicodeError, json.JSONDecodeError):
        raise ApiFailure(400, "invalid-request", "The request body is malformed JSON.") from None
    if not isinstance(value, dict) or set(value) - allowed:
        raise ApiFailure(400, "invalid-request", "The request contains unsupported fields.")
    return value


def _mutation(source: Any) -> dict[str, Any]:
    if not isinstance(source, dict) or not isinstance(source.get("kind"), str):
        raise ApiFailure(400, "invalid-request", "Profile mutation is malformed.")
    kind = source["kind"]
    allowed = {"create": {"kind", "name"}, "clone": {"kind", "sourceProfileId", "name"}, "rename": {"kind", "name"}, "delete": {"kind"}}
    if kind not in allowed or set(source) != allowed[kind]:
        raise ApiFailure(400, "invalid-request", "Profile mutation is malformed.")
    result = {"kind": kind}
    if "name" in source:
        result["name"] = _safe_profile_id(source["name"])
    if "sourceProfileId" in source:
        result["sourceProfileId"] = _safe_profile_id(source["sourceProfileId"])
    return result


def _preview_text(profile_id: str, mutation: dict[str, Any]) -> tuple[str, str, list[str], bool, str | None]:
    kind = mutation["kind"]
    if kind == "create":
        return "Create profile", f"Create fresh profile {mutation['name']}.", ["A new isolated profile is added."], False, None
    if kind == "clone":
        return "Clone safe profile identity", f"Create {mutation['name']} from renderer-safe identity material.", ["SOUL, description, and model assignment are copied.", "Credentials, sessions, raw configuration, archives, and paths are not copied."], False, None
    if kind == "rename":
        return "Rename profile", f"Rename {profile_id} to {mutation['name']}.", ["Future references use the new profile identity."], False, None
    return "Delete profile", f"Permanently delete {profile_id}.", ["Profile state is removed and cannot be recovered from this surface."], True, f"delete {profile_id}"


def _write_soul(profile_dir: Path, text: str) -> None:
    from utils import atomic_write_text

    atomic_write_text(profile_dir / "SOUL.md", text, preserve_mode=True, create_mode=0o644)


def _write_model(profile_dir: Path, provider: str, model: str) -> None:
    from hermes_cli.config import atomic_config_write

    config = _read_raw_config(profile_dir)
    model_config = config.get("model")
    model_config = dict(model_config) if isinstance(model_config, dict) else {}
    model_config["provider"] = provider
    model_config["default"] = model
    config["model"] = model_config
    atomic_config_write(profile_dir / "config.yaml", config, sort_keys=False)


def _write_terminal(profile_dir: Path, backend: str) -> None:
    from hermes_cli.config import atomic_config_write

    config = _read_raw_config(profile_dir)
    terminal = config.get("terminal")
    terminal = dict(terminal) if isinstance(terminal, dict) else {}
    terminal["backend"] = backend
    config["terminal"] = terminal
    atomic_config_write(profile_dir / "config.yaml", config, sort_keys=False)


async def _bound_post(request: Request, allowed: set[str], operation: Callable[[dict[str, Any], str, str, int], Any]) -> Any:
    source: dict[str, Any] = {}
    try:
        source = await _json_body(request, allowed)
        profile_id, correlation_id, expected = _identity(source, require_revision=True)
        assert expected is not None
        _claim_correlation(correlation_id)
        with _state_lock:
            _require_current(profile_id, expected)
        value = operation(source, profile_id, correlation_id, expected)
        if inspect.isawaitable(value):
            value = await value
        if isinstance(value, CommittedResult):
            revision = value.revision
            value = value.value
        else:
            revision = _safe_snapshot(profile_id)["revision"] if any(row.name == profile_id for row in _profiles_module().list_profiles()) else expected + 1
        return _envelope(profile_id, correlation_id, expected, revision, value)
    except ApiFailure as exc:
        return _error(source.get("profileId"), source.get("correlationId"), source.get("expectedRevision"), exc)
    except Exception:
        return _error(source.get("profileId"), source.get("correlationId"), source.get("expectedRevision"), ApiFailure(500, "operation-failed", "The profile operation failed safely."))


@router.get(f"{PREFIX}/snapshot")
async def profile_runtime_snapshot(
    contract: str = Query(...), profileId: str = Query(...), correlationId: str = Query(...), expectedRevision: int | None = Query(None)
):
    source = {"contract": contract, "profileId": profileId, "correlationId": correlationId}
    if expectedRevision is not None:
        source["expectedRevision"] = expectedRevision
    try:
        profile_id, correlation_id, expected = _identity(source, require_revision=False)
        snapshot = _safe_snapshot(profile_id)
        if expected is not None and snapshot["revision"] != expected:
            raise ApiFailure(409, "stale-revision", "The profile changed after it was loaded.")
        return _envelope(profile_id, correlation_id, expected, snapshot["revision"], snapshot)
    except ApiFailure as exc:
        return _error(profileId, correlationId, expectedRevision, exc)


@router.post(f"{PREFIX}/active-profile")
async def profile_runtime_active(request: Request):
    async def apply(source: dict[str, Any], profile_id: str, _correlation: str, _expected: int):
        target = _safe_profile_id(source.get("targetProfileId"))
        _profile_info(target)
        from hermes_cli.workbench_credentials import revoke_all_sessions

        await revoke_all_sessions()
        _profiles_module().set_active_profile(target)
        return CommittedResult({"activeProfileId": target}, _safe_snapshot(profile_id)["revision"])

    return await _bound_post(request, {"contract", "profileId", "correlationId", "expectedRevision", "targetProfileId"}, apply)


@router.post(f"{PREFIX}/profile-mutation/preview")
async def profile_runtime_preview(request: Request):
    def apply(source: dict[str, Any], profile_id: str, _correlation: str, expected: int):
        mutation = _mutation(source.get("mutation"))
        if mutation["kind"] in {"create", "clone"}:
            try:
                _profile_info(mutation["name"])
            except ApiFailure as exc:
                if exc.code != "profile-not-found":
                    raise
            else:
                raise ApiFailure(409, "profile-exists", "The target profile already exists.")
        if mutation["kind"] == "clone":
            _profile_info(mutation["sourceProfileId"])
        if mutation["kind"] in {"rename", "delete"} and profile_id == "default":
            raise ApiFailure(409, "protected-profile", "The default profile cannot be renamed or deleted.")
        title, summary, consequences, destructive, phrase = _preview_text(profile_id, mutation)
        preview_id = secrets.token_urlsafe(24)
        expires_at = time.time() + PREVIEW_TTL_SECONDS
        binding = PreviewBinding(profile_id, expected, json.dumps(mutation, sort_keys=True, separators=(",", ":")), expires_at, phrase)
        with _state_lock:
            _previews[preview_id] = binding
            while len(_previews) > MAX_PREVIEWS:
                _previews.popitem(last=False)
        return {
            "previewId": preview_id, "operation": mutation["kind"], "profileId": profile_id,
            "title": title, "summary": summary, "consequences": consequences,
            "destructive": destructive, "confirmationPhrase": phrase,
            "expiresAt": datetime.fromtimestamp(expires_at, timezone.utc).isoformat().replace("+00:00", "Z"),
        }

    return await _bound_post(request, {"contract", "profileId", "correlationId", "expectedRevision", "mutation"}, apply)


@router.post(f"{PREFIX}/profile-mutation/commit")
async def profile_runtime_commit(request: Request):
    async def apply(source: dict[str, Any], profile_id: str, _correlation: str, expected: int):
        mutation = _mutation(source.get("mutation"))
        preview_id = source.get("previewId")
        if not isinstance(preview_id, str) or len(preview_id) > 256:
            raise ApiFailure(400, "invalid-request", "Preview identity is malformed.")
        with _state_lock:
            binding = _previews.get(preview_id)
            if binding is None:
                raise ApiFailure(409, "preview-mismatch", "The preview is unknown.")
            if binding.consumed:
                raise ApiFailure(409, "preview-replay", "The preview was already consumed.")
            if binding.expires_at <= time.time():
                raise ApiFailure(409, "preview-expired", "The preview expired.")
            mutation_json = json.dumps(mutation, sort_keys=True, separators=(",", ":"))
            if binding.profile_id != profile_id or binding.revision != expected or binding.mutation_json != mutation_json:
                raise ApiFailure(409, "preview-mismatch", "The preview does not match this operation.")
            if binding.confirmation is not None:
                confirmation = source.get("destructiveConfirmation")
                if not isinstance(confirmation, dict) or set(confirmation) != {"phrase"} or confirmation.get("phrase") != binding.confirmation:
                    raise ApiFailure(409, "confirmation-mismatch", "The destructive confirmation phrase does not match.")
            binding.consumed = True
        profiles = _profiles_module()
        active = profiles.get_active_profile() or "default"
        kind = mutation["kind"]
        affected = profile_id
        if kind == "create":
            affected = mutation["name"]
            directory = profiles.create_profile(affected, no_alias=True)
            profiles.seed_profile_skills(directory, quiet=True)
        elif kind == "clone":
            affected = mutation["name"]
            source_info = _profile_info(mutation["sourceProfileId"])
            source_config = _read_raw_config(source_info.path)
            provider, model, _identity_value = _model_identity(source_config)
            directory = profiles.create_profile(affected, no_alias=True)
            profiles.seed_profile_skills(directory, quiet=True)
            _write_soul(directory, _read_soul(source_info.path))
            if source_info.description:
                profiles.write_profile_meta(directory, description=source_info.description[:512], description_auto=False)
            if provider and model:
                _write_model(directory, provider, model)
        elif kind == "rename":
            affected = mutation["name"]
            from hermes_cli.workbench_credentials import revoke_profile_session

            await revoke_profile_session(profile_id)
            profiles.rename_profile(profile_id, affected)
            if active == profile_id:
                active = affected
        else:
            from hermes_cli.workbench_credentials import revoke_profile_session

            await revoke_profile_session(profile_id)
            profiles.delete_profile(profile_id, yes=True)
            if active == profile_id:
                profiles.set_active_profile("default")
                active = "default"
        target_revision = _safe_snapshot(affected)["revision"] if kind != "delete" else expected + 1
        return CommittedResult({"affectedProfileId": affected, "activeProfileId": active}, target_revision)

    return await _bound_post(request, {"contract", "profileId", "correlationId", "expectedRevision", "mutation", "previewId", "destructiveConfirmation"}, apply)


@router.post(f"{PREFIX}/documents/soul")
async def profile_runtime_soul(request: Request):
    def apply(source: dict[str, Any], profile_id: str, _correlation: str, _expected: int):
        if source.get("document") != "soul" or not isinstance(source.get("text"), str) or len(source["text"]) > MAX_SOUL_CHARS:
            raise ApiFailure(400, "invalid-request", "The SOUL document is malformed or too large.")
        _write_soul(_profile_info(profile_id).path, source["text"])
        snapshot = _safe_snapshot(profile_id)
        return CommittedResult(snapshot, snapshot["revision"])

    return await _bound_post(request, {"contract", "profileId", "correlationId", "expectedRevision", "document", "text"}, apply)


@router.post(f"{PREFIX}/intent/model")
async def profile_runtime_model(request: Request):
    def apply(source: dict[str, Any], profile_id: str, _correlation: str, _expected: int):
        provider = source.get("provider")
        model = source.get("model")
        if not isinstance(provider, str) or not isinstance(model, str) or not provider.strip() or not model.strip() or len(provider) > 64 or len(model) > 192:
            raise ApiFailure(400, "invalid-request", "The model assignment is malformed.")
        if any(ch not in MODEL_ID_CHARS for ch in provider + model):
            raise ApiFailure(400, "invalid-request", "The model assignment is malformed.")
        _write_model(_profile_info(profile_id).path, provider.strip(), model.strip())
        snapshot = _safe_snapshot(profile_id)
        return CommittedResult(snapshot, snapshot["revision"])

    return await _bound_post(request, {"contract", "profileId", "correlationId", "expectedRevision", "provider", "model"}, apply)


@router.post(f"{PREFIX}/terminal")
async def profile_runtime_terminal(request: Request):
    def apply(source: dict[str, Any], profile_id: str, _correlation: str, _expected: int):
        backend = source.get("backendId")
        if backend not in TERMINAL_IDS:
            raise ApiFailure(400, "invalid-request", "The terminal backend is unsupported.")
        _write_terminal(_profile_info(profile_id).path, backend)
        snapshot = _safe_snapshot(profile_id)
        return CommittedResult(snapshot, snapshot["revision"])

    return await _bound_post(request, {"contract", "profileId", "correlationId", "expectedRevision", "backendId"}, apply)
