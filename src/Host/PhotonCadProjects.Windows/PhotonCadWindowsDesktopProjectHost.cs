using PhotonCadProjects.RuntimeSync;

namespace PhotonCadProjects.Windows;

/// <summary>
/// One reset generation of the native persistence composition. The coordinator and registry are
/// deliberately paired so no wrapper can accidentally create a second project authority.
/// </summary>
public sealed class PhotonCadWindowsDesktopProjectComposition : IAsyncDisposable
{
    private readonly IDisposable? _lifetime;
    private int _disposed;

    public PhotonCadWindowsDesktopProjectComposition(
        PhotonCadProjectCoordinator coordinator,
        PhotonCadAtomicProjectStore store,
        IPhotonCadAtomicStorageBackend storageBackend,
        IPhotonCadNativeWorkspacePicker picker,
        bool available,
        IDisposable? lifetime = null)
    {
        Coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        Store = store ?? throw new ArgumentNullException(nameof(store));
        StorageBackend = storageBackend ?? throw new ArgumentNullException(nameof(storageBackend));
        Picker = picker ?? throw new ArgumentNullException(nameof(picker));
        Available = available;
        _lifetime = lifetime;
    }

    public PhotonCadProjectCoordinator Coordinator { get; }
    public PhotonCadAtomicProjectStore Store { get; }
    public IPhotonCadAtomicStorageBackend StorageBackend { get; }
    public IPhotonCadNativeWorkspacePicker Picker { get; }
    public bool Available { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await Coordinator.DisposeAsync().ConfigureAwait(false); }
        finally { _lifetime?.Dispose(); }
    }
}

/// <summary>Injected generation factory used by isolated tests and the reusable reset lifecycle.</summary>
public interface IPhotonCadWindowsDesktopProjectCompositionFactory
{
    PhotonCadWindowsDesktopProjectComposition Create();
}

/// <summary>
/// Opaque host-only registration for one generation's RuntimeSync composition. It exposes no
/// provider, authority, native path, or storage identity.
/// </summary>
public sealed class PhotonCadWindowsRuntimeSynchronizerRegistration
{
    private readonly PhotonCadWindowsRuntimeProjectAuthority _authority;

    internal PhotonCadWindowsRuntimeSynchronizerRegistration(
        PhotonCadWindowsRuntimeProjectAuthority authority,
        string generationId,
        PhotonCadRuntimeProjectSynchronizer synchronizer)
    {
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        GenerationId = generationId ?? throw new ArgumentNullException(nameof(generationId));
        Synchronizer = synchronizer ?? throw new ArgumentNullException(nameof(synchronizer));
    }

    internal string GenerationId { get; }
    internal PhotonCadRuntimeProjectSynchronizer Synchronizer { get; }

    internal bool IsOwnedBy(PhotonCadWindowsRuntimeProjectAuthority authority, string generationId) =>
        ReferenceEquals(_authority, authority)
        && StringComparer.Ordinal.Equals(GenerationId, generationId);
}

/// <summary>
/// Resettable, provider-neutral project façade for the desktop host. Native paths exist only in
/// the paired Windows registry/backend and are never returned by this type.
/// </summary>
public sealed class PhotonCadWindowsDesktopProjectHost : IPhotonCadDesktopProjectHost
{
    private static readonly TimeSpan SelectionTimeToLive = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaximumCleanupTimeout = TimeSpan.FromSeconds(30);
    private readonly IPhotonCadWindowsDesktopProjectCompositionFactory _factory;
    private readonly IPhotonCadWindowsDirtyCloseAuthority _dirtyClose;
    private readonly IPhotonCadWindowsOverwriteAuthority _overwrite;
    private readonly IPhotonCadDesktopProjectReferenceRevoker _references;
    private readonly IPhotonCadClock _clock;
    private readonly TimeSpan _cleanupTimeout;
    private readonly PhotonCadWindowsRuntimeProjectAuthority _runtimeAuthority = new();
    private readonly object _sync = new();
    private readonly SemaphoreSlim _resetGate = new(1, 1);
    private Generation? _generation;
    private int _disposeStarted;

