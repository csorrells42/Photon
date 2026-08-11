using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HermesCredentialBroker.Runtime;

public sealed record CredentialHostHello(
    int Version,
    string Type,
    string SessionId,
    string HostPublicKey,
    string HostNonce,
    string Proof);

public sealed record CredentialServerHello(
    int Version,
    string Type,
    string SessionId,
    string ContainerId,
    string ImageDigest,
    string UserSid,
    string MachineId,
    string InstallId,
    string ProfileId,
    long ExpiresAtUnix,
    string ServerPublicKey,
    string ServerNonce,
    string Proof);

public sealed class CredentialRuntimeCryptography : IDisposable
{
    private static readonly HashSet<string> ServerHelloFields = new(StringComparer.Ordinal)
    {
        "version", "type", "sessionId", "containerId", "imageDigest", "userSid", "machineId",
        "installId", "profileId", "expiresAtUnix", "serverPublicKey", "serverNonce", "proof",
    };
    private readonly CredentialRuntimeBootstrap _bootstrap;
    private readonly byte[] _hostNonce;
    private bool _established;
    private bool _disposed;

    public CredentialRuntimeCryptography(CredentialRuntimeBootstrap bootstrap, ReadOnlySpan<byte> hostNonce = default)
    {
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        _hostNonce = hostNonce.IsEmpty ? RandomNumberGenerator.GetBytes(32) : hostNonce.ToArray();
        if (_hostNonce.Length != 32 || _hostNonce.All(value => value == 0))
        {
            CryptographicOperations.ZeroMemory(_hostNonce);
            throw new CredentialRuntimeException("invalid_host_nonce", "The host handshake nonce is invalid.");
        }
    }

    public CredentialHostHello CreateHostHello()
    {
        ThrowIfDisposed();
        var publicKey = _bootstrap.HostKey.ExportSubjectPublicKeyInfo();
        byte[]? transcript = null;
        byte[]? proof = null;
        try
        {
            transcript = RuntimeTranscript.EncodeHost(_bootstrap.SessionId, _bootstrap.Binding, publicKey, _hostNonce);
            proof = HMACSHA256.HashData(_bootstrap.BootstrapKey, transcript);
            return new CredentialHostHello(
                CredentialRuntimeProtocol.Version,
                "host-hello",
                _bootstrap.SessionId,
                Base64Url.Encode(publicKey),
                Base64Url.Encode(_hostNonce),
                Base64Url.Encode(proof));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(publicKey);
            if (transcript is not null) CryptographicOperations.ZeroMemory(transcript);
            if (proof is not null) CryptographicOperations.ZeroMemory(proof);
        }
    }

