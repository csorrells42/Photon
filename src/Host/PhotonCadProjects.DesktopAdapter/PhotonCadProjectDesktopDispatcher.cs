using System.Text.Json;
using PhotonCadProjects.Codec;
using PhotonCadProjects.Windows;

namespace PhotonCadProjects.DesktopAdapter;

/// <summary>
/// Strict WebView wire-v1 dispatcher. It owns correlation, cancellation and renderer-generation
/// state; the injected Windows host remains the sole persistence authority.
/// </summary>
public sealed class PhotonCadProjectDesktopDispatcher : IAsyncDisposable
{
    private readonly IPhotonCadDesktopProjectHost _host;
    private readonly PhotonCadProjectWireProjection _projection;
    private readonly Action<object> _post;
    private readonly TimeProvider _time;
    private readonly object _sync = new();
    private readonly Dictionary<string, ActiveRequest> _active = new(StringComparer.Ordinal);
    private readonly PhotonCadProjectRequestRegistry _requests;
    private string? _activePicker;
    private string? _activeRead;
    private string? _activeWrite;
    private long _epoch;
    private int _disposed;

    public PhotonCadProjectDesktopDispatcher(
        IPhotonCadDesktopProjectHost host,
        PhotonCadProjectWireProjection projection,
        Action<object> post,
        TimeProvider? timeProvider = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _projection = projection ?? throw new ArgumentNullException(nameof(projection));
        _post = post ?? throw new ArgumentNullException(nameof(post));
        _time = timeProvider ?? TimeProvider.System;
        _requests = new PhotonCadProjectRequestRegistry(_time);
    }

    public static PhotonCadProjectDesktopDispatcher CreateWindows(
        Action<object> post,
        IPhotonCadWindowsFileDialog? dialog = null,
        IPhotonCadWindowsDirtyCloseAuthority? dirtyClose = null,
        IPhotonCadWindowsOverwriteAuthority? overwrite = null,
        IPhotonCadDesktopProjectReferenceRevoker? references = null,
        IPhotonCadHandleIssuer? handles = null,
        IPhotonCadClock? clock = null,
        TimeProvider? timeProvider = null)
    {
        var codec = new PhotonCadCanonicalProjectCodecV1();
        var host = new PhotonCadWindowsDesktopProjectHost(
            codec, dialog, dirtyClose, overwrite, references, handles, clock);
        return new PhotonCadProjectDesktopDispatcher(host, new PhotonCadProjectWireProjection(codec), post, timeProvider);
    }

    public PhotonCadWindowsDesktopProjectReadiness Readiness => _host.Readiness;

    public object CapabilityAdvertisement()
    {
        var value = Readiness;
        return new
        {
            available = value.Available,
            reason = value.Reason,
            protocolVersion = value.ProtocolVersion,
            maximumOpenProjects = value.MaximumOpenProjects,
            maximumKnownWorkspaces = value.MaximumKnownWorkspaces,
            maximumReopenEntries = value.MaximumReopenEntries,
            maximumRequestHistory = value.MaximumRequestHistory,
            runtimeHydrationAvailable = value.RuntimeHydrationAvailable,
        };
    }

    public async Task HandleAsync(string type, JsonElement frame)
    {
        if (Volatile.Read(ref _disposed) != 0 || string.IsNullOrEmpty(type)) return;
        if (!frame.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String
            || !StringComparer.Ordinal.Equals(type, typeElement.GetString())
            || !PhotonCadProjectEnvelopeParser.TryParse(frame, out var request) || request is null)
        {
            if (PhotonCadProjectEnvelopeParser.TryExtractRequestId(frame, out var requestId))
                SafePost(ErrorFrame(requestId, "invalid-project-request", retryable: false));
            return;
        }
        if (request is PhotonCadProjectDesktopCancelRequest cancel)
        {
            HandleCancel(cancel);
            return;
        }
        var admission = Begin(request);
        if (admission.Active is null)
        {
            SafePost(FailureFrame(request, admission.Code ?? "project-transition-busy", unavailable: false, cancelled: false));
            return;
        }
        await RunAsync(admission.Active).ConfigureAwait(false);
    }

