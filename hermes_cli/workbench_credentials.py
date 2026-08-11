"""Authenticated, ephemeral Workbench credential reverse channel.

The Windows desktop opens this channel through Hermes' existing loopback-only
WebSocket listener.  A one-time bootstrap is delivered separately through
Docker stdin into tmpfs; it is never accepted through argv, environment, URL,
or durable Hermes state.  This module intentionally defines no route.  The
Workbench host must register :func:`accept_reverse_channel` on its authenticated
native-only WebSocket seam after verifying the exact container and image.
"""

from __future__ import annotations

import asyncio
import base64
import hashlib
import hmac
import ipaddress
import json
import os
import re
import secrets
import stat
import struct
import threading
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Dict, Mapping, Optional, Protocol

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.hazmat.primitives.kdf.hkdf import HKDF


PROTOCOL_VERSION = 2
BOOTSTRAP_PATH = Path("/run/photon-credentials/bootstrap.bin")
PROFILE_PATH = Path("/run/photon-credentials/profile.json")
MAX_HANDSHAKE_BYTES = 16 * 1024
MAX_FRAME_BYTES = 96 * 1024
MAX_SESSION_SECONDS = 15 * 60
MAX_LEASE_SECONDS = 60
MAX_REQUESTS_PER_SESSION = 2048

_SESSION_RE = re.compile(r"^hcs2_[A-Za-z0-9_-]{43}$")
_CONNECTION_RE = re.compile(r"^hcv2_[A-Za-z0-9_-]{43}$")
_CONTAINER_RE = re.compile(r"^[a-f0-9]{64}$")
_IMAGE_RE = re.compile(r"^sha256:[a-f0-9]{64}$")
_IDENTIFIER_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._:-]*$")
_SID_RE = re.compile(r"^S-[0-9-]{5,184}$")
_PROFILE_SCHEMA = "photon.workbench.credential-profile/v1"
_DIRECT_ENV_PURPOSES = {
    "OPENROUTER_API_KEY": "model:openrouter",
    "OPENAI_API_KEY": "model:openai",
    "ANTHROPIC_API_KEY": "model:anthropic",
    "GOOGLE_API_KEY": "model:google",
    "DEEPSEEK_API_KEY": "model:deepseek",
    "XAI_API_KEY": "model:xai",
}


class WorkbenchCredentialError(RuntimeError):
    """Safe, machine-readable reverse-channel failure."""

    def __init__(self, code: str, message: str, *, retryable: bool = False):
        super().__init__(message)
        self.code = _identifier(code, 64, "code")
        self.retryable = bool(retryable)


class ReverseChannelSocket(Protocol):
    async def receive(self) -> Mapping[str, Any]: ...
    async def send_text(self, data: str) -> None: ...
    async def send_bytes(self, data: bytes) -> None: ...
    async def close(self, code: int = 1000, reason: str = "") -> None: ...


@dataclass(frozen=True)
class RuntimeBinding:
    container_id: str
    image_digest: str
    user_sid: str
    machine_id: str
    install_id: str
    profile_id: str
    expires_at_unix: int

    def validate(self, now: Optional[int] = None) -> "RuntimeBinding":
        current = int(time.time()) if now is None else int(now)
        if not _CONTAINER_RE.fullmatch(self.container_id):
            raise WorkbenchCredentialError("invalid_container_id", "The runtime container identity is invalid.")
        if not _IMAGE_RE.fullmatch(self.image_digest):
            raise WorkbenchCredentialError("invalid_image_digest", "The runtime image identity is invalid.")
        if not _SID_RE.fullmatch(self.user_sid):
            raise WorkbenchCredentialError("invalid_principal", "The Windows principal is invalid.")
        _identifier(self.machine_id, 128, "machine_id")
        _identifier(self.install_id, 128, "install_id")
        _identifier(self.profile_id, 128, "profile_id")
        remaining = self.expires_at_unix - current
        if remaining <= 0 or remaining > MAX_SESSION_SECONDS:
            raise WorkbenchCredentialError("invalid_session_lifetime", "The credential session lifetime is invalid.")
        return self


@dataclass
class RuntimeBootstrap:
    session_id: str
    binding: RuntimeBinding
    host_public_key: bytearray
    bootstrap_key: bytearray

    def close(self) -> None:
        _zero(self.host_public_key)
        _zero(self.bootstrap_key)


@dataclass(frozen=True)
class LeaseReference:
    connection_ref: str
    purpose: str
    revision: int

    def validate(self) -> "LeaseReference":
        if not _CONNECTION_RE.fullmatch(self.connection_ref):
            raise WorkbenchCredentialError("invalid_reference", "The credential reference is invalid.")
        _purpose(self.purpose)
        if not isinstance(self.revision, int) or isinstance(self.revision, bool) or self.revision <= 0:
            raise WorkbenchCredentialError("invalid_revision", "A positive credential revision is required.")
        return self


