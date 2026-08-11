from __future__ import annotations

import base64
import asyncio
import hashlib
import hmac
import json
import os
import struct
import time
from pathlib import Path

import pytest
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.hazmat.primitives.kdf.hkdf import HKDF

from hermes_cli import workbench_credentials as wc


SESSION = "hcs2_" + "A" * 43
CONNECTION = "hcv2_" + "B" * 43
CONTAINER = "c" * 64
IMAGE = "sha256:" + "d" * 64
BOOTSTRAP_KEY = bytes(range(1, 33))
HOST_NONCE = bytes(range(33, 65))
SERVER_NONCE = bytes(range(65, 97))


class FakeSocket:
    def __init__(self, messages=None):
        self.messages = list(messages or [])
        self.sent_text = []
        self.sent_bytes = []
        self.closed = []
        self.on_binary = None

    async def receive(self):
        if not self.messages:
            raise AssertionError("fake socket has no queued message")
        return self.messages.pop(0)

    async def send_text(self, data: str):
        self.sent_text.append(data)

    async def send_bytes(self, data: bytes):
        self.sent_bytes.append(data)
        if self.on_binary is not None:
            response = self.on_binary(data)
            self.messages.append({"type": "websocket.receive", "bytes": response, "text": None})

    async def close(self, code=1000, reason=""):
        self.closed.append((code, reason))


def _field(value: bytes) -> bytes:
    return struct.pack(">H", len(value)) + value


def _bootstrap_bytes(host_key, expires_at: int) -> bytes:
    public = host_key.public_key().public_bytes(
        serialization.Encoding.DER,
        serialization.PublicFormat.SubjectPublicKeyInfo,
    )
    values = [
        SESSION,
        CONTAINER,
        IMAGE,
        "S-1-5-21-1000",
        "machine-1",
        "install-1",
        "profile-1",
    ]
    return (
        b"HCB2"
        + struct.pack(">i", wc.PROTOCOL_VERSION)
        + b"".join(_field(value.encode()) for value in values)
        + struct.pack(">q", expires_at)
        + _field(public)
        + _field(BOOTSTRAP_KEY)
    )


def _write_bootstrap(path: Path, payload: bytes) -> None:
    path.write_bytes(payload)
    path.chmod(0o600)


def _profile_bytes(expires_at: int, *, env=None) -> bytes:
    return json.dumps(
        {
            "schema": "photon.workbench.credential-profile/v1",
            "sessionId": SESSION,
            "containerId": CONTAINER,
            "imageDigest": IMAGE,
            "profileId": "profile-1",
            "expiresAtUnix": expires_at,
            "env": env or {
                "OPENAI_API_KEY": {
                    "connection_ref": CONNECTION,
                    "purpose": "model:openai",
                    "revision": 7,
                }
            },
        },
        separators=(",", ":"),
    ).encode()


def _host_hello(host_key, expires_at: int) -> dict:
    public = host_key.public_key().public_bytes(
        serialization.Encoding.DER,
        serialization.PublicFormat.SubjectPublicKeyInfo,
    )
    binding = wc.RuntimeBinding(
        CONTAINER, IMAGE, "S-1-5-21-1000", "machine-1", "install-1", "profile-1", expires_at,
    )
    bootstrap = wc.RuntimeBootstrap(SESSION, binding, bytearray(public), bytearray(BOOTSTRAP_KEY))
    try:
        transcript = wc._transcript("host-hello", bootstrap, public, HOST_NONCE, b"", b"")
        return {
            "version": wc.PROTOCOL_VERSION,
            "type": "host-hello",
            "sessionId": SESSION,
            "hostPublicKey": wc._b64url(public),
            "hostNonce": wc._b64url(HOST_NONCE),
            "proof": wc._b64url(hmac.digest(BOOTSTRAP_KEY, transcript, "sha256")),
        }
    finally:
        bootstrap.close()


