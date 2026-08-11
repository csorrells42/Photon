from __future__ import annotations

import hashlib
import io
import json
import time
import zipfile
from dataclasses import asdict

import pytest

from agent.memory_provider import (
    LogicalMemoryImportResult,
    LogicalMemoryPage,
    LogicalMemoryRecord,
    MemoryProvider,
)
from hermes_cli.dashboard_auth.base import Session
from hermes_cli.memory_archive import (
    MANIFEST_NAME,
    RECORDS_NAME,
    MemoryArchiveError,
    MemoryArchiveService,
    logical_content_fingerprint,
    replace_confirmation_phrase,
)


def _session(user="alice", provider="test-idp"):
    return Session(
        user_id=user,
        email=f"{user}@example.test",
        display_name=user,
        org_id="",
        provider=provider,
        expires_at=int(time.time()) + 3600,
        access_token="unused-by-memory-archive",
        refresh_token="unused-by-memory-archive",
    )


def _record(portable_id, content, **metadata):
    return LogicalMemoryRecord(
        portable_id=portable_id,
        content=content,
        source="assistant",
        metadata=metadata,
        created_at="2026-08-10T00:00:00Z",
        updated_at="2026-08-10T00:00:00Z",
    )


class FakeLogicalProvider(MemoryProvider):
    def __init__(self):
        self.records = {}
        self.import_results = {}
        self.import_calls = []
        self.delete_calls = []
        self.raise_after_apply_once = False
        self.raise_after_delete_once = False

    @property
    def name(self):
        return "fake-logical"

    def is_available(self):
        return True

    def initialize(self, session_id, **kwargs):
        pass

    def sync_turn(self, user_content, assistant_content, *, session_id="", messages=None):
        pass

    def get_tool_schemas(self):
        return []

    def export_logical_records(self, *, principal_id, cursor=None, limit=500):
        values = list(self.records.get(principal_id, {}).values())
        start = int(cursor or 0)
        page = values[start : start + limit]
        next_cursor = str(start + limit) if start + limit < len(values) else None
        return LogicalMemoryPage(list(page), next_cursor)

    def import_logical_records(self, records, *, principal_id, infer=False, idempotency_key=""):
        assert infer is False
        self.import_calls.append((principal_id, idempotency_key, list(records)))
        if idempotency_key in self.import_results:
            return self.import_results[idempotency_key]
        owned = self.records.setdefault(principal_id, {})
        imported = duplicates = conflicts = 0
        for record in records:
            existing = owned.get(record.portable_id)
            if existing is None:
                owned[record.portable_id] = record
                imported += 1
            else:
                existing_fp = logical_content_fingerprint(existing.content, existing.source, existing.metadata)
                if existing_fp == record.content_fingerprint:
                    duplicates += 1
                else:
                    suffix = record.content_fingerprint[:12]
                    owned[f"{record.portable_id}~{suffix}"] = LogicalMemoryRecord(
                        **{**asdict(record), "portable_id": f"{record.portable_id}~{suffix}"}
                    )
                    imported += 1
                    conflicts += 1
        result = LogicalMemoryImportResult(imported, duplicates, conflicts, 0)
        self.import_results[idempotency_key] = result
        if self.raise_after_apply_once:
            self.raise_after_apply_once = False
            raise RuntimeError("simulated interruption")
        return result

    def delete_logical_records(self, record_ids, *, principal_id):
        self.delete_calls.append((principal_id, list(record_ids)))
        owned = self.records.setdefault(principal_id, {})
        if record_ids and not owned:
            return len(record_ids)
        deleted = 0
        for record_id in record_ids:
            if record_id in owned:
                del owned[record_id]
                deleted += 1
        if self.raise_after_delete_once:
            self.raise_after_delete_once = False
            raise RuntimeError("simulated delete interruption")
        return deleted


def _principal_for(service, session):
    from hermes_cli.memory_archive import canonical_session_principal
    return canonical_session_principal(session)


def _rewrite_archive(blob, mutate):
    with zipfile.ZipFile(io.BytesIO(blob), "r") as source:
        manifest = json.loads(source.read(MANIFEST_NAME))
        lines = [json.loads(line) for line in source.read(RECORDS_NAME).splitlines()]
    mutate(manifest, lines)
    records_blob = b"\n".join(
        json.dumps(line, ensure_ascii=False, allow_nan=False, sort_keys=True, separators=(",", ":")).encode()
        for line in lines
    ) + (b"\n" if lines else b"")
    manifest["record_count"] = len(lines)
    manifest["records_sha256"] = hashlib.sha256(records_blob).hexdigest()
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w") as target:
        target.writestr(MANIFEST_NAME, json.dumps(manifest, sort_keys=True, separators=(",", ":")))
        target.writestr(RECORDS_NAME, records_blob)
    return output.getvalue()


