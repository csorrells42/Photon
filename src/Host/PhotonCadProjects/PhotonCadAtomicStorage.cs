using System.Security.Cryptography;

namespace PhotonCadProjects;

public sealed class PhotonCadStorageStageHandle(string value) : PhotonCadOpaqueHandle(value, "cad-storage-stage:");
public sealed class PhotonCadRecoveryHandle(string value) : PhotonCadOpaqueHandle(value, "cad-recovery:");
public sealed class PhotonCadOverwriteGrantHandle(string value) : PhotonCadOpaqueHandle(value, "cad-overwrite-grant:");

public enum PhotonCadOverwritePurpose
{
    Create,
    SaveAs,
}

/// <summary>
/// Host-only binding for an overwrite confirmation. Dispatchers must never accept one from,
/// or serialize one to, the renderer.
/// </summary>
public sealed class PhotonCadOverwriteGrantContext
{
    private PhotonCadOverwriteGrantContext(
        PhotonCadOverwritePurpose purpose,
        PhotonCadProjectHandle? sourceProjectHandle,
        string? sessionId,
        string? projectId,
        long? revision,
        string? contentDigest)
    {
        Purpose = ProjectGuards.Enum(purpose, nameof(purpose));
        if (purpose == PhotonCadOverwritePurpose.Create)
        {
            if (sourceProjectHandle is not null || sessionId is not null || projectId is not null || revision is not null || contentDigest is not null)
                throw new PhotonCadProjectException("overwrite_create_source_rejected", nameof(sourceProjectHandle));
            return;
        }
        SourceProjectHandle = sourceProjectHandle ?? throw new PhotonCadProjectException("required", nameof(sourceProjectHandle));
        SessionId = ProjectGuards.Identifier(sessionId, nameof(sessionId));
        ProjectId = ProjectGuards.Identifier(projectId, nameof(projectId));
        Revision = ProjectGuards.Revision(revision ?? -1, nameof(revision));
        ContentDigest = ProjectGuards.Digest(contentDigest, nameof(contentDigest));
    }

    public PhotonCadOverwritePurpose Purpose { get; }
    public PhotonCadProjectHandle? SourceProjectHandle { get; }
    public string? SessionId { get; }
    public string? ProjectId { get; }
    public long? Revision { get; }
    public string? ContentDigest { get; }

    public static PhotonCadOverwriteGrantContext ForCreate() => new(PhotonCadOverwritePurpose.Create, null, null, null, null, null);

    public static PhotonCadOverwriteGrantContext ForSaveAs(
        PhotonCadProjectHandle sourceProjectHandle,
        string sessionId,
        string projectId,
        long revision,
        string contentDigest) => new(
            PhotonCadOverwritePurpose.SaveAs,
            sourceProjectHandle,
            sessionId,
            projectId,
            revision,
            contentDigest);

    internal bool BindingEquals(PhotonCadOverwriteGrantContext other) => Purpose == other.Purpose
        && Equals(SourceProjectHandle, other.SourceProjectHandle)
        && StringComparer.Ordinal.Equals(SessionId, other.SessionId)
        && StringComparer.Ordinal.Equals(ProjectId, other.ProjectId)
        && Revision == other.Revision
        && ((ContentDigest is null && other.ContentDigest is null)
            || (ContentDigest is not null && other.ContentDigest is not null && ProjectGuards.FixedDigestEquals(ContentDigest, other.ContentDigest)));
}

public sealed record PhotonCadPathEvidence
{
    public PhotonCadPathEvidence(
        PhotonCadStorageTargetHandle target,
        string canonicalPathFingerprint,
        string verifiedChainFingerprint,
        string volumeIdentity,
        string parentFileIdentity,
        string? entryFileIdentity,
        bool exists,
        bool exactPathMatched,
        bool entireChainVerified,
        bool containsReparsePoint,
        int hardLinkCount,
        DateTimeOffset inspectedAtUtc)
    {
        Target = target ?? throw new PhotonCadProjectException("required", nameof(target));
        CanonicalPathFingerprint = ProjectGuards.Digest(canonicalPathFingerprint, nameof(canonicalPathFingerprint));
        VerifiedChainFingerprint = ProjectGuards.Digest(verifiedChainFingerprint, nameof(verifiedChainFingerprint));
        VolumeIdentity = ProjectGuards.Identifier(volumeIdentity, nameof(volumeIdentity));
        ParentFileIdentity = ProjectGuards.Identifier(parentFileIdentity, nameof(parentFileIdentity));
        EntryFileIdentity = entryFileIdentity is null ? null : ProjectGuards.Identifier(entryFileIdentity, nameof(entryFileIdentity));
        Exists = exists;
        ExactPathMatched = exactPathMatched;
        EntireChainVerified = entireChainVerified;
        ContainsReparsePoint = containsReparsePoint;
        if (hardLinkCount is < 0 or > 1_000_000)
            throw new PhotonCadProjectException("invalid_link_count", nameof(hardLinkCount));
        HardLinkCount = hardLinkCount;
        InspectedAtUtc = ProjectGuards.Utc(inspectedAtUtc, nameof(inspectedAtUtc));
    }

