using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HermesCredentialBroker;

public interface ICredentialVaultV2
{
    Task<IReadOnlyList<CredentialMetadata>> ListAsync(CancellationToken cancellationToken = default);
    Task<CredentialMetadata?> FindAsync(CredentialReference connectionRef, CancellationToken cancellationToken = default);
    Task<CredentialMetadata> StoreAsync(CredentialWriteIntent intent, byte[] plaintext, CancellationToken cancellationToken = default);
    Task RemoveAsync(CredentialRemoveIntent intent, CancellationToken cancellationToken = default);
    Task VerifyRecordAsync(CredentialReference connectionRef, CancellationToken cancellationToken = default);
}

public sealed class DpapiCredentialVaultV2 : ICredentialVaultV2, ICredentialLeaseResolver, IDisposable
{
    private const int SchemaVersion = 2;
    private const int MaximumRecordBytes = 256 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _rootPath;
    private readonly CredentialPrincipalBinding _principal;
    private readonly ICredentialRecordProtector _protector;
    private readonly ICredentialStorageSecurity _storageSecurity;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public DpapiCredentialVaultV2(
        string rootPath,
        CredentialPrincipalBinding principal,
        ICredentialRecordProtector protector,
        ICredentialStorageSecurity storageSecurity)
    {
        _rootPath = Path.GetFullPath(rootPath ?? throw new ArgumentNullException(nameof(rootPath)));
        _principal = principal ?? throw new ArgumentNullException(nameof(principal));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _storageSecurity = storageSecurity ?? throw new ArgumentNullException(nameof(storageSecurity));
        _storageSecurity.PrepareRoot(_rootPath, _principal.UserSid);
    }

