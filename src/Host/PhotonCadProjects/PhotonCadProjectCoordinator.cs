namespace PhotonCadProjects;

public sealed class PhotonCadProjectCoordinator : IAsyncDisposable
{
    private readonly PhotonCadAtomicProjectStore _store;
    private readonly IPhotonCadProjectCodec _codec;
    private readonly IPhotonCadHandleIssuer _handles;
    private readonly IPhotonCadClock _clock;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly Dictionary<string, WorkspaceState> _workspaces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReopenState> _reopen = new(StringComparer.Ordinal);
    private readonly Queue<string> _reopenOrder = new();
    private readonly HashSet<string> _requestIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _requestOrder = new();
    private readonly Dictionary<string, PhotonCadOverwriteGrantHandle> _pendingOverwriteGrants = new(StringComparer.Ordinal);
    private readonly object _readSync = new();
    private readonly Dictionary<string, LatestRead> _latestReads = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _readGeneration;
    private int _disposeStarted;

    public PhotonCadProjectCoordinator(
        PhotonCadAtomicProjectStore store,
        IPhotonCadProjectCodec codec,
        IPhotonCadHandleIssuer? handles = null,
        IPhotonCadClock? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        if (codec is IPhotonCadProjectCodecPolicy policy)
        {
            if (policy.MaximumEncodedBytes is < 1 or > PhotonCadProjectContract.MaximumCanonicalProjectBytes)
                throw new ArgumentOutOfRangeException(nameof(codec), "The codec encoded-size policy is outside the project storage ceiling.");
            if (store.MaximumEncodedBytes != policy.MaximumEncodedBytes)
                throw Failure("codec_storage_size_policy_mismatch", nameof(store));
        }
        _handles = handles ?? new CryptographicPhotonCadHandleIssuer();
        _clock = clock ?? new SystemPhotonCadClock();
    }

