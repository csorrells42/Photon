using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotonCadProjects.Windows;

/// <summary>
/// NTFS-only durable storage backend. All filesystem authority originates in the host-owned
/// target registry; renderer input is limited to opaque handles.
/// </summary>
public sealed class PhotonCadWindowsAtomicStorageBackend : IPhotonCadAtomicStorageBackend
{
    private const string JournalSchema = "photon.cad.windows-transaction/v1";
    private const string JournalPrefix = ".photon-cad-txn-";
    private const string JournalSuffix = ".journal";
    private const string StageSuffix = ".stage";
    private const int MaximumJournalBytes = 256 * 1024;
    private const int MaximumQuarantineFiles = 256;
    private static readonly JsonSerializerOptions JournalJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
    };

    private readonly PhotonCadWindowsTargetRegistry _registry;
    private readonly IPhotonCadClock _clock;
    private readonly object _sync = new();
    private readonly Dictionary<string, TransactionState> _transactions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _activeTargets = new(StringComparer.Ordinal);

    public PhotonCadWindowsAtomicStorageBackend(
        PhotonCadWindowsTargetRegistry registry,
        IPhotonCadClock? clock = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _clock = clock ?? new SystemPhotonCadClock();
    }

    public bool Available => _registry.Available;

    public ValueTask<PhotonCadPathEvidence> InspectExactAsync(
        PhotonCadStorageTargetHandle target,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(target);
        return ValueTask.FromResult(Evidence(target, PhotonCadWindowsNative.InspectExactPath(_registry.Resolve(target))));
    }

    public ValueTask<PhotonCadDurableRead> ReadExactAsync(
        PhotonCadStorageTargetHandle target,
        PhotonCadPathEvidence expected,
        int maximumBytes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(expected);
        if (maximumBytes is < 1 or > PhotonCadProjectContract.MaximumCanonicalProjectBytes)
            throw Failure("invalid_read_limit", nameof(maximumBytes));
        var path = _registry.Resolve(target);
        var before = Evidence(target, PhotonCadWindowsNative.InspectExactPath(path));
        RequireSameEvidence(expected, before, "read_expected_identity_changed");
        try
        {
            using var handle = PhotonCadWindowsNative.OpenReadLocked(path);
            var opened = Evidence(target, PhotonCadWindowsNative.InspectLockedFile(path, handle));
            RequireSameEvidence(before, opened, "read_open_identity_changed");
            var length = PhotonCadWindowsNative.FileLength(handle);
            if (length > maximumBytes) throw Failure("read_limit_exceeded", nameof(maximumBytes));
            var bytes = new byte[checked((int)length)];
            using (var stream = new FileStream(handle, FileAccess.Read, 1024 * 1024, isAsync: false))
            {
                var offset = 0;
                while (offset < bytes.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read == 0) throw Failure("unexpected_end_of_file", nameof(target));
                    offset += read;
                }
                if (stream.ReadByte() != -1) throw Failure("file_grew_during_read", nameof(target));
                var closed = Evidence(target, PhotonCadWindowsNative.InspectLockedFile(path, handle));
                RequireSameEvidence(opened, closed, "read_identity_changed");
                return ValueTask.FromResult(new PhotonCadDurableRead(target, opened, closed, bytes));
            }
        }
        catch (PhotonCadProjectException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Failure("durable_read_failed", nameof(target), exception);
        }
    }

    public ValueTask<PhotonCadPreparedWrite> PrepareSameVolumeAsync(
        PhotonCadStorageTargetHandle target,
        PhotonCadPathEvidence expectedTarget,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(expectedTarget);
        if (bytes.Length is < 1 or > PhotonCadProjectContract.MaximumCanonicalProjectBytes)
            throw Failure("invalid_content_length", nameof(bytes));
        var targetPath = _registry.Resolve(target);
        var targetCurrent = Evidence(target, PhotonCadWindowsNative.InspectExactPath(targetPath));
        RequireSameEvidence(expectedTarget, targetCurrent, "prepare_target_identity_changed");
        lock (_sync)
        {
            if (!_activeTargets.Add(target.Value)) throw Failure("target_transaction_in_progress", nameof(target));
        }
        try
        {
            var transactionId = PhotonCadWindowsNative.NewTransactionId();
            var parent = Path.GetDirectoryName(targetPath)!;
            var stagePath = Path.Combine(parent, $"{JournalPrefix}{transactionId}{StageSuffix}");
            var journalPath = Path.Combine(parent, $"{JournalPrefix}{transactionId}{JournalSuffix}");
            var stage = new PhotonCadStorageStageHandle($"cad-storage-stage:{PhotonCadWindowsNative.NewOpaqueToken()}");
            var recovery = new PhotonCadRecoveryHandle($"cad-recovery:{PhotonCadWindowsNative.NewOpaqueToken()}");
            var digest = PhotonCadWindowsNative.Sha256(bytes.Span);
            var preparedAt = ValidUtc(_clock.UtcNow);
            var record = JournalRecord.Preparing(
                transactionId,
                target.Value,
                stage.Value,
                recovery.Value,
                targetPath,
                Path.GetFileName(targetPath),
                stagePath,
                bytes.Length,
                digest,
                EvidenceRecord.From(expectedTarget),
                preparedAt);
            WriteJournalNew(journalPath, record);
            cancellationToken.ThrowIfCancellationRequested();
            PhotonCadWindowsNative.WriteDurableNewFile(stagePath, bytes.Span);
            PhotonCadWindowsNative.RequireDirectoryDurability(parent);
            var stageSnapshot = PhotonCadWindowsNative.InspectExactPath(stagePath);
            if (!stageSnapshot.Exists || stageSnapshot.HardLinkCount != 1)
                throw Failure("stage_identity_unverified", nameof(stage));
            var stageEvidence = Evidence(target, stageSnapshot);
            if (stageEvidence.VolumeIdentity != expectedTarget.VolumeIdentity)
                throw Failure("cross_volume_stage", nameof(stage));
            record = record.AsPrepared(EvidenceRecord.From(stageEvidence));
            ReplaceJournal(journalPath, record);
            var prepared = new PhotonCadPreparedWrite(
                stage,
                recovery,
                target,
                expectedTarget,
                stageEvidence,
                bytes.Length,
                digest,
                SameVolume: true,
                ContentFlushedToDisk: true,
                RecoveryJournalFlushedToDisk: true,
                preparedAt);
            var state = new TransactionState(record, journalPath, stagePath, prepared);
            lock (_sync) _transactions.Add(stage.Value, state);
            return ValueTask.FromResult(prepared);
        }
        catch
        {
            lock (_sync) _activeTargets.Remove(target.Value);
            throw;
        }
    }

    public ValueTask<PhotonCadCommitProof> CommitAtomicAsync(
        PhotonCadPreparedWrite prepared,
        PhotonCadPathEvidence expectedTarget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(expectedTarget);
        var state = FindTransaction(prepared);
        var targetPath = _registry.Resolve(prepared.Target);
        var before = Evidence(prepared.Target, PhotonCadWindowsNative.InspectExactPath(targetPath));
        RequireSameEvidence(expectedTarget, before, "commit_target_identity_changed");
        RequireSameEvidence(prepared.TargetBefore, before, "commit_prepared_identity_changed");
        var stageSnapshot = PhotonCadWindowsNative.InspectExactPath(state.StagePath);
        var stageEvidence = Evidence(prepared.Target, stageSnapshot);
        RequireSameEvidence(prepared.StageEvidence, stageEvidence, "commit_stage_identity_changed");
        var stageDigest = HashExactFile(state.StagePath, prepared.ByteLength);
        if (!PhotonCadWindowsNative.FixedDigestEquals(stageDigest, prepared.StorageDigest))
            throw Failure("commit_stage_digest_mismatch", nameof(prepared));
        state = state with { Record = state.Record.WithState("committing") };
        ReplaceJournal(state.JournalPath, state.Record);
        UpdateTransaction(state);

        // Intentionally noncancellable from this point. A caller cancellation cannot be reported
        // after the namespace entry may have changed.
        PhotonCadWindowsNative.CommitDurableFile(state.StagePath, targetPath, replaceExisting: before.Exists);
        PhotonCadWindowsNative.RequireDirectoryDurability(Path.GetDirectoryName(targetPath)!);
        var after = Evidence(prepared.Target, PhotonCadWindowsNative.InspectExactPath(targetPath));
        var postDigest = HashExactFile(targetPath, prepared.ByteLength);
        if (!PhotonCadWindowsNative.FixedDigestEquals(postDigest, prepared.StorageDigest))
            throw Failure("post_commit_digest_mismatch", nameof(prepared));
        state = state with { Record = state.Record.WithState("committed") };
        ReplaceJournal(state.JournalPath, state.Record);
        UpdateTransaction(state);
        return ValueTask.FromResult(new PhotonCadCommitProof(
            prepared.Stage,
            prepared.Target,
            before,
            after,
            prepared.ByteLength,
            postDigest,
            ExpectedTargetIdentityMatched: true,
            AtomicReplace: true,
            ContentFlushedToDisk: true,
            DirectoryEntryFlushedToDisk: true,
            ValidUtc(_clock.UtcNow)));
    }

    public ValueTask CompleteTransactionAsync(
        PhotonCadPreparedWrite prepared,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        TransactionState? state;
        lock (_sync) _transactions.TryGetValue(prepared.Stage.Value, out state);
        if (state is null) return ValueTask.CompletedTask;
        if (!TargetAlreadyContainsPreparedContent(state.Record))
            throw Failure("completion_target_unverified", nameof(prepared));
        var parent = Path.GetDirectoryName(state.JournalPath)!;
        if (File.Exists(state.StagePath)) PhotonCadWindowsNative.DeleteIfPresent(state.StagePath);
        if (File.Exists(state.JournalPath))
        {
            PhotonCadWindowsNative.DeleteIfPresent(state.JournalPath);
        }
        if (File.Exists(state.JournalPath + ".next")) PhotonCadWindowsNative.DeleteIfPresent(state.JournalPath + ".next");
        PhotonCadWindowsNative.RequireDirectoryDurability(parent);
        ForgetTransaction(state);
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<PhotonCadRecoveryCandidate>> ListRecoveryCandidatesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directories = _registry.KnownParentDirectories();
        foreach (var directory in directories)
            _ = PhotonCadWindowsNative.InspectExactPath(Path.Combine(directory, ".photon-cad-recovery-safety-probe"));
        var journals = directories
            .SelectMany(directory => Directory.EnumerateFiles(directory, $"{JournalPrefix}*{JournalSuffix}", SearchOption.TopDirectoryOnly))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (journals.Length > PhotonCadProjectContract.MaximumRecoveryCandidates)
            throw Failure("recovery_inventory_capacity_exceeded", "recovery");
        var candidates = new List<PhotonCadRecoveryCandidate>(journals.Length);
        foreach (var journal in journals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = LoadTransaction(journal);
            if (state is null) continue;
            lock (_sync)
            {
                if (!_transactions.ContainsKey(state.Prepared.Stage.Value))
                {
                    _transactions.Add(state.Prepared.Stage.Value, state);
                    _activeTargets.Add(state.Prepared.Target.Value);
                }
            }
            candidates.Add(new PhotonCadRecoveryCandidate(state.Prepared, ValidUtc(_clock.UtcNow)));
        }
        return ValueTask.FromResult<IReadOnlyList<PhotonCadRecoveryCandidate>>(candidates.AsReadOnly());
    }

    public ValueTask<PhotonCadQuarantineOutcome> QuarantineAsync(
        PhotonCadPreparedWrite prepared,
        string reason,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(prepared);
        var state = FindTransaction(prepared);
        var safeReason = SafeReason(reason);
        var parent = Path.GetDirectoryName(state.JournalPath)!;
        var quarantine = Path.Combine(parent, ".photon-cad-quarantine");
        Directory.CreateDirectory(quarantine);
        _ = PhotonCadWindowsNative.InspectExactPath(Path.Combine(quarantine, ".safety-probe"));
        var artifactCount = (File.Exists(state.StagePath) ? 1 : 0)
            + (File.Exists(state.JournalPath + ".next") ? 1 : 0)
            + (File.Exists(state.JournalPath) ? 1 : 0);
        EnsureQuarantineCapacity(quarantine, artifactCount, nameof(prepared));
        var suffix = $"{state.Record.TransactionId}-{PhotonCadWindowsNative.NewTransactionId()}";
        if (File.Exists(state.StagePath))
            PhotonCadWindowsNative.MoveDurableFile(state.StagePath, Path.Combine(quarantine, $"{suffix}.stage"));
        if (File.Exists(state.JournalPath + ".next"))
            PhotonCadWindowsNative.MoveDurableFile(state.JournalPath + ".next", Path.Combine(quarantine, $"{suffix}.next"));
        if (File.Exists(state.JournalPath))
            PhotonCadWindowsNative.MoveDurableFile(state.JournalPath, Path.Combine(quarantine, $"{suffix}.journal"));
        PhotonCadWindowsNative.RequireDirectoryDurability(quarantine);
        PhotonCadWindowsNative.RequireDirectoryDurability(parent);
        _ = PhotonCadWindowsNative.InspectExactPath(_registry.Resolve(prepared.Target));
        ForgetTransaction(state);
        return ValueTask.FromResult(new PhotonCadQuarantineOutcome(prepared.Recovery, true, true, safeReason));
    }

    private TransactionState? LoadTransaction(string journalPath)
    {
        DiscardInterruptedJournalUpdate(journalPath);
        var bytes = ReadJournalBytes(journalPath);
        RejectDuplicateProperties(bytes);
        JournalRecord record;
        try { record = JsonSerializer.Deserialize<JournalRecord>(bytes, JournalJson) ?? throw Failure("recovery_journal_invalid", "recovery"); }
        catch (PhotonCadProjectException) { throw; }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw Failure("recovery_journal_invalid", "recovery", exception);
        }
        ValidateJournalRecord(record, journalPath);
        var journalTarget = new PhotonCadStorageTargetHandle(record.TargetHandle);
        var stage = new PhotonCadStorageStageHandle(record.StageHandle);
        var recovery = new PhotonCadRecoveryHandle(record.RecoveryHandle);
        _ = record.TargetBefore.ToEvidence(journalTarget);
        var target = _registry.RestoreHostOnlyTarget(journalTarget, record.TargetPath, record.TargetLabel);
        if (!target.Equals(journalTarget))
        {
            record = record with { TargetHandle = target.Value };
            ReplaceJournal(journalPath, record);
        }
        var targetBefore = record.TargetBefore.ToEvidence(target);
        var stagePath = record.StagePath;
        PhotonCadPathEvidence stageEvidence;
        if (File.Exists(stagePath))
        {
            stageEvidence = Evidence(target, PhotonCadWindowsNative.InspectExactPath(stagePath));
            if (record.StageEvidence is not null) RequireSameEvidence(record.StageEvidence.ToEvidence(target), stageEvidence, "recovery_stage_identity_changed");
            if (!PhotonCadWindowsNative.FixedDigestEquals(HashExactFile(stagePath, record.ByteLength), record.StorageDigest))
                throw Failure("recovery_stage_digest_mismatch", "recovery");
        }
        else if (record.State == "committed" && record.StageEvidence is not null)
        {
            stageEvidence = record.StageEvidence.ToEvidence(target);
        }
        else if (record.State == "committing" && record.StageEvidence is not null
                 && TargetAlreadyContainsPreparedContent(record))
        {
            // The namespace replacement completed but the process stopped before the journal
            // could be advanced. Recovery may now verify the target digest and finish cleanup.
            stageEvidence = record.StageEvidence.ToEvidence(target);
        }
        else if (record.State == "committing")
        {
            QuarantineIncompleteJournal(record, journalPath);
            return null;
        }
        else if (record.State == "preparing")
        {
            QuarantineIncompleteJournal(record, journalPath);
            return null;
        }
        else
        {
            throw Failure("recovery_stage_missing", "recovery");
        }
        var prepared = new PhotonCadPreparedWrite(
            stage,
            recovery,
            target,
            targetBefore,
            stageEvidence,
            record.ByteLength,
            record.StorageDigest,
            SameVolume: true,
            ContentFlushedToDisk: true,
            RecoveryJournalFlushedToDisk: true,
            record.PreparedAtUtc);
        return new TransactionState(record, journalPath, stagePath, prepared);
    }

    private static void DiscardInterruptedJournalUpdate(string journalPath)
    {
        var next = journalPath + ".next";
        if (!File.Exists(next)) return;
        _ = PhotonCadWindowsNative.InspectExactPath(next);
        PhotonCadWindowsNative.DeleteIfPresent(next);
        PhotonCadWindowsNative.RequireDirectoryDurability(Path.GetDirectoryName(journalPath)!);
    }

    private void QuarantineIncompleteJournal(JournalRecord record, string journalPath)
    {
        var parent = Path.GetDirectoryName(journalPath)!;
        var quarantine = Path.Combine(parent, ".photon-cad-quarantine");
        Directory.CreateDirectory(quarantine);
        _ = PhotonCadWindowsNative.InspectExactPath(Path.Combine(quarantine, ".safety-probe"));
        var artifactCount = (File.Exists(record.StagePath) ? 1 : 0)
            + (File.Exists(journalPath + ".next") ? 1 : 0)
            + (File.Exists(journalPath) ? 1 : 0);
        EnsureQuarantineCapacity(quarantine, artifactCount, "recovery");
        var suffix = $"{record.TransactionId}-{PhotonCadWindowsNative.NewTransactionId()}";
        if (File.Exists(record.StagePath)) PhotonCadWindowsNative.MoveDurableFile(record.StagePath, Path.Combine(quarantine, $"{suffix}.stage"));
        if (File.Exists(journalPath + ".next")) PhotonCadWindowsNative.MoveDurableFile(journalPath + ".next", Path.Combine(quarantine, $"{suffix}.next"));
        PhotonCadWindowsNative.MoveDurableFile(journalPath, Path.Combine(quarantine, $"{suffix}.journal"));
        PhotonCadWindowsNative.RequireDirectoryDurability(quarantine);
        PhotonCadWindowsNative.RequireDirectoryDurability(parent);
    }

    private static bool TargetAlreadyContainsPreparedContent(JournalRecord record)
    {
        var target = PhotonCadWindowsNative.InspectExactPath(record.TargetPath);
        return target.Exists
            && target.HardLinkCount == 1
            && PhotonCadWindowsNative.FixedDigestEquals(HashExactFile(record.TargetPath, record.ByteLength), record.StorageDigest);
    }

    private static void EnsureQuarantineCapacity(string quarantinePath, int requiredFiles, string field)
    {
        if (requiredFiles is < 1 or > 3) throw Failure("invalid_quarantine_artifact_count", field);
        var existing = Directory.EnumerateFiles(quarantinePath, "*", SearchOption.TopDirectoryOnly)
            .Take(MaximumQuarantineFiles + 1)
            .Count();
        if (existing > MaximumQuarantineFiles - requiredFiles)
            throw Failure("quarantine_capacity_reached", field);
    }

    private TransactionState FindTransaction(PhotonCadPreparedWrite prepared)
    {
        lock (_sync)
        {
            if (!_transactions.TryGetValue(prepared.Stage.Value, out var state)
                || !state.Prepared.Recovery.Equals(prepared.Recovery)
                || !state.Prepared.Target.Equals(prepared.Target))
                throw Failure("transaction_unknown", nameof(prepared));
            return state;
        }
    }

    private void UpdateTransaction(TransactionState state)
    {
        lock (_sync) _transactions[state.Prepared.Stage.Value] = state;
    }

    private void ForgetTransaction(TransactionState state)
    {
        lock (_sync)
        {
            _transactions.Remove(state.Prepared.Stage.Value);
            _activeTargets.Remove(state.Prepared.Target.Value);
        }
    }

    private static PhotonCadPathEvidence Evidence(PhotonCadStorageTargetHandle target, PhotonCadWindowsPathSnapshot snapshot) => new(
        target,
        snapshot.CanonicalPathFingerprint,
        snapshot.VerifiedChainFingerprint,
        snapshot.VolumeIdentity,
        snapshot.ParentFileIdentity,
        snapshot.EntryFileIdentity,
        snapshot.Exists,
        exactPathMatched: true,
        entireChainVerified: true,
        containsReparsePoint: false,
        snapshot.HardLinkCount,
        snapshot.InspectedAtUtc);

    private static void RequireSameEvidence(PhotonCadPathEvidence expected, PhotonCadPathEvidence actual, string code)
    {
        if (!expected.Target.Equals(actual.Target)
            || expected.Exists != actual.Exists
            || expected.CanonicalPathFingerprint != actual.CanonicalPathFingerprint
            || expected.VerifiedChainFingerprint != actual.VerifiedChainFingerprint
            || expected.VolumeIdentity != actual.VolumeIdentity
            || expected.ParentFileIdentity != actual.ParentFileIdentity
            || expected.EntryFileIdentity != actual.EntryFileIdentity
            || expected.ExactPathMatched != actual.ExactPathMatched
            || expected.EntireChainVerified != actual.EntireChainVerified
            || expected.ContainsReparsePoint != actual.ContainsReparsePoint
            || expected.HardLinkCount != actual.HardLinkCount)
            throw Failure(code, nameof(actual));
    }

    private static string HashExactFile(string path, long expectedLength)
    {
        using var handle = PhotonCadWindowsNative.OpenReadLocked(path);
        if (PhotonCadWindowsNative.FileLength(handle) != expectedLength)
            throw Failure("file_length_mismatch", nameof(path));
        using var stream = new FileStream(handle, FileAccess.Read, 1024 * 1024, isAsync: false);
        return $"sha256:{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream))}";
    }

    private static void WriteJournalNew(string journalPath, JournalRecord record)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, JournalJson);
        if (bytes.Length > MaximumJournalBytes) throw Failure("recovery_journal_too_large", nameof(record));
        PhotonCadWindowsNative.WriteDurableNewFile(journalPath, bytes);
        PhotonCadWindowsNative.RequireDirectoryDurability(Path.GetDirectoryName(journalPath)!);
    }

    private static void ReplaceJournal(string journalPath, JournalRecord record)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, JournalJson);
        if (bytes.Length > MaximumJournalBytes) throw Failure("recovery_journal_too_large", nameof(record));
        var next = journalPath + ".next";
        if (File.Exists(next)) throw Failure("recovery_journal_update_pending", nameof(record));
        PhotonCadWindowsNative.WriteDurableNewFile(next, bytes);
        PhotonCadWindowsNative.ReplaceDurableFile(next, journalPath);
        PhotonCadWindowsNative.RequireDirectoryDurability(Path.GetDirectoryName(journalPath)!);
    }

    private static byte[] ReadJournalBytes(string journalPath)
    {
        var before = PhotonCadWindowsNative.InspectExactPath(journalPath);
        using var handle = PhotonCadWindowsNative.OpenReadLocked(journalPath);
        var opened = PhotonCadWindowsNative.InspectLockedFile(journalPath, handle);
        RequireSameSnapshot(before, opened, "recovery_journal_identity_changed");
        var length = PhotonCadWindowsNative.FileLength(handle);
        if (length is < 2 or > MaximumJournalBytes) throw Failure("recovery_journal_size_invalid", "recovery");
        var bytes = new byte[checked((int)length)];
        using var stream = new FileStream(handle, FileAccess.Read, 64 * 1024, isAsync: false);
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw Failure("recovery_journal_changed", "recovery");
        var closed = PhotonCadWindowsNative.InspectLockedFile(journalPath, handle);
        RequireSameSnapshot(opened, closed, "recovery_journal_changed");
        return bytes;
    }

    private static void RequireSameSnapshot(PhotonCadWindowsPathSnapshot expected, PhotonCadWindowsPathSnapshot actual, string code)
    {
        if (expected.Exists != actual.Exists
            || expected.CanonicalPathFingerprint != actual.CanonicalPathFingerprint
            || expected.VerifiedChainFingerprint != actual.VerifiedChainFingerprint
            || expected.VolumeIdentity != actual.VolumeIdentity
            || expected.ParentFileIdentity != actual.ParentFileIdentity
            || expected.EntryFileIdentity != actual.EntryFileIdentity
            || expected.HardLinkCount != actual.HardLinkCount)
            throw Failure(code, "recovery");
    }

    private static void RejectDuplicateProperties(ReadOnlySpan<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 16 });
        Inspect(document.RootElement);
        static void Inspect(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw Failure("duplicate_journal_property", "recovery");
                    Inspect(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray()) Inspect(item);
            }
        }
    }

    private static void ValidateJournalRecord(JournalRecord record, string journalPath)
    {
        if (record.Schema != JournalSchema
            || string.IsNullOrEmpty(record.TransactionId)
            || record.TransactionId.Length != 32
            || record.TransactionId.Any(character => !char.IsAsciiHexDigit(character))
            || record.State is not ("preparing" or "prepared" or "committing" or "committed")
            || string.IsNullOrEmpty(record.TargetHandle)
            || string.IsNullOrEmpty(record.StageHandle)
            || string.IsNullOrEmpty(record.RecoveryHandle)
            || string.IsNullOrEmpty(record.TargetPath)
            || string.IsNullOrEmpty(record.TargetLabel)
            || string.IsNullOrEmpty(record.StagePath)
            || record.ByteLength is < 1 or > PhotonCadProjectContract.MaximumCanonicalProjectBytes
            || string.IsNullOrEmpty(record.StorageDigest)
            || !record.StorageDigest.StartsWith("sha256:", StringComparison.Ordinal)
            || record.StorageDigest.Length != 71
            || record.StorageDigest.AsSpan(7).ContainsAnyExcept("0123456789abcdef")
            || (record.State == "preparing" ? record.StageEvidence is not null : record.StageEvidence is null)
            || record.TargetBefore is null)
            throw Failure("recovery_journal_invalid", "recovery");
        var canonicalJournal = PhotonCadWindowsNative.CanonicalizeLocalFilePath(journalPath);
        var expectedJournal = Path.Combine(Path.GetDirectoryName(record.TargetPath)!, $"{JournalPrefix}{record.TransactionId}{JournalSuffix}");
        var expectedStage = Path.Combine(Path.GetDirectoryName(record.TargetPath)!, $"{JournalPrefix}{record.TransactionId}{StageSuffix}");
        if (!string.Equals(canonicalJournal, PhotonCadWindowsNative.CanonicalizeLocalFilePath(expectedJournal), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(PhotonCadWindowsNative.CanonicalizeLocalFilePath(record.StagePath), PhotonCadWindowsNative.CanonicalizeLocalFilePath(expectedStage), StringComparison.OrdinalIgnoreCase))
            throw Failure("recovery_journal_path_mismatch", "recovery");
        _ = ValidUtc(record.PreparedAtUtc);
    }

    private static DateTimeOffset ValidUtc(DateTimeOffset value)
    {
        if (value == DateTimeOffset.MinValue || value == DateTimeOffset.MaxValue) throw Failure("invalid_timestamp", nameof(value));
        return value.ToUniversalTime();
    }

    private static string SafeReason(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "quarantined";
        var safe = new string(value.Take(64).Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_').ToArray());
        return safe.Length > 0 && char.IsAsciiLetterOrDigit(safe[0]) ? safe : "quarantined";
    }

    private static PhotonCadProjectException Failure(string code, string field, Exception? inner = null) =>
        inner is null ? new PhotonCadProjectException(code, field) : new PhotonCadProjectException(code, field, inner);

    private sealed record TransactionState(JournalRecord Record, string JournalPath, string StagePath, PhotonCadPreparedWrite Prepared);

    private sealed record JournalRecord
    {
        public required string Schema { get; init; }
        public required string TransactionId { get; init; }
        public required string State { get; init; }
        public required string TargetHandle { get; init; }
        public required string StageHandle { get; init; }
        public required string RecoveryHandle { get; init; }
        public required string TargetPath { get; init; }
        public required string TargetLabel { get; init; }
        public required string StagePath { get; init; }
        public required long ByteLength { get; init; }
        public required string StorageDigest { get; init; }
        public required EvidenceRecord TargetBefore { get; init; }
        public EvidenceRecord? StageEvidence { get; init; }
        public required DateTimeOffset PreparedAtUtc { get; init; }

        internal static JournalRecord Preparing(
            string transactionId,
            string targetHandle,
            string stageHandle,
            string recoveryHandle,
            string targetPath,
            string targetLabel,
            string stagePath,
            long byteLength,
            string storageDigest,
            EvidenceRecord targetBefore,
            DateTimeOffset preparedAtUtc) => new()
            {
                Schema = JournalSchema,
                TransactionId = transactionId,
                State = "preparing",
                TargetHandle = targetHandle,
                StageHandle = stageHandle,
                RecoveryHandle = recoveryHandle,
                TargetPath = targetPath,
                TargetLabel = targetLabel,
                StagePath = stagePath,
                ByteLength = byteLength,
                StorageDigest = storageDigest,
                TargetBefore = targetBefore,
                PreparedAtUtc = preparedAtUtc,
            };

        internal JournalRecord AsPrepared(EvidenceRecord stageEvidence) => this with { State = "prepared", StageEvidence = stageEvidence };
        internal JournalRecord WithState(string state) => this with { State = state };
    }

    private sealed record EvidenceRecord
    {
        public required string CanonicalPathFingerprint { get; init; }
        public required string VerifiedChainFingerprint { get; init; }
        public required string VolumeIdentity { get; init; }
        public required string ParentFileIdentity { get; init; }
        public string? EntryFileIdentity { get; init; }
        public required bool Exists { get; init; }
        public required int HardLinkCount { get; init; }
        public required DateTimeOffset InspectedAtUtc { get; init; }

        internal static EvidenceRecord From(PhotonCadPathEvidence value) => new()
        {
            CanonicalPathFingerprint = value.CanonicalPathFingerprint,
            VerifiedChainFingerprint = value.VerifiedChainFingerprint,
            VolumeIdentity = value.VolumeIdentity,
            ParentFileIdentity = value.ParentFileIdentity,
            EntryFileIdentity = value.EntryFileIdentity,
            Exists = value.Exists,
            HardLinkCount = value.HardLinkCount,
            InspectedAtUtc = value.InspectedAtUtc,
        };

        internal PhotonCadPathEvidence ToEvidence(PhotonCadStorageTargetHandle target) => new(
            target,
            CanonicalPathFingerprint,
            VerifiedChainFingerprint,
            VolumeIdentity,
            ParentFileIdentity,
            EntryFileIdentity,
            Exists,
            exactPathMatched: true,
            entireChainVerified: true,
            containsReparsePoint: false,
            HardLinkCount,
            InspectedAtUtc);
    }
}