    public async ValueTask ResetAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ActiveRequest[] active;
        lock (_sync)
        {
            _epoch++;
            active = _active.Values.ToArray();
            _active.Clear();
            _activePicker = null;
            _activeRead = null;
            _activeWrite = null;
            _requests.Clear();
            foreach (var request in active) request.Invalidated = true;
        }
        foreach (var request in active) TryCancel(request.Cancellation);
        await _host.ResetAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;
        ActiveRequest[] active;
        lock (_sync)
        {
            _epoch++;
            active = _active.Values.ToArray();
            _active.Clear();
            _activePicker = null;
            _activeRead = null;
            _activeWrite = null;
            _requests.Clear();
            foreach (var request in active) request.Invalidated = true;
        }
        foreach (var request in active) TryCancel(request.Cancellation);
        await _host.DisposeAsync().ConfigureAwait(false);
    }

    private async Task RunAsync(ActiveRequest active)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(active.Cancellation.Token);
        deadline.CancelAfter(PhotonCadProjectDesktopProtocol.Deadline(active.Request.Operation));
        try
        {
            if (!Readiness.Available)
            {
                PostIfCurrent(active, FailureFrame(active.Request, Readiness.Reason, unavailable: true, cancelled: false));
                return;
            }
            var frame = await DispatchAsync(active.Request, deadline.Token).ConfigureAwait(false);
            PostIfCurrent(active, frame);
        }
        catch (OperationCanceledException)
        {
            if (!active.Invalidated && !active.Superseded)
                PostIfCurrent(active, FailureFrame(active.Request, "request-cancelled", unavailable: false, cancelled: true));
        }
        catch (Exception exception)
        {
            var failure = PhotonCadProjectErrorMapper.Map(exception);
            PostIfCurrent(active, FailureFrame(active.Request, failure.Code, failure.Unavailable, cancelled: false));
        }
        finally
        {
            Complete(active);
            active.Cancellation.Dispose();
        }
    }

    private async ValueTask<object> DispatchAsync(PhotonCadProjectDesktopRequest request, CancellationToken cancellationToken) => request switch
    {
        PhotonCadProjectDesktopPickerRequest picker => PickerFrame(
            request.RequestId,
            await _host.ChooseWorkspaceAsync(request.RequestId, picker.Purpose, picker.SuggestedName, cancellationToken).ConfigureAwait(false)),
        PhotonCadProjectDesktopCreateRequest create => LoadFrame(
            "photonCad.project.create.result",
            request.RequestId,
            "project-created",
            create.Value.WorkspaceHandle.Value,
            await _host.CreateProjectAsync(create.Value, cancellationToken).ConfigureAwait(false)),
        PhotonCadProjectDesktopOpenRequest open => LoadFrame(
            "photonCad.project.open.result",
            request.RequestId,
            "project-opened",
            open.Value.WorkspaceHandle.Value,
            await _host.OpenProjectAsync(open.Value, cancellationToken).ConfigureAwait(false)),
        PhotonCadProjectDesktopReopenRequest reopen => ReopenFrame(
            request.RequestId,
            reopen.Value.ReopenHandle.Value,
            await _host.ReopenProjectAsync(reopen.Value, cancellationToken).ConfigureAwait(false)),
        PhotonCadProjectDesktopRefreshRequest refresh => LoadFrame(
            "photonCad.project.refresh.result",
            request.RequestId,
            "project-refreshed",
            refresh.Value.ProjectHandle.Value,
            await _host.RefreshProjectAsync(refresh.Value, cancellationToken).ConfigureAwait(false)),
        PhotonCadProjectDesktopSaveRequest save => SaveFrame(
            "photonCad.project.save.result",
            request.RequestId,
            "project-saved",
            await _host.SaveProjectAsync(save.Value, cancellationToken).ConfigureAwait(false)),
        PhotonCadProjectDesktopSaveAsRequest saveAs => SaveFrame(
            "photonCad.project.saveAs.result",
            request.RequestId,
            "project-saved-as",
            await _host.SaveProjectAsAsync(saveAs.Value, cancellationToken).ConfigureAwait(false)),
        PhotonCadProjectDesktopCloseRequest close => CloseFrame(
            request.RequestId,
            await _host.CloseProjectAsync(close.Value, cancellationToken).ConfigureAwait(false)),
        _ => throw new PhotonCadProjectException("unsupported_project_operation", nameof(request)),
    };

    private Admission Begin(PhotonCadProjectDesktopRequest request)
    {
        ActiveRequest? superseded = null;
        ActiveRequest? active = null;
        string? rejection = null;
        lock (_sync)
        {
            if (Volatile.Read(ref _disposed) != 0) return new Admission(null, "project-host-closed");
            var activeIds = _active.Keys.ToHashSet(StringComparer.Ordinal);
            if (!_requests.TryReserve(request.RequestId, activeIds)) return new Admission(null, "duplicate-request-id");
            if (_active.Count >= PhotonCadProjectDesktopProtocol.MaximumActiveRequests)
                return new Admission(null, "project-request-capacity-reached");
            if (IsWrite(request.Operation))
            {
                if (_activeWrite is not null || _activeRead is not null || _activePicker is not null)
                    rejection = "project-transition-busy";
                else _activeWrite = request.RequestId;
            }
            else if (IsRead(request.Operation))
            {
                if (_activeWrite is not null || _activePicker is not null) rejection = "project-transition-busy";
                else
                {
                    superseded = TakeActive(_activeRead);
                    if (superseded is not null) superseded.Superseded = true;
                    _activeRead = request.RequestId;
                }
            }
            else
            {
                if (_activeWrite is not null || _activeRead is not null) rejection = "project-transition-busy";
                else
                {
                    superseded = TakeActive(_activePicker);
                    if (superseded is not null) superseded.Superseded = true;
                    _activePicker = request.RequestId;
                }
            }
            if (rejection is null)
            {
                active = new ActiveRequest(request, _epoch, new CancellationTokenSource());
                _active.Add(request.RequestId, active);
            }
        }
        if (superseded is not null) TryCancel(superseded.Cancellation);
        return new Admission(active, rejection);
    }

    private void HandleCancel(PhotonCadProjectDesktopCancelRequest cancel)
    {
        ActiveRequest? target = null;
        lock (_sync)
        {
            var activeIds = _active.Keys.ToHashSet(StringComparer.Ordinal);
            if (!_requests.TryReserve(cancel.RequestId, activeIds)) return;
            if (_active.TryGetValue(cancel.TargetRequestId, out var candidate)
                && candidate.Epoch == _epoch
                && candidate.Request.Operation == cancel.TargetOperation)
            {
                candidate.Invalidated = true;
                target = candidate;
                RemoveActive(candidate);
            }
        }
        if (target is not null) TryCancel(target.Cancellation);
    }

    private ActiveRequest? TakeActive(string? requestId)
    {
        if (requestId is null || !_active.Remove(requestId, out var active)) return null;
        if (_activePicker == requestId) _activePicker = null;
        if (_activeRead == requestId) _activeRead = null;
        if (_activeWrite == requestId) _activeWrite = null;
        return active;
    }

    private void Complete(ActiveRequest active)
    {
        lock (_sync)
        {
            if (_active.TryGetValue(active.Request.RequestId, out var current) && ReferenceEquals(current, active))
                RemoveActive(active);
        }
    }

    private void RemoveActive(ActiveRequest active)
    {
        _active.Remove(active.Request.RequestId);
        if (_activePicker == active.Request.RequestId) _activePicker = null;
        if (_activeRead == active.Request.RequestId) _activeRead = null;
        if (_activeWrite == active.Request.RequestId) _activeWrite = null;
    }

    private void PostIfCurrent(ActiveRequest active, object frame)
    {
        lock (_sync)
        {
            if (Volatile.Read(ref _disposed) != 0 || active.Invalidated || active.Superseded
                || active.Epoch != _epoch || !_active.TryGetValue(active.Request.RequestId, out var current)
                || !ReferenceEquals(current, active)) return;
        }
        SafePost(frame);
    }

    private void SafePost(object frame)
    {
        try { _post(frame); }
        catch { }
    }

    private object PickerFrame(string requestId, PhotonCadDesktopProjectPickerOutcome outcome)
    {
        var status = outcome.Status switch
        {
            PhotonCadNativeServiceStatus.Selected => "selected",
            PhotonCadNativeServiceStatus.Cancelled => "cancelled",
            PhotonCadNativeServiceStatus.Rejected => "rejected",
            PhotonCadNativeServiceStatus.Unavailable => "unavailable",
            _ => "unavailable",
        };
        object value = outcome.Workspace is null
            ? new { contractVersion = PhotonCadProjectContract.Version, requestId, status, reason = outcome.Reason }
            : new
            {
                contractVersion = PhotonCadProjectContract.Version,
                requestId,
                status,
                reason = outcome.Reason,
                workspaceHandle = outcome.Workspace.WorkspaceHandle.Value,
                label = outcome.Workspace.Binding.Label,
            };
        return new { type = "photonCad.project.picker.result", version = PhotonCadProjectDesktopProtocol.Version, value };
    }

    private object LoadFrame(string type, string requestId, string reason, string sourceHandle, PhotonCadProjectDocument document) => new
    {
        type,
        version = PhotonCadProjectDesktopProtocol.Version,
        value = new
        {
            contractVersion = PhotonCadProjectContract.Version,
            requestId,
            status = "opened",
            reason = document.Snapshot.Revision > 0 && !Readiness.RuntimeHydrationAvailable
                ? $"{reason}-view-save-only"
                : reason,
            sourceHandle,
            document = _projection.Document(document),
        },
    };

    private object ReopenFrame(string requestId, string sourceHandle, PhotonCadProjectDocument document) =>
        LoadFrame("photonCad.project.reopen.result", requestId, "project-reopened", sourceHandle, document);

    private object SaveFrame(string type, string requestId, string reason, PhotonCadProjectSaveOutcome outcome) => new
    {
        type,
        version = PhotonCadProjectDesktopProtocol.Version,
        value = new
        {
            contractVersion = PhotonCadProjectContract.Version,
            requestId,
            status = "saved",
            reason,
            receipt = _projection.SaveReceipt(outcome.Receipt),
            document = _projection.Document(outcome.Document),
        },
    };

    private static object CloseFrame(string requestId, PhotonCadProjectCloseOutcome outcome) => new
    {
        type = "photonCad.project.close.result",
        version = PhotonCadProjectDesktopProtocol.Version,
        value = new
        {
            contractVersion = PhotonCadProjectContract.Version,
            requestId,
            status = "closed",
            reason = "project-closed",
            projectHandle = outcome.ProjectHandle.Value,
            reopen = PhotonCadProjectWireProjection.Reopen(outcome.Reopen),
        },
    };

    private static object FailureFrame(
        PhotonCadProjectDesktopRequest request,
        string reason,
        bool unavailable,
        bool cancelled)
    {
        var status = cancelled && request.Operation is PhotonCadProjectDesktopOperation.Picker
            or PhotonCadProjectDesktopOperation.Open
            or PhotonCadProjectDesktopOperation.Reopen
            or PhotonCadProjectDesktopOperation.Refresh
            ? "cancelled"
            : unavailable ? "unavailable" : "rejected";
        var type = request.Operation switch
        {
            PhotonCadProjectDesktopOperation.Picker => "photonCad.project.picker.result",
            PhotonCadProjectDesktopOperation.Create => "photonCad.project.create.result",
            PhotonCadProjectDesktopOperation.Open => "photonCad.project.open.result",
            PhotonCadProjectDesktopOperation.Reopen => "photonCad.project.reopen.result",
            PhotonCadProjectDesktopOperation.Refresh => "photonCad.project.refresh.result",
            PhotonCadProjectDesktopOperation.Save => "photonCad.project.save.result",
            PhotonCadProjectDesktopOperation.SaveAs => "photonCad.project.saveAs.result",
            PhotonCadProjectDesktopOperation.Close => "photonCad.project.close.result",
            _ => "photonCad.project.error",
        };
        return new
        {
            type,
            version = PhotonCadProjectDesktopProtocol.Version,
            value = new { contractVersion = PhotonCadProjectContract.Version, requestId = request.RequestId, status, reason = SafeReason(reason) },
        };
    }

    private static object ErrorFrame(string requestId, string code, bool retryable) => new
    {
        type = "photonCad.project.error",
        version = PhotonCadProjectDesktopProtocol.Version,
        requestId,
        code = SafeReason(code),
        retryable,
    };

    private static string SafeReason(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 96 || !char.IsAsciiLetterOrDigit(value[0]))
            return "project-action-rejected";
        return value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or ':')
            ? value
            : "project-action-rejected";
    }

    private static bool IsRead(PhotonCadProjectDesktopOperation operation) => operation is
        PhotonCadProjectDesktopOperation.Open or PhotonCadProjectDesktopOperation.Reopen or PhotonCadProjectDesktopOperation.Refresh;

    private static bool IsWrite(PhotonCadProjectDesktopOperation operation) => operation is
        PhotonCadProjectDesktopOperation.Create or PhotonCadProjectDesktopOperation.Save
        or PhotonCadProjectDesktopOperation.SaveAs or PhotonCadProjectDesktopOperation.Close;

    private static void TryCancel(CancellationTokenSource source)
    {
        try { source.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(PhotonCadProjectDesktopDispatcher));
    }

    private sealed class ActiveRequest(
        PhotonCadProjectDesktopRequest request,
        long epoch,
        CancellationTokenSource cancellation)
    {
        internal PhotonCadProjectDesktopRequest Request { get; } = request;
        internal long Epoch { get; } = epoch;
        internal CancellationTokenSource Cancellation { get; } = cancellation;
        internal bool Superseded { get; set; }
        internal bool Invalidated { get; set; }
    }

    private sealed record Admission(ActiveRequest? Active, string? Code);
}
