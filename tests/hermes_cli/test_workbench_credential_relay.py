from __future__ import annotations

import asyncio
import io
import struct

import pytest
from websockets.exceptions import ConnectionClosedOK

from hermes_cli import workbench_credential_relay as relay


SESSION = b"hcs2_" + b"A" * 43


class FakeWebSocket:
    def __init__(self, received=()):
        self.received = list(received)
        self.sent = []
        self.closed = []
        self.recv_started = False
        self.recv_cancelled = False
        self.recv_calls = 0

    async def send(self, message):
        self.sent.append(message)

    async def recv(self):
        self.recv_calls += 1
        message = self.received.pop(0)
        if message is PEER_CLOSED:
            raise ConnectionClosedOK(None, None)
        if message is NEVER_RETURNS:
            self.recv_started = True
            try:
                await asyncio.Future()
            except asyncio.CancelledError:
                self.recv_cancelled = True
                raise
        return message

    async def close(self, code=1000):
        self.closed.append(code)


class FakeConnect:
    def __init__(self, websocket):
        self.websocket = websocket
        self.calls = []
        self.exited = 0

    def __call__(self, uri, **kwargs):
        self.calls.append((uri, kwargs))
        return self

    async def __aenter__(self):
        return self.websocket

    async def __aexit__(self, *_args):
        self.exited += 1


class ControlledSource:
    def __init__(self):
        self.queue = asyncio.Queue(maxsize=1)
        self.closed = False

    async def read(self):
        return await self.queue.get()

    async def close(self):
        self.closed = True

    async def feed(self, frame):
        await self.queue.put(frame)


PEER_CLOSED = object()
NEVER_RETURNS = object()


def _frame(opcode: relay._Opcode, payload: bytes = b"") -> bytes:
    return struct.pack(">4sBBI", b"HCRL", 1, int(opcode), len(payload)) + payload


def _output_frames(output: io.BytesIO):
    stream = io.BytesIO(output.getvalue())
    frames = []
    while True:
        frame = relay._read_frame(stream)
        if frame is None:
            return frames
        frames.append(frame)


def _run(wire: bytes, websocket: FakeWebSocket):
    output = io.BytesIO()
    connect = FakeConnect(websocket)
    asyncio.run(relay.relay(io.BytesIO(wire), output, connect=connect))
    return output, connect


def test_fixed_loopback_connect_options_and_round_trip_are_exact(capsys):
    websocket = FakeWebSocket(["server-hello", b"\x00\x01encrypted"])
    connect = FakeConnect(websocket)
    output = io.BytesIO()

    async def exercise():
        source = ControlledSource()
        task = asyncio.create_task(relay._relay_source(source, output, connect=connect))
        await source.feed(relay._Frame(relay._Opcode.CONNECT, SESSION))
        await source.feed(relay._Frame(relay._Opcode.TEXT, b"host-hello"))
        await source.feed(relay._Frame(relay._Opcode.BINARY, b"\x02\x03request"))
        await source.feed(relay._Frame(relay._Opcode.RECEIVE, b""))
        while websocket.recv_calls < 1:
            await asyncio.sleep(0)
        await source.feed(relay._Frame(relay._Opcode.RECEIVE, b""))
        while websocket.recv_calls < 2:
            await asyncio.sleep(0)
        await source.feed(relay._Frame(relay._Opcode.CLOSE, b""))
        await asyncio.wait_for(task, timeout=0.5)
        assert source.closed

    asyncio.run(exercise())

    assert connect.calls == [
        (
            "ws://127.0.0.1:9119/api/workbench/credentials/v2?session=" + SESSION.decode(),
            {
                "proxy": None,
                "compression": None,
                "ping_interval": None,
                "max_queue": 1,
                "max_size": 96 * 1024,
                "close_timeout": 2,
            },
        )
    ]
    assert websocket.sent == ["host-hello", b"\x02\x03request"]
    assert websocket.closed == [1000]
    assert connect.exited == 1
    assert _output_frames(output) == [
        relay._Frame(relay._Opcode.OPEN, b""),
        relay._Frame(relay._Opcode.TEXT, b"server-hello"),
        relay._Frame(relay._Opcode.BINARY, b"\x00\x01encrypted"),
    ]
    assert SESSION not in output.getvalue()
    assert capsys.readouterr() == ("", "")


