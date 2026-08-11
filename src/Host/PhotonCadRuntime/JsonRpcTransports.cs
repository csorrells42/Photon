using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PhotonCadRuntime;

public class CadProtocolException : InvalidOperationException
{
    public CadProtocolException(string code, string? safeDetail = null, Exception? innerException = null)
        : base("The isolated CAD runtime returned an invalid protocol response.", innerException)
    {
        Code = ContractGuards.Identifier(code, nameof(code), 96);
        SafeDetail = safeDetail is null ? null : Sanitize(safeDetail);
    }

    public string Code { get; }
    public string? SafeDetail { get; }

    private static string? Sanitize(string value)
    {
        var safe = new string(value.Select(character => char.IsControl(character) ? ' ' : character).ToArray());
        safe = string.Join(' ', safe.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return safe.Length == 0 ? null : safe[..Math.Min(safe.Length, 512)];
    }
}

internal sealed class CadRemoteProtocolException : CadProtocolException
{
    public CadRemoteProtocolException(long remoteCode)
        : base("protocol_remote_error") => RemoteCode = remoteCode;

    public long RemoteCode { get; }
}

internal interface ICadJsonFrameTransport : IAsyncDisposable
{
    ValueTask SendAsync(ReadOnlyMemory<byte> json, CancellationToken cancellationToken);
    ValueTask<byte[]> ReceiveAsync(CancellationToken cancellationToken);
}

internal abstract class CadJsonFrameTransport : ICadJsonFrameTransport
{
    public const int MaximumFrameBytes = 1_048_576;
    public const int MaximumWriteBytes = 262_144;

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _disposed;

    protected CadJsonFrameTransport(Stream input, Stream output)
    {
        Input = input ?? throw new ArgumentNullException(nameof(input));
        Output = output ?? throw new ArgumentNullException(nameof(output));
        if (!input.CanWrite || !output.CanRead)
            throw new CadContractException("invalid_protocol_stream", nameof(input));
    }

    protected Stream Input { get; }
    protected Stream Output { get; }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> json, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateJsonFrame(json.Span, MaximumWriteBytes);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await WriteFrameAsync(json, cancellationToken).ConfigureAwait(false);
            await Input.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public abstract ValueTask<byte[]> ReceiveAsync(CancellationToken cancellationToken);

    public virtual ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        _writeGate.Dispose();
        return ValueTask.CompletedTask;
    }

    protected abstract ValueTask WriteFrameAsync(ReadOnlyMemory<byte> json, CancellationToken cancellationToken);

    protected void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(GetType().Name);
    }

    protected static byte[] CompleteFrame(ArrayBufferWriter<byte> writer)
    {
        var result = writer.WrittenSpan.ToArray();
        ValidateJsonFrame(result, MaximumFrameBytes);
        return result;
    }

    protected static void ValidateJsonFrame(ReadOnlySpan<byte> json, int maximumBytes)
    {
        if (json.Length == 0 || json.Length > maximumBytes)
            throw new CadProtocolException("protocol_frame_size_rejected");
        if (json.StartsWith(Encoding.UTF8.Preamble))
            throw new CadProtocolException("protocol_bom_rejected");
        try
        {
            _ = new UTF8Encoding(false, true).GetString(json);
            using var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new CadProtocolException("protocol_object_required");
        }
        catch (CadProtocolException)
        {
            throw;
        }
        catch (Exception exception) when (exception is DecoderFallbackException or JsonException)
        {
            throw new CadProtocolException("malformed_protocol_json", innerException: exception);
        }
    }
}

internal sealed class CadNdjsonTransport : CadJsonFrameTransport
{
    public CadNdjsonTransport(Stream input, Stream output) : base(input, output) { }

