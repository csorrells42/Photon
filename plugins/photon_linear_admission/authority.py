"""Fail-closed admission authority for Photon work sourced from Linear.

This module deliberately separates three things that are easy to conflate:

* Linear issue text is context, never authority.
* An Architect receipt is an out-of-band, revision-bound approval.
* Chris approves the exact tool call through Hermes' existing visible approval
  surface, or explicitly approves the direct-override tool call.

The plugin process owns active admissions. They are intentionally not restored
after restart. Architect receipts carry the current boot nonce, expire quickly,
and are consumed once. The JSONL audit is evidence only and is never read back
as authority.
"""

from __future__ import annotations

from contextlib import contextmanager
from dataclasses import asdict, dataclass
from hashlib import sha256
import hmac
import json
import os
from pathlib import Path
import secrets
import threading
import time
from typing import Any, Callable, Mapping, Optional

from hermes_constants import get_hermes_home

try:
    import fcntl
except ImportError:  # pragma: no cover - Photon runs this authority in Linux.
    fcntl = None


MAX_TEXT = 8_192
MAX_RECORD = 32_768
APPROVAL_TTL_SECONDS = 15 * 60
ADMISSION_TTL_SECONDS = 60 * 60
MAX_ACTIVE_ADMISSIONS = 64


class AdmissionError(RuntimeError):
    """A safe, operator-actionable admission failure."""


@dataclass(frozen=True)
class IssueSnapshot:
    identifier: str
    revision: str
    title: str
    status: str
    url: str


@dataclass(frozen=True)
class ArchitectApproval:
    issue_id: str
    issue_revision: str
    generation: str
    issued_at: int
    expires_at: int
    nonce: str
    boot_id: str
    signature: str


@dataclass
class ActiveAdmission:
    admission_id: str
    issue_id: str
    issue_revision: str
    generation: str
    session_id: str
    authority: str
    admitted_at: int
    expires_at: int
    progress_count: int = 0


def _runtime_instance_id() -> str:
    """Return one opaque identity shared by processes in this Photon runtime."""
    explicit = os.environ.get("PHOTON_RUNTIME_INSTANCE_ID", "").strip()
    if explicit:
        return sha256(explicit.encode("utf-8")).hexdigest()

    material: list[str] = []
    for path in (Path("/etc/hostname"), Path("/proc/1/stat")):
        try:
            material.append(path.read_text(encoding="utf-8")[:8_192])
        except OSError:
            continue
    if not material:
        # Non-Linux callers can opt into cross-process sharing with the
        # environment variable above. This fallback remains process-scoped.
        material.append(f"{os.getpid()}:{time.monotonic_ns()}")
    return sha256("\n".join(material).encode("utf-8")).hexdigest()


@contextmanager
def _exclusive_file_lock(path: Path):
    descriptor = os.open(path, os.O_CREAT | os.O_RDWR, 0o600)
    with os.fdopen(descriptor, "a+b") as handle:
        if fcntl is not None:
            fcntl.flock(handle.fileno(), fcntl.LOCK_EX)
        try:
            yield
        finally:
            if fcntl is not None:
                fcntl.flock(handle.fileno(), fcntl.LOCK_UN)


def _bounded_text(value: Any, maximum: int = MAX_TEXT) -> str:
    text = str(value or "").strip()
    return text if len(text) <= maximum else text[:maximum]


def _safe_issue_id(value: Any) -> str:
    text = _bounded_text(value, 64).upper()
    if not text or len(text) > 64:
        raise AdmissionError("A bounded Linear issue identifier is required.")
    if not all(ch.isalnum() or ch in "-_" for ch in text):
        raise AdmissionError("The Linear issue identifier is invalid.")
    return text


def _safe_token(value: Any, *, label: str, maximum: int = 160) -> str:
    text = _bounded_text(value, maximum)
    if not text or len(text) > maximum:
        raise AdmissionError(f"A bounded {label} is required.")
    if any(ord(ch) < 0x20 or ord(ch) == 0x7F for ch in text):
        raise AdmissionError(f"The {label} is invalid.")
    return text


def generation_for_session(session_id: str) -> str:
    session = _safe_token(session_id, label="Photon session", maximum=512)
    return "photon-generation:v1:" + sha256(session.encode("utf-8")).hexdigest()