    public CredentialDirectionalCipher AcceptServerHello(CredentialServerHello hello, DateTimeOffset now)
    {
        ThrowIfDisposed();
        if (_established) throw new CredentialRuntimeException("handshake_reused", "The runtime handshake was already completed.");
        ArgumentNullException.ThrowIfNull(hello);
        if (hello.Version != CredentialRuntimeProtocol.Version
            || !StringComparer.Ordinal.Equals(hello.Type, "server-hello")
            || !StringComparer.Ordinal.Equals(hello.SessionId, _bootstrap.SessionId))
        {
            throw new CredentialRuntimeException("handshake_mismatch", "The runtime handshake does not match this session.");
        }
        var echoedPrincipal = new HermesCredentialBroker.CredentialPrincipalBinding(hello.UserSid, hello.MachineId, hello.InstallId, hello.ProfileId);
        var echoedBinding = new CredentialContainerBinding(
            hello.ContainerId,
            hello.ImageDigest,
            echoedPrincipal,
            DateTimeOffset.FromUnixTimeSeconds(hello.ExpiresAtUnix));
        echoedBinding.ValidateLifetime(now);
        if (!_bootstrap.Binding.ExactlyMatches(echoedBinding))
        {
            throw new CredentialRuntimeException("container_binding_mismatch", "The runtime container binding changed.");
        }

        var hostPublic = _bootstrap.HostKey.ExportSubjectPublicKeyInfo();
        var serverPublic = Base64Url.Decode(hello.ServerPublicKey, 512);
        var serverNonce = Base64Url.Decode(hello.ServerNonce, 32);
        var suppliedProof = Base64Url.Decode(hello.Proof, 32);
        byte[]? transcript = null;
        byte[]? expectedProof = null;
        byte[]? sharedSecret = null;
        byte[]? saltInput = null;
        byte[]? salt = null;
        byte[]? info = null;
        var output = new byte[64];
        try
        {
            if (serverNonce.Length != 32 || serverNonce.All(value => value == 0))
            {
                throw new CredentialRuntimeException("invalid_server_nonce", "The server handshake nonce is invalid.");
            }
            transcript = RuntimeTranscript.EncodeServer(_bootstrap.SessionId, _bootstrap.Binding, hostPublic, _hostNonce, serverPublic, serverNonce);
            expectedProof = HMACSHA256.HashData(_bootstrap.BootstrapKey, transcript);
            if (!CryptographicOperations.FixedTimeEquals(expectedProof, suppliedProof))
            {
                throw new CredentialRuntimeException("handshake_auth_failed", "The runtime handshake authentication failed.");
            }
            using var serverKey = ECDiffieHellman.Create();
            serverKey.ImportSubjectPublicKeyInfo(serverPublic, out var read);
            if (read != serverPublic.Length) throw new CredentialRuntimeException("invalid_server_key", "The runtime server key is invalid.");
            sharedSecret = _bootstrap.HostKey.DeriveRawSecretAgreement(serverKey.PublicKey);
            saltInput = new byte[_hostNonce.Length + serverNonce.Length];
            _hostNonce.CopyTo(saltInput, 0);
            serverNonce.CopyTo(saltInput, _hostNonce.Length);
            salt = HMACSHA256.HashData(_bootstrap.BootstrapKey, saltInput);
            info = SHA256.HashData(transcript);
            HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, output, salt, info);
            _established = true;
            return new CredentialDirectionalCipher(_bootstrap.SessionId, output.AsSpan(0, 32), output.AsSpan(32, 32));
        }
        catch (CryptographicException)
        {
            throw new CredentialRuntimeException("handshake_crypto_failed", "The runtime handshake cryptography failed.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hostPublic);
            CryptographicOperations.ZeroMemory(serverPublic);
            CryptographicOperations.ZeroMemory(serverNonce);
            CryptographicOperations.ZeroMemory(suppliedProof);
            if (transcript is not null) CryptographicOperations.ZeroMemory(transcript);
            if (expectedProof is not null) CryptographicOperations.ZeroMemory(expectedProof);
            if (sharedSecret is not null) CryptographicOperations.ZeroMemory(sharedSecret);
            if (saltInput is not null) CryptographicOperations.ZeroMemory(saltInput);
            if (salt is not null) CryptographicOperations.ZeroMemory(salt);
            if (info is not null) CryptographicOperations.ZeroMemory(info);
            CryptographicOperations.ZeroMemory(output);
        }
    }