    public PhotonCadStorageTargetHandle Target { get; }
    public string CanonicalPathFingerprint { get; }
    public string VerifiedChainFingerprint { get; }
    public string VolumeIdentity { get; }
    public string ParentFileIdentity { get; }
    public string? EntryFileIdentity { get; }
    public bool Exists { get; }
    public bool ExactPathMatched { get; }
    public bool EntireChainVerified { get; }
    public bool ContainsReparsePoint { get; }
    public int HardLinkCount { get; }
    public DateTimeOffset InspectedAtUtc { get; }
}

public sealed record PhotonCadDurableRead(
    PhotonCadStorageTargetHandle Target,
    PhotonCadPathEvidence OpenedEvidence,
    PhotonCadPathEvidence ClosedEvidence,
    ReadOnlyMemory<byte> Bytes);

/// <summary>
/// Host-only last-seen storage version. OS identities remain internal and must not cross the
/// renderer boundary.
/// </summary>
public sealed class PhotonCadStorageVersion
{
    internal PhotonCadStorageVersion(PhotonCadPathEvidence evidence, long byteLength, string storageDigest)
    {
        Evidence = evidence ?? throw new PhotonCadProjectException("required", nameof(evidence));
        if (!evidence.Exists) throw new PhotonCadProjectException("target_missing", nameof(evidence));
        if (byteLength is <= 0 or > PhotonCadProjectContract.MaximumCanonicalProjectBytes)
            throw new PhotonCadProjectException("invalid_content_length", nameof(byteLength));
        ByteLength = byteLength;
        StorageDigest = ProjectGuards.Digest(storageDigest, nameof(storageDigest));
    }

    public PhotonCadStorageTargetHandle Target => Evidence.Target;
    public long ByteLength { get; }
    public string StorageDigest { get; }
    internal PhotonCadPathEvidence Evidence { get; }
}

public sealed class PhotonCadAtomicReadEvidence
{
    private readonly byte[] _bytes;

    internal PhotonCadAtomicReadEvidence(ReadOnlyMemory<byte> bytes, PhotonCadStorageVersion version)
    {
        _bytes = bytes.ToArray();
        Version = version ?? throw new PhotonCadProjectException("required", nameof(version));
    }

    public ReadOnlyMemory<byte> Bytes => _bytes.ToArray();
    public PhotonCadStorageVersion Version { get; }
}

public sealed class PhotonCadCommittedRecoveryRequiredException : PhotonCadProjectException
{
    internal PhotonCadCommittedRecoveryRequiredException(PhotonCadRecoveryHandle recoveryHandle, Exception innerException)
        : base("committed_recovery_required", "recovery", innerException)
    {
        RecoveryHandle = recoveryHandle ?? throw new ArgumentNullException(nameof(recoveryHandle));
    }

    /// <summary>Host-only recovery capability. Never serialize it to the renderer.</summary>
    public PhotonCadRecoveryHandle RecoveryHandle { get; }
}

public sealed record PhotonCadPreparedWrite(
    PhotonCadStorageStageHandle Stage,
    PhotonCadRecoveryHandle Recovery,
    PhotonCadStorageTargetHandle Target,
    PhotonCadPathEvidence TargetBefore,
    PhotonCadPathEvidence StageEvidence,
    long ByteLength,
    string StorageDigest,
    bool SameVolume,
    bool ContentFlushedToDisk,
    bool RecoveryJournalFlushedToDisk,
    DateTimeOffset PreparedAtUtc);

