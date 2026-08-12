using System.Security.Cryptography;

namespace HermesCredentialBroker;

/// <summary>
/// Owns at most one process-lifetime credential and resolves bounded copies for
/// the existing encrypted runtime relay. No session credential is serialized.
/// </summary>
public sealed class SessionCredentialLeaseResolver : ICredentialLeaseResolver, IDisposable
{
    private readonly object _gate = new();
    private SessionEntry? _entry;
    private long _revision;
    private bool _disposed;

    public CredentialMetadata Replace(CredentialBinding binding, CredentialSecret secret)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(secret);
        var bytes = secret.Consume();
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var previous = _entry;
                var metadata = new CredentialMetadata(
                    CredentialReference.Create(),
                    binding.Principal.ProfileId,
                    binding.ProviderId,
                    binding.SlotId,
                    binding.AuthKind,
                    binding.SourceKind,
                    binding.Purposes,
                    checked(++_revision),
                    DateTimeOffset.UtcNow);
                _entry = new SessionEntry(binding, metadata, bytes);
                bytes = null;
                previous?.Dispose();
                return metadata;
            }
        }
        finally
        {
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public CredentialMetadata? CurrentMetadata
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _entry?.Metadata;
            }
        }
    }

    public bool Owns(CredentialReference reference)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _entry is not null
                && StringComparer.Ordinal.Equals(_entry.Metadata.ConnectionRef.Value, reference.Value);
        }
    }

    public Task<CredentialLease> ResolveLeaseAsync(
        CredentialLeaseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var entry = _entry;
            if (entry is null
                || !StringComparer.Ordinal.Equals(entry.Metadata.ConnectionRef.Value, request.ConnectionRef.Value))
            {
                throw new CredentialBrokerException("credential_not_found", "The session credential is unavailable.");
            }
            if (!entry.Binding.Principal.UserSid.Equals(request.Principal.UserSid, StringComparison.Ordinal)
                || !entry.Binding.Principal.MachineId.Equals(request.Principal.MachineId, StringComparison.Ordinal)
                || !entry.Binding.Principal.InstallId.Equals(request.Principal.InstallId, StringComparison.Ordinal)
                || !entry.Binding.Principal.ProfileId.Equals(request.Principal.ProfileId, StringComparison.Ordinal))
            {
                throw new CredentialBrokerException("principal_mismatch", "The session credential principal changed.");
            }
            if (entry.Metadata.Revision != request.ExpectedRevision)
                throw new CredentialBrokerException("revision_conflict", "The session credential revision changed.");
            if (!entry.Binding.Purposes.Contains(request.Purpose, StringComparer.Ordinal))
                throw new CredentialBrokerException("purpose_denied", "The session credential is not authorized for this purpose.");
            return Task.FromResult(new CredentialLease(entry.Metadata, request.Purpose, entry.CopySecret()));
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var previous = _entry;
            _entry = null;
            previous?.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            var previous = _entry;
            _entry = null;
            previous?.Dispose();
        }
    }

    private sealed class SessionEntry(
        CredentialBinding binding,
        CredentialMetadata metadata,
        byte[] secret) : IDisposable
    {
        private byte[]? _secret = secret;
        internal CredentialBinding Binding { get; } = binding;
        internal CredentialMetadata Metadata { get; } = metadata;

        internal byte[] CopySecret()
        {
            var current = _secret ?? throw new ObjectDisposedException(nameof(SessionEntry));
            return current.ToArray();
        }

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _secret, null);
            if (current is not null) CryptographicOperations.ZeroMemory(current);
        }
    }
}