def test_bootstrap_is_owner_only_single_use_and_zeroable(tmp_path):
    host_key = ec.generate_private_key(ec.SECP256R1())
    expires_at = int(time.time()) + 300
    path = tmp_path / "bootstrap.bin"
    _write_bootstrap(path, _bootstrap_bytes(host_key, expires_at))

    bootstrap = wc.load_bootstrap_once(path)
    assert not path.exists()
    assert bootstrap.session_id == SESSION
    assert bootstrap.binding.container_id == CONTAINER
    assert bytes(bootstrap.bootstrap_key) == BOOTSTRAP_KEY
    bootstrap.close()
    assert not any(bootstrap.bootstrap_key)
    assert not any(bootstrap.host_public_key)


def test_bootstrap_rejects_broad_permissions_and_symlink(tmp_path):
    if os.name != "posix":
        pytest.skip("POSIX ownership and mode enforcement is exercised in the Linux container gate")
    host_key = ec.generate_private_key(ec.SECP256R1())
    expires_at = int(time.time()) + 300
    broad = tmp_path / "broad.bin"
    broad.write_bytes(_bootstrap_bytes(host_key, expires_at))
    broad.chmod(0o644)
    with pytest.raises(wc.WorkbenchCredentialError, match="permissions"):
        wc.load_bootstrap_once(broad)

    target = tmp_path / "target.bin"
    _write_bootstrap(target, _bootstrap_bytes(host_key, expires_at))
    link = tmp_path / "link.bin"
    link.symlink_to(target)
    with pytest.raises(wc.WorkbenchCredentialError):
        wc.load_bootstrap_once(link)
    assert target.exists()


def test_authenticated_channel_resolves_exact_encrypted_lease(tmp_path):
    asyncio.run(_authenticated_channel_resolves_exact_encrypted_lease(tmp_path))


async def _authenticated_channel_resolves_exact_encrypted_lease(tmp_path):
    now = int(time.time())
    expires_at = now + 300
    host_key = ec.generate_private_key(ec.SECP256R1())
    server_key = ec.generate_private_key(ec.SECP256R1())
    path = tmp_path / "bootstrap.bin"
    _write_bootstrap(path, _bootstrap_bytes(host_key, expires_at))
    hello = _host_hello(host_key, expires_at)
    socket = FakeSocket([{"type": "websocket.receive", "text": json.dumps(hello), "bytes": None}])
    session = await wc.accept_reverse_channel(
        socket,
        peer_host="127.0.0.1",
        session_id=SESSION,
        bootstrap_path=path,
        now=now,
        server_private_key=server_key,
        server_nonce=SERVER_NONCE,
    )
    assert not path.exists()
    server_hello = json.loads(socket.sent_text[0])
    server_public = wc._b64url_decode(server_hello["serverPublicKey"], 512)
    host_public = host_key.public_key().public_bytes(
        serialization.Encoding.DER,
        serialization.PublicFormat.SubjectPublicKeyInfo,
    )
    bootstrap = wc.RuntimeBootstrap(
        SESSION,
        wc.RuntimeBinding(CONTAINER, IMAGE, "S-1-5-21-1000", "machine-1", "install-1", "profile-1", expires_at),
        bytearray(host_public),
        bytearray(BOOTSTRAP_KEY),
    )
    transcript = wc._transcript("server-hello", bootstrap, host_public, HOST_NONCE, server_public, SERVER_NONCE)
    assert hmac.compare_digest(
        wc._b64url_decode(server_hello["proof"], 32),
        hmac.digest(BOOTSTRAP_KEY, transcript, "sha256"),
    )
    shared = host_key.exchange(ec.ECDH(), serialization.load_der_public_key(server_public))
    salt = hmac.digest(BOOTSTRAP_KEY, HOST_NONCE + SERVER_NONCE, "sha256")
    keys = HKDF(algorithm=hashes.SHA256(), length=64, salt=salt, info=hashlib.sha256(transcript).digest()).derive(shared)
    host_to_container, container_to_host = keys[:32], keys[32:]
    secret = b"interoperable-secret"

    def host_response(frame: bytes) -> bytes:
        assert frame[:6] == b"HCF2" + bytes((wc.PROTOCOL_VERSION, 1))
        sequence = struct.unpack(">Q", frame[6:14])[0]
        request_plain = AESGCM(container_to_host).decrypt(
            wc._nonce(1, sequence), frame[14:], wc._frame_aad(SESSION, 1, sequence),
        )
        reader = wc._Reader(memoryview(request_plain))
        assert reader.read_raw(4) == b"HCR2"
        request_id = reader.read_text(128)
        connection_ref = reader.read_text(64)
        assert reader.read_text(128) == "profile-1"
        assert reader.read_text(160) == "model:chat"
        revision = reader.read_i64()
        nonce = reader.read_bytes(32, exact=32)
        reader.require_end()
        response = bytearray(b"HCS2\0")
        response += _field(request_id.encode()) + _field(nonce) + _field(connection_ref.encode())
        response += struct.pack(">q", revision) + struct.pack(">q", int(time.time()) + 30)
        response += struct.pack(">i", len(secret)) + secret
        response_sequence = 1
        encrypted = AESGCM(host_to_container).encrypt(
            wc._nonce(2, response_sequence), bytes(response), wc._frame_aad(SESSION, 2, response_sequence),
        )
        return b"HCF2" + bytes((wc.PROTOCOL_VERSION, 2)) + struct.pack(">Q", response_sequence) + encrypted

    socket.on_binary = host_response
    with await session.resolve(wc.LeaseReference(CONNECTION, "model:chat", 7)) as lease:
        assert lease.value.tobytes() == secret
    assert lease._value is None
    await session.close()
    bootstrap.close()
    wc._reset_sessions_for_tests()


