"""One-shot bounded stdio relay for the Workbench credential WebSocket.

This module is started only by the Desktop's fixed ``docker exec`` command.
It carries transport frames and has no credential, bootstrap, profile, or
authorization authority of its own.
"""

from __future__ import annotations

import asyncio
import enum
import io
import struct
import sys
from dataclasses import dataclass
from typing import Any, BinaryIO, Callable, Protocol
from urllib.parse import quote_from_bytes

import websockets
from websockets.exceptions import ConnectionClosed

from hermes_cli.workbench_credentials import MAX_FRAME_BYTES, MAX_HANDSHAKE_BYTES, _SESSION_RE


_MAGIC = b"HCRL"
_VERSION = 1
_HEADER = struct.Struct(">4sBBI")
_MAX_SESSION_BYTES = 64
_MAX_TEXT_BYTES = MAX_HANDSHAKE_BYTES
_MAX_BINARY_BYTES = MAX_FRAME_BYTES


class _Opcode(enum.IntEnum):
    CONNECT = 1
    OPEN = 2
    TEXT = 3
    BINARY = 4
    RECEIVE = 5
    CLOSE = 6


@dataclass(frozen=True)
class _Frame:
    opcode: _Opcode
    payload: bytes


class RelayProtocolError(RuntimeError):
    """A safe local transport failure with no payload-derived message."""


class _FrameSource(Protocol):
    async def read(self) -> _Frame | None: ...
    async def close(self) -> None: ...


class _MemoryFrameSource:
    """Non-blocking source used only by focused in-memory protocol tests."""

    def __init__(self, stream: BinaryIO):
        self._stream = stream

    async def read(self) -> _Frame | None:
        return _read_frame(self._stream)

    async def close(self) -> None:
        return None


class _PipeFrameSource:
    """Cancellable asyncio reader for the relay's real Docker stdin pipe."""

    def __init__(self, reader: asyncio.StreamReader, transport: asyncio.ReadTransport):
        self._reader = reader
        self._transport = transport

    @classmethod
    async def open(cls, stream: BinaryIO) -> "_PipeFrameSource":
        loop = asyncio.get_running_loop()
        reader = asyncio.StreamReader(limit=_MAX_BINARY_BYTES + _HEADER.size)
        protocol = asyncio.StreamReaderProtocol(reader)
        transport, _ = await loop.connect_read_pipe(lambda: protocol, stream)
        return cls(reader, transport)

    async def read(self) -> _Frame | None:
        try:
            header = await self._reader.readexactly(_HEADER.size)
        except asyncio.IncompleteReadError as exc:
            if not exc.partial:
                return None
            raise RelayProtocolError("invalid relay frame") from exc
        opcode, length = _parse_header(header)
        try:
            payload = await self._reader.readexactly(length) if length else b""
        except asyncio.IncompleteReadError as exc:
            raise RelayProtocolError("invalid relay frame") from exc
        return _decode_frame(opcode, payload)

    async def close(self) -> None:
        self._transport.close()
        await asyncio.sleep(0)


@dataclass(frozen=True)
class _InputEvent:
    frame: _Frame | None = None
    error: Exception | None = None


def _maximum_payload(opcode: _Opcode) -> int:
    if opcode is _Opcode.CONNECT:
        return _MAX_SESSION_BYTES
    if opcode is _Opcode.TEXT:
        return _MAX_TEXT_BYTES
    if opcode is _Opcode.BINARY:
        return _MAX_BINARY_BYTES
    if opcode in (_Opcode.OPEN, _Opcode.RECEIVE, _Opcode.CLOSE):
        return 0
    raise RelayProtocolError("invalid relay frame")


def _read_exact(stream: BinaryIO, length: int, *, allow_clean_eof: bool = False) -> bytes | None:
    chunks = bytearray()
    try:
        while len(chunks) < length:
            chunk = stream.read(length - len(chunks))
            if not chunk:
                if not chunks and allow_clean_eof:
                    return None
                raise RelayProtocolError("invalid relay frame")
            chunks.extend(chunk)
        return bytes(chunks)
    finally:
        for index in range(len(chunks)):
            chunks[index] = 0


def _read_frame(stream: BinaryIO) -> _Frame | None:
    header = _read_exact(stream, _HEADER.size, allow_clean_eof=True)
    if header is None:
        return None
    opcode, length = _parse_header(header)
    payload = _read_exact(stream, length) if length else b""
    assert payload is not None
    return _decode_frame(opcode, payload)


def _parse_header(header: bytes) -> tuple[_Opcode, int]:
    magic, version, raw_opcode, length = _HEADER.unpack(header)
    try:
        opcode = _Opcode(raw_opcode)
    except ValueError as exc:
        raise RelayProtocolError("invalid relay frame") from exc
    maximum = _maximum_payload(opcode)
    if magic != _MAGIC or version != _VERSION or length > maximum:
        raise RelayProtocolError("invalid relay frame")
    if opcode in (_Opcode.OPEN, _Opcode.RECEIVE, _Opcode.CLOSE) and length != 0:
        raise RelayProtocolError("invalid relay frame")
    return opcode, length


def _decode_frame(opcode: _Opcode, payload: bytes) -> _Frame:
    if opcode is _Opcode.TEXT:
        try:
            payload.decode("utf-8", errors="strict")
        except UnicodeDecodeError as exc:
            raise RelayProtocolError("invalid relay frame") from exc
    return _Frame(opcode, payload)