def test_no_login_and_expired_session_fail_closed(tmp_path):
    service = MemoryArchiveService(FakeLogicalProvider(), tmp_path)
    with pytest.raises(MemoryArchiveError) as missing:
        service.export(None)
    assert missing.value.code == "authentication_required"
    expired = _session()
    expired = Session(**{**expired.__dict__, "expires_at": int(time.time()) - 1})
    with pytest.raises(MemoryArchiveError) as stale:
        service.export(expired)
    assert stale.value.code == "authentication_required"


def test_archive_is_principal_bound_and_cross_principal_import_is_denied(tmp_path):
    provider = FakeLogicalProvider()
    service = MemoryArchiveService(provider, tmp_path)
    alice = _session("alice")
    provider.records[_principal_for(service, alice)] = {"one": _record("one", "Alice likes tea")}
    archive = service.export(alice)
    with pytest.raises(MemoryArchiveError) as denied:
        service.import_additive(_session("bob"), archive)
    assert denied.value.code == "principal_mismatch"
    assert provider.import_calls == []


def test_export_contains_only_manifest_and_logical_jsonl_not_session_credentials(tmp_path):
    provider = FakeLogicalProvider()
    service = MemoryArchiveService(provider, tmp_path)
    session = _session()
    principal = _principal_for(service, session)
    provider.records[principal] = {"one": _record("one", "safe portable fact", tags=["portable"])}
    blob = service.export(session)
    assert session.access_token.encode() not in blob
    assert session.refresh_token.encode() not in blob
    assert b"qdrant" not in blob.lower()
    with zipfile.ZipFile(io.BytesIO(blob), "r") as archive:
        assert sorted(archive.namelist()) == [MANIFEST_NAME, RECORDS_NAME]
        record = json.loads(archive.read(RECORDS_NAME).splitlines()[0])
    assert set(record) == {
        "portable_id",
        "content",
        "source",
        "metadata",
        "created_at",
        "updated_at",
        "content_fingerprint",
    }
    assert not {"embedding", "vector", "backend_id", "user_id"} & set(record)


@pytest.mark.parametrize(
    "secret",
    [
        "OPENAI_API_KEY=sk-proj-abcdefghijklmnopqrstuv",
        "Authorization: Bearer abcdefghijklmnopqrstuvwxyz",
        "Cookie: session=abcdefghijklmnopqrstuvwxyz",
        "-----BEGIN PRIVATE KEY-----",
        "refresh_token=abcdefghijklmnopqrstuvwxyz",
        "credential_ref=machine-local-slot-12345",
        "vault_id=machine-only-secret-slot",
        "session=abcdefghijklmnopqrstuvwxyz",
        "C:/Users/test/.env",
        "backup/auth.json",
        "eyJabcdefghijk.abcdefghijk.abcdefghijk",
    ],
)
def test_export_rejects_secret_corpus_without_echo(tmp_path, secret):
    provider = FakeLogicalProvider()
    service = MemoryArchiveService(provider, tmp_path)
    session = _session()
    principal = _principal_for(service, session)
    provider.records[principal] = {"one": _record("one", secret)}
    with pytest.raises(MemoryArchiveError) as rejected:
        service.export(session)
    assert rejected.value.code == "secret_detected"
    assert secret not in str(rejected.value)


def test_import_rescans_archive_and_rejects_secret_metadata(tmp_path):
    provider = FakeLogicalProvider()
    service = MemoryArchiveService(provider, tmp_path)
    session = _session()
    principal = _principal_for(service, session)
    provider.records[principal] = {"one": _record("one", "safe fact")}
    archive = service.export(session)
    provider.records[principal] = {}

    def mutate(_manifest, lines):
        lines[0]["metadata"] = {"api_key": "sk-proj-abcdefghijklmnopqrstuvwxyz"}
        lines[0]["content_fingerprint"] = logical_content_fingerprint(
            lines[0]["content"], lines[0]["source"], lines[0]["metadata"]
        )

    hostile = _rewrite_archive(archive, mutate)
    with pytest.raises(MemoryArchiveError) as rejected:
        service.import_additive(session, hostile)
    assert rejected.value.code == "secret_detected"
    assert "sk-proj" not in str(rejected.value)
    assert provider.import_calls == []