    public PhotonCadWindowsDesktopProjectHost(
        IPhotonCadProjectCodec codec,
        IPhotonCadWindowsFileDialog? dialog = null,
        IPhotonCadWindowsDirtyCloseAuthority? dirtyClose = null,
        IPhotonCadWindowsOverwriteAuthority? overwrite = null,
        IPhotonCadDesktopProjectReferenceRevoker? references = null,
        IPhotonCadHandleIssuer? handles = null,
        IPhotonCadClock? clock = null,
        TimeSpan? cleanupTimeout = null)
        : this(
            new ProductionCompositionFactory(codec, dialog, handles, clock),
            dirtyClose,
            overwrite,
            references,
            clock,
            cleanupTimeout)
    {
    }

    public PhotonCadWindowsDesktopProjectHost(
        IPhotonCadWindowsDesktopProjectCompositionFactory factory,
        IPhotonCadWindowsDirtyCloseAuthority? dirtyClose = null,
        IPhotonCadWindowsOverwriteAuthority? overwrite = null,
        IPhotonCadDesktopProjectReferenceRevoker? references = null,
        IPhotonCadClock? clock = null,
        TimeSpan? cleanupTimeout = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _dirtyClose = dirtyClose ?? new PhotonCadWindowsDirtyCloseAuthority();
        _overwrite = overwrite ?? new PhotonCadWindowsOverwriteAuthority();
        _references = references ?? NullPhotonCadDesktopProjectReferenceRevoker.Instance;
        _clock = clock ?? new SystemPhotonCadClock();
        _cleanupTimeout = RequireCleanupTimeout(cleanupTimeout ?? MaximumCleanupTimeout);
        _generation = NewGeneration();
    }

    public PhotonCadWindowsDesktopProjectReadiness Readiness
    {
        get
        {
            if (Volatile.Read(ref _disposeStarted) != 0)
                return new PhotonCadWindowsDesktopProjectReadiness(false, "project-host-closed");
            lock (_sync)
            {
                return _generation is { Composition.Available: true }
                    ? new PhotonCadWindowsDesktopProjectReadiness(true, "project-host-ready")
                    : new PhotonCadWindowsDesktopProjectReadiness(false, "project-host-unavailable");
            }
        }
    }

    /// <summary>
    /// Creates a provider-neutral synchronizer bound to this host's sole canonical authority.
    /// Product composition must supply a verified container-backed provider and compensator.
    /// </summary>
    public PhotonCadWindowsRuntimeSynchronizerRegistration CreateRuntimeProjectSynchronizer(
        IPhotonCadSealedMutationProvider provider,
        IPhotonCadSealedMutationCompensator compensator,
        PhotonCadRuntimeCanonicalMapperV1 mapper)
    {
        ThrowIfDisposed();
        var generation = CurrentGeneration();
        return new PhotonCadWindowsRuntimeSynchronizerRegistration(
            _runtimeAuthority,
            generation.Id,
            new PhotonCadRuntimeProjectSynchronizer(_runtimeAuthority, provider, compensator, mapper));
    }

    /// <summary>
    /// Applies the initial clean millimeter revision-zero runtime tranche while the current native
    /// generation gate is held. Native targets and storage versions remain host-only.
    /// </summary>
    public ValueTask<PhotonCadRuntimeSyncResult> ApplyRuntimeMutationAsync(
        PhotonCadWindowsRuntimeSynchronizerRegistration synchronizer,
        PhotonCadRuntimeSyncRequest request,
        CancellationToken cancellationToken = default) => UseAsync(async (generation, token) =>
    {
        ArgumentNullException.ThrowIfNull(synchronizer);
        ArgumentNullException.ThrowIfNull(request);
        if (!synchronizer.IsOwnedBy(_runtimeAuthority, generation.Id))
            throw Failure("runtime_synchronizer_foreign_or_stale", nameof(synchronizer));
        var hosted = generation.ResolveRuntimeProject(request);
        var persisted = await generation.Composition.Store
            .ReadVersionedAsync(hosted.Attachment.Target, token).ConfigureAwait(false);
        EnsureCurrent(generation);
        RequireExactRuntimeStorageBinding(hosted, persisted);

        using var authority = _runtimeAuthority.BeginOperation(
            generation.Id,
            generation.Composition.Coordinator,
            hosted.Document,
            persisted.Version,
            hosted.Attachment.Revision,
            hosted.Attachment.ContentDigest);
        var result = await synchronizer.Synchronizer.SynchronizeAsync(request, token).ConfigureAwait(false);
        var committed = authority.Complete(result.SavedProject);
        generation.Track(committed);
        generation.AdvanceRuntimeAttachment(hosted.Attachment, committed);
        return result;
    }, cancellationToken);