    public async ValueTask<PhotonCadWorkspaceRegistration> RegisterWorkspaceAsync(
        PhotonCadWorkspaceBinding binding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var existing = _workspaces.Values.FirstOrDefault(workspace => workspace.Binding.Target.Equals(binding.Target));
            if (existing is not null) return existing.Registration;
            if (_workspaces.Count >= PhotonCadProjectContract.MaximumKnownWorkspaces) PruneUnusedWorkspaces();
            if (_workspaces.Count >= PhotonCadProjectContract.MaximumKnownWorkspaces)
                throw Failure("workspace_capacity_reached", nameof(binding));
            var handle = _handles.NewWorkspace();
            if (_workspaces.ContainsKey(handle.Value)) throw Failure("handle_collision", nameof(handle));
            var registration = new PhotonCadWorkspaceRegistration(handle, binding);
            _workspaces.Add(handle.Value, new WorkspaceState(registration));
            return registration;
        }
        finally { _mutationGate.Release(); }
    }

    /// <summary>
    /// Host-only seam called after the native picker has displayed the exact existing target and
    /// the human has explicitly confirmed replacement. Never expose this method or its result to
    /// renderer dispatch.
    /// </summary>
    public async ValueTask<PhotonCadOverwriteGrantHandle> ConfirmOverwriteAsync(
        PhotonCadWorkspaceHandle workspaceHandle,
        PhotonCadOverwritePurpose purpose,
        PhotonCadProjectHandle? sourceProjectHandle = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspaceHandle);
        _ = ProjectGuards.Enum(purpose, nameof(purpose));
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var workspace = GetWorkspace(workspaceHandle);
            PhotonCadOverwriteGrantContext context;
            if (purpose == PhotonCadOverwritePurpose.Create)
            {
                if (sourceProjectHandle is not null)
                    throw Failure("overwrite_create_source_rejected", nameof(sourceProjectHandle));
                context = PhotonCadOverwriteGrantContext.ForCreate();
            }
            else
            {
                var source = GetSession(sourceProjectHandle ?? throw Failure("overwrite_source_required", nameof(sourceProjectHandle)));
                if (source.Workspace.Binding.Target.Equals(workspace.Binding.Target))
                    throw Failure("save_as_same_target", nameof(workspaceHandle));
                context = SaveAsOverwriteContext(source);
            }
            var handle = await _store.IssueOverwriteGrantAsync(workspace.Binding.Target, context, cancellationToken).ConfigureAwait(false);
            var key = OverwriteGrantKey(workspaceHandle, purpose);
            if (_pendingOverwriteGrants.Remove(key, out var superseded)) _store.RevokeOverwriteGrant(superseded);
            _pendingOverwriteGrants.Add(key, handle);
            return handle;
        }
        finally { _mutationGate.Release(); }
    }

    public async ValueTask<PhotonCadProjectDocument> CreateProjectAsync(
        PhotonCadProjectCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ReserveRequest(request.RequestId);
            EnsureOpenCapacity();
            var workspace = GetWorkspace(request.WorkspaceHandle);
            if (_sessions.Values.Any(session => session.Workspace.Binding.Target.Equals(workspace.Binding.Target)))
                throw Failure("storage_target_already_open", nameof(request.WorkspaceHandle));
            var project = await _codec.CreateAsync(request.Title, request.Units, cancellationToken).ConfigureAwait(false)
                ?? throw Failure("codec_returned_null", nameof(_codec));
            ValidateCodecProject(project, requireClean: true);
            if (!StringComparer.Ordinal.Equals(project.DisplayName, request.Title) || project.Units != request.Units)
                throw Failure("create_binding_mismatch", nameof(project));
            EnsureDurableIdentityAvailable(project);
            var projectHandle = _handles.NewProject();
            EnsureProjectHandleAvailable(projectHandle);
            var context = PhotonCadOverwriteGrantContext.ForCreate();
            var evidence = await SaveNewOrConfirmedOverwriteAsync(
                workspace,
                PhotonCadOverwritePurpose.Create,
                context,
                project.CanonicalBytes,
                cancellationToken).ConfigureAwait(false);
            var session = NewSession(workspace, projectHandle, project, evidence.Version, evidence.CommittedAtUtc);
            _sessions.Add(projectHandle.Value, session);
            return Document(session);
        }
        finally { _mutationGate.Release(); }
    }

    public ValueTask<PhotonCadProjectDocument> OpenProjectAsync(
        PhotonCadProjectOpenRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return OpenLatestAsync(request.RequestId, request.WorkspaceHandle, null, cancellationToken);
    }

    public async ValueTask<PhotonCadProjectDocument> ReopenProjectAsync(
        PhotonCadProjectReopenRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ReopenState reopen;
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ReserveRequest(request.RequestId);
            reopen = GetReopen(request.ReopenHandle);
        }
        finally { _mutationGate.Release(); }
        return await OpenLatestCoreAsync(request.RequestId, reopen.Workspace.Registration.WorkspaceHandle, request.ReopenHandle, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PhotonCadProjectDocument> RefreshProjectAsync(
        PhotonCadProjectRefreshRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string readKey;
        PhotonCadStorageTargetHandle target;
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ReserveRequest(request.RequestId);
            var session = GetSession(request.ProjectHandle);
            EnsureIdentity(session, request.SessionId, request.ProjectId);
            if (session.Current.Revision < request.KnownRevision)
                throw Failure("known_revision_ahead", nameof(request.KnownRevision));
            target = session.Workspace.Binding.Target;
            readKey = target.Value;
        }
        finally { _mutationGate.Release(); }

        var read = BeginLatestRead(readKey, cancellationToken);
        try
        {
            var persisted = await _store.ReadVersionedAsync(
                target,
                read.Token).ConfigureAwait(false);
            var project = _codec.Decode(persisted.Bytes) ?? throw Failure("codec_returned_null", nameof(_codec));
            ValidateCodecProject(project, requireClean: true);
            await _mutationGate.WaitAsync(read.Token).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                EnsureCurrentRead(read);
                var session = GetSession(request.ProjectHandle);
                EnsureIdentity(session, request.SessionId, request.ProjectId);
                if (session.Current.Revision < request.KnownRevision)
                    throw Failure("known_revision_ahead", nameof(request.KnownRevision));
                if (PhotonCadAtomicProjectStore.SameStorageVersion(session.LastStorageVersion, persisted.Version))
                    return Document(session);
                if (session.Current.Dirty)
                    throw Failure("refresh_conflict_unsaved_changes", nameof(request));
                EnsureIdentity(session, project.SessionId, project.ProjectId);
                if (project.Revision < session.LastSavedRevision)
                    throw Failure("refresh_revision_regressed", nameof(project));
                if (project.Revision == session.LastSavedRevision
                    && (!ProjectGuards.FixedDigestEquals(project.ContentDigest, session.LastSavedContentDigest)
                        || !ProjectGuards.FixedDigestEquals(project.BomDigest, session.LastSavedProject.BomDigest)
                        || !StringComparer.Ordinal.Equals(project.DisplayName, session.LastSavedProject.DisplayName)
                        || project.Units != session.LastSavedProject.Units
                        || !BomEquivalent(project.Bom, session.LastSavedProject.Bom)))
                    throw Failure("refresh_non_advancing_change", nameof(project));
                session.Current = project;
                session.LastSavedProject = project;
                session.LastSavedContentDigest = project.ContentDigest;
                session.LastSavedRevision = project.Revision;
                session.LastStorageVersion = persisted.Version;
                return Document(session);
            }
            finally { _mutationGate.Release(); }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Failure("read_superseded", nameof(request));
        }
        finally { EndLatestRead(read); }
    }

    /// <summary>
    /// Host-internal update seam. A renderer request cannot provide canonical bytes.
    /// </summary>
    public async ValueTask<PhotonCadProjectDocument> ApplyCurrentProjectAsync(
        PhotonCadProjectHandle projectHandle,
        PhotonCadCanonicalProject updated,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectHandle);
        ArgumentNullException.ThrowIfNull(updated);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var session = GetSession(projectHandle);
            ValidateCodecProject(updated, requireClean: false);
            EnsureIdentity(session, updated.SessionId, updated.ProjectId);
            if (!updated.Dirty || updated.Revision <= session.Current.Revision)
                throw Failure("non_advancing_dirty_update", nameof(updated));
            session.Current = updated;
            return Document(session);
        }
        finally { _mutationGate.Release(); }
    }

    /// <summary>
    /// Host-only authoritative mutation seam. The mapped canonical project and opened storage
    /// version must come from trusted host composition; neither is accepted by renderer dispatch.
    /// Storage performs the sole post-write filesystem readback. After it returns, this method
    /// decodes a separate copy of those exact digest/length-bound bytes before publishing memory.
    /// </summary>
    public async ValueTask<PhotonCadProjectApplyAndSaveOutcome> ApplyAndSaveCurrentProjectAsync(
        string requestId,
        PhotonCadProjectHandle projectHandle,
        string sessionId,
        string projectId,
        long baseRevision,
        string baseContentDigest,
        PhotonCadStorageVersion openedStorageVersion,
        PhotonCadCanonicalProject mappedProject,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectHandle);
        ArgumentNullException.ThrowIfNull(openedStorageVersion);
        ArgumentNullException.ThrowIfNull(mappedProject);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ReserveRequest(requestId);
            var session = GetSession(projectHandle);
            EnsureSaveBinding(session, sessionId, projectId, baseRevision, baseContentDigest);
            if (session.Current.Dirty) throw Failure("mutation_base_not_clean", nameof(session));
            if (!session.Workspace.Binding.Target.Equals(openedStorageVersion.Target)
                || !PhotonCadAtomicProjectStore.SameStorageVersion(session.LastStorageVersion, openedStorageVersion))
                throw Failure("opened_storage_version_mismatch", nameof(openedStorageVersion));

            ValidateCodecProject(mappedProject, requireClean: false);
            EnsureIdentity(session, mappedProject.SessionId, mappedProject.ProjectId);
            if (!mappedProject.Dirty || mappedProject.Revision <= baseRevision)
                throw Failure("non_advancing_dirty_update", nameof(mappedProject));
            if (!StringComparer.Ordinal.Equals(mappedProject.DisplayName, session.Current.DisplayName)
                || mappedProject.Units != session.Current.Units)
                throw Failure("mutation_metadata_changed", nameof(mappedProject));

            var saved = MarkSavedBound(mappedProject);
            var committedBytes = saved.CanonicalBytes.ToArray();
            var receiptHandle = _handles.NewSaveReceipt();
            PhotonCadAtomicSaveEvidence evidence;
            try
            {
                evidence = await _store.SaveIfVersionAsync(
                    session.Workspace.Binding.Target,
                    committedBytes,
                    openedStorageVersion,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (PhotonCadCommittedRecoveryRequiredException exception)
            {
                return PhotonCadProjectApplyAndSaveOutcome.RecoveryRequired(exception.RecoveryHandle);
            }

            var receipt = MutationReceipt(
                session,
                baseRevision,
                baseContentDigest,
                saved,
                evidence,
                receiptHandle);
            PhotonCadCanonicalProject rebound;
            try
            {
                rebound = _codec.Decode(committedBytes.ToArray())
                    ?? throw Failure("codec_returned_null", nameof(_codec));
                ValidateCommittedMutationRebind(
                    session.Workspace.Binding.Target,
                    saved,
                    rebound,
                    committedBytes,
                    evidence);
            }
            catch
            {
                return PhotonCadProjectApplyAndSaveOutcome.ReadbackRequired(receipt);
            }

            var document = new PhotonCadProjectDocument(
                session.Workspace.Registration.WorkspaceHandle,
                session.ProjectHandle,
                rebound,
                rebound.ContentDigest,
                rebound.Revision,
                session.OpenedAtUtc);
            var outcome = PhotonCadProjectApplyAndSaveOutcome.Committed(receipt, document);
            session.Current = rebound;
            session.LastSavedProject = rebound;
            session.LastSavedContentDigest = rebound.ContentDigest;
            session.LastSavedRevision = rebound.Revision;
            session.LastStorageVersion = evidence.Version;
            return outcome;
        }
        finally { _mutationGate.Release(); }
    }

    public async ValueTask<PhotonCadProjectSaveOutcome> SaveProjectAsync(
        PhotonCadProjectSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ReserveRequest(request.RequestId);
            var session = GetSession(request.ProjectHandle);
            EnsureSaveBinding(session, request.SessionId, request.ProjectId, request.BaseRevision, request.ContentDigest);
            var saved = MarkSavedBound(session.Current);
            var receiptHandle = _handles.NewSaveReceipt();
            var evidence = await _store.SaveIfVersionAsync(
                session.Workspace.Binding.Target,
                saved.CanonicalBytes,
                session.LastStorageVersion,
                cancellationToken).ConfigureAwait(false);
            session.Current = saved;
            session.LastSavedProject = saved;
            session.LastSavedContentDigest = saved.ContentDigest;
            session.LastSavedRevision = saved.Revision;
            session.LastStorageVersion = evidence.Version;
            return SaveOutcome(session, request.ProjectHandle, request.BaseRevision, evidence, receiptHandle);
        }
        finally { _mutationGate.Release(); }
    }

    public async ValueTask<PhotonCadProjectSaveOutcome> SaveProjectAsAsync(
        PhotonCadProjectSaveAsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ReserveRequest(request.RequestId);
            var source = GetSession(request.SourceProjectHandle);
            EnsureSaveBinding(source, request.SessionId, request.ProjectId, request.BaseRevision, request.ContentDigest);
            var destination = GetWorkspace(request.DestinationWorkspaceHandle);
            if (destination.Binding.Target.Equals(source.Workspace.Binding.Target))
                throw Failure("save_as_same_target", nameof(request.DestinationWorkspaceHandle));
            if (_sessions.Values.Any(session => session.Workspace.Binding.Target.Equals(destination.Binding.Target)))
                throw Failure("destination_target_already_open", nameof(request.DestinationWorkspaceHandle));
            var saved = MarkSavedBound(source.Current);
            var destinationHandle = _handles.NewProject();
            EnsureProjectHandleAvailable(destinationHandle);
            var reopen = PrepareReopen(source);
            var receiptHandle = _handles.NewSaveReceipt();
            var overwriteContext = SaveAsOverwriteContext(source);
            var evidence = await SaveNewOrConfirmedOverwriteAsync(
                destination,
                PhotonCadOverwritePurpose.SaveAs,
                overwriteContext,
                saved.CanonicalBytes,
                cancellationToken).ConfigureAwait(false);
            var moved = NewSession(destination, destinationHandle, saved, evidence.Version, evidence.CommittedAtUtc);
            _sessions.Remove(source.ProjectHandle.Value);
            CommitReopen(reopen);
            _sessions.Add(destinationHandle.Value, moved);
            return SaveOutcome(moved, request.SourceProjectHandle, request.BaseRevision, evidence, receiptHandle);
        }
        finally { _mutationGate.Release(); }
    }

    public async ValueTask<PhotonCadProjectCloseOutcome> CloseProjectAsync(
        PhotonCadProjectCloseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ReserveRequest(request.RequestId);
            var session = GetSession(request.ProjectHandle);
            EnsureIdentity(session, request.SessionId, request.ProjectId);
            if (session.Current.Revision != request.Revision || session.LastSavedRevision != request.LastSavedRevision
                || !ProjectGuards.FixedDigestEquals(session.Current.ContentDigest, request.ContentDigest)
                || !ProjectGuards.FixedDigestEquals(session.LastSavedContentDigest, request.LastSavedContentDigest))
                throw Failure("close_binding_mismatch", nameof(request));
            if (session.Current.Dirty && !request.DiscardUnsavedChanges)
                throw Failure("dirty_close_confirmation_required", nameof(request.DiscardUnsavedChanges));
            if (!session.Current.Dirty && request.DiscardUnsavedChanges)
                throw Failure("discard_requires_dirty_project", nameof(request.DiscardUnsavedChanges));
            var reopen = PrepareReopen(session);
            _sessions.Remove(session.ProjectHandle.Value);
            CommitReopen(reopen);
            return new PhotonCadProjectCloseOutcome(request.ProjectHandle, reopen.Metadata);
        }
        finally { _mutationGate.Release(); }
    }

    public void CancelLatestRead()
    {
        lock (_readSync)
        {
            foreach (var read in _latestReads.Values) read.Source.Cancel();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposeStarted, 1, 0) != 0)
        {
            await _disposeCompletion.Task.ConfigureAwait(false);
            return;
        }
        try
        {
            lock (_readSync)
            {
                foreach (var read in _latestReads.Values)
                {
                    read.Source.Cancel();
                    read.Source.Dispose();
                }
                _latestReads.Clear();
            }
            await _mutationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                foreach (var grant in _pendingOverwriteGrants.Values) _store.RevokeOverwriteGrant(grant);
                _pendingOverwriteGrants.Clear();
                _sessions.Clear();
                _reopen.Clear();
                _reopenOrder.Clear();
                _workspaces.Clear();
                _requestIds.Clear();
                _requestOrder.Clear();
            }
            finally
            {
                _mutationGate.Release();
                _mutationGate.Dispose();
            }
            _disposeCompletion.TrySetResult();
        }
        catch (Exception exception)
        {
            _disposeCompletion.TrySetException(exception);
            throw;
        }
    }

    private async ValueTask<PhotonCadProjectDocument> OpenLatestAsync(string requestId, PhotonCadWorkspaceHandle workspaceHandle, PhotonCadReopenHandle? reopenHandle, CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ReserveRequest(requestId);
            _ = GetWorkspace(workspaceHandle);
        }
        finally { _mutationGate.Release(); }
        return await OpenLatestCoreAsync(requestId, workspaceHandle, reopenHandle, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<PhotonCadProjectDocument> OpenLatestCoreAsync(string requestId, PhotonCadWorkspaceHandle workspaceHandle, PhotonCadReopenHandle? reopenHandle, CancellationToken cancellationToken)
    {
        WorkspaceState workspace;
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            workspace = GetWorkspace(workspaceHandle);
            EnsureOpenCapacity();
        }
        finally { _mutationGate.Release(); }
        var read = BeginLatestRead(workspace.Binding.Target.Value, cancellationToken);
        try
        {
            var persisted = await _store.ReadVersionedAsync(workspace.Binding.Target, read.Token).ConfigureAwait(false);
            var project = _codec.Decode(persisted.Bytes) ?? throw Failure("codec_returned_null", nameof(_codec));
            ValidateCodecProject(project, requireClean: true);
            await _mutationGate.WaitAsync(read.Token).ConfigureAwait(false);
            try
            {
                EnsureCurrentRead(read);
                if (_sessions.Values.Any(session => session.Workspace.Binding.Target.Equals(workspace.Binding.Target)))
                    throw Failure("storage_target_already_open", nameof(workspaceHandle));
                EnsureDurableIdentityAvailable(project);
                if (reopenHandle is not null)
                {
                    var reopen = GetReopen(reopenHandle);
                    if (!reopen.Workspace.Registration.WorkspaceHandle.Equals(workspaceHandle)
                        || !StringComparer.Ordinal.Equals(reopen.Metadata.ProjectId, project.ProjectId)
                        || reopen.Metadata.LastSavedRevision != project.Revision
                        || !ProjectGuards.FixedDigestEquals(reopen.Metadata.ContentDigest, project.ContentDigest))
                        throw Failure("reopen_binding_mismatch", nameof(reopenHandle));
                    _reopen.Remove(reopenHandle.Value);
                }
                var projectHandle = _handles.NewProject();
                EnsureProjectHandleAvailable(projectHandle);
                var session = NewSession(workspace, projectHandle, project, persisted.Version, _clock.UtcNow);
                _sessions.Add(projectHandle.Value, session);
                return Document(session);
            }
            finally { _mutationGate.Release(); }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Failure("read_superseded", requestId);
        }
        finally { EndLatestRead(read); }
    }

    private PhotonCadCanonicalProject MarkSavedBound(PhotonCadCanonicalProject current)
    {
        var saved = _codec.MarkSaved(current) ?? throw Failure("codec_returned_null", nameof(_codec));
        ValidateCodecProject(saved, requireClean: true);
        if (!StringComparer.Ordinal.Equals(saved.SessionId, current.SessionId)
            || !StringComparer.Ordinal.Equals(saved.ProjectId, current.ProjectId)
            || saved.Revision != current.Revision
            || !ProjectGuards.FixedDigestEquals(saved.ContentDigest, current.ContentDigest)
            || !ProjectGuards.FixedDigestEquals(saved.BomDigest, current.BomDigest)
            || !BomEquivalent(saved.Bom, current.Bom)
            || !StringComparer.Ordinal.Equals(saved.DisplayName, current.DisplayName)
            || saved.Units != current.Units)
            throw Failure("mark_saved_binding_mismatch", nameof(saved));
        return saved;
    }

    private void ValidateCommittedMutationRebind(
        PhotonCadStorageTargetHandle expectedTarget,
        PhotonCadCanonicalProject expected,
        PhotonCadCanonicalProject rebound,
        ReadOnlyMemory<byte> committedBytes,
        PhotonCadAtomicSaveEvidence evidence)
    {
        ValidateCodecProject(rebound, requireClean: true);
        var expectedStorageDigest = ProjectGuards.Sha256(committedBytes.Span);
        if (!evidence.Target.Equals(expectedTarget)
            || !evidence.Target.Equals(evidence.Version.Target)
            || evidence.ByteLength != committedBytes.Length
            || evidence.Version.ByteLength != committedBytes.Length
            || !evidence.Atomic
            || !evidence.FlushedToDisk
            || !ProjectGuards.FixedDigestEquals(evidence.StorageDigest, expectedStorageDigest)
            || !ProjectGuards.FixedDigestEquals(evidence.Version.StorageDigest, expectedStorageDigest)
            || !StringComparer.Ordinal.Equals(rebound.SessionId, expected.SessionId)
            || !StringComparer.Ordinal.Equals(rebound.ProjectId, expected.ProjectId)
            || rebound.Revision != expected.Revision
            || !StringComparer.Ordinal.Equals(rebound.DisplayName, expected.DisplayName)
            || rebound.Units != expected.Units
            || !ProjectGuards.FixedDigestEquals(rebound.ContentDigest, expected.ContentDigest)
            || !ProjectGuards.FixedDigestEquals(rebound.BomDigest, expected.BomDigest)
            || !BomEquivalent(rebound.Bom, expected.Bom)
            || !rebound.CanonicalBytes.Span.SequenceEqual(committedBytes.Span))
            throw Failure("committed_mutation_rebind_mismatch", nameof(rebound));
    }

    private static bool BomEquivalent(IReadOnlyList<PhotonCadBomRow> left, IReadOnlyList<PhotonCadBomRow> right)
    {
        if (left.Count != right.Count) return false;
        for (var index = 0; index < left.Count; index++)
        {
            if (!StringComparer.Ordinal.Equals(left[index].PartNumber, right[index].PartNumber)
                || !StringComparer.Ordinal.Equals(left[index].Description, right[index].Description)
                || left[index].Quantity != right[index].Quantity
                || left[index].Unit != right[index].Unit
                || !StringComparer.Ordinal.Equals(left[index].SourceEntityId, right[index].SourceEntityId)) return false;
        }
        return true;
    }

    private void ValidateCodecProject(PhotonCadCanonicalProject project, bool requireClean)
    {
        if (requireClean && project.Dirty) throw Failure("persisted_project_marked_dirty", nameof(project));
        var computed = ProjectGuards.Digest(_codec.ComputeLogicalContentDigest(project.CanonicalBytes), nameof(project.ContentDigest));
        if (!ProjectGuards.FixedDigestEquals(computed, project.ContentDigest))
            throw Failure("logical_content_digest_mismatch", nameof(project));
    }

    private void EnsureDurableIdentityAvailable(PhotonCadCanonicalProject project)
    {
        if (_sessions.Values.Any(session =>
                StringComparer.Ordinal.Equals(session.Current.SessionId, project.SessionId)
                && StringComparer.Ordinal.Equals(session.Current.ProjectId, project.ProjectId)))
            throw Failure("duplicate_project_identity_open", nameof(project));
    }

    private static void EnsureIdentity(SessionState session, string sessionId, string projectId)
    {
        if (!StringComparer.Ordinal.Equals(session.Current.SessionId, sessionId)
            || !StringComparer.Ordinal.Equals(session.Current.ProjectId, projectId))
            throw Failure("project_identity_mismatch", nameof(session));
    }

    private static void EnsureSaveBinding(SessionState session, string sessionId, string projectId, long revision, string digest)
    {
        EnsureIdentity(session, sessionId, projectId);
        if (session.Current.Revision != revision || !ProjectGuards.FixedDigestEquals(session.Current.ContentDigest, digest))
            throw Failure("save_binding_mismatch", nameof(session));
    }

    private SessionState NewSession(
        WorkspaceState workspace,
        PhotonCadProjectHandle projectHandle,
        PhotonCadCanonicalProject project,
        PhotonCadStorageVersion storageVersion,
        DateTimeOffset openedAtUtc) =>
        new(
            workspace,
            projectHandle,
            project,
            project.ContentDigest,
            project.Revision,
            storageVersion,
            ProjectGuards.Utc(openedAtUtc, nameof(openedAtUtc)));

    private PhotonCadProjectSaveOutcome SaveOutcome(
        SessionState session,
        PhotonCadProjectHandle sourceHandle,
        long baseRevision,
        PhotonCadAtomicSaveEvidence evidence,
        PhotonCadSaveReceiptHandle receiptHandle)
    {
        var receipt = new PhotonCadProjectSaveReceipt(
            receiptHandle,
            sourceHandle,
            session.ProjectHandle,
            session.Current.SessionId,
            session.Current.ProjectId,
            baseRevision,
            baseRevision,
            session.Current.ContentDigest,
            evidence.CommittedAtUtc,
            atomic: evidence.Atomic,
            storageDigest: evidence.StorageDigest);
        return new PhotonCadProjectSaveOutcome(receipt, Document(session));
    }

    private static PhotonCadProjectMutationCommitReceipt MutationReceipt(
        SessionState session,
        long baseRevision,
        string baseContentDigest,
        PhotonCadCanonicalProject committed,
        PhotonCadAtomicSaveEvidence evidence,
        PhotonCadSaveReceiptHandle receiptHandle) => new(
            receiptHandle,
            session.ProjectHandle,
            committed.SessionId,
            committed.ProjectId,
            baseRevision,
            committed.Revision,
            baseContentDigest,
            committed.ContentDigest,
            committed.BomDigest,
            evidence.StorageDigest,
            evidence.ByteLength,
            evidence.CommittedAtUtc,
            evidence.Atomic,
            evidence.FlushedToDisk);

    private async ValueTask<PhotonCadAtomicSaveEvidence> SaveNewOrConfirmedOverwriteAsync(
        WorkspaceState workspace,
        PhotonCadOverwritePurpose purpose,
        PhotonCadOverwriteGrantContext context,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        var key = OverwriteGrantKey(workspace.Registration.WorkspaceHandle, purpose);
        if (_pendingOverwriteGrants.Remove(key, out var grant))
            return await _store.SaveWithOverwriteGrantAsync(
                workspace.Binding.Target,
                bytes,
                grant,
                context,
                cancellationToken).ConfigureAwait(false);
        return await _store.SaveAsync(workspace.Binding.Target, bytes, cancellationToken).ConfigureAwait(false);
    }

    private static PhotonCadOverwriteGrantContext SaveAsOverwriteContext(SessionState source) =>
        PhotonCadOverwriteGrantContext.ForSaveAs(
            source.ProjectHandle,
            source.Current.SessionId,
            source.Current.ProjectId,
            source.Current.Revision,
            source.Current.ContentDigest);

    private static string OverwriteGrantKey(PhotonCadWorkspaceHandle workspaceHandle, PhotonCadOverwritePurpose purpose) =>
        $"{workspaceHandle.Value}\0{purpose}";

    private static PhotonCadProjectDocument Document(SessionState session)
    {
        if (session.LastSavedRevision > session.Current.Revision)
            throw Failure("last_saved_revision_ahead", nameof(session));
        if (!session.Current.Dirty && (session.LastSavedRevision != session.Current.Revision
            || !ProjectGuards.FixedDigestEquals(session.LastSavedContentDigest, session.Current.ContentDigest)))
            throw Failure("clean_project_save_binding_mismatch", nameof(session));
        return new PhotonCadProjectDocument(
            session.Workspace.Registration.WorkspaceHandle,
            session.ProjectHandle,
            session.Current,
            session.LastSavedContentDigest,
            session.LastSavedRevision,
            session.OpenedAtUtc);
    }

    private ReopenState PrepareReopen(SessionState session)
    {
        var handle = _handles.NewReopen();
        if (_reopen.ContainsKey(handle.Value)) throw Failure("handle_collision", nameof(handle));
        var metadata = new PhotonCadProjectReopenMetadata(
            handle,
            session.LastSavedProject.DisplayName,
            session.LastSavedProject.ProjectId,
            session.LastSavedRevision,
            session.LastSavedContentDigest,
            ProjectGuards.Utc(_clock.UtcNow, nameof(_clock.UtcNow)));
        return new ReopenState(session.Workspace, metadata);
    }

    private void CommitReopen(ReopenState reopen)
    {
        var handle = reopen.Metadata.ReopenHandle;
        _reopen.Add(handle.Value, reopen);
        _reopenOrder.Enqueue(handle.Value);
        while (_reopen.Count > PhotonCadProjectContract.MaximumReopenEntries && _reopenOrder.TryDequeue(out var expired))
        {
            if (_reopen.Remove(expired, out var expiredReopen)) TryPruneWorkspace(expiredReopen.Workspace);
        }
    }

    private LatestRead BeginLatestRead(string key, CancellationToken cancellationToken)
    {
        lock (_readSync)
        {
            ThrowIfDisposed();
            if (_latestReads.Remove(key, out var existing))
            {
                existing.Source.Cancel();
                existing.Source.Dispose();
            }
            var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _readGeneration = checked(_readGeneration + 1);
            var read = new LatestRead(key, _readGeneration, source);
            _latestReads.Add(key, read);
            return read;
        }
    }

    private void EndLatestRead(LatestRead read)
    {
        lock (_readSync)
        {
            if (!_latestReads.TryGetValue(read.Key, out var current)
                || current.Generation != read.Generation
                || !ReferenceEquals(current.Source, read.Source)) return;
            _latestReads.Remove(read.Key);
            read.Source.Dispose();
        }
    }

    private void EnsureCurrentRead(LatestRead read)
    {
        lock (_readSync)
        {
            if (!_latestReads.TryGetValue(read.Key, out var current)
                || current.Generation != read.Generation
                || !ReferenceEquals(current.Source, read.Source)
                || current.Source.IsCancellationRequested)
                throw new OperationCanceledException();
        }
    }

    private void ReserveRequest(string requestId)
    {
        ProjectGuards.Identifier(requestId, nameof(requestId));
        if (!_requestIds.Add(requestId)) throw Failure("request_id_reused", nameof(requestId));
        _requestOrder.Enqueue(requestId);
        while (_requestIds.Count > PhotonCadProjectContract.MaximumRequestHistory && _requestOrder.TryDequeue(out var expired))
            _requestIds.Remove(expired);
    }

    private WorkspaceState GetWorkspace(PhotonCadWorkspaceHandle handle) => _workspaces.TryGetValue(handle.Value, out var value)
        ? value
        : throw Failure("workspace_handle_unknown", nameof(handle));

    private SessionState GetSession(PhotonCadProjectHandle handle) => _sessions.TryGetValue(handle.Value, out var value)
        ? value
        : throw Failure("project_handle_unknown", nameof(handle));

    private ReopenState GetReopen(PhotonCadReopenHandle handle) => _reopen.TryGetValue(handle.Value, out var value)
        ? value
        : throw Failure("reopen_handle_unknown", nameof(handle));

    private void EnsureOpenCapacity()
    {
        if (_sessions.Count >= PhotonCadProjectContract.MaximumOpenProjects)
            throw Failure("open_project_capacity_reached", nameof(_sessions));
    }

    private void EnsureProjectHandleAvailable(PhotonCadProjectHandle handle)
    {
        if (_sessions.ContainsKey(handle.Value)) throw Failure("handle_collision", nameof(handle));
    }

    private void PruneUnusedWorkspaces()
    {
        foreach (var workspace in _workspaces.Values.ToArray()) TryPruneWorkspace(workspace);
    }

    private void TryPruneWorkspace(WorkspaceState workspace)
    {
        var handle = workspace.Registration.WorkspaceHandle.Value;
        var inUse = _sessions.Values.Any(session => ReferenceEquals(session.Workspace, workspace))
            || _reopen.Values.Any(reopen => ReferenceEquals(reopen.Workspace, workspace))
            || _pendingOverwriteGrants.Keys.Any(key => key.StartsWith($"{handle}\0", StringComparison.Ordinal));
        if (!inUse) _workspaces.Remove(handle);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposeStarted) != 0) throw new ObjectDisposedException(nameof(PhotonCadProjectCoordinator));
    }

    private static PhotonCadProjectException Failure(string code, string field) => new(code, field);

    private sealed record WorkspaceState(PhotonCadWorkspaceRegistration Registration)
    {
        internal PhotonCadWorkspaceBinding Binding => Registration.Binding;
    }

    private sealed class SessionState(
        WorkspaceState workspace,
        PhotonCadProjectHandle projectHandle,
        PhotonCadCanonicalProject current,
        string lastSavedContentDigest,
        long lastSavedRevision,
        PhotonCadStorageVersion lastStorageVersion,
        DateTimeOffset openedAtUtc)
    {
        internal WorkspaceState Workspace { get; } = workspace;
        internal PhotonCadProjectHandle ProjectHandle { get; } = projectHandle;
        internal PhotonCadCanonicalProject Current { get; set; } = current;
        internal PhotonCadCanonicalProject LastSavedProject { get; set; } = current;
        internal string LastSavedContentDigest { get; set; } = ProjectGuards.Digest(lastSavedContentDigest, nameof(lastSavedContentDigest));
        internal long LastSavedRevision { get; set; } = ProjectGuards.Revision(lastSavedRevision, nameof(lastSavedRevision));
        internal PhotonCadStorageVersion LastStorageVersion { get; set; } = lastStorageVersion ?? throw new ArgumentNullException(nameof(lastStorageVersion));
        internal DateTimeOffset OpenedAtUtc { get; } = ProjectGuards.Utc(openedAtUtc, nameof(openedAtUtc));
    }

    private sealed record ReopenState(WorkspaceState Workspace, PhotonCadProjectReopenMetadata Metadata);
    private sealed record LatestRead(string Key, long Generation, CancellationTokenSource Source)
    {
        internal CancellationToken Token => Source.Token;
    }
}

