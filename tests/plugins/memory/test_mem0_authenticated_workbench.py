from __future__ import annotations

import json

import pytest

from agent.memory_provider import LogicalMemoryRecord
from hermes_cli.dashboard_auth import live_principals
from plugins.memory.mem0 import Mem0MemoryProvider


class _Backend:
    supports_owner_filtered_mutation = False

    def __init__(self) -> None:
        self.searches: list[tuple[str, dict]] = []
        self.adds: list[dict] = []
        self.raw_mutations: list[str] = []
        self.rows: list[dict] = []

    def search(self, query, *, filters, top_k=10, rerank=False):
        self.searches.append((query, filters))
        return list(self.rows[:top_k])

    def add(self, messages, *, user_id, agent_id, infer=False, metadata=None):
        self.adds.append({
            "user_id": user_id,
            "messages": messages,
            "infer": infer,
            "metadata": metadata,
        })
        self.rows.append({
            "id": f"row-{len(self.rows) + 1}",
            "memory": messages[0]["content"],
            "metadata": dict(metadata or {}),
        })
        return {"event_id": "ok"}

    def update(self, memory_id, text):
        self.raw_mutations.append("update")
        return {}

    def delete(self, memory_id):
        self.raw_mutations.append("delete")
        return {}

    def close(self):
        return None

    def list_owned(self, principal_id):
        return list(self.rows)

    def delete_all_owned(self, principal_id):
        self.rows.clear()


class _DeleteCapableBackend(_Backend):
    supports_owner_filtered_delete = True

    def __init__(self) -> None:
        super().__init__()
        self.owned_deletes: list[tuple[str, str]] = []

    def delete_owned(self, memory_id, principal_id):
        self.owned_deletes.append((memory_id, principal_id))
        return {"result": "Owned memory deletion applied.", "memory_id": memory_id}


@pytest.fixture(autouse=True)
def _strict_mode(monkeypatch):
    monkeypatch.setenv("HERMES_WORKBENCH_AUTHENTICATED_MEM0", "1")
    live_principals._reset_for_tests()
    yield
    live_principals._reset_for_tests()


def _provider(
    monkeypatch, subject: str, backend_type=_Backend
) -> tuple[Mem0MemoryProvider, _Backend]:
    # Deliberately include the legacy fallback and an operator override.  Both
    # must be ignored when identity comes from the authenticated Workbench.
    monkeypatch.setattr(
        "plugins.memory.mem0._load_config",
        lambda: {
            "mode": "oss",
            "user_id": "hermes-user",
            "oss": {"vector_store": {"provider": "qdrant"}},
        },
    )
    backend = backend_type()
    provider = Mem0MemoryProvider()
    monkeypatch.setattr(provider, "_create_backend", lambda: backend)
    lease = live_principals.activate_principal("stub", subject)
    provider.initialize(
        "session",
        user_id="configured-override",
        principal_id=lease.principal_key,
        principal_generation=lease.generation,
    )
    return provider, backend


def test_two_users_are_isolated_and_legacy_override_is_ignored(monkeypatch):
    alice, alice_backend = _provider(monkeypatch, "alice")
    bob, bob_backend = _provider(monkeypatch, "bob")

    alice.handle_tool_call("mem0_search", {"query": "project"})
    bob.handle_tool_call("mem0_search", {"query": "project"})

    alice_id = alice_backend.searches[0][1]["user_id"]
    bob_id = bob_backend.searches[0][1]["user_id"]
    assert alice_id != bob_id
    assert alice_id not in {"alice", "hermes-user", "configured-override"}
    assert bob_id not in {"bob", "hermes-user", "configured-override"}


def test_raw_id_mutation_is_unavailable_without_atomic_owner_filter(monkeypatch):
    provider, backend = _provider(monkeypatch, "alice")

    schema_names = {schema["name"] for schema in provider.get_tool_schemas()}
    assert schema_names == {"mem0_search", "mem0_add"}

    update = json.loads(
        provider.handle_tool_call(
            "mem0_update",
            {"memory_id": "owned-by-someone-else", "text": "changed"},
        )
    )
    delete = json.loads(
        provider.handle_tool_call(
            "mem0_delete",
            {"memory_id": "owned-by-someone-else"},
        )
    )
    assert "disabled" in update["error"]
    assert "disabled" in delete["error"]
    assert backend.raw_mutations == []


def test_exact_owner_filtered_delete_is_available_without_enabling_update(monkeypatch):
    provider, backend = _provider(monkeypatch, "alice", _DeleteCapableBackend)

    schema_names = {schema["name"] for schema in provider.get_tool_schemas()}
    assert schema_names == {"mem0_search", "mem0_add", "mem0_delete"}

    update = json.loads(
        provider.handle_tool_call(
            "mem0_update",
            {"memory_id": "72a8548f-4841-41ee-b37a-b8c3d736503c", "text": "changed"},
        )
    )
    deleted = json.loads(
        provider.handle_tool_call(
            "mem0_delete",
            {"memory_id": "72a8548f-4841-41ee-b37a-b8c3d736503c"},
        )
    )
    assert "disabled" in update["error"]
    assert deleted["memory_id"] == "72a8548f-4841-41ee-b37a-b8c3d736503c"
    assert backend.owned_deletes == [
        ("72a8548f-4841-41ee-b37a-b8c3d736503c", provider._user_id),
    ]
    assert backend.raw_mutations == []