class SecretLease:
    """A short-lived plaintext lease whose mutable bytes are wiped on close."""

    def __init__(self, reference: LeaseReference, expires_at_unix: int, value: bytearray):
        self.reference = reference.validate()
        self.expires_at_unix = int(expires_at_unix)
        self._value: Optional[bytearray] = value
        if not value or len(value) > 64 * 1024:
            self.close()
            raise WorkbenchCredentialError("invalid_secret_size", "The credential value has an invalid size.")

    @property
    def value(self) -> memoryview:
        if self._value is None:
            raise WorkbenchCredentialError("lease_closed", "The credential lease is closed.")
        return memoryview(self._value)

    def text(self) -> str:
        """Decode a provider key for the legacy SecretSource string contract.

        The returned immutable Python string cannot be wiped. Callers using the
        native runtime path should consume :attr:`value` directly instead.
        """
        try:
            return self.value.tobytes().decode("utf-8", errors="strict")
        except UnicodeDecodeError as exc:
            raise WorkbenchCredentialError("invalid_secret_encoding", "The credential value is not valid UTF-8.") from exc

    def close(self) -> None:
        value, self._value = self._value, None
        if value is not None:
            _zero(value)

    def __enter__(self) -> "SecretLease":
        return self

    def __exit__(self, *_args: object) -> None:
        self.close()


class _DirectionalCipher:
    def __init__(self, session_id: str, host_to_container: bytes, container_to_host: bytes):
        self._session_id = session_id
        self._host_to_container = bytearray(host_to_container)
        self._container_to_host = bytearray(container_to_host)
        self._send_sequence = 0
        self._receive_sequence = 0
        self._closed = False

    def encrypt_request(self, plaintext: bytearray) -> bytes:
        self._require_open()
        self._send_sequence += 1
        return self._encrypt(plaintext, self._container_to_host, 1, self._send_sequence)

    def decrypt_response(self, frame: bytes) -> bytearray:
        self._require_open()
        if len(frame) < 30 or frame[:4] != b"HCF2" or frame[4] != PROTOCOL_VERSION or frame[5] != 2:
            raise WorkbenchCredentialError("invalid_frame", "The credential channel frame is invalid.")
        sequence = struct.unpack(">Q", frame[6:14])[0]
        if sequence != self._receive_sequence + 1:
            raise WorkbenchCredentialError("frame_replay", "The credential channel frame sequence is invalid.")
        plaintext = self._decrypt(frame, self._host_to_container, 2, sequence)
        self._receive_sequence = sequence
        return plaintext

    def _encrypt(self, plaintext: bytearray, key: bytearray, direction: int, sequence: int) -> bytes:
        if not plaintext or len(plaintext) > MAX_FRAME_BYTES - 30:
            raise WorkbenchCredentialError("invalid_plaintext", "The credential channel payload has an invalid size.")
        nonce = _nonce(direction, sequence)
        aad = _frame_aad(self._session_id, direction, sequence)
        ciphertext = AESGCM(bytes(key)).encrypt(nonce, bytes(plaintext), aad)
        return b"HCF2" + bytes((PROTOCOL_VERSION, direction)) + struct.pack(">Q", sequence) + ciphertext

    def _decrypt(self, frame: bytes, key: bytearray, direction: int, sequence: int) -> bytearray:
        if len(frame) > MAX_FRAME_BYTES:
            raise WorkbenchCredentialError("frame_too_large", "The credential channel frame is too large.")
        nonce = _nonce(direction, sequence)
        aad = _frame_aad(self._session_id, direction, sequence)
        try:
            return bytearray(AESGCM(bytes(key)).decrypt(nonce, frame[14:], aad))
        except Exception as exc:
            raise WorkbenchCredentialError("frame_auth_failed", "The credential channel frame authentication failed.") from exc

    def _require_open(self) -> None:
        if self._closed:
            raise WorkbenchCredentialError("session_closed", "The credential session is closed.")

    def close(self) -> None:
        if self._closed:
            return
        self._closed = True
        _zero(self._host_to_container)
        _zero(self._container_to_host)


