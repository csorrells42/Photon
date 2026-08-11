namespace PhotonCadProjects.Windows;

/// <summary>
/// Host-only optimistic revision binding. It contains logical project identity only: never a
/// native path, OS file identity, storage version, or overwrite grant.
/// </summary>
public sealed record PhotonCadWindowsProjectRevisionBinding
{
    internal PhotonCadWindowsProjectRevisionBinding(PhotonCadProjectDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        ProjectHandle = document.ProjectHandle;
        SessionId = document.Snapshot.SessionId;
        ProjectId = document.Snapshot.ProjectId;
        Revision = document.Snapshot.Revision;
        ContentDigest = document.Snapshot.ContentDigest;
    }

    public PhotonCadProjectHandle ProjectHandle { get; }
    public string SessionId { get; }
    public string ProjectId { get; }
    public long Revision { get; }
    public string ContentDigest { get; }
}

/// <summary>
/// Result of decoding a native project through the authoritative project codec. The document
/// includes canonical bytes, BOM, logical digest, and its actual persisted revision; no path or
/// OS identity is exposed.
/// </summary>
public sealed record PhotonCadWindowsHydratedProject
{
    internal PhotonCadWindowsHydratedProject(
        PhotonCadWorkspaceRegistration workspace,
        PhotonCadProjectDocument document)
    {
        Workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        Document = document ?? throw new ArgumentNullException(nameof(document));
        if (!workspace.WorkspaceHandle.Equals(document.WorkspaceHandle))
            throw new PhotonCadProjectException("hydration_workspace_mismatch", nameof(document));
        RevisionBinding = new PhotonCadWindowsProjectRevisionBinding(document);
    }

    public PhotonCadWorkspaceRegistration Workspace { get; }
    public PhotonCadProjectDocument Document { get; }
    public PhotonCadWindowsProjectRevisionBinding RevisionBinding { get; }
}

public sealed record PhotonCadWindowsSavedProject
{
    internal PhotonCadWindowsSavedProject(PhotonCadProjectSaveOutcome outcome)
    {
        Outcome = outcome ?? throw new ArgumentNullException(nameof(outcome));
        RevisionBinding = new PhotonCadWindowsProjectRevisionBinding(outcome.Document);
    }

    public PhotonCadProjectSaveOutcome Outcome { get; }
    public PhotonCadWindowsProjectRevisionBinding RevisionBinding { get; }
}

/// <summary>
/// Native Windows composition boundary. All filesystem access remains behind opaque targets and
/// the stable atomic store. Renderer dispatch must expose only explicitly mapped project DTOs,
/// never this provider, its host-path registration methods, or overwrite capabilities.
/// </summary>
public sealed class PhotonCadWindowsProjectProvider : IAsyncDisposable
{
    private readonly PhotonCadWindowsTargetRegistry _registry;
    private readonly PhotonCadAtomicProjectStore _store;
    private readonly PhotonCadProjectCoordinator _coordinator;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, HostedDocument> _current = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposeStarted;

    public PhotonCadWindowsProjectProvider(
        IPhotonCadProjectCodec codec,
        IPhotonCadWindowsFileDialog? dialog = null,
        IPhotonCadHandleIssuer? handles = null,
        IPhotonCadClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(codec);
        _registry = new PhotonCadWindowsTargetRegistry();
        var backend = new PhotonCadWindowsAtomicStorageBackend(_registry, clock);
        _store = new PhotonCadAtomicProjectStore(backend, clock);
        _coordinator = new PhotonCadProjectCoordinator(_store, codec, handles, clock);
        Picker = new PhotonCadWindowsWorkspacePicker(_registry, dialog);
        Mount = new PhotonCadWindowsWorkspaceMount(_registry);
    }

    public bool Available => Volatile.Read(ref _disposeStarted) == 0 && _registry.Available;
    public IPhotonCadNativeWorkspacePicker Picker { get; }
    public IPhotonCadNativeWorkspaceMount Mount { get; }

    /// <summary>Host/native UI only. Never accept a path from renderer content.</summary>
    public PhotonCadWorkspaceBinding RegisterHostPath(string exactPath, string? displayLabel = null)
    {
        ThrowIfDisposed();
        return _registry.RegisterExactPath(exactPath, displayLabel);
    }

