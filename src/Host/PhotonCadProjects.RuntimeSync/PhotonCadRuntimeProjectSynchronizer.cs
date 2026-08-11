using System.Runtime.ExceptionServices;

namespace PhotonCadProjects.RuntimeSync;

/// <summary>
/// Serializes one provider mutation and one optimistic canonical commit per durable project
/// identity. Distinct projects may advance concurrently; a stale queued request is rejected by the
/// authority before the provider is called.
/// </summary>
public sealed class PhotonCadRuntimeProjectSynchronizer
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ProjectGate> _projects = new(StringComparer.Ordinal);
    private readonly IPhotonCadCanonicalMutationAuthority _authority;
    private readonly IPhotonCadSealedMutationProvider _provider;
    private readonly IPhotonCadSealedMutationCompensator _compensator;
    private readonly PhotonCadRuntimeCanonicalMapperV1 _mapper;
    private readonly PhotonCadRuntimeSyncPolicy _policy;

    public PhotonCadRuntimeProjectSynchronizer(
        IPhotonCadCanonicalMutationAuthority authority,
        IPhotonCadSealedMutationProvider provider,
        IPhotonCadSealedMutationCompensator compensator,
        PhotonCadRuntimeCanonicalMapperV1 mapper)
    {
        _authority = authority ?? throw RuntimeSyncGuards.Failure("required", nameof(authority));
        _provider = provider ?? throw RuntimeSyncGuards.Failure("required", nameof(provider));
        _compensator = compensator ?? throw RuntimeSyncGuards.Failure("required", nameof(compensator));
        _mapper = mapper ?? throw RuntimeSyncGuards.Failure("required", nameof(mapper));
        _policy = mapper.Policy;
    }

    public int ActiveProjectCount
    {
        get { lock (_gate) return _projects.Count; }
    }

    public async ValueTask<PhotonCadRuntimeSyncResult> SynchronizeAsync(
        PhotonCadRuntimeSyncRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var projectLease = await EnterProjectAsync(request, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var binding = await _authority.ResolveAsync(request, cancellationToken).ConfigureAwait(false);
        var providerRequest = _mapper.PrepareProviderRequest(binding, request);
        PhotonCadSealedMutationDelta? mutation = null;
        try
        {
            mutation = await _provider.ApplyAsync(providerRequest, cancellationToken).ConfigureAwait(false)
                ?? throw RuntimeSyncGuards.Failure("provider_returned_null", nameof(mutation));
            cancellationToken.ThrowIfCancellationRequested();
            var updated = _mapper.Apply(binding, request, mutation);
            cancellationToken.ThrowIfCancellationRequested();
            var saved = await _authority.CommitAsync(binding, updated, cancellationToken).ConfigureAwait(false);
            _mapper.ValidateSaved(updated, saved, mutation);
            return new PhotonCadRuntimeSyncResult(saved, mutation);
        }
        catch (Exception failure) when (mutation is not null)
        {
            await CompensateOrThrowAsync(mutation, failure).ConfigureAwait(false);
            ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
    }

    private async ValueTask<ProjectLease> EnterProjectAsync(
        PhotonCadRuntimeSyncRequest request,
        CancellationToken cancellationToken)
    {
        var key = $"{request.SessionId}\0{request.ProjectId}";
        ProjectGate project;
        lock (_gate)
        {
            if (!_projects.TryGetValue(key, out project!))
            {
                if (_projects.Count >= PhotonCadRuntimeSyncContract.MaximumActiveProjects)
                    throw RuntimeSyncGuards.Failure("active_project_capacity_reached", nameof(request));
                project = new ProjectGate();
                _projects.Add(key, project);
            }
            if (project.References >= PhotonCadRuntimeSyncContract.MaximumPendingMutationsPerProject)
                throw RuntimeSyncGuards.Failure("project_pending_capacity_reached", nameof(request));
            project.References++;
        }

        try
        {
            await project.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new ProjectLease(this, key, project);
        }
        catch
        {
            ReleaseReference(key, project, releaseSemaphore: false);
            throw;
        }
    }

    private async ValueTask CompensateOrThrowAsync(
        PhotonCadSealedMutationDelta mutation,
        Exception failure)
    {
        var reason = failure is OperationCanceledException ? "canonical_commit_canceled" : "canonical_commit_failed";
        try
        {
            using var timeout = new CancellationTokenSource(_policy.CompensationTimeout);
            var task = _compensator.CompensateAsync(mutation, reason, timeout.Token).AsTask();
            await task.WaitAsync(_policy.CompensationTimeout).ConfigureAwait(false);
        }
        catch (Exception compensationFailure)
        {
            throw new PhotonCadRuntimeSyncException(
                "compensation_failed",
                nameof(mutation),
                new AggregateException(failure, compensationFailure));
        }
    }

    private void ReleaseReference(string key, ProjectGate project, bool releaseSemaphore)
    {
        if (releaseSemaphore) project.Semaphore.Release();
        lock (_gate)
        {
            project.References--;
            if (project.References == 0 && _projects.TryGetValue(key, out var current) && ReferenceEquals(current, project))
                _projects.Remove(key);
        }
    }

    private sealed class ProjectGate
    {
        internal SemaphoreSlim Semaphore { get; } = new(1, 1);
        internal int References { get; set; }
    }

    private sealed class ProjectLease : IAsyncDisposable
    {
        private PhotonCadRuntimeProjectSynchronizer? _owner;
        private readonly string _key;
        private readonly ProjectGate _project;

        internal ProjectLease(PhotonCadRuntimeProjectSynchronizer owner, string key, ProjectGate project)
        {
            _owner = owner;
            _key = key;
            _project = project;
        }

        public ValueTask DisposeAsync()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.ReleaseReference(_key, _project, releaseSemaphore: true);
            return ValueTask.CompletedTask;
        }
    }
}