class WorkbenchCredentialSession:
    """One authenticated desktop-to-container reverse channel."""

    def __init__(
        self,
        socket: ReverseChannelSocket,
        bootstrap: RuntimeBootstrap,
        cipher: _DirectionalCipher,
        loop: asyncio.AbstractEventLoop,
        scope_config: Optional[Dict[str, Any]] = None,
    ):
        self._socket = socket
        self._bootstrap = bootstrap
        self._cipher = cipher
        self._loop = loop
        self._lock = asyncio.Lock()
        self._request_count = 0
        self._closed = False
        self._closed_event = asyncio.Event()
        self._scope_config = scope_config

    @property
    def profile_id(self) -> str:
        return self._bootstrap.binding.profile_id

    @property
    def session_id(self) -> str:
        return self._bootstrap.session_id

    async def resolve(self, reference: LeaseReference) -> SecretLease:
        reference.validate()
        self._require_live()
        async with self._lock:
            self._require_live()
            self._request_count += 1
            if self._request_count > MAX_REQUESTS_PER_SESSION:
                await self.close(code=1008, reason="credential request limit reached")
                raise WorkbenchCredentialError("session_request_limit", "The credential session request limit was reached.")
            request_id = f"lease-{secrets.token_urlsafe(18)}"
            request_nonce = bytearray(secrets.token_bytes(32))
            plaintext: Optional[bytearray] = None
            response_plaintext: Optional[bytearray] = None
            try:
                plaintext = _encode_request(
                    request_id,
                    reference,
                    self._bootstrap.binding.profile_id,
                    request_nonce,
                )
                encrypted = self._cipher.encrypt_request(plaintext)
                await self._socket.send_bytes(encrypted)
                message = await self._socket.receive()
                frame = _binary_message(message)
                response_plaintext = self._cipher.decrypt_response(frame)
                return _decode_response(
                    response_plaintext,
                    request_id=request_id,
                    request_nonce=request_nonce,
                    reference=reference,
                    session_expires_at=self._bootstrap.binding.expires_at_unix,
                )
            except BaseException:
                await self.close(code=1011, reason="credential channel failed")
                raise
            finally:
                _zero(request_nonce)
                if plaintext is not None:
                    _zero(plaintext)
                if response_plaintext is not None:
                    _zero(response_plaintext)

    def resolve_blocking(self, reference: LeaseReference, timeout_seconds: float) -> SecretLease:
        self._require_live()
        if timeout_seconds <= 0 or timeout_seconds > 120:
            raise WorkbenchCredentialError("invalid_timeout", "The credential resolution timeout is invalid.")
        try:
            running = asyncio.get_running_loop()
        except RuntimeError:
            running = None
        if running is self._loop:
            raise WorkbenchCredentialError("event_loop_deadlock", "Synchronous credential resolution cannot run on the channel event loop.")
        future = asyncio.run_coroutine_threadsafe(self.resolve(reference), self._loop)
        try:
            return future.result(timeout=timeout_seconds)
        except TimeoutError as exc:
            future.cancel()
            raise WorkbenchCredentialError("resolve_timeout", "The native credential request timed out.", retryable=True) from exc

    async def close(self, code: int = 1000, reason: str = "credential session closed") -> None:
        if self._closed:
            return
        self._closed = True
        self._scope_config = None
        self._cipher.close()
        self._bootstrap.close()
        try:
            await self._socket.close(code=code, reason=reason[:120])
        except Exception:
            pass
        _remove_active_session(self)
        self._closed_event.set()

    async def wait_closed(self) -> None:
        await self._closed_event.wait()

    def scope_config(self) -> Dict[str, Any]:
        self._require_live()
        if self._scope_config is None:
            raise WorkbenchCredentialError("scope_unavailable", "The native credential profile scope is unavailable.")
        return {
            "enabled": True,
            "profile_id": self.profile_id,
            "timeout_seconds": 10.0,
            "env": {name: dict(reference) for name, reference in self._scope_config["env"].items()},
        }

    def _require_live(self) -> None:
        if self._closed or int(time.time()) >= self._bootstrap.binding.expires_at_unix:
            raise WorkbenchCredentialError("session_unavailable", "The native credential session is unavailable.", retryable=True)


_sessions_lock = threading.Lock()
_sessions: Dict[str, WorkbenchCredentialSession] = {}


def _remove_active_session(session: WorkbenchCredentialSession) -> None:
    with _sessions_lock:
        if _sessions.get(session.profile_id) is session:
            _sessions.pop(session.profile_id, None)