def issue_snapshot_from_payload(payload: Mapping[str, Any]) -> IssueSnapshot:
    identifier = _safe_issue_id(payload.get("identifier") or payload.get("id"))
    updated_at = _safe_token(
        payload.get("updatedAt") or payload.get("updated_at"),
        label="Linear issue update revision",
        maximum=96,
    )
    material = {
        "identifier": identifier,
        "updatedAt": updated_at,
        "title": _bounded_text(payload.get("title"), 1_024),
        "description": _bounded_text(payload.get("description"), MAX_RECORD),
        "priority": payload.get("priority"),
        "status": payload.get("status"),
        "labels": payload.get("labels"),
        "project": payload.get("project"),
    }
    canonical = json.dumps(material, sort_keys=True, separators=(",", ":"), ensure_ascii=False)
    digest = sha256(canonical.encode("utf-8")).hexdigest()
    return IssueSnapshot(
        identifier=identifier,
        revision=f"linear:v1:{updated_at}:{digest}",
        title=material["title"],
        status=_bounded_text(payload.get("status"), 128),
        url=_bounded_text(payload.get("url"), 2_048),
    )


def parse_linear_tool_payload(raw: Any) -> Mapping[str, Any]:
    candidate: Any = raw
    for _ in range(4):
        if not isinstance(candidate, Mapping):
            text = _bounded_text(candidate, 256 * 1024)
            try:
                candidate = json.loads(text)
            except (TypeError, ValueError) as exc:
                raise AdmissionError("Linear returned a malformed issue snapshot.") from exc
            continue

        if candidate.get("success") is False or candidate.get("error"):
            raise AdmissionError("Linear issue inspection failed.")

        result = candidate.get("result")
        if isinstance(result, (Mapping, str)):
            candidate = result
            continue

        structured = candidate.get("structuredContent")
        if isinstance(structured, Mapping):
            candidate = structured
            continue

        return candidate

    if not isinstance(candidate, Mapping):
        raise AdmissionError("Linear returned a malformed issue snapshot.")
    if candidate.get("success") is False or candidate.get("error"):
        raise AdmissionError("Linear issue inspection failed.")
    return candidate