    public static string SerializeHello<T>(T hello) => JsonSerializer.Serialize(hello, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static CredentialServerHello ParseServerHello(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty || utf8.Length > CredentialRuntimeProtocol.MaximumHandshakeBytes)
        {
            throw new CredentialRuntimeException("invalid_server_hello", "The runtime server hello is invalid.");
        }
        try
        {
            using var document = JsonDocument.Parse(utf8.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new CredentialRuntimeException("invalid_server_hello", "The runtime server hello is invalid.");
            }
            var properties = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
            if (properties.Length != ServerHelloFields.Count
                || properties.Distinct(StringComparer.Ordinal).Count() != ServerHelloFields.Count
                || properties.Any(property => !ServerHelloFields.Contains(property)))
            {
                throw new CredentialRuntimeException("invalid_server_hello", "The runtime server hello is invalid.");
            }
            return JsonSerializer.Deserialize<CredentialServerHello>(utf8, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new CredentialRuntimeException("invalid_server_hello", "The runtime server hello is invalid.");
        }
        catch (JsonException)
        {
            throw new CredentialRuntimeException("invalid_server_hello", "The runtime server hello is invalid.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_hostNonce);
    }
}

public sealed class CredentialDirectionalCipher : IDisposable
{
    private static readonly byte[] FrameMagic = "HCF2"u8.ToArray();
    private readonly string _sessionId;
    private readonly byte[] _hostToContainerKey;
    private readonly byte[] _containerToHostKey;
    private ulong _sendSequence;
    private ulong _receiveSequence;
    private bool _disposed;

    internal CredentialDirectionalCipher(string sessionId, ReadOnlySpan<byte> hostToContainerKey, ReadOnlySpan<byte> containerToHostKey)
    {
        _sessionId = sessionId;
        _hostToContainerKey = hostToContainerKey.ToArray();
        _containerToHostKey = containerToHostKey.ToArray();
    }

    public byte[] EncryptHostResponse(ReadOnlySpan<byte> plaintext)
    {
        ThrowIfDisposed();
        var sequence = checked(++_sendSequence);
        return Encrypt(plaintext, _hostToContainerKey, direction: 2, sequence);
    }

    public byte[] DecryptContainerRequest(ReadOnlySpan<byte> frame)
    {
        ThrowIfDisposed();
        if (frame.Length < 4 + 1 + 1 + 8 + 16 || !frame[..4].SequenceEqual(FrameMagic)
            || frame[4] != CredentialRuntimeProtocol.Version || frame[5] != 1)
        {
            throw new CredentialRuntimeException("invalid_frame", "The runtime credential frame is invalid.");
        }
        var sequence = BinaryPrimitives.ReadUInt64BigEndian(frame.Slice(6, 8));
        if (sequence != checked(_receiveSequence + 1))
        {
            throw new CredentialRuntimeException("frame_replay", "The runtime credential frame sequence is invalid.");
        }
        var plaintext = Decrypt(frame, _containerToHostKey, direction: 1, sequence);
        _receiveSequence = sequence;
        return plaintext;
    }

    private byte[] Encrypt(ReadOnlySpan<byte> plaintext, byte[] key, byte direction, ulong sequence)
    {
        if (plaintext.IsEmpty || plaintext.Length > CredentialRuntimeProtocol.MaximumEncryptedFrameBytes - 30)
        {
            throw new CredentialRuntimeException("invalid_plaintext", "The runtime credential payload has an invalid size.");
        }
        var frame = new byte[14 + plaintext.Length + 16];
        FrameMagic.CopyTo(frame, 0);
        frame[4] = CredentialRuntimeProtocol.Version;
        frame[5] = direction;
        BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(6, 8), sequence);
        Span<byte> nonce = stackalloc byte[12];
        BuildNonce(direction, sequence, nonce);
        var aad = RuntimeTranscript.FrameAad(_sessionId, direction, sequence);
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, plaintext, frame.AsSpan(14, plaintext.Length), frame.AsSpan(14 + plaintext.Length, 16), aad);
            return frame;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(frame);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(aad);
        }
    }

    private byte[] Decrypt(ReadOnlySpan<byte> frame, byte[] key, byte direction, ulong sequence)
    {
        var ciphertextLength = frame.Length - 14 - 16;
        if (ciphertextLength <= 0 || frame.Length > CredentialRuntimeProtocol.MaximumEncryptedFrameBytes)
        {
            throw new CredentialRuntimeException("invalid_frame", "The runtime credential frame is invalid.");
        }
        var plaintext = new byte[ciphertextLength];
        Span<byte> nonce = stackalloc byte[12];
        BuildNonce(direction, sequence, nonce);
        var aad = RuntimeTranscript.FrameAad(_sessionId, direction, sequence);
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, frame.Slice(14, ciphertextLength), frame.Slice(14 + ciphertextLength, 16), plaintext, aad);
            return plaintext;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new CredentialRuntimeException("frame_auth_failed", "The runtime credential frame authentication failed.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(aad);
        }
    }

    private static void BuildNonce(byte direction, ulong sequence, Span<byte> destination)
    {
        destination.Clear();
        if (direction == 1) "C2H"u8.CopyTo(destination);
        else "H2C"u8.CopyTo(destination);
        destination[3] = 0;
        BinaryPrimitives.WriteUInt64BigEndian(destination[4..], sequence);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_hostToContainerKey);
        CryptographicOperations.ZeroMemory(_containerToHostKey);
    }
}