public enum PhotonCadProjectApplyAndSaveStatus
{
    Committed,
    CommittedReadbackRequired,
    CommittedRecoveryRequired,
}

/// <summary>
/// Host-only receipt for an atomic canonical mutation. Base revision and committed revision are
/// intentionally distinct; provider operation suffix length, not storage, determines the advance.
/// </summary>
public sealed class PhotonCadProjectMutationCommitReceipt
{
    internal PhotonCadProjectMutationCommitReceipt(
        PhotonCadSaveReceiptHandle receiptHandle,
        PhotonCadProjectHandle projectHandle,
        string sessionId,
        string projectId,
        long baseRevision,
        long committedRevision,
        string baseContentDigest,
        string contentDigest,
        string bomDigest,
        string storageDigest,
        long byteLength,
        DateTimeOffset committedAtUtc,
        bool atomic,
        bool flushedToDisk)
    {
        ReceiptHandle = receiptHandle ?? throw new PhotonCadProjectException("required", nameof(receiptHandle));
        ProjectHandle = projectHandle ?? throw new PhotonCadProjectException("required", nameof(projectHandle));
        SessionId = ProjectGuards.Identifier(sessionId, nameof(sessionId));
        ProjectId = ProjectGuards.Identifier(projectId, nameof(projectId));
        BaseRevision = ProjectGuards.Revision(baseRevision, nameof(baseRevision));
        CommittedRevision = ProjectGuards.Revision(committedRevision, nameof(committedRevision));
        if (CommittedRevision <= BaseRevision)
            throw new PhotonCadProjectException("committed_revision_not_advancing", nameof(committedRevision));
        BaseContentDigest = ProjectGuards.Digest(baseContentDigest, nameof(baseContentDigest));
        ContentDigest = ProjectGuards.Digest(contentDigest, nameof(contentDigest));
        BomDigest = ProjectGuards.Digest(bomDigest, nameof(bomDigest));
        StorageDigest = ProjectGuards.Digest(storageDigest, nameof(storageDigest));
        if (byteLength is <= 0 or > PhotonCadProjectContract.MaximumCanonicalProjectBytes)
            throw new PhotonCadProjectException("invalid_content_length", nameof(byteLength));
        ByteLength = byteLength;
        CommittedAtUtc = ProjectGuards.Utc(committedAtUtc, nameof(committedAtUtc));
        if (!atomic || !flushedToDisk)
            throw new PhotonCadProjectException("non_durable_mutation_receipt", nameof(atomic));
        Atomic = true;
        FlushedToDisk = true;
    }

