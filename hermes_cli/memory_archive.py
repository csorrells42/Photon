"""Authenticated, secret-free logical memory archive service.

This module is deliberately not a CLI command and never accepts a caller-
supplied user ID.  A route/controller must pass the verified dashboard
``Session``.  The active memory provider receives only the canonical
provider+subject principal derived from that session.

Archives contain a small canonical manifest and JSONL logical records.  They
never contain backend files, embeddings, credentials, provider configuration,
or raw vector-database state.
"""

from __future__ import annotations

import hashlib
import io
import json
import os
import re
import threading
import time
import zipfile
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, List, Mapping, Optional, Tuple

from agent.memory_provider import (
    LogicalMemoryImportResult,
    LogicalMemoryPage,
    LogicalMemoryRecord,
    MemoryLogicalRecordsUnsupported,
    MemoryProvider,
)
from hermes_cli.dashboard_auth.base import Session


ARCHIVE_FORMAT = "hermes-logical-memory"
ARCHIVE_VERSION = 1
MANIFEST_NAME = "manifest.json"
RECORDS_NAME = "records.jsonl"
MAX_ARCHIVE_BYTES = 32 * 1024 * 1024
MAX_RECORDS = 10_000
MAX_RECORD_BYTES = 256 * 1024
MAX_METADATA_BYTES = 64 * 1024
PAGE_SIZE = 500

_RECORD_KEYS = {
    "portable_id",
    "content",
    "source",
    "metadata",
    "created_at",
    "updated_at",
    "content_fingerprint",
}
_MANIFEST_KEYS = {
    "format",
    "version",
    "created_at",
    "principal_fingerprint",
    "record_count",
    "records_sha256",
}
_METADATA_ALLOWLIST = {
    "category",
    "tags",
    "source",
    "source_type",
    "write_origin",
    "execution_context",
    "background_review",
    "created_at",
    "updated_at",
}
_SENSITIVE_KEY_PARTS = {
    "apikey",
    "accesstoken",
    "refreshtoken",
    "authtoken",
    "authorization",
    "bearer",
    "clientsecret",
    "cookie",
    "credential",
    "credentialpool",
    "credentialref",
    "env",
    "machinebound",
    "machinestate",
    "oauth",
    "password",
    "privatekey",
    "session",
    "token",
    "vault",
}
_SECRET_PATTERNS = tuple(
    re.compile(pattern, re.IGNORECASE)
    for pattern in (
        r"-----BEGIN (?:RSA |EC |OPENSSH |PGP )?PRIVATE KEY-----",
        r"\b(?:sk|pk|rk)-(?:live|test|proj)-[A-Za-z0-9_-]{12,}\b",
        r"\b(?:sk|sess|key)-[A-Za-z0-9_-]{20,}\b",
        r"\b(?:ghp|gho|ghu|ghs|github_pat)_[A-Za-z0-9_]{20,}\b",
        r"\b(?:glpat-|npm_)[A-Za-z0-9_-]{20,}\b",
        r"\bxox[baprs]-[A-Za-z0-9-]{16,}\b",
        r"\bAIza[0-9A-Za-z_-]{20,}\b",
        r"\bGOCSPX-[0-9A-Za-z_-]{16,}\b",
        r"\bAKIA[0-9A-Z]{16}\b",
        r"\bya29\.[0-9A-Za-z_-]{20,}\b",
        r"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b",
        r"\b(?:authorization|cookie)\s*:\s*\S+",
        r"\b(?:api[_-]?key|access[_-]?token|refresh[_-]?token|client[_-]?secret|password)\s*[:=]\s*['\"]?[^\s'\"]{8,}",
        r"\b(?:auth[_-]?state|session|cookie|credential[_-]?(?:ref|pool)|vault[_-]?(?:ref|id)|machine[_-]?(?:state|ref))\s*[:=]\s*['\"]?[^\s'\"]{8,}",
        r"(?m)^\s*[A-Z][A-Z0-9_]*(?:KEY|TOKEN|SECRET|PASSWORD|COOKIE)\s*=\s*\S{8,}\s*$",
        r"(?:^|[\\/])\.env(?:\.[A-Za-z0-9_.-]+)?(?:$|[\\/])",
        r"(?:^|[\\/])(?:auth\.json|state\.db)(?:$|[\\/])",
    )
)
_SAFE_TOKEN_RE = re.compile(r"^[^\x00-\x1f\x7f]{1,256}$")


