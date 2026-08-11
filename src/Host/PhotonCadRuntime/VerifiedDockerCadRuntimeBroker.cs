using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace PhotonCadRuntime;

public static class CadDockerRuntimeBrokerFactory
{
    public static async ValueTask<ICadRuntimeBroker> ProvisionAsync(
        CadDockerRuntimeSettings? settings,
        ICadDockerImageInspector? imageInspector = null,
        ICadPathInspector? pathInspector = null,
        CancellationToken cancellationToken = default)
    {
        if (settings is null) return new UnavailableCadRuntimeBroker();
        try
        {
            var evidence = await CadDockerRuntimeEvidenceVerifier.VerifyAsync(
                settings,
                imageInspector ?? new CadDockerCliImageInspector(),
                cancellationToken).ConfigureAwait(false);
            await CadDockerRuntimeProtocolProbe.VerifyAsync(evidence, cancellationToken).ConfigureAwait(false);
            return new VerifiedDockerCadRuntimeBroker(
                evidence,
                pathInspector ?? new CadWindowsPathInspector());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
            KeyNotFoundException or FormatException or OverflowException or IOException or
            UnauthorizedAccessException or JsonException or DecoderFallbackException or
            System.ComponentModel.Win32Exception or CryptographicException)
        {
            return new RejectedCadRuntimeBroker();
        }
    }
}

public sealed class RejectedCadRuntimeBroker : ICadRuntimeBroker
{
    private static readonly CadRuntimeDescription Description = new(
        CadRuntimeAvailability.Rejected,
        "verified_runtime_rejected",
        "The configured CAD runtime failed identity or policy verification.");
    private static readonly CadError Error = new(
        "cad_runtime_rejected",
        "The configured CAD runtime failed identity or policy verification.",
        retryable: false);

    public CadRuntimeDescription Describe() => Description;
    public ValueTask<CadResult<CadProjectDescriptor>> OpenProjectAsync(CadOpenProjectRequest request, CancellationToken cancellationToken = default) =>
        Failure<CadProjectDescriptor>(cancellationToken);
    public ValueTask<CadResult<CadSessionDescriptor>> StartSessionAsync(CadStartSessionRequest request, CancellationToken cancellationToken = default) =>
        Failure<CadSessionDescriptor>(cancellationToken);
    public ValueTask<CadResult<CadCapabilityCatalog>> GetCatalogAsync(CadRequestId requestId, CancellationToken cancellationToken = default) =>
        Failure<CadCapabilityCatalog>(cancellationToken);
    public ValueTask<CadResult<CadOperationResult>> ExecuteAsync(CadOperationRequest request, CancellationToken cancellationToken = default) =>
        Failure<CadOperationResult>(cancellationToken);
    public ValueTask<CadResult<CadArtifactDescriptor>> GetArtifactReceiptAsync(CadArtifactReadRequest request, CancellationToken cancellationToken = default) =>
        Failure<CadArtifactDescriptor>(cancellationToken);
    public ValueTask<CadResult<CadArtifactReadLease>> OpenArtifactReadAsync(CadArtifactReadRequest request, CancellationToken cancellationToken = default) =>
        Failure<CadArtifactReadLease>(cancellationToken);
    public ValueTask<CadResult<CadVerificationResult>> VerifyAsync(CadVerificationRequest request, CancellationToken cancellationToken = default) =>
        Failure<CadVerificationResult>(cancellationToken);
    public ValueTask<CadResult<CadSessionDescriptor>> CloseSessionAsync(CadCloseSessionRequest request, CancellationToken cancellationToken = default) =>
        Failure<CadSessionDescriptor>(cancellationToken);

    private static ValueTask<CadResult<T>> Failure<T>(CancellationToken cancellationToken) where T : class =>
        ValueTask.FromResult(CadResult<T>.Failure(cancellationToken.IsCancellationRequested
            ? new CadError("cancelled", "The CAD request was cancelled.", retryable: true)
            : Error));
}