    public PhotonCadSaveReceiptHandle ReceiptHandle { get; }
    public PhotonCadProjectHandle ProjectHandle { get; }
    public string SessionId { get; }
    public string ProjectId { get; }
    public long BaseRevision { get; }
    public long CommittedRevision { get; }
    public string BaseContentDigest { get; }
    public string ContentDigest { get; }
    public string BomDigest { get; }
    public string StorageDigest { get; }
    public long ByteLength { get; }
    public DateTimeOffset CommittedAtUtc { get; }
    public bool Atomic { get; }
    public bool FlushedToDisk { get; }
}

/// <summary>
/// Discriminated host-only result. Once storage crossed its commit point this method never reports
/// an ordinary failure: it returns either a verified receipt/document, a committed receipt that
/// requires host readback reconciliation, or the opaque recovery capability retained by storage.
/// </summary>
public sealed class PhotonCadProjectApplyAndSaveOutcome
{
    private PhotonCadProjectApplyAndSaveOutcome(
        PhotonCadProjectApplyAndSaveStatus status,
        string reason,
        PhotonCadProjectMutationCommitReceipt? receipt,
        PhotonCadProjectDocument? document,
        PhotonCadRecoveryHandle? recoveryHandle)
    {
        Status = ProjectGuards.Enum(status, nameof(status));
        Reason = ProjectGuards.Identifier(reason, nameof(reason));
        Receipt = receipt;
        Document = document;
        RecoveryHandle = recoveryHandle;
        var valid = status switch
        {
            PhotonCadProjectApplyAndSaveStatus.Committed => receipt is not null && document is not null && recoveryHandle is null,
            PhotonCadProjectApplyAndSaveStatus.CommittedReadbackRequired => receipt is not null && document is null && recoveryHandle is null,
            PhotonCadProjectApplyAndSaveStatus.CommittedRecoveryRequired => receipt is null && document is null && recoveryHandle is not null,
            _ => false,
        };
        if (!valid) throw new PhotonCadProjectException("invalid_apply_save_outcome", nameof(status));
        if (document is not null && receipt is not null
            && (!document.ProjectHandle.Equals(receipt.ProjectHandle)
                || !StringComparer.Ordinal.Equals(document.Snapshot.SessionId, receipt.SessionId)
                || !StringComparer.Ordinal.Equals(document.Snapshot.ProjectId, receipt.ProjectId)
                || document.Snapshot.Revision != receipt.CommittedRevision
                || !ProjectGuards.FixedDigestEquals(document.ContentDigest, receipt.ContentDigest)))
            throw new PhotonCadProjectException("apply_save_document_binding_mismatch", nameof(document));
    }