def test_handshake_rejects_tampering_and_non_loopback_without_consuming(tmp_path):
    asyncio.run(_handshake_rejects_tampering_and_non_loopback_without_consuming(tmp_path))


def test_handshake_json_rejects_duplicate_or_extra_fields():
    with pytest.raises(wc.WorkbenchCredentialError, match="handshake"):
        wc._strict_json_object('{"version":2,"version":2}', {"version"})
    with pytest.raises(wc.WorkbenchCredentialError, match="shape"):
        wc._strict_json_object('{"version":2,"unexpected":true}', {"version"})


async def _handshake_rejects_tampering_and_non_loopback_without_consuming(tmp_path):
    now = int(time.time())
    expires_at = now + 300
    host_key = ec.generate_private_key(ec.SECP256R1())
    path = tmp_path / "bootstrap.bin"
    _write_bootstrap(path, _bootstrap_bytes(host_key, expires_at))
    socket = FakeSocket()
    with pytest.raises(wc.WorkbenchCredentialError, match="loopback"):
        await wc.accept_reverse_channel(socket, peer_host="192.0.2.1", session_id=SESSION, bootstrap_path=path, now=now)
    assert path.exists()

    mismatch = FakeSocket()
    with pytest.raises(wc.WorkbenchCredentialError, match="session"):
        await wc.accept_reverse_channel(
            mismatch,
            peer_host="127.0.0.1",
            session_id="hcs2_" + "Z" * 43,
            bootstrap_path=path,
            now=now,
        )
    assert not path.exists()
    assert mismatch.closed
    _write_bootstrap(path, _bootstrap_bytes(host_key, expires_at))

    hello = _host_hello(host_key, expires_at)
    hello["proof"] = wc._b64url(b"x" * 32)
    tampered = FakeSocket([{"type": "websocket.receive", "text": json.dumps(hello), "bytes": None}])
    with pytest.raises(wc.WorkbenchCredentialError, match="authentication"):
        await wc.accept_reverse_channel(tampered, peer_host="::1", session_id=SESSION, bootstrap_path=path, now=now)
    assert not path.exists()
    assert tampered.closed


