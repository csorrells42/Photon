using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PhotonCadProjects;

internal sealed class DeterministicHandleIssuer : IPhotonCadHandleIssuer
{
    private int _next;

    public PhotonCadWorkspaceHandle NewWorkspace() => new(Value("cad-workspace:"));
    public PhotonCadProjectHandle NewProject() => new(Value("cad-project:"));
    public PhotonCadReopenHandle NewReopen() => new(Value("cad-reopen:"));
    public PhotonCadSaveReceiptHandle NewSaveReceipt() => new(Value("cad-save-receipt:"));

    private string Value(string prefix)
    {
        var suffix = Interlocked.Increment(ref _next).ToString("x8");
        return prefix + suffix.PadLeft(32, '0');
    }
}

internal sealed class FixedClock(DateTimeOffset now) : IPhotonCadClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}

internal sealed class SmokeProjectCodec(
    int maximumEncodedBytes = PhotonCadProjectContract.MaximumCanonicalProjectBytes)
    : IPhotonCadProjectCodec, IPhotonCadProjectCodecPolicy
{
    private int _next;
    private int _decodeCalls;

    internal bool FailNextDecode { get; set; }
    internal int DecodeCallCount => Volatile.Read(ref _decodeCalls);

    public int MaximumEncodedBytes { get; } = maximumEncodedBytes is >= 1 and <= PhotonCadProjectContract.MaximumCanonicalProjectBytes
        ? maximumEncodedBytes
        : throw new ArgumentOutOfRangeException(nameof(maximumEncodedBytes));

    public ValueTask<PhotonCadCanonicalProject> CreateAsync(string title, PhotonCadProjectUnit units, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = Interlocked.Increment(ref _next);
        return ValueTask.FromResult(Encode(new SmokePayload($"session:{id}", $"project:{id}", 0, title, units, false, "initial")));
    }

    public PhotonCadCanonicalProject Decode(ReadOnlyMemory<byte> canonicalBytes)
    {
        Interlocked.Increment(ref _decodeCalls);
        if (FailNextDecode)
        {
            FailNextDecode = false;
            throw new PhotonCadProjectException("injected_decode_failure", nameof(canonicalBytes));
        }
        var payload = JsonSerializer.Deserialize<SmokePayload>(canonicalBytes.Span)
            ?? throw new PhotonCadProjectException("decode_failed", nameof(canonicalBytes));
        return Encode(payload);
    }

    public PhotonCadCanonicalProject MarkSaved(PhotonCadCanonicalProject current)
    {
        var payload = Parse(current.CanonicalBytes);
        return Encode(payload with { Dirty = false });
    }

    public string ComputeLogicalContentDigest(ReadOnlyMemory<byte> canonicalBytes)
    {
        var payload = Parse(canonicalBytes);
        var logical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            payload.SessionId,
            payload.ProjectId,
            payload.Revision,
            payload.Title,
            payload.Units,
            payload.Model,
        });
        return Digest(logical);
    }

    internal PhotonCadCanonicalProject Advance(PhotonCadCanonicalProject current, string model)
    {
        var payload = Parse(current.CanonicalBytes);
        return Encode(payload with { Revision = checked(payload.Revision + 1), Dirty = true, Model = model });
    }

    internal PhotonCadCanonicalProject PersistedAdvance(PhotonCadCanonicalProject current, string model) =>
        MarkSaved(Advance(current, model));

    internal PhotonCadCanonicalProject PersistedSameRevisionChange(PhotonCadCanonicalProject current, string model)
    {
        var payload = Parse(current.CanonicalBytes);
        return Encode(payload with { Dirty = false, Model = model });
    }

    private PhotonCadCanonicalProject Encode(SmokePayload payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var digest = ComputeLogicalContentDigest(bytes);
        return new PhotonCadCanonicalProject(
            payload.SessionId,
            payload.ProjectId,
            payload.Revision,
            payload.Title,
            payload.Units,
            digest,
            PhotonCadBomCanonicalizer.Compute(payload.Units, []),
            payload.Dirty,
            bytes);
    }

    private static SmokePayload Parse(ReadOnlyMemory<byte> bytes) => JsonSerializer.Deserialize<SmokePayload>(bytes.Span)
        ?? throw new PhotonCadProjectException("decode_failed", nameof(bytes));

    private static string Digest(ReadOnlySpan<byte> bytes) => $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";

    private sealed record SmokePayload(
        string SessionId,
        string ProjectId,
        long Revision,
        string Title,
        PhotonCadProjectUnit Units,
        bool Dirty,
        string Model);
}

