using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HermesCredentialBroker.Runtime;

namespace HermesDesktop;

internal sealed class DockerExecCredentialRuntimeSocket : ICredentialRuntimeSocket
{
    internal const int MaximumTextBytes = 16 * 1024;
    internal const int MaximumBinaryBytes = 96 * 1024;
    private const int MaximumSessionBytes = 64;
    private const byte RelayVersion = 1;
    private static readonly byte[] RelayMagic = "HCRL"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Regex ContainerPattern = new(
        "^[a-f0-9]{64}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);

    private readonly string _dockerExecutable;
    private readonly string _containerId;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Process? _process;
    private Task _stderrDrain = Task.CompletedTask;
    private int _connectStarted;
    private int _shutdownStarted;

    internal DockerExecCredentialRuntimeSocket(string dockerExecutable, string containerId)
    {
        if (!Path.IsPathFullyQualified(dockerExecutable)
            || !StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(dockerExecutable), "docker.exe"))
        {
            throw new CredentialRuntimeException("docker_unavailable", "The fixed Docker runtime is unavailable.");
        }
        if (!ContainerPattern.IsMatch(containerId))
        {
            throw new CredentialRuntimeException("invalid_container_id", "The runtime container identity is invalid.");
        }
        _dockerExecutable = dockerExecutable;
        _containerId = containerId;
    }

