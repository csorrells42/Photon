using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace HermesCredentialBroker;

public sealed class NativeCredentialBrokerV2 : IDisposable
{
    private readonly ICredentialVaultV2 _vault;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, PendingReview> _pending = new(StringComparer.Ordinal);
    private bool _disposed;

    public NativeCredentialBrokerV2(ICredentialVaultV2 vault, TimeProvider? timeProvider = null)
    {
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<IReadOnlyList<CredentialMetadata>> ListAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _vault.ListAsync(cancellationToken);
    }

    public CredentialReviewTicket BeginChangeReview(CredentialWriteIntent intent, CredentialSecret secret, TimeSpan? lifetime = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(secret);
        var session = ContractText.Identifier(intent.WorkbenchSessionId, nameof(intent.WorkbenchSessionId), 160);
        var expiresAt = Expiration(lifetime);
        PruneExpired();
        var handle = NewReviewHandle();
        var pending = PendingReview.Change(intent with { WorkbenchSessionId = session }, secret, expiresAt);
        if (!_pending.TryAdd(handle, pending))
        {
            pending.Dispose();
            throw new CredentialBrokerException("review_collision", "The native review could not be created.");
        }
        return Ticket(handle, pending);
    }

    public async Task<CredentialMetadata> CommitChangeAsync(string reviewHandle, string workbenchSessionId, CancellationToken cancellationToken = default)
    {
        var pending = Consume(reviewHandle, workbenchSessionId, CredentialReviewAction.Change);
        using (pending)
        {
            var secret = pending.Secret?.Consume()
                ?? throw new CredentialBrokerException("review_invalid", "The native review is invalid.");
            return await _vault.StoreAsync(pending.WriteIntent!, secret, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<CredentialReviewTicket> BeginRemoveReviewAsync(CredentialRemoveIntent intent, TimeSpan? lifetime = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(intent);
        var session = ContractText.Identifier(intent.WorkbenchSessionId, nameof(intent.WorkbenchSessionId), 160);
        var metadata = await _vault.FindAsync(intent.ConnectionRef, cancellationToken).ConfigureAwait(false)
            ?? throw new CredentialBrokerException("credential_not_found", "The credential no longer exists.");
        if (metadata.Revision != intent.ExpectedRevision)
        {
            throw new CredentialBrokerException("revision_conflict", "The credential revision changed.");
        }
        var expiresAt = Expiration(lifetime);
        PruneExpired();
        var handle = NewReviewHandle();
        var pending = PendingReview.Remove(intent with { WorkbenchSessionId = session }, metadata, expiresAt);
        if (!_pending.TryAdd(handle, pending))
        {
            pending.Dispose();
            throw new CredentialBrokerException("review_collision", "The native review could not be created.");
        }
        return Ticket(handle, pending);
    }

    public async Task CommitRemoveAsync(string reviewHandle, string workbenchSessionId, CancellationToken cancellationToken = default)
    {
        var pending = Consume(reviewHandle, workbenchSessionId, CredentialReviewAction.Remove);
        using (pending)
        {
            await _vault.RemoveAsync(pending.RemoveIntent!, cancellationToken).ConfigureAwait(false);
        }
    }

    public bool CancelReview(string reviewHandle, string workbenchSessionId)
    {
        ThrowIfDisposed();
        var handle = ValidateReviewHandle(reviewHandle);
        var session = ContractText.Identifier(workbenchSessionId, nameof(workbenchSessionId), 160);
        if (!_pending.TryGetValue(handle, out var pending)
            || !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(pending.WorkbenchSessionId),
                System.Text.Encoding.UTF8.GetBytes(session))) return false;
        if (!_pending.TryRemove(handle, out pending)) return false;
        pending.Dispose();
        return true;
    }

    private PendingReview Consume(string reviewHandle, string workbenchSessionId, CredentialReviewAction expectedAction)
    {
        ThrowIfDisposed();
        PruneExpired();
        var handle = ValidateReviewHandle(reviewHandle);
        var session = ContractText.Identifier(workbenchSessionId, nameof(workbenchSessionId), 160);
        if (!_pending.TryRemove(handle, out var pending))
        {
            throw new CredentialBrokerException("review_unavailable", "The native review is expired or already consumed.");
        }
        if (pending.ExpiresAt <= _timeProvider.GetUtcNow()
            || pending.Action != expectedAction
            || !FixedEquals(pending.WorkbenchSessionId, session))
        {
            pending.Dispose();
            throw new CredentialBrokerException("review_binding_mismatch", "The native review does not match this request.");
        }
        return pending;
    }

    private DateTimeOffset Expiration(TimeSpan? requested)
    {
        var lifetime = requested ?? TimeSpan.FromMinutes(2);
        if (lifetime <= TimeSpan.Zero || lifetime > HermesCredentialBrokerProtocol.MaximumReviewLifetime)
        {
            throw new CredentialBrokerException("invalid_review_lifetime", "The native review lifetime is invalid.");
        }
        return _timeProvider.GetUtcNow().Add(lifetime);
    }

    private void PruneExpired()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var pair in _pending)
        {
            if (pair.Value.ExpiresAt > now || !_pending.TryRemove(pair.Key, out var removed)) continue;
            removed.Dispose();
        }
    }