    public ValueTask<PhotonCadDesktopProjectPickerOutcome> ChooseWorkspaceAsync(
        string requestId,
        string purpose,
        CancellationToken cancellationToken = default) => UseAsync(async (generation, token) =>
    {
        _ = SafeRequestId(requestId);
        if (purpose is not ("new" or "open" or "save-as"))
            throw Failure("unsupported_picker_purpose", nameof(purpose));
        var selection = await generation.Composition.Picker.ChooseAsync(purpose, token).ConfigureAwait(false);
        EnsureCurrent(generation);
        if (selection.Status != PhotonCadNativeServiceStatus.Selected)
            return new PhotonCadDesktopProjectPickerOutcome(selection.Status, selection.Reason, null);
        var binding = selection.Binding ?? throw Failure("selected_workspace_required", nameof(selection));
        var workspace = await generation.Composition.Coordinator.RegisterWorkspaceAsync(binding, token).ConfigureAwait(false);
        EnsureCurrent(generation);
        generation.PruneSelections(_clock.UtcNow);
        if (generation.Selections.Count >= PhotonCadProjectContract.MaximumKnownWorkspaces
            && !generation.Selections.ContainsKey(workspace.WorkspaceHandle.Value))
            throw Failure("workspace_selection_capacity_reached", nameof(workspace));
        generation.Selections[workspace.WorkspaceHandle.Value] = new SelectionState(workspace, purpose, _clock.UtcNow);
        return new PhotonCadDesktopProjectPickerOutcome(PhotonCadNativeServiceStatus.Selected, "native-workspace-selected", workspace);
    }, cancellationToken);

    public ValueTask<PhotonCadProjectDocument> CreateProjectAsync(
        PhotonCadProjectCreateRequest request,
        CancellationToken cancellationToken = default) => UseAsync(async (generation, token) =>
    {
        ArgumentNullException.ThrowIfNull(request);
        var selection = generation.ConsumeSelection(request.WorkspaceHandle, "new", _clock.UtcNow);
        if (generation.WorkspaceProjects.ContainsKey(request.WorkspaceHandle.Value))
            throw Failure("storage_target_already_open", nameof(request.WorkspaceHandle));
        await ConfirmOverwriteIfRequiredAsync(generation, selection.Workspace, PhotonCadOverwritePurpose.Create, null, token).ConfigureAwait(false);
        var document = await generation.Composition.Coordinator.CreateProjectAsync(request, token).ConfigureAwait(false);
        generation.Track(document);
        generation.EstablishRuntimeAttachment(document, selection.Workspace.Binding.Target);
        return document;
    }, cancellationToken);

    public ValueTask<PhotonCadProjectDocument> OpenProjectAsync(
        PhotonCadProjectOpenRequest request,
        CancellationToken cancellationToken = default) => UseAsync(async (generation, token) =>
    {
        ArgumentNullException.ThrowIfNull(request);
        var selection = generation.ConsumeSelection(request.WorkspaceHandle, "open", _clock.UtcNow);
        if (generation.WorkspaceProjects.TryGetValue(request.WorkspaceHandle.Value, out var existingHandle)
            && generation.Documents.TryGetValue(existingHandle, out var existing))
        {
            generation.RevokeRuntimeAttachment(existing.ProjectHandle);
            return existing;
        }
        var document = await generation.Composition.Coordinator.OpenProjectAsync(request, token).ConfigureAwait(false);
        generation.RevokeRuntimeAttachmentsForProject(document.Snapshot.ProjectId);
        generation.Track(document);
        return document;
    }, cancellationToken);