    public PhotonCadProjectApplyAndSaveStatus Status { get; }
    public string Reason { get; }
    public PhotonCadProjectMutationCommitReceipt? Receipt { get; }
    public PhotonCadProjectDocument? Document { get; }
    /// <summary>Host-only capability. Never serialize it to renderer frames.</summary>
    public PhotonCadRecoveryHandle? RecoveryHandle { get; }

    internal static PhotonCadProjectApplyAndSaveOutcome Committed(
        PhotonCadProjectMutationCommitReceipt receipt,
        PhotonCadProjectDocument document) => new(
            PhotonCadProjectApplyAndSaveStatus.Committed,
            "committed",
            receipt,
            document,
            null);

    internal static PhotonCadProjectApplyAndSaveOutcome ReadbackRequired(
        PhotonCadProjectMutationCommitReceipt receipt) => new(
            PhotonCadProjectApplyAndSaveStatus.CommittedReadbackRequired,
            "committed_readback_required",
            receipt,
            null,
            null);

    internal static PhotonCadProjectApplyAndSaveOutcome RecoveryRequired(
        PhotonCadRecoveryHandle recoveryHandle) => new(
            PhotonCadProjectApplyAndSaveStatus.CommittedRecoveryRequired,
            "committed_recovery_required",
            null,
            null,
            recoveryHandle);
}