class PhotonLinearAuthority:
    """Thread-safe, process-owned admission authority."""

    def __init__(
        self,
        issue_loader: Callable[[str], IssueSnapshot],
        comment_writer: Callable[[str, str], None],
        *,
        state_root: Optional[Path] = None,
        clock: Callable[[], float] = time.time,
        initialize_boot: bool = True,
        shared_runtime_boot: bool = False,
    ) -> None:
        self._issue_loader = issue_loader
        self._comment_writer = comment_writer
        self._clock = clock
        self._root = Path(state_root or (get_hermes_home() / "photon-linear-admission"))
        self._root.mkdir(parents=True, exist_ok=True)
        self._boot_path = self._root / "boot.json"
        self._key_path = self._root / "authority.key"
        self._approval_path = self._root / "architect-approvals.json"
        self._audit_path = self._root / "audit.jsonl"
        self._maintenance_path = self._root / "maintenance.jsonl"
        self._lock = threading.RLock()
        self._active: dict[str, ActiveAdmission] = {}
        self._pending: dict[str, tuple[str, str, str]] = {}
        self._direct_challenges: dict[tuple[str, str, str], tuple[str, int]] = {}
        self._key = self._load_or_create_key()
        if shared_runtime_boot:
            self._boot_id = self._load_or_create_runtime_boot()
        elif initialize_boot:
            self._boot_id = secrets.token_hex(24)
            self._atomic_json(self._boot_path, {"bootId": self._boot_id, "startedAt": int(self._clock())})
            self._atomic_json(self._approval_path, {"approvals": []})
        else:
            self._boot_id = self._read_boot_id()

    @property
    def boot_id(self) -> str:
        return self._boot_id

    def inspect(self, issue_id: str, session_id: str) -> dict[str, Any]:
        issue = self._issue_loader(_safe_issue_id(issue_id))
        generation = generation_for_session(session_id)
        with self._lock:
            active = [item for item in self._active.values() if item.session_id == session_id]
            if active and (active[0].issue_id != issue.identifier or active[0].issue_revision != issue.revision):
                raise AdmissionError("Finish or cancel the current admitted issue before inspecting another issue.")
            self._pending[session_id] = (issue.identifier, issue.revision, generation)
        return {
            "issueId": issue.identifier,
            "issueRevision": issue.revision,
            "generation": generation,
            "title": issue.title,
            "status": issue.status,
            "url": issue.url,
            "authority": "context-only",
            "executionAuthorized": False,
        }

    def prepare_dual(self, issue_id: str, issue_revision: str, session_id: str) -> tuple[IssueSnapshot, str]:
        issue = self._current_exact(issue_id, issue_revision)
        generation = generation_for_session(session_id)
        with self._lock:
            self._require_pending(issue, session_id, generation)
            approval = self._matching_approval(issue.identifier, issue.revision, generation)
            if approval is None:
                raise AdmissionError(
                    "Architect approval is missing, expired, consumed, or bound to another issue revision/generation."
                )
        return issue, approval.nonce

    def admit_dual(self, issue_id: str, issue_revision: str, session_id: str) -> dict[str, Any]:
        issue = self._current_exact(issue_id, issue_revision)
        generation = generation_for_session(session_id)
        with self._lock:
            approval = self._matching_approval(issue.identifier, issue.revision, generation)
            if approval is None:
                raise AdmissionError("Architect approval is no longer valid.")
            self._consume_approval(approval)
            return self._new_admission(issue, session_id, generation, "chris+architect")

    def prepare_direct(self, issue_id: str, issue_revision: str, session_id: str) -> tuple[IssueSnapshot, str]:
        if not session_id:
            raise AdmissionError("A visible Photon conversation session is required.")
        issue = self._current_exact(issue_id, issue_revision)
        generation = generation_for_session(session_id)
        challenge = secrets.token_hex(16)
        with self._lock:
            self._require_pending(issue, session_id, generation)
            self._direct_challenges[(session_id, issue.identifier, issue.revision)] = (
                challenge,
                int(self._clock()) + 120,
            )
        return issue, challenge

    def admit_direct(self, issue_id: str, issue_revision: str, session_id: str) -> dict[str, Any]:
        if not session_id:
            raise AdmissionError("A visible Photon conversation session is required.")
        issue = self._current_exact(issue_id, issue_revision)
        key = (session_id, issue.identifier, issue.revision)
        now = int(self._clock())
        with self._lock:
            challenge = self._direct_challenges.pop(key, None)
            if challenge is None or challenge[1] < now:
                raise AdmissionError("The visible Chris override challenge is missing, expired, or consumed.")
        generation = generation_for_session(session_id)
        with self._lock:
            return self._new_admission(issue, session_id, generation, "chris-direct-override")

    def status(self, issue_id: str, issue_revision: str, session_id: str) -> dict[str, Any]:
        issue = self._current_exact(issue_id, issue_revision)
        generation = generation_for_session(session_id)
        now = int(self._clock())
        with self._lock:
            self._expire_active(now)
            approval = self._matching_approval(issue.identifier, issue.revision, generation)
            active = [
                admission
                for admission in self._active.values()
                if admission.issue_id == issue.identifier
                and admission.issue_revision == issue.revision
                and admission.session_id == session_id
            ]
        return {
            "issueId": issue.identifier,
            "issueRevision": issue.revision,
            "generation": generation,
            "architectApproved": approval is not None,
            "activeAdmission": asdict(active[0]) if active else None,
            "pendingAdmission": self._pending.get(session_id) == (
                issue.identifier,
                issue.revision,
                generation,
            ),
        }

    def enforce_session_tool(self, session_id: str) -> None:
        """Fail closed unless this session has an exact active admission.

        The hook calls this immediately before every non-admission tool. A
        current admission is revalidated against Linear on every call, so a
        material issue edit retires execution authority before work continues.
        """
        session = _safe_token(session_id, label="Photon session", maximum=512)
        now = int(self._clock())
        with self._lock:
            self._expire_active(now)
            active = next((item for item in self._active.values() if item.session_id == session), None)
        if active is None:
            raise AdmissionError(
                "Photon has no active exact-revision admission; engineering tools are blocked."
            )
        try:
            self._current_exact(active.issue_id, active.issue_revision)
        except AdmissionError:
            with self._lock:
                self._audit("revision-invalidated", active, {})
                self._active.pop(active.admission_id, None)
            raise

    def post_progress(self, admission_id: str, issue_id: str, text: str, session_id: str) -> dict[str, Any]:
        message = _bounded_text(text, 4_096)
        if not message:
            raise AdmissionError("A bounded progress message is required.")
        admission = self._require_active(admission_id, issue_id, session_id)
        if admission.progress_count >= 64:
            raise AdmissionError("The admission progress limit has been reached.")
        self._current_exact(admission.issue_id, admission.issue_revision)
        self._comment_writer(admission.issue_id, message)
        with self._lock:
            admission.progress_count += 1
            self._audit("progress", admission, {"textSha256": sha256(message.encode()).hexdigest()})
        return {"accepted": True, "issueId": admission.issue_id, "progressCount": admission.progress_count}

    def complete(self, admission_id: str, issue_id: str, record: Mapping[str, Any], session_id: str) -> dict[str, Any]:
        admission = self._require_active(admission_id, issue_id, session_id)
        self._current_exact(admission.issue_id, admission.issue_revision)
        safe = self._normalize_maintenance(record, admission)
        evidence = (
            "Photon implementation evidence recorded for review.\n\n"
            f"Purpose: {safe['purpose']}\n"
            f"Components: {', '.join(safe['components']) or 'none'}\n"
            f"Tests: {', '.join(safe['tests']) or 'none'}\n"
            f"Known limitations: {', '.join(safe['limitations']) or 'none'}\n\n"
            "Photon did not close this issue; Chris retains Done authority."
        )
        self._comment_writer(admission.issue_id, evidence)
        with self._lock:
            self._append_jsonl(self._maintenance_path, safe)
            self._audit("completed-for-review", admission, {"recordSha256": self._digest(safe)})
            self._active.pop(admission.admission_id, None)
        return {"accepted": True, "issueId": admission.issue_id, "state": "ready-for-chris-review"}

    def cancel(self, admission_id: str, session_id: str) -> dict[str, Any]:
        token = _safe_token(admission_id, label="admission identifier", maximum=128)
        with self._lock:
            admission = self._active.get(token)
            if admission is None or admission.session_id != session_id:
                raise AdmissionError("The admission is stale or foreign.")
            self._audit("cancelled", admission, {})
            self._active.pop(token, None)
        return {"accepted": True, "issueId": admission.issue_id, "state": "cancelled"}

    def revoke_issue(self, issue_id: str) -> int:
        target = _safe_issue_id(issue_id)
        with self._lock:
            doomed = [key for key, value in self._active.items() if value.issue_id == target]
            for key in doomed:
                admission = self._active.pop(key)
                self._audit("revoked", admission, {})
            return len(doomed)

    def write_architect_approval(
        self,
        issue_id: str,
        issue_revision: str,
        generation: str,
        *,
        ttl_seconds: int = APPROVAL_TTL_SECONDS,
    ) -> ArchitectApproval:
        issue = _safe_issue_id(issue_id)
        revision = _safe_token(issue_revision, label="issue revision")
        bound_generation = _safe_token(generation, label="Photon generation")
        ttl = max(30, min(APPROVAL_TTL_SECONDS, int(ttl_seconds)))
        now = int(self._clock())
        unsigned = {
            "issue_id": issue,
            "issue_revision": revision,
            "generation": bound_generation,
            "issued_at": now,
            "expires_at": now + ttl,
            "nonce": secrets.token_hex(16),
            "boot_id": self._boot_id,
        }
        approval = ArchitectApproval(**unsigned, signature=self._sign(unsigned))
        with self._lock:
            approvals = [item for item in self._read_approvals() if item.expires_at >= now]
            approvals = [item for item in approvals if not (
                item.issue_id == issue and item.issue_revision == revision and item.generation == bound_generation
            )]
            approvals.append(approval)
            if len(approvals) > 128:
                raise AdmissionError("Architect approval capacity is full; no approval was written.")
            self._write_approvals(approvals)
            self._append_jsonl(self._audit_path, {
                "event": "architect-approved",
                "issueId": issue,
                "issueRevision": revision,
                "generation": bound_generation,
                "at": now,
                "expiresAt": approval.expires_at,
            })
        return approval

    def _current_exact(self, issue_id: str, issue_revision: str) -> IssueSnapshot:
        target = _safe_issue_id(issue_id)
        expected = _safe_token(issue_revision, label="issue revision")
        current = self._issue_loader(target)
        if current.identifier != target or current.revision != expected:
            raise AdmissionError("The Linear issue changed; obtain fresh inspection and approvals.")
        return current

    def _new_admission(self, issue: IssueSnapshot, session_id: str, generation: str, authority: str) -> dict[str, Any]:
        now = int(self._clock())
        self._expire_active(now)
        if len(self._active) >= MAX_ACTIVE_ADMISSIONS:
            raise AdmissionError("Admission capacity is full; no work was authorized.")
        if any(item.session_id == session_id for item in self._active.values()):
            raise AdmissionError("This Photon conversation already has an active Linear admission.")
        admission = ActiveAdmission(
            admission_id="pla:v1:" + secrets.token_hex(24),
            issue_id=issue.identifier,
            issue_revision=issue.revision,
            generation=generation,
            session_id=session_id,
            authority=authority,
            admitted_at=now,
            expires_at=now + ADMISSION_TTL_SECONDS,
        )
        self._active[admission.admission_id] = admission
        self._pending[session_id] = (issue.identifier, issue.revision, generation)
        self._audit("admitted", admission, {})
        return {
            "admissionId": admission.admission_id,
            "issueId": admission.issue_id,
            "issueRevision": admission.issue_revision,
            "generation": admission.generation,
            "authority": admission.authority,
            "expiresAt": admission.expires_at,
            "instruction": "Work only this exact revision; report through photon_linear_progress; never close the issue.",
        }

    def _require_active(self, admission_id: str, issue_id: str, session_id: str) -> ActiveAdmission:
        token = _safe_token(admission_id, label="admission identifier", maximum=128)
        target = _safe_issue_id(issue_id)
        now = int(self._clock())
        with self._lock:
            self._expire_active(now)
            admission = self._active.get(token)
            if admission is None:
                raise AdmissionError("The admission is stale, consumed, cancelled, or unknown.")
            if admission.issue_id != target or admission.session_id != session_id:
                raise AdmissionError("The admission is bound to another issue or conversation.")
            if admission.generation != generation_for_session(session_id):
                raise AdmissionError("The Photon conversation generation changed.")
            return admission

    def _matching_approval(self, issue_id: str, revision: str, generation: str) -> Optional[ArchitectApproval]:
        now = int(self._clock())
        for approval in self._read_approvals():
            if approval.expires_at < now or approval.boot_id != self._boot_id:
                continue
            if not hmac.compare_digest(approval.signature, self._sign(self._unsigned(approval))):
                continue
            if (
                approval.issue_id == issue_id
                and approval.issue_revision == revision
                and approval.generation == generation
            ):
                return approval
        return None

    def _require_pending(self, issue: IssueSnapshot, session_id: str, generation: str) -> None:
        if self._pending.get(session_id) != (issue.identifier, issue.revision, generation):
            raise AdmissionError("Inspect this exact issue revision in the current Photon conversation first.")

    def _consume_approval(self, selected: ArchitectApproval) -> None:
        approvals = [item for item in self._read_approvals() if item.nonce != selected.nonce]
        self._write_approvals(approvals)
        self._append_jsonl(self._audit_path, {
            "event": "architect-approval-consumed",
            "issueId": selected.issue_id,
            "issueRevision": selected.issue_revision,
            "generation": selected.generation,
            "at": int(self._clock()),
        })

    def _normalize_maintenance(self, record: Mapping[str, Any], admission: ActiveAdmission) -> dict[str, Any]:
        if not isinstance(record, Mapping):
            raise AdmissionError("A structured maintenance record is required.")

        def strings(key: str, maximum_items: int = 32, maximum_text: int = 512) -> list[str]:
            raw = record.get(key)
            if not isinstance(raw, list) or len(raw) > maximum_items:
                raise AdmissionError(f"Maintenance field '{key}' must be a bounded list.")
            result = [_bounded_text(item, maximum_text) for item in raw]
            if any(not item for item in result):
                raise AdmissionError(f"Maintenance field '{key}' contains an empty value.")
            return result

        safe = {
            "contractVersion": 1,
            "issueId": admission.issue_id,
            "issueRevision": admission.issue_revision,
            "purpose": _bounded_text(record.get("purpose"), 2_048),
            "components": strings("components"),
            "sourceAnchors": strings("sourceAnchors", maximum_text=1_024),
            "contract": _bounded_text(record.get("contract"), 1_024),
            "tests": strings("tests", maximum_text=1_024),
            "limitations": strings("limitations", maximum_text=1_024),
            "recordedAt": int(self._clock()),
        }
        if not safe["purpose"] or not safe["contract"]:
            raise AdmissionError("Maintenance purpose and contract are required.")
        serialized = json.dumps(safe, ensure_ascii=False)
        forbidden = (
            "api_key",
            "authorization:",
            "bearer ",
            "password",
            "secret=",
            "token=",
            ".env",
            "raw transcript",
            "chain-of-thought",
            "chain of thought",
            "\\users\\",
            "c:/users/",
            "/home/",
        )
        if len(serialized) > MAX_RECORD or any(term in serialized.lower() for term in forbidden):
            raise AdmissionError("The maintenance record contains forbidden or oversized data.")
        for anchor in safe["sourceAnchors"]:
            if (
                anchor.startswith(("/", "\\"))
                or (len(anchor) >= 3 and anchor[1] == ":" and anchor[2] in "\\/")
                or ".." in Path(anchor).parts
            ):
                raise AdmissionError("Maintenance source anchors must be project-relative.")
        return safe

    def _expire_active(self, now: int) -> None:
        for token in [key for key, item in self._active.items() if item.expires_at < now]:
            admission = self._active.pop(token)
            self._audit("expired", admission, {})

    def _load_or_create_key(self) -> bytes:
        if self._key_path.exists():
            data = self._key_path.read_bytes()
            if len(data) == 32:
                return data
        data = secrets.token_bytes(32)
        temporary = self._key_path.with_suffix(".tmp")
        temporary.write_bytes(data)
        os.replace(temporary, self._key_path)
        return data

    def _read_boot_id(self) -> str:
        try:
            payload = json.loads(self._boot_path.read_text(encoding="utf-8"))
            return _safe_token(payload.get("bootId"), label="authority boot identifier")
        except (OSError, ValueError, AdmissionError) as exc:
            raise AdmissionError("The live Photon admission authority is unavailable.") from exc

    def _load_or_create_runtime_boot(self) -> str:
        """Share one boot nonce across the dashboard and gateway processes."""
        runtime_id = _runtime_instance_id()
        with _exclusive_file_lock(self._root / "boot.lock"):
            try:
                payload = json.loads(self._boot_path.read_text(encoding="utf-8"))
            except (OSError, ValueError):
                payload = {}
            if payload.get("runtimeId") == runtime_id:
                try:
                    return _safe_token(payload.get("bootId"), label="authority boot identifier")
                except AdmissionError:
                    pass

            boot_id = secrets.token_hex(24)
            self._atomic_json(
                self._boot_path,
                {
                    "bootId": boot_id,
                    "runtimeId": runtime_id,
                    "startedAt": int(self._clock()),
                },
            )
            self._atomic_json(self._approval_path, {"approvals": []})
            return boot_id

    def _read_approvals(self) -> list[ArchitectApproval]:
        try:
            payload = json.loads(self._approval_path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            return []
        rows = payload.get("approvals") if isinstance(payload, Mapping) else None
        if not isinstance(rows, list) or len(rows) > 128:
            return []
        result: list[ArchitectApproval] = []
        for row in rows:
            if not isinstance(row, Mapping):
                continue
            try:
                result.append(ArchitectApproval(**{key: row[key] for key in ArchitectApproval.__dataclass_fields__}))
            except (KeyError, TypeError, ValueError):
                continue
        return result

    def _write_approvals(self, approvals: list[ArchitectApproval]) -> None:
        self._atomic_json(self._approval_path, {"approvals": [asdict(item) for item in approvals]})

    def _unsigned(self, approval: ArchitectApproval) -> dict[str, Any]:
        payload = asdict(approval)
        payload.pop("signature", None)
        return payload

    def _sign(self, payload: Mapping[str, Any]) -> str:
        body = json.dumps(payload, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
        return hmac.new(self._key, body, sha256).hexdigest()

    def _audit(self, event: str, admission: ActiveAdmission, extra: Mapping[str, Any]) -> None:
        self._append_jsonl(self._audit_path, {
            "event": event,
            "issueId": admission.issue_id,
            "issueRevision": admission.issue_revision,
            "generation": admission.generation,
            "authority": admission.authority,
            "at": int(self._clock()),
            **dict(extra),
        })

    @staticmethod
    def _digest(payload: Mapping[str, Any]) -> str:
        body = json.dumps(payload, sort_keys=True, separators=(",", ":"), ensure_ascii=False)
        return sha256(body.encode("utf-8")).hexdigest()

    def _append_jsonl(self, path: Path, payload: Mapping[str, Any]) -> None:
        line = json.dumps(payload, sort_keys=True, ensure_ascii=False, separators=(",", ":"))
        if len(line) > MAX_RECORD:
            raise AdmissionError("The bounded audit record is too large.")
        with path.open("a", encoding="utf-8", newline="\n") as handle:
            handle.write(line + "\n")

    @staticmethod
    def _atomic_json(path: Path, payload: Mapping[str, Any]) -> None:
        temporary = path.with_suffix(path.suffix + ".tmp")
        temporary.write_text(json.dumps(payload, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
        os.replace(temporary, path)