class MemoryArchiveError(RuntimeError):
    """A bounded, non-sensitive archive failure safe to return to a client."""

    def __init__(self, code: str, message: str):
        self.code = code
        super().__init__(message[:240])


@dataclass(frozen=True)
class MemoryArchiveImportReport:
    mode: str
    imported: int
    duplicates: int
    conflicts: int
    skipped: int
    deleted: int = 0
    prebackup_name: str = ""
    retried: bool = False


def canonical_session_principal(session: Session) -> str:
    """Return the opaque issuer+subject key for a verified live session."""
    if not isinstance(session, Session):
        raise MemoryArchiveError("authentication_required", "Authentication is required.")
    provider = str(session.provider or "").strip().casefold()
    subject = str(session.user_id or "").strip()
    try:
        expires_at = int(session.expires_at or 0)
    except (TypeError, ValueError, OverflowError):
        expires_at = 0
    if not provider or not subject or expires_at <= int(time.time()):
        raise MemoryArchiveError("authentication_required", "Authentication is required.")
    if not _SAFE_TOKEN_RE.fullmatch(provider) or not _SAFE_TOKEN_RE.fullmatch(subject):
        raise MemoryArchiveError("invalid_principal", "The authenticated identity is invalid.")
    return f"{len(provider)}:{provider}:{subject}"


def replace_confirmation_phrase(session: Session) -> str:
    principal = canonical_session_principal(session)
    token = hashlib.sha256(principal.encode("utf-8")).hexdigest()[:12].upper()
    return f"REPLACE MY MEMORIES {token}"


def logical_content_fingerprint(
    content: str,
    source: str,
    metadata: Mapping[str, Any],
) -> str:
    body = _canonical_json({"content": content, "metadata": metadata, "source": source})
    return hashlib.sha256(body).hexdigest()


def _canonical_json(value: Any) -> bytes:
    try:
        return json.dumps(
            value,
            ensure_ascii=False,
            allow_nan=False,
            sort_keys=True,
            separators=(",", ":"),
        ).encode("utf-8")
    except (TypeError, ValueError, UnicodeError) as exc:
        raise MemoryArchiveError("invalid_record", "Memory data is not portable JSON.") from exc


def _normalize_key(value: str) -> str:
    return re.sub(r"[^a-z0-9]", "", value.casefold())


def _contains_suspected_secret(value: Any, *, depth: int = 0) -> bool:
    if depth > 12:
        return True
    if isinstance(value, Mapping):
        for key, child in value.items():
            key_text = str(key)
            normalized = _normalize_key(key_text)
            if any(part in normalized for part in _SENSITIVE_KEY_PARTS):
                return True
            if _contains_suspected_secret(child, depth=depth + 1):
                return True
        return False
    if isinstance(value, (list, tuple)):
        return any(_contains_suspected_secret(item, depth=depth + 1) for item in value)
    if isinstance(value, str):
        return any(pattern.search(value) for pattern in _SECRET_PATTERNS)
    return False


def _safe_metadata(raw: Any) -> Dict[str, Any]:
    if not isinstance(raw, Mapping):
        raise MemoryArchiveError("invalid_record", "Memory metadata is invalid.")
    if _contains_suspected_secret(raw):
        raise MemoryArchiveError("secret_detected", "The memory archive was rejected because it may contain credentials.")
    projected = {str(key): value for key, value in raw.items() if str(key) in _METADATA_ALLOWLIST}
    encoded = _canonical_json(projected)
    if len(encoded) > MAX_METADATA_BYTES:
        raise MemoryArchiveError("record_too_large", "Memory metadata exceeds the portable archive limit.")
    return projected


