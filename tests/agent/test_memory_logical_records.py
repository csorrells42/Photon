from __future__ import annotations

import pytest

from agent.memory_provider import (
    LogicalMemoryRecord,
    MemoryLogicalRecordsUnsupported,
)
from tests.agent.test_memory_provider import FakeMemoryProvider


def test_logical_record_capability_fails_closed_by_default():
    provider = FakeMemoryProvider()
    with pytest.raises(MemoryLogicalRecordsUnsupported):
        provider.export_logical_records(principal_id="issuer:subject")
    with pytest.raises(MemoryLogicalRecordsUnsupported):
        provider.import_logical_records(
            [LogicalMemoryRecord(portable_id="one", content="fact")],
            principal_id="issuer:subject",
            infer=False,
            idempotency_key="operation",
        )
    with pytest.raises(MemoryLogicalRecordsUnsupported):
        provider.delete_logical_records(["one"], principal_id="issuer:subject")


def test_logical_record_has_no_backend_or_embedding_fields():
    fields = set(LogicalMemoryRecord.__dataclass_fields__)
    assert fields == {
        "portable_id",
        "content",
        "source",
        "metadata",
        "created_at",
        "updated_at",
        "content_fingerprint",
    }
    assert not {"backend_id", "embedding", "vector", "user_id"} & fields
