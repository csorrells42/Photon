using PhotonCadProjects.RuntimeSync;

namespace PhotonCadProjects.Windows;

/// <summary>
/// One-shot host composition authority for a runtime mutation. The desktop host activates this
/// authority only while its generation operation gate is held. Native paths, overwrite grants,
/// OS identities, and renderer values are neither accepted nor returned by this type.
/// </summary>
public sealed class PhotonCadWindowsRuntimeProjectAuthority : IPhotonCadCanonicalMutationAuthority
{
    private readonly object _sync = new();
    private Operation? _active;
    private bool _closed;

    internal OperationLease BeginOperation(
        string generationId,
        PhotonCadProjectCoordinator coordinator,
        PhotonCadProjectDocument document,
        PhotonCadStorageVersion openedStorageVersion,
        long eligibleRevision,
        string eligibleContentDigest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generationId);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(openedStorageVersion);
        RequireEligibleBinding(document, eligibleRevision, eligibleContentDigest);

        var operation = new Operation(generationId, coordinator, document, openedStorageVersion);
        lock (_sync)
        {
            if (_closed) throw Failure("runtime_authority_closed", nameof(generationId));
            if (_active is not null) throw Failure("runtime_authority_busy", nameof(generationId));
            _active = operation;
        }
        return new OperationLease(this, operation);
    }

    public ValueTask<PhotonCadCanonicalMutationBinding> ResolveAsync(
        PhotonCadRuntimeSyncRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var operation = RequireActive();
            if (operation.Binding is not null) throw Failure("runtime_binding_already_resolved", nameof(request));
            var current = operation.Document.Snapshot;
            if (!StringComparer.Ordinal.Equals(request.SessionId, current.SessionId)
                || !StringComparer.Ordinal.Equals(request.ProjectId, current.ProjectId)
                || request.BaseRevision != current.Revision)
                throw Failure("runtime_request_binding_mismatch", nameof(request));
            operation.RequestId = request.RequestId;
            operation.Binding = new PhotonCadCanonicalMutationBinding(operation.Document.ProjectHandle, current);
            return ValueTask.FromResult(operation.Binding);
        }
    }

    public async ValueTask<PhotonCadCanonicalProject> CommitAsync(
        PhotonCadCanonicalMutationBinding expected,
        PhotonCadCanonicalProject updated,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(updated);
        Operation operation;
        lock (_sync)
        {
            operation = RequireActive();
            if (!ReferenceEquals(operation.Binding, expected))
                throw Failure("runtime_commit_binding_mismatch", nameof(expected));
            if (operation.CommitStarted) throw Failure("runtime_commit_already_started", nameof(expected));
            if (operation.RequestId is null) throw Failure("runtime_request_not_resolved", nameof(expected));
            operation.CommitStarted = true;
        }

        var current = operation.Document.Snapshot;
        var outcome = await operation.Coordinator.ApplyAndSaveCurrentProjectAsync(
            operation.RequestId,
            operation.Document.ProjectHandle,
            current.SessionId,
            current.ProjectId,
            current.Revision,
            current.ContentDigest,
            operation.OpenedStorageVersion,
            updated,
            cancellationToken).ConfigureAwait(false);

        if (outcome.Status != PhotonCadProjectApplyAndSaveStatus.Committed)
        {
            var code = outcome.Status switch
            {
                PhotonCadProjectApplyAndSaveStatus.CommittedReadbackRequired => "runtime_commit_readback_required",
                PhotonCadProjectApplyAndSaveStatus.CommittedRecoveryRequired => "runtime_commit_recovery_required",
                _ => "runtime_commit_outcome_invalid",
            };
            throw Failure(code, nameof(outcome));
        }

        var document = outcome.Document ?? throw Failure("runtime_commit_document_missing", nameof(outcome));
        var receipt = outcome.Receipt ?? throw Failure("runtime_commit_receipt_missing", nameof(outcome));
        RequireCommittedBinding(operation, expected, updated, document, receipt);
        lock (_sync)
        {
            if (!ReferenceEquals(_active, operation))
                throw Failure("runtime_authority_revoked", nameof(outcome));
            operation.CommittedDocument = document;
        }
        return document.Snapshot;
    }

    internal void RevokeGeneration(string generationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generationId);
        lock (_sync)
        {
            if (_active is { } operation && StringComparer.Ordinal.Equals(operation.GenerationId, generationId))
                _active = null;
        }
    }

    internal void Close()
    {
        lock (_sync)
        {
            _closed = true;
            _active = null;
        }
    }

    private static void RequireEligibleBinding(
        PhotonCadProjectDocument document,
        long eligibleRevision,
        string eligibleContentDigest)
    {
        var current = document.Snapshot;
        if (current.Units != PhotonCadProjectUnit.Millimeter)
            throw Failure("runtime_project_units_unsupported", nameof(document));
        if (current.Dirty) throw Failure("runtime_project_must_be_clean", nameof(document));
        if (eligibleRevision < 0
            || current.Revision != eligibleRevision
            || document.LastSavedRevision != eligibleRevision
            || !FixedDigestEquals(current.ContentDigest, eligibleContentDigest)
            || !FixedDigestEquals(current.ContentDigest, document.LastSavedContentDigest))
            throw Failure("runtime_project_save_binding_mismatch", nameof(document));
    }

    private static void RequireCommittedBinding(
        Operation operation,
        PhotonCadCanonicalMutationBinding expected,
        PhotonCadCanonicalProject updated,
        PhotonCadProjectDocument document,
        PhotonCadProjectMutationCommitReceipt receipt)
    {
        if (!document.ProjectHandle.Equals(operation.Document.ProjectHandle)
            || !document.ProjectHandle.Equals(expected.ProjectHandle)
            || !StringComparer.Ordinal.Equals(document.Snapshot.SessionId, updated.SessionId)
            || !StringComparer.Ordinal.Equals(document.Snapshot.ProjectId, updated.ProjectId)
            || document.Snapshot.Revision != updated.Revision
            || document.Snapshot.Dirty
            || !FixedDigestEquals(document.Snapshot.ContentDigest, updated.ContentDigest)
            || !FixedDigestEquals(document.Snapshot.BomDigest, updated.BomDigest)
            || !receipt.ProjectHandle.Equals(document.ProjectHandle)
            || receipt.BaseRevision != operation.Document.Snapshot.Revision
            || receipt.CommittedRevision != document.Snapshot.Revision
            || !FixedDigestEquals(receipt.BaseContentDigest, operation.Document.Snapshot.ContentDigest)
            || !FixedDigestEquals(receipt.ContentDigest, document.Snapshot.ContentDigest)
            || !FixedDigestEquals(receipt.BomDigest, document.Snapshot.BomDigest))
            throw Failure("runtime_committed_binding_mismatch", nameof(document));
    }

    private PhotonCadProjectDocument Complete(Operation operation, PhotonCadCanonicalProject savedProject)
    {
        ArgumentNullException.ThrowIfNull(savedProject);
        lock (_sync)
        {
            if (!ReferenceEquals(_active, operation)) throw Failure("runtime_authority_revoked", nameof(savedProject));
            var document = operation.CommittedDocument
                ?? throw Failure("runtime_commit_not_completed", nameof(savedProject));
            if (!StringComparer.Ordinal.Equals(savedProject.SessionId, document.Snapshot.SessionId)
                || !StringComparer.Ordinal.Equals(savedProject.ProjectId, document.Snapshot.ProjectId)
                || savedProject.Revision != document.Snapshot.Revision
                || !FixedDigestEquals(savedProject.ContentDigest, document.Snapshot.ContentDigest)
                || !FixedDigestEquals(savedProject.BomDigest, document.Snapshot.BomDigest))
                throw Failure("runtime_result_binding_mismatch", nameof(savedProject));
            _active = null;
            return document;
        }
    }

    private void End(Operation operation)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_active, operation)) _active = null;
        }
    }

    private Operation RequireActive() =>
        !_closed && _active is { } operation
            ? operation
            : throw Failure(_closed ? "runtime_authority_closed" : "runtime_authority_not_bound", nameof(_active));

    private static bool FixedDigestEquals(string left, string right)
    {
        var leftBytes = System.Text.Encoding.ASCII.GetBytes(left);
        var rightBytes = System.Text.Encoding.ASCII.GetBytes(right);
        return leftBytes.Length == rightBytes.Length
            && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static PhotonCadRuntimeSyncException Failure(string code, string field) => new(code, field);

    internal sealed class Operation(
        string generationId,
        PhotonCadProjectCoordinator coordinator,
        PhotonCadProjectDocument document,
        PhotonCadStorageVersion openedStorageVersion)
    {
        internal string GenerationId { get; } = generationId;
        internal PhotonCadProjectCoordinator Coordinator { get; } = coordinator;
        internal PhotonCadProjectDocument Document { get; } = document;
        internal PhotonCadStorageVersion OpenedStorageVersion { get; } = openedStorageVersion;
        internal string? RequestId { get; set; }
        internal PhotonCadCanonicalMutationBinding? Binding { get; set; }
        internal bool CommitStarted { get; set; }
        internal PhotonCadProjectDocument? CommittedDocument { get; set; }
    }

    internal sealed class OperationLease : IDisposable
    {
        private PhotonCadWindowsRuntimeProjectAuthority? _owner;
        private readonly Operation _operation;

        internal OperationLease(PhotonCadWindowsRuntimeProjectAuthority owner, Operation operation)
        {
            _owner = owner;
            _operation = operation;
        }

        internal PhotonCadProjectDocument Complete(PhotonCadCanonicalProject savedProject)
        {
            var owner = _owner ?? throw Failure("runtime_operation_lease_closed", nameof(savedProject));
            var document = owner.Complete(_operation, savedProject);
            _owner = null;
            return document;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.End(_operation);
        }
    }
}