    private static CredentialReviewTicket Ticket(string handle, PendingReview pending)
    {
        var metadata = pending.Metadata;
        var connectionRef = pending.Action == CredentialReviewAction.Change
            ? pending.WriteIntent!.ExistingReference
            : metadata.ConnectionRef;
        return new CredentialReviewTicket(
            handle,
            pending.Action,
            connectionRef,
            metadata.ProfileId,
            metadata.ProviderId,
            metadata.SlotId,
            pending.ExpectedRevision,
            pending.ExpiresAt);
    }

    private static string NewReviewHandle()
    {
        Span<byte> random = stackalloc byte[32];
        RandomNumberGenerator.Fill(random);
        var encoded = Convert.ToBase64String(random).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        CryptographicOperations.ZeroMemory(random);
        return $"hcr2_{encoded}";
    }

    private static string ValidateReviewHandle(string? value)
    {
        if (value is null || value.Length != 48 || !value.StartsWith("hcr2_", StringComparison.Ordinal)
            || value[5..].Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
        {
            throw new CredentialBrokerException("invalid_review_handle", "The native review handle is invalid.");
        }
        return value;
    }

    private static bool FixedEquals(string left, string right)
    {
        var leftBytes = System.Text.Encoding.UTF8.GetBytes(left);
        var rightBytes = System.Text.Encoding.UTF8.GetBytes(right);
        try { return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes); }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var pending)) pending.Dispose();
        }
    }

    private sealed class PendingReview : IDisposable
    {
        private PendingReview(
            CredentialReviewAction action,
            CredentialWriteIntent? writeIntent,
            CredentialRemoveIntent? removeIntent,
            CredentialSecret? secret,
            CredentialMetadata metadata,
            DateTimeOffset expiresAt)
        {
            Action = action;
            WriteIntent = writeIntent;
            RemoveIntent = removeIntent;
            Secret = secret;
            Metadata = metadata;
            ExpiresAt = expiresAt;
        }

        internal CredentialReviewAction Action { get; }
        internal CredentialWriteIntent? WriteIntent { get; }
        internal CredentialRemoveIntent? RemoveIntent { get; }
        internal CredentialSecret? Secret { get; }
        internal CredentialMetadata Metadata { get; }
        internal DateTimeOffset ExpiresAt { get; }
        internal string WorkbenchSessionId => WriteIntent?.WorkbenchSessionId ?? RemoveIntent!.WorkbenchSessionId;
        internal long ExpectedRevision => WriteIntent?.ExpectedRevision ?? RemoveIntent!.ExpectedRevision;

        internal static PendingReview Change(CredentialWriteIntent intent, CredentialSecret secret, DateTimeOffset expiresAt) => new(
            CredentialReviewAction.Change,
            intent,
            null,
            secret,
            new CredentialMetadata(
                intent.ExistingReference ?? default,
                intent.Binding.Principal.ProfileId,
                intent.Binding.ProviderId,
                intent.Binding.SlotId,
                intent.Binding.AuthKind,
                intent.Binding.SourceKind,
                intent.Binding.Purposes,
                intent.ExpectedRevision,
                DateTimeOffset.MinValue,
                false),
            expiresAt);

        internal static PendingReview Remove(CredentialRemoveIntent intent, CredentialMetadata metadata, DateTimeOffset expiresAt) => new(
            CredentialReviewAction.Remove, null, intent, null, metadata, expiresAt);

        public void Dispose() => Secret?.Dispose();
    }
}