async def accept_reverse_channel(
    socket: ReverseChannelSocket,
    *,
    peer_host: str,
    session_id: str,
    bootstrap_path: Path = BOOTSTRAP_PATH,
    profile_path: Optional[Path] = None,
    now: Optional[int] = None,
    server_private_key: Optional[ec.EllipticCurvePrivateKey] = None,
    server_nonce: Optional[bytes] = None,
) -> WorkbenchCredentialSession:
    """Authenticate one native reverse channel and make it active by profile."""
    if not isinstance(session_id, str) or not _SESSION_RE.fullmatch(session_id):
        try:
            await socket.close(code=1008, reason="credential session rejected")
        except Exception:
            pass
        raise WorkbenchCredentialError("invalid_session_id", "The credential session identity is invalid.")
    try:
        if not ipaddress.ip_address(peer_host).is_loopback:
            raise ValueError("not loopback")
    except ValueError as exc:
        try:
            await socket.close(code=1008, reason="credential channel requires loopback")
        except Exception:
            pass
        raise WorkbenchCredentialError("non_loopback_peer", "The credential channel requires a loopback peer.") from exc
    try:
        bootstrap = load_bootstrap_once(bootstrap_path, now=now)
    except Exception:
        try:
            await socket.close(code=1008, reason="credential bootstrap rejected")
        except Exception:
            pass
        raise
    cipher: Optional[_DirectionalCipher] = None
    try:
        if not hmac.compare_digest(bootstrap.session_id, session_id):
            raise WorkbenchCredentialError("session_mismatch", "The credential session does not match its bootstrap.")
        message = await socket.receive()
        raw_hello = _text_message(message)
        if len(raw_hello.encode("utf-8")) > MAX_HANDSHAKE_BYTES:
            raise WorkbenchCredentialError("host_hello_too_large", "The host handshake is too large.")
        hello = _strict_json_object(raw_hello, {
            "version", "type", "sessionId", "hostPublicKey", "hostNonce", "proof",
        })
        if hello.get("version") != PROTOCOL_VERSION or hello.get("type") != "host-hello" or hello.get("sessionId") != bootstrap.session_id:
            raise WorkbenchCredentialError("handshake_mismatch", "The host handshake does not match this session.")
        host_public = bytearray(_b64url_decode(hello.get("hostPublicKey"), 512))
        host_nonce = bytearray(_b64url_decode(hello.get("hostNonce"), 32))
        supplied_proof = bytearray(_b64url_decode(hello.get("proof"), 32))
        transcript: Optional[bytearray] = None
        server_transcript: Optional[bytearray] = None
        expected_proof: Optional[bytearray] = None
        server_proof: Optional[bytearray] = None
        shared_secret: Optional[bytearray] = None
        salt_input: Optional[bytearray] = None
        salt: Optional[bytearray] = None
        key_material: Optional[bytearray] = None
        server_public = bytearray()
        actual_server_nonce = bytearray(server_nonce or secrets.token_bytes(32))
        private_key = server_private_key or ec.generate_private_key(ec.SECP256R1())
        try:
            if host_public != bootstrap.host_public_key or len(host_nonce) != 32 or not any(host_nonce):
                raise WorkbenchCredentialError("host_binding_mismatch", "The host handshake binding changed.")
            transcript = _transcript("host-hello", bootstrap, host_public, host_nonce, b"", b"")
            expected_proof = bytearray(hmac.digest(bootstrap.bootstrap_key, transcript, "sha256"))
            if not hmac.compare_digest(expected_proof, supplied_proof):
                raise WorkbenchCredentialError("handshake_auth_failed", "The host handshake authentication failed.")
            host_key = serialization.load_der_public_key(bytes(host_public))
            if not isinstance(host_key, ec.EllipticCurvePublicKey) or not isinstance(host_key.curve, ec.SECP256R1):
                raise WorkbenchCredentialError("invalid_host_key", "The host handshake key is invalid.")
            server_public.extend(private_key.public_key().public_bytes(
                serialization.Encoding.DER,
                serialization.PublicFormat.SubjectPublicKeyInfo,
            ))
            server_transcript = _transcript(
                "server-hello", bootstrap, host_public, host_nonce, server_public, actual_server_nonce,
            )
            server_proof = bytearray(hmac.digest(bootstrap.bootstrap_key, server_transcript, "sha256"))
            response = {
                "version": PROTOCOL_VERSION,
                "type": "server-hello",
                "sessionId": bootstrap.session_id,
                "containerId": bootstrap.binding.container_id,
                "imageDigest": bootstrap.binding.image_digest,
                "userSid": bootstrap.binding.user_sid,
                "machineId": bootstrap.binding.machine_id,
                "installId": bootstrap.binding.install_id,
                "profileId": bootstrap.binding.profile_id,
                "expiresAtUnix": bootstrap.binding.expires_at_unix,
                "serverPublicKey": _b64url(server_public),
                "serverNonce": _b64url(actual_server_nonce),
                "proof": _b64url(server_proof),
            }
            await socket.send_text(json.dumps(response, separators=(",", ":"), sort_keys=True))
            shared_secret = bytearray(private_key.exchange(ec.ECDH(), host_key))
            salt_input = bytearray(host_nonce + actual_server_nonce)
            salt = bytearray(hmac.digest(bootstrap.bootstrap_key, salt_input, "sha256"))
            key_material = bytearray(HKDF(
                algorithm=hashes.SHA256(),
                length=64,
                salt=bytes(salt),
                info=hashlib.sha256(server_transcript).digest(),
            ).derive(bytes(shared_secret)))
            cipher = _DirectionalCipher(bootstrap.session_id, key_material[:32], key_material[32:])
        finally:
            for value in (host_public, host_nonce, supplied_proof, transcript, expected_proof,
                          server_transcript, server_proof, shared_secret, salt_input, salt,
                          key_material, server_public, actual_server_nonce):
                if value is not None:
                    _zero(value)
        scope_config = load_profile_once(profile_path, bootstrap) if profile_path is not None else None
        session = WorkbenchCredentialSession(socket, bootstrap, cipher, asyncio.get_running_loop(), scope_config)
        cipher = None
        with _sessions_lock:
            previous = _sessions.get(session.profile_id)
            _sessions[session.profile_id] = session
        if previous is not None and previous is not session:
            await previous.close(code=1008, reason="credential session replaced")
        return session
    except Exception:
        if cipher is not None:
            cipher.close()
        bootstrap.close()
        try:
            await socket.close(code=1008, reason="credential handshake rejected")
        except Exception:
            pass
        raise


def resolve_lease_blocking(profile_id: str, reference: LeaseReference, timeout_seconds: float = 10.0) -> SecretLease:
    profile = _identifier(profile_id, 128, "profile_id")
    with _sessions_lock:
        session = _sessions.get(profile)
    if session is None:
        raise WorkbenchCredentialError("session_unavailable", "The native credential session is unavailable.", retryable=True)
    return session.resolve_blocking(reference, timeout_seconds)


def resolve_profile_secret_scope(profile_id: str) -> Dict[str, str]:
    """Resolve the authenticated native overlay for one exact profile.

    No active reverse channel means no native overlay, preserving standalone
    Hermes and the compatibility `.env` path.  Once a channel is active its
    mapping is atomic: any denied or stale lease fails the whole scope.
    """
    profile = _identifier(profile_id, 128, "profile_id")
    with _sessions_lock:
        session = _sessions.get(profile)
    if session is None:
        return {}
    from agent.secret_sources.workbench import WorkbenchSecretSource
    return WorkbenchSecretSource().resolve_scope(session.scope_config())