public sealed class VerifiedDockerCadRuntimeBroker : ICadRuntimeBroker, IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly CadVerifiedDockerRuntimeEvidence _evidence;
    private readonly ICadPathInspector _pathInspector;
    private readonly CadCapabilityCatalog _catalog;
    private readonly CadRuntimeDescription _description;
    private readonly Dictionary<string, CadProjectRuntimeState> _projects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CadDockerRuntimeSession> _sessions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _openingProjects = new(StringComparer.Ordinal);
    private int _disposed;

    internal VerifiedDockerCadRuntimeBroker(
        CadVerifiedDockerRuntimeEvidence evidence,
        ICadPathInspector pathInspector)
    {
        _evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        _pathInspector = pathInspector ?? throw new ArgumentNullException(nameof(pathInspector));
        _catalog = CadPinnedCapabilityCatalog.Create(DateTimeOffset.UtcNow);
        var bundles = new CadRuntimeBundleSetIdentity(
            new CadBundleIdentity(
                CadRuntimeRole.Geometry,
                "photon-cad-geometry",
                CadDockerRuntimeIdentity.GeometryBundleVersion,
                "docker",
                evidence.Geometry.SourceRevision,
                "linux-amd64",
                evidence.ReceiptSha256,
                evidence.ReceiptCreatedAtUtc),
            new CadBundleIdentity(
                CadRuntimeRole.Assembly,
                "photon-cad-assembly",
                CadDockerRuntimeIdentity.AssemblyBundleVersion,
                "docker",
                evidence.Assembly.SourceRevision,
                "linux-amd64",
                evidence.ReceiptSha256,
                evidence.ReceiptCreatedAtUtc),
            new CadRevision(1));
        _description = new CadRuntimeDescription(
            CadRuntimeAvailability.Ready,
            "verified_runtime_ready",
            "The pinned isolated CAD runtimes passed immutable evidence and protocol verification.",
            bundles,
            _catalog);
    }

    public CadRuntimeDescription Describe() => Volatile.Read(ref _disposed) == 0
        ? _description
        : new CadRuntimeDescription(
            CadRuntimeAvailability.Rejected,
            "runtime_closed",
            "The CAD runtime broker is closed.");

    public ValueTask<CadResult<CadProjectDescriptor>> OpenProjectAsync(
        CadOpenProjectRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromResult(Cancelled<CadProjectDescriptor>());
        if (Volatile.Read(ref _disposed) != 0)
            return ValueTask.FromResult(Closed<CadProjectDescriptor>());
        var project = new CadProjectRuntimeState(
            CadProjectHandle.New(),
            request.Workspace,
            request.DisplayName);
        lock (_sync)
        {
            if (_disposed != 0) return ValueTask.FromResult(Closed<CadProjectDescriptor>());
            _projects.Add(project.Handle.Value, project);
        }
        return ValueTask.FromResult(CadResult<CadProjectDescriptor>.Success(project.Describe()));
    }

    public async ValueTask<CadResult<CadSessionDescriptor>> StartSessionAsync(
        CadStartSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        CadProjectRuntimeState project;
        lock (_sync)
        {
            if (_disposed != 0) return Closed<CadSessionDescriptor>();
            if (!_projects.TryGetValue(request.Project.Value, out project!))
                return Failure<CadSessionDescriptor>("project_not_found", "The CAD project is unavailable.", false);
            if (project.Revision.Value != request.ExpectedRevision.Value)
                return Failure<CadSessionDescriptor>("revision_conflict", "The CAD project revision changed.", true);
            if (_sessions.Values.Any(session => session.Project.Handle == request.Project) ||
                _openingProjects.Contains(request.Project.Value))
                return Failure<CadSessionDescriptor>("session_already_active", "The CAD project already has an active session.", false);
            if (_sessions.Count + _openingProjects.Count >= _evidence.Settings.MaximumConcurrentSessions)
                return Failure<CadSessionDescriptor>("session_capacity_reached", "The isolated CAD runtime is at its session limit.", true);
            _openingProjects.Add(request.Project.Value);
        }

        try
        {
            var session = await CadDockerRuntimeSession.StartAsync(
                _evidence,
                _pathInspector,
                project,
                cancellationToken).ConfigureAwait(false);
            var brokerClosed = false;
            lock (_sync)
            {
                if (_disposed != 0)
                {
                    brokerClosed = true;
                }
                else
                {
                    _sessions.Add(session.Handle.Value, session);
                }
            }
            if (brokerClosed)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                return Closed<CadSessionDescriptor>();
            }
            return CadResult<CadSessionDescriptor>.Success(session.Describe());
        }
        catch (OperationCanceledException)
        {
            return Cancelled<CadSessionDescriptor>();
        }
        catch (Exception exception) when (IsRuntimeException(exception))
        {
            return RuntimeFailure<CadSessionDescriptor>(exception);
        }
        finally
        {
            lock (_sync) _openingProjects.Remove(request.Project.Value);
        }
    }

    public ValueTask<CadResult<CadCapabilityCatalog>> GetCatalogAsync(
        CadRequestId requestId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestId);
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromResult(Cancelled<CadCapabilityCatalog>());
        return ValueTask.FromResult(Volatile.Read(ref _disposed) == 0
            ? CadResult<CadCapabilityCatalog>.Success(_catalog)
            : Closed<CadCapabilityCatalog>());
    }

    public async ValueTask<CadResult<CadOperationResult>> ExecuteAsync(
        CadOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var session = FindSession(request.Session);
        if (session is null)
            return Failure<CadOperationResult>("session_not_found", "The CAD session is unavailable.", false);
        try
        {
            return CadResult<CadOperationResult>.Success(
                await session.ExecuteAsync(request, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            return Cancelled<CadOperationResult>();
        }
        catch (Exception exception) when (IsRuntimeException(exception))
        {
            return RuntimeFailure<CadOperationResult>(exception);
        }
    }

    public async ValueTask<CadResult<CadArtifactDescriptor>> GetArtifactReceiptAsync(
        CadArtifactReadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var session = FindSession(request.Session);
        if (session is null)
            return Failure<CadArtifactDescriptor>("session_not_found", "The CAD session is unavailable.", false);
        try
        {
            var descriptor = await session.GetArtifactReceiptAsync(
                request.Artifact,
                request.MaximumByteLength,
                cancellationToken).ConfigureAwait(false);
            return descriptor is null
                ? Failure<CadArtifactDescriptor>("artifact_not_found", "The CAD artifact is unavailable.", false)
                : CadResult<CadArtifactDescriptor>.Success(descriptor);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<CadArtifactDescriptor>();
        }
        catch (Exception exception) when (IsRuntimeException(exception))
        {
            return RuntimeFailure<CadArtifactDescriptor>(exception);
        }
    }

    public async ValueTask<CadResult<CadArtifactReadLease>> OpenArtifactReadAsync(
        CadArtifactReadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var session = FindSession(request.Session);
        if (session is null)
            return Failure<CadArtifactReadLease>("session_not_found", "The CAD session is unavailable.", false);
        try
        {
            var lease = await session.OpenArtifactReadAsync(request.Artifact, request.MaximumByteLength, cancellationToken).ConfigureAwait(false);
            return lease is null
                ? Failure<CadArtifactReadLease>("artifact_not_found", "The CAD artifact is unavailable.", false)
                : CadResult<CadArtifactReadLease>.Success(lease);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<CadArtifactReadLease>();
        }
        catch (Exception exception) when (IsRuntimeException(exception) || exception is CryptographicException)
        {
            return RuntimeFailure<CadArtifactReadLease>(exception);
        }
    }

    public async ValueTask<CadResult<CadVerificationResult>> VerifyAsync(
        CadVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var session = FindSession(request.Session);
        if (session is null)
            return Failure<CadVerificationResult>("session_not_found", "The CAD session is unavailable.", false);
        try
        {
            return CadResult<CadVerificationResult>.Success(
                await session.VerifyAsync(request, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            return Cancelled<CadVerificationResult>();
        }
        catch (Exception exception) when (IsRuntimeException(exception))
        {
            return RuntimeFailure<CadVerificationResult>(exception);
        }
    }

    public async ValueTask<CadResult<CadSessionDescriptor>> CloseSessionAsync(
        CadCloseSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        CadDockerRuntimeSession? session;
        lock (_sync)
        {
            _sessions.TryGetValue(request.Session.Value, out session);
        }
        if (session is null)
            return Failure<CadSessionDescriptor>("session_not_found", "The CAD session is unavailable.", false);
        try
        {
            await session.CloseAsync(cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                if (_sessions.TryGetValue(request.Session.Value, out var current) && ReferenceEquals(current, session))
                    _sessions.Remove(request.Session.Value);
            }
            return CadResult<CadSessionDescriptor>.Success(session.Describe());
        }
        catch (OperationCanceledException)
        {
            var cleanupFailure = await TryRetireSessionAsync(request.Session, session).ConfigureAwait(false);
            if (cleanupFailure is not null) return RuntimeFailure<CadSessionDescriptor>(cleanupFailure);
            return Cancelled<CadSessionDescriptor>();
        }
        catch (Exception exception) when (IsRuntimeException(exception))
        {
            var cleanupFailure = await TryRetireSessionAsync(request.Session, session).ConfigureAwait(false);
            if (cleanupFailure is not null) return RuntimeFailure<CadSessionDescriptor>(cleanupFailure);
            return RuntimeFailure<CadSessionDescriptor>(exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        CadDockerRuntimeSession[] sessions;
        lock (_sync)
        {
            sessions = _sessions.Values.ToArray();
            _sessions.Clear();
        }
        await CadAsyncCleanup.DisposeAllAsync(sessions).ConfigureAwait(false);
    }

    private CadDockerRuntimeSession? FindSession(CadSessionHandle session)
    {
        lock (_sync)
            return _disposed == 0 && _sessions.TryGetValue(session.Value, out var value) ? value : null;
    }

    private async ValueTask<Exception?> TryRetireSessionAsync(
        CadSessionHandle handle,
        CadDockerRuntimeSession session)
    {
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRuntimeException(exception))
        {
            return exception;
        }
        lock (_sync)
        {
            if (_sessions.TryGetValue(handle.Value, out var current) && ReferenceEquals(current, session))
                _sessions.Remove(handle.Value);
        }
        return null;
    }

    private static bool IsRuntimeException(Exception exception) =>
        exception is CadContractException or CadDockerRuntimeException or CadProtocolException or
            CadStateTransitionException or IOException or UnauthorizedAccessException or InvalidOperationException;

    private static CadResult<T> RuntimeFailure<T>(Exception exception) where T : class
    {
        var code = exception switch
        {
            CadContractException => "invalid_cad_request",
            CadStateTransitionException => "invalid_session_state",
            CadProtocolException => "isolated_runtime_protocol_failed",
            CadDockerRuntimeException => "isolated_runtime_failed",
            _ => "cad_runtime_failed",
        };
        return Failure<T>(code, "The isolated CAD operation failed closed.", retryable: false);
    }

    private static CadResult<T> Failure<T>(string code, string message, bool retryable) where T : class =>
        CadResult<T>.Failure(new CadError(code, message, retryable));
    private static CadResult<T> Cancelled<T>() where T : class =>
        Failure<T>("cancelled", "The CAD request was cancelled.", true);
    private static CadResult<T> Closed<T>() where T : class =>
        Failure<T>("runtime_closed", "The CAD runtime broker is closed.", false);
}

internal sealed class CadProjectRuntimeState
{
    private readonly object _sync = new();
    private readonly List<CadProjectEntity> _entities = [];
    private readonly List<CadProjectOperationRecord> _operations = [];
    private CadRevision _revision = new(0);
    private CadProjectState _state = CadProjectState.Available;

    internal CadProjectRuntimeState(CadProjectHandle handle, CadWorkspaceHandle workspace, string displayName)
    {
        Handle = handle;
        Workspace = workspace;
        DisplayName = displayName;
    }

    internal CadProjectHandle Handle { get; }
    internal CadWorkspaceHandle Workspace { get; }
    internal string DisplayName { get; }
    internal CadRevision Revision { get { lock (_sync) return _revision; } }

    internal CadProjectDescriptor Describe()
    {
        lock (_sync) return new CadProjectDescriptor(Handle, DisplayName, _revision, _state);
    }

    internal bool RevisionMatches(CadRevision expected)
    {
        lock (_sync) return _revision.Value == expected.Value && _state == CadProjectState.Available;
    }

    internal bool HasCapacity(bool createsEntity)
    {
        lock (_sync)
            return _state == CadProjectState.Available && _operations.Count < 5_000 &&
                (!createsEntity || _entities.Count < 10_000);
    }

    internal CadProjectSnapshot Apply(
        CadSessionHandle session,
        CadOperationRequest request,
        CadGeometryObjectReference? created,
        string label)
    {
        lock (_sync)
        {
            if (_state != CadProjectState.Available || _revision.Value != request.BaseRevision.Value)
                throw new CadStateTransitionException("project_revision_conflict");
            _revision = new CadRevision(checked(_revision.Value + 1));
            if (created is not null)
            {
                _entities.Add(new CadProjectEntity(
                    created.EntityId,
                    parentId: null,
                    CadEntityKind.Body,
                    label,
                    visible: true,
                    suppressed: false,
                    created.CapabilityId));
            }
            _operations.Add(new CadProjectOperationRecord(
                $"operation_{Guid.NewGuid():N}",
                request.CapabilityId,
                label,
                DateTimeOffset.UtcNow,
                CadOperationRecordState.Applied));
            return SnapshotUnsafe(session, CadProjectMode.Scratch);
        }
    }

    internal CadProjectSnapshot Snapshot(CadSessionHandle session)
    {
        lock (_sync) return SnapshotUnsafe(session, CadProjectMode.Scratch);
    }

    internal void MarkClosed()
    {
        lock (_sync) _state = CadProjectState.Closed;
    }

    private CadProjectSnapshot SnapshotUnsafe(CadSessionHandle session, CadProjectMode mode) => new(
        session,
        Handle,
        _revision,
        DisplayName,
        CadLengthUnit.Millimeter,
        mode,
        _entities,
        _operations,
        [],
        dirty: _operations.Count > 0);
}

internal sealed class CadDockerRuntimeSession : IAsyncDisposable
{
    private readonly CadVerifiedDockerRuntimeEvidence _evidence;
    private readonly ICadPathInspector _pathInspector;
    private readonly CadSessionStateGate _state;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _artifactLeaseSync = new();
    private readonly Dictionary<string, CadGeometryObjectReference> _objects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CadRuntimeArtifactReceipt> _artifacts = new(StringComparer.Ordinal);
    private readonly HashSet<CadArtifactReadLease> _artifactLeases = [];
    private readonly CadDockerContainerProcess _geometryContainer;
    private readonly CadDockerContainerProcess _assemblyContainer;
    private readonly CadGeometryMcpClient _geometry;
    private readonly CadPartCadProtocolClient _assembly;
    private int _pendingOperations;
    private int _disposed;
    private Exception? _cleanupFailure;
    private int _cleanupComplete;

    private CadDockerRuntimeSession(
        CadVerifiedDockerRuntimeEvidence evidence,
        ICadPathInspector pathInspector,
        CadProjectRuntimeState project,
        CadSessionHandle handle,
        CadSessionStateGate state,
        CadDockerContainerProcess geometryContainer,
        CadDockerContainerProcess assemblyContainer,
        CadGeometryMcpClient geometry,
        CadPartCadProtocolClient assembly)
    {
        _evidence = evidence;
        _pathInspector = pathInspector;
        Project = project;
        Handle = handle;
        _state = state;
        _geometryContainer = geometryContainer;
        _assemblyContainer = assemblyContainer;
        _geometry = geometry;
        _assembly = assembly;
    }

    internal CadProjectRuntimeState Project { get; }
    internal CadSessionHandle Handle { get; }
    internal CadPartCadProtocolProof AssemblyProof => _assembly.Proof;

    internal static async ValueTask<CadDockerRuntimeSession> StartAsync(
        CadVerifiedDockerRuntimeEvidence evidence,
        ICadPathInspector pathInspector,
        CadProjectRuntimeState project,
        CancellationToken cancellationToken)
    {
        var handle = CadSessionHandle.New();
        var state = new CadSessionStateGate(handle);
        state.BeginOpening();
        CadDockerContainerProcess? geometryContainer = null;
        CadDockerContainerProcess? assemblyContainer = null;
        CadGeometryMcpClient? geometry = null;
        CadPartCadProtocolClient? assembly = null;
        try
        {
            geometryContainer = await CadDockerContainerProcess.StartAsync(
                evidence,
                CadDockerRuntimeRole.Geometry,
                handle,
                project.Handle,
                cancellationToken).ConfigureAwait(false);
            assemblyContainer = await CadDockerContainerProcess.StartAsync(
                evidence,
                CadDockerRuntimeRole.Assembly,
                handle,
                project.Handle,
                cancellationToken).ConfigureAwait(false);
            geometry = await CadGeometryMcpClient.ConnectAsync(
                geometryContainer,
                evidence.Settings.RequestTimeout,
                cancellationToken).ConfigureAwait(false);
            assembly = await CadPartCadProtocolClient.ConnectAndProveAsync(
                assemblyContainer,
                evidence.Settings.RequestTimeout,
                cancellationToken).ConfigureAwait(false);
            state.MarkReady();
            return new CadDockerRuntimeSession(
                evidence,
                pathInspector,
                project,
                handle,
                state,
                geometryContainer,
                assemblyContainer,
                geometry,
                assembly);
        }
        catch
        {
            try { state.MarkFaulted(); } catch (CadStateTransitionException) { }
            await CadAsyncCleanup.DisposeAllAsync(
                assembly,
                geometry,
                assemblyContainer,
                geometryContainer).ConfigureAwait(false);
            throw;
        }
    }

    internal CadSessionDescriptor Describe()
    {
        var snapshot = _state.Snapshot();
        return new CadSessionDescriptor(Handle, Project.Handle, snapshot.State, Project.Revision);
    }

    internal async ValueTask<CadOperationResult> ExecuteAsync(
        CadOperationRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Session != Handle || request.Project != Project.Handle)
            throw new CadContractException("session_project_mismatch", nameof(request));
        if (Interlocked.Increment(ref _pendingOperations) > 2)
        {
            Interlocked.Decrement(ref _pendingOperations);
            return Rejected(request, "operation_queue_full", stale: false, CadOperationStatus.Unavailable);
        }
        try
        {
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Decrement(ref _pendingOperations);
            throw;
        }
        var began = false;
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new CadStateTransitionException("session_closed");
            _state.BeginOperation(request.RequestId);
            began = true;
            if (!Project.RevisionMatches(request.BaseRevision))
                return Rejected(request, "revision_conflict", stale: true);
            if (request.Mode == CadOperationMode.Suggest)
                return new CadOperationResult(
                    request.RequestId,
                    request.Project,
                    request.BaseRevision,
                    request.BaseRevision,
                    CadOperationStatus.Unavailable,
                    stale: false,
                    "preview_not_supported");

            var createsEntity = request.CapabilityId is CadPinnedCapabilityCatalog.BoxCapabilityId or
                CadPinnedCapabilityCatalog.CylinderCapabilityId;
            if (!Project.HasCapacity(createsEntity))
                return Rejected(request, "project_capacity_reached", stale: false, CadOperationStatus.Unavailable);

            CadGeometryObjectReference? created = null;
            string label;
            CadArtifactHandle[] artifacts = [];
            CadValidationFinding[] issues = [];
            switch (request.CapabilityId)
            {
                case CadPinnedCapabilityCatalog.BoxCapabilityId:
                    RequireNoTargets(request);
                    created = await _geometry.CreateBoxAsync(
                        RequiredNumber(request, "length"),
                        RequiredNumber(request, "width"),
                        RequiredNumber(request, "height"),
                        cancellationToken).ConfigureAwait(false);
                    label = "Box";
                    break;
                case CadPinnedCapabilityCatalog.CylinderCapabilityId:
                    RequireNoTargets(request);
                    created = await _geometry.CreateCylinderAsync(
                        RequiredNumber(request, "radius"),
                        RequiredNumber(request, "height"),
                        cancellationToken).ConfigureAwait(false);
                    label = "Cylinder";
                    break;
                case CadPinnedCapabilityCatalog.MeasureCapabilityId:
                    RequireNoInputs(request);
                    var measured = await _geometry.MeasureAsync(RequiredTarget(request), cancellationToken).ConfigureAwait(false);
                    issues = [new CadValidationFinding(
                        CadValidationSeverity.Information,
                        "measurement_summary",
                        DisplaySummary(measured.Summary),
                        request.TargetEntityIds)];
                    label = "Measure solid";
                    break;
                case CadPinnedCapabilityCatalog.ValidateCapabilityId:
                    RequireNoInputs(request);
                    _ = await _geometry.ValidateAsync(RequiredTarget(request), cancellationToken).ConfigureAwait(false);
                    label = "Validate solid";
                    break;
                case CadPinnedCapabilityCatalog.ExportStepCapabilityId:
                    RequireNoInputs(request);
                    var receipt = await ExportStepAsync(RequiredTarget(request), cancellationToken).ConfigureAwait(false);
                    artifacts = [receipt.Artifact];
                    label = "Export STEP copy";
                    break;
                default:
                    return Rejected(request, "capability_not_available", stale: false, CadOperationStatus.Unavailable);
            }

            var snapshot = Project.Apply(Handle, request, created, label);
            if (created is not null) _objects.Add(created.EntityId, created);
            return new CadOperationResult(
                request.RequestId,
                request.Project,
                request.BaseRevision,
                snapshot.Revision,
                CadOperationStatus.Accepted,
                stale: false,
                "accepted",
                issues,
                artifacts,
                snapshot);
        }
        catch (OperationCanceledException)
        {
            Fault();
            await DisposeCoreAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (exception is CadProtocolException or CadDockerRuntimeException or IOException)
        {
            Fault();
            await DisposeCoreAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (began && _state.Snapshot().State == CadSessionState.Busy)
                _state.CompleteOperation(request.RequestId);
            _operationGate.Release();
            Interlocked.Decrement(ref _pendingOperations);
        }
    }

    internal async ValueTask<CadVerificationResult> VerifyAsync(
        CadVerificationRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Session != Handle || request.Project != Project.Handle)
            throw new CadContractException("session_project_mismatch", nameof(request));
        if (Interlocked.Increment(ref _pendingOperations) > 2)
        {
            Interlocked.Decrement(ref _pendingOperations);
            return new CadVerificationResult(
                request.RequestId,
                request.Project,
                request.Revision,
                CadVerificationStatus.Unavailable,
                stale: false,
                [new CadValidationFinding(CadValidationSeverity.Information, "operation_queue_full", "The isolated CAD session already has active and pending work.")],
                DateTimeOffset.UtcNow);
        }
        try
        {
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Decrement(ref _pendingOperations);
            throw;
        }
        var began = false;
        try
        {
            _state.BeginOperation(request.RequestId);
            began = true;
            if (!Project.RevisionMatches(request.Revision))
                return new CadVerificationResult(
                    request.RequestId,
                    request.Project,
                    request.Revision,
                    CadVerificationStatus.Unavailable,
                    stale: true,
                    [new CadValidationFinding(CadValidationSeverity.Warning, "revision_conflict", "The project revision changed.")],
                    DateTimeOffset.UtcNow);
            if (request.Checks.Any(check => check is not CadVerificationCheck.ValidSolids and
                not CadVerificationCheck.ExportReadiness))
                return new CadVerificationResult(
                    request.RequestId,
                    request.Project,
                    request.Revision,
                    CadVerificationStatus.Unavailable,
                    stale: false,
                    [new CadValidationFinding(CadValidationSeverity.Information, "verification_not_mapped", "Only valid-solids and export-readiness checks are mapped in this minimal runtime.")],
                    DateTimeOffset.UtcNow);
            foreach (var reference in _objects.Values)
                _ = await _geometry.ValidateAsync(reference, cancellationToken).ConfigureAwait(false);
            return new CadVerificationResult(
                request.RequestId,
                request.Project,
                request.Revision,
                CadVerificationStatus.Passed,
                stale: false,
                [],
                DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            Fault();
            await DisposeCoreAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (exception is CadProtocolException or CadDockerRuntimeException or IOException)
        {
            Fault();
            await DisposeCoreAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (began && _state.Snapshot().State == CadSessionState.Busy)
                _state.CompleteOperation(request.RequestId);
            _operationGate.Release();
            Interlocked.Decrement(ref _pendingOperations);
        }
    }

    internal async ValueTask CloseAsync(CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state.Snapshot().State is not CadSessionState.Closing and not CadSessionState.Closed)
                _state.BeginClosing();
            await DisposeCoreAsync().ConfigureAwait(false);
            if (_state.Snapshot().State != CadSessionState.Closed) _state.MarkClosed();
            Project.MarkClosed();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_state.Snapshot().State is not CadSessionState.Closing and not CadSessionState.Closed)
                _state.BeginClosing();
            await DisposeCoreAsync().ConfigureAwait(false);
            if (_state.Snapshot().State != CadSessionState.Closed) _state.MarkClosed();
            Project.MarkClosed();
        }
        finally
        {
            _operationGate.Release();
            _operationGate.Dispose();
        }
    }

    private async ValueTask<CadRuntimeArtifactReceipt> ExportStepAsync(
        CadGeometryObjectReference reference,
        CancellationToken cancellationToken)
    {
        var artifact = CadArtifactHandle.New();
        var relative = new CadRelativePath($"photon-{artifact.Value}.step");
        var resolved = CadPathPolicy.ResolveUnderTrustedRoot(_evidence.Settings.WorkspacePath, relative, _pathInspector);
        if (_pathInspector.Inspect(resolved.AbsolutePath).Kind != CadPathEntryKind.Missing)
            throw new CadContractException("artifact_collision", nameof(relative));
        var report = await _geometry.ExportStepAsync(reference, relative, cancellationToken).ConfigureAwait(false);
        if (!report.Summary.Contains(relative.Value, StringComparison.Ordinal))
            throw new CadProtocolException("step_export_acknowledgement_missing");
        resolved = CadPathPolicy.ResolveUnderTrustedRoot(_evidence.Settings.WorkspacePath, relative, _pathInspector);
        var inspection = _pathInspector.Inspect(resolved.AbsolutePath);
        if (inspection.Kind != CadPathEntryKind.File || inspection.IsReparsePoint || inspection.HasMultipleHardLinks)
            throw new CadContractException("unsafe_export_artifact", nameof(relative));
        await using var stream = new FileStream(
            resolved.AbsolutePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1_048_576,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var byteLength = stream.Length;
        if (byteLength <= 0 || byteLength > CadContractLimits.MaximumBrokeredArtifactBytes)
            throw new CadContractException("invalid_byte_length", nameof(relative));
        var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        var identity = CadArtifactOpenFileVerifier.Verify(stream.SafeFileHandle, resolved.AbsolutePath);
        var identityAfterHash = CadArtifactOpenFileVerifier.Verify(stream.SafeFileHandle, resolved.AbsolutePath);
        if (identityAfterHash != identity)
            throw new CadContractException("artifact_identity_changed", nameof(relative));
        var receipt = new CadRuntimeArtifactReceipt(artifact, relative, identity, digest, byteLength, "model/step");
        _artifacts.Add(artifact.Value, receipt);
        return receipt;
    }

    internal async ValueTask<CadArtifactDescriptor?> GetArtifactReceiptAsync(
        CadArtifactHandle artifact,
        long maximumByteLength,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireArtifactReadableState();
            if (!_artifacts.TryGetValue(artifact.Value, out var receipt) || receipt.ByteLength > maximumByteLength)
                return null;
            return receipt.Describe(Handle);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    internal async ValueTask<CadArtifactReadLease?> OpenArtifactReadAsync(
        CadArtifactHandle artifact,
        long maximumByteLength,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireArtifactReadableState();
            if (!_artifacts.TryGetValue(artifact.Value, out var receipt) || receipt.ByteLength > maximumByteLength)
                return null;

            var resolved = CadPathPolicy.ResolveUnderTrustedRoot(
                _evidence.Settings.WorkspacePath,
                receipt.RelativePath,
                _pathInspector);
            var inspection = _pathInspector.Inspect(resolved.AbsolutePath);
            if (inspection.Kind != CadPathEntryKind.File || inspection.IsReparsePoint || inspection.HasMultipleHardLinks)
                throw new CadContractException("unsafe_artifact_read", nameof(artifact));

            var stream = new FileStream(
                resolved.AbsolutePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1_048_576,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            try
            {
                var identity = CadArtifactOpenFileVerifier.Verify(stream.SafeFileHandle, resolved.AbsolutePath);
                if (identity != receipt.Identity)
                    throw new CadContractException("artifact_receipt_identity_mismatch", nameof(artifact));
                if (stream.Length != receipt.ByteLength)
                    throw new CadContractException("artifact_receipt_length_mismatch", nameof(artifact));
                var digest = Convert.ToHexString(
                    await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
                if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(digest),
                    Convert.FromHexString(receipt.Sha256)))
                    throw new CadContractException("artifact_receipt_digest_mismatch", nameof(artifact));
                var identityAfterHash = CadArtifactOpenFileVerifier.Verify(stream.SafeFileHandle, resolved.AbsolutePath);
                if (identityAfterHash != receipt.Identity)
                    throw new CadContractException("artifact_identity_changed", nameof(artifact));
                stream.Position = 0;
                var lease = new CadArtifactReadLease(receipt.Describe(Handle), stream, ReleaseArtifactLease);
                lock (_artifactLeaseSync)
                {
                    if (Volatile.Read(ref _disposed) != 0)
                        throw new CadStateTransitionException("session_not_ready");
                    _artifactLeases.Add(lease);
                }
                return lease;
            }
            catch
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private void RequireArtifactReadableState()
    {
        if (Volatile.Read(ref _disposed) != 0 || _state.Snapshot().State != CadSessionState.Ready)
            throw new CadStateTransitionException("session_not_ready");
    }

    private void ReleaseArtifactLease(CadArtifactReadLease lease)
    {
        lock (_artifactLeaseSync)
            _artifactLeases.Remove(lease);
    }

    private async ValueTask DisposeArtifactLeasesAsync()
    {
        CadArtifactReadLease[] leases;
        lock (_artifactLeaseSync)
        {
            leases = _artifactLeases.ToArray();
            _artifactLeases.Clear();
        }
        await CadAsyncCleanup.DisposeAllAsync(leases).ConfigureAwait(false);
    }

    private CadGeometryObjectReference RequiredTarget(CadOperationRequest request)
    {
        if (request.TargetEntityIds.Count != 1 || !_objects.TryGetValue(request.TargetEntityIds[0], out var reference))
            throw new CadContractException("single_known_target_required", nameof(request.TargetEntityIds));
        return reference;
    }

    private static double RequiredNumber(CadOperationRequest request, string id)
    {
        if (!request.Inputs.TryGetValue(id, out var value) || value is not CadNumberInputValue number)
            throw new CadContractException("required_number", id);
        var expected = request.CapabilityId == CadPinnedCapabilityCatalog.BoxCapabilityId
            ? new[] { "length", "width", "height" }
            : new[] { "radius", "height" };
        if (request.Inputs.Count != expected.Length || request.Inputs.Keys.Except(expected, StringComparer.Ordinal).Any())
            throw new CadContractException("unexpected_operation_input", nameof(request.Inputs));
        return number.Value;
    }

    private static void RequireNoTargets(CadOperationRequest request)
    {
        if (request.TargetEntityIds.Count != 0)
            throw new CadContractException("targets_not_supported", nameof(request.TargetEntityIds));
    }

    private static void RequireNoInputs(CadOperationRequest request)
    {
        if (request.Inputs.Count != 0)
            throw new CadContractException("inputs_not_supported", nameof(request.Inputs));
    }

    private static CadOperationResult Rejected(
        CadOperationRequest request,
        string reason,
        bool stale,
        CadOperationStatus status = CadOperationStatus.Rejected) => new(
            request.RequestId,
            request.Project,
            request.BaseRevision,
            request.BaseRevision,
            status,
            stale,
            reason);

    private void Fault()
    {
        try { _state.MarkFaulted(); } catch (CadStateTransitionException) { }
    }

    private async ValueTask DisposeCoreAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            if (_cleanupFailure is not null)
                throw new CadDockerRuntimeException("runtime_cleanup_failed", innerException: _cleanupFailure);
            if (Volatile.Read(ref _cleanupComplete) == 0)
                throw new CadDockerRuntimeException("runtime_cleanup_in_progress");
            return;
        }
        try
        {
            await DisposeArtifactLeasesAsync().ConfigureAwait(false);
            await CadAsyncCleanup.DisposeAllAsync(
                _assembly,
                _geometry,
                _assemblyContainer,
                _geometryContainer).ConfigureAwait(false);
            Volatile.Write(ref _cleanupComplete, 1);
        }
        catch (Exception exception) when (exception is CadDockerRuntimeException or CadProtocolException or
            CadContractException or IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            _cleanupFailure = exception;
            throw;
        }
    }

    private static string DisplaySummary(string value)
    {
        var safe = new string(value.Select(character => char.IsControl(character) ? ' ' : character).ToArray());
        safe = string.Join(' ', safe.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (safe.Length == 0) return "Measurement completed.";
        return safe[..Math.Min(safe.Length, CadContractLimits.MessageLength)];
    }
}

internal sealed class CadRuntimeArtifactReceipt
{
    internal CadRuntimeArtifactReceipt(
        CadArtifactHandle artifact,
        CadRelativePath relativePath,
        CadArtifactFileIdentity identity,
        string sha256,
        long byteLength,
        string mediaType)
    {
        Artifact = artifact ?? throw new CadContractException("required", nameof(artifact));
        RelativePath = relativePath ?? throw new CadContractException("required", nameof(relativePath));
        Identity = identity;
        Sha256 = ContractGuards.Sha256(sha256, nameof(sha256));
        ByteLength = ContractGuards.ByteLength(byteLength, nameof(byteLength));
        MediaType = ContractGuards.RequiredText(mediaType, nameof(mediaType), 128);
    }

    public CadArtifactHandle Artifact { get; }
    internal CadRelativePath RelativePath { get; }
    internal CadArtifactFileIdentity Identity { get; }
    public string Sha256 { get; }
    public long ByteLength { get; }
    public string MediaType { get; }

    internal CadArtifactDescriptor Describe(CadSessionHandle session) =>
        new(session, Artifact, Sha256, ByteLength, MediaType);
}

internal readonly record struct CadArtifactFileIdentity(uint VolumeSerialNumber, ulong FileIndex);

internal static class CadArtifactOpenFileVerifier
{
    private const int MaximumFinalPathLength = 32_767;
    private const uint FileAttributeReparsePoint = 0x00000400;

    internal static CadArtifactFileIdentity Verify(SafeFileHandle handle, string expectedAbsolutePath)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Photon CAD artifact handle verification requires Windows.");
        if (handle.IsInvalid || handle.IsClosed)
            throw new CadContractException("invalid_artifact_handle", nameof(handle));
        if (!GetFileInformationByHandle(handle, out var information))
            throw new IOException("The CAD artifact handle could not be inspected.", new Win32Exception(Marshal.GetLastWin32Error()));
        if ((information.FileAttributes & FileAttributeReparsePoint) != 0)
            throw new CadContractException("reparse_point_rejected", nameof(handle));
        if (information.NumberOfLinks != 1)
            throw new CadContractException("hard_link_rejected", nameof(handle));

        var expected = NormalizeDosPath(expectedAbsolutePath);
        var actual = NormalizeDosPath(FinalPath(handle));
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            throw new CadContractException("artifact_final_path_mismatch", nameof(handle));

        return new CadArtifactFileIdentity(
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
    }

    private static string FinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(1_024);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0)
            throw new IOException("The CAD artifact final path could not be resolved.", new Win32Exception(Marshal.GetLastWin32Error()));
        if (length >= buffer.Capacity)
        {
            if (length > MaximumFinalPathLength)
                throw new CadContractException("artifact_final_path_too_long", nameof(handle));
            buffer = new StringBuilder(checked((int)length + 1));
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity)
                throw new IOException("The CAD artifact final path could not be resolved.", new Win32Exception(Marshal.GetLastWin32Error()));
        }
        return buffer.ToString();
    }

    private static string NormalizeDosPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new CadContractException("invalid_artifact_path", nameof(value));
        var normalized = value.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)
            ? throw new CadContractException("network_path_rejected", nameof(value))
            : value.StartsWith("\\\\?\\", StringComparison.Ordinal)
                ? value[4..]
                : value;
        if (normalized.StartsWith("\\\\", StringComparison.Ordinal) || normalized.StartsWith("\\\\.\\", StringComparison.Ordinal))
            throw new CadContractException("device_path_rejected", nameof(value));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(normalized));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        [Out] StringBuilder path,
        uint pathLength,
        uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }
}