public sealed record PhotonCadCommitProof(
    PhotonCadStorageStageHandle Stage,
    PhotonCadStorageTargetHandle Target,
    PhotonCadPathEvidence TargetBefore,
    PhotonCadPathEvidence TargetAfter,
    long ByteLength,
    string PostWriteDigest,
    bool ExpectedTargetIdentityMatched,
    bool AtomicReplace,
    bool ContentFlushedToDisk,
    bool DirectoryEntryFlushedToDisk,
    DateTimeOffset CommittedAtUtc);

public sealed record PhotonCadRecoveryCandidate(
    PhotonCadPreparedWrite Prepared,
    DateTimeOffset DiscoveredAtUtc);

public sealed record PhotonCadQuarantineOutcome(
    PhotonCadRecoveryHandle Recovery,
    bool TransactionArtifactsIsolated,
    bool TargetRestoredOrIsolated,
    string Reason);

public sealed record PhotonCadRecoveryReport(
    int Discovered,
    int Completed,
    int Resumed,
    int Quarantined);

/// <summary>
/// Host-owned durable storage authority. Implementations must bind every method to an exact
/// local path and stable OS file identity; renderer data can only supply the opaque target.
/// </summary>
public interface IPhotonCadAtomicStorageBackend
{
    ValueTask<PhotonCadPathEvidence> InspectExactAsync(
        PhotonCadStorageTargetHandle target,
        CancellationToken cancellationToken = default);

    ValueTask<PhotonCadDurableRead> ReadExactAsync(
        PhotonCadStorageTargetHandle target,
        PhotonCadPathEvidence expected,
        int maximumBytes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// If this throws after creating durable artifacts, those artifacts must still appear in the
    /// recovery inventory; otherwise cancellation could orphan an untracked stage.
    /// </summary>
    ValueTask<PhotonCadPreparedWrite> PrepareSameVolumeAsync(
        PhotonCadStorageTargetHandle target,
        PhotonCadPathEvidence expectedTarget,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The call boundary is the durable commit point. Once replacement begins, implementations
    /// must finish or leave the flushed recovery journal sufficient to determine the outcome;
    /// they must never report ordinary cancellation after replacing the target.
    /// </summary>
    ValueTask<PhotonCadCommitProof> CommitAtomicAsync(
        PhotonCadPreparedWrite prepared,
        PhotonCadPathEvidence expectedTarget,
        CancellationToken cancellationToken = default);

    /// <summary>Must be idempotent and safe to retry during crash recovery.</summary>
    ValueTask CompleteTransactionAsync(
        PhotonCadPreparedWrite prepared,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<PhotonCadRecoveryCandidate>> ListRecoveryCandidatesAsync(
        CancellationToken cancellationToken = default);

    ValueTask<PhotonCadQuarantineOutcome> QuarantineAsync(
        PhotonCadPreparedWrite prepared,
        string reason,
        CancellationToken cancellationToken = default);
}

public sealed record PhotonCadAtomicSaveEvidence(
    PhotonCadStorageTargetHandle Target,
    string StorageDigest,
    long ByteLength,
    DateTimeOffset CommittedAtUtc,
    bool Atomic,
    bool FlushedToDisk,
    PhotonCadStorageVersion Version);

public sealed class PhotonCadAtomicProjectStore
{
    private static readonly TimeSpan OverwriteGrantTimeToLive = TimeSpan.FromMinutes(5);
    private readonly IPhotonCadAtomicStorageBackend _backend;
    private readonly IPhotonCadClock _clock;
    private readonly int _maximumEncodedBytes;
    private readonly object _grantSync = new();
    private readonly Dictionary<string, OverwriteGrantState> _overwriteGrants = new(StringComparer.Ordinal);

    public PhotonCadAtomicProjectStore(
        IPhotonCadAtomicStorageBackend backend,
        IPhotonCadClock? clock = null,
        int maximumEncodedBytes = PhotonCadProjectContract.MaximumCanonicalProjectBytes)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _clock = clock ?? new SystemPhotonCadClock();
        if (maximumEncodedBytes is < 1 or > PhotonCadProjectContract.MaximumCanonicalProjectBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumEncodedBytes));
        _maximumEncodedBytes = maximumEncodedBytes;
    }

    public int MaximumEncodedBytes => _maximumEncodedBytes;

    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(
        PhotonCadStorageTargetHandle target,
        CancellationToken cancellationToken = default) =>
        (await ReadVersionedAsync(target, cancellationToken).ConfigureAwait(false)).Bytes;

    public async ValueTask<PhotonCadAtomicReadEvidence> ReadVersionedAsync(
        PhotonCadStorageTargetHandle target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var before = await _backend.InspectExactAsync(target, cancellationToken).ConfigureAwait(false);
        EnsureSafeEvidence(before, requireExisting: true, "read_before");
        var read = await _backend.ReadExactAsync(
            target,
            before,
            _maximumEncodedBytes,
            cancellationToken).ConfigureAwait(false);
        if (!target.Equals(read.Target)) throw Failure("foreign_read_target", nameof(read));
        EnsureSafeEvidence(read.OpenedEvidence, requireExisting: true, "read_open");
        EnsureSafeEvidence(read.ClosedEvidence, requireExisting: true, "read_close");
        EnsureSameIdentity(before, read.OpenedEvidence, "read_open_identity_changed");
        EnsureSameIdentity(read.OpenedEvidence, read.ClosedEvidence, "read_identity_changed");
        if (read.Bytes.Length is <= 0 || read.Bytes.Length > _maximumEncodedBytes)
            throw Failure("invalid_content_length", nameof(read));
        var version = VersionFromRead(read);
        return new PhotonCadAtomicReadEvidence(read.Bytes, version);
    }

    public ValueTask<PhotonCadAtomicSaveEvidence> SaveAsync(
        PhotonCadStorageTargetHandle target,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default) =>
        SaveCoreAsync(target, bytes, WritePrecondition.MustNotExist(), cancellationToken);

    public ValueTask<PhotonCadAtomicSaveEvidence> SaveIfVersionAsync(
        PhotonCadStorageTargetHandle target,
        ReadOnlyMemory<byte> bytes,
        PhotonCadStorageVersion expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedVersion);
        return SaveCoreAsync(target, bytes, WritePrecondition.MatchOpenedVersion(expectedVersion), cancellationToken);
    }