def test_additive_duplicate_conflict_and_retry_are_idempotent(tmp_path):
    provider = FakeLogicalProvider()
    service = MemoryArchiveService(provider, tmp_path)
    session = _session()
    principal = _principal_for(service, session)
    original_one = _record("one", "portable original", category="preference")
    original_two = _record("two", "portable duplicate")
    provider.records[principal] = {"one": original_one, "two": original_two}
    archive = service.export(session)
    provider.records[principal] = {
        "one": _record("one", "destination conflict", category="preference"),
        "two": original_two,
    }
    first = service.import_additive(session, archive)
    second = service.import_additive(session, archive)
    assert (first.imported, first.duplicates, first.conflicts) == (1, 1, 1)
    assert second == type(second)(**{**first.__dict__, "retried": True})
    assert len(provider.import_calls) == 1
    assert len(provider.records[principal]) == 3


def test_additive_retry_after_interruption_uses_provider_idempotency(tmp_path):
    provider = FakeLogicalProvider()
    service = MemoryArchiveService(provider, tmp_path)
    session = _session()
    principal = _principal_for(service, session)
    provider.records[principal] = {"one": _record("one", "portable")}
    archive = service.export(session)
    provider.records[principal] = {}
    provider.raise_after_apply_once = True
    with pytest.raises(MemoryArchiveError) as interrupted:
        service.import_additive(session, archive)
    assert interrupted.value.code == "provider_failed"
    assert "simulated" not in str(interrupted.value)
    report = service.import_additive(session, archive)
    assert report.imported == 1
    assert list(provider.records[principal]) == ["one"]
    assert len(provider.import_calls) == 2
    assert provider.import_calls[0][1] == provider.import_calls[1][1]


def test_archive_with_extra_raw_backend_entry_is_rejected(tmp_path):
    provider = FakeLogicalProvider()
    service = MemoryArchiveService(provider, tmp_path)
    session = _session()
    principal = _principal_for(service, session)
    provider.records[principal] = {"one": _record("one", "safe")}
    clean = service.export(session)
    output = io.BytesIO()
    with zipfile.ZipFile(io.BytesIO(clean), "r") as source, zipfile.ZipFile(output, "w") as target:
        for info in source.infolist():
            target.writestr(info.filename, source.read(info.filename))
        target.writestr("qdrant/storage.sqlite", b"raw backend")
    with pytest.raises(MemoryArchiveError) as rejected:
        service.import_additive(session, output.getvalue())
    assert rejected.value.code == "archive_invalid"


def test_replace_requires_typed_confirmation_prebacks_up_and_scopes_delete(tmp_path):
    provider = FakeLogicalProvider()
    service = MemoryArchiveService(provider, tmp_path)
    alice = _session("alice")
    bob = _session("bob")
    alice_id = _principal_for(service, alice)
    bob_id = _principal_for(service, bob)
    provider.records[alice_id] = {"desired": _record("desired", "desired memory")}
    archive = service.export(alice)
    provider.records[alice_id] = {"old": _record("old", "old memory")}
    provider.records[bob_id] = {"bob": _record("bob", "Bob memory")}

    with pytest.raises(MemoryArchiveError) as denied:
        service.import_replace(alice, archive, confirmation="REPLACE")
    assert denied.value.code == "confirmation_required"
    assert provider.delete_calls == []

    report = service.import_replace(
        alice,
        archive,
        confirmation=replace_confirmation_phrase(alice),
    )
    assert report.deleted == 1
    assert report.prebackup_name
    assert (tmp_path / "pre-replace" / report.prebackup_name).is_file()
    assert list(provider.records[alice_id]) == ["desired"]
    assert list(provider.records[bob_id]) == ["bob"]
    assert provider.delete_calls == [(alice_id, ["old"])]


def test_replace_retry_after_atomic_delete_interruption_reuses_prebackup(tmp_path):
    provider = FakeLogicalProvider()
    service = MemoryArchiveService(provider, tmp_path)
    session = _session()
    principal = _principal_for(service, session)
    provider.records[principal] = {"desired": _record("desired", "desired")}
    archive = service.export(session)
    provider.records[principal] = {"old": _record("old", "old")}
    provider.raise_after_delete_once = True

    with pytest.raises(MemoryArchiveError) as interrupted:
        service.import_replace(
            session,
            archive,
            confirmation=replace_confirmation_phrase(session),
        )
    assert interrupted.value.code == "provider_failed"

    report = service.import_replace(
        session,
        archive,
        confirmation=replace_confirmation_phrase(session),
    )
    assert report.deleted == 1
    assert list(provider.records[principal]) == ["desired"]
    assert len(provider.delete_calls) == 2
    assert provider.delete_calls[0] == provider.delete_calls[1]