async def revoke_profile_session(profile_id: str) -> None:
    profile = _identifier(profile_id, 128, "profile_id")
    with _sessions_lock:
        session = _sessions.pop(profile, None)
    if session is not None:
        await session.close(code=1008, reason="credential session revoked")


async def revoke_all_sessions() -> None:
    with _sessions_lock:
        sessions = list(_sessions.values())
        _sessions.clear()
    for session in sessions:
        await session.close(code=1008, reason="credential sessions revoked")


def load_profile_once(path: Path, bootstrap: RuntimeBootstrap) -> Dict[str, Any]:
    """Read and unlink one owner-only, session-bound opaque profile map."""
    candidate = Path(path)
    if not candidate.is_absolute():
        raise WorkbenchCredentialError("profile_path_invalid", "The credential profile path must be absolute.")
    flags = (os.O_RDONLY | getattr(os, "O_BINARY", 0) | getattr(os, "O_CLOEXEC", 0)
             | getattr(os, "O_NOFOLLOW", 0))
    try:
        before = os.lstat(candidate)
        if not stat.S_ISREG(before.st_mode) or before.st_nlink != 1:
            raise WorkbenchCredentialError("profile_file_invalid", "The credential profile must be a single regular file.")
        expected_uid = os.geteuid() if hasattr(os, "geteuid") else before.st_uid
        if before.st_uid != expected_uid or (os.name == "posix" and stat.S_IMODE(before.st_mode) & 0o077):
            raise WorkbenchCredentialError("profile_permissions", "The credential profile permissions are invalid.")
        if before.st_size <= 0 or before.st_size > 64 * 1024:
            raise WorkbenchCredentialError("profile_size", "The credential profile size is invalid.")
        fd = os.open(candidate, flags)
    except OSError as exc:
        raise WorkbenchCredentialError("profile_unavailable", "The credential profile is unavailable.") from exc
    raw = bytearray()
    descriptor_open = True
    try:
        opened = os.fstat(fd)
        if (opened.st_dev, opened.st_ino) != (before.st_dev, before.st_ino) or opened.st_nlink != 1:
            raise WorkbenchCredentialError("profile_identity_changed", "The credential profile identity changed.")
        if os.name == "posix":
            os.unlink(candidate)
        while len(raw) <= 64 * 1024:
            chunk = os.read(fd, min(4096, 64 * 1024 + 1 - len(raw)))
            if not chunk:
                break
            raw.extend(chunk)
        if not raw or len(raw) > 64 * 1024:
            raise WorkbenchCredentialError("profile_size", "The credential profile size is invalid.")
        if os.name != "posix":
            os.close(fd)
            descriptor_open = False
            after = os.lstat(candidate)
            if (after.st_dev, after.st_ino) != (before.st_dev, before.st_ino):
                raise WorkbenchCredentialError("profile_identity_changed", "The credential profile identity changed.")
            os.unlink(candidate)
        return _parse_profile(raw, bootstrap)
    finally:
        if descriptor_open:
            os.close(fd)
        _zero(raw)


def _parse_profile(raw: bytearray, bootstrap: RuntimeBootstrap) -> Dict[str, Any]:
    def reject_duplicates(pairs: list[tuple[str, Any]]) -> Dict[str, Any]:
        result: Dict[str, Any] = {}
        for key, value in pairs:
            if key in result:
                raise ValueError("duplicate profile field")
            result[key] = value
        return result

    try:
        value = json.loads(bytes(raw), object_pairs_hook=reject_duplicates)
    except (TypeError, ValueError, UnicodeDecodeError) as exc:
        raise WorkbenchCredentialError("profile_json_invalid", "The credential profile is invalid.") from exc
    required = {"schema", "sessionId", "containerId", "imageDigest", "profileId", "expiresAtUnix", "env"}
    if not isinstance(value, dict) or set(value) != required:
        raise WorkbenchCredentialError("profile_shape_invalid", "The credential profile shape is invalid.")
    binding = bootstrap.binding
    exact = (
        value.get("schema") == _PROFILE_SCHEMA
        and hmac.compare_digest(str(value.get("sessionId", "")), bootstrap.session_id)
        and hmac.compare_digest(str(value.get("containerId", "")), binding.container_id)
        and hmac.compare_digest(str(value.get("imageDigest", "")), binding.image_digest)
        and hmac.compare_digest(str(value.get("profileId", "")), binding.profile_id)
        and isinstance(value.get("expiresAtUnix"), int)
        and not isinstance(value.get("expiresAtUnix"), bool)
        and value.get("expiresAtUnix") == binding.expires_at_unix
    )
    if not exact:
        raise WorkbenchCredentialError("profile_binding_mismatch", "The credential profile binding changed.")
    raw_env = value.get("env")
    if not isinstance(raw_env, dict) or len(raw_env) > len(_DIRECT_ENV_PURPOSES):
        raise WorkbenchCredentialError("profile_mapping_invalid", "The credential profile mapping is invalid.")
    env: Dict[str, Dict[str, Any]] = {}
    for env_name, reference in raw_env.items():
        if env_name not in _DIRECT_ENV_PURPOSES or not isinstance(reference, dict):
            raise WorkbenchCredentialError("profile_mapping_invalid", "The credential profile mapping is invalid.")
        if set(reference) != {"connection_ref", "purpose", "revision"}:
            raise WorkbenchCredentialError("profile_mapping_invalid", "The credential profile mapping is invalid.")
        lease = LeaseReference(
            connection_ref=reference.get("connection_ref"),
            purpose=reference.get("purpose"),
            revision=reference.get("revision"),
        ).validate()
        if lease.purpose != _DIRECT_ENV_PURPOSES[env_name]:
            raise WorkbenchCredentialError("profile_mapping_invalid", "The credential profile mapping is invalid.")
        env[env_name] = {
            "connection_ref": lease.connection_ref,
            "purpose": lease.purpose,
            "revision": lease.revision,
        }
    return {"env": env}