def test_binary_contract_rejects_wrong_echo_revision_and_expiry():
    nonce = bytearray(range(32))
    reference = wc.LeaseReference(CONNECTION, "model:chat", 3)
    request = wc._encode_request("request-1", reference, "profile-1", nonce)
    assert request.startswith(b"HCR2")

    response = bytearray(b"HCS2\0")
    response += _field(b"request-1") + _field(nonce) + _field(CONNECTION.encode())
    response += struct.pack(">q", 4) + struct.pack(">q", int(time.time()) + 30)
    response += struct.pack(">i", 1) + b"x"
    with pytest.raises(wc.WorkbenchCredentialError, match="binding"):
        wc._decode_response(
            response,
            request_id="request-1",
            request_nonce=nonce,
            reference=reference,
            session_expires_at=int(time.time()) + 300,
        )


def test_profile_is_owner_only_single_use_and_exact_bootstrap_bound(tmp_path):
    expires_at = int(time.time()) + 300
    host_key = ec.generate_private_key(ec.SECP256R1())
    host_public = host_key.public_key().public_bytes(
        serialization.Encoding.DER,
        serialization.PublicFormat.SubjectPublicKeyInfo,
    )
    bootstrap = wc.RuntimeBootstrap(
        SESSION,
        wc.RuntimeBinding(
            CONTAINER,
            IMAGE,
            "S-1-5-21-1000",
            "machine-1",
            "install-1",
            "profile-1",
            expires_at,
        ),
        bytearray(host_public),
        bytearray(BOOTSTRAP_KEY),
    )
    path = tmp_path / "profile.json"
    path.write_bytes(_profile_bytes(expires_at))
    path.chmod(0o600)
    try:
        config = wc.load_profile_once(path, bootstrap)
        assert not path.exists()
        assert config == {
            "env": {
                "OPENAI_API_KEY": {
                    "connection_ref": CONNECTION,
                    "purpose": "model:openai",
                    "revision": 7,
                }
            }
        }
    finally:
        bootstrap.close()


def test_profile_rejects_duplicate_fields_and_wrong_purpose():
    expires_at = int(time.time()) + 300
    host_key = ec.generate_private_key(ec.SECP256R1())
    host_public = host_key.public_key().public_bytes(
        serialization.Encoding.DER,
        serialization.PublicFormat.SubjectPublicKeyInfo,
    )
    bootstrap = wc.RuntimeBootstrap(
        SESSION,
        wc.RuntimeBinding(
            CONTAINER,
            IMAGE,
            "S-1-5-21-1000",
            "machine-1",
            "install-1",
            "profile-1",
            expires_at,
        ),
        bytearray(host_public),
        bytearray(BOOTSTRAP_KEY),
    )
    try:
        duplicate = (
            b'{"schema":"photon.workbench.credential-profile/v1",'
            b'"schema":"photon.workbench.credential-profile/v1"}'
        )
        with pytest.raises(wc.WorkbenchCredentialError) as duplicate_exc:
            wc._parse_profile(bytearray(duplicate), bootstrap)
        assert duplicate_exc.value.code == "profile_json_invalid"

        wrong_purpose = _profile_bytes(
            expires_at,
            env={
                "OPENAI_API_KEY": {
                    "connection_ref": CONNECTION,
                    "purpose": "model:anthropic",
                    "revision": 7,
                }
            },
        )
        with pytest.raises(wc.WorkbenchCredentialError) as purpose_exc:
            wc._parse_profile(bytearray(wrong_purpose), bootstrap)
        assert purpose_exc.value.code == "profile_mapping_invalid"
    finally:
        bootstrap.close()


def test_revoke_all_sessions_closes_every_registered_profile():
    class StubSession:
        def __init__(self):
            self.closed = False

        async def close(self, **_kwargs):
            self.closed = True

    one = StubSession()
    two = StubSession()
    with wc._sessions_lock:
        wc._sessions["profile-1"] = one
        wc._sessions["profile-2"] = two
    asyncio.run(wc.revoke_all_sessions())
    assert one.closed and two.closed
    assert wc._sessions == {}


def test_exact_reverse_channel_route_is_registered_once():
    from hermes_cli.web_server import app

    routes = [
        route
        for route in app.routes
        if getattr(route, "path", None) == "/api/workbench/credentials/v2"
    ]
    assert len(routes) == 1
    assert routes[0].name == "workbench_credentials_ws"