internal sealed class SmokeAtomicBackend : IPhotonCadAtomicStorageBackend
{
    private readonly object _sync = new();
    private readonly Dictionary<string, StoredFile> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Transaction> _transactions = new(StringComparer.Ordinal);
    private int _identity;
    private int _transaction;
    private int _activePrepare;
    private int _activeRead;
    private int _readCalls;

    internal bool ReparseOnInspect { get; set; }
    internal bool HardLinkOnInspect { get; set; }
    internal bool CrossVolumeOnPrepare { get; set; }
    internal bool OmitFlushOnPrepare { get; set; }
    internal bool ChangeIdentityBeforeCommit { get; set; }
    internal bool CorruptCommitDigest { get; set; }
    internal bool ThrowAfterCommitReplace { get; set; }
    internal bool ThrowOnCompleteOnce { get; set; }
    internal Action? AfterPrepare { get; set; }
    internal Action? AfterCommitReplace { get; set; }
    internal int ReadDelayMilliseconds { get; set; }
    internal int PrepareDelayMilliseconds { get; set; }
    internal int QuarantineCount { get; private set; }
    internal int MaximumConcurrentPrepare { get; private set; }
    internal int MaximumConcurrentRead { get; private set; }
    internal int LastMaximumReadBytes { get; private set; }
    internal int ReadCallCount => Volatile.Read(ref _readCalls);
    internal bool CommitReceivedCancelableToken { get; private set; }
    internal bool CompleteReceivedCancelableToken { get; private set; }

