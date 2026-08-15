from __future__ import annotations

import json
import threading

import pytest

from plugins import photon_linear_admission as plugin
from plugins.photon_linear_admission import authority as authority_module
from plugins.photon_linear_admission.authority import (
    AdmissionError,
    IssueSnapshot,
    PhotonLinearAuthority,
    generation_for_session,
    issue_snapshot_from_payload,
    parse_linear_tool_payload,
)


class Fixture:
    def __init__(self, root, *, now: int = 1_000):
        self.now = now
        self.revision = "linear:v1:2026-08-14T00:00:00Z:" + "a" * 64
        self.comments: list[tuple[str, str]] = []
        self.issue_id = "CLS-6"
        self.root = root
        self.authority = PhotonLinearAuthority(
            self.load,
            self.comment,
            state_root=root,
            clock=lambda: self.now,
        )

    def load(self, issue_id: str) -> IssueSnapshot:
        if issue_id != self.issue_id:
            raise AdmissionError("unknown issue")
        return IssueSnapshot(
            issue_id,
            self.revision,
            "Linear admission",
            "In Progress",
            "https://linear.app/example",
        )

    def comment(self, issue_id: str, body: str) -> None:
        self.comments.append((issue_id, body))

    def architect_approve(self, session_id: str = "session-a"):
        return self.authority.write_architect_approval(
            self.issue_id,
            self.revision,
            generation_for_session(session_id),
        )


def _record() -> dict:
    return {
        "purpose": "Gate Photon work sourced from Linear.",
        "components": ["plugins/photon_linear_admission"],
        "sourceAnchors": ["plugins/photon_linear_admission/authority.py"],
        "contract": "photon-linear-admission/v1",
        "tests": ["tests/plugins/test_photon_linear_admission.py"],
        "limitations": ["Chris retains issue closure."],
    }


def test_issue_snapshot_revision_covers_material_fields():
    payload = {
        "identifier": "CLS-6",
        "updatedAt": "2026-08-14T00:00:00Z",
        "title": "Gate Photon",
        "description": "A",
        "priority": {"value": 2},
        "status": "Todo",
        "labels": ["governance"],
        "project": "Hermes Workbench",
        "url": "https://linear.app/example",
    }
    first = issue_snapshot_from_payload(payload)
    second = issue_snapshot_from_payload({**payload, "description": "B"})
    assert first.identifier == "CLS-6"
    assert first.revision != second.revision


def test_linear_payload_parser_unwraps_hermes_mcp_text_envelope():
    issue = {
        "id": "CLS-6",
        "updatedAt": "2026-08-14T00:00:00Z",
        "title": "Gate Photon",
    }
    wrapped = json.dumps({"result": json.dumps(issue)})
    assert parse_linear_tool_payload(wrapped) == issue


def test_linear_payload_parser_prefers_structured_content_and_rejects_errors():
    issue = {
        "id": "CLS-6",
        "updatedAt": "2026-08-14T00:00:00Z",
        "title": "Gate Photon",
    }
    assert parse_linear_tool_payload({"structuredContent": issue}) == issue
    with pytest.raises(AdmissionError, match="inspection failed"):
        parse_linear_tool_payload({"result": json.dumps({"error": "denied"})})


def test_inspection_is_context_only_and_binds_actual_session(tmp_path):
    fixture = Fixture(tmp_path)
    result = fixture.authority.inspect("CLS-6", "session-a")
    assert result["authority"] == "context-only"
    assert result["executionAuthorized"] is False
    assert result["issueRevision"] == fixture.revision
    assert result["generation"] == generation_for_session("session-a")


def test_dual_admission_requires_both_exact_authorities_and_consumes_once(tmp_path):
    fixture = Fixture(tmp_path)
    fixture.authority.inspect("CLS-6", "session-a")
    with pytest.raises(AdmissionError, match="Architect approval"):
        fixture.authority.prepare_dual("CLS-6", fixture.revision, "session-a")
    fixture.architect_approve()
    fixture.authority.prepare_dual("CLS-6", fixture.revision, "session-a")
    admitted = fixture.authority.admit_dual("CLS-6", fixture.revision, "session-a")
    assert admitted["authority"] == "chris+architect"
    with pytest.raises(AdmissionError, match="Architect approval"):
        fixture.authority.admit_dual("CLS-6", fixture.revision, "session-a")