def _write_frame(stream: BinaryIO, opcode: _Opcode, payload: bytes = b"") -> None:
    if len(payload) > _maximum_payload(opcode):
        raise RelayProtocolError("invalid relay frame")
    if opcode in (_Opcode.OPEN, _Opcode.RECEIVE, _Opcode.CLOSE) and payload:
        raise RelayProtocolError("invalid relay frame")
    stream.write(_HEADER.pack(_MAGIC, _VERSION, int(opcode), len(payload)))
    if payload:
        stream.write(payload)
    stream.flush()


async def _open_frame_source(stdin: BinaryIO) -> _FrameSource:
    try:
        stdin.fileno()
    except (AttributeError, io.UnsupportedOperation, OSError):
        return _MemoryFrameSource(stdin)
    return await _PipeFrameSource.open(stdin)


async def _produce_frames(source: _FrameSource, queue: asyncio.Queue[_InputEvent]) -> None:
    try:
        while True:
            frame = await source.read()
            await queue.put(_InputEvent(frame=frame))
            if frame is None:
                return
    except Exception as exc:
        await queue.put(_InputEvent(error=exc))


async def _next_input(queue: asyncio.Queue[_InputEvent]) -> _Frame | None:
    event = await queue.get()
    if event.error is not None:
        raise event.error
    return event.frame


async def _cancel(task: asyncio.Task[Any]) -> None:
    if not task.done():
        task.cancel()
    await asyncio.gather(task, return_exceptions=True)


async def relay(
    stdin: BinaryIO,
    stdout: BinaryIO,
    *,
    connect: Callable[..., Any] = websockets.connect,
) -> None:
    source = await _open_frame_source(stdin)
    await _relay_source(source, stdout, connect=connect)


async def _relay_source(
    source: _FrameSource,
    stdout: BinaryIO,
    *,
    connect: Callable[..., Any],
) -> None:
    input_queue: asyncio.Queue[_InputEvent] = asyncio.Queue(maxsize=1)
    producer = asyncio.create_task(_produce_frames(source, input_queue))
    session = bytearray()
    session_text: str | None = None
    uri: str | None = None
    try:
        first = await _next_input(input_queue)
        if first is None or first.opcode is not _Opcode.CONNECT or not first.payload:
            raise RelayProtocolError("invalid relay state")
        session.extend(first.payload)
        first = None
        try:
            session_text = session.decode("ascii", errors="strict")
        except UnicodeDecodeError as exc:
            raise RelayProtocolError("invalid relay session") from exc
        if not _SESSION_RE.fullmatch(session_text):
            raise RelayProtocolError("invalid relay session")
        uri = (
            "ws://127.0.0.1:9119/api/workbench/credentials/v2?session="
            + quote_from_bytes(session, safe="")
        )

        async with connect(
            uri,
            proxy=None,
            compression=None,
            ping_interval=None,
            max_queue=1,
            max_size=_MAX_BINARY_BYTES,
            close_timeout=2,
        ) as websocket:
            session_text = None
            uri = None
            for index in range(len(session)):
                session[index] = 0
            await asyncio.to_thread(_write_frame, stdout, _Opcode.OPEN)

            while True:
                frame = await _next_input(input_queue)
                if frame is None:
                    await websocket.close(code=1000)
                    return
                if frame.opcode is _Opcode.TEXT:
                    text = frame.payload.decode("utf-8", errors="strict")
                    await websocket.send(text)
                elif frame.opcode is _Opcode.BINARY:
                    await websocket.send(frame.payload)
                elif frame.opcode is _Opcode.RECEIVE:
                    receive_task = asyncio.create_task(websocket.recv())
                    input_task = asyncio.create_task(_next_input(input_queue))
                    try:
                        done, _ = await asyncio.wait(
                            (receive_task, input_task),
                            return_when=asyncio.FIRST_COMPLETED,
                        )
                        if input_task in done:
                            concurrent = input_task.result()
                            await _cancel(receive_task)
                            if concurrent is None or concurrent.opcode is _Opcode.CLOSE:
                                await websocket.close(code=1000)
                                return
                            raise RelayProtocolError("invalid relay state")
                        await _cancel(input_task)
                        message = receive_task.result()
                    except ConnectionClosed:
                        await asyncio.to_thread(_write_frame, stdout, _Opcode.CLOSE)
                        return
                    finally:
                        await _cancel(receive_task)
                        await _cancel(input_task)
                    if isinstance(message, str):
                        payload = message.encode("utf-8", errors="strict")
                        if len(payload) > _MAX_TEXT_BYTES:
                            raise RelayProtocolError("invalid relay message")
                        await asyncio.to_thread(_write_frame, stdout, _Opcode.TEXT, payload)
                    elif isinstance(message, bytes):
                        if len(message) > _MAX_BINARY_BYTES:
                            raise RelayProtocolError("invalid relay message")
                        await asyncio.to_thread(_write_frame, stdout, _Opcode.BINARY, message)
                    else:
                        raise RelayProtocolError("invalid relay message")
                elif frame.opcode is _Opcode.CLOSE:
                    await websocket.close(code=1000)
                    return
                else:
                    raise RelayProtocolError("invalid relay state")
    finally:
        session_text = None
        uri = None
        for index in range(len(session)):
            session[index] = 0
        producer.cancel()
        try:
            await source.close()
        finally:
            await asyncio.gather(producer, return_exceptions=True)


def main() -> int:
    try:
        asyncio.run(relay(sys.stdin.buffer, sys.stdout.buffer))
        return 0
    except BaseException:
        return 1
    finally:
        try:
            sys.stdout.buffer.flush()
        except BaseException:
            pass
        try:
            sys.stdin.buffer.close()
        except BaseException:
            pass
        try:
            sys.stdout.buffer.close()
        except BaseException:
            pass


if __name__ == "__main__":
    raise SystemExit(main())