    /// <summary>Host/native UI only. The selection token must never cross into web content.</summary>
    public string IssueHostMountToken(string exactPath, string? displayLabel = null)
    {
        ThrowIfDisposed();
        return _registry.IssueMountToken(exactPath, displayLabel);
    }

    public async ValueTask<PhotonCadWindowsHydratedProject> HydrateAsync(
        PhotonCadWorkspaceBinding binding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _ = await _store.RecoverAsync(cancellationToken).ConfigureAwait(false);
            var workspace = await _coordinator.RegisterWorkspaceAsync(binding, cancellationToken).ConfigureAwait(false);
            var document = await _coordinator.OpenProjectAsync(
                new PhotonCadProjectOpenRequest(NewRequestId("hydrate"), workspace.WorkspaceHandle),
                cancellationToken).ConfigureAwait(false);
            _current.Add(document.ProjectHandle.Value, new HostedDocument(workspace, document));
            return new PhotonCadWindowsHydratedProject(workspace, document);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Atomically accepts a host-produced runtime update only when its logical base revision and
    /// digest still match the current hydrated document. Canonical bytes cannot originate in the
    /// renderer; callers must decode/construct them through the trusted host codec/runtime seam.
    /// </summary>
    public async ValueTask<PhotonCadWindowsHydratedProject> ApplyRuntimeUpdateAsync(
        PhotonCadWindowsProjectRevisionBinding expected,
        PhotonCadCanonicalProject updated,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(updated);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var current = RequireCurrent(expected);
            var document = await _coordinator.ApplyCurrentProjectAsync(expected.ProjectHandle, updated, cancellationToken).ConfigureAwait(false);
            _current[document.ProjectHandle.Value] = current with { Document = document };
            return new PhotonCadWindowsHydratedProject(current.Workspace, document);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Persists exactly the bound current revision through MatchOpenedVersion.</summary>
    public async ValueTask<PhotonCadWindowsSavedProject> SaveCurrentAsync(
        PhotonCadWindowsProjectRevisionBinding expected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var current = RequireCurrent(expected);
            var outcome = await _coordinator.SaveProjectAsync(
                new PhotonCadProjectSaveRequest(
                    NewRequestId("save"),
                    current.Document.ProjectHandle,
                    current.Document.Snapshot.SessionId,
                    current.Document.Snapshot.ProjectId,
                    current.Document.Snapshot.Revision,
                    current.Document.Snapshot.ContentDigest),
                cancellationToken).ConfigureAwait(false);
            _current[current.Document.ProjectHandle.Value] = current with { Document = outcome.Document };
            return new PhotonCadWindowsSavedProject(outcome);
        }
        finally { _gate.Release(); }
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
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _current.Clear();
                await _coordinator.DisposeAsync().ConfigureAwait(false);
                _registry.Dispose();
            }
            finally
            {
                _gate.Release();
                _gate.Dispose();
            }
            _disposeCompletion.TrySetResult();
        }
        catch (Exception exception)
        {
            _disposeCompletion.TrySetException(exception);
            throw;
        }
    }

    private HostedDocument RequireCurrent(PhotonCadWindowsProjectRevisionBinding expected)
    {
        if (!_current.TryGetValue(expected.ProjectHandle.Value, out var current))
            throw new PhotonCadProjectException("project_not_hydrated", nameof(expected));
        if (!StringComparer.Ordinal.Equals(current.Document.Snapshot.SessionId, expected.SessionId)
            || !StringComparer.Ordinal.Equals(current.Document.Snapshot.ProjectId, expected.ProjectId)
            || current.Document.Snapshot.Revision != expected.Revision
            || !FixedDigestEquals(current.Document.Snapshot.ContentDigest, expected.ContentDigest))
            throw new PhotonCadProjectException("runtime_update_base_conflict", nameof(expected));
        return current;
    }

    private static bool FixedDigestEquals(string left, string right)
    {
        var leftBytes = System.Text.Encoding.ASCII.GetBytes(left);
        var rightBytes = System.Text.Encoding.ASCII.GetBytes(right);
        return leftBytes.Length == rightBytes.Length
            && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static string NewRequestId(string purpose) => $"windows-{purpose}-{Guid.NewGuid():N}";

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposeStarted) != 0)
            throw new ObjectDisposedException(nameof(PhotonCadWindowsProjectProvider));
    }

    private sealed record HostedDocument(PhotonCadWorkspaceRegistration Workspace, PhotonCadProjectDocument Document);
}