    public ValueTask<PhotonCadProjectDocument> ReopenProjectAsync(
        PhotonCadProjectReopenRequest request,
        CancellationToken cancellationToken = default) => UseAsync(async (generation, token) =>
    {
        ArgumentNullException.ThrowIfNull(request);
        var document = await generation.Composition.Coordinator.ReopenProjectAsync(request, token).ConfigureAwait(false);
        generation.RevokeRuntimeAttachmentsForProject(document.Snapshot.ProjectId);
        generation.Track(document);
        return document;
    }, cancellationToken);

    public ValueTask<PhotonCadProjectDocument> RefreshProjectAsync(
        PhotonCadProjectRefreshRequest request,
        CancellationToken cancellationToken = default) => UseAsync(async (generation, token) =>
    {
        ArgumentNullException.ThrowIfNull(request);
        await _references.RevokeProjectAsync(request.ProjectHandle, token).ConfigureAwait(false);
        generation.RevokeRuntimeAttachment(request.ProjectHandle);
        var document = await generation.Composition.Coordinator.RefreshProjectAsync(request, token).ConfigureAwait(false);
        generation.Track(document);
        return document;
    }, cancellationToken);

    public ValueTask<PhotonCadProjectSaveOutcome> SaveProjectAsync(
        PhotonCadProjectSaveRequest request,
        CancellationToken cancellationToken = default) => UseAsync(async (generation, token) =>
    {
        ArgumentNullException.ThrowIfNull(request);
        var outcome = await generation.Composition.Coordinator.SaveProjectAsync(request, token).ConfigureAwait(false);
        generation.Track(outcome.Document);
        return outcome;
    }, cancellationToken);

    public ValueTask<PhotonCadProjectSaveOutcome> SaveProjectAsAsync(
        PhotonCadProjectSaveAsRequest request,
        CancellationToken cancellationToken = default) => UseAsync(async (generation, token) =>
    {
        ArgumentNullException.ThrowIfNull(request);
        var selection = generation.ConsumeSelection(request.DestinationWorkspaceHandle, "save-as", _clock.UtcNow);
        var source = generation.GetDocument(request.SourceProjectHandle);
        if (generation.WorkspaceProjects.ContainsKey(request.DestinationWorkspaceHandle.Value))
            throw Failure("destination_target_already_open", nameof(request.DestinationWorkspaceHandle));
        await ConfirmOverwriteIfRequiredAsync(generation, selection.Workspace, PhotonCadOverwritePurpose.SaveAs, source, token).ConfigureAwait(false);
        await _references.RevokeProjectAsync(source.ProjectHandle, token).ConfigureAwait(false);
        var outcome = await generation.Composition.Coordinator.SaveProjectAsAsync(request, token).ConfigureAwait(false);
        generation.RevokeRuntimeAttachment(source.ProjectHandle);
        generation.Untrack(source);
        generation.Track(outcome.Document);
        return outcome;
    }, cancellationToken);

    public ValueTask<PhotonCadProjectCloseOutcome> CloseProjectAsync(
        PhotonCadProjectCloseRequest request,
        CancellationToken cancellationToken = default) => UseAsync(async (generation, token) =>
    {
        ArgumentNullException.ThrowIfNull(request);
        var document = generation.GetDocument(request.ProjectHandle);
        RequireCloseBinding(document, request);
        if (document.Snapshot.Dirty && request.DiscardUnsavedChanges)
        {
            var confirmed = await _dirtyClose.ConfirmDiscardAsync(new PhotonCadWindowsDirtyCloseContext(document), token).ConfigureAwait(false);
            EnsureCurrent(generation);
            if (!confirmed) throw Failure("dirty_close_confirmation_declined", nameof(request));
        }
        await _references.RevokeProjectAsync(document.ProjectHandle, token).ConfigureAwait(false);
        var outcome = await generation.Composition.Coordinator.CloseProjectAsync(request, token).ConfigureAwait(false);
        generation.RevokeRuntimeAttachment(document.ProjectHandle);
        generation.Untrack(document);
        return outcome;
    }, cancellationToken);

