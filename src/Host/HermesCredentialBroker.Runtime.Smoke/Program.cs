using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HermesCredentialBroker;
using HermesCredentialBroker.Runtime;

var suite = new SmokeSuite();

await suite.RunAsync("runtime binding rejects floating or foreign identities", () =>
{
    var principal = Principal();
    ThrowsCode(() => _ = new CredentialContainerBinding("short", Image(), principal, DateTimeOffset.UtcNow.AddMinutes(5)), "invalid_container_id");
    ThrowsCode(() => _ = new CredentialContainerBinding(Container(), "latest", principal, DateTimeOffset.UtcNow.AddMinutes(5)), "invalid_image_digest");
    ThrowsCode(() => _ = new CredentialContainerBinding(Container(), Image(), principal, DateTimeOffset.UtcNow.AddMinutes(16)), "invalid_session_lifetime", validate: true);
    return Task.CompletedTask;
});

await suite.RunAsync("reverse endpoint is exact loopback session only", () =>
{
    var session = "hcs2_" + new string('A', 43);
    try { CredentialRuntimeClient.ValidateLoopbackEndpoint(new Uri($"ws://127.0.0.1:9119/api/workbench/credentials/v2?session={session}"), session); }
    catch (Exception exception) { throw new InvalidOperationException("IPv4 loopback rejected", exception); }
    try { CredentialRuntimeClient.ValidateLoopbackEndpoint(new Uri($"wss://[::1]/api/workbench/credentials/v2?session={session}"), session); }
    catch (Exception exception) { throw new InvalidOperationException("IPv6 loopback rejected", exception); }
    ThrowsCode(() => CredentialRuntimeClient.ValidateLoopbackEndpoint(new Uri($"ws://192.0.2.1:9119/api?session={session}"), session), "non_loopback_endpoint");
    ThrowsCode(() => CredentialRuntimeClient.ValidateLoopbackEndpoint(new Uri($"ws://user@localhost:9119/api?session={session}"), session), "non_loopback_endpoint");
    ThrowsCode(() => CredentialRuntimeClient.ValidateLoopbackEndpoint(new Uri($"ws://localhost:9119/api/workbench/credentials/v2?session={session}&token=raw"), session), "endpoint_session_mismatch");
    ThrowsCode(() => CredentialRuntimeClient.ValidateLoopbackEndpoint(new Uri($"ws://localhost:9119/api?session={session}"), session), "non_loopback_endpoint");
    return Task.CompletedTask;
});

await suite.RunAsync("server handshake rejects duplicate or extra fields", () =>
{
    ThrowsCode(
        () => CredentialRuntimeCryptography.ParseServerHello(Encoding.UTF8.GetBytes("{\"version\":2,\"version\":2}")),
        "invalid_server_hello");
    ThrowsCode(
        () => CredentialRuntimeCryptography.ParseServerHello(Encoding.UTF8.GetBytes("{\"unexpected\":true}")),
        "invalid_server_hello");
    return Task.CompletedTask;
});

await suite.RunAsync("bootstrap is bounded binary and contains no credential value", async () =>
{
    using var bootstrap = CredentialRuntimeBootstrap.Create(Binding());
    using var stream = new WriteOnlyCaptureStream();
    await bootstrap.WriteToAsync(stream);
    var payload = stream.ToArray();
    try
    {
        True(payload.AsSpan(0, 4).SequenceEqual("HCB2"u8), "bootstrap magic");
        True(payload.Length < CredentialRuntimeProtocol.MaximumHandshakeBytes, "bootstrap bounded");
        True(!Encoding.UTF8.GetString(payload).Contains("provider-secret-sentinel", StringComparison.Ordinal), "bootstrap has no provider secret");
    }
    finally
    {
        CryptographicOperations.ZeroMemory(payload);
    }
    using var forbidden = new MemoryStream();
    try
    {
        await bootstrap.WriteToAsync(forbidden);
        throw new InvalidOperationException("seekable bootstrap destination accepted");
    }
    catch (CredentialRuntimeException exception)
    {
        Equal("bootstrap_destination_invalid", exception.Code, "bootstrap destination code");
    }
});