def load_bootstrap_once(path: Path = BOOTSTRAP_PATH, *, now: Optional[int] = None) -> RuntimeBootstrap:
    """Read and unlink one owner-only regular bootstrap file exactly once."""
    candidate = Path(path)
    if not candidate.is_absolute():
        raise WorkbenchCredentialError("bootstrap_path_invalid", "The credential bootstrap path must be absolute.")
    flags = (os.O_RDONLY | getattr(os, "O_BINARY", 0) | getattr(os, "O_CLOEXEC", 0)
             | getattr(os, "O_NOFOLLOW", 0))
    try:
        before = os.lstat(candidate)
        if not stat.S_ISREG(before.st_mode) or before.st_nlink != 1:
            raise WorkbenchCredentialError("bootstrap_file_invalid", "The credential bootstrap must be a single regular file.")
        expected_uid = os.geteuid() if hasattr(os, "geteuid") else before.st_uid
        if before.st_uid != expected_uid or (os.name == "posix" and stat.S_IMODE(before.st_mode) & 0o077):
            raise WorkbenchCredentialError("bootstrap_permissions", "The credential bootstrap permissions are invalid.")
        if before.st_size <= 0 or before.st_size > MAX_HANDSHAKE_BYTES:
            raise WorkbenchCredentialError("bootstrap_size", "The credential bootstrap size is invalid.")
        fd = os.open(candidate, flags)
    except OSError as exc:
        raise WorkbenchCredentialError("bootstrap_unavailable", "The credential bootstrap is unavailable.") from exc
    raw = bytearray()
    descriptor_open = True
    try:
        opened = os.fstat(fd)
        if (opened.st_dev, opened.st_ino) != (before.st_dev, before.st_ino) or opened.st_nlink != 1:
            raise WorkbenchCredentialError("bootstrap_identity_changed", "The credential bootstrap identity changed.")
        if os.name == "posix":
            os.unlink(candidate)
        while len(raw) <= MAX_HANDSHAKE_BYTES:
            chunk = os.read(fd, min(4096, MAX_HANDSHAKE_BYTES + 1 - len(raw)))
            if not chunk:
                break
            raw.extend(chunk)
        if not raw or len(raw) > MAX_HANDSHAKE_BYTES:
            raise WorkbenchCredentialError("bootstrap_size", "The credential bootstrap size is invalid.")
        if os.name != "posix":
            os.close(fd)
            descriptor_open = False
            after = os.lstat(candidate)
            if (after.st_dev, after.st_ino) != (before.st_dev, before.st_ino):
                raise WorkbenchCredentialError("bootstrap_identity_changed", "The credential bootstrap identity changed.")
            os.unlink(candidate)
        return _parse_bootstrap(raw, now=now)
    finally:
        if descriptor_open:
            os.close(fd)
        _zero(raw)


def _parse_bootstrap(raw: bytearray, *, now: Optional[int]) -> RuntimeBootstrap:
    reader = _Reader(memoryview(raw))
    if reader.read_raw(4) != b"HCB2" or reader.read_i32() != PROTOCOL_VERSION:
        raise WorkbenchCredentialError("bootstrap_version", "The credential bootstrap version is invalid.")
    session_id = reader.read_text(64)
    if not _SESSION_RE.fullmatch(session_id):
        raise WorkbenchCredentialError("invalid_session_id", "The credential session identity is invalid.")
    binding = RuntimeBinding(
        container_id=reader.read_text(80),
        image_digest=reader.read_text(80),
        user_sid=reader.read_text(192),
        machine_id=reader.read_text(128),
        install_id=reader.read_text(128),
        profile_id=reader.read_text(128),
        expires_at_unix=reader.read_i64(),
    ).validate(now)
    host_public = bytearray(reader.read_bytes(512))
    bootstrap_key = bytearray(reader.read_bytes(32, exact=32))
    reader.require_end()
    if not any(bootstrap_key):
        _zero(host_public)
        _zero(bootstrap_key)
        raise WorkbenchCredentialError("invalid_bootstrap_key", "The credential bootstrap key is invalid.")
    try:
        key = serialization.load_der_public_key(bytes(host_public))
        if not isinstance(key, ec.EllipticCurvePublicKey) or not isinstance(key.curve, ec.SECP256R1):
            raise ValueError("wrong key type")
    except Exception as exc:
        _zero(host_public)
        _zero(bootstrap_key)
        raise WorkbenchCredentialError("invalid_host_key", "The credential host key is invalid.") from exc
    return RuntimeBootstrap(session_id, binding, host_public, bootstrap_key)


