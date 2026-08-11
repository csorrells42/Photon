using PhotonCadProjects.Codec;
using PhotonCadProjects.Windows;

namespace PhotonCadProjects.DesktopAdapter.Smoke;

internal sealed class FakePhotonCadDesktopProjectHost : IPhotonCadDesktopProjectHost
{
    private readonly PhotonCadCanonicalProjectCodecV1 _codec;
    private readonly Dictionary<string, PhotonCadWorkspaceRegistration> _workspaces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PhotonCadProjectDocument> _documents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ClosedState> _closed = new(StringComparer.Ordinal);
    private int _sequence;
    private bool _disposed;

    internal FakePhotonCadDesktopProjectHost(PhotonCadCanonicalProjectCodecV1 codec) => _codec = codec;

    internal bool Available { get; set; } = true;
    internal int ResetCount { get; private set; }
    internal TaskCompletionSource? BlockOpen { get; set; }
    internal TaskCompletionSource? BlockSave { get; set; }
    internal int CloseCalls { get; private set; }
    internal string? LastSuggestedName { get; private set; }

    public PhotonCadWindowsDesktopProjectReadiness Readiness => new(
        Available && !_disposed,
        Available && !_disposed ? "project-host-ready" : "project-host-unavailable");

    public ValueTask<PhotonCadDesktopProjectPickerOutcome> ChooseWorkspaceAsync(
        string requestId,
        string purpose,
        CancellationToken cancellationToken = default)
        => ChooseWorkspaceAsync(requestId, purpose, null, cancellationToken);

    public ValueTask<PhotonCadDesktopProjectPickerOutcome> ChooseWorkspaceAsync(
        string requestId,
        string purpose,
        string? suggestedName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        LastSuggestedName = suggestedName;
        var index = Interlocked.Increment(ref _sequence);
        var workspace = new PhotonCadWorkspaceRegistration(
            new PhotonCadWorkspaceHandle(Handle("cad-workspace:", $"workspace{index}")),
            new PhotonCadWorkspaceBinding(
                new PhotonCadStorageTargetHandle(Handle("cad-storage-target:", $"target{index}")),
                $"Project {index}.photoncad"));
        _workspaces.Add(workspace.WorkspaceHandle.Value, workspace);
        return ValueTask.FromResult(new PhotonCadDesktopProjectPickerOutcome(
            PhotonCadNativeServiceStatus.Selected,
            "native-workspace-selected",
            workspace));
    }

    public async ValueTask<PhotonCadProjectDocument> CreateProjectAsync(
        PhotonCadProjectCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var workspace = Workspace(request.WorkspaceHandle);
        var canonical = await _codec.CreateAsync(request.Title, request.Units, cancellationToken).ConfigureAwait(false);
        var document = Document(workspace, canonical);
        _documents.Add(document.ProjectHandle.Value, document);
        return document;
    }

    public async ValueTask<PhotonCadProjectDocument> OpenProjectAsync(
        PhotonCadProjectOpenRequest request,
        CancellationToken cancellationToken = default)
    {
        var blocker = BlockOpen;
        BlockOpen = null;
        if (blocker is not null) await blocker.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        var workspace = Workspace(request.WorkspaceHandle);
        var existing = _documents.Values.FirstOrDefault(value => value.WorkspaceHandle.Equals(workspace.WorkspaceHandle));
        if (existing is not null) return existing;
        var canonical = await _codec.CreateAsync("Opened project", PhotonCadProjectUnit.Millimeter, cancellationToken).ConfigureAwait(false);
        var document = Document(workspace, canonical);
        _documents.Add(document.ProjectHandle.Value, document);
        return document;
    }