await suite.RunAsync("binary lease request is exact profile purpose revision nonce bound", () =>
{
    using var request = Request("model:chat", 7);
    var encoded = CredentialLeaseWireCodec.EncodeRequest(request, Binding());
    try
    {
        using var decoded = CredentialLeaseWireCodec.DecodeRequest(encoded, Binding());
        Equal(request.RequestId, decoded.RequestId, "request id");
        Equal(request.ConnectionRef.Value, decoded.ConnectionRef.Value, "connection ref");
        Equal(request.Purpose, decoded.Purpose, "purpose");
        Equal(request.ExpectedRevision, decoded.ExpectedRevision, "revision");
        True(CryptographicOperations.FixedTimeEquals(request.RequestNonce, decoded.RequestNonce), "nonce");
    }
    finally
    {
        CryptographicOperations.ZeroMemory(encoded);
    }
    using var wrongProfile = new CredentialRuntimeLeaseRequest("request-2", Reference(), "profile-2", "model:chat", 7, RandomNumberGenerator.GetBytes(32));
    ThrowsCode(() => wrongProfile.Validate(Binding()), "profile_denied");
    return Task.CompletedTask;
});

await suite.RunAsync("authenticated reverse channel resolves and encrypts exact lease", async () =>
{
    using var fixture = new VaultFixture();
    var stored = await fixture.Vault.StoreAsync(
        new CredentialWriteIntent("window-1", fixture.Binding, null, 0),
        Encoding.UTF8.GetBytes("runtime-secret-sentinel"));
    using var bootstrap = CredentialRuntimeBootstrap.Create(new CredentialContainerBinding(
        Container(), Image(), fixture.Principal, DateTimeOffset.UtcNow.AddMinutes(5)));
    using var bootstrapBytes = new WriteOnlyCaptureStream();
    await bootstrap.WriteToAsync(bootstrapBytes);
    var socket = new ContainerEmulatorSocket(bootstrapBytes.ToArray(), stored.ConnectionRef, stored.Revision, "model:chat", expectedSecret: "runtime-secret-sentinel");
    var client = new CredentialRuntimeClient(() => socket);
    var endpoint = new Uri($"ws://127.0.0.1:9119/api/workbench/credentials/v2?session={bootstrap.SessionId}");
    await client.RunAsync(endpoint, bootstrap, fixture.Vault);
    True(socket.Connected, "socket connected");
    True(socket.SecretVerified, "container decrypted exact credential bytes");
    True(socket.Closed, "session closed");
});

await suite.RunAsync("purpose denial returns encrypted failure and never a value", async () =>
{
    using var fixture = new VaultFixture();
    var stored = await fixture.Vault.StoreAsync(
        new CredentialWriteIntent("window-1", fixture.Binding, null, 0),
        Encoding.UTF8.GetBytes("must-not-cross"));
    using var bootstrap = CredentialRuntimeBootstrap.Create(new CredentialContainerBinding(
        Container(), Image(), fixture.Principal, DateTimeOffset.UtcNow.AddMinutes(5)));
    using var bootstrapBytes = new WriteOnlyCaptureStream();
    await bootstrap.WriteToAsync(bootstrapBytes);
    var socket = new ContainerEmulatorSocket(bootstrapBytes.ToArray(), stored.ConnectionRef, stored.Revision, "mcp:filesystem", expectedError: "purpose_denied");
    var client = new CredentialRuntimeClient(() => socket);
    await client.RunAsync(new Uri($"ws://localhost:9119/api/workbench/credentials/v2?session={bootstrap.SessionId}"), bootstrap, fixture.Vault);
    Equal("purpose_denied", socket.ErrorVerified, "encrypted error");
    True(!socket.SecretVerified, "no value delivered");
});

suite.Complete();

static CredentialPrincipalBinding Principal() => new("S-1-5-21-1000", "machine-1", "install-1", "profile-1");
static string Container() => new('c', 64);
static string Image() => "sha256:" + new string('d', 64);
static CredentialContainerBinding Binding() => new(Container(), Image(), Principal(), DateTimeOffset.UtcNow.AddMinutes(5));
static CredentialReference Reference() => new("hcv2_" + new string('R', 43));
static CredentialRuntimeLeaseRequest Request(string purpose, long revision) => new("request-1", Reference(), "profile-1", purpose, revision, RandomNumberGenerator.GetBytes(32));

static void True(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Equal<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
}

static void ThrowsCode(Action action, string code, bool validate = false)
{
    try
    {
        action();
        if (validate)
        {
            var value = new CredentialContainerBinding(Container(), Image(), Principal(), DateTimeOffset.UtcNow.AddMinutes(16));
            value.ValidateLifetimeForSmoke(DateTimeOffset.UtcNow);
        }
    }
    catch (CredentialRuntimeException exception) when (exception.Code == code) { return; }
    throw new InvalidOperationException($"Expected runtime error {code}.");
}

