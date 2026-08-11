"""Authenticated Workbench principal leases and logout revocation.

This module is deliberately process-local.  The dashboard auth provider is
the issuer of identity and every live WebSocket / PTY in this process holds a
lease for one canonical principal generation.  Explicit logout invalidates
that generation first, closes its live channels, then waits for memory
operations already inside the commit boundary to drain.

The canonical key is opaque and collision-safe.  It binds both the dashboard
auth provider (issuer) and that provider's subject; a raw ``user_id`` is never
used as Workbench memory authority.
"""

from __future__ import annotations

import contextlib
import hashlib
import json
import os
import threading
import time
import uuid
from dataclasses import dataclass
from typing import Callable, Iterator


WORKBENCH_MEMORY_MODE_ENV = "HERMES_WORKBENCH_AUTHENTICATED_MEM0"


def authenticated_memory_mode_enabled() -> bool:
    """Return whether the strict Workbench authenticated-memory mode is on."""
    return os.environ.get(WORKBENCH_MEMORY_MODE_ENV) == "1"


def canonical_principal_key(provider: str, subject: str) -> str:
    """Build a stable opaque key from the verified issuer and subject."""
    issuer = str(provider or "").strip().lower()
    sub = str(subject or "").strip()
    if not issuer or not sub:
        raise ValueError("verified provider and subject are required")
    payload = json.dumps(
        {"issuer": issuer, "subject": sub},
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
    ).encode("utf-8")
    # Persist only an irreversible stable identifier.  Provider subjects may
    # themselves be email addresses or other sensitive account identifiers;
    # base64 encoding would merely disguise them in session and vector-store
    # metadata.
    return f"workbench-principal:v1:{hashlib.sha256(payload).hexdigest()}"


@dataclass(frozen=True)
class PrincipalLease:
    principal_key: str
    generation: int


class PrincipalRevoked(RuntimeError):
    """The principal generation is no longer live."""


class _State:
    __slots__ = ("generation", "revoked", "operations", "closers")

    def __init__(self, generation: int) -> None:
        self.generation = generation
        self.revoked = False
        self.operations = 0
        self.closers: dict[str, Callable[[], None]] = {}


_condition = threading.Condition(threading.RLock())
_states: dict[str, _State] = {}


def activate_principal(provider: str, subject: str) -> PrincipalLease:
    """Return/create a live lease, but never undo an explicit logout."""
    key = canonical_principal_key(provider, subject)
    with _condition:
        state = _states.get(key)
        if state is None:
            state = _State(1)
            _states[key] = state
        elif state.revoked:
            raise PrincipalRevoked("fresh authentication is required after logout")
        return PrincipalLease(key, state.generation)


def begin_principal_session(provider: str, subject: str) -> PrincipalLease:
    """Start a generation only after an authentication flow succeeds."""
    key = canonical_principal_key(provider, subject)
    with _condition:
        state = _states.get(key)
        if state is None:
            state = _State(1)
            _states[key] = state
        elif state.revoked:
            state = _State(state.generation + 1)
            _states[key] = state
        return PrincipalLease(key, state.generation)


def lease_is_live(principal_key: str, generation: int) -> bool:
    if not principal_key or not generation:
        return False
    with _condition:
        state = _states.get(principal_key)
        return bool(
            state
            and not state.revoked
            and state.generation == int(generation)
        )


def require_live(principal_key: str, generation: int) -> None:
    if not lease_is_live(principal_key, generation):
        raise PrincipalRevoked("authenticated memory session is no longer active")


@contextlib.contextmanager
def memory_operation(principal_key: str, generation: int) -> Iterator[None]:
    """Hold an operation lease through the backend's commit boundary."""
    with _condition:
        state = _states.get(principal_key)
        if (
            state is None
            or state.revoked
            or state.generation != int(generation)
        ):
            raise PrincipalRevoked("authenticated memory session is no longer active")
        state.operations += 1
    try:
        yield
    finally:
        with _condition:
            state.operations = max(0, state.operations - 1)
            _condition.notify_all()


def register_live_channel(
    principal_key: str,
    generation: int,
    close: Callable[[], None],
) -> str:
    """Register a server-side channel closer under a live generation."""
    if not callable(close):
        raise TypeError("close callback is required")
    token = uuid.uuid4().hex
    with _condition:
        state = _states.get(principal_key)
        if (
            state is None
            or state.revoked
            or state.generation != int(generation)
        ):
            raise PrincipalRevoked("authenticated memory session is no longer active")
        state.closers[token] = close
    return token


def unregister_live_channel(principal_key: str, generation: int, token: str) -> None:
    with _condition:
        state = _states.get(principal_key)
        if state is not None and state.generation == int(generation):
            state.closers.pop(token, None)


def revoke_principal(
    provider: str,
    subject: str,
    *,
    drain_timeout: float | None = None,
) -> bool:
    """Invalidate, close, and drain one principal generation.

    ``None`` means wait until every memory operation that entered before
    revocation has left its backend commit boundary.  A finite timeout is
    intended for tests/shutdown only; callers enforcing logout should leave it
    as ``None`` so returning is proof no old-generation commit remains active.
    """
    key = canonical_principal_key(provider, subject)
    with _condition:
        state = _states.get(key)
        if state is None:
            return False
        state.revoked = True
        closers = list(state.closers.values())
        state.closers.clear()

    for close in closers:
        try:
            close()
        except Exception:
            # Revocation cannot be undone because one channel was already gone.
            pass

    deadline = None if drain_timeout is None else time.monotonic() + max(0.0, drain_timeout)
    with _condition:
        while state.operations:
            if deadline is None:
                _condition.wait()
                continue
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                return False
            _condition.wait(remaining)
    return True


def principal_fingerprint(principal_key: str) -> str:
    """Bounded non-reversible label suitable for diagnostics (never identity)."""
    return hashlib.sha256(principal_key.encode("utf-8")).hexdigest()[:12]


def _reset_for_tests() -> None:
    with _condition:
        _states.clear()
        _condition.notify_all()