def test_material_edit_and_foreign_generation_fail_before_admission(tmp_path):
    fixture = Fixture(tmp_path)
    fixture.authority.inspect("CLS-6", "session-a")
    fixture.architect_approve("session-a")
    with pytest.raises(AdmissionError, match="Inspect this exact issue revision"):
        fixture.authority.prepare_dual("CLS-6", fixture.revision, "session-b")
    fixture.revision = "linear:v1:2026-08-14T00:01:00Z:" + "b" * 64
    with pytest.raises(AdmissionError, match="changed"):
        fixture.authority.admit_dual(
            "CLS-6", "linear:v1:2026-08-14T00:00:00Z:" + "a" * 64, "session-a"
        )


def test_approval_expiry_and_restart_fail_closed(tmp_path):
    fixture = Fixture(tmp_path)
    fixture.authority.inspect("CLS-6", "session-a")
    fixture.architect_approve()
    fixture.now += 901
    with pytest.raises(AdmissionError, match="Architect approval"):
        fixture.authority.prepare_dual("CLS-6", fixture.revision, "session-a")

    fixture.now = 2_000
    fixture.architect_approve()
    restarted = PhotonLinearAuthority(
        fixture.load,
        fixture.comment,
        state_root=tmp_path,
        clock=lambda: fixture.now,
        initialize_boot=True,
    )
    restarted.inspect("CLS-6", "session-a")
    with pytest.raises(AdmissionError, match="Architect approval"):
        restarted.prepare_dual("CLS-6", fixture.revision, "session-a")


def test_runtime_processes_share_boot_but_new_runtime_invalidates_receipts(
    tmp_path, monkeypatch
):
    fixture = Fixture(tmp_path)
    monkeypatch.setattr(authority_module, "_runtime_instance_id", lambda: "runtime-a")
    first = PhotonLinearAuthority(
        fixture.load,
        fixture.comment,
        state_root=tmp_path,
        clock=lambda: fixture.now,
        shared_runtime_boot=True,
    )
    first.inspect("CLS-6", "session-a")
    first.write_architect_approval(
        "CLS-6", fixture.revision, generation_for_session("session-a")
    )

    second = PhotonLinearAuthority(
        fixture.load,
        fixture.comment,
        state_root=tmp_path,
        clock=lambda: fixture.now,
        shared_runtime_boot=True,
    )
    assert second.boot_id == first.boot_id
    second.inspect("CLS-6", "session-a")
    second.prepare_dual("CLS-6", fixture.revision, "session-a")

    monkeypatch.setattr(authority_module, "_runtime_instance_id", lambda: "runtime-b")
    restarted = PhotonLinearAuthority(
        fixture.load,
        fixture.comment,
        state_root=tmp_path,
        clock=lambda: fixture.now,
        shared_runtime_boot=True,
    )
    assert restarted.boot_id != first.boot_id
    restarted.inspect("CLS-6", "session-a")
    with pytest.raises(AdmissionError, match="Architect approval"):
        restarted.prepare_dual("CLS-6", fixture.revision, "session-a")


def test_direct_override_is_exact_revision_and_actual_session_bound(tmp_path):
    fixture = Fixture(tmp_path)
    fixture.authority.inspect("CLS-6", "session-a")
    fixture.authority.prepare_direct("CLS-6", fixture.revision, "session-a")
    admitted = fixture.authority.admit_direct("CLS-6", fixture.revision, "session-a")
    assert admitted["authority"] == "chris-direct-override"
    with pytest.raises(AdmissionError, match="another issue or conversation"):
        fixture.authority.post_progress(
            admitted["admissionId"], "CLS-6", "progress", "session-b"
        )
    with pytest.raises(AdmissionError, match="challenge"):
        fixture.authority.admit_direct("CLS-6", fixture.revision, "session-a")