sealed class VaultFixture : IDisposable
{
    internal VaultFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), $"hcb-runtime-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
        Principal = new CredentialPrincipalBinding("S-1-5-21-1000", "machine-1", "install-1", "profile-1");
        Binding = new CredentialBinding(Principal, "openai", "primary", CredentialAuthKind.ApiKey, CredentialSourceKind.Native, ["model:chat"]);
        Vault = new DpapiCredentialVaultV2(Root, Principal, new SmokeProtector(), new SmokeStorageSecurity());
    }

    internal string Root { get; }
    internal CredentialPrincipalBinding Principal { get; }
    internal CredentialBinding Binding { get; }
    internal DpapiCredentialVaultV2 Vault { get; }

    public void Dispose()
    {
        Vault.Dispose();
        var root = Path.GetFullPath(Root);
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (root.StartsWith(temp, StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(root).StartsWith("hcb-runtime-smoke-", StringComparison.Ordinal)
            && Directory.Exists(root)) Directory.Delete(root, true);
    }
}

sealed class SmokeProtector : ICredentialRecordProtector
{
    public byte[] Protect(byte[] plaintext, byte[] entropy) => Transform(plaintext, entropy);
    public byte[] Unprotect(byte[] protectedBytes, byte[] entropy) => Transform(protectedBytes, entropy);
    private static byte[] Transform(byte[] source, byte[] entropy)
    {
        var key = SHA256.HashData(entropy);
        try
        {
            var result = new byte[source.Length];
            for (var index = 0; index < source.Length; index++) result[index] = (byte)(source[index] ^ key[index % key.Length] ^ 0x5A);
            return result;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}

sealed class SmokeStorageSecurity : ICredentialStorageSecurity
{
    public void PrepareRoot(string rootPath, string expectedUserSid) => Directory.CreateDirectory(rootPath);
    public void SecureFile(string rootPath, string filePath, string expectedUserSid) => ValidatePath(rootPath, filePath, expectedUserSid);
    public void ValidatePath(string rootPath, string path, string expectedUserSid)
    {
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar);
        var candidate = Path.GetFullPath(path);
        if (!candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
            && !candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("path escape");
    }
}

sealed class ContainerEmulatorSocket : ICredentialRuntimeSocket
{
    private readonly Queue<CredentialRuntimeMessage> _incoming = new();
    private readonly BootstrapFields _bootstrap;
    private readonly CredentialReference _reference;
    private readonly long _revision;
    private readonly string _purpose;
    private readonly string? _expectedSecret;
    private readonly string? _expectedError;
    private ECDiffieHellman? _serverKey;
    private byte[]? _hostToContainer;
    private byte[]? _containerToHost;
    private byte[]? _requestNonce;
    private string? _requestId;

    internal ContainerEmulatorSocket(byte[] bootstrap, CredentialReference reference, long revision, string purpose, string? expectedSecret = null, string? expectedError = null)
    {
        _bootstrap = BootstrapFields.Parse(bootstrap);
        CryptographicOperations.ZeroMemory(bootstrap);
        _reference = reference;
        _revision = revision;
        _purpose = purpose;
        _expectedSecret = expectedSecret;
        _expectedError = expectedError;
    }

    internal bool Connected { get; private set; }
    internal bool Closed { get; private set; }
    internal bool SecretVerified { get; private set; }
    internal string? ErrorVerified { get; private set; }

    public Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        Connected = true;
        return Task.CompletedTask;
    }

    public Task SendTextAsync(ReadOnlyMemory<byte> utf8, CancellationToken cancellationToken)
    {
        var hello = JsonSerializer.Deserialize<CredentialHostHello>(utf8.Span, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("missing host hello");
        var hostPublic = Decode(hello.HostPublicKey);
        var hostNonce = Decode(hello.HostNonce);
        var proof = Decode(hello.Proof);
        var hostTranscript = Transcript("host-hello", hostPublic, hostNonce, [], []);
        var expected = HMACSHA256.HashData(_bootstrap.BootstrapKey, hostTranscript);
        Require(CryptographicOperations.FixedTimeEquals(proof, expected), "host proof");
        _serverKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var serverPublic = _serverKey.ExportSubjectPublicKeyInfo();
        var serverNonce = Enumerable.Range(1, 32).Select(value => (byte)(value + 100)).ToArray();
        var serverTranscript = Transcript("server-hello", hostPublic, hostNonce, serverPublic, serverNonce);
        var serverProof = HMACSHA256.HashData(_bootstrap.BootstrapKey, serverTranscript);
        using var hostKey = ECDiffieHellman.Create();
        hostKey.ImportSubjectPublicKeyInfo(hostPublic, out _);
        var shared = _serverKey.DeriveRawSecretAgreement(hostKey.PublicKey);
        var saltInput = hostNonce.Concat(serverNonce).ToArray();
        var salt = HMACSHA256.HashData(_bootstrap.BootstrapKey, saltInput);
        var info = SHA256.HashData(serverTranscript);
        var keys = new byte[64];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, keys, salt, info);
        _hostToContainer = keys[..32];
        _containerToHost = keys[32..];
        var response = new CredentialServerHello(
            CredentialRuntimeProtocol.Version,
            "server-hello",
            _bootstrap.SessionId,
            _bootstrap.ContainerId,
            _bootstrap.ImageDigest,
            _bootstrap.UserSid,
            _bootstrap.MachineId,
            _bootstrap.InstallId,
            _bootstrap.ProfileId,
            _bootstrap.ExpiresAtUnix,
            Encode(serverPublic),
            Encode(serverNonce),
            Encode(serverProof));
        _incoming.Enqueue(new CredentialRuntimeMessage(CredentialRuntimeMessageKind.Text, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web)))));
        QueueRequest();
        foreach (var bytes in new[] { hostPublic, hostNonce, proof, hostTranscript, expected, serverPublic, serverNonce, serverTranscript, serverProof, shared, saltInput, salt, info, keys })
            CryptographicOperations.ZeroMemory(bytes);
        return Task.CompletedTask;
    }

    private void QueueRequest()
    {
        using var request = new CredentialRuntimeLeaseRequest("request-runtime-1", _reference, _bootstrap.ProfileId, _purpose, _revision, RandomNumberGenerator.GetBytes(32));
        _requestId = request.RequestId;
        _requestNonce = request.RequestNonce.ToArray();
        var plaintext = CredentialLeaseWireCodec.EncodeRequest(request, new CredentialContainerBinding(
            _bootstrap.ContainerId, _bootstrap.ImageDigest,
            new CredentialPrincipalBinding(_bootstrap.UserSid, _bootstrap.MachineId, _bootstrap.InstallId, _bootstrap.ProfileId),
            DateTimeOffset.FromUnixTimeSeconds(_bootstrap.ExpiresAtUnix)));
        try { _incoming.Enqueue(new CredentialRuntimeMessage(CredentialRuntimeMessageKind.Binary, Encrypt(plaintext, _containerToHost!, 1, 1))); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public Task SendBinaryAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        var plaintext = Decrypt(bytes.Span, _hostToContainer!, 2, 1);
        try
        {
            using var response = CredentialLeaseWireCodec.DecodeResponse(plaintext);
            AssertEqual(_requestId, response.RequestId, "response request id");
            Require(CryptographicOperations.FixedTimeEquals(_requestNonce!, response.RequestNonce), "response nonce");
            if (_expectedSecret is not null)
            {
                Require(response.Succeeded && response.Secret is not null, "lease success");
                SecretVerified = Encoding.UTF8.GetString(response.Secret!) == _expectedSecret;
            }
            else
            {
                Require(!response.Succeeded && response.Secret is null, "lease denied");
                ErrorVerified = response.ErrorCode;
                AssertEqual(_expectedError, ErrorVerified, "error code");
            }
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        _incoming.Enqueue(new CredentialRuntimeMessage(CredentialRuntimeMessageKind.Close, []));
        return Task.CompletedTask;
    }

    public Task<CredentialRuntimeMessage> ReceiveAsync(int maximumBytes, CancellationToken cancellationToken) =>
        Task.FromResult(_incoming.Dequeue());

    public Task CloseAsync(CancellationToken cancellationToken)
    {
        Closed = true;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _serverKey?.Dispose();
        foreach (var bytes in new[] { _hostToContainer, _containerToHost, _requestNonce, _bootstrap.BootstrapKey, _bootstrap.HostPublicKey })
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
        return ValueTask.CompletedTask;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
    }

    private byte[] Transcript(string label, byte[] hostPublic, byte[] hostNonce, byte[] serverPublic, byte[] serverNonce)
    {
        using var stream = new MemoryStream();
        foreach (var text in new[] { "hermes-credential-broker/v2", label, _bootstrap.SessionId, _bootstrap.ContainerId, _bootstrap.ImageDigest,
                     _bootstrap.UserSid, _bootstrap.MachineId, _bootstrap.InstallId, _bootstrap.ProfileId }) WriteField(stream, Encoding.UTF8.GetBytes(text));
        Span<byte> expiry = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(expiry, _bootstrap.ExpiresAtUnix);
        stream.Write(expiry);
        foreach (var value in new[] { hostPublic, hostNonce, serverPublic, serverNonce }) WriteField(stream, value);
        return stream.ToArray();
    }

    private byte[] Encrypt(byte[] plaintext, byte[] key, byte direction, ulong sequence)
    {
        var result = new byte[14 + plaintext.Length + 16];
        "HCF2"u8.CopyTo(result);
        result[4] = CredentialRuntimeProtocol.Version;
        result[5] = direction;
        BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(6, 8), sequence);
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(Nonce(direction, sequence), plaintext, result.AsSpan(14, plaintext.Length), result.AsSpan(14 + plaintext.Length), Aad(direction, sequence));
        return result;
    }

    private byte[] Decrypt(ReadOnlySpan<byte> frame, byte[] key, byte direction, ulong sequence)
    {
        var length = frame.Length - 30;
        var result = new byte[length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(Nonce(direction, sequence), frame.Slice(14, length), frame.Slice(14 + length, 16), result, Aad(direction, sequence));
        return result;
    }

    private byte[] Aad(byte direction, ulong sequence)
    {
        using var stream = new MemoryStream();
        WriteField(stream, Encoding.UTF8.GetBytes("hermes-credential-broker/v2/frame"));
        WriteField(stream, Encoding.UTF8.GetBytes(_bootstrap.SessionId));
        stream.WriteByte(direction);
        Span<byte> seq = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(seq, sequence);
        stream.Write(seq);
        return stream.ToArray();
    }

    private static byte[] Nonce(byte direction, ulong sequence)
    {
        var nonce = new byte[12];
        (direction == 1 ? "C2H"u8 : "H2C"u8).CopyTo(nonce);
        BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), sequence);
        return nonce;
    }

    private static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Decode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }

    private static void WriteField(Stream stream, byte[] value)
    {
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort)value.Length));
        stream.Write(length);
        stream.Write(value);
    }
}

