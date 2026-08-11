using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace HermesDeveloperServices;

public static class DapMessageCodec
{
    internal static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static byte[] EncodeFrame<TMessage>(TMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, SerializerOptions);
        var header = Encoding.ASCII.GetBytes(
            $"Content-Length: {payload.Length.ToString(CultureInfo.InvariantCulture)}\r\n\r\n");
        var frame = GC.AllocateUninitializedArray<byte>(header.Length + payload.Length);
        header.CopyTo(frame, 0);
        payload.CopyTo(frame, header.Length);
        return frame;
    }

    public static DapIncomingMessage DecodeIncoming(ReadOnlySpan<byte> payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload.ToArray());
            if (!document.RootElement.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String)
            {
                throw new DapProtocolException("The DAP payload does not declare a message type.");
            }

            return typeElement.GetString() switch
            {
                "response" => new DapIncomingResponse(
                    JsonSerializer.Deserialize<DapResponse>(payload, SerializerOptions)
                    ?? throw new DapProtocolException("The DAP response payload is empty.")),
                "event" => new DapIncomingEvent(
                    JsonSerializer.Deserialize<DapEvent>(payload, SerializerOptions)
                    ?? throw new DapProtocolException("The DAP event payload is empty.")),
                _ => throw new DapProtocolException("The DAP adapter sent an unsupported message type."),
            };
        }
        catch (JsonException exception)
        {
            throw new DapProtocolException("The DAP payload is not valid JSON.", exception);
        }
    }
}

/// <summary>
/// Incrementally decodes DAP Content-Length frames. Limits are enforced before payload allocation so
/// a malformed or hostile adapter cannot cause unbounded buffering.
/// </summary>
public sealed class DapFrameDecoder
{
    public const int DefaultMaximumHeaderBytes = 8 * 1024;
    public const int DefaultMaximumPayloadBytes = 4 * 1024 * 1024;

    private static readonly byte[] HeaderDelimiter = "\r\n\r\n"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly int _maximumHeaderBytes;
    private readonly int _maximumPayloadBytes;
    private byte[] _buffer;
    private int _count;

    public DapFrameDecoder(
        int maximumHeaderBytes = DefaultMaximumHeaderBytes,
        int maximumPayloadBytes = DefaultMaximumPayloadBytes)
    {
        if (maximumHeaderBytes < HeaderDelimiter.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumHeaderBytes));
        }

        if (maximumPayloadBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));
        }

        _maximumHeaderBytes = maximumHeaderBytes;
        _maximumPayloadBytes = maximumPayloadBytes;
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Min(maximumHeaderBytes, 1024));
    }

    public IReadOnlyList<byte[]> Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > _maximumHeaderBytes + _maximumPayloadBytes - _count)
        {
            throw new DapProtocolException("The buffered DAP frame exceeds the configured limit.");
        }

        EnsureCapacity(_count + bytes.Length);
        bytes.CopyTo(_buffer.AsSpan(_count));
        _count += bytes.Length;

        var frames = new List<byte[]>();
        while (TryReadFrame(out var frame))
        {
            frames.Add(frame);
        }

        return frames;
    }

    private bool TryReadFrame(out byte[] frame)
    {
        frame = Array.Empty<byte>();
        var headerEnd = _buffer.AsSpan(0, _count).IndexOf(HeaderDelimiter);
        if (headerEnd < 0)
        {
            if (_count > _maximumHeaderBytes)
            {
                throw new DapProtocolException("The DAP frame header exceeds the configured limit.");
            }

            return false;
        }

        if (headerEnd > _maximumHeaderBytes)
        {
            throw new DapProtocolException("The DAP frame header exceeds the configured limit.");
        }

        var payloadLength = ParseContentLength(_buffer.AsSpan(0, headerEnd));
        if (payloadLength > _maximumPayloadBytes)
        {
            throw new DapProtocolException("The DAP payload exceeds the configured limit.");
        }

        var payloadStart = headerEnd + HeaderDelimiter.Length;
        if (_count - payloadStart < payloadLength)
        {
            return false;
        }

        frame = GC.AllocateUninitializedArray<byte>(payloadLength);
        _buffer.AsSpan(payloadStart, payloadLength).CopyTo(frame);
        try
        {
            _ = StrictUtf8.GetCharCount(frame);
        }
        catch (DecoderFallbackException exception)
        {
            throw new DapProtocolException("The DAP payload is not valid UTF-8.", exception);
        }

        var consumed = payloadStart + payloadLength;
        _buffer.AsSpan(consumed, _count - consumed).CopyTo(_buffer);
        _count -= consumed;
        return true;
    }

    private static int ParseContentLength(ReadOnlySpan<byte> headerBytes)
    {
        string header;
        try
        {
            header = Encoding.ASCII.GetString(headerBytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new DapProtocolException("The DAP frame header is invalid.", exception);
        }

        int? contentLength = null;
        foreach (var line in header.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                throw new DapProtocolException("The DAP frame header is malformed.");
            }

            var name = line[..separator].Trim();
            if (!name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (contentLength is not null
                || !int.TryParse(
                    line[(separator + 1)..].Trim(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var parsed)
                || parsed < 0)
            {
                throw new DapProtocolException("The DAP Content-Length header is invalid.");
            }

            contentLength = parsed;
        }

        return contentLength
            ?? throw new DapProtocolException("The DAP frame is missing Content-Length.");
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _buffer.Length)
        {
            return;
        }

        var replacement = ArrayPool<byte>.Shared.Rent(required);
        _buffer.AsSpan(0, _count).CopyTo(replacement);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = replacement;
    }

    ~DapFrameDecoder()
    {
        ArrayPool<byte>.Shared.Return(_buffer);
    }
}