def _encode_request(request_id: str, reference: LeaseReference, profile_id: str, nonce: bytearray) -> bytearray:
    _identifier(request_id, 128, "request_id")
    reference.validate()
    _identifier(profile_id, 128, "profile_id")
    if len(nonce) != 32 or not any(nonce):
        raise WorkbenchCredentialError("invalid_request_nonce", "The credential request nonce is invalid.")
    output = bytearray(b"HCR2")
    _write_field(output, request_id.encode())
    _write_field(output, reference.connection_ref.encode())
    _write_field(output, profile_id.encode())
    _write_field(output, reference.purpose.encode())
    output.extend(struct.pack(">q", reference.revision))
    _write_field(output, nonce)
    return output


def _decode_response(
    payload: bytearray,
    *,
    request_id: str,
    request_nonce: bytearray,
    reference: LeaseReference,
    session_expires_at: int,
) -> SecretLease:
    reader = _Reader(memoryview(payload))
    if reader.read_raw(4) != b"HCS2":
        raise WorkbenchCredentialError("invalid_lease_response", "The credential lease response is invalid.")
    status = reader.read_u8()
    echoed_id = reader.read_text(128)
    echoed_nonce = bytearray(reader.read_bytes(32, exact=32))
    secret: Optional[bytearray] = None
    try:
        if not hmac.compare_digest(echoed_id.encode(), request_id.encode()) or not hmac.compare_digest(echoed_nonce, request_nonce):
            raise WorkbenchCredentialError("lease_response_mismatch", "The credential lease response does not match its request.")
        if status == 1:
            code = _identifier(reader.read_text(64), 64, "code")
            retryable = reader.read_u8()
            if retryable not in (0, 1):
                raise WorkbenchCredentialError("invalid_lease_response", "The credential lease response is invalid.")
            reader.require_end()
            raise WorkbenchCredentialError(code, "The native credential request was denied.", retryable=bool(retryable))
        if status != 0:
            raise WorkbenchCredentialError("invalid_lease_response", "The credential lease response is invalid.")
        connection_ref = reader.read_text(64)
        revision = reader.read_i64()
        expires_at = reader.read_i64()
        secret_length = reader.read_i32()
        if connection_ref != reference.connection_ref or revision != reference.revision:
            raise WorkbenchCredentialError("lease_response_mismatch", "The credential lease response binding changed.")
        current = int(time.time())
        if expires_at <= current or expires_at > min(session_expires_at, current + MAX_LEASE_SECONDS):
            raise WorkbenchCredentialError("invalid_lease_expiry", "The credential lease expiry is invalid.")
        if secret_length <= 0 or secret_length > 64 * 1024:
            raise WorkbenchCredentialError("invalid_secret_size", "The credential value has an invalid size.")
        secret = reader.read_mutable(secret_length)
        reader.require_end()
        lease = SecretLease(reference, expires_at, secret)
        secret = None
        return lease
    finally:
        _zero(echoed_nonce)
        if secret is not None:
            _zero(secret)


def _transcript(label: str, bootstrap: RuntimeBootstrap, host_public: bytes | bytearray,
                host_nonce: bytes | bytearray, server_public: bytes | bytearray,
                server_nonce: bytes | bytearray) -> bytearray:
    output = bytearray()
    for value in (
        b"hermes-credential-broker/v2",
        label.encode(),
        bootstrap.session_id.encode(),
        bootstrap.binding.container_id.encode(),
        bootstrap.binding.image_digest.encode(),
        bootstrap.binding.user_sid.encode(),
        bootstrap.binding.machine_id.encode(),
        bootstrap.binding.install_id.encode(),
        bootstrap.binding.profile_id.encode(),
    ):
        _write_field(output, value)
    output.extend(struct.pack(">q", bootstrap.binding.expires_at_unix))
    for value in (host_public, host_nonce, server_public, server_nonce):
        _write_field(output, value)
    return output


def _frame_aad(session_id: str, direction: int, sequence: int) -> bytes:
    output = bytearray()
    _write_field(output, b"hermes-credential-broker/v2/frame")
    _write_field(output, session_id.encode())
    output.append(direction)
    output.extend(struct.pack(">Q", sequence))
    return bytes(output)


def _nonce(direction: int, sequence: int) -> bytes:
    return (b"C2H\0" if direction == 1 else b"H2C\0") + struct.pack(">Q", sequence)


def _write_field(output: bytearray, value: bytes | bytearray | memoryview) -> None:
    if len(value) > 65535:
        raise WorkbenchCredentialError("field_too_large", "A credential protocol field is too large.")
    output.extend(struct.pack(">H", len(value)))
    output.extend(value)