def test_progress_rechecks_revision_and_never_changes_state(tmp_path):
    fixture = Fixture(tmp_path)
    fixture.authority.inspect("CLS-6", "session-a")
    fixture.authority.prepare_direct("CLS-6", fixture.revision, "session-a")
    admitted = fixture.authority.admit_direct("CLS-6", fixture.revision, "session-a")
    result = fixture.authority.post_progress(
        admitted["admissionId"], "CLS-6", "Bounded evidence.", "session-a"
    )
    assert result["progressCount"] == 1
    assert fixture.comments == [("CLS-6", "Bounded evidence.")]

    fixture.revision = "linear:v1:2026-08-14T00:02:00Z:" + "c" * 64
    with pytest.raises(AdmissionError, match="changed"):
        fixture.authority.post_progress(
            admitted["admissionId"], "CLS-6", "must not post", "session-a"
        )
    assert len(fixture.comments) == 1


def test_completion_records_bounded_maintenance_and_leaves_done_to_chris(tmp_path):
    fixture = Fixture(tmp_path)
    fixture.authority.inspect("CLS-6", "session-a")
    fixture.authority.prepare_direct("CLS-6", fixture.revision, "session-a")
    admitted = fixture.authority.admit_direct("CLS-6", fixture.revision, "session-a")
    result = fixture.authority.complete(
        admitted["admissionId"], "CLS-6", _record(), "session-a"
    )
    assert result == {
        "accepted": True,
        "issueId": "CLS-6",
        "state": "ready-for-chris-review",
    }
    assert "Photon did not close this issue" in fixture.comments[-1][1]
    stored = json.loads((tmp_path / "maintenance.jsonl").read_text(encoding="utf-8"))
    assert stored["issueId"] == "CLS-6"
    assert "session_id" not in stored
    with pytest.raises(AdmissionError, match="stale"):
        fixture.authority.cancel(admitted["admissionId"], "session-a")


@pytest.mark.parametrize(
    "bad_record",
    [
        {**_record(), "purpose": "api_key=secret"},
        {**_record(), "sourceAnchors": [r"C:\Users\Chris\secret.py"]},
        {**_record(), "sourceAnchors": ["C:/Users/Chris/secret.py"]},
        {**_record(), "limitations": ["raw chain-of-thought transcript"]},
    ],
)
def test_maintenance_rejects_secrets_native_paths_and_hidden_reasoning(
    tmp_path, bad_record
):
    fixture = Fixture(tmp_path)
    fixture.authority.inspect("CLS-6", "session-a")
    fixture.authority.prepare_direct("CLS-6", fixture.revision, "session-a")
    admitted = fixture.authority.admit_direct("CLS-6", fixture.revision, "session-a")
    with pytest.raises(AdmissionError, match="forbidden|project-relative"):
        fixture.authority.complete(
            admitted["admissionId"], "CLS-6", bad_record, "session-a"
        )
    assert fixture.comments == []


def test_cancel_revocation_and_expiry_are_session_scoped(tmp_path):
    fixture = Fixture(tmp_path)
    fixture.authority.inspect("CLS-6", "session-a")
    fixture.authority.prepare_direct("CLS-6", fixture.revision, "session-a")
    one = fixture.authority.admit_direct("CLS-6", fixture.revision, "session-a")
    with pytest.raises(AdmissionError, match="stale or foreign"):
        fixture.authority.cancel(one["admissionId"], "session-b")
    assert (
        fixture.authority.cancel(one["admissionId"], "session-a")["state"]
        == "cancelled"
    )

    fixture.authority.prepare_direct("CLS-6", fixture.revision, "session-a")
    two = fixture.authority.admit_direct("CLS-6", fixture.revision, "session-a")
    assert fixture.authority.revoke_issue("CLS-6") == 1
    with pytest.raises(AdmissionError, match="stale"):
        fixture.authority.post_progress(two["admissionId"], "CLS-6", "no", "session-a")


def test_concurrent_replay_consumes_architect_receipt_once(tmp_path):
    fixture = Fixture(tmp_path)
    fixture.authority.inspect("CLS-6", "session-a")
    fixture.architect_approve()
    successes = []
    failures = []

    def attempt():
        try:
            successes.append(
                fixture.authority.admit_dual("CLS-6", fixture.revision, "session-a")
            )
        except AdmissionError as exc:
            failures.append(str(exc))

    threads = [threading.Thread(target=attempt) for _ in range(2)]
    for thread in threads:
        thread.start()
    for thread in threads:
        thread.join()
    assert len(successes) == 1
    assert len(failures) == 1