    public async Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _connectStarted, 1) != 0)
            throw Closed("relay_state_invalid");

        byte[]? sessionBytes = null;
        try
        {
            var query = System.Web.HttpUtility.ParseQueryString(endpoint.Query);
            var sessionId = query["session"] ?? throw Closed("endpoint_session_mismatch");
            CredentialRuntimeClient.ValidateLoopbackEndpoint(endpoint, sessionId);
            sessionBytes = StrictUtf8.GetBytes(sessionId);
            if (sessionBytes.Length is <= 0 or > MaximumSessionBytes)
                throw Closed("endpoint_session_mismatch");

            var process = new Process
            {
                StartInfo = CreateStartInfo(_dockerExecutable, _containerId),
                EnableRaisingEvents = true,
            };
            if (!process.Start())
            {
                process.Dispose();
                throw Closed("relay_start_failed");
            }
            _process = process;
            _stderrDrain = DrainStderrAsync(process.StandardError.BaseStream);

            await WriteFrameAsync(RelayOpcode.Connect, sessionBytes, cancellationToken).ConfigureAwait(false);
            var opened = await ReadFrameCoreAsync(cancellationToken).ConfigureAwait(false);
            if (opened is null || opened.Value.Opcode != RelayOpcode.Open || opened.Value.Payload.Length != 0)
                throw Closed("relay_protocol_invalid");
        }
        catch
        {
            await AbortAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (sessionBytes is not null) CryptographicOperations.ZeroMemory(sessionBytes);
        }
    }

    public Task SendTextAsync(ReadOnlyMemory<byte> utf8, CancellationToken cancellationToken)
    {
        if (utf8.Length > MaximumTextBytes) return RejectApplicationFrameAsync("socket_message_too_large");
        try { _ = StrictUtf8.GetCharCount(utf8.Span); }
        catch (DecoderFallbackException) { return RejectApplicationFrameAsync("relay_protocol_invalid"); }
        return WriteApplicationFrameAsync(RelayOpcode.Text, utf8, cancellationToken);
    }

    public Task SendBinaryAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        if (bytes.Length > MaximumBinaryBytes) return RejectApplicationFrameAsync("socket_message_too_large");
        return WriteApplicationFrameAsync(RelayOpcode.Binary, bytes, cancellationToken);
    }

    public async Task<CredentialRuntimeMessage> ReceiveAsync(int maximumBytes, CancellationToken cancellationToken)
    {
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        try
        {
            await WriteApplicationFrameAsync(RelayOpcode.Receive, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
            var frame = await ReadFrameCoreAsync(cancellationToken).ConfigureAwait(false);
            if (frame is null) return new CredentialRuntimeMessage(CredentialRuntimeMessageKind.Close, []);
            if (frame.Value.Payload.Length > maximumBytes)
                throw Closed("socket_message_too_large");
            return frame.Value.Opcode switch
            {
                RelayOpcode.Text => new CredentialRuntimeMessage(CredentialRuntimeMessageKind.Text, frame.Value.Payload),
                RelayOpcode.Binary => new CredentialRuntimeMessage(CredentialRuntimeMessageKind.Binary, frame.Value.Payload),
                RelayOpcode.Close when frame.Value.Payload.Length == 0 => new CredentialRuntimeMessage(CredentialRuntimeMessageKind.Close, []),
                _ => throw Closed("relay_protocol_invalid"),
            };
        }
        catch (OperationCanceledException)
        {
            await AbortAsync().ConfigureAwait(false);
            throw;
        }
        catch (CredentialRuntimeException)
        {
            await AbortAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            await AbortAsync().ConfigureAwait(false);
            throw Closed("relay_closed");
        }
    }

    public Task CloseAsync(CancellationToken cancellationToken) => ShutdownAsync(cancellationToken, sendClose: true);

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync(CancellationToken.None, sendClose: true).ConfigureAwait(false);
        _writeGate.Dispose();
        _readGate.Dispose();
    }

    private async Task WriteApplicationFrameAsync(
        RelayOpcode opcode,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _connectStarted) == 0 || Volatile.Read(ref _shutdownStarted) != 0)
            throw Closed("relay_closed");
        try
        {
            await WriteFrameAsync(opcode, payload, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await AbortAsync().ConfigureAwait(false);
            throw;
        }
        catch (CredentialRuntimeException)
        {
            await AbortAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            await AbortAsync().ConfigureAwait(false);
            throw Closed("relay_closed");
        }
    }

    private async Task RejectApplicationFrameAsync(string code)
    {
        await AbortAsync().ConfigureAwait(false);
        throw Closed(code);
    }

    private async Task WriteFrameAsync(
        RelayOpcode opcode,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        var process = _process ?? throw Closed("relay_closed");
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var header = EncodeHeader(opcode, payload.Length);
            await process.StandardInput.BaseStream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            if (!payload.IsEmpty)
                await process.StandardInput.BaseStream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<RelayFrame?> ReadFrameCoreAsync(CancellationToken cancellationToken)
    {
        var process = _process ?? throw Closed("relay_closed");
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadFrameAsync(process.StandardOutput.BaseStream, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _readGate.Release();
        }
    }

    private async Task AbortAsync() => await ShutdownAsync(CancellationToken.None, sendClose: false).ConfigureAwait(false);

    private async Task ShutdownAsync(CancellationToken cancellationToken, bool sendClose)
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
        {
            try { await _closed.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            return;
        }

        var process = _process;
        try
        {
            if (process is null) return;
            using var timeout = new CancellationTokenSource(ShutdownTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            if (sendClose && !HasExited(process))
            {
                try { await WriteFrameAsync(RelayOpcode.Close, ReadOnlyMemory<byte>.Empty, linked.Token).ConfigureAwait(false); }
                catch { }
            }
            try { process.StandardInput.Close(); } catch { }
            try { process.StandardOutput.Close(); } catch { }
            try { await process.WaitForExitAsync(linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { TryKill(process); }
            if (!HasExited(process)) TryKill(process);
            try { await process.WaitForExitAsync().WaitAsync(ShutdownTimeout).ConfigureAwait(false); }
            catch { TryKill(process); }
            try { await _stderrDrain.WaitAsync(ShutdownTimeout).ConfigureAwait(false); }
            catch { }
        }
        finally
        {
            if (process is not null)
            {
                try { process.StandardError.Close(); } catch { }
                process.Dispose();
            }
            _closed.TrySetResult();
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string dockerExecutable, string containerId)
    {
        var start = new ProcessStartInfo
        {
            FileName = dockerExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
        {
            "exec", "-i", "--user", "10000", containerId,
            "/opt/hermes/.venv/bin/python", "-I", "-m", "hermes_cli.workbench_credential_relay",
        })
        {
            start.ArgumentList.Add(argument);
        }
        return start;
    }

    internal static byte[] EncodeHeader(RelayOpcode opcode, int payloadLength)
    {
        if (payloadLength < 0 || payloadLength > MaximumPayload(opcode))
            throw Closed("relay_protocol_invalid");
        if (IsControl(opcode) && payloadLength != 0)
            throw Closed("relay_protocol_invalid");
        var header = new byte[10];
        RelayMagic.CopyTo(header, 0);
        header[4] = RelayVersion;
        header[5] = (byte)opcode;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(6), (uint)payloadLength);
        return header;
    }

    internal static async Task<RelayFrame?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = new byte[10];
        var headerBytes = await ReadExactlyAsync(stream, header, allowCleanEof: true, cancellationToken).ConfigureAwait(false);
        if (headerBytes == 0) return null;
        if (!header.AsSpan(0, 4).SequenceEqual(RelayMagic)
            || header[4] != RelayVersion
            || !Enum.IsDefined((RelayOpcode)header[5]))
        {
            throw Closed("relay_protocol_invalid");
        }
        var opcode = (RelayOpcode)header[5];
        var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(6));
        if (length > MaximumPayload(opcode) || IsControl(opcode) && length != 0)
            throw Closed("relay_protocol_invalid");
        var payload = new byte[(int)length];
        if (payload.Length != 0)
            await ReadExactlyAsync(stream, payload, allowCleanEof: false, cancellationToken).ConfigureAwait(false);
        if (opcode == RelayOpcode.Text)
        {
            try { _ = StrictUtf8.GetCharCount(payload); }
            catch (DecoderFallbackException) { throw Closed("relay_protocol_invalid"); }
        }
        return new RelayFrame(opcode, payload);
    }

    private static async Task<int> ReadExactlyAsync(
        Stream stream,
        byte[] buffer,
        bool allowCleanEof,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (offset == 0 && allowCleanEof) return 0;
                throw Closed("relay_protocol_invalid");
            }
            offset += read;
        }
        return offset;
    }

    private static async Task DrainStderrAsync(Stream stream)
    {
        var buffer = new byte[4096];
        try
        {
            while (await stream.ReadAsync(buffer).ConfigureAwait(false) != 0) { }
        }
        catch { }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private static int MaximumPayload(RelayOpcode opcode) => opcode switch
    {
        RelayOpcode.Connect => MaximumSessionBytes,
        RelayOpcode.Text => MaximumTextBytes,
        RelayOpcode.Binary => MaximumBinaryBytes,
        RelayOpcode.Open or RelayOpcode.Receive or RelayOpcode.Close => 0,
        _ => throw Closed("relay_protocol_invalid"),
    };

    private static bool IsControl(RelayOpcode opcode) =>
        opcode is RelayOpcode.Open or RelayOpcode.Receive or RelayOpcode.Close;

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch { return true; }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }

    private static CredentialRuntimeException Closed(string code) =>
        new(code, "The credential relay closed safely.");

    internal enum RelayOpcode : byte
    {
        Connect = 1,
        Open = 2,
        Text = 3,
        Binary = 4,
        Receive = 5,
        Close = 6,
    }

    internal readonly record struct RelayFrame(RelayOpcode Opcode, byte[] Payload);
}