def _normalize_record(record: LogicalMemoryRecord) -> LogicalMemoryRecord:
    if not isinstance(record, LogicalMemoryRecord):
        raise MemoryArchiveError("invalid_record", "The memory provider returned an invalid record.")
    portable_id = str(record.portable_id or "")
    content = str(record.content or "")
    source = str(record.source or "")
    created_at = str(record.created_at or "")
    updated_at = str(record.updated_at or "")
    if not _SAFE_TOKEN_RE.fullmatch(portable_id):
        raise MemoryArchiveError("invalid_record", "A portable memory identifier is invalid.")
    if _contains_suspected_secret((portable_id, content, source, created_at, updated_at)):
        raise MemoryArchiveError("secret_detected", "The memory archive was rejected because it may contain credentials.")
    metadata = _safe_metadata(record.metadata)
    fingerprint = logical_content_fingerprint(content, source, metadata)
    normalized = LogicalMemoryRecord(
        portable_id=portable_id,
        content=content,
        source=source,
        metadata=metadata,
        created_at=created_at,
        updated_at=updated_at,
        content_fingerprint=fingerprint,
    )
    if len(_canonical_json(asdict(normalized))) > MAX_RECORD_BYTES:
        raise MemoryArchiveError("record_too_large", "A memory record exceeds the portable archive limit.")
    return normalized


def _record_dict(record: LogicalMemoryRecord) -> Dict[str, Any]:
    return {key: asdict(record)[key] for key in sorted(_RECORD_KEYS)}