    public ValueTask<PhotonCadProjectDocument> ReopenProjectAsync(
        PhotonCadProjectReopenRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_closed.Remove(request.ReopenHandle.Value, out var closed))
            throw new PhotonCadProjectException("reopen_handle_unknown", nameof(request));
        var document = Document(closed.Workspace, closed.Snapshot);
        _documents.Add(document.ProjectHandle.Value, document);
        return ValueTask.FromResult(document);
    }

    public ValueTask<PhotonCadProjectDocument> RefreshProjectAsync(
        PhotonCadProjectRefreshRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Project(request.ProjectHandle));
    }

    public async ValueTask<PhotonCadProjectSaveOutcome> SaveProjectAsync(
        PhotonCadProjectSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        if (BlockSave is not null) await BlockSave.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        var current = Project(request.ProjectHandle);
        var saved = _codec.MarkSaved(current.Snapshot);
        var document = new PhotonCadProjectDocument(
            current.WorkspaceHandle,
            current.ProjectHandle,
            saved,
            saved.ContentDigest,
            saved.Revision,
            current.OpenedAtUtc);
        _documents[document.ProjectHandle.Value] = document;
        return Outcome(request.ProjectHandle, request.BaseRevision, document);
    }

    public ValueTask<PhotonCadProjectSaveOutcome> SaveProjectAsAsync(
        PhotonCadProjectSaveAsRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = Project(request.SourceProjectHandle);
        var workspace = Workspace(request.DestinationWorkspaceHandle);
        var saved = _codec.MarkSaved(source.Snapshot);
        var document = Document(workspace, saved);
        _documents.Remove(source.ProjectHandle.Value);
        _documents.Add(document.ProjectHandle.Value, document);
        return ValueTask.FromResult(Outcome(source.ProjectHandle, request.BaseRevision, document));
    }

    public ValueTask<PhotonCadProjectCloseOutcome> CloseProjectAsync(
        PhotonCadProjectCloseRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CloseCalls++;
        var document = Project(request.ProjectHandle);
        if (document.Snapshot.Dirty && !request.DiscardUnsavedChanges)
            throw new PhotonCadProjectException("dirty_close_confirmation_required", nameof(request));
        var reopen = new PhotonCadProjectReopenMetadata(
            new PhotonCadReopenHandle(Handle("cad-reopen:", $"reopen{Interlocked.Increment(ref _sequence)}")),
            document.DisplayName,
            document.Snapshot.ProjectId,
            document.LastSavedRevision,
            document.LastSavedContentDigest,
            DateTimeOffset.UtcNow);
        _documents.Remove(document.ProjectHandle.Value);
        _closed.Add(reopen.ReopenHandle.Value, new ClosedState(Workspace(document.WorkspaceHandle), _codec.MarkSaved(document.Snapshot)));
        return ValueTask.FromResult(new PhotonCadProjectCloseOutcome(document.ProjectHandle, reopen));
    }

    internal PhotonCadProjectDocument MakeDirty(PhotonCadProjectHandle handle)
    {
        var document = Project(handle);
        var state = _codec.Inspect(document.Snapshot);
        var dirty = _codec.Encode(new PhotonCadProjectStateV1(
            state.SessionId,
            state.ProjectId,
            state.Revision + 1,
            state.Title,
            state.Units,
            state.Entities,
            state.Operations,
            state.Occurrences,
            state.Issues,
            state.Bom,
            state.Artifacts,
            dirty: true));
        var updated = new PhotonCadProjectDocument(
            document.WorkspaceHandle,
            document.ProjectHandle,
            dirty,
            document.LastSavedContentDigest,
            document.LastSavedRevision,
            document.OpenedAtUtc);
        _documents[handle.Value] = updated;
        return updated;
    }

    public ValueTask ResetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ResetCount++;
        _workspaces.Clear();
        _documents.Clear();
        _closed.Clear();
        BlockOpen = null;
        BlockSave = null;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _workspaces.Clear();
        _documents.Clear();
        _closed.Clear();
        return ValueTask.CompletedTask;
    }

    private PhotonCadProjectSaveOutcome Outcome(
        PhotonCadProjectHandle source,
        long baseRevision,
        PhotonCadProjectDocument document)
    {
        var receipt = new PhotonCadProjectSaveReceipt(
            new PhotonCadSaveReceiptHandle(Handle("cad-save-receipt:", $"receipt{Interlocked.Increment(ref _sequence)}")),
            source,
            document.ProjectHandle,
            document.Snapshot.SessionId,
            document.Snapshot.ProjectId,
            baseRevision,
            document.Snapshot.Revision,
            document.ContentDigest,
            DateTimeOffset.UtcNow,
            atomic: true,
            document.ContentDigest);
        return new PhotonCadProjectSaveOutcome(receipt, document);
    }

    private PhotonCadProjectDocument Document(PhotonCadWorkspaceRegistration workspace, PhotonCadCanonicalProject canonical) =>
        new(
            workspace.WorkspaceHandle,
            new PhotonCadProjectHandle(Handle("cad-project:", $"project{Interlocked.Increment(ref _sequence)}")),
            canonical,
            canonical.ContentDigest,
            canonical.Revision,
            DateTimeOffset.UtcNow);

    private PhotonCadWorkspaceRegistration Workspace(PhotonCadWorkspaceHandle handle) =>
        _workspaces.TryGetValue(handle.Value, out var workspace)
            ? workspace
            : throw new PhotonCadProjectException("workspace_handle_unknown", nameof(handle));

    private PhotonCadProjectDocument Project(PhotonCadProjectHandle handle) =>
        _documents.TryGetValue(handle.Value, out var document)
            ? document
            : throw new PhotonCadProjectException("project_handle_unknown", nameof(handle));

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FakePhotonCadDesktopProjectHost));
    }

    private static string Handle(string prefix, string seed) => prefix + seed.PadRight(32, 'x');
    private sealed record ClosedState(PhotonCadWorkspaceRegistration Workspace, PhotonCadCanonicalProject Snapshot);
}