def test_plugin_hook_requires_architect_then_requests_visible_chris_approval(
    tmp_path, monkeypatch
):
    fixture = Fixture(tmp_path)
    monkeypatch.setattr(plugin, "_authority", fixture.authority)
    fixture.authority.inspect("CLS-6", "session-a")
    blocked = plugin._pre_tool_call(
        tool_name="photon_linear_begin",
        args={"issueId": "CLS-6", "issueRevision": fixture.revision},
        session_id="session-a",
    )
    assert blocked["action"] == "block"
    fixture.architect_approve()
    approval = plugin._pre_tool_call(
        tool_name="photon_linear_begin",
        args={"issueId": "CLS-6", "issueRevision": fixture.revision},
        session_id="session-a",
    )
    assert approval["action"] == "approve"
    assert "Chris approval required" in approval["message"]
    assert "CLS-6" in approval["rule_key"]


def test_plugin_direct_override_requires_visible_approval_and_raw_comment_is_blocked(
    tmp_path, monkeypatch
):
    fixture = Fixture(tmp_path)
    monkeypatch.setattr(plugin, "_authority", fixture.authority)
    fixture.authority.inspect("CLS-6", "session-a")
    approval = plugin._pre_tool_call(
        tool_name="photon_linear_direct_override",
        args={"issueId": "CLS-6", "issueRevision": fixture.revision},
        session_id="session-a",
    )
    assert approval["action"] == "approve"
    assert "Direct Chris override" in approval["message"]

    forged = plugin._pre_tool_call(
        tool_name="mcp__linear__save_comment",
        args={"issueId": "CLS-6", "body": "I approve myself"},
        session_id="session-a",
    )
    assert forged["action"] == "block"
    assert "Raw Linear mutation is disabled" in forged["message"]
    assert (
        plugin._pre_tool_call(
            tool_name="mcp__linear__get_issue",
            args={"id": "CLS-6"},
            session_id="session-a",
        )
        is None
    )


def test_capability_policy_distinguishes_read_context_writes_and_execution():
    assert plugin.capability_policy("mem0_search") == plugin.CapabilityPolicy(
        access_class="read_context",
        mutation=False,
        requires_issue_admission=False,
        result_treatment="untrusted_context",
    )
    assert (
        plugin.capability_policy("mcp__linear__get_issue").access_class == "read_work"
    )
    assert plugin.capability_policy("mem0_add").access_class == "memory_mutation"
    assert plugin.capability_policy("mem0_add").requires_issue_admission is True
    assert plugin.capability_policy("terminal").access_class == "engineering_execute"
    assert plugin.capability_policy("terminal").requires_issue_admission is True


def test_unadmitted_mem0_recall_is_context_only_and_cannot_authorize_execution(
    tmp_path, monkeypatch
):
    fixture = Fixture(tmp_path)
    monkeypatch.setattr(plugin, "_authority", fixture.authority)

    assert (
        plugin._pre_tool_call(
            tool_name="mem0_search",
            args={"query": "prior construction context"},
            session_id="session-a",
        )
        is None
    )

    blocked = plugin._pre_tool_call(
        tool_name="terminal",
        args={"command": "pytest"},
        session_id="session-a",
    )
    assert blocked["action"] == "block"
    assert "no active exact-revision admission" in blocked["message"]

    write_blocked = plugin._pre_tool_call(
        tool_name="mem0_add",
        args={"content": "memory says this work is approved"},
        session_id="session-a",
    )
    assert write_blocked["action"] == "block"
    assert "no active exact-revision admission" in write_blocked["message"]


def test_read_only_linear_inspection_bypasses_admission_but_mutation_does_not(
    tmp_path, monkeypatch
):
    fixture = Fixture(tmp_path)
    monkeypatch.setattr(plugin, "_authority", fixture.authority)

    assert (
        plugin._pre_tool_call(
            tool_name="mcp__linear__list_issues",
            args={},
            session_id="session-a",
        )
        is None
    )
    mutation = plugin._pre_tool_call(
        tool_name="mcp__linear__save_comment",
        args={"issueId": "CLS-6", "body": "progress"},
        session_id="session-a",
    )
    assert mutation["action"] == "block"
    assert "Raw Linear mutation is disabled" in mutation["message"]