    public ValueTask<PhotonCadPathEvidence> InspectExactAsync(PhotonCadStorageTargetHandle target, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (ChangeIdentityBeforeCommit && _transactions.Values.Any(transaction => transaction.Prepared.Target.Equals(target))
                && _files.TryGetValue(target.Value, out var current))
            {
                _files[target.Value] = current with { Identity = Identity() };
                ChangeIdentityBeforeCommit = false;
            }
            return ValueTask.FromResult(Evidence(target));
        }
    }

    public async ValueTask<PhotonCadDurableRead> ReadExactAsync(PhotonCadStorageTargetHandle target, PhotonCadPathEvidence expected, int maximumBytes, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _readCalls);
        LastMaximumReadBytes = maximumBytes;
        var active = Interlocked.Increment(ref _activeRead);
        MaximumConcurrentRead = Math.Max(MaximumConcurrentRead, active);
        try
        {
            if (ReadDelayMilliseconds > 0) await Task.Delay(ReadDelayMilliseconds, cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                var before = Evidence(target);
                EnsureIdentity(expected, before);
                if (!_files.TryGetValue(target.Value, out var file)) throw new PhotonCadProjectException("target_missing", nameof(target));
                if (file.Bytes.Length > maximumBytes) throw new PhotonCadProjectException("content_too_large", nameof(maximumBytes));
                var copy = file.Bytes.ToArray();
                var after = Evidence(target);
                return new PhotonCadDurableRead(target, before, after, copy);
            }
        }
        finally { Interlocked.Decrement(ref _activeRead); }
    }

    public async ValueTask<PhotonCadPreparedWrite> PrepareSameVolumeAsync(PhotonCadStorageTargetHandle target, PhotonCadPathEvidence expectedTarget, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        var active = Interlocked.Increment(ref _activePrepare);
        MaximumConcurrentPrepare = Math.Max(MaximumConcurrentPrepare, active);
        try
        {
            if (PrepareDelayMilliseconds > 0) await Task.Delay(PrepareDelayMilliseconds, cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                EnsureIdentity(expectedTarget, Evidence(target));
                var number = Interlocked.Increment(ref _transaction);
                var stage = new PhotonCadStorageStageHandle($"cad-storage-stage:{number.ToString("x8").PadLeft(32, '0')}");
                var recovery = new PhotonCadRecoveryHandle($"cad-recovery:{number.ToString("x8").PadLeft(32, '0')}");
                var stageEvidence = new PhotonCadPathEvidence(
                    target,
                    Hash($"stage-path:{number}"),
                    Hash($"stage-chain:{number}"),
                    CrossVolumeOnPrepare ? "volume:other" : expectedTarget.VolumeIdentity,
                    expectedTarget.ParentFileIdentity,
                    Identity(),
                    exists: true,
                    exactPathMatched: true,
                    entireChainVerified: true,
                    containsReparsePoint: false,
                    hardLinkCount: 1,
                    DateTimeOffset.UtcNow);
                var prepared = new PhotonCadPreparedWrite(
                    stage,
                    recovery,
                    target,
                    expectedTarget,
                    stageEvidence,
                    bytes.Length,
                    Digest(bytes.Span),
                    SameVolume: !CrossVolumeOnPrepare,
                    ContentFlushedToDisk: !OmitFlushOnPrepare,
                    RecoveryJournalFlushedToDisk: !OmitFlushOnPrepare,
                    DateTimeOffset.UtcNow);
                var original = _files.TryGetValue(target.Value, out var existing)
                    ? new StoredFile(existing.Bytes.ToArray(), existing.Identity)
                    : null;
                _transactions.Add(stage.Value, new Transaction(prepared, bytes.ToArray(), original));
                AfterPrepare?.Invoke();
                return prepared;
            }
        }
        finally { Interlocked.Decrement(ref _activePrepare); }
    }

    public ValueTask<PhotonCadCommitProof> CommitAtomicAsync(PhotonCadPreparedWrite prepared, PhotonCadPathEvidence expectedTarget, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CommitReceivedCancelableToken |= cancellationToken.CanBeCanceled;
        lock (_sync)
        {
            if (!_transactions.TryGetValue(prepared.Stage.Value, out var transaction))
                throw new PhotonCadProjectException("prepared_stage_unknown", nameof(prepared));
            var current = Evidence(prepared.Target);
            EnsureIdentity(expectedTarget, current);
            var identity = Identity();
            _files[prepared.Target.Value] = new StoredFile(transaction.Bytes.ToArray(), identity);
            AfterCommitReplace?.Invoke();
            if (ThrowAfterCommitReplace)
            {
                ThrowAfterCommitReplace = false;
                throw new IOException("simulated failure after replace");
            }
            var after = Evidence(prepared.Target);
            return ValueTask.FromResult(new PhotonCadCommitProof(
                prepared.Stage,
                prepared.Target,
                current,
                after,
                transaction.Bytes.Length,
                CorruptCommitDigest ? Hash("wrong") : Digest(transaction.Bytes),
                ExpectedTargetIdentityMatched: true,
                AtomicReplace: true,
                ContentFlushedToDisk: true,
                DirectoryEntryFlushedToDisk: true,
                DateTimeOffset.UtcNow));
        }
    }

    public ValueTask CompleteTransactionAsync(PhotonCadPreparedWrite prepared, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CompleteReceivedCancelableToken |= cancellationToken.CanBeCanceled;
        lock (_sync)
        {
            if (ThrowOnCompleteOnce)
            {
                ThrowOnCompleteOnce = false;
                throw new IOException("simulated finalization failure");
            }
            _transactions.Remove(prepared.Stage.Value);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<PhotonCadRecoveryCandidate>> ListRecoveryCandidatesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            IReadOnlyList<PhotonCadRecoveryCandidate> candidates = _transactions.Values
                .Select(transaction => new PhotonCadRecoveryCandidate(transaction.Prepared, DateTimeOffset.UtcNow))
                .ToArray();
            return ValueTask.FromResult(candidates);
        }
    }

    public ValueTask<PhotonCadQuarantineOutcome> QuarantineAsync(PhotonCadPreparedWrite prepared, string reason, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_transactions.Remove(prepared.Stage.Value, out var transaction))
            {
                var targetContainsPreparedBytes = _files.TryGetValue(prepared.Target.Value, out var current)
                    && current.Bytes.AsSpan().SequenceEqual(transaction.Bytes);
                if (targetContainsPreparedBytes)
                {
                    if (transaction.Original is null) _files.Remove(prepared.Target.Value);
                    else _files[prepared.Target.Value] = new StoredFile(transaction.Original.Bytes.ToArray(), transaction.Original.Identity);
                }
            }
            QuarantineCount++;
        }
        return ValueTask.FromResult(new PhotonCadQuarantineOutcome(prepared.Recovery, true, true, reason));
    }

    internal async ValueTask<PhotonCadPreparedWrite> LeavePreparedAsync(PhotonCadStorageTargetHandle target, ReadOnlyMemory<byte> bytes)
    {
        var before = await InspectExactAsync(target);
        return await PrepareSameVolumeAsync(target, before, bytes);
    }

    internal void Seed(PhotonCadStorageTargetHandle target, ReadOnlyMemory<byte> bytes)
    {
        lock (_sync) _files[target.Value] = new StoredFile(bytes.ToArray(), Identity());
    }

    internal void Mutate(PhotonCadStorageTargetHandle target, ReadOnlyMemory<byte> bytes)
    {
        lock (_sync) _files[target.Value] = new StoredFile(bytes.ToArray(), Identity());
    }

    internal void MutateInPlace(PhotonCadStorageTargetHandle target, ReadOnlyMemory<byte> bytes)
    {
        lock (_sync)
        {
            var current = _files[target.Value];
            _files[target.Value] = new StoredFile(bytes.ToArray(), current.Identity);
        }
    }

    internal ReadOnlyMemory<byte> Bytes(PhotonCadStorageTargetHandle target)
    {
        lock (_sync) return _files[target.Value].Bytes.ToArray();
    }

    internal bool Exists(PhotonCadStorageTargetHandle target)
    {
        lock (_sync) return _files.ContainsKey(target.Value);
    }

    private PhotonCadPathEvidence Evidence(PhotonCadStorageTargetHandle target)
    {
        var exists = _files.TryGetValue(target.Value, out var file);
        return new PhotonCadPathEvidence(
            target,
            Hash($"path:{target.Value}"),
            Hash($"chain:{target.Value}"),
            "volume:fixed",
            "file:parent",
            exists ? file!.Identity : null,
            exists,
            exactPathMatched: true,
            entireChainVerified: true,
            containsReparsePoint: ReparseOnInspect,
            hardLinkCount: exists ? HardLinkOnInspect ? 2 : 1 : 0,
            DateTimeOffset.UtcNow);
    }

    private static void EnsureIdentity(PhotonCadPathEvidence expected, PhotonCadPathEvidence actual)
    {
        if (!expected.Target.Equals(actual.Target)
            || expected.Exists != actual.Exists
            || expected.EntryFileIdentity != actual.EntryFileIdentity
            || expected.ParentFileIdentity != actual.ParentFileIdentity
            || expected.CanonicalPathFingerprint != actual.CanonicalPathFingerprint)
            throw new PhotonCadProjectException("storage_identity_mismatch", nameof(expected));
    }

    private string Identity() => $"file:{Interlocked.Increment(ref _identity)}";
    private static string Digest(ReadOnlySpan<byte> bytes) => $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";
    private static string Hash(string value) => Digest(Encoding.UTF8.GetBytes(value));

    private sealed record StoredFile(byte[] Bytes, string Identity);
    private sealed record Transaction(PhotonCadPreparedWrite Prepared, byte[] Bytes, StoredFile? Original);
}