    public async Task<IReadOnlyList<CredentialMetadata>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _storageSecurity.ValidatePath(_rootPath, _rootPath, _principal.UserSid);
            var result = new List<CredentialMetadata>();
            foreach (var path in Directory.EnumerateFiles(_rootPath, "*.hcv2", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(ToMetadata(await ReadEnvelopeAsync(path, cancellationToken).ConfigureAwait(false)));
            }
            return result.OrderBy(entry => entry.ProviderId, StringComparer.Ordinal)
                .ThenBy(entry => entry.SlotId, StringComparer.Ordinal)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CredentialMetadata?> FindAsync(CredentialReference connectionRef, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var path = PathFor(connectionRef);
            return File.Exists(path) ? ToMetadata(await ReadEnvelopeAsync(path, cancellationToken).ConfigureAwait(false)) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CredentialMetadata> StoreAsync(CredentialWriteIntent intent, byte[] plaintext, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(plaintext);
        if (plaintext.Length == 0 || plaintext.Length > HermesCredentialBrokerProtocol.MaximumSecretBytes)
        {
            throw new CredentialBrokerException("invalid_secret_size", "The credential value has an invalid size.");
        }
        EnsurePrincipal(intent.Binding.Principal);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        byte[]? protectedBytes = null;
        byte[]? entropy = null;
        try
        {
            ThrowIfDisposed();
            var connectionRef = intent.ExistingReference ?? CredentialReference.Create();
            var path = PathFor(connectionRef);
            StoredCredentialEnvelope? existing = null;
            if (File.Exists(path)) existing = await ReadEnvelopeAsync(path, cancellationToken).ConfigureAwait(false);

            if (existing is null && intent.ExpectedRevision != 0)
            {
                throw new CredentialBrokerException("revision_conflict", "The credential revision changed.");
            }
            if (existing is not null)
            {
                if (existing.Revision != intent.ExpectedRevision || !existing.Binding.ExactlyMatches(intent.Binding))
                {
                    throw new CredentialBrokerException("revision_conflict", "The credential revision or binding changed.");
                }
            }

            var nextRevision = checked((existing?.Revision ?? 0) + 1);
            entropy = BuildEntropy(connectionRef, intent.Binding);
            protectedBytes = _protector.Protect(plaintext, entropy);
            var envelope = new StoredCredentialEnvelope(
                SchemaVersion,
                connectionRef,
                intent.Binding,
                nextRevision,
                DateTimeOffset.UtcNow,
                Convert.ToBase64String(protectedBytes),
                Sha256(protectedBytes));
            await WriteEnvelopeAsync(path, envelope, cancellationToken).ConfigureAwait(false);
            return ToMetadata(envelope);
        }
        finally
        {
            if (protectedBytes is not null) CryptographicOperations.ZeroMemory(protectedBytes);
            if (entropy is not null) CryptographicOperations.ZeroMemory(entropy);
            CryptographicOperations.ZeroMemory(plaintext);
            _gate.Release();
        }
    }

    public async Task RemoveAsync(CredentialRemoveIntent intent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var path = PathFor(intent.ConnectionRef);
            if (!File.Exists(path)) throw new CredentialBrokerException("credential_not_found", "The credential no longer exists.");
            var existing = await ReadEnvelopeAsync(path, cancellationToken).ConfigureAwait(false);
            EnsurePrincipal(existing.Binding.Principal);
            if (existing.Revision != intent.ExpectedRevision)
            {
                throw new CredentialBrokerException("revision_conflict", "The credential revision changed.");
            }
            _storageSecurity.ValidatePath(_rootPath, path, _principal.UserSid);
            File.Delete(path);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task VerifyRecordAsync(CredentialReference connectionRef, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        byte[]? protectedBytes = null;
        byte[]? plaintext = null;
        byte[]? entropy = null;
        try
        {
            ThrowIfDisposed();
            var envelope = await ReadEnvelopeAsync(PathFor(connectionRef), cancellationToken).ConfigureAwait(false);
            protectedBytes = Convert.FromBase64String(envelope.ProtectedPayload);
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(envelope.ProtectedSha256), Encoding.ASCII.GetBytes(Sha256(protectedBytes))))
            {
                throw new CredentialBrokerException("record_integrity_failure", "The credential record failed integrity validation.");
            }
            entropy = BuildEntropy(connectionRef, envelope.Binding);
            plaintext = _protector.Unprotect(protectedBytes, entropy);
            if (plaintext.Length == 0 || plaintext.Length > HermesCredentialBrokerProtocol.MaximumSecretBytes)
            {
                throw new CredentialBrokerException("record_integrity_failure", "The credential record failed integrity validation.");
            }
        }
        catch (FormatException)
        {
            throw new CredentialBrokerException("record_integrity_failure", "The credential record failed integrity validation.");
        }
        finally
        {
            if (protectedBytes is not null) CryptographicOperations.ZeroMemory(protectedBytes);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            if (entropy is not null) CryptographicOperations.ZeroMemory(entropy);
            _gate.Release();
        }
    }

    public async Task<CredentialLease> ResolveLeaseAsync(CredentialLeaseRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        EnsurePrincipal(request.Principal);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        byte[]? protectedBytes = null;
        byte[]? plaintext = null;
        byte[]? entropy = null;
        try
        {
            ThrowIfDisposed();
            var envelope = await ReadEnvelopeAsync(PathFor(request.ConnectionRef), cancellationToken).ConfigureAwait(false);
            if (envelope.Revision != request.ExpectedRevision)
            {
                throw new CredentialBrokerException("revision_conflict", "The credential revision changed.");
            }
            if (!envelope.Binding.Purposes.Contains(request.Purpose, StringComparer.Ordinal))
            {
                throw new CredentialBrokerException("purpose_denied", "The credential is not authorized for this purpose.");
            }
            protectedBytes = Convert.FromBase64String(envelope.ProtectedPayload);
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(envelope.ProtectedSha256),
                    Encoding.ASCII.GetBytes(Sha256(protectedBytes))))
            {
                throw new CredentialBrokerException("record_integrity_failure", "The credential record failed integrity validation.");
            }
            entropy = BuildEntropy(request.ConnectionRef, envelope.Binding);
            plaintext = _protector.Unprotect(protectedBytes, entropy);
            var lease = new CredentialLease(ToMetadata(envelope), request.Purpose, plaintext);
            plaintext = null;
            return lease;
        }
        catch (FormatException)
        {
            throw new CredentialBrokerException("record_integrity_failure", "The credential record failed integrity validation.");
        }
        finally
        {
            if (protectedBytes is not null) CryptographicOperations.ZeroMemory(protectedBytes);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            if (entropy is not null) CryptographicOperations.ZeroMemory(entropy);
            _gate.Release();
        }
    }

    private async Task<StoredCredentialEnvelope> ReadEnvelopeAsync(string path, CancellationToken cancellationToken)
    {
        _storageSecurity.ValidatePath(_rootPath, path, _principal.UserSid);
        var info = new FileInfo(path);
        if (!info.Exists) throw new CredentialBrokerException("credential_not_found", "The credential no longer exists.");
        if (info.Length is <= 0 or > MaximumRecordBytes) throw new CredentialBrokerException("invalid_record", "The credential record is invalid.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var envelope = await JsonSerializer.DeserializeAsync<StoredCredentialEnvelope>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new CredentialBrokerException("invalid_record", "The credential record is invalid.");
        ValidateEnvelope(envelope);
        return envelope;
    }

    private async Task WriteEnvelopeAsync(string path, StoredCredentialEnvelope envelope, CancellationToken cancellationToken)
    {
        _storageSecurity.ValidatePath(_rootPath, _rootPath, _principal.UserSid);
        var temporaryPath = Path.Combine(_rootPath, $".{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, envelope, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            _storageSecurity.SecureFile(_rootPath, temporaryPath, _principal.UserSid);
            if (File.Exists(path)) File.Move(temporaryPath, path, overwrite: true);
            else File.Move(temporaryPath, path);
            _storageSecurity.SecureFile(_rootPath, path, _principal.UserSid);
            _storageSecurity.ValidatePath(_rootPath, path, _principal.UserSid);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private void ValidateEnvelope(StoredCredentialEnvelope envelope)
    {
        if (envelope.SchemaVersion != SchemaVersion || envelope.Revision <= 0 || envelope.UpdatedAt == default)
        {
            throw new CredentialBrokerException("invalid_record", "The credential record is invalid.");
        }
        _ = new CredentialReference(envelope.ConnectionRef.Value);
        EnsurePrincipal(envelope.Binding.Principal);
        if (string.IsNullOrWhiteSpace(envelope.ProtectedPayload) || !envelope.ProtectedSha256.StartsWith("sha256:", StringComparison.Ordinal))
        {
            throw new CredentialBrokerException("invalid_record", "The credential record is invalid.");
        }
    }

    private void EnsurePrincipal(CredentialPrincipalBinding candidate)
    {
        if (!StringComparer.Ordinal.Equals(_principal.UserSid, candidate.UserSid)
            || !StringComparer.Ordinal.Equals(_principal.MachineId, candidate.MachineId)
            || !StringComparer.Ordinal.Equals(_principal.InstallId, candidate.InstallId)
            || !StringComparer.Ordinal.Equals(_principal.ProfileId, candidate.ProfileId))
        {
            throw new CredentialBrokerException("principal_mismatch", "The credential binding does not match this vault.");
        }
    }

    private string PathFor(CredentialReference connectionRef) => Path.Combine(_rootPath, $"{connectionRef.Value}.hcv2");

    private static byte[] BuildEntropy(CredentialReference connectionRef, CredentialBinding binding) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
            "hermes-credential-broker/v2",
            connectionRef.Value,
            binding.Principal.UserSid,
            binding.Principal.MachineId,
            binding.Principal.InstallId,
            binding.Principal.ProfileId,
            binding.ProviderId,
            binding.SlotId,
            binding.AuthKind,
            string.Join(',', binding.Purposes))));

    private static string Sha256(byte[] bytes) => $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";

    private static CredentialMetadata ToMetadata(StoredCredentialEnvelope envelope) => new(
        envelope.ConnectionRef,
        envelope.Binding.Principal.ProfileId,
        envelope.Binding.ProviderId,
        envelope.Binding.SlotId,
        envelope.Binding.AuthKind,
        envelope.Binding.SourceKind,
        envelope.Binding.Purposes,
        envelope.Revision,
        envelope.UpdatedAt);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }

    private sealed record StoredCredentialEnvelope(
        int SchemaVersion,
        CredentialReference ConnectionRef,
        CredentialBinding Binding,
        long Revision,
        DateTimeOffset UpdatedAt,
        string ProtectedPayload,
        string ProtectedSha256);
}
