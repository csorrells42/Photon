using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using HermesCredentialBroker;

namespace HermesCredentialBroker.Runtime;

public static class CredentialLeaseWireCodec
{
    private static readonly byte[] RequestMagic = "HCR2"u8.ToArray();
    private static readonly byte[] ResponseMagic = "HCS2"u8.ToArray();

    public static CredentialRuntimeLeaseRequest DecodeRequest(ReadOnlySpan<byte> payload, CredentialContainerBinding binding)
    {
        if (payload.Length < 4 || payload.Length > 4096 || !payload[..4].SequenceEqual(RequestMagic))
        {
            throw new CredentialRuntimeException("invalid_lease_request", "The credential lease request is invalid.");
        }
        var reader = new WireReader(payload[4..]);
        var requestId = reader.ReadText(128);
        var connectionRef = new CredentialReference(reader.ReadText(64));
        var profileId = reader.ReadText(128);
        var purpose = reader.ReadText(160);
        var expectedRevision = reader.ReadInt64();
        var nonce = reader.ReadBytes(32, exactLength: 32);
        reader.RequireEnd();
        try
        {
            return new CredentialRuntimeLeaseRequest(requestId, connectionRef, profileId, purpose, expectedRevision, nonce).Validate(binding);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(nonce);
            throw;
        }
    }

    public static byte[] EncodeRequest(CredentialRuntimeLeaseRequest request, CredentialContainerBinding binding)
    {
        request.Validate(binding);
        using var stream = new MemoryStream();
        stream.Write(RequestMagic);
        CredentialRuntimeBootstrap.WriteText(stream, request.RequestId);
        CredentialRuntimeBootstrap.WriteText(stream, request.ConnectionRef.Value);
        CredentialRuntimeBootstrap.WriteText(stream, request.ProfileId);
        CredentialRuntimeBootstrap.WriteText(stream, request.Purpose);
        CredentialRuntimeBootstrap.WriteInt64(stream, request.ExpectedRevision);
        CredentialRuntimeBootstrap.WriteBytes(stream, request.RequestNonce);
        return stream.ToArray();
    }

