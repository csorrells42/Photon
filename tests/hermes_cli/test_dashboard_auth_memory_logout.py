from __future__ import annotations

import threading
import time

import pytest

from hermes_cli.dashboard_auth import live_principals, ws_tickets


@pytest.fixture(autouse=True)
def _clean_registry():
    live_principals._reset_for_tests()
    ws_tickets._reset_for_tests()
    yield
    live_principals._reset_for_tests()
    ws_tickets._reset_for_tests()


def test_principal_key_binds_issuer_and_subject_without_exposing_either():
    first = live_principals.canonical_principal_key("Nous", "alice@example.test")
    same = live_principals.canonical_principal_key("nous", "alice@example.test")
    other_issuer = live_principals.canonical_principal_key("local", "alice@example.test")

    assert first == same
    assert first != other_issuer
    assert "alice" not in first
    assert "nous" not in first


def test_logout_closes_channels_and_waits_for_inflight_memory_operation():
    lease = live_principals.activate_principal("stub", "alice")
    entered = threading.Event()
    release = threading.Event()
    closed = threading.Event()
    revoked = threading.Event()

    live_principals.register_live_channel(
        lease.principal_key, lease.generation, closed.set,
    )

    def operation() -> None:
        with live_principals.memory_operation(lease.principal_key, lease.generation):
            entered.set()
            release.wait(2)

    worker = threading.Thread(target=operation)
    worker.start()
    assert entered.wait(1)

    def logout() -> None:
        assert live_principals.revoke_principal("stub", "alice") is True
        revoked.set()

    logout_thread = threading.Thread(target=logout)
    logout_thread.start()
    assert closed.wait(1)
    time.sleep(0.02)
    assert not revoked.is_set(), "logout returned before an in-flight commit drained"

    release.set()
    worker.join(1)
    logout_thread.join(1)
    assert revoked.is_set()
    assert not live_principals.lease_is_live(lease.principal_key, lease.generation)

    with pytest.raises(live_principals.PrincipalRevoked):
        live_principals.activate_principal("stub", "alice")
    replacement = live_principals.begin_principal_session("stub", "alice")
    assert replacement.generation == lease.generation + 1


def test_ticket_and_internal_credential_are_rejected_after_logout():
    ticket = ws_tickets.mint_ticket(user_id="alice", provider="stub")
    info = ws_tickets.consume_ticket(ticket)
    credential = ws_tickets.internal_ws_credential(
        user_id="alice",
        provider="stub",
        principal_key=info["principal_key"],
        principal_generation=info["principal_generation"],
    )

    assert live_principals.revoke_principal("stub", "alice") is True
    with pytest.raises(ws_tickets.TicketInvalid, match="fresh authentication"):
        ws_tickets.mint_ticket(user_id="alice", provider="stub")
    with pytest.raises(ws_tickets.TicketInvalid, match="revoked"):
        ws_tickets.consume_internal_credential(credential)