def test_logout_revokes_future_recall_and_writes(monkeypatch):
    provider, backend = _provider(monkeypatch, "alice")
    assert live_principals.revoke_principal("stub", "alice") is True

    search = json.loads(provider.handle_tool_call("mem0_search", {"query": "secret"}))
    add = json.loads(provider.handle_tool_call("mem0_add", {"content": "fact"}))
    assert "no longer active" in search["error"]
    assert "no longer active" in add["error"]
    assert backend.searches == []
    assert backend.adds == []


def test_background_review_add_uses_parent_principal_and_no_inference(monkeypatch):
    provider, backend = _provider(monkeypatch, "alice")
    provider.on_memory_write(
        "add",
        "memory",
        "Prefers metric fasteners",
        metadata={
            "write_origin": "background_review",
            "execution_context": "background_review",
            "tool_call_id": "call-1",
            "untrusted_extra": "must-not-cross",
        },
    )

    assert len(backend.adds) == 1
    assert backend.adds[0]["user_id"] == provider._user_id
    assert backend.adds[0]["messages"] == [
        {"role": "user", "content": "Prefers metric fasteners"}
    ]
    assert backend.adds[0]["infer"] is False
    assert backend.adds[0]["metadata"]["write_origin"] == "background_review"
    assert "untrusted_extra" not in backend.adds[0]["metadata"]


def test_memory_prompt_marks_recall_as_non_authoritative(monkeypatch):
    provider, _ = _provider(monkeypatch, "alice")
    prompt = provider.system_prompt_block().lower()
    for term in (
        "untrusted",
        "authorization",
        "permissions",
        "identity proof",
        "consent",
        "current user intent",
    ):
        assert term in prompt
    assert "alice" not in prompt


def test_authenticated_recall_is_bounded_redacted_and_marked_untrusted(monkeypatch):
    provider, backend = _provider(monkeypatch, "alice")
    backend.rows.extend([
        {"id": "safe", "memory": "Uses metric fasteners", "score": 0.9},
        {
            "id": "secret",
            "memory": "OPENAI_API_KEY=sk-proj-abcdefghijklmnopqrstuvwxyz",
            "score": 0.8,
        },
        {
            "id": "path",
            "memory": r"Private source is C:\Users\Chris\project.py",
            "score": 0.7,
        },
        {
            "id": "redacted",
            "memory": "Rotate sk-proj-abcdefghijklmnopqrstuvwxyz soon",
            "score": 0.6,
        },
        {
            "id": "container-path",
            "memory": "Identity receipt is at /opt/data/private/receipt.json",
            "score": 0.5,
        },
    ])

    result = json.loads(
        provider.handle_tool_call(
            "mem0_search",
            {"query": "project", "top_k": 50},
        )
    )

    assert result["treatment"] == "untrusted_context"
    assert result["count"] == 2
    assert result["results"] == [
        {"id": "safe", "memory": "Uses metric fasteners", "score": 0.9},
        {"id": "redacted", "memory": "Rotate «redacted:sk-…» soon", "score": 0.6},
    ]
    assert backend.searches == [("project", {"user_id": provider._user_id})]


def test_authenticated_recall_surfaces_real_backend_error_not_admission_error(
    monkeypatch,
):
    provider, backend = _provider(monkeypatch, "alice")

    def fail(*_args, **_kwargs):
        raise ConnectionError("qdrant unavailable")

    backend.search = fail
    result = json.loads(provider.handle_tool_call("mem0_search", {"query": "health"}))

    assert "Search failed: qdrant unavailable" in result["error"]
    assert "admission" not in result["error"].lower()


def test_logical_archive_is_additive_retry_safe_and_full_owner_delete_only(monkeypatch):
    provider, backend = _provider(monkeypatch, "alice")
    backend.supports_logical_records = True
    first = LogicalMemoryRecord(
        portable_id="portable-a",
        content="Uses metric fasteners",
        source="user",
        metadata={"category": "preference"},
    )

    result = provider.import_logical_records(
        [first],
        principal_id=provider._user_id,
        infer=False,
        idempotency_key="op-1",
    )
    assert (result.imported, result.duplicates, result.conflicts, result.skipped) == (
        1,
        0,
        0,
        0,
    )
    retry = provider.import_logical_records(
        [first],
        principal_id=provider._user_id,
        infer=False,
        idempotency_key="op-1",
    )
    assert (retry.imported, retry.duplicates) == (0, 1)

    page = provider.export_logical_records(principal_id=provider._user_id)
    assert [record.portable_id for record in page.records] == ["portable-a"]
    with pytest.raises(ValueError, match="complete"):
        provider.delete_logical_records([], principal_id=provider._user_id)
    assert (
        provider.delete_logical_records(
            ["portable-a"],
            principal_id=provider._user_id,
        )
        == 1
    )
    # Crash-safe retry after delete_all committed but before journal update.
    assert (
        provider.delete_logical_records(
            ["portable-a"],
            principal_id=provider._user_id,
        )
        == 1
    )