class MemoryArchiveService:
    """Logical export/import service bound to a configured provider.

    The public methods accept a verified ``Session`` and archive bytes only.
    There is intentionally no ``user_id`` argument, raw-path import, or generic
    backup integration.
    """

    def __init__(self, provider: MemoryProvider, state_dir: Path | str):
        if not isinstance(provider, MemoryProvider):
            raise TypeError("provider must implement MemoryProvider")
        self._provider = provider
        self._state_dir = Path(state_dir)
        self._journal_dir = self._state_dir / "journals"
        self._backup_dir = self._state_dir / "pre-replace"
        self._lock = threading.RLock()

    def export(self, session: Session) -> bytes:
        principal = canonical_session_principal(session)
        records = self._collect_records(principal)
        return self._build_archive(principal, records)

    def import_additive(self, session: Session, archive: bytes) -> MemoryArchiveImportReport:
        principal = canonical_session_principal(session)
        records, digest = self._read_archive(principal, archive)
        operation_id = self._operation_id(principal, digest, "additive")
        with self._lock:
            prior = self._read_journal(operation_id)
            if prior and prior.get("stage") == "complete":
                return self._report_from_journal(prior, retried=True)
            try:
                result = self._provider.import_logical_records(
                    records, principal_id=principal, infer=False, idempotency_key=operation_id
                )
            except MemoryLogicalRecordsUnsupported as exc:
                raise MemoryArchiveError("provider_unsupported", "The active memory provider does not support logical import.") from exc
            except Exception as exc:
                raise MemoryArchiveError("provider_failed", "The memory provider could not complete the import.") from exc
            self._validate_import_result(result, len(records))
            journal = self._result_journal("additive", result, stage="complete")
            self._write_journal(operation_id, journal)
            return self._report_from_journal(journal)

    def import_replace(
        self,
        session: Session,
        archive: bytes,
        *,
        confirmation: str,
    ) -> MemoryArchiveImportReport:
        principal = canonical_session_principal(session)
        if confirmation != replace_confirmation_phrase(session):
            raise MemoryArchiveError("confirmation_required", "The principal-specific replace confirmation did not match.")
        records, digest = self._read_archive(principal, archive)
        operation_id = self._operation_id(principal, digest, "replace")
        with self._lock:
            journal = self._read_journal(operation_id) or {
                "mode": "replace",
                "stage": "initialized",
                "imported": 0,
                "duplicates": 0,
                "conflicts": 0,
                "skipped": 0,
                "deleted": 0,
                "prebackup_name": "",
            }
            if journal.get("stage") == "complete":
                return self._report_from_journal(journal, retried=True)

            if journal.get("stage") == "initialized":
                existing = self._collect_records(principal)
                prebackup = self._build_archive(principal, existing)
                backup_name = f"memory-pre-replace-{operation_id}.zip"
                self._atomic_write(self._backup_dir / backup_name, prebackup)
                journal.update(
                    {
                        "stage": "backed_up",
                        "prebackup_name": backup_name,
                        "delete_ids": [record.portable_id for record in existing],
                    }
                )
                self._write_journal(operation_id, journal)

            if journal.get("stage") == "backed_up":
                ids = journal.get("delete_ids")
                if not isinstance(ids, list) or any(not isinstance(item, str) for item in ids):
                    raise MemoryArchiveError("journal_invalid", "The replace journal is invalid; no further changes were made.")
                try:
                    deleted = self._provider.delete_logical_records(ids, principal_id=principal)
                except MemoryLogicalRecordsUnsupported as exc:
                    raise MemoryArchiveError("provider_unsupported", "The active memory provider does not support safe logical replacement.") from exc
                except Exception as exc:
                    raise MemoryArchiveError("provider_failed", "The memory provider could not complete the replacement.") from exc
                if (
                    not isinstance(deleted, int)
                    or isinstance(deleted, bool)
                    or deleted != len(ids)
                ):
                    raise MemoryArchiveError("provider_contract", "The memory provider returned an invalid delete result.")
                journal.update({"stage": "deleted", "deleted": deleted})
                self._write_journal(operation_id, journal)

            try:
                result = self._provider.import_logical_records(
                    records, principal_id=principal, infer=False, idempotency_key=operation_id
                )
            except MemoryLogicalRecordsUnsupported as exc:
                raise MemoryArchiveError("provider_unsupported", "The active memory provider does not support logical import.") from exc
            except Exception as exc:
                raise MemoryArchiveError("provider_failed", "The memory provider could not complete the import.") from exc
            self._validate_import_result(result, len(records))
            journal.update(self._result_journal("replace", result, stage="complete"))
            journal.pop("delete_ids", None)
            self._write_journal(operation_id, journal)
            return self._report_from_journal(journal)

    def _collect_records(self, principal: str) -> List[LogicalMemoryRecord]:
        records: List[LogicalMemoryRecord] = []
        cursor: Optional[str] = None
        seen_cursors = set()
        seen_ids: Dict[str, str] = {}
        while True:
            try:
                page = self._provider.export_logical_records(
                    principal_id=principal,
                    cursor=cursor,
                    limit=PAGE_SIZE,
                )
            except MemoryLogicalRecordsUnsupported as exc:
                raise MemoryArchiveError("provider_unsupported", "The active memory provider does not support logical export.") from exc
            except Exception as exc:
                raise MemoryArchiveError("provider_failed", "The memory provider could not complete the export.") from exc
            if not isinstance(page, LogicalMemoryPage) or not isinstance(page.records, list):
                raise MemoryArchiveError("provider_contract", "The memory provider returned an invalid export page.")
            if len(page.records) > PAGE_SIZE:
                raise MemoryArchiveError("provider_contract", "The memory provider exceeded the export page limit.")
            for raw in page.records:
                record = _normalize_record(raw)
                previous = seen_ids.get(record.portable_id)
                if previous is not None:
                    raise MemoryArchiveError("provider_contract", "The memory provider returned duplicate portable identifiers.")
                seen_ids[record.portable_id] = record.content_fingerprint
                records.append(record)
                if len(records) > MAX_RECORDS:
                    raise MemoryArchiveError("archive_too_large", "The memory record count exceeds the portable archive limit.")
            next_cursor = page.next_cursor
            if next_cursor is None:
                break
            if not isinstance(next_cursor, str) or not _SAFE_TOKEN_RE.fullmatch(next_cursor) or next_cursor in seen_cursors:
                raise MemoryArchiveError("provider_contract", "The memory provider returned an invalid export cursor.")
            seen_cursors.add(next_cursor)
            cursor = next_cursor
        records.sort(key=lambda item: (item.portable_id, item.content_fingerprint))
        return records

    def _build_archive(self, principal: str, records: List[LogicalMemoryRecord]) -> bytes:
        record_lines = [_canonical_json(_record_dict(record)) for record in records]
        records_blob = b"\n".join(record_lines) + (b"\n" if record_lines else b"")
        digest = hashlib.sha256(records_blob).hexdigest()
        manifest = {
            "created_at": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
            "format": ARCHIVE_FORMAT,
            "principal_fingerprint": hashlib.sha256(principal.encode("utf-8")).hexdigest(),
            "record_count": len(records),
            "records_sha256": digest,
            "version": ARCHIVE_VERSION,
        }
        output = io.BytesIO()
        with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
            for name, payload in ((MANIFEST_NAME, _canonical_json(manifest)), (RECORDS_NAME, records_blob)):
                info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
                info.compress_type = zipfile.ZIP_DEFLATED
                info.external_attr = 0o600 << 16
                archive.writestr(info, payload)
        blob = output.getvalue()
        if len(blob) > MAX_ARCHIVE_BYTES:
            raise MemoryArchiveError("archive_too_large", "The memory archive exceeds the portable archive limit.")
        return blob

    def _read_archive(self, principal: str, blob: bytes) -> Tuple[List[LogicalMemoryRecord], str]:
        if not isinstance(blob, bytes) or not blob or len(blob) > MAX_ARCHIVE_BYTES:
            raise MemoryArchiveError("archive_invalid", "The memory archive is missing or exceeds the size limit.")
        try:
            with zipfile.ZipFile(io.BytesIO(blob), "r") as archive:
                infos = archive.infolist()
                names = [info.filename for info in infos]
                if sorted(names) != [MANIFEST_NAME, RECORDS_NAME] or len(set(names)) != 2:
                    raise MemoryArchiveError("archive_invalid", "The memory archive has an unexpected file layout.")
                if any(info.is_dir() or info.flag_bits & 0x1 for info in infos):
                    raise MemoryArchiveError("archive_invalid", "The memory archive contains an unsupported entry.")
                sizes = {info.filename: info.file_size for info in infos}
                if sizes[MANIFEST_NAME] > 64 * 1024 or sizes[RECORDS_NAME] > MAX_ARCHIVE_BYTES:
                    raise MemoryArchiveError("archive_too_large", "The memory archive expands beyond the size limit.")
                manifest_blob = archive.read(MANIFEST_NAME)
                records_blob = archive.read(RECORDS_NAME)
        except MemoryArchiveError:
            raise
        except (zipfile.BadZipFile, KeyError, OSError, RuntimeError) as exc:
            raise MemoryArchiveError("archive_invalid", "The memory archive is invalid.") from exc

        try:
            manifest = json.loads(manifest_blob.decode("utf-8"))
        except (UnicodeError, json.JSONDecodeError) as exc:
            raise MemoryArchiveError("archive_invalid", "The memory archive manifest is invalid.") from exc
        if not isinstance(manifest, dict) or set(manifest) != _MANIFEST_KEYS:
            raise MemoryArchiveError("archive_invalid", "The memory archive manifest is invalid.")
        if _canonical_json(manifest) != manifest_blob or _contains_suspected_secret(manifest):
            raise MemoryArchiveError("archive_invalid", "The memory archive manifest is invalid.")
        if manifest.get("format") != ARCHIVE_FORMAT or manifest.get("version") != ARCHIVE_VERSION:
            raise MemoryArchiveError("archive_version", "The memory archive format is not supported.")
        if (
            not isinstance(manifest.get("created_at"), str)
            or not isinstance(manifest.get("record_count"), int)
            or isinstance(manifest.get("record_count"), bool)
            or manifest.get("record_count", -1) < 0
            or not isinstance(manifest.get("records_sha256"), str)
            or not re.fullmatch(r"[0-9a-f]{64}", manifest.get("records_sha256", ""))
            or not isinstance(manifest.get("principal_fingerprint"), str)
            or not re.fullmatch(r"[0-9a-f]{64}", manifest.get("principal_fingerprint", ""))
        ):
            raise MemoryArchiveError("archive_invalid", "The memory archive manifest is invalid.")
        expected_principal = hashlib.sha256(principal.encode("utf-8")).hexdigest()
        if manifest.get("principal_fingerprint") != expected_principal:
            raise MemoryArchiveError("principal_mismatch", "This memory archive belongs to a different authenticated principal.")
        digest = hashlib.sha256(records_blob).hexdigest()
        if manifest.get("records_sha256") != digest:
            raise MemoryArchiveError("integrity_failed", "The memory archive integrity check failed.")

        lines = records_blob.splitlines()
        if len(lines) != manifest.get("record_count") or len(lines) > MAX_RECORDS:
            raise MemoryArchiveError("archive_invalid", "The memory archive record count is invalid.")
        records: List[LogicalMemoryRecord] = []
        seen_ids: Dict[str, str] = {}
        for line in lines:
            if not line or len(line) > MAX_RECORD_BYTES:
                raise MemoryArchiveError("invalid_record", "A memory record is invalid or exceeds the size limit.")
            try:
                raw = json.loads(line.decode("utf-8"))
            except (UnicodeError, json.JSONDecodeError) as exc:
                raise MemoryArchiveError("invalid_record", "A memory record is invalid.") from exc
            if not isinstance(raw, dict) or set(raw) != _RECORD_KEYS or _canonical_json(raw) != line:
                raise MemoryArchiveError("invalid_record", "A memory record is not canonical.")
            record = _normalize_record(LogicalMemoryRecord(**raw))
            if raw.get("content_fingerprint") != record.content_fingerprint:
                raise MemoryArchiveError("integrity_failed", "A memory record integrity check failed.")
            previous = seen_ids.get(record.portable_id)
            if previous is not None:
                raise MemoryArchiveError("invalid_record", "The memory archive contains duplicate portable identifiers.")
            seen_ids[record.portable_id] = record.content_fingerprint
            records.append(record)
        return records, digest

    @staticmethod
    def _operation_id(principal: str, digest: str, mode: str) -> str:
        return hashlib.sha256(f"{principal}\0{digest}\0{mode}\0v{ARCHIVE_VERSION}".encode("utf-8")).hexdigest()

    @staticmethod
    def _validate_import_result(result: Any, total: int) -> None:
        if not isinstance(result, LogicalMemoryImportResult):
            raise MemoryArchiveError("provider_contract", "The memory provider returned an invalid import result.")
        counts = (result.imported, result.duplicates, result.conflicts, result.skipped)
        if any(not isinstance(value, int) or value < 0 for value in counts):
            raise MemoryArchiveError("provider_contract", "The memory provider returned invalid import counts.")
        if result.imported + result.duplicates + result.skipped != total or result.conflicts > result.imported:
            raise MemoryArchiveError("provider_contract", "The memory provider returned inconsistent import counts.")

    @staticmethod
    def _result_journal(mode: str, result: LogicalMemoryImportResult, *, stage: str) -> Dict[str, Any]:
        return {
            "mode": mode,
            "stage": stage,
            "imported": result.imported,
            "duplicates": result.duplicates,
            "conflicts": result.conflicts,
            "skipped": result.skipped,
        }

    @staticmethod
    def _report_from_journal(journal: Mapping[str, Any], *, retried: bool = False) -> MemoryArchiveImportReport:
        return MemoryArchiveImportReport(
            mode=str(journal.get("mode") or ""),
            imported=int(journal.get("imported") or 0),
            duplicates=int(journal.get("duplicates") or 0),
            conflicts=int(journal.get("conflicts") or 0),
            skipped=int(journal.get("skipped") or 0),
            deleted=int(journal.get("deleted") or 0),
            prebackup_name=str(journal.get("prebackup_name") or ""),
            retried=retried,
        )

    def _journal_path(self, operation_id: str) -> Path:
        return self._journal_dir / f"{operation_id}.json"

    def _read_journal(self, operation_id: str) -> Optional[Dict[str, Any]]:
        path = self._journal_path(operation_id)
        try:
            raw = path.read_bytes()
        except FileNotFoundError:
            return None
        except OSError as exc:
            raise MemoryArchiveError("journal_unavailable", "The memory import journal is unavailable.") from exc
        try:
            value = json.loads(raw.decode("utf-8"))
        except (UnicodeError, json.JSONDecodeError) as exc:
            raise MemoryArchiveError("journal_invalid", "The memory import journal is invalid; no changes were made.") from exc
        if not isinstance(value, dict) or value.get("mode") not in {"additive", "replace"}:
            raise MemoryArchiveError("journal_invalid", "The memory import journal is invalid; no changes were made.")
        return value

    def _write_journal(self, operation_id: str, value: Mapping[str, Any]) -> None:
        self._atomic_write(self._journal_path(operation_id), _canonical_json(value))

    @staticmethod
    def _atomic_write(path: Path, payload: bytes) -> None:
        temp: Optional[Path] = None
        try:
            path.parent.mkdir(parents=True, exist_ok=True)
            temp = path.with_name(f".{path.name}.{os.getpid()}.{threading.get_ident()}.tmp")
            with open(temp, "xb") as handle:
                handle.write(payload)
                handle.flush()
                os.fsync(handle.fileno())
            os.replace(temp, path)
        except OSError as exc:
            try:
                if temp is not None:
                    temp.unlink(missing_ok=True)
            except Exception:
                pass
            raise MemoryArchiveError("storage_unavailable", "The memory archive operation could not be safely persisted.") from exc


__all__ = [
    "ARCHIVE_FORMAT",
    "ARCHIVE_VERSION",
    "MemoryArchiveError",
    "MemoryArchiveImportReport",
    "MemoryArchiveService",
    "canonical_session_principal",
    "logical_content_fingerprint",
    "replace_confirmation_phrase",
]