def _identifier(value: Any, maximum: int, name: str) -> str:
    if not isinstance(value, str) or not value or len(value) > maximum or not _IDENTIFIER_RE.fullmatch(value):
        raise WorkbenchCredentialError("invalid_identifier", f"{name} is invalid.")
    return value


def _purpose(value: Any) -> str:
    purpose = _identifier(value, 160, "purpose")
    if not (purpose.startswith(("model:", "mcp:", "channel:")) or purpose in {"oauth-refresh", "usage"}):
        raise WorkbenchCredentialError("invalid_purpose", "The credential purpose is not recognized.")
    return purpose


def _b64url(value: bytes | bytearray) -> str:
    return base64.urlsafe_b64encode(bytes(value)).rstrip(b"=").decode("ascii")


def _b64url_decode(value: Any, maximum: int) -> bytes:
    if not isinstance(value, str) or not value or len(value) > ((maximum + 2) // 3) * 4 + 2:
        raise WorkbenchCredentialError("invalid_base64url", "A credential protocol field is invalid.")
    if any(not (character.isascii() and (character.isalnum() or character in "-_")) for character in value):
        raise WorkbenchCredentialError("invalid_base64url", "A credential protocol field is invalid.")
    try:
        decoded = base64.urlsafe_b64decode(value + "=" * ((4 - len(value) % 4) % 4))
    except Exception as exc:
        raise WorkbenchCredentialError("invalid_base64url", "A credential protocol field is invalid.") from exc
    if len(decoded) > maximum:
        raise WorkbenchCredentialError("invalid_base64url", "A credential protocol field is invalid.")
    return decoded


def _strict_json_object(raw: str, fields: set[str]) -> Dict[str, Any]:
    def reject_duplicates(pairs: list[tuple[str, Any]]) -> Dict[str, Any]:
        result: Dict[str, Any] = {}
        for key, value in pairs:
            if key in result:
                raise ValueError("duplicate handshake field")
            result[key] = value
        return result

    try:
        value = json.loads(raw, object_pairs_hook=reject_duplicates)
    except (TypeError, ValueError) as exc:
        raise WorkbenchCredentialError("invalid_handshake_json", "The credential handshake is invalid.") from exc
    if not isinstance(value, dict) or set(value) != fields:
        raise WorkbenchCredentialError("invalid_handshake_shape", "The credential handshake shape is invalid.")
    return value


def _text_message(message: Mapping[str, Any]) -> str:
    if message.get("type") != "websocket.receive" or not isinstance(message.get("text"), str) or message.get("bytes") is not None:
        raise WorkbenchCredentialError("expected_text_frame", "The credential handshake requires a text frame.")
    return str(message["text"])


def _binary_message(message: Mapping[str, Any]) -> bytes:
    value = message.get("bytes")
    if message.get("type") != "websocket.receive" or not isinstance(value, bytes) or message.get("text") is not None:
        raise WorkbenchCredentialError("expected_binary_frame", "The credential lease requires a binary frame.")
    if not value or len(value) > MAX_FRAME_BYTES:
        raise WorkbenchCredentialError("frame_size", "The credential channel frame has an invalid size.")
    return value


def _zero(value: bytearray | memoryview) -> None:
    if isinstance(value, memoryview):
        value[:] = b"\0" * len(value)
    else:
        value[:] = b"\0" * len(value)


class _Reader:
    def __init__(self, value: memoryview):
        self._value = value
        self._offset = 0

    def read_bytes(self, maximum: int, *, exact: Optional[int] = None) -> bytes:
        length = struct.unpack(">H", self.read_raw(2))[0]
        if length > maximum or (exact is not None and length != exact):
            raise WorkbenchCredentialError("invalid_field_length", "A credential protocol field has an invalid length.")
        return self.read_raw(length)

    def read_text(self, maximum: int) -> str:
        raw = self.read_bytes(maximum)
        try:
            value = raw.decode("utf-8", errors="strict")
        except UnicodeDecodeError as exc:
            raise WorkbenchCredentialError("invalid_wire_text", "A credential protocol field is invalid.") from exc
        if "\0" in value:
            raise WorkbenchCredentialError("invalid_wire_text", "A credential protocol field is invalid.")
        return value

    def read_raw(self, length: int) -> bytes:
        if length < 0 or self._offset + length > len(self._value):
            raise WorkbenchCredentialError("truncated_frame", "The credential protocol frame is truncated.")
        value = bytes(self._value[self._offset:self._offset + length])
        self._offset += length
        return value

    def read_mutable(self, length: int) -> bytearray:
        if length < 0 or self._offset + length > len(self._value):
            raise WorkbenchCredentialError("truncated_frame", "The credential protocol frame is truncated.")
        value = bytearray(self._value[self._offset:self._offset + length])
        self._offset += length
        return value

    def read_i64(self) -> int:
        return struct.unpack(">q", self.read_raw(8))[0]

    def read_i32(self) -> int:
        return struct.unpack(">i", self.read_raw(4))[0]

    def read_u8(self) -> int:
        return self.read_raw(1)[0]

    def require_end(self) -> None:
        if self._offset != len(self._value):
            raise WorkbenchCredentialError("trailing_frame_data", "The credential protocol frame has trailing data.")


def _reset_sessions_for_tests() -> None:
    with _sessions_lock:
        _sessions.clear()