    /// <summary>
    /// Host-only entry point called only after the native UI confirms replacement of the exact
    /// existing target. The returned capability is single-use and expires after five minutes.
    /// </summary>
    public async ValueTask<PhotonCadOverwriteGrantHandle> IssueOverwriteGrantAsync(
        PhotonCadStorageTargetHandle target,
        PhotonCadOverwriteGrantContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);
        var read = await ReadVersionedAsync(target, cancellationToken).ConfigureAwait(false);
        var now = ProjectGuards.Utc(_clock.UtcNow, nameof(_clock.UtcNow));
        lock (_grantSync)
        {
            PruneOverwriteGrants(now);
            while (_overwriteGrants.Count >= PhotonCadProjectContract.MaximumOverwriteGrants)
            {
                var oldest = _overwriteGrants.MinBy(pair => pair.Value.IssuedAtUtc).Key;
                _overwriteGrants.Remove(oldest);
            }
            PhotonCadOverwriteGrantHandle handle;
            do { handle = NewOverwriteGrant(); } while (_overwriteGrants.ContainsKey(handle.Value));
            _overwriteGrants.Add(handle.Value, new OverwriteGrantState(
                handle,
                target,
                context,
                read.Version,
                now,
                now.Add(OverwriteGrantTimeToLive)));
            return handle;
        }
    }

    public bool RevokeOverwriteGrant(PhotonCadOverwriteGrantHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        lock (_grantSync) return _overwriteGrants.Remove(handle.Value);
    }

    public ValueTask<PhotonCadAtomicSaveEvidence> SaveWithOverwriteGrantAsync(
        PhotonCadStorageTargetHandle target,
        ReadOnlyMemory<byte> bytes,
        PhotonCadOverwriteGrantHandle grantHandle,
        PhotonCadOverwriteGrantContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grantHandle);
        ArgumentNullException.ThrowIfNull(context);
        return SaveCoreAsync(target, bytes, WritePrecondition.ExplicitOverwrite(grantHandle, context), cancellationToken);
    }

    private async ValueTask<PhotonCadAtomicSaveEvidence> SaveCoreAsync(
        PhotonCadStorageTargetHandle target,
        ReadOnlyMemory<byte> bytes,
        WritePrecondition precondition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (bytes.Length is <= 0 || bytes.Length > _maximumEncodedBytes)
            throw Failure("invalid_content_length", nameof(bytes));
        var expectedStorageDigest = ProjectGuards.Sha256(bytes.Span);
        var resolvedPrecondition = ResolvePrecondition(target, precondition);
        var targetBefore = await _backend.InspectExactAsync(target, cancellationToken).ConfigureAwait(false);
        EnsureSafeEvidence(targetBefore, requireExisting: null, "save_before");
        targetBefore = await EnforcePreconditionAsync(target, targetBefore, resolvedPrecondition, cancellationToken).ConfigureAwait(false);
        PhotonCadPreparedWrite? prepared = null;
        var commitEntered = false;
        try
        {
            prepared = await _backend.PrepareSameVolumeAsync(target, targetBefore, bytes, cancellationToken).ConfigureAwait(false);
            ValidatePrepared(prepared, target, targetBefore, expectedStorageDigest, bytes.Length);
            var immediatelyBefore = await _backend.InspectExactAsync(target, cancellationToken).ConfigureAwait(false);
            EnsureSafeEvidence(immediatelyBefore, requireExisting: targetBefore.Exists, "save_precommit");
            EnsureSameIdentity(targetBefore, immediatelyBefore, "target_identity_changed");
            cancellationToken.ThrowIfCancellationRequested();
            commitEntered = true;
            var committed = await _backend.CommitAtomicAsync(prepared, immediatelyBefore, CancellationToken.None).ConfigureAwait(false);
            ValidateCommitted(committed, prepared, immediatelyBefore, expectedStorageDigest, bytes.Length);
            var readback = await _backend.ReadExactAsync(
                target,
                committed.TargetAfter,
                _maximumEncodedBytes,
                CancellationToken.None).ConfigureAwait(false);
            ValidateBoundRead(readback, committed.TargetAfter, "save_readback");
            if (readback.Bytes.Length != bytes.Length || !ProjectGuards.FixedDigestEquals(ProjectGuards.Sha256(readback.Bytes.Span), expectedStorageDigest))
                throw Failure("post_write_digest_mismatch", nameof(readback));
            await _backend.CompleteTransactionAsync(prepared, CancellationToken.None).ConfigureAwait(false);
            var version = VersionFromRead(readback);
            return new PhotonCadAtomicSaveEvidence(
                target,
                expectedStorageDigest,
                bytes.Length,
                ProjectGuards.Utc(committed.CommittedAtUtc, nameof(committed.CommittedAtUtc)),
                Atomic: true,
                FlushedToDisk: true,
                Version: version);
        }
        catch (Exception exception) when (commitEntered)
        {
            throw new PhotonCadCommittedRecoveryRequiredException(
                prepared?.Recovery ?? throw Failure("recovery_handle_missing", nameof(prepared)),
                exception);
        }
        catch (OperationCanceledException)
        {
            if (prepared is not null) await RequireQuarantineAsync(prepared, "cancelled", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (PhotonCadProjectException)
        {
            if (prepared is not null) await RequireQuarantineAsync(prepared, "save_failed", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            if (prepared is not null) await RequireQuarantineAsync(prepared, "storage_failure", CancellationToken.None).ConfigureAwait(false);
            throw new PhotonCadProjectException("storage_failure", nameof(target), exception);
        }
    }

    private WritePrecondition ResolvePrecondition(PhotonCadStorageTargetHandle target, WritePrecondition precondition)
    {
        if (precondition.Mode != WritePreconditionMode.ExplicitOverwriteGrant) return precondition;
        var handle = precondition.GrantHandle ?? throw Failure("overwrite_grant_missing", nameof(precondition));
        var context = precondition.GrantContext ?? throw Failure("overwrite_context_missing", nameof(precondition));
        var now = ProjectGuards.Utc(_clock.UtcNow, nameof(_clock.UtcNow));
        lock (_grantSync)
        {
            if (!_overwriteGrants.Remove(handle.Value, out var grant))
                throw Failure("overwrite_grant_unknown_or_used", nameof(handle));
            PruneOverwriteGrants(now);
            if (now >= grant.ExpiresAtUtc) throw Failure("overwrite_grant_expired", nameof(handle));
            if (!target.Equals(grant.Target) || !context.BindingEquals(grant.Context))
                throw Failure("overwrite_grant_binding_mismatch", nameof(handle));
            return WritePrecondition.MatchOpenedVersion(grant.Version);
        }
    }

    private async ValueTask<PhotonCadPathEvidence> EnforcePreconditionAsync(
        PhotonCadStorageTargetHandle target,
        PhotonCadPathEvidence current,
        WritePrecondition precondition,
        CancellationToken cancellationToken)
    {
        if (precondition.Mode == WritePreconditionMode.MustNotExist)
        {
            if (current.Exists) throw Failure("target_exists_overwrite_confirmation_required", nameof(target));
            return current;
        }
        var expected = precondition.ExpectedVersion ?? throw Failure("storage_version_missing", nameof(precondition));
        if (!target.Equals(expected.Target) || !current.Exists)
            throw Failure("storage_version_conflict", nameof(target));
        var read = await _backend.ReadExactAsync(
            target,
            current,
            _maximumEncodedBytes,
            cancellationToken).ConfigureAwait(false);
        ValidateBoundRead(read, current, "precondition_read");
        var actual = VersionFromRead(read);
        if (!SameStorageVersion(expected, actual))
            throw Failure("storage_version_conflict", nameof(target));
        return read.ClosedEvidence;
    }

    private PhotonCadStorageVersion VersionFromRead(PhotonCadDurableRead read)
    {
        ValidateBoundRead(read, read.OpenedEvidence, "version_read");
        return new PhotonCadStorageVersion(read.ClosedEvidence, read.Bytes.Length, ProjectGuards.Sha256(read.Bytes.Span));
    }

    internal static bool SameStorageVersion(PhotonCadStorageVersion left, PhotonCadStorageVersion right) =>
        left.ByteLength == right.ByteLength
        && ProjectGuards.FixedDigestEquals(left.StorageDigest, right.StorageDigest)
        && SameIdentity(left.Evidence, right.Evidence);

    private static PhotonCadOverwriteGrantHandle NewOverwriteGrant()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return new PhotonCadOverwriteGrantHandle($"cad-overwrite-grant:{token}");
    }

    private void PruneOverwriteGrants(DateTimeOffset now)
    {
        foreach (var expired in _overwriteGrants
                     .Where(pair => now >= pair.Value.ExpiresAtUtc)
                     .Select(pair => pair.Key)
                     .ToArray())
            _overwriteGrants.Remove(expired);
    }

    public async ValueTask<PhotonCadRecoveryReport> RecoverAsync(CancellationToken cancellationToken = default)
    {
        var candidates = await _backend.ListRecoveryCandidatesAsync(cancellationToken).ConfigureAwait(false)
            ?? throw Failure("recovery_inventory_missing", "recovery");
        if (candidates.Count > PhotonCadProjectContract.MaximumRecoveryCandidates
            || candidates.Any(candidate => candidate is null || candidate.Prepared is null))
            throw Failure("invalid_recovery_inventory", "recovery");
        var completed = 0;
        var resumed = 0;
        var quarantined = 0;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var prepared = candidate.Prepared ?? throw Failure("invalid_recovery_candidate", "recovery");
                if (prepared.ByteLength is <= 0 || prepared.ByteLength > _maximumEncodedBytes)
                    throw Failure("project_codec_size_policy_exceeded", "recovery");
                ValidatePrepared(prepared, prepared.Target, prepared.TargetBefore, prepared.StorageDigest, checked((int)prepared.ByteLength));
                var current = await _backend.InspectExactAsync(prepared.Target, cancellationToken).ConfigureAwait(false);
                EnsureSafeEvidence(current, requireExisting: null, "recovery_current");
                if (current.Exists)
                {
                    var read = await _backend.ReadExactAsync(
                        prepared.Target,
                        current,
                        _maximumEncodedBytes,
                        cancellationToken).ConfigureAwait(false);
                    ValidateBoundRead(read, current, "recovery_read");
                    if (read.Bytes.Length == prepared.ByteLength
                        && ProjectGuards.FixedDigestEquals(ProjectGuards.Sha256(read.Bytes.Span), prepared.StorageDigest))
                    {
                        try
                        {
                            await _backend.CompleteTransactionAsync(prepared, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception exception)
                        {
                            throw new PhotonCadCommittedRecoveryRequiredException(prepared.Recovery, exception);
                        }
                        completed++;
                        continue;
                    }
                }
                if (SameIdentity(prepared.TargetBefore, current))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var commit = await _backend.CommitAtomicAsync(prepared, current, CancellationToken.None).ConfigureAwait(false);
                        ValidateCommitted(commit, prepared, current, prepared.StorageDigest, checked((int)prepared.ByteLength));
                        var readback = await _backend.ReadExactAsync(
                            prepared.Target,
                            commit.TargetAfter,
                            _maximumEncodedBytes,
                            CancellationToken.None).ConfigureAwait(false);
                        ValidateBoundRead(readback, commit.TargetAfter, "recovery_readback");
                        if (readback.Bytes.Length != prepared.ByteLength
                            || !ProjectGuards.FixedDigestEquals(ProjectGuards.Sha256(readback.Bytes.Span), prepared.StorageDigest))
                            throw Failure("post_write_digest_mismatch", "recovery_readback");
                        await _backend.CompleteTransactionAsync(prepared, CancellationToken.None).ConfigureAwait(false);
                        resumed++;
                        continue;
                    }
                    catch (Exception exception)
                    {
                        throw new PhotonCadCommittedRecoveryRequiredException(prepared.Recovery, exception);
                    }
                }
                await RequireQuarantineAsync(prepared, "recovery_identity_conflict", cancellationToken).ConfigureAwait(false);
                quarantined++;
            }
            catch (OperationCanceledException) { throw; }
            catch (PhotonCadCommittedRecoveryRequiredException) { throw; }
            catch
            {
                await RequireQuarantineAsync(candidate.Prepared, "recovery_validation_failed", cancellationToken).ConfigureAwait(false);
                quarantined++;
            }
        }
        return new PhotonCadRecoveryReport(candidates.Count, completed, resumed, quarantined);
    }

    private static void ValidatePrepared(PhotonCadPreparedWrite prepared, PhotonCadStorageTargetHandle target, PhotonCadPathEvidence targetBefore, string digest, int byteLength)
    {
        if (!target.Equals(prepared.Target) || !target.Equals(prepared.TargetBefore.Target)
            || !target.Equals(prepared.StageEvidence.Target))
            throw Failure("foreign_prepared_target", nameof(prepared));
        EnsureSameIdentity(targetBefore, prepared.TargetBefore, "prepared_target_identity_changed");
        EnsureSafeEvidence(prepared.StageEvidence, requireExisting: true, "prepared_stage");
        if (!prepared.SameVolume || prepared.StageEvidence.VolumeIdentity != targetBefore.VolumeIdentity)
            throw Failure("cross_volume_stage", nameof(prepared));
        if (!prepared.ContentFlushedToDisk || !prepared.RecoveryJournalFlushedToDisk)
            throw Failure("prepare_not_durable", nameof(prepared));
        if (prepared.ByteLength != byteLength || !ProjectGuards.FixedDigestEquals(prepared.StorageDigest, digest))
            throw Failure("prepared_digest_mismatch", nameof(prepared));
        ProjectGuards.Utc(prepared.PreparedAtUtc, nameof(prepared.PreparedAtUtc));
    }

    private static void ValidateCommitted(PhotonCadCommitProof committed, PhotonCadPreparedWrite prepared, PhotonCadPathEvidence expectedTarget, string digest, int byteLength)
    {
        if (!prepared.Stage.Equals(committed.Stage) || !prepared.Target.Equals(committed.Target)
            || !prepared.Target.Equals(committed.TargetAfter.Target))
            throw Failure("foreign_commit", nameof(committed));
        EnsureSameIdentity(expectedTarget, committed.TargetBefore, "commit_expected_identity_changed");
        EnsureSafeEvidence(committed.TargetAfter, requireExisting: true, "commit_target");
        if (!committed.ExpectedTargetIdentityMatched || !committed.AtomicReplace || !committed.ContentFlushedToDisk || !committed.DirectoryEntryFlushedToDisk)
            throw Failure("commit_not_atomic_or_durable", nameof(committed));
        if (committed.ByteLength != byteLength || !ProjectGuards.FixedDigestEquals(committed.PostWriteDigest, digest))
            throw Failure("commit_digest_mismatch", nameof(committed));
        ProjectGuards.Utc(committed.CommittedAtUtc, nameof(committed.CommittedAtUtc));
    }

    private static void EnsureSafeEvidence(PhotonCadPathEvidence evidence, bool? requireExisting, string field)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (!evidence.ExactPathMatched || !evidence.EntireChainVerified)
            throw Failure("exact_path_unverified", field);
        if (evidence.ContainsReparsePoint) throw Failure("reparse_point_rejected", field);
        if (evidence.Exists != (evidence.EntryFileIdentity is not null)) throw Failure("file_identity_missing", field);
        if (evidence.Exists && evidence.HardLinkCount != 1) throw Failure("hard_link_rejected", field);
        if (!evidence.Exists && evidence.HardLinkCount != 0) throw Failure("missing_path_has_links", field);
        if (requireExisting.HasValue && evidence.Exists != requireExisting.Value)
            throw Failure(requireExisting.Value ? "target_missing" : "target_unexpectedly_exists", field);
        ProjectGuards.Utc(evidence.InspectedAtUtc, nameof(evidence.InspectedAtUtc));
    }

    private void ValidateBoundRead(PhotonCadDurableRead read, PhotonCadPathEvidence expected, string field)
    {
        if (read is null || !expected.Target.Equals(read.Target)) throw Failure("foreign_read_target", field);
        EnsureSafeEvidence(read.OpenedEvidence, requireExisting: true, field);
        EnsureSafeEvidence(read.ClosedEvidence, requireExisting: true, field);
        EnsureSameIdentity(expected, read.OpenedEvidence, "read_open_identity_changed");
        EnsureSameIdentity(read.OpenedEvidence, read.ClosedEvidence, "read_identity_changed");
        if (read.Bytes.Length is <= 0 || read.Bytes.Length > _maximumEncodedBytes)
            throw Failure("invalid_content_length", field);
    }

    private static void EnsureSameIdentity(PhotonCadPathEvidence expected, PhotonCadPathEvidence actual, string code)
    {
        if (!SameIdentity(expected, actual)) throw Failure(code, nameof(actual));
    }

    private static bool SameIdentity(PhotonCadPathEvidence left, PhotonCadPathEvidence right) =>
        left.Target.Equals(right.Target)
        && left.Exists == right.Exists
        && StringComparer.Ordinal.Equals(left.CanonicalPathFingerprint, right.CanonicalPathFingerprint)
        && StringComparer.Ordinal.Equals(left.VerifiedChainFingerprint, right.VerifiedChainFingerprint)
        && StringComparer.Ordinal.Equals(left.VolumeIdentity, right.VolumeIdentity)
        && StringComparer.Ordinal.Equals(left.ParentFileIdentity, right.ParentFileIdentity)
        && StringComparer.Ordinal.Equals(left.EntryFileIdentity, right.EntryFileIdentity)
        && left.ExactPathMatched == right.ExactPathMatched
        && left.EntireChainVerified == right.EntireChainVerified
        && left.ContainsReparsePoint == right.ContainsReparsePoint
        && left.HardLinkCount == right.HardLinkCount;

    private async ValueTask RequireQuarantineAsync(PhotonCadPreparedWrite prepared, string reason, CancellationToken cancellationToken)
    {
        var outcome = await _backend.QuarantineAsync(prepared, reason, cancellationToken).ConfigureAwait(false);
        if (outcome is null || !prepared.Recovery.Equals(outcome.Recovery)
            || !outcome.TransactionArtifactsIsolated || !outcome.TargetRestoredOrIsolated)
            throw Failure("quarantine_failed", "recovery");
        ProjectGuards.Identifier(outcome.Reason, nameof(outcome.Reason));
    }

    private static PhotonCadProjectException Failure(string code, string field) => new(code, field);

    private enum WritePreconditionMode
    {
        MustNotExist,
        MatchOpenedVersion,
        ExplicitOverwriteGrant,
    }

    private sealed record WritePrecondition(
        WritePreconditionMode Mode,
        PhotonCadStorageVersion? ExpectedVersion,
        PhotonCadOverwriteGrantHandle? GrantHandle,
        PhotonCadOverwriteGrantContext? GrantContext)
    {
        internal static WritePrecondition MustNotExist() => new(WritePreconditionMode.MustNotExist, null, null, null);
        internal static WritePrecondition MatchOpenedVersion(PhotonCadStorageVersion version) =>
            new(WritePreconditionMode.MatchOpenedVersion, version, null, null);
        internal static WritePrecondition ExplicitOverwrite(PhotonCadOverwriteGrantHandle handle, PhotonCadOverwriteGrantContext context) =>
            new(WritePreconditionMode.ExplicitOverwriteGrant, null, handle, context);
    }

    private sealed record OverwriteGrantState(
        PhotonCadOverwriteGrantHandle Handle,
        PhotonCadStorageTargetHandle Target,
        PhotonCadOverwriteGrantContext Context,
        PhotonCadStorageVersion Version,
        DateTimeOffset IssuedAtUtc,
        DateTimeOffset ExpiresAtUtc);
}
