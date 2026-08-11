using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace HermesDeveloperServices;

public static class LspMessageCodec
{
    internal static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static byte[] EncodeFrame(LspOutgoingMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, message.GetType(), SerializerOptions);
        return AddHeader(payload);
    }

    public static byte[] EncodeFrame<TMessage>(TMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return AddHeader(JsonSerializer.SerializeToUtf8Bytes(message, SerializerOptions));
    }

    public static LspIncomingMessage DecodeIncoming(ReadOnlySpan<byte> payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("jsonrpc", out var version)
                || version.ValueKind != JsonValueKind.String
                || version.GetString() != "2.0")
            {
                throw new LspProtocolException("The LSP payload does not declare JSON-RPC 2.0.");
            }

            var hasMethod = root.TryGetProperty("method", out var methodElement);
            var hasId = root.TryGetProperty("id", out var idElement);
            if (hasMethod)
            {
                if (methodElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(methodElement.GetString()))
                {
                    throw new LspProtocolException("The LSP method is invalid.");
                }

                var method = methodElement.GetString()!;
                var parameters = root.TryGetProperty("params", out var paramsElement) ? paramsElement.Clone() : (JsonElement?)null;
                if (!hasId) return new LspIncomingNotification(method, parameters);
                return new LspIncomingServerRequest(ReadId(idElement), method, parameters);
            }

            if (!hasId) throw new LspProtocolException("The LSP payload is neither a response nor a notification.");
            var result = root.TryGetProperty("result", out var resultElement) ? resultElement.Clone() : (JsonElement?)null;
            LspError? error = null;
            if (root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind is not JsonValueKind.Null)
            {
                error = JsonSerializer.Deserialize<LspError>(errorElement, SerializerOptions)
                    ?? throw new LspProtocolException("The LSP error response is empty.");
            }

            if (result is null && error is null)
            {
                throw new LspProtocolException("The LSP response has neither result nor error.");
            }

            return new LspIncomingResponse(ReadId(idElement), result, error);
        }
        catch (JsonException exception)
        {
            throw new LspProtocolException("The LSP payload is not valid JSON.", exception);
        }
    }

    private static int ReadId(JsonElement idElement) =>
        idElement.ValueKind == JsonValueKind.Number && idElement.TryGetInt32(out var id) && id >= 0
            ? id
            : throw new LspProtocolException("The LSP response identifier is invalid.");

    private static byte[] AddHeader(byte[] payload)
    {
        var header = Encoding.ASCII.GetBytes(
            $"Content-Length: {payload.Length.ToString(CultureInfo.InvariantCulture)}\r\n\r\n");
        var frame = GC.AllocateUninitializedArray<byte>(header.Length + payload.Length);
        header.CopyTo(frame, 0);
        payload.CopyTo(frame, header.Length);
        return frame;
    }
}

/// <summary>
/// Incrementally decodes bounded LSP Content-Length frames before allocating their payloads.
/// </summary>
public sealed class LspFrameDecoder : IDisposable
{
    public const int DefaultMaximumHeaderBytes = 8 * 1024;
    public const int DefaultMaximumPayloadBytes = 4 * 1024 * 1024;

    private static readonly byte[] HeaderDelimiter = "\r\n\r\n"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly int _maximumHeaderBytes;
    private readonly int _maximumPayloadBytes;
    private byte[]? _buffer;
    private int _count;

    public LspFrameDecoder(
        int maximumHeaderBytes = DefaultMaximumHeaderBytes,
        int maximumPayloadBytes = DefaultMaximumPayloadBytes)
    {
        if (maximumHeaderBytes < HeaderDelimiter.Length) throw new ArgumentOutOfRangeException(nameof(maximumHeaderBytes));
        if (maximumPayloadBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));
        _maximumHeaderBytes = maximumHeaderBytes;
        _maximumPayloadBytes = maximumPayloadBytes;
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Min(maximumHeaderBytes, 1024));
    }

    public IReadOnlyList<byte[]> Append(ReadOnlySpan<byte> bytes)
    {
        ObjectDisposedException.ThrowIf(_buffer is null, this);
        if (bytes.Length > _maximumHeaderBytes + _maximumPayloadBytes - _count)
        {
            throw new LspProtocolException("The buffered LSP frame exceeds the configured limit.");
        }

        EnsureCapacity(_count + bytes.Length);
        bytes.CopyTo(_buffer.AsSpan(_count));
        _count += bytes.Length;
        var frames = new List<byte[]>();
        while (TryReadFrame(out var frame)) frames.Add(frame);
        return frames;
    }

    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
        GC.SuppressFinalize(this);
    }

    private bool TryReadFrame(out byte[] frame)
    {
        frame = Array.Empty<byte>();
        var buffer = _buffer ?? throw new ObjectDisposedException(nameof(LspFrameDecoder));
        var headerEnd = buffer.AsSpan(0, _count).IndexOf(HeaderDelimiter);
        if (headerEnd < 0)
        {
            if (_count > _maximumHeaderBytes) throw new LspProtocolException("The LSP frame header exceeds the configured limit.");
            return false;
        }

        if (headerEnd > _maximumHeaderBytes) throw new LspProtocolException("The LSP frame header exceeds the configured limit.");
        var payloadLength = ParseContentLength(buffer.AsSpan(0, headerEnd));
        if (payloadLength > _maximumPayloadBytes) throw new LspProtocolException("The LSP payload exceeds the configured limit.");
        var payloadStart = headerEnd + HeaderDelimiter.Length;
        if (_count - payloadStart < payloadLength) return false;

        frame = GC.AllocateUninitializedArray<byte>(payloadLength);
        buffer.AsSpan(payloadStart, payloadLength).CopyTo(frame);
        try { _ = StrictUtf8.GetCharCount(frame); }
        catch (DecoderFallbackException exception) { throw new LspProtocolException("The LSP payload is not valid UTF-8.", exception); }
        var consumed = payloadStart + payloadLength;
        buffer.AsSpan(consumed, _count - consumed).CopyTo(buffer);
        _count -= consumed;
        return true;
    }

    private static int ParseContentLength(ReadOnlySpan<byte> headerBytes)
    {
        var header = Encoding.ASCII.GetString(headerBytes);
        int? contentLength = null;
        foreach (var line in header.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0) throw new LspProtocolException("The LSP frame header is malformed.");
            var name = line[..separator].Trim();
            if (!name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (contentLength is not null
                || !int.TryParse(line[(separator + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                || parsed < 0)
            {
                throw new LspProtocolException("The LSP Content-Length header is invalid.");
            }
            contentLength = parsed;
        }
        return contentLength ?? throw new LspProtocolException("The LSP frame is missing Content-Length.");
    }

    private void EnsureCapacity(int required)
    {
        var buffer = _buffer ?? throw new ObjectDisposedException(nameof(LspFrameDecoder));
        if (required <= buffer.Length) return;
        var replacement = ArrayPool<byte>.Shared.Rent(required);
        buffer.AsSpan(0, _count).CopyTo(replacement);
        ArrayPool<byte>.Shared.Return(buffer);
        _buffer = replacement;
    }

    ~LspFrameDecoder()
    {
        if (_buffer is not null) ArrayPool<byte>.Shared.Return(_buffer);
    }
}