internal static class RuntimeTranscript
{
    internal static byte[] EncodeHost(string sessionId, CredentialContainerBinding binding, ReadOnlySpan<byte> hostPublic, ReadOnlySpan<byte> hostNonce) =>
        Encode("host-hello", sessionId, binding, hostPublic, hostNonce, ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty);

    internal static byte[] EncodeServer(string sessionId, CredentialContainerBinding binding, ReadOnlySpan<byte> hostPublic, ReadOnlySpan<byte> hostNonce, ReadOnlySpan<byte> serverPublic, ReadOnlySpan<byte> serverNonce) =>
        Encode("server-hello", sessionId, binding, hostPublic, hostNonce, serverPublic, serverNonce);

    private static byte[] Encode(string label, string sessionId, CredentialContainerBinding binding, ReadOnlySpan<byte> hostPublic, ReadOnlySpan<byte> hostNonce, ReadOnlySpan<byte> serverPublic, ReadOnlySpan<byte> serverNonce)
    {
        using var stream = new MemoryStream();
        CredentialRuntimeBootstrap.WriteText(stream, "hermes-credential-broker/v2");
        CredentialRuntimeBootstrap.WriteText(stream, label);
        CredentialRuntimeBootstrap.WriteText(stream, sessionId);
        CredentialRuntimeBootstrap.WriteText(stream, binding.ContainerId);
        CredentialRuntimeBootstrap.WriteText(stream, binding.ImageDigest);
        CredentialRuntimeBootstrap.WriteText(stream, binding.Principal.UserSid);
        CredentialRuntimeBootstrap.WriteText(stream, binding.Principal.MachineId);
        CredentialRuntimeBootstrap.WriteText(stream, binding.Principal.InstallId);
        CredentialRuntimeBootstrap.WriteText(stream, binding.Principal.ProfileId);
        CredentialRuntimeBootstrap.WriteInt64(stream, binding.ExpiresAt.ToUnixTimeSeconds());
        CredentialRuntimeBootstrap.WriteBytes(stream, hostPublic);
        CredentialRuntimeBootstrap.WriteBytes(stream, hostNonce);
        CredentialRuntimeBootstrap.WriteBytes(stream, serverPublic);
        CredentialRuntimeBootstrap.WriteBytes(stream, serverNonce);
        return stream.ToArray();
    }

    internal static byte[] FrameAad(string sessionId, byte direction, ulong sequence)
    {
        using var stream = new MemoryStream();
        CredentialRuntimeBootstrap.WriteText(stream, "hermes-credential-broker/v2/frame");
        CredentialRuntimeBootstrap.WriteText(stream, sessionId);
        stream.WriteByte(direction);
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, sequence);
        stream.Write(bytes);
        return stream.ToArray();
    }
}

internal static class Base64Url
{
    internal static string Encode(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static byte[] Decode(string? value, int expectedMaximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > ((expectedMaximum + 2) / 3) * 4 + 2
            || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new CredentialRuntimeException("invalid_base64url", "A runtime protocol field is invalid.");
        }
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        try
        {
            var decoded = Convert.FromBase64String(padded);
            if (decoded.Length > expectedMaximum) throw new CredentialRuntimeException("invalid_base64url", "A runtime protocol field is invalid.");
            return decoded;
        }
        catch (FormatException)
        {
            throw new CredentialRuntimeException("invalid_base64url", "A runtime protocol field is invalid.");
        }
    }
}