    public override async ValueTask<byte[]> ReceiveAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var writer = new ArrayBufferWriter<byte>(4_096);
        var single = new byte[1];
        while (true)
        {
            var count = await Output.ReadAsync(single, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                if (writer.WrittenCount == 0) throw new CadProtocolException("protocol_stream_closed");
                throw new CadProtocolException("truncated_ndjson_frame");
            }
            var value = single[0];
            if (value == (byte)'\n') return CompleteFrame(writer);
            if (value is (byte)'\r' or 0)
                throw new CadProtocolException("invalid_ndjson_delimiter");
            if (writer.WrittenCount >= MaximumFrameBytes)
                throw new CadProtocolException("protocol_frame_size_rejected");
            writer.GetSpan(1)[0] = value;
            writer.Advance(1);
        }
    }

    protected override async ValueTask WriteFrameAsync(ReadOnlyMemory<byte> json, CancellationToken cancellationToken)
    {
        if (json.Span.IndexOfAny((byte)'\r', (byte)'\n') >= 0)
            throw new CadProtocolException("multiline_ndjson_rejected");
        await Input.WriteAsync(json, cancellationToken).ConfigureAwait(false);
        await Input.WriteAsync(new byte[] { (byte)'\n' }, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class CadContentLengthTransport : CadJsonFrameTransport
{
    private const int MaximumHeaderBytes = 8_192;

    public CadContentLengthTransport(Stream input, Stream output) : base(input, output) { }

    public override async ValueTask<byte[]> ReceiveAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var header = new ArrayBufferWriter<byte>(256);
        var single = new byte[1];
        var delimiterState = 0;
        while (delimiterState != 4)
        {
            var count = await Output.ReadAsync(single, cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new CadProtocolException("truncated_content_length_header");
            if (header.WrittenCount >= MaximumHeaderBytes)
                throw new CadProtocolException("protocol_header_size_rejected");
            var value = single[0];
            if (value == 0 || value > 0x7f)
                throw new CadProtocolException("invalid_protocol_header");
            header.GetSpan(1)[0] = value;
            header.Advance(1);
            delimiterState = (delimiterState, value) switch
            {
                (0, (byte)'\r') => 1,
                (1, (byte)'\n') => 2,
                (2, (byte)'\r') => 3,
                (3, (byte)'\n') => 4,
                (_, (byte)'\r') => 1,
                _ => 0,
            };
        }

        var headerText = Encoding.ASCII.GetString(header.WrittenSpan[..^4]);
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        long? contentLength = null;
        foreach (var line in lines)
        {
            if (line.Length == 0 || line[0] is ' ' or '\t')
                throw new CadProtocolException("invalid_protocol_header");
            var separator = line.IndexOf(':');
            if (separator <= 0)
                throw new CadProtocolException("invalid_protocol_header");
            var name = line[..separator];
            var value = line[(separator + 1)..].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (contentLength is not null || !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                    throw new CadProtocolException("invalid_content_length");
                contentLength = parsed;
            }
            else if (!name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                throw new CadProtocolException("unsupported_protocol_header");
            }
        }
        if (contentLength is null or <= 0 or > MaximumFrameBytes)
            throw new CadProtocolException("protocol_frame_size_rejected");

        var payload = new byte[checked((int)contentLength.Value)];
        var offset = 0;
        while (offset < payload.Length)
        {
            var count = await Output.ReadAsync(payload.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new CadProtocolException("truncated_content_length_frame");
            offset += count;
        }
        ValidateJsonFrame(payload, MaximumFrameBytes);
        return payload;
    }

    protected override async ValueTask WriteFrameAsync(ReadOnlyMemory<byte> json, CancellationToken cancellationToken)
    {
        var header = Encoding.ASCII.GetBytes($"Content-Length: {json.Length.ToString(CultureInfo.InvariantCulture)}\r\n\r\n");
        await Input.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await Input.WriteAsync(json, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class CadJsonRpcClient : IAsyncDisposable
{
    private readonly ICadJsonFrameTransport _transport;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly TimeSpan _requestTimeout;
    private readonly IReadOnlySet<string> _allowedNotifications;
    private long _nextRequestId;
    private int _terminal;

    public CadJsonRpcClient(
        ICadJsonFrameTransport transport,
        TimeSpan requestTimeout,
        IEnumerable<string>? allowedNotifications = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        if (requestTimeout <= TimeSpan.Zero || requestTimeout > TimeSpan.FromMinutes(5))
            throw new CadContractException("invalid_timeout", nameof(requestTimeout));
        _requestTimeout = requestTimeout;
        var notifications = (allowedNotifications ?? []).ToArray();
        if (notifications.Length > 64 || notifications.Distinct(StringComparer.Ordinal).Count() != notifications.Length)
            throw new CadContractException("invalid_notification_allowlist", nameof(allowedNotifications));
        foreach (var notification in notifications) ValidateMethod(notification);
        _allowedNotifications = new HashSet<string>(notifications, StringComparer.Ordinal);
    }

    public bool IsTerminal => Volatile.Read(ref _terminal) != 0;

    public async ValueTask<JsonElement> RequestAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken = default)
    {
        ValidateMethod(method);
        ThrowIfTerminal();
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfTerminal();
            var requestId = checked(Interlocked.Increment(ref _nextRequestId));
            var frame = JsonSerializer.SerializeToUtf8Bytes(new
            {
                jsonrpc = "2.0",
                id = requestId,
                method,
                @params = parameters ?? new { },
            });
            using var timeout = new CancellationTokenSource(_requestTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                await _transport.SendAsync(frame, linked.Token).ConfigureAwait(false);
                for (var frameCount = 0; frameCount < 65; frameCount++)
                {
                    var response = await _transport.ReceiveAsync(linked.Token).ConfigureAwait(false);
                    var parsed = ParseResponse(response, requestId, _allowedNotifications);
                    if (parsed is not null) return parsed.Value;
                }
                throw new CadProtocolException("protocol_notification_flood");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Interlocked.Exchange(ref _terminal, 1);
                throw new CadProtocolException("protocol_request_timeout");
            }
            catch (CadRemoteProtocolException)
            {
                throw;
            }
            catch
            {
                Interlocked.Exchange(ref _terminal, 1);
                throw;
            }
        }
        finally
        {
            _requestGate.Release();
        }
    }

    public async ValueTask NotifyAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken = default)
    {
        ValidateMethod(method);
        ThrowIfTerminal();
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfTerminal();
            var frame = JsonSerializer.SerializeToUtf8Bytes(new
            {
                jsonrpc = "2.0",
                method,
                @params = parameters ?? new { },
            });
            using var timeout = new CancellationTokenSource(_requestTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                await _transport.SendAsync(frame, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Interlocked.Exchange(ref _terminal, 1);
                throw new CadProtocolException("protocol_request_timeout");
            }
            catch
            {
                Interlocked.Exchange(ref _terminal, 1);
                throw;
            }
        }
        finally
        {
            _requestGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _terminal, 1);
        await _transport.DisposeAsync().ConfigureAwait(false);
        _requestGate.Dispose();
    }

    private void ThrowIfTerminal()
    {
        if (IsTerminal) throw new CadProtocolException("protocol_session_terminal");
    }

    private static void ValidateMethod(string method)
    {
        if (string.IsNullOrWhiteSpace(method) || method.Length > 128 ||
            method.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-' and not '.' and not '/' and not ':'))
            throw new CadContractException("invalid_protocol_method", nameof(method));
    }

    private static JsonElement? ParseResponse(
        ReadOnlySpan<byte> bytes,
        long expectedId,
        IReadOnlySet<string> allowedNotifications)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new CadProtocolException("protocol_object_required");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new CadProtocolException("duplicate_protocol_property");
                if (property.Name is not "jsonrpc" and not "id" and not "result" and not "error" and
                    not "method" and not "params")
                    throw new CadProtocolException("unexpected_protocol_property");
            }
            if (!root.TryGetProperty("jsonrpc", out var version) || version.ValueKind != JsonValueKind.String ||
                version.GetString() != "2.0")
                throw new CadProtocolException("protocol_version_mismatch");
            if (!root.TryGetProperty("id", out var id))
            {
                if (!root.TryGetProperty("method", out var notificationMethod) ||
                    notificationMethod.ValueKind != JsonValueKind.String ||
                    !allowedNotifications.Contains(notificationMethod.GetString()!) ||
                    names.Any(name => name is "result" or "error") ||
                    names.Any(name => name is not "jsonrpc" and not "method" and not "params"))
                    throw new CadProtocolException("foreign_or_stale_protocol_frame");
                if (root.TryGetProperty("params", out var notificationParameters) &&
                    notificationParameters.ValueKind is not JsonValueKind.Object and not JsonValueKind.Array)
                    throw new CadProtocolException("invalid_protocol_notification");
                return null;
            }
            if (names.Any(name => name is "method" or "params") || id.ValueKind != JsonValueKind.Number ||
                !id.TryGetInt64(out var actualId) || actualId != expectedId)
                throw new CadProtocolException("foreign_or_stale_protocol_frame");
            var hasResult = root.TryGetProperty("result", out var result);
            var hasError = root.TryGetProperty("error", out var error);
            if (hasResult == hasError)
                throw new CadProtocolException("invalid_protocol_response_shape");
            if (hasError)
            {
                if (error.ValueKind != JsonValueKind.Object)
                    throw new CadProtocolException("invalid_protocol_error");
                var errorNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in error.EnumerateObject())
                {
                    if (!errorNames.Add(property.Name) || property.Name is not "code" and not "message" and not "data")
                        throw new CadProtocolException("invalid_protocol_error");
                }
                if (!error.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Number ||
                    !code.TryGetInt64(out var remoteCode) ||
                    !error.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String)
                    throw new CadProtocolException("invalid_protocol_error");
                throw new CadRemoteProtocolException(remoteCode);
            }
            return result.Clone();
        }
        catch (CadProtocolException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new CadProtocolException("malformed_protocol_json", innerException: exception);
        }
    }
}