public enum PhotonCadNativeServiceStatus
{
    Selected,
    Cancelled,
    Rejected,
    Unavailable,
}

public sealed record PhotonCadNativeWorkspaceSelection(
    PhotonCadNativeServiceStatus Status,
    string Reason,
    PhotonCadWorkspaceBinding? Binding);

public interface IPhotonCadNativeWorkspacePicker
{
    ValueTask<PhotonCadNativeWorkspaceSelection> ChooseAsync(
        string purpose,
        CancellationToken cancellationToken = default);
}

public interface IPhotonCadNativeWorkspaceMount
{
    ValueTask<PhotonCadNativeWorkspaceSelection> MountAsync(
        string hostOwnedSelectionToken,
        CancellationToken cancellationToken = default);
}

public interface IPhotonCadProjectDispatcher
{
    bool Available { get; }
    ValueTask<object> DispatchAsync(object hostFrame, CancellationToken cancellationToken = default);
}

public sealed class UnavailablePhotonCadNativeWorkspacePicker : IPhotonCadNativeWorkspacePicker
{
    public ValueTask<PhotonCadNativeWorkspaceSelection> ChooseAsync(string purpose, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = ProjectGuards.Identifier(purpose, nameof(purpose));
        return ValueTask.FromResult(new PhotonCadNativeWorkspaceSelection(PhotonCadNativeServiceStatus.Unavailable, "native-picker-unavailable", null));
    }
}

public sealed class UnavailablePhotonCadNativeWorkspaceMount : IPhotonCadNativeWorkspaceMount
{
    public ValueTask<PhotonCadNativeWorkspaceSelection> MountAsync(string hostOwnedSelectionToken, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = ProjectGuards.Identifier(hostOwnedSelectionToken, nameof(hostOwnedSelectionToken));
        return ValueTask.FromResult(new PhotonCadNativeWorkspaceSelection(PhotonCadNativeServiceStatus.Unavailable, "native-mount-unavailable", null));
    }
}

public sealed class UnavailablePhotonCadProjectDispatcher : IPhotonCadProjectDispatcher
{
    public bool Available => false;

    public ValueTask<object> DispatchAsync(object hostFrame, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(hostFrame);
        throw new PhotonCadProjectException("dispatcher_unavailable", nameof(hostFrame));
    }
}
