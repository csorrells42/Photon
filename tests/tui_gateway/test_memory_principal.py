from __future__ import annotations

from hermes_cli.dashboard_auth import live_principals
from tui_gateway import server
from tui_gateway.transport import bind_transport, reset_transport


class _Transport:
    def __init__(self, principal_key: str, generation: int):
        self.principal_key = principal_key
        self.principal_generation = generation


def test_live_session_lookup_is_bound_to_authenticated_transport(monkeypatch):
    monkeypatch.setenv("HERMES_WORKBENCH_AUTHENTICATED_MEM0", "1")
    live_principals._reset_for_tests()
    alice = live_principals.activate_principal("stub", "alice")
    bob = live_principals.activate_principal("stub", "bob")
    sid = "owned-session"
    previous = server._sessions.get(sid)
    server._sessions[sid] = {
        "memory_principal_id": alice.principal_key,
        "memory_principal_generation": alice.generation,
    }
    try:
        token = bind_transport(_Transport(bob.principal_key, bob.generation))
        try:
            session, error = server._sess_nowait({"session_id": sid}, "rid")
            assert session is None
            assert error["error"]["message"] == "session not found"
        finally:
            reset_transport(token)

        token = bind_transport(_Transport(alice.principal_key, alice.generation))
        try:
            session, error = server._sess_nowait({"session_id": sid}, "rid")
            assert session is server._sessions[sid]
            assert error is None
        finally:
            reset_transport(token)
    finally:
        if previous is None:
            server._sessions.pop(sid, None)
        else:
            server._sessions[sid] = previous
        live_principals._reset_for_tests()


def test_persisted_session_owner_uses_canonical_principal_not_subject(monkeypatch):
    monkeypatch.setenv("HERMES_WORKBENCH_AUTHENTICATED_MEM0", "1")
    live_principals._reset_for_tests()
    alice = live_principals.activate_principal("stub", "alice")

    assert server._principal_may_access_session(
        {"user_id": alice.principal_key},
        (alice.principal_key, alice.generation),
    )
    assert not server._principal_may_access_session(
        {"user_id": "alice"},
        (alice.principal_key, alice.generation),
    )
    live_principals._reset_for_tests()
