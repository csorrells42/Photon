using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using HermesCredentialBroker;

namespace HermesCredentialBroker.Runtime;

public sealed class CredentialRuntimeBootstrap : IDisposable
{
    private static readonly byte[] Magic = "HCB2"u8.ToArray();
    private readonly byte[] _bootstrapKey;
    private bool _disposed;

    private CredentialRuntimeBootstrap(
        string sessionId,
        CredentialContainerBinding binding,
        ECDiffieHellman hostKey,
        byte[] bootstrapKey)
    {
        SessionId = sessionId;
        Binding = binding;
        HostKey = hostKey;
        _bootstrapKey = bootstrapKey;
    }

    public string SessionId { get; }
    public CredentialContainerBinding Binding { get; }
    internal ECDiffieHellman HostKey { get; }
    internal ReadOnlySpan<byte> BootstrapKey => _disposed
        ? throw new ObjectDisposedException(nameof(CredentialRuntimeBootstrap))
        : _bootstrapKey;

    public static CredentialRuntimeBootstrap Create(CredentialContainerBinding binding, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(binding);
        binding.ValidateLifetime((timeProvider ?? TimeProvider.System).GetUtcNow());
        Span<byte> sessionRandom = stackalloc byte[32];
        RandomNumberGenerator.Fill(sessionRandom);
        var sessionId = $"hcs2_{Convert.ToBase64String(sessionRandom).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
        CryptographicOperations.ZeroMemory(sessionRandom);
        var bootstrapKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            var hostKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            return new CredentialRuntimeBootstrap(sessionId, binding, hostKey, bootstrapKey);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bootstrapKey);
            throw;
        }
    }

    public async Task WriteToAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite || destination.CanRead || destination.CanSeek)
        {
            throw new CredentialRuntimeException(
                "bootstrap_destination_invalid",
                "The runtime bootstrap destination must be a write-only non-seekable pipe.");
        }
        var publicKey = HostKey.ExportSubjectPublicKeyInfo();
        byte[]? payload = null;
        using var buffer = new MemoryStream();
        try
        {
            buffer.Write(Magic);
            WriteInt32(buffer, CredentialRuntimeProtocol.Version);
            WriteText(buffer, SessionId);
            WriteText(buffer, Binding.ContainerId);
            WriteText(buffer, Binding.ImageDigest);
            WriteText(buffer, Binding.Principal.UserSid);
            WriteText(buffer, Binding.Principal.MachineId);
            WriteText(buffer, Binding.Principal.InstallId);
            WriteText(buffer, Binding.Principal.ProfileId);
            WriteInt64(buffer, Binding.ExpiresAt.ToUnixTimeSeconds());
            WriteBytes(buffer, publicKey);
            WriteBytes(buffer, _bootstrapKey);
            if (buffer.Length > CredentialRuntimeProtocol.MaximumHandshakeBytes)
            {
                throw new CredentialRuntimeException("bootstrap_too_large", "The runtime bootstrap is too large.");
            }
            payload = buffer.ToArray();
            await destination.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(publicKey);
            if (payload is not null) CryptographicOperations.ZeroMemory(payload);
            if (buffer.TryGetBuffer(out var written)) CryptographicOperations.ZeroMemory(written.AsSpan());
        }
    }

    internal static void WriteText(Stream stream, string value) => WriteBytes(stream, Encoding.UTF8.GetBytes(value));

    internal static void WriteBytes(Stream stream, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > ushort.MaxValue) throw new CredentialRuntimeException("field_too_large", "A runtime protocol field is too large.");
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort)bytes.Length));
        stream.Write(length);
        stream.Write(bytes);
    }

    internal static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    internal static void WriteInt64(Stream stream, long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        stream.Write(bytes);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_bootstrapKey);
        HostKey.Dispose();
    }
}