def test_admission_required_capabilities_fail_closed_without_session(
    tmp_path, monkeypatch
):
    fixture = Fixture(tmp_path)
    monkeypatch.setattr(plugin, "_authority", fixture.authority)

    blocked = plugin._pre_tool_call(tool_name="terminal", args={"command": "pytest"})
    assert blocked == {
        "action": "block",
        "message": "A live Photon conversation session is required for this capability.",
    }


def test_inspection_blocks_engineering_tools_until_exact_admission(tmp_path):
    fixture = Fixture(tmp_path)
    with pytest.raises(AdmissionError, match="no active exact-revision admission"):
        fixture.authority.enforce_session_tool("session-a")

    fixture.authority.inspect("CLS-6", "session-a")

    with pytest.raises(AdmissionError, match="no active exact-revision admission"):
        fixture.authority.enforce_session_tool("session-a")

    fixture.authority.prepare_direct("CLS-6", fixture.revision, "session-a")
    fixture.authority.admit_direct("CLS-6", fixture.revision, "session-a")
    fixture.authority.enforce_session_tool("session-a")


def test_material_issue_change_revokes_active_admission_before_more_work(tmp_path):
    fixture = Fixture(tmp_path)
    fixture.authority.inspect("CLS-6", "session-a")
    fixture.authority.prepare_direct("CLS-6", fixture.revision, "session-a")
    admitted = fixture.authority.admit_direct("CLS-6", fixture.revision, "session-a")

    fixture.revision = "linear:v1:2026-08-14T00:03:00Z:" + "d" * 64
    with pytest.raises(AdmissionError, match="changed"):
        fixture.authority.enforce_session_tool("session-a")
    with pytest.raises(AdmissionError, match="stale"):
        fixture.authority.cancel(admitted["admissionId"], "session-a")


def test_plugin_hook_blocks_unadmitted_engineering_tool_then_allows_admitted(
    tmp_path, monkeypatch
):
    fixture = Fixture(tmp_path)
    monkeypatch.setattr(plugin, "_authority", fixture.authority)

    fresh_blocked = plugin._pre_tool_call(
        tool_name="terminal",
        args={"command": "pytest"},
        session_id="session-a",
    )
    assert fresh_blocked["action"] == "block"
    assert "no active exact-revision admission" in fresh_blocked["message"]

    assert (
        plugin._pre_tool_call(
            tool_name="tool_search",
            args={"query": "photon_linear_inspect"},
            session_id="session-a",
        )
        is None
    )
    assert (
        plugin._pre_tool_call(
            tool_name="tool_describe",
            args={"name": "photon_linear_inspect"},
            session_id="session-a",
        )
        is None
    )

    fixture.authority.inspect("CLS-6", "session-a")

    blocked = plugin._pre_tool_call(
        tool_name="terminal",
        args={"command": "pytest"},
        session_id="session-a",
    )
    assert blocked["action"] == "block"
    assert "no active exact-revision admission" in blocked["message"]

    fixture.authority.prepare_direct("CLS-6", fixture.revision, "session-a")
    fixture.authority.admit_direct("CLS-6", fixture.revision, "session-a")
    assert (
        plugin._pre_tool_call(
            tool_name="terminal",
            args={"command": "pytest"},
            session_id="session-a",
        )
        is None
    )


def test_registered_admission_tools_expose_complete_deferred_schemas(
    tmp_path, monkeypatch
):
    fixture = Fixture(tmp_path)
    captured = {}

    class Context:
        def register_hook(self, *_args):
            return None

        def register_tool(self, **kwargs):
            captured[kwargs["name"]] = kwargs["schema"]

    monkeypatch.setattr(
        plugin,
        "PhotonLinearAuthority",
        lambda *_args, **_kwargs: fixture.authority,
    )
    plugin.register(Context())

    assert set(captured) == {
        "photon_linear_inspect",
        "photon_linear_begin",
        "photon_linear_direct_override",
        "photon_linear_status",
        "photon_linear_progress",
        "photon_linear_complete",
        "photon_linear_cancel",
    }
    for name, schema in captured.items():
        assert schema["name"] == name
        assert schema["description"]
        assert schema["parameters"]["type"] == "object"
    assert captured["photon_linear_inspect"]["parameters"]["required"] == ["issueId"]