    public static byte[] EncodeSuccess(CredentialRuntimeLeaseRequest request, CredentialLease lease, DateTimeOffset expiresAt)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(lease);
        if (!StringComparer.Ordinal.Equals(request.ConnectionRef.Value, lease.Metadata.ConnectionRef.Value)
            || request.ExpectedRevision != lease.Metadata.Revision
            || !StringComparer.Ordinal.Equals(request.Purpose, lease.Purpose))
        {
            throw new CredentialRuntimeException("lease_binding_mismatch", "The credential lease does not match its request.");
        }
        using var stream = new MemoryStream();
        stream.Write(ResponseMagic);
        stream.WriteByte(0);
        CredentialRuntimeBootstrap.WriteText(stream, request.RequestId);
        CredentialRuntimeBootstrap.WriteBytes(stream, request.RequestNonce);
        CredentialRuntimeBootstrap.WriteText(stream, request.ConnectionRef.Value);
        CredentialRuntimeBootstrap.WriteInt64(stream, lease.Metadata.Revision);
        CredentialRuntimeBootstrap.WriteInt64(stream, expiresAt.ToUnixTimeSeconds());
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, lease.SecretLength);
        stream.Write(length);
        var secret = new byte[lease.SecretLength];
        try
        {
            lease.CopySecretTo(secret);
            stream.Write(secret);
            return stream.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            if (stream.TryGetBuffer(out var written)) CryptographicOperations.ZeroMemory(written.AsSpan());
        }
    }

    public static byte[] EncodeFailure(CredentialRuntimeLeaseRequest request, string code, bool retryable)
    {
        ArgumentNullException.ThrowIfNull(request);
        var safeCode = RuntimeText.Identifier(code, nameof(code), 64);
        using var stream = new MemoryStream();
        stream.Write(ResponseMagic);
        stream.WriteByte(1);
        CredentialRuntimeBootstrap.WriteText(stream, request.RequestId);
        CredentialRuntimeBootstrap.WriteBytes(stream, request.RequestNonce);
        CredentialRuntimeBootstrap.WriteText(stream, safeCode);
        stream.WriteByte(retryable ? (byte)1 : (byte)0);
        return stream.ToArray();
    }

    internal static CredentialLeaseWireResponse DecodeResponse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 5 || payload.Length > CredentialRuntimeProtocol.MaximumEncryptedFrameBytes - 30
            || !payload[..4].SequenceEqual(ResponseMagic))
        {
            throw new CredentialRuntimeException("invalid_lease_response", "The credential lease response is invalid.");
        }
        var status = payload[4];
        var reader = new WireReader(payload[5..]);
        var requestId = reader.ReadText(128);
        var requestNonce = reader.ReadBytes(32, exactLength: 32);
        try
        {
            if (status == 0)
            {
                var connectionRef = new CredentialReference(reader.ReadText(64));
                var revision = reader.ReadInt64();
                var expiresAt = DateTimeOffset.FromUnixTimeSeconds(reader.ReadInt64());
                var secretLength = reader.ReadInt32();
                if (secretLength <= 0 || secretLength > HermesCredentialBrokerProtocol.MaximumSecretBytes)
                {
                    throw new CredentialRuntimeException("invalid_lease_response", "The credential lease response is invalid.");
                }
                var secret = reader.ReadRaw(secretLength);
                try
                {
                    reader.RequireEnd();
                    var response = CredentialLeaseWireResponse.Success(requestId, requestNonce, connectionRef, revision, expiresAt, secret);
                    secret = null!;
                    return response;
                }
                finally
                {
                    if (secret is not null) CryptographicOperations.ZeroMemory(secret);
                }
            }
            if (status == 1)
            {
                var code = RuntimeText.Identifier(reader.ReadText(64), "code", 64);
                var retryable = reader.ReadByte() switch
                {
                    0 => false,
                    1 => true,
                    _ => throw new CredentialRuntimeException("invalid_lease_response", "The credential lease response is invalid."),
                };
                reader.RequireEnd();
                return CredentialLeaseWireResponse.Failure(requestId, requestNonce, code, retryable);
            }
            throw new CredentialRuntimeException("invalid_lease_response", "The credential lease response is invalid.");
        }
        catch
        {
            CryptographicOperations.ZeroMemory(requestNonce);
            throw;
        }
    }

    private ref struct WireReader
    {
        private ReadOnlySpan<byte> _remaining;

        internal WireReader(ReadOnlySpan<byte> payload) => _remaining = payload;

        internal string ReadText(int maximumBytes)
        {
            var bytes = ReadBytes(maximumBytes);
            try
            {
                var value = new UTF8Encoding(false, true).GetString(bytes);
                if (value.IndexOf('\0') >= 0) throw new CredentialRuntimeException("invalid_wire_text", "A runtime protocol field is invalid.");
                return value;
            }
            catch (DecoderFallbackException)
            {
                throw new CredentialRuntimeException("invalid_wire_text", "A runtime protocol field is invalid.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        internal byte[] ReadBytes(int maximumBytes, int? exactLength = null)
        {
            if (_remaining.Length < 2) throw new CredentialRuntimeException("truncated_frame", "The runtime protocol frame is truncated.");
            var length = BinaryPrimitives.ReadUInt16BigEndian(_remaining[..2]);
            _remaining = _remaining[2..];
            if (length > maximumBytes || (exactLength.HasValue && length != exactLength.Value) || _remaining.Length < length)
            {
                throw new CredentialRuntimeException("invalid_field_length", "A runtime protocol field has an invalid length.");
            }
            var value = _remaining[..length].ToArray();
            _remaining = _remaining[length..];
            return value;
        }

        internal byte[] ReadRaw(int length)
        {
            if (length < 0 || _remaining.Length < length) throw new CredentialRuntimeException("truncated_frame", "The runtime protocol frame is truncated.");
            var value = _remaining[..length].ToArray();
            _remaining = _remaining[length..];
            return value;
        }

        internal long ReadInt64()
        {
            if (_remaining.Length < 8) throw new CredentialRuntimeException("truncated_frame", "The runtime protocol frame is truncated.");
            var value = BinaryPrimitives.ReadInt64BigEndian(_remaining[..8]);
            _remaining = _remaining[8..];
            return value;
        }

        internal int ReadInt32()
        {
            if (_remaining.Length < 4) throw new CredentialRuntimeException("truncated_frame", "The runtime protocol frame is truncated.");
            var value = BinaryPrimitives.ReadInt32BigEndian(_remaining[..4]);
            _remaining = _remaining[4..];
            return value;
        }

        internal byte ReadByte()
        {
            if (_remaining.IsEmpty) throw new CredentialRuntimeException("truncated_frame", "The runtime protocol frame is truncated.");
            var value = _remaining[0];
            _remaining = _remaining[1..];
            return value;
        }

        internal void RequireEnd()
        {
            if (!_remaining.IsEmpty) throw new CredentialRuntimeException("trailing_frame_data", "The runtime protocol frame has trailing data.");
        }
    }
}

internal sealed class CredentialLeaseWireResponse : IDisposable
{
    private CredentialLeaseWireResponse(
        string requestId,
        byte[] requestNonce,
        bool succeeded,
        CredentialReference? connectionRef,
        long revision,
        DateTimeOffset expiresAt,
        byte[]? secret,
        string? errorCode,
        bool retryable)
    {
        RequestId = requestId;
        RequestNonce = requestNonce;
        Succeeded = succeeded;
        ConnectionRef = connectionRef;
        Revision = revision;
        ExpiresAt = expiresAt;
        Secret = secret;
        ErrorCode = errorCode;
        Retryable = retryable;
    }

    public string RequestId { get; }
    public byte[] RequestNonce { get; }
    public bool Succeeded { get; }
    public CredentialReference? ConnectionRef { get; }
    public long Revision { get; }
    public DateTimeOffset ExpiresAt { get; }
    public byte[]? Secret { get; private set; }
    public string? ErrorCode { get; }
    public bool Retryable { get; }

    internal static CredentialLeaseWireResponse Success(string requestId, byte[] nonce, CredentialReference reference, long revision, DateTimeOffset expiresAt, byte[] secret) =>
        new(requestId, nonce, true, reference, revision, expiresAt, secret, null, false);

    internal static CredentialLeaseWireResponse Failure(string requestId, byte[] nonce, string code, bool retryable) =>
        new(requestId, nonce, false, null, 0, default, null, code, retryable);

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(RequestNonce);
        if (Secret is not null)
        {
            CryptographicOperations.ZeroMemory(Secret);
            Secret = null;
        }
    }
}