@pytest.mark.parametrize(
    "wire",
    [
        b"BAD!" + bytes((1, int(relay._Opcode.CONNECT))) + struct.pack(">I", len(SESSION)) + SESSION,
        b"HCRL" + bytes((2, int(relay._Opcode.CONNECT))) + struct.pack(">I", len(SESSION)) + SESSION,
        b"HCRL" + bytes((1, 255)) + struct.pack(">I", 0),
        b"HCRL" + bytes((1, int(relay._Opcode.CONNECT))) + struct.pack(">I", 65),
        _frame(relay._Opcode.CONNECT, b"invalid session"),
        _frame(relay._Opcode.RECEIVE),
        _frame(relay._Opcode.CONNECT, SESSION)[:-1],
    ],
)
def test_invalid_initial_frames_fail_before_websocket_connect(wire):
    connect = FakeConnect(FakeWebSocket())
    output = io.BytesIO()
    with pytest.raises(relay.RelayProtocolError):
        asyncio.run(relay.relay(io.BytesIO(wire), output, connect=connect))
    assert connect.calls == []
    assert output.getvalue() == b""


@pytest.mark.parametrize(
    "after_connect",
    [
        _frame(relay._Opcode.CONNECT, SESSION),
        _frame(relay._Opcode.OPEN),
        _frame(relay._Opcode.RECEIVE, b"x"),
        _frame(relay._Opcode.TEXT, b"\xff"),
        b"HCRL" + bytes((1, int(relay._Opcode.BINARY))) + struct.pack(">I", 2) + b"x",
        b"HCRL" + bytes((1, int(relay._Opcode.BINARY))) + struct.pack(">I", 96 * 1024 + 1),
    ],
)
def test_duplicate_out_of_order_invalid_utf8_truncated_and_oversized_frames_fail_closed(after_connect):
    websocket = FakeWebSocket()
    connect = FakeConnect(websocket)
    output = io.BytesIO()
    with pytest.raises(relay.RelayProtocolError):
        asyncio.run(
            relay.relay(
                io.BytesIO(_frame(relay._Opcode.CONNECT, SESSION) + after_connect),
                output,
                connect=connect,
            )
        )
    assert len(connect.calls) == 1
    assert connect.exited == 1
    assert _output_frames(output) == [relay._Frame(relay._Opcode.OPEN, b"")]
    assert SESSION not in output.getvalue()


def test_eof_closes_websocket_and_peer_close_emits_one_close_frame():
    eof_socket = FakeWebSocket()
    eof_output, eof_connect = _run(_frame(relay._Opcode.CONNECT, SESSION), eof_socket)
    assert eof_socket.closed == [1000]
    assert eof_connect.exited == 1
    assert _output_frames(eof_output) == [relay._Frame(relay._Opcode.OPEN, b"")]

    peer_socket = FakeWebSocket([PEER_CLOSED])
    peer_output = io.BytesIO()
    peer_connect = FakeConnect(peer_socket)

    async def peer_exercise():
        source = ControlledSource()
        task = asyncio.create_task(relay._relay_source(source, peer_output, connect=peer_connect))
        await source.feed(relay._Frame(relay._Opcode.CONNECT, SESSION))
        await source.feed(relay._Frame(relay._Opcode.RECEIVE, b""))
        await asyncio.wait_for(task, timeout=0.5)
        assert source.closed

    asyncio.run(peer_exercise())
    assert peer_connect.exited == 1
    assert _output_frames(peer_output) == [
        relay._Frame(relay._Opcode.OPEN, b""),
        relay._Frame(relay._Opcode.CLOSE, b""),
    ]


@pytest.mark.parametrize("shutdown", [_frame(relay._Opcode.CLOSE), b""])
def test_pending_receive_is_promptly_cancelled_by_close_or_eof(shutdown):
    websocket = FakeWebSocket([NEVER_RETURNS])
    connect = FakeConnect(websocket)
    output = io.BytesIO()

    async def exercise():
        await asyncio.wait_for(
            relay.relay(
                io.BytesIO(
                    _frame(relay._Opcode.CONNECT, SESSION)
                    + _frame(relay._Opcode.RECEIVE)
                    + shutdown
                ),
                output,
                connect=connect,
            ),
            timeout=0.5,
        )

    asyncio.run(exercise())
    assert websocket.recv_started
    assert websocket.recv_cancelled
    assert websocket.closed == [1000]
    assert connect.exited == 1
    assert _output_frames(output) == [relay._Frame(relay._Opcode.OPEN, b"")]


def test_pending_receive_rejects_any_other_concurrent_opcode_and_cancels_recv():
    websocket = FakeWebSocket([NEVER_RETURNS])
    connect = FakeConnect(websocket)
    output = io.BytesIO()

    async def exercise():
        with pytest.raises(relay.RelayProtocolError):
            await asyncio.wait_for(
                relay.relay(
                    io.BytesIO(
                        _frame(relay._Opcode.CONNECT, SESSION)
                        + _frame(relay._Opcode.RECEIVE)
                        + _frame(relay._Opcode.TEXT, b"out-of-order")
                    ),
                    output,
                    connect=connect,
                ),
                timeout=0.5,
            )

    asyncio.run(exercise())
    assert websocket.recv_started
    assert websocket.recv_cancelled
    assert connect.exited == 1
    assert _output_frames(output) == [relay._Frame(relay._Opcode.OPEN, b"")]