    public async ValueTask ResetAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _resetGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            var replacement = NewGeneration();
            Generation previous;
            lock (_sync)
            {
                previous = _generation ?? throw new ObjectDisposedException(nameof(PhotonCadWindowsDesktopProjectHost));
                _generation = replacement;
            }
            previous.Cancellation.Cancel();
            Exception? failure = null;
            using var cleanup = new CancellationTokenSource(_cleanupTimeout);
            try
            {
                await _references.RevokeAllAsync(cleanup.Token).AsTask()
                    .WaitAsync(_cleanupTimeout).ConfigureAwait(false);
            }
            catch (Exception exception) { failure = exception; }
            try
            {
                await previous.DisposeAsync().AsTask()
                    .WaitAsync(_cleanupTimeout).ConfigureAwait(false);
            }
            catch (Exception exception) { failure ??= exception; }
            finally { _runtimeAuthority.RevokeGeneration(previous.Id); }
            if (failure is not null)
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_generation, replacement)) _generation = null;
                }
                replacement.Cancellation.Cancel();
                try
                {
                    await replacement.DisposeAsync().AsTask()
                        .WaitAsync(_cleanupTimeout).ConfigureAwait(false);
                }
                catch (Exception cleanupFailure)
                {
                    failure = new AggregateException(failure, cleanupFailure);
                }
                finally { _runtimeAuthority.RevokeGeneration(replacement.Id); }
                throw failure;
            }
        }
        finally { _resetGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposeStarted, 1, 0) != 0) return;
        await _resetGate.WaitAsync().ConfigureAwait(false);
        try
        {
            Generation? previous;
            lock (_sync)
            {
                previous = _generation;
                _generation = null;
            }
            previous?.Cancellation.Cancel();
            Exception? failure = null;
            using var cleanup = new CancellationTokenSource(_cleanupTimeout);
            try
            {
                await _references.RevokeAllAsync(cleanup.Token).AsTask()
                    .WaitAsync(_cleanupTimeout).ConfigureAwait(false);
            }
            catch (Exception exception) { failure = exception; }
            if (previous is not null)
            {
                try
                {
                    await previous.DisposeAsync().AsTask()
                        .WaitAsync(_cleanupTimeout).ConfigureAwait(false);
                }
                catch (Exception exception) { failure ??= exception; }
                finally { _runtimeAuthority.RevokeGeneration(previous.Id); }
            }
            if (failure is not null) throw failure;
        }
        finally
        {
            _resetGate.Release();
            _runtimeAuthority.Close();
            _resetGate.Dispose();
        }
    }

    private async ValueTask ConfirmOverwriteIfRequiredAsync(
        Generation generation,
        PhotonCadWorkspaceRegistration workspace,
        PhotonCadOverwritePurpose purpose,
        PhotonCadProjectDocument? source,
        CancellationToken cancellationToken)
    {
        var evidence = await generation.Composition.StorageBackend
            .InspectExactAsync(workspace.Binding.Target, cancellationToken).ConfigureAwait(false);
        EnsureCurrent(generation);
        if (!evidence.Exists) return;
        var confirmed = await _overwrite.ConfirmOverwriteAsync(
            new PhotonCadWindowsOverwriteContext(workspace, purpose, source),
            cancellationToken).ConfigureAwait(false);
        EnsureCurrent(generation);
        if (!confirmed) throw Failure("overwrite_confirmation_declined", nameof(workspace));
        _ = await generation.Composition.Coordinator.ConfirmOverwriteAsync(
            workspace.WorkspaceHandle,
            purpose,
            source?.ProjectHandle,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<T> UseAsync<T>(
        Func<Generation, CancellationToken, ValueTask<T>> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        var generation = CurrentGeneration();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, generation.Cancellation.Token);
        await generation.OperationGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            EnsureCurrent(generation);
            if (!generation.Composition.Available) throw Failure("project_host_unavailable", nameof(generation));
            await generation.EnsureRecoveredAsync(linked.Token).ConfigureAwait(false);
            EnsureCurrent(generation);
            return await action(generation, linked.Token).ConfigureAwait(false);
        }
        finally { generation.OperationGate.Release(); }
    }

    private Generation CurrentGeneration()
    {
        ThrowIfDisposed();
        lock (_sync) return _generation ?? throw new ObjectDisposedException(nameof(PhotonCadWindowsDesktopProjectHost));
    }

    private void EnsureCurrent(Generation generation)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_generation, generation)) throw Failure("project_host_reset", nameof(generation));
        }
    }

    private Generation NewGeneration() => new(_factory.Create());

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposeStarted) != 0)
            throw new ObjectDisposedException(nameof(PhotonCadWindowsDesktopProjectHost));
    }

    private static TimeSpan RequireCleanupTimeout(TimeSpan value)
    {
        if (value <= TimeSpan.Zero || value > MaximumCleanupTimeout)
            throw new ArgumentOutOfRangeException(nameof(value));
        return value;
    }

    private static string SafeRequestId(string? value)
    {
        if (value is null || value.Length is < 1 or > PhotonCadProjectContract.MaximumIdentifierLength
            || !char.IsAsciiLetterOrDigit(value[0])
            || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-' and not '.' and not ':'))
            throw Failure("invalid_request_id", nameof(value));
        return value;
    }

    private static void RequireCloseBinding(PhotonCadProjectDocument document, PhotonCadProjectCloseRequest request)
    {
        if (!StringComparer.Ordinal.Equals(document.Snapshot.SessionId, request.SessionId)
            || !StringComparer.Ordinal.Equals(document.Snapshot.ProjectId, request.ProjectId)
            || document.Snapshot.Revision != request.Revision
            || document.LastSavedRevision != request.LastSavedRevision
            || !FixedDigestEquals(document.ContentDigest, request.ContentDigest)
            || !FixedDigestEquals(document.LastSavedContentDigest, request.LastSavedContentDigest))
            throw Failure("close_binding_mismatch", nameof(request));
    }

    private static void RequireExactRuntimeStorageBinding(
        RuntimeProjectBinding hosted,
        PhotonCadAtomicReadEvidence persisted)
    {
        if (!persisted.Version.Target.Equals(hosted.Attachment.Target))
            throw Failure("runtime_storage_target_mismatch", nameof(persisted));
        var expected = hosted.Document.Snapshot.CanonicalBytes;
        var actual = persisted.Bytes;
        if (expected.Length != actual.Length
            || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expected.Span, actual.Span))
            throw Failure("runtime_storage_content_mismatch", nameof(persisted));
        var storageDigest = $"sha256:{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(actual.Span))}";
        if (!FixedDigestEquals(storageDigest, persisted.Version.StorageDigest))
            throw Failure("runtime_storage_digest_mismatch", nameof(persisted));
    }

    private static bool FixedDigestEquals(string left, string right)
    {
        var leftBytes = System.Text.Encoding.ASCII.GetBytes(left);
        var rightBytes = System.Text.Encoding.ASCII.GetBytes(right);
        return leftBytes.Length == rightBytes.Length
            && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static PhotonCadProjectException Failure(string code, string field) => new(code, field);

    private sealed class Generation : IAsyncDisposable
    {
        private bool _recovered;
        private int _disposed;

        internal Generation(PhotonCadWindowsDesktopProjectComposition composition) =>
            Composition = composition ?? throw new ArgumentNullException(nameof(composition));

        internal string Id { get; } = $"generation:{Guid.NewGuid():N}";
        internal PhotonCadWindowsDesktopProjectComposition Composition { get; }
        internal CancellationTokenSource Cancellation { get; } = new();
        internal SemaphoreSlim OperationGate { get; } = new(1, 1);
        internal Dictionary<string, SelectionState> Selections { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, PhotonCadProjectDocument> Documents { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, string> WorkspaceProjects { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, RuntimeProjectAttachment> RuntimeAttachments { get; } = new(StringComparer.Ordinal);

        internal async ValueTask EnsureRecoveredAsync(CancellationToken cancellationToken)
        {
            if (_recovered) return;
            _ = await Composition.Store.RecoverAsync(cancellationToken).ConfigureAwait(false);
            _recovered = true;
        }

        internal void Track(PhotonCadProjectDocument document)
        {
            Documents[document.ProjectHandle.Value] = document;
            WorkspaceProjects[document.WorkspaceHandle.Value] = document.ProjectHandle.Value;
        }

        internal void Untrack(PhotonCadProjectDocument document)
        {
            Documents.Remove(document.ProjectHandle.Value);
            RuntimeAttachments.Remove(document.ProjectHandle.Value);
            if (WorkspaceProjects.TryGetValue(document.WorkspaceHandle.Value, out var handle)
                && StringComparer.Ordinal.Equals(handle, document.ProjectHandle.Value))
                WorkspaceProjects.Remove(document.WorkspaceHandle.Value);
        }

        internal PhotonCadProjectDocument GetDocument(PhotonCadProjectHandle handle) =>
            Documents.TryGetValue(handle.Value, out var document)
                ? document
                : throw Failure("project_handle_unknown", nameof(handle));

        internal void EstablishRuntimeAttachment(
            PhotonCadProjectDocument document,
            PhotonCadStorageTargetHandle target)
        {
            if (document.Snapshot.Units != PhotonCadProjectUnit.Millimeter
                || document.Snapshot.Dirty
                || document.Snapshot.Revision != 0
                || document.LastSavedRevision != 0
                || !FixedDigestEquals(document.Snapshot.ContentDigest, document.LastSavedContentDigest))
                return;
            RuntimeAttachments[document.ProjectHandle.Value] = new RuntimeProjectAttachment(
                Id,
                document.ProjectHandle,
                document.Snapshot.SessionId,
                document.Snapshot.ProjectId,
                document.Snapshot.Revision,
                document.Snapshot.ContentDigest,
                target);
        }

        internal void AdvanceRuntimeAttachment(
            RuntimeProjectAttachment expected,
            PhotonCadProjectDocument committed)
        {
            if (!RuntimeAttachments.TryGetValue(expected.ProjectHandle.Value, out var current)
                || !ReferenceEquals(current, expected)
                || !StringComparer.Ordinal.Equals(expected.GenerationId, Id)
                || !committed.ProjectHandle.Equals(expected.ProjectHandle)
                || !StringComparer.Ordinal.Equals(committed.Snapshot.SessionId, expected.SessionId)
                || !StringComparer.Ordinal.Equals(committed.Snapshot.ProjectId, expected.ProjectId)
                || committed.Snapshot.Revision <= expected.Revision
                || committed.Snapshot.Dirty
                || committed.LastSavedRevision != committed.Snapshot.Revision
                || !FixedDigestEquals(committed.Snapshot.ContentDigest, committed.LastSavedContentDigest))
                throw Failure("runtime_attachment_advance_mismatch", nameof(committed));
            RuntimeAttachments[expected.ProjectHandle.Value] = expected with
            {
                Revision = committed.Snapshot.Revision,
                ContentDigest = committed.Snapshot.ContentDigest,
            };
        }

        internal void RevokeRuntimeAttachment(PhotonCadProjectHandle handle) =>
            RuntimeAttachments.Remove(handle.Value);

        internal void RevokeRuntimeAttachmentsForProject(string projectId)
        {
            foreach (var key in RuntimeAttachments
                .Where(pair => StringComparer.Ordinal.Equals(pair.Value.ProjectId, projectId))
                .Select(pair => pair.Key)
                .ToArray())
                RuntimeAttachments.Remove(key);
        }

        internal RuntimeProjectBinding ResolveRuntimeProject(PhotonCadRuntimeSyncRequest request)
        {
            var matches = Documents.Values.Where(document =>
                    StringComparer.Ordinal.Equals(document.Snapshot.SessionId, request.SessionId)
                    && StringComparer.Ordinal.Equals(document.Snapshot.ProjectId, request.ProjectId))
                .Take(2)
                .ToArray();
            if (matches.Length == 0) throw Failure("runtime_project_binding_unknown", nameof(request));
            if (matches.Length != 1) throw Failure("runtime_project_binding_ambiguous", nameof(request));
            var document = matches[0];
            if (document.Snapshot.Revision != request.BaseRevision)
                throw Failure("runtime_project_revision_mismatch", nameof(request));
            if (!RuntimeAttachments.TryGetValue(document.ProjectHandle.Value, out var attachment))
                throw Failure("runtime_project_attachment_unavailable", nameof(request));
            if (!StringComparer.Ordinal.Equals(attachment.GenerationId, Id)
                || !attachment.ProjectHandle.Equals(document.ProjectHandle)
                || !StringComparer.Ordinal.Equals(attachment.SessionId, request.SessionId)
                || !StringComparer.Ordinal.Equals(attachment.ProjectId, request.ProjectId)
                || attachment.Revision != request.BaseRevision
                || !FixedDigestEquals(attachment.ContentDigest, document.Snapshot.ContentDigest))
                throw Failure("runtime_project_attachment_mismatch", nameof(request));
            return new RuntimeProjectBinding(document, attachment);
        }

        internal SelectionState ConsumeSelection(PhotonCadWorkspaceHandle handle, string purpose, DateTimeOffset now)
        {
            PruneSelections(now);
            if (!Selections.Remove(handle.Value, out var selection))
                throw Failure("workspace_selection_unknown_or_used", nameof(handle));
            if (!StringComparer.Ordinal.Equals(selection.Purpose, purpose))
                throw Failure("workspace_selection_purpose_mismatch", nameof(purpose));
            if (now - selection.SelectedAtUtc >= SelectionTimeToLive)
                throw Failure("workspace_selection_expired", nameof(handle));
            return selection;
        }

        internal void PruneSelections(DateTimeOffset now)
        {
            foreach (var key in Selections
                .Where(pair => now - pair.Value.SelectedAtUtc >= SelectionTimeToLive)
                .Select(pair => pair.Key)
                .ToArray())
                Selections.Remove(key);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Cancellation.Cancel();
            await OperationGate.WaitAsync().ConfigureAwait(false);
            try { await Composition.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                Selections.Clear();
                Documents.Clear();
                WorkspaceProjects.Clear();
                RuntimeAttachments.Clear();
                Cancellation.Dispose();
                OperationGate.Release();
                OperationGate.Dispose();
            }
        }
    }

    private sealed record SelectionState(
        PhotonCadWorkspaceRegistration Workspace,
        string Purpose,
        DateTimeOffset SelectedAtUtc);

    private sealed record RuntimeProjectBinding(
        PhotonCadProjectDocument Document,
        RuntimeProjectAttachment Attachment);

    private sealed record RuntimeProjectAttachment(
        string GenerationId,
        PhotonCadProjectHandle ProjectHandle,
        string SessionId,
        string ProjectId,
        long Revision,
        string ContentDigest,
        PhotonCadStorageTargetHandle Target);

    private sealed class ProductionCompositionFactory : IPhotonCadWindowsDesktopProjectCompositionFactory
    {
        private readonly IPhotonCadProjectCodec _codec;
        private readonly IPhotonCadWindowsFileDialog? _dialog;
        private readonly IPhotonCadHandleIssuer? _handles;
        private readonly IPhotonCadClock? _clock;

        internal ProductionCompositionFactory(
            IPhotonCadProjectCodec codec,
            IPhotonCadWindowsFileDialog? dialog,
            IPhotonCadHandleIssuer? handles,
            IPhotonCadClock? clock)
        {
            _codec = codec ?? throw new ArgumentNullException(nameof(codec));
            _dialog = dialog;
            _handles = handles;
            _clock = clock;
        }

        public PhotonCadWindowsDesktopProjectComposition Create()
        {
            var registry = new PhotonCadWindowsTargetRegistry();
            try
            {
                var backend = new PhotonCadWindowsAtomicStorageBackend(registry, _clock);
                var maximumEncodedBytes = _codec is IPhotonCadProjectCodecPolicy policy
                    ? policy.MaximumEncodedBytes
                    : PhotonCadProjectContract.MaximumCanonicalProjectBytes;
                var store = new PhotonCadAtomicProjectStore(backend, _clock, maximumEncodedBytes);
                var coordinator = new PhotonCadProjectCoordinator(store, _codec, _handles, _clock);
                var picker = new PhotonCadWindowsWorkspacePicker(registry, _dialog);
                return new PhotonCadWindowsDesktopProjectComposition(
                    coordinator,
                    store,
                    backend,
                    picker,
                    registry.Available,
                    registry);
            }
            catch
            {
                registry.Dispose();
                throw;
            }
        }
    }
}