sealed class WriteOnlyCaptureStream : Stream
{
    private readonly MemoryStream _inner = new();

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public byte[] ToArray() => _inner.ToArray();
    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.WriteAsync(buffer, cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}

sealed record BootstrapFields(
    string SessionId,
    string ContainerId,
    string ImageDigest,
    string UserSid,
    string MachineId,
    string InstallId,
    string ProfileId,
    long ExpiresAtUnix,
    byte[] HostPublicKey,
    byte[] BootstrapKey)
{
    internal static BootstrapFields Parse(byte[] payload)
    {
        var reader = new BinaryReader(new MemoryStream(payload), Encoding.UTF8, leaveOpen: false);
        if (!reader.ReadBytes(4).SequenceEqual("HCB2"u8.ToArray()) || ReadI32(reader) != CredentialRuntimeProtocol.Version) throw new InvalidOperationException("bootstrap header");
        var fields = Enumerable.Range(0, 7).Select(_ => ReadText(reader)).ToArray();
        var expiry = ReadI64(reader);
        var publicKey = ReadBytes(reader);
        var key = ReadBytes(reader);
        return new BootstrapFields(fields[0], fields[1], fields[2], fields[3], fields[4], fields[5], fields[6], expiry, publicKey, key);
    }

    private static byte[] ReadBytes(BinaryReader reader)
    {
        Span<byte> length = stackalloc byte[2];
        if (reader.Read(length) != 2) throw new InvalidOperationException("bootstrap truncated");
        var count = BinaryPrimitives.ReadUInt16BigEndian(length);
        return reader.ReadBytes(count);
    }
    private static string ReadText(BinaryReader reader) => Encoding.UTF8.GetString(ReadBytes(reader));
    private static int ReadI32(BinaryReader reader) { Span<byte> value = stackalloc byte[4]; reader.Read(value); return BinaryPrimitives.ReadInt32BigEndian(value); }
    private static long ReadI64(BinaryReader reader) { Span<byte> value = stackalloc byte[8]; reader.Read(value); return BinaryPrimitives.ReadInt64BigEndian(value); }
}

static class SmokeExtensions
{
    internal static void ValidateLifetimeForSmoke(this CredentialContainerBinding binding, DateTimeOffset now)
    {
        // Public construction preserves the value; bootstrap creation performs the lifetime gate.
        using var bootstrap = CredentialRuntimeBootstrap.Create(binding, new FixedTimeProvider(now));
    }
}

sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

sealed class SmokeSuite
{
    private int _passed;
    private int _failed;
    internal async Task RunAsync(string name, Func<Task> test)
    {
        try { await test(); _passed++; Console.WriteLine($"PASS {name}"); }
        catch (Exception exception) { _failed++; Console.WriteLine($"FAIL {name}: {exception.GetType().Name}: {exception.Message}"); }
    }
    internal void Complete()
    {
        Console.WriteLine($"RESULT {_passed} passed, {_failed} failed");
        if (_failed != 0) Environment.ExitCode = 1;
    }
}
