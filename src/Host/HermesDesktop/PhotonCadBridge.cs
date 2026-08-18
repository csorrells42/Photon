using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PhotonCadFileConversion;
using PhotonCadPreviews;
using PhotonCadProjects;
using PhotonCadProjects.Codec;
using PhotonCadProjects.DesktopAdapter;
using PhotonCadProjects.RuntimeSync;
using PhotonCadProjects.Windows;
using PhotonCadRuntime;
using PhotonCadRuntime.IndustrialProvider;
using PhotonCadRuntime.ManualProvider;
using PhotonCadVerification;

namespace HermesDesktop;

internal sealed record IndustrialMutationRequest(
    string RequestId,
    string SessionId,
    string ProjectId,
    long BaseRevision,
    string EntityId,
    string CapabilityId,
    IReadOnlyDictionary<string, double> NumericInputs,
    IReadOnlyDictionary<string, PhotonCadIndustrialCatalogInputValue?> CatalogInputs,
    AssemblyPlacementInputs? AssemblyInputs,
    AssemblyTransformInputs? AssemblyTransform,
    AssemblyRemovalInputs? AssemblyRemoval,
    ManualMutationInputs? ManualInputs);

internal sealed record ManualMutationInputs(
    PhotonCadManualOperationKind Kind,
    string TargetEntityId,
    string? SeedFeatureId,
    long PatternCount,
    double PatternMeasure,
    string ProfileKind,
    string Plane,
    double WidthMm,
    double HeightMm,
    double RadiusOrDiameterMm,
    double DepthMm,
    double XMm,
    double YMm,
    double ZMm,
    PhotonCadManualMouseSketch? MouseSketch = null,
    string? MouseSketchInput = null,
    bool JoinsExistingSolid = false);

internal sealed record AssemblyPlacementInputs(
    string SourceEntityId,
    string? ParentOccurrenceId,
    IReadOnlyList<double> Transform);

internal sealed record AssemblyRemovalInputs(
    string OccurrenceId,
    string SourceEntityId);

internal sealed record AssemblyTransformInputs(
    string OccurrenceId,
    string SourceEntityId,
    IReadOnlyList<double> Transform);

internal sealed record IndustrialMutationBinding(
    PhotonCadRuntimeSyncRequest Request,
    IPhotonCadSealedMutationProvider Provider,
    IPhotonCadSealedMutationCompensator Compensator);

/// <summary>
/// Thin, typed renderer adapter for the receipt-bound Photon CAD Docker broker.
/// This class never accepts executable paths, process arguments, environment
/// variables, source code, Python, or host filesystem paths from the renderer.
/// </summary>
internal sealed class PhotonCadBridge : IAsyncDisposable
{
    internal const int ProtocolVersion = 1;
    internal const string AcceptedReceiptSha256 = "73774bd9e932624775f281c46377267945f0934b6ae6c9d6d83e13d0702b3687";
    internal const string InstalledAssetRelativePath = "runtime-assets/photon-cad";
    internal const string InstalledIndustrialAssetRelativePath = "runtime-assets/photon-cad-industrial";
    internal const string InstalledManualAssetRelativePath = "runtime-assets/photon-cad-manual";
    internal const string IndustrialEvidenceSelectionFileName = "evidence-selection.json";
    internal const string PreviewResourcePathPrefix = "/api/photon-cad/previews/";
    internal const string IndustrialImageSha256 = "sha256:1f5b532d241cc7139e22e03ba7d3a2773acd58b5fcaacef93e7f0e2d741371e0";
    private const string IndustrialReceiptSha256 = "fd831217e23baf638c78a29326ccd570b1e9e67037b4e1f840da1ae47761d65b";
    private const string GeometryImageSha256 = "33d9c839840115640b08dd3c4142b7f29624329408155fe1484e1d88c3891703";
    private const string GeometryProtocolId = "mcp-2025-06-18";
    private const long MaximumSealedStepBytes = 64L * 1024 * 1024;

    private const int MaximumRequestCharacters = 128;
    private const int MaximumActiveCoreRequests = 8;
    private const int MaximumPendingPreviewRefreshes = 8;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PreviewRefreshMarkerTimeToLive = TimeSpan.FromSeconds(15);

    private readonly string _installRoot;
    private readonly Action<object> _post;
    private readonly Func<CancellationToken, ValueTask<ICadRuntimeBroker>> _brokerFactory;
    private readonly Func<IndustrialMutationRequest, CancellationToken, ValueTask<IndustrialMutationBinding>>? _industrialBindingFactory;
    private readonly Func<PhotonCadCanonicalProject, IReadOnlyList<PhotonCadCommittedVerificationCheck>, CancellationToken, ValueTask<PhotonCadCommittedVerificationResult>>? _industrialVerification;
    private readonly bool _legacyBrokerTestMode;
    private readonly PhotonCadProjectDesktopDispatcher _projects;
    private readonly PhotonCadCanonicalProjectCodecV1? _projectCodec;
    private readonly PhotonCadWindowsDesktopProjectHost? _projectHost;
    private readonly IPhotonCadWindowsFileDialog? _projectDialog;
    private readonly PhotonCadProjectWireProjection? _projectProjection;
    private readonly PhotonCadStepPassthroughConversionAuthority _stepImportConversion = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _bindingGate = new(1, 1);
    private readonly SemaphoreSlim _resetGate = new(1, 1);
    private readonly SemaphoreSlim _coreCapacity = new(MaximumActiveCoreRequests, MaximumActiveCoreRequests);
    private readonly Dictionary<string, RuntimeBinding> _bindings = new(StringComparer.Ordinal);
    private readonly object _brokerLock = new();
    private readonly object _previewLock = new();
    private readonly object _rendererLock = new();
    private readonly PhotonCadPreviewCustody _previewCustody;
    private readonly PhotonCadStepExportHost _stepExportHost;
    private readonly Dictionary<string, PhotonCadPreviewReceipt> _previewReceipts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PhotonCadPreviewContext> _previewResources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingPreviewRefreshMarker> _pendingPreviewRefreshes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingPreviewRefreshMarker> _inflightPreviewRefreshes = new(StringComparer.Ordinal);
    private readonly TimeProvider _previewRefreshTimeProvider;
    private Task<ICadRuntimeBroker>? _brokerTask;
    private Task<PhotonCadIndustrialProviderRuntime>? _industrialRuntimeTask;
    private Task<PhotonCadManualProviderRuntime>? _manualRuntimeTask;
    private TaskCompletionSource? _handlersDrained;
    private string? _runtimeWorkspace;
    private string? _industrialWorkspace;
    private string? _manualWorkspace;
    private string _rendererSessionId = "renderer-unavailable";
    private long _rendererEpoch;
    private long _completedResetEpoch = -1;
    private int _inflightHandlers;
    private bool _rendererAdmissionOpen;
    private string? _activeStepExportRequestId;
    private bool _disposed;

    internal PhotonCadBridge(
        string installRoot,
        Action<object> post,
        Func<CancellationToken, ValueTask<ICadRuntimeBroker>>? brokerFactory = null,
        PhotonCadProjectDesktopDispatcher? projects = null,
        IPhotonCadWindowsFileDialog? projectDialog = null,
        PhotonCadWindowsDesktopProjectHost? runtimeProjectHost = null,
        PhotonCadCanonicalProjectCodecV1? projectCodec = null,
        Uri? workbenchOrigin = null,
        Func<IndustrialMutationRequest, CancellationToken, ValueTask<IndustrialMutationBinding>>? industrialBindingFactory = null,
        Func<PhotonCadCanonicalProject, IReadOnlyList<PhotonCadCommittedVerificationCheck>, CancellationToken, ValueTask<PhotonCadCommittedVerificationResult>>? industrialVerification = null,
        PhotonCadStepExportHost? stepExportHost = null,
        TimeProvider? previewRefreshTimeProvider = null)
    {
        _installRoot = Path.GetFullPath(installRoot ?? throw new ArgumentNullException(nameof(installRoot)));
        ArgumentNullException.ThrowIfNull(post);
        _post = message =>
        {
            lock (_rendererLock)
            {
                if (_disposed || !_rendererAdmissionOpen) return;
                ProcessProjectLifecycleResult(message);
                LogCadResultFrame(message);
                post(message);
            }
        };
        _brokerFactory = brokerFactory ?? ProvisionBrokerAsync;
        _industrialBindingFactory = industrialBindingFactory;
        _industrialVerification = industrialVerification;
        _legacyBrokerTestMode = brokerFactory is not null && industrialBindingFactory is null;
        if ((runtimeProjectHost is null) != (projectCodec is null))
            throw new ArgumentException("The runtime project host and codec must be supplied together.");
        if (projects is null)
        {
            _projectCodec = projectCodec ?? new PhotonCadCanonicalProjectCodecV1();
            _projectDialog = projectDialog ?? new PhotonCadWindowsFileDialog(() => System.Windows.Application.Current?.MainWindow);
            _projectHost = runtimeProjectHost ?? new PhotonCadWindowsDesktopProjectHost(_projectCodec, _projectDialog);
            _projectProjection = new PhotonCadProjectWireProjection(_projectCodec);
            _projects = new PhotonCadProjectDesktopDispatcher(
                _projectHost,
                _projectProjection,
                _post);
        }
        else
        {
            _projects = projects;
            _projectHost = runtimeProjectHost;
            _projectCodec = projectCodec;
            _projectDialog = projectDialog;
            _projectProjection = projectCodec is null ? null : new PhotonCadProjectWireProjection(projectCodec);
        }
        var previewOrigin = workbenchOrigin ?? new Uri("https://127.0.0.1:9119/", UriKind.Absolute);
        _previewCustody = new PhotonCadPreviewCustody(
            _projectCodec ?? new PhotonCadCanonicalProjectCodecV1(),
            new PhotonCadPreviewCustodyOptions(previewOrigin, PreviewResourcePathPrefix));
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _stepExportHost = stepExportHost ?? new PhotonCadStepExportHost(
            Path.Combine(localData, "PhotosAgapeAphthartos", "PhotonCad", "StepExportJournal"));
        _previewRefreshTimeProvider = previewRefreshTimeProvider ?? TimeProvider.System;
    }

    private static void LogCadResultFrame(object message)
    {
        try
        {
            var frame = JsonSerializer.SerializeToElement(message);
            if (!frame.TryGetProperty("type", out var typeElement)
                || SafeDiagnosticToken(typeElement.GetString()) is not { } type
                || !type.StartsWith("photonCad.", StringComparison.Ordinal))
            {
                return;
            }

            string? status = null;
            string? reason = null;
            if (frame.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Object)
            {
                if (value.TryGetProperty("status", out var statusElement)) status = SafeDiagnosticToken(statusElement.GetString());
                if (value.TryGetProperty("reason", out var reasonElement)) reason = SafeDiagnosticToken(reasonElement.GetString());
            }
            else if (frame.TryGetProperty("code", out var codeElement))
            {
                status = "error";
                reason = SafeDiagnosticToken(codeElement.GetString());
            }

            // Project status and reason are already identifier-bounded. Never serialize the full
            // frame: it can carry opaque handles and project metadata that diagnostics do not need.
            DesktopLog.Write($"Photon CAD result frame: type={type}, status={status ?? "none"}, reason={reason ?? "none"}");
        }
        catch
        {
            // Diagnostics cannot affect the host-to-renderer result path.
        }
    }

    private static string? SafeDiagnosticToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128) return null;
        return value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' or ':')
            ? value
            : null;
    }

    internal bool ProjectActionsAvailable => !_disposed && _projects.Readiness.Available;
    internal bool IsDisposed => _disposed;
    internal object ProjectCapabilityAdvertisement()
    {
        var advertisement = JsonSerializer.SerializeToElement(_projects.CapabilityAdvertisement());
        var extended = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in advertisement.EnumerateObject())
            extended[property.Name] = property.Value.Clone();
        extended["runtimeProjectSyncAvailable"] = _projectHost is not null;
        return extended;
    }

    internal bool TryOpenRendererGeneration(long epoch)
    {
        lock (_rendererLock)
        {
            if (_disposed || epoch != _rendererEpoch || epoch != _completedResetEpoch || _inflightHandlers != 0)
                return false;
            _rendererSessionId = $"renderer-{Guid.NewGuid():N}";
            _rendererAdmissionOpen = true;
            return true;
        }
    }

    internal async Task HandleAsync(string type, JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object || !TryBeginRendererHandler()) return;
        try
        {
            if (SafeDiagnosticToken(type) is { } safeType && safeType.StartsWith("photonCad.", StringComparison.Ordinal))
                DesktopLog.Write($"Photon CAD request frame: type={safeType}");
            switch (type)
            {
                case "photonCad.describe":
                    await DescribeAsync(message).ConfigureAwait(false);
                    break;
                case "photonCad.execute":
                    await ExecuteAsync(message).ConfigureAwait(false);
                    break;
                case "photonCad.verify":
                    await VerifyAsync(message).ConfigureAwait(false);
                    break;
                case "photonCad.step.export":
                    await ExportStepAsync(message).ConfigureAwait(false);
                    break;
                case "photonCad.step.import":
                    await ImportStepAsync(message).ConfigureAwait(false);
                    break;
                case "photonCad.cancel":
                    Cancel(message);
                    break;
                case "photonCad.preview.resolve":
                    await ResolvePreviewAsync(message).ConfigureAwait(false);
                    break;
                case "photonCad.preview.hydrate":
                    await HydratePreviewAsync(message).ConfigureAwait(false);
                    break;
                case "photonCad.preview.cancel":
                    Cancel(message);
                    break;
                case "photonCad.release.review":
                    PostReleaseReviewUnavailable(message);
                    break;
                case "photonCad.release.commit":
                    PostReleaseCommitUnavailable(message);
                    break;
                case "photonCad.release.discard":
                    break;
                case "photonCad.project.picker":
                case "photonCad.project.create":
                case "photonCad.project.open":
                case "photonCad.project.reopen":
                case "photonCad.project.refresh":
                case "photonCad.project.save":
                case "photonCad.project.saveAs":
                case "photonCad.project.close":
                case "photonCad.project.cancel":
                    await DispatchProjectAsync(type, message).ConfigureAwait(false);
                    break;
                case "photonCad.commercial.bom.review":
                case "photonCad.commercial.bom.commit":
                case "photonCad.commercial.document.review":
                case "photonCad.commercial.document.approve":
                case "photonCad.commercial.document.commit":
                    PostCommercialUnavailable(type, message);
                    break;
                case "photonCad.commercial.bom.discard":
                case "photonCad.commercial.document.discard":
                    break;
                case "photonCad.commercial.cancel":
                    Cancel(message);
                    break;
            }
        }
        finally { CompleteRendererHandler(); }
    }

    private bool TryBeginRendererHandler()
    {
        lock (_rendererLock)
        {
            if (_disposed || !_rendererAdmissionOpen) return false;
            if (_inflightHandlers == 0)
                _handlersDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _inflightHandlers++;
            return true;
        }
    }

    private void CompleteRendererHandler()
    {
        TaskCompletionSource? drained = null;
        lock (_rendererLock)
        {
            if (_inflightHandlers <= 0) return;
            _inflightHandlers--;
            if (_inflightHandlers == 0)
            {
                drained = _handlersDrained;
                _handlersDrained = null;
            }
        }
        drained?.TrySetResult();
    }

    private Task DispatchProjectAsync(string type, JsonElement message)
    {
        lock (_rendererLock)
        {
            if (_disposed || !_rendererAdmissionOpen) return Task.CompletedTask;
            PendingPreviewRefreshMarker? marker = null;
            var preservesCommittedPreview = type == "photonCad.project.refresh"
                && TryBeginPreviewRefresh(message, out marker);
            if (!preservesCommittedPreview && marker is not null)
                RevokeProjectPreviews(marker.CadSessionId, marker.ProjectId);
            if (!preservesCommittedPreview && type is ("photonCad.project.open" or "photonCad.project.reopen" or "photonCad.project.refresh"
                or "photonCad.project.saveAs" or "photonCad.project.close"))
            {
                if (TryProjectBinding(message, out var cadSessionId, out var projectId))
                    RevokeProjectPreviews(cadSessionId, projectId);
                else
                    RevokeAllPreviews();
            }
            // PhotonCadProjectDesktopDispatcher begins/reserves synchronously before its first await.
            // Starting it while holding the renderer gate means reset either sees and cancels the
            // reservation, or closes admission first and this old-generation request is dropped.
            var dispatch = _projects.HandleAsync(type, message);
            return preservesCommittedPreview && marker is not null
                ? ObservePreviewRefreshAsync(marker.RequestId, dispatch)
                : dispatch;
        }
    }

    private async Task ObservePreviewRefreshAsync(string requestId, Task dispatch)
    {
        try
        {
            await dispatch.ConfigureAwait(false);
        }
        finally
        {
            PendingPreviewRefreshMarker? abandoned = null;
            lock (_previewLock)
            {
                if (_inflightPreviewRefreshes.Remove(requestId, out var marker)) abandoned = marker;
            }
            if (abandoned is not null) RevokeProjectPreviews(abandoned.CadSessionId, abandoned.ProjectId);
        }
    }

    private async Task DescribeAsync(JsonElement message)
    {
        if (!TryEnvelope(message, out var requestId))
        {
            if (TryWireEnvelope(message, out requestId)) PostError(requestId, "invalid_contract_version", retryable: false);
            return;
        }
        await RunAsync(requestId, async cancellationToken =>
        {
            if (!_legacyBrokerTestMode)
            {
                try
                {
                    var runtime = await EnsureIndustrialProviderReadyAsync(cancellationToken).ConfigureAwait(false);
                    var providerCatalog = await runtime.GetCatalogAsync(cancellationToken).ConfigureAwait(false);
                    IReadOnlyList<PhotonCadManualCapability> manualCatalog = [];
                    try
                    {
                        var manualRuntime = await EnsureManualProviderReadyAsync(cancellationToken).ConfigureAwait(false);
                        manualCatalog = manualRuntime.GetCatalog();
                    }
                    catch (Exception exception) when (IsIndustrialAvailabilityFailure(exception))
                    {
                        DesktopLog.Write($"Photon CAD manual runtime unavailable: {SafeAvailabilityDiagnostic(exception)}");
                    }
                    _post(new
                    {
                        type = "photonCad.describe.result",
                        version = ProtocolVersion,
                        requestId,
                        value = CadWireProjection.Description(IndustrialRendererDescription(providerCatalog, manualCatalog)),
                    });
                }
                catch (Exception exception) when (IsIndustrialAvailabilityFailure(exception))
                {
                    DesktopLog.Write($"Photon CAD industrial runtime unavailable: {SafeAvailabilityDiagnostic(exception)}");
                    _post(new
                    {
                        type = "photonCad.describe.result",
                        version = ProtocolVersion,
                        requestId,
                        value = CadWireProjection.Description(new CadRuntimeDescription(
                            CadRuntimeAvailability.NotProvisioned,
                            "industrial_runtime_unavailable",
                            "The exact verified industrial CAD runtime is unavailable.")),
                    });
                }
                return;
            }
            var broker = await GetBrokerAsync(cancellationToken).ConfigureAwait(false);
            _post(new
            {
                type = "photonCad.describe.result",
                version = ProtocolVersion,
                requestId,
                value = CadWireProjection.Description(RendererDescription(broker)),
            });
        }).ConfigureAwait(false);
    }

    private async Task ExecuteAsync(JsonElement message)
    {
        if (!TryEnvelope(message, out var requestId))
        {
            if (TryWireEnvelope(message, out requestId)) PostError(requestId, "invalid_contract_version", retryable: false);
            return;
        }
        await RunAsync(requestId, async cancellationToken =>
        {
            if (!TryOperationEnvelope(message, out var external, out var mode, out var capabilityId,
                    out var inputsElement, out var targetsElement))
            {
                PostError(requestId, "invalid_request", retryable: false);
                return;
            }

            if (!_legacyBrokerTestMode)
            {
                await ExecuteIndustrialAsync(external, mode, capabilityId, inputsElement, targetsElement, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var broker = await GetBrokerAsync(cancellationToken).ConfigureAwait(false);
            var description = RendererDescription(broker);
            if (description.Availability != CadRuntimeAvailability.Ready)
            {
                PostOperationUnavailable(external, description.ReasonCode);
                return;
            }
            if (_projectHost is null || _projectCodec is null
                || capabilityId is not (CadPinnedCapabilityCatalog.BoxCapabilityId or CadPinnedCapabilityCatalog.CylinderCapabilityId)
                || mode != CadOperationMode.Scratch
                || targetsElement.GetArrayLength() != 0)
            {
                PostOperationUnavailable(external, "runtime_persisted_operation_unavailable");
                return;
            }
            var binding = await GetOrCreateBindingAsync(broker, external, cancellationToken).ConfigureAwait(false);
            if (binding is null)
            {
                PostOperationUnavailable(external, "runtime_session_unavailable");
                return;
            }
            CadOperationRequest createRequest;
            try
            {
                createRequest = CreateOperationRequest(
                    broker.Describe(),
                    binding,
                    external,
                    mode,
                    capabilityId,
                    inputsElement,
                    targetsElement);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or JsonException or OverflowException)
            {
                PostError(requestId, SafeErrorCode(exception), retryable: false);
                return;
            }
            var logicalEntityId = $"entity-{Guid.NewGuid():N}";
            var syncRequest = new PhotonCadRuntimeSyncRequest(
                external.RequestId,
                external.SessionId,
                external.ProjectId,
                external.Revision,
                capabilityId,
                PhotonCadOperationModeV1.Scratch,
                createRequest.Inputs.Select(ToSyncInput),
                [logicalEntityId]);
            var provider = new GeometryRuntimeMutationProvider(
                this,
                broker,
                binding,
                broker.Describe(),
                createRequest,
                logicalEntityId);
            try
            {
                var mapper = new PhotonCadRuntimeCanonicalMapperV1(_projectCodec);
                var registration = _projectHost.CreateRuntimeProjectSynchronizer(provider, provider, mapper);
                var result = await _projectHost.ApplyRuntimeMutationAsync(registration, syncRequest, cancellationToken).ConfigureAwait(false);
                provider.CommitSucceeded(result.SavedProject.Revision);
                _post(new
                {
                    type = "photonCad.execute.result",
                    version = ProtocolVersion,
                    value = ExternalCommittedOperationResult(result, external, _projectCodec),
                });
            }
            catch
            {
                await EvictBindingAsync(broker, binding, "runtime_mutation_failed").ConfigureAwait(false);
                throw;
            }
        }).ConfigureAwait(false);
    }

    private async Task VerifyAsync(JsonElement message)
    {
        if (!TryEnvelope(message, out var requestId))
        {
            if (TryWireEnvelope(message, out requestId)) PostError(requestId, "invalid_contract_version", retryable: false);
            return;
        }
        await RunAsync(requestId, async cancellationToken =>
        {
            if (!TryVerificationEnvelope(message, out var external, out var checks))
            {
                PostError(requestId, "invalid_request", retryable: false);
                return;
            }
            if (!_legacyBrokerTestMode)
            {
                await VerifyCommittedIndustrialAsync(external, checks, cancellationToken).ConfigureAwait(false);
                return;
            }
            var broker = await GetBrokerAsync(cancellationToken).ConfigureAwait(false);
            var description = RendererDescription(broker);
            if (description.Availability != CadRuntimeAvailability.Ready)
            {
                PostVerificationUnavailable(external, description.ReasonCode);
                return;
            }
            var binding = await GetOrCreateBindingAsync(broker, external, cancellationToken).ConfigureAwait(false);
            if (binding is null)
            {
                PostVerificationUnavailable(external, "runtime_session_unavailable");
                return;
            }
            CadVerificationRequest request;
            try
            {
                request = new CadVerificationRequest(
                    new CadRequestId(external.RequestId),
                    binding.Session,
                    binding.Project,
                    new CadRevision(external.Revision),
                    checks);
            }
            catch (ArgumentException exception)
            {
                PostError(requestId, SafeErrorCode(exception), retryable: false);
                return;
            }
            var result = await broker.VerifyAsync(request, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                PostVerificationUnavailable(external, result.Error?.Code ?? "runtime_verification_failed");
                return;
            }
            var value = result.Value!;
            _post(new
            {
                type = "photonCad.verify.result",
                version = ProtocolVersion,
                value = new
                {
                    contractVersion = ProtocolVersion,
                    requestId = external.RequestId,
                    projectId = external.ProjectId,
                    revision = value.Revision.Value,
                    status = value.Status switch
                    {
                        CadVerificationStatus.Passed => "passed",
                        CadVerificationStatus.Failed => "failed",
                        _ => "unavailable",
                    },
                    stale = value.Stale,
                    issues = value.Issues.Select(ExternalIssue).ToArray(),
                    measuredAtUtc = Utc(value.MeasuredAtUtc),
                },
            });
        }).ConfigureAwait(false);
    }

    private async Task VerifyCommittedIndustrialAsync(
        ExternalRuntimeRequest external,
        IReadOnlyList<CadVerificationCheck> checks,
        CancellationToken cancellationToken)
    {
        if (_projectHost is null)
        {
            PostVerificationUnavailable(external, "committed_project_host_unavailable");
            return;
        }

        try
        {
            var readback = await _projectHost.ResolveCommittedProjectReadbackAsync(
                external.RequestId,
                external.SessionId,
                external.ProjectId,
                external.Revision,
                cancellationToken).ConfigureAwait(false);
            var project = readback.Project;
            var canonical = new PhotonCadCanonicalVerifier().Verify(
                project.CanonicalBytes,
                new PhotonCadVerificationRequest(
                    readback.ByteLength,
                    readback.StorageDigest,
                    project.ContentDigest,
                    project.BomDigest),
                cancellationToken);
            if (!canonical.Verified
                || !StringComparer.Ordinal.Equals(canonical.ProjectId, external.ProjectId)
                || canonical.Revision != external.Revision)
            {
                var failedReason = canonical.Checks.FirstOrDefault(value => value.Status == PhotonCadVerificationStatus.Failed)?.Reason
                    ?? "canonical_verification_failed";
                PostVerificationFailed(external, failedReason);
                return;
            }
            var industrialChecks = checks.Select(value => value switch
            {
                CadVerificationCheck.ValidSolids => PhotonCadCommittedVerificationCheck.ValidSolids,
                CadVerificationCheck.Interference => PhotonCadCommittedVerificationCheck.Interference,
                CadVerificationCheck.Dimensions => PhotonCadCommittedVerificationCheck.Dimensions,
                CadVerificationCheck.AssemblyStructure => PhotonCadCommittedVerificationCheck.AssemblyStructure,
                CadVerificationCheck.ExportReadiness => PhotonCadCommittedVerificationCheck.ExportReadiness,
                _ => throw new InvalidOperationException("committed_verification_check_invalid"),
            }).ToArray();
            var result = _industrialVerification is not null
                ? await _industrialVerification(project, industrialChecks, cancellationToken).ConfigureAwait(false)
                : await (await EnsureIndustrialProviderReadyAsync(cancellationToken).ConfigureAwait(false))
                    .VerifyCommittedAsync(project, industrialChecks, cancellationToken).ConfigureAwait(false);
            if (result.Revision != external.Revision)
            {
                PostVerificationUnavailable(external, "committed_project_revision_mismatch");
                return;
            }
            if (!result.Available)
            {
                PostVerificationUnavailable(external, result.Reason);
                return;
            }
            _post(new
            {
                type = "photonCad.verify.result",
                version = ProtocolVersion,
                value = new
                {
                    contractVersion = ProtocolVersion,
                    requestId = external.RequestId,
                    projectId = external.ProjectId,
                    revision = external.Revision,
                    status = result.Passed ? "passed" : "failed",
                    stale = false,
                    issues = result.Passed
                        ? Array.Empty<object>()
                        : new[]
                        {
                            new
                            {
                                code = SafeReason(result.Reason),
                                severity = "error",
                                message = "The committed CAD project failed the selected verification checks.",
                                entityIds = Array.Empty<string>(),
                            },
                        },
                    measuredAtUtc = Utc(result.MeasuredAtUtc),
                },
            });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is PhotonCadProjectException or ArgumentException or InvalidOperationException
            or IOException or UnauthorizedAccessException or JsonException)
        {
            PostVerificationUnavailable(external, "committed_verification_unavailable");
        }
    }

    private async Task ExportStepAsync(JsonElement message)
    {
        if (!TryEnvelope(message, out var requestId)
            || message.EnumerateObject().Count() != 9
            || !TryIdentifier(message, "sessionId", out var sessionId)
            || !TryIdentifier(message, "projectId", out var projectId)
            || !TryRevision(message, "revision", out var revision)
            || !TryIdentifier(message, "contentDigest", out var contentDigest)
            || !TryIdentifier(message, "entityId", out var entityId)
            || !IsSha256Digest(contentDigest))
        {
            if (TryWireEnvelope(message, out requestId)) PostError(requestId, "invalid-step-export-request", retryable: false);
            return;
        }

        CancellationTokenSource? prior = null;
        lock (_rendererLock)
        {
            if (_activeStepExportRequestId is { } priorId) _active.TryGetValue(priorId, out prior);
            _activeStepExportRequestId = requestId;
        }
        prior?.Cancel();
        try
        {
            await RunAsync(requestId, async cancellationToken =>
            {
                if (_projectHost is null || _projectCodec is null)
                {
                    PostStepExportResult(requestId, "unavailable", "committed-project-host-unavailable", projectId, revision);
                    return;
                }
                try
                {
                    var project = await _projectHost.ResolveCommittedProjectAsync(requestId, sessionId, projectId, revision, cancellationToken).ConfigureAwait(false);
                    if (project.Dirty || !FixedDigestEquals(project.ContentDigest, contentDigest))
                    {
                        PostStepExportResult(requestId, "rejected", "committed-project-binding-mismatch", projectId, revision);
                        return;
                    }
                    var state = _projectCodec.Inspect(project);
                    var entity = state.Entities.SingleOrDefault(value => StringComparer.Ordinal.Equals(value.Id, entityId));
                    if (entity is null || entity.Kind is not (PhotonCadEntityKindV1.Body or PhotonCadEntityKindV1.Part))
                    {
                        PostStepExportResult(requestId, "rejected", "step-export-entity-unavailable", projectId, revision);
                        return;
                    }
                    var steps = state.Artifacts.Where(value => value.Role == PhotonCadArtifactRoleV1.AuthoritativeGeometry
                            && value.Kind == PhotonCadArtifactKindV1.Step
                            && StringComparer.Ordinal.Equals(value.OwnerEntityId, entityId))
                        .Take(2).ToArray();
                    if (steps.Length != 1)
                    {
                        PostStepExportResult(requestId, "rejected", "step-export-artifact-ambiguous", projectId, revision);
                        return;
                    }
                    string rendererSession;
                    lock (_rendererLock) rendererSession = _rendererSessionId;
                    var receipt = await _stepExportHost.ExportAsync(rendererSession, sessionId, projectId, revision,
                        entityId, steps[0].Content, steps[0].Digest, cancellationToken).ConfigureAwait(false);
                    if (receipt is null)
                    {
                        PostStepExportResult(requestId, "cancelled", "native-picker-cancelled", projectId, revision);
                        return;
                    }
                    _post(new
                    {
                        type = "photonCad.step.export.result",
                        version = ProtocolVersion,
                        value = new
                        {
                            contractVersion = ProtocolVersion,
                            requestId,
                            projectId,
                            revision,
                            status = "committed",
                            reason = "step-export-committed",
                            entityId,
                            contentDigest = receipt.ContentDigest,
                            byteLength = receipt.ByteLength,
                            destinationLabel = receipt.DestinationLabel,
                        },
                    });
                }
                catch (OperationCanceledException)
                {
                    PostStepExportResult(requestId, "cancelled", "step-export-cancelled", projectId, revision);
                }
                catch (Exception exception) when (exception is PhotonCadProjectException or PhotonCadArtifacts.PhotonCadArtifactException
                    or InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    PostStepExportResult(requestId, "rejected", "step-export-rejected", projectId, revision);
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            lock (_rendererLock)
                if (StringComparer.Ordinal.Equals(_activeStepExportRequestId, requestId)) _activeStepExportRequestId = null;
        }
    }

    private void PostStepExportResult(string requestId, string status, string reason, string? projectId, long revision)
    {
        if (projectId is null) return;
        _post(new
        {
            type = "photonCad.step.export.result",
            version = ProtocolVersion,
            value = new { contractVersion = ProtocolVersion, requestId, projectId, revision, status, reason },
        });
    }

    private async Task ImportStepAsync(JsonElement message)
    {
        if (!TryEnvelope(message, out var requestId) || message.EnumerateObject().Count() != 4)
        {
            if (TryWireEnvelope(message, out requestId))
                PostStepImportResult(requestId, "rejected", "invalid-step-import-request");
            return;
        }

        await RunAsync(requestId, async cancellationToken =>
        {
            if (_projectDialog is null || _projectHost is null || _projectCodec is null || _projectProjection is null)
            {
                PostStepImportResult(requestId, "unavailable", "step-import-host-unavailable");
                return;
            }

            PhotonCadWindowsDialogResult sourceSelection;
            try
            {
                sourceSelection = _projectDialog.Show("import-step");
            }
            catch (Exception exception) when (exception is InvalidOperationException or PhotonCadProjectException)
            {
                PostStepImportResult(requestId, "unavailable", "step-source-picker-unavailable");
                return;
            }
            if (!sourceSelection.Accepted)
            {
                PostStepImportResult(requestId, "cancelled", "native-picker-cancelled");
                return;
            }
            if (string.IsNullOrWhiteSpace(sourceSelection.ExactPath))
            {
                PostStepImportResult(requestId, "rejected", "step-source-picker-invalid");
                return;
            }

            PhotonCadConversionResult converted;
            var displayName = SafeImportedStepDisplayName(sourceSelection.ExactPath);
            try
            {
                await using var source = new FileStream(
                    sourceSelection.ExactPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                converted = await _stepImportConversion.ConvertAsync(
                    Path.GetFileName(sourceSelection.ExactPath),
                    source,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                PostStepImportResult(requestId, "cancelled", "step-import-cancelled");
                return;
            }
            catch (InvalidDataException exception)
            {
                var reason = PhotonCadConversionReason(exception);
                PostStepImportResult(
                    requestId,
                    reason is "autodesk-inventor-authority-unavailable" or "glb-import-authority-unavailable"
                        ? "unavailable"
                        : "rejected",
                    reason);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or NotSupportedException or ArgumentException)
            {
                PostStepImportResult(requestId, "rejected", "step-source-invalid");
                return;
            }

            PhotonCadIndustrialProviderRuntime runtime;
            try
            {
                runtime = await EnsureIndustrialProviderReadyAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsIndustrialAvailabilityFailure(exception))
            {
                PostStepImportResult(requestId, "unavailable", "industrial-runtime-unavailable");
                return;
            }

            PhotonCadDesktopProjectPickerOutcome destination;
            try
            {
                destination = await _projectHost.ChooseWorkspaceAsync(
                    $"step-import-pick-{Guid.NewGuid():N}",
                    "new",
                    displayName,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                PostStepImportResult(requestId, "cancelled", "step-import-cancelled");
                return;
            }
            catch (Exception exception) when (exception is PhotonCadProjectException or InvalidOperationException)
            {
                PostStepImportResult(requestId, "unavailable", "step-destination-picker-unavailable");
                return;
            }
            if (destination.Status != PhotonCadNativeServiceStatus.Selected || destination.Workspace is null)
            {
                var status = destination.Status == PhotonCadNativeServiceStatus.Cancelled ? "cancelled" :
                    destination.Status == PhotonCadNativeServiceStatus.Unavailable ? "unavailable" : "rejected";
                PostStepImportResult(requestId, status, destination.Reason);
                return;
            }

            try
            {
                var created = await _projectHost.CreateProjectAsync(
                    new PhotonCadProjectCreateRequest(
                        $"step-import-create-{Guid.NewGuid():N}",
                        destination.Workspace.WorkspaceHandle,
                        displayName,
                        PhotonCadProjectUnit.Millimeter),
                    cancellationToken).ConfigureAwait(false);
                var digest = $"sha256:{converted.Artifact.Digest}";
                var evidence = new PhotonCadProviderEvidence(
                    converted.ContainsAssemblyConstructs ? PhotonCadBackendV1.Assembly : PhotonCadBackendV1.Geometry,
                    converted.ContainsAssemblyConstructs ? "photon.cad.step.assembly.import.v1" : "photon.cad.step.import.v1",
                    converted.ContainsAssemblyConstructs ? ["iso-10303-21-xcaf"] : ["iso-10303-21"],
                    digest,
                    converted.ContainsAssemblyConstructs ? "external.step.assembly.v1" : "external.step.part21.v1",
                    digest,
                    digest,
                    digest,
                    digest,
                    new PhotonCadSourceIdentityV1("user-supplied-step", "part21", digest, "user-supplied"));
                var bound = converted.ContainsAssemblyConstructs
                    ? await runtime.BindImportedStepAssemblyAsync(
                        requestId,
                        created.Snapshot.SessionId,
                        created.Snapshot.ProjectId,
                        created.Snapshot.Revision,
                        converted.Artifact.Content,
                        digest,
                        evidence,
                        cancellationToken).ConfigureAwait(false)
                    : runtime.BindImportedStepPart(
                        requestId,
                        created.Snapshot.SessionId,
                        created.Snapshot.ProjectId,
                        created.Snapshot.Revision,
                        $"imported-{Guid.NewGuid():N}",
                        $"IMPORT-{converted.Artifact.Digest[..12].ToUpperInvariant()}",
                        displayName,
                        converted.Artifact.Content,
                        digest,
                        evidence);
                var mapper = new PhotonCadRuntimeCanonicalMapperV1(_projectCodec);
                var registration = _projectHost.CreateRuntimeProjectSynchronizer(bound.Provider, bound.Compensator, mapper);
                var result = await _projectHost.ApplyRuntimeMutationAsync(registration, bound.Request, cancellationToken).ConfigureAwait(false);
                var committed = new PhotonCadProjectDocument(
                    created.WorkspaceHandle,
                    created.ProjectHandle,
                    result.SavedProject,
                    result.SavedProject.ContentDigest,
                    result.SavedProject.Revision,
                    created.OpenedAtUtc);
                _post(new
                {
                    type = "photonCad.step.import.result",
                    version = ProtocolVersion,
                    value = new
                    {
                        contractVersion = ProtocolVersion,
                        requestId,
                        status = "opened",
                        reason = "step-import-committed",
                        document = _projectProjection.Document(committed),
                    },
                });
            }
            catch (OperationCanceledException)
            {
                PostStepImportResult(requestId, "cancelled", "step-import-cancelled");
            }
            catch (Exception exception) when (exception is PhotonCadProjectException or PhotonCadRuntimeSyncException
                or InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                PostStepImportResult(requestId, "rejected", "step-import-commit-failed");
            }
        }).ConfigureAwait(false);
    }

    private void PostStepImportResult(string requestId, string status, string reason) => _post(new
    {
        type = "photonCad.step.import.result",
        version = ProtocolVersion,
        value = new { contractVersion = ProtocolVersion, requestId, status, reason },
    });

    private static string PhotonCadConversionReason(InvalidDataException exception)
    {
        var separator = exception.Message.IndexOf(':');
        var code = separator < 0 ? exception.Message : exception.Message[..separator];
        return code switch
        {
            "autodesk-inventor-authority-unavailable" => code,
            "glb-import-authority-unavailable" => code,
            _ => "step-source-invalid",
        };
    }

    private static string SafeImportedStepDisplayName(string exactPath)
    {
        var value = Path.GetFileNameWithoutExtension(exactPath).Trim();
        var safe = new string(value.Take(120)
            .Select(character => char.IsControl(character) || character is '\\' or '/' ? '-' : character)
            .ToArray()).Trim().TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(safe) || safe is "." or ".." ? "Imported STEP part" : safe;
    }

    private async Task ExecuteIndustrialAsync(
        ExternalRuntimeRequest external,
        CadOperationMode mode,
        string capabilityId,
        JsonElement inputsElement,
        JsonElement targetsElement,
        CancellationToken cancellationToken)
    {
        if (_projectHost is null || _projectCodec is null || mode != CadOperationMode.Scratch)
        {
            PostOperationUnavailable(external, "industrial_persisted_operation_unavailable");
            return;
        }

        IReadOnlyDictionary<string, double> numericInputs = new Dictionary<string, double>(StringComparer.Ordinal);
        IReadOnlyDictionary<string, PhotonCadIndustrialCatalogInputValue?> catalogInputs =
            new Dictionary<string, PhotonCadIndustrialCatalogInputValue?>(StringComparer.Ordinal);
        AssemblyPlacementInputs? assemblyInputs = null;
        AssemblyTransformInputs? assemblyTransform = null;
        AssemblyRemovalInputs? assemblyRemoval = null;
        ManualMutationInputs? manualInputs = null;
        try
        {
            if (capabilityId == PhotonCadAssemblyContract.PlaceCapabilityId)
            {
                if (targetsElement.GetArrayLength() != 0) throw new ArgumentException("targets_invalid", nameof(targetsElement));
                assemblyInputs = ParseAssemblyPlacementInputs(inputsElement);
            }
            else if (capabilityId == PhotonCadAssemblyContract.TransformCapabilityId)
            {
                if (targetsElement.GetArrayLength() != 1)
                    throw new ArgumentException("assembly_transform_shape_invalid", nameof(targetsElement));
                var target = targetsElement.EnumerateArray().Single();
                var occurrenceId = target.ValueKind == JsonValueKind.String ? target.GetString() : null;
                if (!IsIdentifier(occurrenceId)) throw new ArgumentException("assembly_transform_target_invalid", nameof(targetsElement));
                var project = await _projectHost.ResolveCommittedProjectAsync(
                    $"assembly-transform-resolve-{Guid.NewGuid():N}",
                    external.SessionId,
                    external.ProjectId,
                    external.Revision,
                    cancellationToken).ConfigureAwait(false);
                var occurrence = _projectCodec.Inspect(project).Occurrences
                    .SingleOrDefault(value => StringComparer.Ordinal.Equals(value.OccurrenceId, occurrenceId))
                    ?? throw new ArgumentException("assembly_transform_target_missing", nameof(targetsElement));
                assemblyTransform = new AssemblyTransformInputs(
                    occurrence.OccurrenceId,
                    occurrence.SourceEntityId,
                    ParseAssemblyTransformInputs(inputsElement));
            }
            else if (capabilityId == PhotonCadAssemblyContract.RemoveCapabilityId)
            {
                if (inputsElement.EnumerateObject().Any() || targetsElement.GetArrayLength() != 1)
                    throw new ArgumentException("assembly_remove_shape_invalid", nameof(targetsElement));
                var target = targetsElement.EnumerateArray().Single();
                var occurrenceId = target.ValueKind == JsonValueKind.String ? target.GetString() : null;
                if (!IsIdentifier(occurrenceId)) throw new ArgumentException("assembly_remove_target_invalid", nameof(targetsElement));
                var project = await _projectHost.ResolveCommittedProjectAsync(
                    $"assembly-remove-resolve-{Guid.NewGuid():N}",
                    external.SessionId,
                    external.ProjectId,
                    external.Revision,
                    cancellationToken).ConfigureAwait(false);
                var occurrence = _projectCodec.Inspect(project).Occurrences
                    .SingleOrDefault(value => StringComparer.Ordinal.Equals(value.OccurrenceId, occurrenceId))
                    ?? throw new ArgumentException("assembly_remove_target_missing", nameof(targetsElement));
                assemblyRemoval = new AssemblyRemovalInputs(occurrence.OccurrenceId, occurrence.SourceEntityId);
            }
            else if (capabilityId is CadPinnedCapabilityCatalog.BoxCapabilityId or CadPinnedCapabilityCatalog.CylinderCapabilityId)
            {
                if (targetsElement.GetArrayLength() != 0) throw new ArgumentException("targets_invalid", nameof(targetsElement));
                numericInputs = ParseIndustrialInputs(capabilityId, inputsElement);
            }
            else if (IsManualCapability(capabilityId))
            {
                manualInputs = await ParseManualMutationInputsAsync(
                    external,
                    capabilityId,
                    inputsElement,
                    targetsElement,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (targetsElement.GetArrayLength() != 0) throw new ArgumentException("targets_invalid", nameof(targetsElement));
                var runtime = await EnsureIndustrialProviderReadyAsync(cancellationToken).ConfigureAwait(false);
                var catalog = await runtime.GetCatalogAsync(cancellationToken).ConfigureAwait(false);
                var item = catalog.Items.SingleOrDefault(candidate => StringComparer.Ordinal.Equals(candidate.CapabilityId, capabilityId))
                    ?? throw new ArgumentException("industrial_catalog_capability_not_found", nameof(capabilityId));
                catalogInputs = ParseIndustrialCatalogInputs(item, inputsElement);
            }
        }
        catch (PhotonCadProjectException)
        {
            PostOperationUnavailable(external, "industrial_project_binding_unavailable");
            return;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or JsonException)
        {
            PostError(external.RequestId, "invalid_request", retryable: false);
            return;
        }

        var request = new IndustrialMutationRequest(
            external.RequestId,
            external.SessionId,
            external.ProjectId,
            external.Revision,
            $"entity-{Guid.NewGuid():N}",
            capabilityId,
            numericInputs,
            catalogInputs,
            assemblyInputs,
            assemblyTransform,
            assemblyRemoval,
            manualInputs);
        IndustrialMutationBinding binding;
        try { binding = await CreateIndustrialBindingAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (IsIndustrialAvailabilityFailure(exception))
        {
            PostOperationUnavailable(external, "industrial_runtime_unavailable");
            return;
        }

        var mapper = new PhotonCadRuntimeCanonicalMapperV1(_projectCodec);
        IPhotonCadSealedMutationProvider provider = binding.Provider;
        IPhotonCadSealedMutationCompensator compensator = binding.Compensator;
        if (assemblyInputs is not null)
        {
            var guarded = new AssemblyPlacementGuardProvider(binding.Request, assemblyInputs, provider, compensator);
            provider = guarded;
            compensator = guarded;
        }
        else if (assemblyTransform is not null)
        {
            var guarded = new AssemblyTransformGuardProvider(binding.Request, assemblyTransform, provider, compensator);
            provider = guarded;
            compensator = guarded;
        }
        else if (assemblyRemoval is not null)
        {
            var guarded = new AssemblyRemovalGuardProvider(binding.Request, assemblyRemoval, provider, compensator);
            provider = guarded;
            compensator = guarded;
        }
        var registration = _projectHost.CreateRuntimeProjectSynchronizer(provider, compensator, mapper);
        var result = await _projectHost.ApplyRuntimeMutationAsync(registration, binding.Request, cancellationToken).ConfigureAwait(false);
        PhotonCadPreviewReceipt? preview = null;
        try
        {
            var previewContext = CurrentPreviewContext(result.SavedProject);
            preview = _previewCustody.SealCommitted(new PhotonCadCommittedPreviewReadback(previewContext, result.SavedProject));
            lock (_previewLock)
            {
                _previewReceipts[preview.PreviewId] = preview;
                foreach (var stale in _previewReceipts.Where(pair => pair.Value.Context.RendererGeneration == preview.Context.RendererGeneration
                    && StringComparer.Ordinal.Equals(pair.Value.Context.RendererSessionId, preview.Context.RendererSessionId)
                    && StringComparer.Ordinal.Equals(pair.Value.Context.CadSessionId, preview.Context.CadSessionId)
                    && StringComparer.Ordinal.Equals(pair.Value.Context.ProjectId, preview.Context.ProjectId)
                    && !StringComparer.Ordinal.Equals(pair.Key, preview.PreviewId)).Select(pair => pair.Key).ToArray())
                    _previewReceipts.Remove(stale);
                TryCreatePreviewRefreshMarker(preview.PreviewId, preview.Context, result.SavedProject.ContentDigest);
            }
        }
        catch (Exception exception) when (exception is PhotonCadPreviewException or InvalidOperationException or ArgumentException)
        {
            DesktopLog.Write($"Photon CAD committed preview remained unavailable: {exception.GetType().Name}");
        }
        _post(new
        {
            type = "photonCad.execute.result",
            version = ProtocolVersion,
            value = ExternalCommittedOperationResult(result, external, _projectCodec, preview),
        });
    }

    private static AssemblyPlacementInputs ParseAssemblyPlacementInputs(JsonElement inputs)
    {
        if (inputs.ValueKind != JsonValueKind.Object) throw new ArgumentException("inputs_invalid", nameof(inputs));
        var expected = new[] { "parentOccurrenceId", "rotationDegrees", "sourceEntityId", "translation" };
        var actual = inputs.EnumerateObject().Select(property => property.Name).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal)) throw new ArgumentException("inputs_invalid", nameof(inputs));
        if (inputs.GetProperty("sourceEntityId") is not { ValueKind: JsonValueKind.String } source
            || source.GetString() is not { } sourceEntityId)
            throw new ArgumentException("inputs_invalid", nameof(inputs));
        var parentElement = inputs.GetProperty("parentOccurrenceId");
        var parentOccurrenceId = parentElement.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => parentElement.GetString(),
            _ => throw new ArgumentException("inputs_invalid", nameof(inputs)),
        };
        var translation = ParseBoundedVector(inputs.GetProperty("translation"), 1_000_000);
        var rotation = ParseBoundedVector(inputs.GetProperty("rotationDegrees"), 360);
        return new AssemblyPlacementInputs(
            sourceEntityId,
            parentOccurrenceId,
            RigidTransform(translation, rotation));
    }

    private static IReadOnlyList<double> ParseAssemblyTransformInputs(JsonElement inputs)
    {
        if (inputs.ValueKind != JsonValueKind.Object) throw new ArgumentException("inputs_invalid", nameof(inputs));
        var expected = new[] { "rotationDegrees", "translation" };
        var actual = inputs.EnumerateObject().Select(property => property.Name).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal)) throw new ArgumentException("inputs_invalid", nameof(inputs));
        var translation = ParseBoundedVector(inputs.GetProperty("translation"), 1_000_000);
        var rotation = ParseBoundedVector(inputs.GetProperty("rotationDegrees"), 360);
        return RigidTransform(translation, rotation);
    }

    private static CadVector3 ParseBoundedVector(JsonElement value, double maximumAbsolute)
    {
        if (value.ValueKind != JsonValueKind.Object
            || value.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal)
                .SequenceEqual(new[] { "x", "y", "z" }, StringComparer.Ordinal) is false)
            throw new ArgumentException("inputs_invalid", nameof(value));
        var x = RequiredDouble(value, "x");
        var y = RequiredDouble(value, "y");
        var z = RequiredDouble(value, "z");
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)
            || Math.Abs(x) > maximumAbsolute || Math.Abs(y) > maximumAbsolute || Math.Abs(z) > maximumAbsolute)
            throw new ArgumentException("inputs_invalid", nameof(value));
        return new CadVector3(x, y, z);
    }

    private static IReadOnlyList<double> RigidTransform(CadVector3 translation, CadVector3 rotationDegrees)
    {
        var x = rotationDegrees.X * Math.PI / 180d;
        var y = rotationDegrees.Y * Math.PI / 180d;
        var z = rotationDegrees.Z * Math.PI / 180d;
        var sx = Math.Sin(x); var cx = Math.Cos(x);
        var sy = Math.Sin(y); var cy = Math.Cos(y);
        var sz = Math.Sin(z); var cz = Math.Cos(z);
        return Array.AsReadOnly(new[]
        {
            cz * cy, (cz * sy * sx) - (sz * cx), (cz * sy * cx) + (sz * sx), translation.X,
            sz * cy, (sz * sy * sx) + (cz * cx), (sz * sy * cx) - (cz * sx), translation.Y,
            -sy, cy * sx, cy * cx, translation.Z,
            0d, 0d, 0d, 1d,
        });
    }

    private static IReadOnlyDictionary<string, double> ParseIndustrialInputs(string capabilityId, JsonElement inputs)
    {
        if (inputs.ValueKind != JsonValueKind.Object) throw new ArgumentException("inputs_invalid", nameof(inputs));
        var expected = capabilityId == CadPinnedCapabilityCatalog.BoxCapabilityId
            ? new[] { "height", "length", "width" }
            : new[] { "height", "radius" };
        var actual = inputs.EnumerateObject().Select(property => property.Name).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal)) throw new ArgumentException("inputs_invalid", nameof(inputs));
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var name in expected)
        {
            var element = inputs.GetProperty(name);
            if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out var value)
                || !double.IsFinite(value) || value < 0.001 || value > 1_000_000)
                throw new ArgumentException("inputs_invalid", nameof(inputs));
            result.Add(name, value);
        }
        return result;
    }

    private static IReadOnlyDictionary<string, PhotonCadIndustrialCatalogInputValue?> ParseIndustrialCatalogInputs(
        PhotonCadIndustrialCatalogItem item,
        JsonElement inputs)
    {
        if (inputs.ValueKind != JsonValueKind.Object) throw new ArgumentException("inputs_invalid", nameof(inputs));
        var expected = item.Parameters.Select(parameter => parameter.Id).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var actual = inputs.EnumerateObject().Select(property => property.Name).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal)) throw new ArgumentException("inputs_invalid", nameof(inputs));
        var result = new Dictionary<string, PhotonCadIndustrialCatalogInputValue?>(StringComparer.Ordinal);
        foreach (var parameter in item.Parameters)
        {
            var value = inputs.GetProperty(parameter.Id);
            if (value.ValueKind == JsonValueKind.Null)
            {
                result.Add(parameter.Id, null);
                continue;
            }
            PhotonCadIndustrialCatalogInputValue converted = parameter.Kind switch
            {
                PhotonCadIndustrialParameterKind.Number when value.ValueKind == JsonValueKind.Number
                    && value.TryGetDouble(out var number) && double.IsFinite(number) =>
                    PhotonCadIndustrialCatalogInputValue.Number(number),
                PhotonCadIndustrialParameterKind.Integer when value.ValueKind == JsonValueKind.Number
                    && value.TryGetInt64(out var integer) => PhotonCadIndustrialCatalogInputValue.Integer(integer),
                PhotonCadIndustrialParameterKind.Boolean when value.ValueKind is JsonValueKind.True or JsonValueKind.False =>
                    PhotonCadIndustrialCatalogInputValue.Boolean(value.GetBoolean()),
                PhotonCadIndustrialParameterKind.Choice when value.ValueKind == JsonValueKind.String
                    && value.GetString() is { } token
                    && parameter.Choices.Any(choice => StringComparer.Ordinal.Equals(choice.Token, token)) =>
                    PhotonCadIndustrialCatalogInputValue.Choice(token),
                _ => throw new ArgumentException("inputs_invalid", nameof(inputs)),
            };
            result.Add(parameter.Id, converted);
        }
        return result;
    }

    private static bool IsManualCapability(string capabilityId) => capabilityId is
        PhotonCadManualCapabilityIds.SketchExtrudeAdd
        or PhotonCadManualCapabilityIds.SketchExtrudeCut
        or PhotonCadManualCapabilityIds.MouseSketchExtrudeAdd
        or PhotonCadManualCapabilityIds.MouseSketchExtrudeCut
        or PhotonCadManualCapabilityIds.HoleCut
        or PhotonCadManualCapabilityIds.LinearPattern
        or PhotonCadManualCapabilityIds.CircularPattern;

    private async ValueTask<ManualMutationInputs> ParseManualMutationInputsAsync(
        ExternalRuntimeRequest external,
        string capabilityId,
        JsonElement inputs,
        JsonElement targets,
        CancellationToken cancellationToken)
    {
        if (inputs.ValueKind != JsonValueKind.Object || targets.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("manual_request_shape_invalid", nameof(inputs));
        if (capabilityId == PhotonCadManualCapabilityIds.SketchExtrudeAdd)
        {
            RequireExactProperties(inputs,
                "extrusionDepthMm", "profileHeightMm", "profileKind", "profileRadiusMm", "profileWidthMm", "sketchPlane");
            if (targets.GetArrayLength() != 0) throw new ArgumentException("manual_create_targets_invalid", nameof(targets));
            var profileKind = RequiredToken(inputs, "profileKind");
            var plane = RequiredToken(inputs, "sketchPlane");
            var depth = RequiredFiniteNumber(inputs, "extrusionDepthMm", positive: true);
            var width = OptionalFiniteNumber(inputs, "profileWidthMm", positive: true);
            var height = OptionalFiniteNumber(inputs, "profileHeightMm", positive: true);
            var radius = OptionalFiniteNumber(inputs, "profileRadiusMm", positive: true);
            if (profileKind == "rectangle" && (width is null || height is null || radius is not null)
                || profileKind == "circle" && (radius is null || width is not null || height is not null)
                || profileKind is not ("rectangle" or "circle")
                || plane != "xy")
                throw new ArgumentException("manual_profile_invalid", nameof(inputs));
            return new ManualMutationInputs(
                PhotonCadManualOperationKind.SketchExtrudeAdd,
                $"entity-{Guid.NewGuid():N}",
                null, 0, 0,
                profileKind,
                plane,
                width ?? 0,
                height ?? 0,
                radius ?? 0,
                depth,
                0, 0, 0);
        }

        if (capabilityId == PhotonCadManualCapabilityIds.MouseSketchExtrudeAdd)
        {
            RequireExactProperties(inputs, "extrusionDepthMm", "sketch");
            var sketchInput = RequiredMouseSketchInput(inputs);
            var targetCount = targets.GetArrayLength();
            if (targetCount > 1) throw new ArgumentException("manual_add_targets_invalid", nameof(targets));
            var mouseTargetEntityId = targetCount == 0
                ? $"entity-{Guid.NewGuid():N}"
                : await ResolveManualTargetAsync(external, targets, cancellationToken).ConfigureAwait(false);
            return new ManualMutationInputs(
                PhotonCadManualOperationKind.SketchExtrudeAdd,
                mouseTargetEntityId,
                null, 0, 0, string.Empty, "xy", 0, 0, 0,
                RequiredFiniteNumber(inputs, "extrusionDepthMm", positive: true),
                0, 0, 0,
                ParseMouseSketch(sketchInput), sketchInput, targetCount == 1);
        }

        var targetEntityId = await ResolveManualTargetAsync(external, targets, cancellationToken).ConfigureAwait(false);
        if (capabilityId == PhotonCadManualCapabilityIds.MouseSketchExtrudeCut)
        {
            RequireExactProperties(inputs, "cutDepthMm", "sketch");
            var sketchInput = RequiredMouseSketchInput(inputs);
            return new ManualMutationInputs(
                PhotonCadManualOperationKind.SketchExtrudeCut,
                targetEntityId,
                null, 0, 0, string.Empty, "xy", 0, 0, 0,
                RequiredFiniteNumber(inputs, "cutDepthMm", positive: true),
                0, 0, 0,
                ParseMouseSketch(sketchInput), sketchInput);
        }
        if (capabilityId == PhotonCadManualCapabilityIds.SketchExtrudeCut)
        {
            RequireExactProperties(inputs, "cutDepthMm", "profileHeightMm", "profileWidthMm", "sketchPlane");
            return new ManualMutationInputs(
                PhotonCadManualOperationKind.SketchExtrudeCut,
                targetEntityId,
                null, 0, 0,
                "rectangle",
                RequiredToken(inputs, "sketchPlane"),
                RequiredFiniteNumber(inputs, "profileWidthMm", positive: true),
                RequiredFiniteNumber(inputs, "profileHeightMm", positive: true),
                0,
                RequiredFiniteNumber(inputs, "cutDepthMm", positive: true),
                0, 0, 0);
        }
        if (capabilityId == PhotonCadManualCapabilityIds.HoleCut)
        {
            RequireExactProperties(inputs, "depthMm", "diameterMm", "xMm", "yMm", "zMm");
            return new ManualMutationInputs(
                PhotonCadManualOperationKind.HoleCut,
                targetEntityId,
                null, 0, 0,
                string.Empty,
                "xy",
                0, 0,
                RequiredFiniteNumber(inputs, "diameterMm", positive: true),
                RequiredFiniteNumber(inputs, "depthMm", positive: true),
                RequiredFiniteNumber(inputs, "xMm", positive: false),
                RequiredFiniteNumber(inputs, "yMm", positive: false),
                RequiredFiniteNumber(inputs, "zMm", positive: false));
        }
        if (capabilityId == PhotonCadManualCapabilityIds.LinearPattern)
        {
            RequireExactProperties(inputs, "count", "seedFeatureId", "spacingMm");
            var seedFeatureId = await ResolveManualPatternSeedAsync(external, targetEntityId, inputs, cancellationToken).ConfigureAwait(false);
            return new ManualMutationInputs(
                PhotonCadManualOperationKind.LinearPattern,
                targetEntityId, seedFeatureId, RequiredBoundedCount(inputs, "count"),
                RequiredFiniteNumber(inputs, "spacingMm", positive: true),
                string.Empty, "xy", 0, 0, 0, 0, 0, 0, 0);
        }
        if (capabilityId == PhotonCadManualCapabilityIds.CircularPattern)
        {
            RequireExactProperties(inputs, "angleDegrees", "count", "seedFeatureId");
            var seedFeatureId = await ResolveManualPatternSeedAsync(external, targetEntityId, inputs, cancellationToken).ConfigureAwait(false);
            return new ManualMutationInputs(
                PhotonCadManualOperationKind.CircularPattern,
                targetEntityId, seedFeatureId, RequiredBoundedCount(inputs, "count"),
                RequiredFiniteNumber(inputs, "angleDegrees", positive: true),
                string.Empty, "xy", 0, 0, 0, 0, 0, 0, 0);
        }
        throw new ArgumentException("manual_capability_invalid", nameof(capabilityId));
    }

    private static string RequiredMouseSketchInput(JsonElement inputs)
    {
        var sketch = inputs.GetProperty("sketch");
        var value = sketch.ValueKind == JsonValueKind.String ? sketch.GetString() : null;
        return !string.IsNullOrWhiteSpace(value) && value.Length <= 8192
            ? value
            : throw new ArgumentException("manual_mouse_sketch_invalid", nameof(inputs));
    }

    private static PhotonCadManualMouseSketch ParseMouseSketch(string encoded)
    {
        try
        {
            using var document = JsonDocument.Parse(encoded, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException("manual_mouse_sketch_invalid");
            var kind = RequiredToken(root, "kind");
            if (kind is not ("rectangle" or "circle" or "polygon" or "filletedPolygon"))
                throw new ArgumentException("manual_mouse_sketch_invalid");
            RequireExactProperties(root, kind == "filletedPolygon"
                ? ["cornerRadiiMm", "kind", "normal", "originMm", "points", "xDirection"]
                : ["kind", "normal", "originMm", "points", "xDirection"]);
            var origin = ParseBoundedVector(root.GetProperty("originMm"), 1_000_000);
            var xDirection = ParseBoundedVector(root.GetProperty("xDirection"), 1);
            var normal = ParseBoundedVector(root.GetProperty("normal"), 1);
            ValidateSketchFrame(xDirection, normal);
            var pointsElement = root.GetProperty("points");
            if (pointsElement.ValueKind != JsonValueKind.Array) throw new ArgumentException("manual_mouse_sketch_invalid");
            var points = pointsElement.EnumerateArray().Select(ParseMouseSketchPoint).ToArray();
            var expectedCount = kind is "polygon" or "filletedPolygon" ? points.Length is >= 3 and <= 64 : points.Length == 2;
            if (!expectedCount || points.Distinct().Count() != points.Length)
                throw new ArgumentException("manual_mouse_sketch_invalid");
            if (kind == "rectangle" && (Math.Abs(points[0].XMm - points[1].XMm) < 0.000001 || Math.Abs(points[0].YMm - points[1].YMm) < 0.000001))
                throw new ArgumentException("manual_mouse_sketch_invalid");
            if (kind == "circle" && Math.Pow(points[0].XMm - points[1].XMm, 2) + Math.Pow(points[0].YMm - points[1].YMm, 2) < 0.000000000001)
                throw new ArgumentException("manual_mouse_sketch_invalid");
            if (kind is "polygon" or "filletedPolygon" && Math.Abs(PolygonArea(points)) < 0.000001)
                throw new ArgumentException("manual_mouse_sketch_invalid");
            var cornerRadii = kind == "filletedPolygon"
                ? ParseCornerRadii(root.GetProperty("cornerRadiiMm"), points.Length)
                : Array.Empty<double>();
            return new PhotonCadManualMouseSketch(
                kind, points, cornerRadii,
                origin.X, origin.Y, origin.Z,
                xDirection.X, xDirection.Y, xDirection.Z,
                normal.X, normal.Y, normal.Z);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("manual_mouse_sketch_invalid", nameof(encoded), exception);
        }
    }

    private static ManualSketchPoint ParseMouseSketchPoint(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object
            || value.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal)
                .SequenceEqual(["x", "y"], StringComparer.Ordinal) is false)
            throw new ArgumentException("manual_mouse_sketch_invalid");
        var x = RequiredDouble(value, "x");
        var y = RequiredDouble(value, "y");
        if (!double.IsFinite(x) || !double.IsFinite(y) || Math.Abs(x) > 1_000_000 || Math.Abs(y) > 1_000_000)
            throw new ArgumentException("manual_mouse_sketch_invalid");
        return new ManualSketchPoint(x, y);
    }

    private static IReadOnlyList<double> ParseCornerRadii(JsonElement value, int expectedCount)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != expectedCount)
            throw new ArgumentException("manual_mouse_sketch_invalid");
        var result = new List<double>(expectedCount);
        foreach (var radius in value.EnumerateArray())
        {
            if (radius.ValueKind != JsonValueKind.Number || !radius.TryGetDouble(out var millimeters)
                || !double.IsFinite(millimeters) || millimeters < 0 || millimeters > 1_000_000)
                throw new ArgumentException("manual_mouse_sketch_invalid");
            result.Add(millimeters);
        }
        if (!result.Any(radius => radius > 0)) throw new ArgumentException("manual_mouse_sketch_invalid");
        return result;
    }

    private static void ValidateSketchFrame(CadVector3 xDirection, CadVector3 normal)
    {
        static double Magnitude(CadVector3 vector) => Math.Sqrt(vector.X * vector.X + vector.Y * vector.Y + vector.Z * vector.Z);
        var xLength = Magnitude(xDirection);
        var normalLength = Magnitude(normal);
        var dot = xDirection.X * normal.X + xDirection.Y * normal.Y + xDirection.Z * normal.Z;
        if (Math.Abs(xLength - 1) > 0.0001 || Math.Abs(normalLength - 1) > 0.0001 || Math.Abs(dot) > 0.0001)
            throw new ArgumentException("manual_mouse_sketch_frame_invalid");
    }

    private static double PolygonArea(IReadOnlyList<ManualSketchPoint> points)
    {
        var area = 0d;
        for (var index = 0; index < points.Count; index++)
        {
            var next = points[(index + 1) % points.Count];
            area += points[index].XMm * next.YMm - next.XMm * points[index].YMm;
        }
        return area / 2d;
    }

    private async ValueTask<string> ResolveManualTargetAsync(
        ExternalRuntimeRequest external,
        JsonElement targets,
        CancellationToken cancellationToken)
    {
        if (_projectHost is null || _projectCodec is null || targets.GetArrayLength() != 1)
            throw new ArgumentException("manual_target_invalid", nameof(targets));
        var target = targets.EnumerateArray().Single();
        var targetEntityId = target.ValueKind == JsonValueKind.String ? target.GetString() : null;
        if (!IsIdentifier(targetEntityId)) throw new ArgumentException("manual_target_invalid", nameof(targets));
        var project = await _projectHost.ResolveCommittedProjectAsync(
            $"manual-target-resolve-{Guid.NewGuid():N}",
            external.SessionId,
            external.ProjectId,
            external.Revision,
            cancellationToken).ConfigureAwait(false);
        var entity = _projectCodec.Inspect(project).Entities.SingleOrDefault(value =>
            StringComparer.Ordinal.Equals(value.Id, targetEntityId));
        if (entity is null || entity.Kind is not (PhotonCadEntityKindV1.Body or PhotonCadEntityKindV1.Part))
            throw new ArgumentException("manual_target_invalid", nameof(targets));
        return targetEntityId!;
    }

    private async ValueTask<string> ResolveManualPatternSeedAsync(
        ExternalRuntimeRequest external,
        string targetEntityId,
        JsonElement inputs,
        CancellationToken cancellationToken)
    {
        var seedFeatureId = inputs.GetProperty("seedFeatureId").ValueKind == JsonValueKind.String
            ? inputs.GetProperty("seedFeatureId").GetString()
            : null;
        if (!IsIdentifier(seedFeatureId) || _projectHost is null || _projectCodec is null)
            throw new ArgumentException("manual_pattern_seed_invalid", nameof(inputs));
        var project = await _projectHost.ResolveCommittedProjectAsync(
            $"manual-pattern-seed-resolve-{Guid.NewGuid():N}",
            external.SessionId,
            external.ProjectId,
            external.Revision,
            cancellationToken).ConfigureAwait(false);
        var seed = _projectCodec.Inspect(project).Entities.SingleOrDefault(value =>
            StringComparer.Ordinal.Equals(value.Id, seedFeatureId));
        if (seed is null
            || seed.Kind != PhotonCadEntityKindV1.Datum
            || !StringComparer.Ordinal.Equals(seed.ParentId, targetEntityId)
            || seed.SourceCapabilityId is not (PhotonCadManualCapabilityIds.SketchExtrudeCut or PhotonCadManualCapabilityIds.HoleCut))
            throw new ArgumentException("manual_pattern_seed_invalid", nameof(inputs));
        return seedFeatureId!;
    }

    private static void RequireExactProperties(JsonElement value, params string[] expected)
    {
        var actual = value.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(expected.OrderBy(name => name, StringComparer.Ordinal), StringComparer.Ordinal))
            throw new ArgumentException("manual_inputs_invalid", nameof(value));
    }

    private static string RequiredToken(JsonElement value, string property)
    {
        var element = value.GetProperty(property);
        var token = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        return !string.IsNullOrWhiteSpace(token) && token.Length <= 64
            && token.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
            ? token
            : throw new ArgumentException("manual_input_token_invalid", property);
    }

    private static double RequiredFiniteNumber(JsonElement value, string property, bool positive)
    {
        var element = value.GetProperty(property);
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out var number)
            || !double.IsFinite(number) || Math.Abs(number) > 1_000_000 || positive && number <= 0)
            throw new ArgumentException("manual_input_number_invalid", property);
        return number;
    }

    private static long RequiredBoundedCount(JsonElement value, string property)
    {
        var element = value.GetProperty(property);
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var count) || count is < 2 or > 256)
            throw new ArgumentException("manual_input_count_invalid", property);
        return count;
    }

    private static double? OptionalFiniteNumber(JsonElement value, string property, bool positive)
    {
        var element = value.GetProperty(property);
        return element.ValueKind == JsonValueKind.Null ? null : RequiredFiniteNumber(value, property, positive);
    }

    private async ValueTask<IndustrialMutationBinding> CreateIndustrialBindingAsync(
        IndustrialMutationRequest request,
        CancellationToken cancellationToken)
    {
        if (_industrialBindingFactory is not null)
            return await _industrialBindingFactory(request, cancellationToken).ConfigureAwait(false);
        if (request.ManualInputs is { } manual)
        {
            var manualRuntime = await EnsureManualProviderReadyAsync(cancellationToken).ConfigureAwait(false);
            PhotonCadManualBoundMutation boundManual = manual.Kind switch
            {
                PhotonCadManualOperationKind.SketchExtrudeAdd when manual.MouseSketch is { } sketch && manual.MouseSketchInput is { } sketchInput =>
                    manualRuntime.BindMouseSketchExtrudeAdd(
                        request.RequestId, request.SessionId, request.ProjectId, request.BaseRevision, manual.TargetEntityId,
                        sketch, manual.DepthMm, sketchInput, manual.JoinsExistingSolid),
                PhotonCadManualOperationKind.SketchExtrudeCut when manual.MouseSketch is { } sketch && manual.MouseSketchInput is { } sketchInput =>
                    manualRuntime.BindMouseSketchExtrudeCut(
                        request.RequestId, request.SessionId, request.ProjectId, request.BaseRevision, manual.TargetEntityId,
                        sketch, manual.DepthMm, sketchInput),
                PhotonCadManualOperationKind.SketchExtrudeAdd when manual.ProfileKind == "rectangle" =>
                    manualRuntime.BindSketchExtrudeAdd(
                        request.RequestId, request.SessionId, request.ProjectId, request.BaseRevision, manual.TargetEntityId,
                        manual.Plane, manual.WidthMm, manual.HeightMm, manual.DepthMm),
                PhotonCadManualOperationKind.SketchExtrudeAdd when manual.ProfileKind == "circle" =>
                    manualRuntime.BindCircularSketchExtrudeAdd(
                        request.RequestId, request.SessionId, request.ProjectId, request.BaseRevision, manual.TargetEntityId,
                        manual.Plane, manual.RadiusOrDiameterMm, manual.DepthMm),
                PhotonCadManualOperationKind.SketchExtrudeCut =>
                    manualRuntime.BindSketchExtrudeCut(
                        request.RequestId, request.SessionId, request.ProjectId, request.BaseRevision, manual.TargetEntityId,
                        manual.Plane, manual.WidthMm, manual.HeightMm, manual.DepthMm),
                PhotonCadManualOperationKind.HoleCut =>
                    manualRuntime.BindHoleCut(
                        request.RequestId, request.SessionId, request.ProjectId, request.BaseRevision, manual.TargetEntityId,
                        manual.RadiusOrDiameterMm, manual.DepthMm, manual.XMm, manual.YMm, manual.ZMm),
                PhotonCadManualOperationKind.LinearPattern when manual.SeedFeatureId is { } seedFeatureId =>
                    manualRuntime.BindLinearPattern(
                        request.RequestId, request.SessionId, request.ProjectId, request.BaseRevision, manual.TargetEntityId,
                        seedFeatureId, manual.PatternCount, manual.PatternMeasure),
                PhotonCadManualOperationKind.CircularPattern when manual.SeedFeatureId is { } seedFeatureId =>
                    manualRuntime.BindCircularPattern(
                        request.RequestId, request.SessionId, request.ProjectId, request.BaseRevision, manual.TargetEntityId,
                        seedFeatureId, manual.PatternCount, manual.PatternMeasure),
                _ => throw new InvalidOperationException("manual_operation_unavailable"),
            };
            return new IndustrialMutationBinding(boundManual.Request, boundManual.Provider, boundManual.Compensator);
        }
        var runtime = await EnsureIndustrialProviderReadyAsync(cancellationToken).ConfigureAwait(false);
        if (request.AssemblyInputs is { } assembly)
        {
            var parentOccurrenceId = assembly.ParentOccurrenceId ?? $"{assembly.SourceEntityId}.occ";
            var boundAssembly = runtime.BindAssemblyPlace(
                request.RequestId,
                request.SessionId,
                request.ProjectId,
                request.BaseRevision,
                request.EntityId,
                assembly.SourceEntityId,
                parentOccurrenceId,
                assembly.Transform);
            return new IndustrialMutationBinding(boundAssembly.Request, boundAssembly.Provider, boundAssembly.Compensator);
        }
        if (request.AssemblyTransform is { } transform)
        {
            var boundAssembly = runtime.BindAssemblyTransform(
                request.RequestId,
                request.SessionId,
                request.ProjectId,
                request.BaseRevision,
                transform.OccurrenceId,
                transform.SourceEntityId,
                transform.Transform);
            return new IndustrialMutationBinding(boundAssembly.Request, boundAssembly.Provider, boundAssembly.Compensator);
        }
        if (request.AssemblyRemoval is { } removal)
        {
            var boundAssembly = runtime.BindAssemblyRemove(
                request.RequestId,
                request.SessionId,
                request.ProjectId,
                request.BaseRevision,
                removal.OccurrenceId,
                removal.SourceEntityId);
            return new IndustrialMutationBinding(boundAssembly.Request, boundAssembly.Provider, boundAssembly.Compensator);
        }
        PhotonCadIndustrialBoundMutation bound;
        if (request.CapabilityId == CadPinnedCapabilityCatalog.BoxCapabilityId)
        {
            bound = runtime.BindBox(request.RequestId, request.SessionId, request.ProjectId, request.BaseRevision, request.EntityId,
                request.NumericInputs["length"], request.NumericInputs["width"], request.NumericInputs["height"]);
        }
        else if (request.CapabilityId == CadPinnedCapabilityCatalog.CylinderCapabilityId)
        {
            bound = runtime.BindCylinder(request.RequestId, request.SessionId, request.ProjectId, request.BaseRevision, request.EntityId,
                request.NumericInputs["radius"], request.NumericInputs["height"]);
        }
        else
        {
            bound = await runtime.BindCatalogItemAsync(
                request.RequestId,
                request.SessionId,
                request.ProjectId,
                request.BaseRevision,
                request.EntityId,
                request.CapabilityId,
                request.CatalogInputs,
                cancellationToken).ConfigureAwait(false);
        }
        return new IndustrialMutationBinding(bound.Request, bound.Provider, bound.Compensator);
    }

    private Task<PhotonCadIndustrialProviderRuntime> EnsureIndustrialProviderReadyAsync(CancellationToken cancellationToken)
    {
        lock (_brokerLock) _industrialRuntimeTask ??= CreateIndustrialRuntimeAsync();
        return _industrialRuntimeTask.WaitAsync(cancellationToken);
    }

    private Task<PhotonCadManualProviderRuntime> EnsureManualProviderReadyAsync(CancellationToken cancellationToken)
    {
        lock (_brokerLock) _manualRuntimeTask ??= CreateManualRuntimeAsync();
        return _manualRuntimeTask.WaitAsync(cancellationToken);
    }

    private async Task<PhotonCadManualProviderRuntime> CreateManualRuntimeAsync()
    {
        var assets = Path.Combine(_installRoot, InstalledManualAssetRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var evidence = Path.Combine(assets, IndustrialEvidenceSelectionFileName);
        var docker = DockerDesktopCliResolver.Resolve();
        if (!File.Exists(evidence) || !File.Exists(docker)) throw new InvalidOperationException("manual_runtime_unavailable");
        var workspace = CreateManualWorkspace();
        try
        {
            var config = Path.Combine(workspace, "docker-config");
            var jobs = Path.Combine(workspace, "jobs");
            Directory.CreateDirectory(config);
            Directory.CreateDirectory(jobs);
            return await PhotonCadManualProviderRuntime.CreateLocalEngineeringAsync(
                docker, config, jobs, evidence, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            DeleteOwnedProviderWorkspace(workspace, "PhotonCadManual");
            _manualWorkspace = null;
            throw;
        }
    }

    private string CreateManualWorkspace()
    {
        var parent = Path.Combine(Path.GetTempPath(), "PhotosAgapeAphthartos", "PhotonCadManual");
        Directory.CreateDirectory(parent);
        var workspace = Path.Combine(parent, $"session-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        if ((File.GetAttributes(workspace) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The manual CAD workspace is not a normal directory.");
        _manualWorkspace = workspace;
        return workspace;
    }

    private async Task<PhotonCadIndustrialProviderRuntime> CreateIndustrialRuntimeAsync()
    {
        var assets = Path.Combine(_installRoot, InstalledIndustrialAssetRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var evidence = Path.Combine(assets, IndustrialEvidenceSelectionFileName);
        var docker = DockerDesktopCliResolver.Resolve();
        if (!File.Exists(evidence) || !File.Exists(docker)) throw new InvalidOperationException("industrial_runtime_unavailable");
        var workspace = CreateIndustrialWorkspace();
        try
        {
            var config = Path.Combine(workspace, "docker-config");
            var jobs = Path.Combine(workspace, "jobs");
            Directory.CreateDirectory(config);
            Directory.CreateDirectory(jobs);
            await VerifyIndustrialImageAsync(docker, config, CancellationToken.None).ConfigureAwait(false);
            return await PhotonCadIndustrialProviderRuntime.CreateLocalEngineeringAsync(
                docker, config, jobs, evidence, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            DeleteOwnedIndustrialWorkspace(workspace);
            _industrialWorkspace = null;
            throw;
        }
    }

    private string CreateIndustrialWorkspace()
    {
        var parent = Path.Combine(Path.GetTempPath(), "PhotosAgapeAphthartos", "PhotonCadIndustrial");
        Directory.CreateDirectory(parent);
        var workspace = Path.Combine(parent, $"session-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        if ((File.GetAttributes(workspace) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The industrial CAD workspace is not a normal directory.");
        _industrialWorkspace = workspace;
        return workspace;
    }

    private static async Task VerifyIndustrialImageAsync(string docker, string config, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = docker,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "--config", config, "--host", "npipe:////./pipe/docker_engine", "image", "inspect",
                     IndustrialImageSha256, "--format", "{{.Id}}" }) startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException("industrial_docker_start_failed");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } throw; }
        var identity = (await stdout.ConfigureAwait(false)).Trim();
        var error = await stderr.ConfigureAwait(false);
        if (identity.Length > 96 || error.Length > 64 * 1024 || process.ExitCode != 0
            || !StringComparer.Ordinal.Equals(identity, IndustrialImageSha256))
            throw new InvalidOperationException("industrial_exact_image_unavailable");
    }

    private static CadRuntimeDescription IndustrialRendererDescription(
        PhotonCadIndustrialCatalog providerCatalog,
        IReadOnlyList<PhotonCadManualCapability>? manualCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(providerCatalog);
        var generated = DateTimeOffset.UtcNow;
        var pinned = CadPinnedCapabilityCatalog.Create(generated);
        var primitiveCapabilities = pinned.Capabilities
            .Where(capability => capability.Id is CadPinnedCapabilityCatalog.BoxCapabilityId or CadPinnedCapabilityCatalog.CylinderCapabilityId)
            .Select(capability => new CadCapability(
                capability.Id, capability.Backend, capability.Category, capability.Title, capability.Description,
                capability.Operation, capability.Parameters, capability.Source, previewSupported: true, capability.Experimental))
            .ToArray();
        var source = new CadSourceIdentity(
            "bd-warehouse",
            "0.2.0",
            IndustrialImageSha256,
            "redistribution-blocked");
        var catalogCapabilities = providerCatalog.Items.Select(item => new CadCapability(
            item.CapabilityId,
            CadBackend.Assembly,
            IndustrialCatalogCategory(item.Category),
            item.Title,
            "Create one exact catalog part from the verified industrial component library and persist its STEP, preview, occurrence, and BOM row.",
            CadCapabilityOperationKind.Create,
            item.Parameters.Select(IndustrialCatalogParameter),
            source,
            previewSupported: true,
            experimental: false)).ToArray();
        var manualCapabilities = (manualCatalog ?? [])
            .Where(capability => capability.Availability == PhotonCadManualAvailability.Available)
            .Select(ManualRendererCapability)
            .ToArray();
        var capabilities = primitiveCapabilities.Concat(catalogCapabilities)
            .Concat(manualCapabilities)
            .Concat([AssemblyRendererCapability(), AssemblyTransformRendererCapability(), AssemblyRemoveRendererCapability()])
            .ToArray();
        var catalog = new CadCapabilityCatalog(
            $"industrial-{providerCatalog.Digest[7..]}-manual-{manualCapabilities.Length}-assembly-v1",
            generated,
            capabilities,
            new CadCatalogCoverage(capabilities.Length, capabilities.Length, 0));
        var digest = IndustrialImageSha256[7..];
        var receipt = IndustrialReceiptSha256;
        var bundles = new CadRuntimeBundleSetIdentity(
            new CadBundleIdentity(CadRuntimeRole.Geometry, "photon-cad-industrial-geometry", "0.1.0", "docker",
                "industrial-v1", "linux-amd64", digest, generated),
            new CadBundleIdentity(CadRuntimeRole.Assembly, "photon-cad-industrial-preview", "0.1.0", "docker",
                "industrial-v1", "linux-amd64", receipt, generated),
            new CadRevision(0));
        return new CadRuntimeDescription(CadRuntimeAvailability.Ready, "ready", "The verified industrial CAD runtime is ready.", bundles, catalog);
    }

    private static CadCapability ManualRendererCapability(PhotonCadManualCapability capability)
    {
        var source = new CadSourceIdentity(
            "build123d",
            "0.3.80",
            IndustrialImageSha256,
            "redistribution-blocked");
        CadParameterDefinition PositiveLength(string id, string label, bool required = true, double? defaultValue = null) => new(
            id, label, $"Finite positive {label.ToLowerInvariant()} in millimeters.",
            CadParameterKind.Number, required, CadParameterUnit.Length,
            minimum: 0.000001, maximum: 1_000_000, step: null,
            defaultValue: defaultValue is { } value
                ? new CadNumberInputValue(value)
                : required ? new CadNumberInputValue(10) : new CadNullInputValue(CadParameterKind.Number));
        CadParameterDefinition SignedLength(string id, string label) => new(
            id, label, $"Finite signed {label.ToLowerInvariant()} in millimeters.",
            CadParameterKind.Number, required: true, CadParameterUnit.Length,
            minimum: -1_000_000, maximum: 1_000_000, defaultValue: new CadNumberInputValue(0));
        CadParameterDefinition MouseSketch() => new(
            "sketch", "Mouse sketch", "Validated closed sketch payload from the desktop sketch canvas.",
            CadParameterKind.Text, required: true, defaultValue: new CadTextInputValue("{}", CadParameterKind.Text));
        IReadOnlyList<CadParameterDefinition> parameters = capability.Kind switch
        {
            PhotonCadManualOperationKind.SketchExtrudeAdd when capability.CapabilityId == PhotonCadManualCapabilityIds.MouseSketchExtrudeAdd =>
            [
                MouseSketch(),
                PositiveLength("extrusionDepthMm", "Extrusion depth"),
            ],
            PhotonCadManualOperationKind.SketchExtrudeCut when capability.CapabilityId == PhotonCadManualCapabilityIds.MouseSketchExtrudeCut =>
            [
                MouseSketch(),
                PositiveLength("cutDepthMm", "Cut depth"),
            ],
            PhotonCadManualOperationKind.SketchExtrudeAdd =>
            [
                new CadParameterDefinition("profileKind", "Profile", "Rectangle or circle sketch profile.",
                    CadParameterKind.Choice, required: true, choices:
                    [new CadChoice("rectangle", "Rectangle"), new CadChoice("circle", "Circle")],
                    defaultValue: new CadTextInputValue("rectangle", CadParameterKind.Choice)),
                new CadParameterDefinition("sketchPlane", "Sketch plane", "Supported exact sketch plane.",
                    CadParameterKind.Choice, required: true,
                    choices: [new CadChoice("xy", "XY")],
                    defaultValue: new CadTextInputValue("xy", CadParameterKind.Choice)),
                PositiveLength("profileWidthMm", "Profile width", required: false, defaultValue: 10),
                PositiveLength("profileHeightMm", "Profile height", required: false, defaultValue: 10),
                PositiveLength("profileRadiusMm", "Profile radius", required: false),
                PositiveLength("extrusionDepthMm", "Extrusion depth"),
            ],
            PhotonCadManualOperationKind.SketchExtrudeCut =>
            [
                new CadParameterDefinition("sketchPlane", "Sketch plane", "Supported exact sketch plane.",
                    CadParameterKind.Choice, required: true,
                    choices: [new CadChoice("xy", "XY")],
                    defaultValue: new CadTextInputValue("xy", CadParameterKind.Choice)),
                PositiveLength("profileWidthMm", "Profile width"),
                PositiveLength("profileHeightMm", "Profile height"),
                PositiveLength("cutDepthMm", "Cut depth"),
            ],
            PhotonCadManualOperationKind.HoleCut =>
            [
                PositiveLength("diameterMm", "Hole diameter"),
                PositiveLength("depthMm", "Hole depth"),
                SignedLength("xMm", "X position"),
                SignedLength("yMm", "Y position"),
                SignedLength("zMm", "Z position"),
            ],
            PhotonCadManualOperationKind.LinearPattern =>
            [
                new CadParameterDefinition("seedFeatureId", "Seed feature", "Existing cut or hole feature on the selected solid.",
                    CadParameterKind.Entity, required: true),
                new CadParameterDefinition("count", "Pattern count", "Number of instances including the seed feature.",
                    CadParameterKind.Integer, required: true, CadParameterUnit.Count, minimum: 2, maximum: 256, step: 1,
                    defaultValue: new CadIntegerInputValue(2)),
                PositiveLength("spacingMm", "Spacing"),
            ],
            PhotonCadManualOperationKind.CircularPattern =>
            [
                new CadParameterDefinition("seedFeatureId", "Seed feature", "Existing cut or hole feature on the selected solid.",
                    CadParameterKind.Entity, required: true),
                new CadParameterDefinition("count", "Pattern count", "Number of instances including the seed feature.",
                    CadParameterKind.Integer, required: true, CadParameterUnit.Count, minimum: 2, maximum: 256, step: 1,
                    defaultValue: new CadIntegerInputValue(2)),
                new CadParameterDefinition("angleDegrees", "Sweep angle", "Finite positive sweep angle in degrees.",
                    CadParameterKind.Number, required: true, CadParameterUnit.Angle, minimum: 0.000001, maximum: 360,
                    defaultValue: new CadNumberInputValue(360)),
            ],
            _ => throw new InvalidOperationException("manual_capability_unavailable"),
        };
        return new CadCapability(
            capability.CapabilityId,
            CadBackend.Geometry,
            "Build123d design",
            capability.Title,
            capability.Description,
            capability.Kind == PhotonCadManualOperationKind.SketchExtrudeAdd
                ? CadCapabilityOperationKind.Create
                : CadCapabilityOperationKind.Modify,
            parameters,
            source,
            previewSupported: true,
            experimental: false);
    }

    private static string IndustrialCatalogCategory(string category)
    {
        if (StringComparer.Ordinal.Equals(category, "openbuilds")) return "Industrial OpenBuilds";
        var words = category.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return "Industrial components";
        return "Industrial " + string.Join(' ', words);
    }

    internal static CadCapability AssemblyRendererCapability()
    {
        var zeroVector = new CadVectorInputValue(new CadVector3(0, 0, 0));
        return new CadCapability(
            PhotonCadAssemblyContract.PlaceCapabilityId,
            CadBackend.Assembly,
            "Assembly",
            "Place occurrence",
            "Place one existing canonical part occurrence and persist the complete assembly preview and derived bill of materials.",
            CadCapabilityOperationKind.Assemble,
            [
                new CadParameterDefinition(
                    "sourceEntityId", "Source part", "Existing canonical part entity to place.",
                    CadParameterKind.Entity, required: true),
                new CadParameterDefinition(
                    "parentOccurrenceId", "Parent occurrence", "Optional existing occurrence parent; defaults to the source part's canonical occurrence.",
                    CadParameterKind.Text, required: false, defaultValue: new CadNullInputValue(CadParameterKind.Text)),
                new CadParameterDefinition(
                    "translation", "Translation", "Finite millimeter translation bounded to the supported project workspace.",
                    CadParameterKind.Vector3, required: true, CadParameterUnit.Length,
                    minimum: -1_000_000, maximum: 1_000_000, defaultValue: zeroVector),
                new CadParameterDefinition(
                    "rotationDegrees", "Rotation", "Finite XYZ Euler rotation in degrees; the host derives the rigid transform.",
                    CadParameterKind.Vector3, required: true, CadParameterUnit.Angle,
                    minimum: -360, maximum: 360, defaultValue: zeroVector),
            ],
            new CadSourceIdentity("bd-warehouse", "0.2.0", IndustrialImageSha256, "redistribution-blocked"),
            previewSupported: true,
            experimental: false);
    }

    internal static CadCapability AssemblyRemoveRendererCapability() => new(
        PhotonCadAssemblyContract.RemoveCapabilityId,
        CadBackend.Assembly,
        "Assembly",
        "Remove occurrence",
        "Remove the selected assembly occurrence and its descendants, persist the replacement preview and bill of materials, and retain source part definitions for reuse.",
        CadCapabilityOperationKind.Assemble,
        [],
        new CadSourceIdentity("bd-warehouse", "0.2.0", IndustrialImageSha256, "redistribution-blocked"),
        previewSupported: true,
        experimental: false);

    internal static CadCapability AssemblyTransformRendererCapability()
    {
        var zeroVector = new CadVectorInputValue(new CadVector3(0, 0, 0));
        return new CadCapability(
            PhotonCadAssemblyContract.TransformCapabilityId,
            CadBackend.Assembly,
            "Assembly",
            "Move occurrence",
            "Replace the selected occurrence's rigid transform, then persist the complete assembly preview and derived bill of materials.",
            CadCapabilityOperationKind.Assemble,
            [
                new CadParameterDefinition(
                    "translation", "Translation", "Finite millimeter translation bounded to the supported project workspace.",
                    CadParameterKind.Vector3, required: true, CadParameterUnit.Length,
                    minimum: -1_000_000, maximum: 1_000_000, defaultValue: zeroVector),
                new CadParameterDefinition(
                    "rotationDegrees", "Rotation", "Finite XYZ Euler rotation in degrees; the host derives the rigid transform.",
                    CadParameterKind.Vector3, required: true, CadParameterUnit.Angle,
                    minimum: -360, maximum: 360, defaultValue: zeroVector),
            ],
            new CadSourceIdentity("bd-warehouse", "0.2.0", IndustrialImageSha256, "redistribution-blocked"),
            previewSupported: true,
            experimental: false);
    }

    private static CadParameterDefinition IndustrialCatalogParameter(PhotonCadIndustrialCatalogParameter parameter)
    {
        var kind = parameter.Kind switch
        {
            PhotonCadIndustrialParameterKind.Number => CadParameterKind.Number,
            PhotonCadIndustrialParameterKind.Integer => CadParameterKind.Integer,
            PhotonCadIndustrialParameterKind.Boolean => CadParameterKind.Boolean,
            PhotonCadIndustrialParameterKind.Choice => CadParameterKind.Choice,
            _ => throw new InvalidOperationException("industrial_catalog_parameter_kind_invalid"),
        };
        var unit = parameter.Id switch
        {
            "pressure_angle" => CadParameterUnit.Angle,
            "tooth_count" => CadParameterUnit.Count,
            "module" or "thickness" or "addendum" or "dedendum" or "root_fillet" => CadParameterUnit.Length,
            _ => (CadParameterUnit?)null,
        };
        return new CadParameterDefinition(
            parameter.Id,
            parameter.Label,
            $"Verified {parameter.Label.ToLowerInvariant()} parameter from the exact pinned industrial catalog.",
            kind,
            parameter.Required,
            unit,
            parameter.Minimum,
            parameter.Maximum,
            kind == CadParameterKind.Integer ? 1 : null,
            defaultValue: null,
            parameter.Choices.Select(choice => new CadChoice(choice.Token, choice.Label)));
    }

    private PhotonCadPreviewContext CurrentPreviewContext(PhotonCadCanonicalProject project)
    {
        lock (_rendererLock)
        {
            if (!_rendererAdmissionOpen || _disposed) throw new InvalidOperationException("renderer_generation_unavailable");
            return new PhotonCadPreviewContext(_rendererEpoch, _rendererSessionId, project.SessionId, project.ProjectId, project.Revision);
        }
    }

    private static bool IsIndustrialAvailabilityFailure(Exception exception) => exception is
        ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or
        System.ComponentModel.Win32Exception or CryptographicException or JsonException or TimeoutException;

    private static string SafeAvailabilityDiagnostic(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is CadContractException contract
                && SafeDiagnosticToken(contract.Code) is { } contractCode)
                return $"{current.GetType().Name}:{contractCode}";
            if (SafeDiagnosticToken(current.Message) is { } code) return $"{current.GetType().Name}:{code}";
        }
        return exception.GetType().Name;
    }

    private Task ResolvePreviewAsync(JsonElement message)
    {
        if (!TryEnvelope(message, out var requestId)
            || !TryIdentifier(message, "sessionId", out var sessionId)
            || !TryIdentifier(message, "projectId", out var projectId)
            || !TryIdentifier(message, "previewId", out var previewId)
            || !TryRevision(message, "revision", out var revision)
            || !TryIdentifier(message, "expectedDigest", out var expectedDigest)
            || !message.TryGetProperty("maximumBytes", out var maximumElement)
            || maximumElement.ValueKind != JsonValueKind.Number
            || !maximumElement.TryGetInt64(out var maximumBytes))
        {
            if (TryWireEnvelope(message, out requestId)) PostPreviewUnavailable(requestId, "invalid_request");
            return Task.CompletedTask;
        }
        return RunAsync(requestId, _ =>
        {
            try
            {
                PhotonCadPreviewReceipt receipt;
                lock (_previewLock)
                {
                    if (!_previewReceipts.TryGetValue(previewId, out receipt!))
                        throw new PhotonCadPreviewException("preview_unavailable");
                }
                if (!StringComparer.Ordinal.Equals(receipt.Context.CadSessionId, sessionId)
                    || !StringComparer.Ordinal.Equals(receipt.Context.ProjectId, projectId)
                    || receipt.Context.Revision != revision)
                    throw new PhotonCadPreviewException("preview_unavailable");
                var asset = _previewCustody.Resolve(new PhotonCadPreviewResolveRequest(
                    requestId, receipt.Context, previewId, expectedDigest, maximumBytes));
                lock (_previewLock) _previewResources[asset.Url.AbsoluteUri] = receipt.Context;
                _post(new
                {
                    type = "photonCad.preview.resolve.result",
                    version = ProtocolVersion,
                    value = new
                    {
                        contractVersion = ProtocolVersion,
                        requestId,
                        status = "available",
                        url = asset.Url.AbsoluteUri,
                        contentDigest = asset.ContentDigest,
                        byteLength = asset.ByteLength,
                        mediaType = asset.MediaType,
                        expiresAtUtc = asset.ExpiresAtUtc.ToUniversalTime()
                            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture),
                    },
                });
            }
            catch (Exception exception) when (exception is PhotonCadPreviewException or ArgumentException or InvalidOperationException)
            {
                PostPreviewUnavailable(requestId, "preview_unavailable");
            }
            return Task.CompletedTask;
        });
    }

    private Task HydratePreviewAsync(JsonElement message)
    {
        if (!TryEnvelope(message, out var requestId)
            || !TryIdentifier(message, "sessionId", out var sessionId)
            || !TryIdentifier(message, "projectId", out var projectId)
            || !TryRevision(message, "revision", out var revision))
        {
            if (TryWireEnvelope(message, out requestId)) PostPreviewHydrationUnavailable(requestId, "invalid_request");
            return Task.CompletedTask;
        }
        return RunAsync(requestId, async cancellationToken =>
        {
            try
            {
                if (_projectHost is null) throw new InvalidOperationException("committed_project_host_unavailable");
                var project = await _projectHost.ResolveCommittedProjectAsync(
                    requestId, sessionId, projectId, revision, cancellationToken).ConfigureAwait(false);
                var context = CurrentPreviewContext(project);
                var receipt = _previewCustody.SealCommitted(new PhotonCadCommittedPreviewReadback(context, project));
                lock (_previewLock)
                {
                    _previewReceipts[receipt.PreviewId] = receipt;
                    foreach (var stale in _previewReceipts.Where(pair =>
                            pair.Value.Context.RendererGeneration == receipt.Context.RendererGeneration
                            && StringComparer.Ordinal.Equals(pair.Value.Context.RendererSessionId, receipt.Context.RendererSessionId)
                            && StringComparer.Ordinal.Equals(pair.Value.Context.CadSessionId, receipt.Context.CadSessionId)
                            && StringComparer.Ordinal.Equals(pair.Value.Context.ProjectId, receipt.Context.ProjectId)
                            && !StringComparer.Ordinal.Equals(pair.Key, receipt.PreviewId))
                        .Select(pair => pair.Key).ToArray())
                        _previewReceipts.Remove(stale);
                }
                _post(new
                {
                    type = "photonCad.preview.hydrate.result",
                    version = ProtocolVersion,
                    value = new
                    {
                        contractVersion = ProtocolVersion,
                        requestId,
                        status = "available",
                        preview = ExternalPreviewReceipt(receipt),
                    },
                });
            }
            catch (Exception exception) when (exception is PhotonCadPreviewException or PhotonCadProjectException
                or InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
            {
                PostPreviewHydrationUnavailable(requestId, "preview_unavailable");
            }
        });
    }

    private void PostPreviewUnavailable(string requestId, string reason) => _post(new
    {
        type = "photonCad.preview.resolve.result",
        version = ProtocolVersion,
        value = new
        {
            contractVersion = ProtocolVersion,
            requestId,
            status = "unavailable",
            reason = SafeReason(reason),
        },
    });

    private void PostPreviewHydrationUnavailable(string requestId, string reason) => _post(new
    {
        type = "photonCad.preview.hydrate.result",
        version = ProtocolVersion,
        value = new
        {
            contractVersion = ProtocolVersion,
            requestId,
            status = "unavailable",
            reason = SafeReason(reason),
        },
    });

    internal PhotonCadPreviewResourceResponse? TryRespondPreviewResource(
        string method,
        Uri requestUri,
        bool rendererAuthorized,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        PhotonCadPreviewContext? context;
        lock (_previewLock)
        {
            _previewResources.Remove(requestUri.AbsoluteUri, out context);
        }
        return _previewCustody.TryRespond(new PhotonCadPreviewResourceRequest(
            method, requestUri, rendererAuthorized, context, headers));
    }

    private void RevokeAllPreviews()
    {
        PhotonCadPreviewContext[] contexts;
        lock (_previewLock)
        {
            contexts = _previewReceipts.Values.Select(value => value.Context)
                .Concat(_pendingPreviewRefreshes.Values.Select(value => value.Context))
                .Concat(_inflightPreviewRefreshes.Values.Select(value => value.Context))
                .Distinct()
                .ToArray();
            _previewReceipts.Clear();
            _previewResources.Clear();
            _pendingPreviewRefreshes.Clear();
            _inflightPreviewRefreshes.Clear();
        }
        foreach (var context in contexts) _previewCustody.RevokeProject(context);
    }

    private void RevokeProjectPreviews(string cadSessionId, string projectId)
    {
        PhotonCadPreviewContext[] contexts;
        lock (_previewLock)
        {
            static bool Matches(PhotonCadPreviewContext context, string sessionId, string targetProjectId) =>
                StringComparer.Ordinal.Equals(context.CadSessionId, sessionId)
                && StringComparer.Ordinal.Equals(context.ProjectId, targetProjectId);

            contexts = _previewReceipts.Values.Select(value => value.Context)
                .Concat(_pendingPreviewRefreshes.Values.Select(value => value.Context))
                .Concat(_inflightPreviewRefreshes.Values.Select(value => value.Context))
                .Where(context => Matches(context, cadSessionId, projectId))
                .Distinct()
                .ToArray();
            foreach (var previewId in _previewReceipts.Where(pair => Matches(pair.Value.Context, cadSessionId, projectId))
                .Select(pair => pair.Key).ToArray())
                _previewReceipts.Remove(previewId);
            foreach (var resource in _previewResources.Where(pair => Matches(pair.Value, cadSessionId, projectId))
                .Select(pair => pair.Key).ToArray())
                _previewResources.Remove(resource);
            foreach (var requestId in _pendingPreviewRefreshes.Where(pair => Matches(pair.Value.Context, cadSessionId, projectId))
                .Select(pair => pair.Key).ToArray())
                _pendingPreviewRefreshes.Remove(requestId);
            foreach (var requestId in _inflightPreviewRefreshes.Where(pair => Matches(pair.Value.Context, cadSessionId, projectId))
                .Select(pair => pair.Key).ToArray())
                _inflightPreviewRefreshes.Remove(requestId);
        }
        foreach (var context in contexts) _previewCustody.RevokeProject(context);
    }

    private void TryCreatePreviewRefreshMarker(string refreshRequestId, PhotonCadPreviewContext context, string projectContentDigest)
    {
        var now = _previewRefreshTimeProvider.GetUtcNow();
        foreach (var stale in _pendingPreviewRefreshes.Where(pair => pair.Value.ExpiresAtUtc <= now
            || pair.Value.RendererGeneration == context.RendererGeneration
                && StringComparer.Ordinal.Equals(pair.Value.RendererSessionId, context.RendererSessionId)
                && StringComparer.Ordinal.Equals(pair.Value.CadSessionId, context.CadSessionId)
                && StringComparer.Ordinal.Equals(pair.Value.ProjectId, context.ProjectId)).Select(pair => pair.Key).ToArray())
            _pendingPreviewRefreshes.Remove(stale);
        if (_pendingPreviewRefreshes.Count + _inflightPreviewRefreshes.Count >= MaximumPendingPreviewRefreshes
            || _pendingPreviewRefreshes.ContainsKey(refreshRequestId)
            || !IsSha256Digest(projectContentDigest)) return;
        _pendingPreviewRefreshes.Add(refreshRequestId, new PendingPreviewRefreshMarker(
            refreshRequestId, context.RendererGeneration, context.RendererSessionId, context.CadSessionId, context.ProjectId,
            context.Revision, projectContentDigest.ToLowerInvariant(), now + PreviewRefreshMarkerTimeToLive));
    }

    private bool TryBeginPreviewRefresh(JsonElement message, out PendingPreviewRefreshMarker? marker)
    {
        marker = null;
        if (!TryEnvelope(message, out var requestId)
            || !TryIdentifier(message, "sessionId", out var cadSessionId)
            || !TryIdentifier(message, "projectId", out var projectId)
            || !TryRevision(message, "knownRevision", out var revision)) return false;
        lock (_previewLock)
        {
            var now = _previewRefreshTimeProvider.GetUtcNow();
            foreach (var expired in _pendingPreviewRefreshes.Where(pair => pair.Value.ExpiresAtUtc <= now).Select(pair => pair.Key).ToArray())
                _pendingPreviewRefreshes.Remove(expired);
            if (!_pendingPreviewRefreshes.Remove(requestId, out var found)) return false;
            marker = found;
            var matches = found.ExpiresAtUtc > now
                && found.RendererGeneration == _rendererEpoch
                && StringComparer.Ordinal.Equals(found.RendererSessionId, _rendererSessionId)
                && StringComparer.Ordinal.Equals(found.CadSessionId, cadSessionId)
                && StringComparer.Ordinal.Equals(found.ProjectId, projectId)
                && found.Revision == revision
                && IsSha256Digest(found.ProjectContentDigest)
                && _previewReceipts.Values.Any(receipt => receipt.Context.RendererGeneration == found.RendererGeneration
                && StringComparer.Ordinal.Equals(receipt.Context.RendererSessionId, found.RendererSessionId)
                && StringComparer.Ordinal.Equals(receipt.Context.CadSessionId, found.CadSessionId)
                && StringComparer.Ordinal.Equals(receipt.Context.ProjectId, found.ProjectId)
                && receipt.Context.Revision == found.Revision);
            if (!matches) return false;
            _inflightPreviewRefreshes.Add(requestId, found);
            return true;
        }
    }

    private void ProcessProjectLifecycleResult(object message)
    {
        JsonElement frame;
        try { frame = JsonSerializer.SerializeToElement(message); }
        catch (Exception exception) when (exception is NotSupportedException or JsonException)
        {
            RevokeInflightPreviewRefreshes();
            return;
        }
        if (!frame.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String
            || !StringComparer.Ordinal.Equals(typeElement.GetString(), "photonCad.project.refresh.result")
            || !frame.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object
            || !TryIdentifier(value, "requestId", out var requestId)) return;

        PendingPreviewRefreshMarker marker;
        lock (_previewLock)
        {
            if (!_inflightPreviewRefreshes.Remove(requestId, out var found)) return;
            marker = found;
        }
        if (!RefreshResultMatches(marker, value))
        {
            RevokeProjectPreviews(marker.CadSessionId, marker.ProjectId);
            return;
        }
        PreserveExactProjectPreview(marker);
    }

    private bool RefreshResultMatches(PendingPreviewRefreshMarker marker, JsonElement value)
    {
        if (_previewRefreshTimeProvider.GetUtcNow() >= marker.ExpiresAtUtc
            || marker.RendererGeneration != _rendererEpoch
            || !StringComparer.Ordinal.Equals(marker.RendererSessionId, _rendererSessionId)
            || !value.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String
            || !StringComparer.Ordinal.Equals(status.GetString(), "opened")
            || !value.TryGetProperty("document", out var document) || document.ValueKind != JsonValueKind.Object
            || !document.TryGetProperty("contentDigest", out var digest) || digest.ValueKind != JsonValueKind.String
            || !FixedDigestEquals(digest.GetString() ?? string.Empty, marker.ProjectContentDigest)
            || !document.TryGetProperty("snapshot", out var snapshot) || snapshot.ValueKind != JsonValueKind.Object
            || !TryIdentifier(snapshot, "sessionId", out var cadSessionId)
            || !TryIdentifier(snapshot, "projectId", out var projectId)
            || !TryRevision(snapshot, "revision", out var revision)) return false;
        return StringComparer.Ordinal.Equals(cadSessionId, marker.CadSessionId)
            && StringComparer.Ordinal.Equals(projectId, marker.ProjectId)
            && revision == marker.Revision;
    }

    private void PreserveExactProjectPreview(PendingPreviewRefreshMarker marker)
    {
        PhotonCadPreviewContext[] staleContexts;
        var retained = false;
        lock (_previewLock)
        {
            static bool SameProject(PhotonCadPreviewContext context, PendingPreviewRefreshMarker value) =>
                StringComparer.Ordinal.Equals(context.CadSessionId, value.CadSessionId)
                && StringComparer.Ordinal.Equals(context.ProjectId, value.ProjectId);
            static bool Exact(PhotonCadPreviewContext context, PendingPreviewRefreshMarker value) =>
                SameProject(context, value)
                && context.RendererGeneration == value.RendererGeneration
                && StringComparer.Ordinal.Equals(context.RendererSessionId, value.RendererSessionId)
                && context.Revision == value.Revision;

            retained = _previewReceipts.Values.Any(receipt => Exact(receipt.Context, marker));
            staleContexts = _previewReceipts.Values.Select(receipt => receipt.Context)
                .Where(context => SameProject(context, marker) && !Exact(context, marker)).Distinct().ToArray();
            foreach (var previewId in _previewReceipts.Where(pair => SameProject(pair.Value.Context, marker) && !Exact(pair.Value.Context, marker))
                .Select(pair => pair.Key).ToArray())
                _previewReceipts.Remove(previewId);
            foreach (var resource in _previewResources.Where(pair => SameProject(pair.Value, marker) && !Exact(pair.Value, marker))
                .Select(pair => pair.Key).ToArray())
                _previewResources.Remove(resource);
            foreach (var pending in _pendingPreviewRefreshes.Where(pair => SameProject(pair.Value.Context, marker))
                .Select(pair => pair.Key).ToArray())
                _pendingPreviewRefreshes.Remove(pending);
        }
        foreach (var context in staleContexts) _previewCustody.RevokeProject(context);
        if (!retained) RevokeProjectPreviews(marker.CadSessionId, marker.ProjectId);
    }

    private void RevokeInflightPreviewRefreshes()
    {
        PendingPreviewRefreshMarker[] markers;
        lock (_previewLock)
        {
            markers = _inflightPreviewRefreshes.Values.ToArray();
            _inflightPreviewRefreshes.Clear();
        }
        foreach (var marker in markers) RevokeProjectPreviews(marker.CadSessionId, marker.ProjectId);
    }

    private static bool TryProjectBinding(JsonElement message, out string cadSessionId, out string projectId)
    {
        cadSessionId = string.Empty;
        projectId = string.Empty;
        return TryIdentifier(message, "sessionId", out cadSessionId)
            && TryIdentifier(message, "projectId", out projectId);
    }

    private async Task<ICadRuntimeBroker> GetBrokerAsync(CancellationToken cancellationToken)
    {
        Task<ICadRuntimeBroker> task;
        lock (_brokerLock)
        {
            _brokerTask ??= CreateBrokerAsync();
            task = _brokerTask;
        }
        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static CadRuntimeDescription RendererDescription(ICadRuntimeBroker broker)
    {
        var description = broker.Describe();
        if (description.Availability != CadRuntimeAvailability.Ready || description.Catalog is null || description.ActiveBundles is null)
            return description;
        var capabilities = description.Catalog.Capabilities
            .Where(capability => capability.Id is CadPinnedCapabilityCatalog.BoxCapabilityId or CadPinnedCapabilityCatalog.CylinderCapabilityId)
            .ToArray();
        var reasons = description.Catalog.Coverage.UnavailableReasons
            .Concat(["V1 exposes only persisted box and cylinder creation; STEP sealing is host-internal and other CAD actions remain unavailable."])
            .ToArray();
        var catalog = new CadCapabilityCatalog(
            description.Catalog.CatalogRevision,
            description.Catalog.GeneratedAtUtc,
            capabilities,
            new CadCatalogCoverage(
                description.Catalog.Coverage.Discovered,
                capabilities.Length,
                description.Catalog.Coverage.Discovered - capabilities.Length,
                reasons));
        return new CadRuntimeDescription(
            CadRuntimeAvailability.Ready,
            description.ReasonCode,
            description.Message,
            description.ActiveBundles,
            catalog);
    }

    private async Task<ICadRuntimeBroker> CreateBrokerAsync()
    {
        try { return await _brokerFactory(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or
            UnauthorizedAccessException or System.ComponentModel.Win32Exception or CryptographicException or JsonException)
        {
            DesktopLog.Write($"Photon CAD runtime provisioning failed closed: {exception.GetType().Name}");
            return new UnavailableCadRuntimeBroker();
        }
    }

    private async ValueTask<ICadRuntimeBroker> ProvisionBrokerAsync(CancellationToken cancellationToken)
    {
        var assets = Path.Combine(_installRoot, InstalledAssetRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var policy = Path.Combine(assets, "runtime-policy.json");
        var receipt = Path.Combine(assets, "bundle-receipt.json");
        var docker = DockerDesktopCliResolver.Resolve();
        if (!File.Exists(policy) || !File.Exists(receipt) || !File.Exists(docker))
            return new UnavailableCadRuntimeBroker();

        var workspace = CreateRuntimeWorkspace();
        try
        {
            await using var dockerStream = new FileStream(docker, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var dockerSha256 = Convert.ToHexString(await SHA256.HashDataAsync(dockerStream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
            var settings = new CadDockerRuntimeSettings(
                docker,
                dockerSha256,
                workspace,
                policy,
                receipt,
                AcceptedReceiptSha256,
                maximumConcurrentSessions: 1);
            return await CadDockerRuntimeBrokerFactory.ProvisionAsync(settings, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception provisionFailure)
        {
            try
            {
                DeleteOwnedRuntimeWorkspace(workspace);
                _runtimeWorkspace = null;
            }
            catch (Exception cleanupFailure) when (IsBoundedResetFailure(cleanupFailure))
            {
                throw new InvalidOperationException(
                    "Photon CAD provisioning failed and its owned workspace could not be cleaned up.",
                    new AggregateException(provisionFailure, cleanupFailure));
            }
            throw;
        }
    }

    private string CreateRuntimeWorkspace()
    {
        var parent = Path.Combine(Path.GetTempPath(), "PhotosAgapeAphthartos", "PhotonCad");
        Directory.CreateDirectory(parent);
        var workspace = Path.Combine(parent, $"session-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        if ((File.GetAttributes(workspace) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The Photon CAD runtime workspace is not a normal directory.");
        _runtimeWorkspace = workspace;
        return workspace;
    }

    private async ValueTask<RuntimeBinding?> GetOrCreateBindingAsync(
        ICadRuntimeBroker broker,
        ExternalRuntimeRequest external,
        CancellationToken cancellationToken)
    {
        var key = $"{external.SessionId}\n{external.ProjectId}";
        await _bindingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_bindings.TryGetValue(key, out var existing))
            {
                if (!existing.IsPoisoned) return existing;
                _bindings.Remove(key);
            }
            if (external.Revision != 0) return null;
            var opened = await broker.OpenProjectAsync(
                new CadOpenProjectRequest(new CadRequestId($"{external.RequestId}:open"), CadWorkspaceHandle.New(), "Photon CAD Project"),
                cancellationToken).ConfigureAwait(false);
            if (!opened.Succeeded) return null;
            var started = await broker.StartSessionAsync(
                new CadStartSessionRequest(new CadRequestId($"{external.RequestId}:start"), opened.Value!.Project, opened.Value.Revision),
                cancellationToken).ConfigureAwait(false);
            if (!started.Succeeded) return null;
            var binding = new RuntimeBinding(
                key,
                external.SessionId,
                external.ProjectId,
                opened.Value.Project,
                started.Value!.Session);
            _bindings.Add(key, binding);
            return binding;
        }
        finally { _bindingGate.Release(); }
    }

    private async ValueTask EvictBindingAsync(
        ICadRuntimeBroker broker,
        RuntimeBinding binding,
        string reason)
    {
        if (!binding.TryBeginEviction()) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await _bindingGate.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            if (_bindings.TryGetValue(binding.Key, out var current) && ReferenceEquals(current, binding))
                _bindings.Remove(binding.Key);
        }
        finally { _bindingGate.Release(); }

        var closed = await broker.CloseSessionAsync(
            new CadCloseSessionRequest(new CadRequestId($"close-{Guid.NewGuid():N}"), binding.Session),
            timeout.Token).ConfigureAwait(false);
        if (!closed.Succeeded)
            throw new InvalidOperationException($"The poisoned CAD runtime binding could not be revoked ({SafeReason(reason)}).");
    }

    private static CadOperationRequest CreateOperationRequest(
        CadRuntimeDescription description,
        RuntimeBinding binding,
        ExternalRuntimeRequest external,
        CadOperationMode mode,
        string capabilityId,
        JsonElement inputsElement,
        JsonElement targetsElement)
    {
        var capability = description.Catalog?.Capabilities.SingleOrDefault(item => item.Id == capabilityId)
            ?? throw new CadContractException("capability_not_available", nameof(capabilityId));
        var definitions = capability.Parameters.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var inputs = new List<CadOperationInput>();
        foreach (var property in inputsElement.EnumerateObject())
        {
            if (!definitions.TryGetValue(property.Name, out var definition))
                throw new CadContractException("unknown_parameter", nameof(inputsElement));
            inputs.Add(new CadOperationInput(property.Name, InputValue(definition.Kind, property.Value)));
        }
        var targets = targetsElement.EnumerateArray().Select(item => item.GetString()
            ?? throw new CadContractException("invalid_target", nameof(targetsElement))).ToArray();
        return new CadOperationRequest(
            new CadRequestId(external.RequestId),
            binding.Session,
            binding.Project,
            new CadRevision(external.Revision),
            mode,
            capabilityId,
            inputs,
            targets);
    }

    private static CadInputValue InputValue(CadParameterKind kind, JsonElement value) => kind switch
    {
        CadParameterKind.Number when value.ValueKind == JsonValueKind.Number => new CadNumberInputValue(value.GetDouble()),
        CadParameterKind.Integer when value.ValueKind == JsonValueKind.Number => new CadIntegerInputValue(value.GetInt64()),
        CadParameterKind.Boolean when value.ValueKind is JsonValueKind.True or JsonValueKind.False => new CadBooleanInputValue(value.GetBoolean()),
        CadParameterKind.Text when value.ValueKind == JsonValueKind.String => new CadTextInputValue(value.GetString()!),
        CadParameterKind.Choice when value.ValueKind == JsonValueKind.String => new CadTextInputValue(value.GetString()!, CadParameterKind.Choice),
        CadParameterKind.Entity when value.ValueKind == JsonValueKind.String => new CadTextInputValue(value.GetString()!, CadParameterKind.Entity),
        CadParameterKind.Vector3 when value.ValueKind == JsonValueKind.Object => new CadVectorInputValue(new CadVector3(
            RequiredDouble(value, "x"), RequiredDouble(value, "y"), RequiredDouble(value, "z"))),
        CadParameterKind.EntityList when value.ValueKind == JsonValueKind.Array => new CadEntityListInputValue(
            value.EnumerateArray().Select(item => item.GetString()
                ?? throw new CadContractException("invalid_entity", nameof(value)))),
        _ when value.ValueKind == JsonValueKind.Null => new CadNullInputValue(kind),
        _ => throw new CadContractException("input_kind_mismatch", nameof(value)),
    };

    private static PhotonCadSyncOperationInput ToSyncInput(KeyValuePair<string, CadInputValue> input) =>
        new(input.Key, input.Value switch
        {
            CadNumberInputValue number => PhotonCadSyncInputValue.Number(number.Value),
            CadIntegerInputValue integer => PhotonCadSyncInputValue.Integer(integer.Value),
            CadBooleanInputValue boolean => PhotonCadSyncInputValue.Boolean(boolean.Value),
            CadTextInputValue text when text.Kind == CadParameterKind.Text => PhotonCadSyncInputValue.Text(text.Value),
            CadTextInputValue text when text.Kind == CadParameterKind.Choice => PhotonCadSyncInputValue.Choice(text.Value),
            CadTextInputValue text when text.Kind == CadParameterKind.Entity => PhotonCadSyncInputValue.Entity(text.Value),
            CadVectorInputValue vector => PhotonCadSyncInputValue.Vector3(
                new PhotonCadSyncVector3(vector.Value.X, vector.Value.Y, vector.Value.Z)),
            CadEntityListInputValue entities => PhotonCadSyncInputValue.EntityList(entities.EntityIds),
            CadNullInputValue value => PhotonCadSyncInputValue.Null(value.Kind switch
            {
                CadParameterKind.Number => PhotonCadInputKindV1.Number,
                CadParameterKind.Integer => PhotonCadInputKindV1.Integer,
                CadParameterKind.Boolean => PhotonCadInputKindV1.Boolean,
                CadParameterKind.Text => PhotonCadInputKindV1.Text,
                CadParameterKind.Choice => PhotonCadInputKindV1.Choice,
                CadParameterKind.Vector3 => PhotonCadInputKindV1.Vector3,
                CadParameterKind.Entity => PhotonCadInputKindV1.Entity,
                CadParameterKind.EntityList => PhotonCadInputKindV1.EntityList,
                _ => throw new CadContractException("input_kind_mismatch", nameof(input)),
            }),
            _ => throw new CadContractException("input_kind_mismatch", nameof(input)),
        });

    private static double RequiredDouble(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number
            ? property.GetDouble()
            : throw new CadContractException("number_required", name);

    private static object ExternalOperationResult(CadOperationResult result, ExternalRuntimeRequest external)
    {
        var value = new Dictionary<string, object?>
        {
            ["contractVersion"] = ProtocolVersion,
            ["requestId"] = external.RequestId,
            ["projectId"] = external.ProjectId,
            ["baseRevision"] = result.BaseRevision.Value,
            ["resultingRevision"] = result.ResultingRevision.Value,
            ["status"] = result.Status switch
            {
                CadOperationStatus.Accepted => "accepted",
                CadOperationStatus.Rejected => "rejected",
                _ => "unavailable",
            },
            ["stale"] = result.Stale,
            ["reason"] = result.Reason,
            ["issues"] = result.Issues.Select(ExternalIssue).ToArray(),
        };
        if (result.Snapshot is not null) value["snapshot"] = ExternalSnapshot(result.Snapshot, external);
        // The runtime does not currently produce a GLB preview asset. Do not expose
        // an artifact path or manufacture a preview receipt.
        return value;
    }

    private static object ExternalCommittedOperationResult(
        PhotonCadRuntimeSyncResult result,
        ExternalRuntimeRequest external,
        PhotonCadCanonicalProjectCodecV1 codec,
        PhotonCadPreviewReceipt? preview = null)
    {
        var state = codec.Inspect(result.SavedProject);
        var projected = new
        {
            contractVersion = ProtocolVersion,
            requestId = external.RequestId,
            projectId = external.ProjectId,
            baseRevision = external.Revision,
            resultingRevision = state.Revision,
            status = "accepted",
            stale = false,
            reason = "accepted-and-saved",
            issues = state.Issues.Select(issue => new
            {
                code = issue.Code,
                severity = issue.Severity switch
                {
                    PhotonCadIssueSeverityV1.Information => "info",
                    PhotonCadIssueSeverityV1.Warning => "warning",
                    PhotonCadIssueSeverityV1.Error => "error",
                    _ => "error",
                },
                message = issue.Message,
                entityIds = issue.EntityIds.ToArray(),
            }).ToArray(),
            snapshot = PhotonCadSnapshotWireProjection.Snapshot(state, ProtocolVersion),
        };
        if (preview is null) return projected;
        var extended = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in JsonSerializer.SerializeToElement(projected).EnumerateObject())
            extended[property.Name] = property.Value.Clone();
        extended["preview"] = ExternalPreviewReceipt(preview);
        return extended;
    }

    private static object ExternalPreviewReceipt(PhotonCadPreviewReceipt preview) => new
    {
        previewId = preview.PreviewId,
        projectId = preview.Context.ProjectId,
        revision = preview.Context.Revision,
        contentDigest = preview.ContentDigest,
        units = preview.Units == PhotonCadProjectUnit.Millimeter ? "millimeter" : "inch",
        bounds = new
        {
            minimum = new { x = preview.Bounds.Minimum.X, y = preview.Bounds.Minimum.Y, z = preview.Bounds.Minimum.Z },
            maximum = new { x = preview.Bounds.Maximum.X, y = preview.Bounds.Maximum.Y, z = preview.Bounds.Maximum.Z },
        },
        entityCount = preview.EntityCount,
    };

    private static object ExternalSnapshot(CadProjectSnapshot snapshot, ExternalRuntimeRequest external) => new
    {
        contractVersion = ProtocolVersion,
        sessionId = external.SessionId,
        projectId = external.ProjectId,
        revision = snapshot.Revision.Value,
        title = snapshot.Title,
        units = snapshot.Units == CadLengthUnit.Millimeter ? "millimeter" : "inch",
        mode = snapshot.ModeValue == CadProjectMode.Canonical ? "canonical" : "scratch",
        entities = snapshot.Entities.Select(entity =>
        {
            var value = new Dictionary<string, object?>
            {
                ["id"] = entity.Id,
                ["parentId"] = entity.ParentId,
                ["kind"] = entity.KindValue.ToString().ToLowerInvariant(),
                ["name"] = entity.Name,
                ["visible"] = entity.Visible,
                ["suppressed"] = entity.Suppressed,
            };
            if (entity.SourceCapabilityId is not null) value["sourceCapabilityId"] = entity.SourceCapabilityId;
            return value;
        }).ToArray(),
        occurrences = Array.Empty<object>(),
        operations = snapshot.Operations.Select(operation => new
        {
            id = operation.Id,
            capabilityId = operation.CapabilityId,
            label = operation.Label,
            createdAtUtc = Utc(operation.CreatedAtUtc),
            state = operation.StateValue.ToString().ToLowerInvariant(),
        }).ToArray(),
        issues = snapshot.Issues.Select(ExternalIssue).ToArray(),
        dirty = snapshot.Dirty,
    };

    private static object ExternalIssue(CadValidationFinding issue) => new
    {
        code = issue.Code,
        severity = issue.SeverityName,
        message = issue.Message,
        entityIds = issue.EntityIds.ToArray(),
    };

    private void PostOperationUnavailable(ExternalRuntimeRequest request, string reason)
    {
        _post(new
        {
            type = "photonCad.execute.result",
            version = ProtocolVersion,
            value = new
            {
                contractVersion = ProtocolVersion,
                requestId = request.RequestId,
                projectId = request.ProjectId,
                baseRevision = request.Revision,
                resultingRevision = request.Revision,
                status = "unavailable",
                stale = false,
                reason = SafeReason(reason),
                issues = Array.Empty<object>(),
            },
        });
    }

    private void PostVerificationUnavailable(ExternalRuntimeRequest request, string reason)
    {
        _post(new
        {
            type = "photonCad.verify.result",
            version = ProtocolVersion,
            value = new
            {
                contractVersion = ProtocolVersion,
                requestId = request.RequestId,
                projectId = request.ProjectId,
                revision = request.Revision,
                status = "unavailable",
                stale = false,
                issues = new[] { new { code = SafeReason(reason), severity = "info", message = "The verified CAD runtime is unavailable.", entityIds = Array.Empty<string>() } },
                measuredAtUtc = Utc(DateTimeOffset.UtcNow),
            },
        });
    }

    private void PostVerificationFailed(ExternalRuntimeRequest request, string reason)
    {
        _post(new
        {
            type = "photonCad.verify.result",
            version = ProtocolVersion,
            value = new
            {
                contractVersion = ProtocolVersion,
                requestId = request.RequestId,
                projectId = request.ProjectId,
                revision = request.Revision,
                status = "failed",
                stale = false,
                issues = new[]
                {
                    new
                    {
                        code = SafeReason(reason),
                        severity = "error",
                        message = "The committed CAD project failed canonical verification.",
                        entityIds = Array.Empty<string>(),
                    },
                },
                measuredAtUtc = Utc(DateTimeOffset.UtcNow),
            },
        });
    }

    private void PostReleaseReviewUnavailable(JsonElement message)
    {
        if (!TryWireEnvelope(message, out var requestId) || !TryIdentifier(message, "projectId", out var projectId)
            || !TryRevision(message, "revision", out var revision)) return;
        _post(new
        {
            type = "photonCad.release.review.result",
            version = ProtocolVersion,
            value = new
            {
                contractVersion = ProtocolVersion,
                requestId,
                projectId,
                revision,
                status = "unavailable",
                reason = "release_provider_unavailable",
                files = Array.Empty<object>(),
                issues = Array.Empty<object>(),
            },
        });
    }

    private void PostReleaseCommitUnavailable(JsonElement message)
    {
        if (!TryWireEnvelope(message, out var requestId)) return;
        _post(new
        {
            type = "photonCad.release.commit.result",
            version = ProtocolVersion,
            value = new { contractVersion = ProtocolVersion, requestId, status = "unavailable", reason = "release_provider_unavailable" },
        });
    }

    private void PostCommercialUnavailable(string type, JsonElement message)
    {
        if (!TryWireEnvelope(message, out var requestId)) return;
        var resultType = $"{type}.result";
        object value = type switch
        {
            "photonCad.commercial.bom.review" when TryIdentifier(message, "projectId", out var projectId)
                && TryRevision(message, "projectRevision", out var revision) => new
                {
                    contractVersion = ProtocolVersion,
                    requestId,
                    projectId,
                    projectRevision = revision,
                    status = "unavailable",
                    reason = "commercial_provider_unavailable",
                    files = Array.Empty<object>(),
                },
            "photonCad.commercial.document.review" => new
            {
                contractVersion = ProtocolVersion,
                requestId,
                status = "unavailable",
                reason = "commercial_provider_unavailable",
                pages = Array.Empty<object>(),
            },
            _ => new { contractVersion = ProtocolVersion, requestId, status = "unavailable", reason = "commercial_provider_unavailable" },
        };
        _post(new { type = resultType, version = ProtocolVersion, value });
    }

    private void Cancel(JsonElement message)
    {
        if (!TryWireEnvelope(message, out _) || !TryIdentifier(message, "targetRequestId", out var target)) return;
        if (_active.TryGetValue(target, out var cancellation)) cancellation.Cancel();
    }

    private async Task RunAsync(string requestId, Func<CancellationToken, Task> operation)
    {
        if (!_coreCapacity.Wait(0))
        {
            PostError(requestId, "runtime_request_capacity_reached", retryable: true);
            return;
        }
        using var cancellation = new CancellationTokenSource(RequestTimeout);
        var accepted = false;
        lock (_rendererLock)
        {
            if (!_disposed && _rendererAdmissionOpen) accepted = _active.TryAdd(requestId, cancellation);
        }
        if (!accepted)
        {
            lock (_rendererLock)
            {
                if (_rendererAdmissionOpen) PostError(requestId, "duplicate_request", retryable: false);
            }
            _coreCapacity.Release();
            return;
        }
        try { await operation(cancellation.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            PostError(requestId, "cancelled", retryable: true);
        }
        catch (Exception exception)
        {
            DesktopLog.Write($"Photon CAD bridge failed safely: {SafeAvailabilityDiagnostic(exception)}");
            PostError(requestId, "runtime_operation_failed", retryable: true);
        }
        finally
        {
            _active.TryRemove(requestId, out _);
            _coreCapacity.Release();
        }
    }

    private void PostError(string requestId, string code, bool retryable) => _post(new
    {
        type = "photonCad.error",
        version = ProtocolVersion,
        requestId,
        code = SafeReason(code),
        retryable,
    });

    private static bool TryEnvelope(JsonElement message, out string requestId)
    {
        if (!TryWireEnvelope(message, out requestId)
            || !message.TryGetProperty("contractVersion", out var contractVersion)
            || contractVersion.ValueKind != JsonValueKind.Number
            || !contractVersion.TryGetInt32(out var contract)
            || contract != ProtocolVersion)
            return false;
        return true;
    }

    private static bool TryWireEnvelope(JsonElement message, out string requestId)
    {
        requestId = string.Empty;
        return message.TryGetProperty("version", out var version) && version.TryGetInt32(out var protocol) && protocol == ProtocolVersion
            && TryIdentifier(message, "requestId", out requestId);
    }

    private static bool TryOperationEnvelope(
        JsonElement message,
        out ExternalRuntimeRequest request,
        out CadOperationMode mode,
        out string capabilityId,
        out JsonElement inputs,
        out JsonElement targets)
    {
        request = default!;
        mode = default;
        capabilityId = string.Empty;
        inputs = default;
        targets = default;
        if (!TryEnvelope(message, out var requestId)
            || !TryIdentifier(message, "sessionId", out var sessionId)
            || !TryIdentifier(message, "projectId", out var projectId)
            || !TryRevision(message, "baseRevision", out var revision)
            || !TryIdentifier(message, "capabilityId", out capabilityId)
            || !message.TryGetProperty("mode", out var modeElement) || modeElement.ValueKind != JsonValueKind.String
            || !message.TryGetProperty("inputs", out inputs) || inputs.ValueKind != JsonValueKind.Object || inputs.GetRawText().Length > 64 * 1024
            || !message.TryGetProperty("targetEntityIds", out targets) || targets.ValueKind != JsonValueKind.Array || targets.GetArrayLength() > 10_000)
            return false;
        mode = modeElement.GetString() switch
        {
            "suggest" => CadOperationMode.Suggest,
            "scratch" => CadOperationMode.Scratch,
            _ => (CadOperationMode)(-1),
        };
        if (!Enum.IsDefined(mode)) return false;
        request = new ExternalRuntimeRequest(requestId, sessionId, projectId, revision);
        return true;
    }

    private static bool TryVerificationEnvelope(JsonElement message, out ExternalRuntimeRequest request, out CadVerificationCheck[] checks)
    {
        request = default!;
        checks = [];
        if (!TryEnvelope(message, out var requestId)
            || !TryIdentifier(message, "sessionId", out var sessionId)
            || !TryIdentifier(message, "projectId", out var projectId)
            || !TryRevision(message, "revision", out var revision)
            || !message.TryGetProperty("checks", out var checkArray) || checkArray.ValueKind != JsonValueKind.Array
            || checkArray.GetArrayLength() is < 1 or > 5) return false;
        var values = new List<CadVerificationCheck>();
        foreach (var element in checkArray.EnumerateArray())
        {
            var value = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
            values.Add(value switch
            {
                "valid-solids" => CadVerificationCheck.ValidSolids,
                "interference" => CadVerificationCheck.Interference,
                "dimensions" => CadVerificationCheck.Dimensions,
                "assembly-structure" => CadVerificationCheck.AssemblyStructure,
                "export-readiness" => CadVerificationCheck.ExportReadiness,
                _ => (CadVerificationCheck)(-1),
            });
        }
        if (values.Any(value => !Enum.IsDefined(value)) || values.Distinct().Count() != values.Count) return false;
        request = new ExternalRuntimeRequest(requestId, sessionId, projectId, revision);
        checks = values.ToArray();
        return true;
    }

    private static bool TryIdentifier(JsonElement message, string name, out string value)
    {
        value = string.Empty;
        if (!message.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String) return false;
        var candidate = element.GetString();
        if (!IsIdentifier(candidate)) return false;
        value = candidate!;
        return true;
    }

    private static bool IsIdentifier(string? candidate) =>
        !string.IsNullOrEmpty(candidate) && candidate.Length <= MaximumRequestCharacters && char.IsAsciiLetterOrDigit(candidate[0])
        && candidate.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or ':');

    private static bool TryRevision(JsonElement message, string name, out long revision)
    {
        revision = -1;
        return message.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt64(out revision) && revision is >= 0 and <= 9_007_199_254_740_991L;
    }

    private static string SafeErrorCode(Exception exception) => exception is CadContractException contract
        ? SafeReason(contract.Code)
        : "invalid_request";

    private static string SafeReason(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unavailable";
        var safe = new string(value.Take(96).Where(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or ':').ToArray());
        return safe.Length > 0 && char.IsAsciiLetterOrDigit(safe[0]) ? safe : "unavailable";
    }

    private static string Utc(DateTimeOffset value) => value.ToUniversalTime()
        .ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await ResetAsync().ConfigureAwait(false);
        try
        {
            await _projects.DisposeAsync().ConfigureAwait(false);
            await _previewCustody.DisposeAsync().ConfigureAwait(false);
            await _stepExportHost.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _disposed = true;
            _bindingGate.Dispose();
            _resetGate.Dispose();
            _coreCapacity.Dispose();
        }
    }

    internal async Task<long> ResetAsync()
    {
        long epoch;
        Task handlersDrained;
        lock (_rendererLock)
        {
            if (_disposed) return _rendererEpoch;
            _rendererAdmissionOpen = false;
            _rendererSessionId = "renderer-unavailable";
            epoch = ++_rendererEpoch;
            handlersDrained = _inflightHandlers == 0
                ? Task.CompletedTask
                : _handlersDrained?.Task ?? Task.CompletedTask;
        }
        await _resetGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var cancellation in _active.Values) cancellation.Cancel();

            Exception? projectResetFailure = null;
            try { await _projects.ResetAsync().ConfigureAwait(false); }
            catch (Exception exception) when (IsBoundedResetFailure(exception))
            {
                DesktopLog.Write($"Photon CAD project reset failed closed: {exception.GetType().Name}");
                projectResetFailure = exception;
            }

            await handlersDrained.ConfigureAwait(false);
            foreach (var cancellation in _active.Values) cancellation.Dispose();
            _active.Clear();
            RevokeAllPreviews();

            Task<ICadRuntimeBroker>? brokerTask;
            lock (_brokerLock) brokerTask = _brokerTask;
            Exception? brokerResetFailure = null;
            if (brokerTask is not null)
            {
                try
                {
                    var broker = await brokerTask.ConfigureAwait(false);
                    if (broker is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception) when (IsBoundedResetFailure(exception))
                {
                    DesktopLog.Write($"Photon CAD runtime reset failed closed: {exception.GetType().Name}");
                    brokerResetFailure = exception;
                }
            }

            if (brokerResetFailure is null)
            {
                lock (_brokerLock)
                {
                    if (ReferenceEquals(_brokerTask, brokerTask)) _brokerTask = null;
                }
                await _bindingGate.WaitAsync().ConfigureAwait(false);
                try { _bindings.Clear(); }
                finally { _bindingGate.Release(); }
                var workspace = _runtimeWorkspace;
                if (workspace is not null)
                {
                    try
                    {
                        DeleteOwnedRuntimeWorkspace(workspace);
                        _runtimeWorkspace = null;
                    }
                    catch (Exception exception) when (IsBoundedResetFailure(exception))
                    {
                        DesktopLog.Write($"Photon CAD workspace cleanup failed closed: {exception.GetType().Name}");
                        brokerResetFailure = exception;
                    }
                }
            }

            var industrialWorkspace = _industrialWorkspace;
            if (industrialWorkspace is not null)
            {
                try
                {
                    DeleteOwnedIndustrialWorkspace(industrialWorkspace);
                    _industrialWorkspace = null;
                    lock (_brokerLock) _industrialRuntimeTask = null;
                }
                catch (Exception exception) when (IsBoundedResetFailure(exception))
                {
                    DesktopLog.Write($"Photon CAD industrial workspace cleanup failed closed: {exception.GetType().Name}");
                    brokerResetFailure = exception;
                }
            }

            var manualWorkspace = _manualWorkspace;
            if (manualWorkspace is not null)
            {
                try
                {
                    DeleteOwnedProviderWorkspace(manualWorkspace, "PhotonCadManual");
                    _manualWorkspace = null;
                    lock (_brokerLock) _manualRuntimeTask = null;
                }
                catch (Exception exception) when (IsBoundedResetFailure(exception))
                {
                    DesktopLog.Write($"Photon CAD manual workspace cleanup failed closed: {exception.GetType().Name}");
                    brokerResetFailure = exception;
                }
            }

            if (projectResetFailure is not null || brokerResetFailure is not null)
            {
                var failures = new[] { projectResetFailure, brokerResetFailure }.OfType<Exception>().ToArray();
                throw new InvalidOperationException("Photon CAD reset did not revoke every owned authority.", new AggregateException(failures));
            }

            lock (_rendererLock)
            {
                if (_rendererEpoch == epoch) _completedResetEpoch = epoch;
            }
            return epoch;
        }
        finally { _resetGate.Release(); }
    }

    private static bool IsBoundedResetFailure(Exception exception) => exception is
        InvalidOperationException or IOException or UnauthorizedAccessException or CryptographicException
        or System.ComponentModel.Win32Exception or JsonException or TimeoutException;

    private static void DeleteOwnedRuntimeWorkspace(string workspace)
    {
        try
        {
            var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PhotosAgapeAphthartos", "PhotonCad"));
            var full = Path.GetFullPath(workspace);
            if (!string.Equals(Path.GetDirectoryName(full), expectedParent, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(full).StartsWith("session-", StringComparison.Ordinal))
                throw new InvalidOperationException("The Photon CAD runtime workspace is outside its owned cleanup root.");
            var rootAttributes = File.GetAttributes(full);
            var entries = Directory.EnumerateFileSystemEntries(full, "*", SearchOption.AllDirectories).ToArray();
            if ((rootAttributes & FileAttributes.ReparsePoint) != 0
                || entries.Any(entry => (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0))
                throw new InvalidOperationException("The Photon CAD runtime workspace contains a linked path and was retained for review.");
            Directory.Delete(full, recursive: true);
        }
        catch (DirectoryNotFoundException) { }
    }

    private static void DeleteOwnedIndustrialWorkspace(string workspace)
    {
        var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PhotosAgapeAphthartos", "PhotonCadIndustrial"));
        var full = Path.GetFullPath(workspace);
        var prefix = expectedParent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(full).StartsWith("session-", StringComparison.Ordinal)
            || !Directory.Exists(full)
            || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The industrial CAD workspace is outside the owned cleanup root.");
        foreach (var entry in Directory.EnumerateFileSystemEntries(full, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The industrial CAD workspace contains a link and cannot be deleted safely.");
        }
        Directory.Delete(full, recursive: true);
    }

    private static void DeleteOwnedProviderWorkspace(string workspace, string providerDirectory)
    {
        if (providerDirectory.Length is < 1 or > 64
            || providerDirectory.Any(character => !char.IsAsciiLetterOrDigit(character)))
            throw new IOException("The CAD provider cleanup identity is invalid.");
        var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PhotosAgapeAphthartos", providerDirectory));
        var full = Path.GetFullPath(workspace);
        var prefix = expectedParent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(full).StartsWith("session-", StringComparison.Ordinal)
            || !Directory.Exists(full)
            || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The CAD provider workspace is outside the owned cleanup root.");
        foreach (var entry in Directory.EnumerateFileSystemEntries(full, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The CAD provider workspace contains a link and cannot be deleted safely.");
        }
        Directory.Delete(full, recursive: true);
    }

    private static PhotonCadSealedArtifactDelta ValidateAssemblyMutationEnvelope(
        PhotonCadRuntimeSyncRequest expected,
        PhotonCadSealedMutationDelta mutation,
        string capabilityId,
        string shapeFailure,
        string previewFailure)
    {
        if (mutation.BaseRevision != expected.BaseRevision
            || mutation.ResultingRevision != checked(expected.BaseRevision + 2)
            || mutation.Operations.Count != 2
            || mutation.Operations[0].AppliedRevision != checked(expected.BaseRevision + 1)
            || !StringComparer.Ordinal.Equals(mutation.Operations[0].CapabilityId, capabilityId)
            || mutation.Operations[0].Mode != PhotonCadOperationModeV1.Scratch
            || mutation.Operations[1].AppliedRevision != checked(expected.BaseRevision + 2)
            || !StringComparer.Ordinal.Equals(mutation.Operations[1].CapabilityId, PhotonCadAssemblyContract.PreviewCapabilityId)
            || mutation.Operations[1].Mode != PhotonCadOperationModeV1.Scratch
            || mutation.Entities.Count != 0
            || mutation.Issues.Count != 0
            || mutation.OccurrenceMergeMode != PhotonCadCollectionMergeMode.ReplaceAll
            || mutation.BomMergeMode != PhotonCadCollectionMergeMode.ReplaceAll
            || mutation.Artifacts.Count != 1)
            throw new InvalidOperationException(shapeFailure);
        var artifact = mutation.Artifacts[0];
        if (artifact.Role != PhotonCadArtifactRoleV1.ProjectPreview
            || artifact.Kind != PhotonCadArtifactKindV1.Glb
            || artifact.OwnerEntityId is not null
            || artifact.Revision != mutation.ResultingRevision
            || artifact.Bounds is null
            || artifact.ReplacesContentDigest is null
            || !StringComparer.Ordinal.Equals(artifact.MediaType, PhotonCadPreviewContract.MediaType)
            || !StringComparer.Ordinal.Equals(artifact.OperationId, mutation.Operations[1].Id))
            throw new InvalidOperationException(previewFailure);
        return artifact;
    }

    private sealed class AssemblyPlacementGuardProvider : IPhotonCadSealedMutationProvider, IPhotonCadSealedMutationCompensator
    {
        private readonly PhotonCadRuntimeSyncRequest _expected;
        private readonly AssemblyPlacementInputs _inputs;
        private readonly IPhotonCadSealedMutationProvider _provider;
        private readonly IPhotonCadSealedMutationCompensator _compensator;

        internal AssemblyPlacementGuardProvider(
            PhotonCadRuntimeSyncRequest expected,
            AssemblyPlacementInputs inputs,
            IPhotonCadSealedMutationProvider provider,
            IPhotonCadSealedMutationCompensator compensator)
        {
            _expected = expected;
            _inputs = inputs;
            _provider = provider;
            _compensator = compensator;
        }

        public async ValueTask<PhotonCadSealedMutationDelta> ApplyAsync(
            PhotonCadSealedMutationProviderRequest request,
            CancellationToken cancellationToken = default)
        {
            var mutation = await _provider.ApplyAsync(request, cancellationToken).ConfigureAwait(false);
            try
            {
                Validate(request, mutation);
                return mutation;
            }
            catch (Exception validationFailure)
            {
                try
                {
                    await _compensator.CompensateAsync(
                        mutation,
                        SafeDiagnosticToken(validationFailure.Message) ?? "assembly_host_result_rejected",
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception compensationFailure)
                {
                    throw new AggregateException(
                        "assembly_host_result_rejected_compensation_failed",
                        validationFailure,
                        compensationFailure);
                }
                throw;
            }
        }

        public ValueTask CompensateAsync(
            PhotonCadSealedMutationDelta mutation,
            string reason,
            CancellationToken cancellationToken = default) =>
            _compensator.CompensateAsync(mutation, reason, cancellationToken);

        private void Validate(
            PhotonCadSealedMutationProviderRequest request,
            PhotonCadSealedMutationDelta mutation)
        {
            if (!ReferenceEquals(request.Request, _expected))
                throw new InvalidOperationException("assembly_host_result_shape_rejected");
            _ = ValidateAssemblyMutationEnvelope(
                _expected,
                mutation,
                PhotonCadAssemblyContract.PlaceCapabilityId,
                "assembly_host_result_shape_rejected",
                "assembly_host_preview_rejected");

            if (!request.BaseEntities.Any(value => StringComparer.Ordinal.Equals(value.Id, _inputs.SourceEntityId)))
                throw new InvalidOperationException("assembly_host_source_rejected");
            var expectedParent = _inputs.ParentOccurrenceId ?? $"{_inputs.SourceEntityId}.occ";
            if (!request.BaseOccurrences.Any(value => StringComparer.Ordinal.Equals(value.OccurrenceId, expectedParent)))
                throw new InvalidOperationException("assembly_host_parent_rejected");
            if (mutation.Occurrences.Count != request.BaseOccurrences.Count + 1)
                throw new InvalidOperationException("assembly_host_occurrence_count_rejected");
            foreach (var existing in request.BaseOccurrences)
            {
                var preserved = mutation.Occurrences.SingleOrDefault(value =>
                    StringComparer.Ordinal.Equals(value.OccurrenceId, existing.OccurrenceId));
                if (preserved is null
                    || !StringComparer.Ordinal.Equals(preserved.ParentOccurrenceId, existing.ParentOccurrenceId)
                    || !StringComparer.Ordinal.Equals(preserved.PartNumber, existing.PartNumber)
                    || !StringComparer.Ordinal.Equals(preserved.SourceEntityId, existing.SourceEntityId)
                    || !SameTransform(preserved.Transform, existing.Transform))
                    throw new InvalidOperationException("assembly_host_base_occurrence_changed");
            }
            var added = mutation.Occurrences.Where(value =>
                !request.BaseOccurrences.Any(existing => StringComparer.Ordinal.Equals(existing.OccurrenceId, value.OccurrenceId))).ToArray();
            if (added.Length != 1) throw new InvalidOperationException("assembly_host_new_occurrence_count_rejected");
            if (!StringComparer.Ordinal.Equals(added[0].SourceEntityId, _inputs.SourceEntityId))
                throw new InvalidOperationException("assembly_host_new_occurrence_source_rejected");
            if (!StringComparer.Ordinal.Equals(added[0].ParentOccurrenceId, expectedParent))
                throw new InvalidOperationException("assembly_host_new_occurrence_parent_rejected");
            if (!SameTransform(added[0].Transform, _inputs.Transform))
                throw new InvalidOperationException("assembly_host_new_occurrence_transform_rejected");

            var expectedQuantities = mutation.Occurrences
                .GroupBy(value => value.SourceEntityId, StringComparer.Ordinal)
                .ToDictionary(value => value.Key, value => (double)value.Count(), StringComparer.Ordinal);
            if (mutation.Bom.Count != expectedQuantities.Count
                || mutation.Bom.Any(value => !expectedQuantities.TryGetValue(value.SourceEntityId, out var quantity)
                    || value.Quantity != quantity || value.Unit != PhotonCadBomUnit.Each))
                throw new InvalidOperationException("assembly_host_bom_rejected");
        }

        private static bool SameTransform(IReadOnlyList<double> left, IReadOnlyList<double> right) =>
            left.Count == right.Count && left.Select(NormalizedBits)
                .SequenceEqual(right.Select(NormalizedBits));

        private static long NormalizedBits(double value) => BitConverter.DoubleToInt64Bits(value == 0d ? 0d : value);
    }

    private sealed class AssemblyTransformGuardProvider : IPhotonCadSealedMutationProvider, IPhotonCadSealedMutationCompensator
    {
        private readonly PhotonCadRuntimeSyncRequest _expected;
        private readonly AssemblyTransformInputs _inputs;
        private readonly IPhotonCadSealedMutationProvider _provider;
        private readonly IPhotonCadSealedMutationCompensator _compensator;

        internal AssemblyTransformGuardProvider(
            PhotonCadRuntimeSyncRequest expected,
            AssemblyTransformInputs inputs,
            IPhotonCadSealedMutationProvider provider,
            IPhotonCadSealedMutationCompensator compensator)
        {
            _expected = expected;
            _inputs = inputs;
            _provider = provider;
            _compensator = compensator;
        }

        public async ValueTask<PhotonCadSealedMutationDelta> ApplyAsync(
            PhotonCadSealedMutationProviderRequest request,
            CancellationToken cancellationToken = default)
        {
            var mutation = await _provider.ApplyAsync(request, cancellationToken).ConfigureAwait(false);
            try
            {
                Validate(request, mutation);
                return mutation;
            }
            catch (Exception validationFailure)
            {
                try
                {
                    await _compensator.CompensateAsync(
                        mutation,
                        SafeDiagnosticToken(validationFailure.Message) ?? "assembly_transform_host_result_rejected",
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception compensationFailure)
                {
                    throw new AggregateException(
                        "assembly_transform_host_result_rejected_compensation_failed",
                        validationFailure,
                        compensationFailure);
                }
                throw;
            }
        }

        public ValueTask CompensateAsync(
            PhotonCadSealedMutationDelta mutation,
            string reason,
            CancellationToken cancellationToken = default) =>
            _compensator.CompensateAsync(mutation, reason, cancellationToken);

        private void Validate(
            PhotonCadSealedMutationProviderRequest request,
            PhotonCadSealedMutationDelta mutation)
        {
            if (!ReferenceEquals(request.Request, _expected))
                throw new InvalidOperationException("assembly_transform_host_result_shape_rejected");
            _ = ValidateAssemblyMutationEnvelope(
                _expected,
                mutation,
                PhotonCadAssemblyContract.TransformCapabilityId,
                "assembly_transform_host_result_shape_rejected",
                "assembly_transform_host_preview_rejected");

            var target = request.BaseOccurrences.SingleOrDefault(value =>
                StringComparer.Ordinal.Equals(value.OccurrenceId, _inputs.OccurrenceId));
            if (target is null || !StringComparer.Ordinal.Equals(target.SourceEntityId, _inputs.SourceEntityId)
                || mutation.Occurrences.Count != request.BaseOccurrences.Count)
                throw new InvalidOperationException("assembly_transform_host_target_rejected");
            foreach (var existing in request.BaseOccurrences)
            {
                var updated = mutation.Occurrences.SingleOrDefault(value =>
                    StringComparer.Ordinal.Equals(value.OccurrenceId, existing.OccurrenceId));
                var expectedTransform = StringComparer.Ordinal.Equals(existing.OccurrenceId, _inputs.OccurrenceId)
                    ? _inputs.Transform
                    : existing.Transform;
                if (updated is null
                    || !StringComparer.Ordinal.Equals(updated.ParentOccurrenceId, existing.ParentOccurrenceId)
                    || !StringComparer.Ordinal.Equals(updated.PartNumber, existing.PartNumber)
                    || !StringComparer.Ordinal.Equals(updated.SourceEntityId, existing.SourceEntityId)
                    || !SameTransform(updated.Transform, expectedTransform))
                    throw new InvalidOperationException("assembly_transform_host_occurrence_rejected");
            }

            var expectedQuantities = mutation.Occurrences
                .GroupBy(value => value.SourceEntityId, StringComparer.Ordinal)
                .ToDictionary(value => value.Key, value => (double)value.Count(), StringComparer.Ordinal);
            if (mutation.Bom.Count != expectedQuantities.Count
                || mutation.Bom.Any(value => !expectedQuantities.TryGetValue(value.SourceEntityId, out var quantity)
                    || value.Quantity != quantity || value.Unit != PhotonCadBomUnit.Each))
                throw new InvalidOperationException("assembly_transform_host_bom_rejected");
        }

        private static bool SameTransform(IReadOnlyList<double> left, IReadOnlyList<double> right) =>
            left.Count == right.Count && left.Select(NormalizedBits).SequenceEqual(right.Select(NormalizedBits));

        private static long NormalizedBits(double value) => BitConverter.DoubleToInt64Bits(value == 0d ? 0d : value);
    }

    private sealed class AssemblyRemovalGuardProvider : IPhotonCadSealedMutationProvider, IPhotonCadSealedMutationCompensator
    {
        private readonly PhotonCadRuntimeSyncRequest _expected;
        private readonly AssemblyRemovalInputs _inputs;
        private readonly IPhotonCadSealedMutationProvider _provider;
        private readonly IPhotonCadSealedMutationCompensator _compensator;

        internal AssemblyRemovalGuardProvider(
            PhotonCadRuntimeSyncRequest expected,
            AssemblyRemovalInputs inputs,
            IPhotonCadSealedMutationProvider provider,
            IPhotonCadSealedMutationCompensator compensator)
        {
            _expected = expected;
            _inputs = inputs;
            _provider = provider;
            _compensator = compensator;
        }

        public async ValueTask<PhotonCadSealedMutationDelta> ApplyAsync(
            PhotonCadSealedMutationProviderRequest request,
            CancellationToken cancellationToken = default)
        {
            var mutation = await _provider.ApplyAsync(request, cancellationToken).ConfigureAwait(false);
            try
            {
                Validate(request, mutation);
                return mutation;
            }
            catch (Exception validationFailure)
            {
                try
                {
                    await _compensator.CompensateAsync(
                        mutation,
                        SafeDiagnosticToken(validationFailure.Message) ?? "assembly_remove_host_result_rejected",
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception compensationFailure)
                {
                    throw new AggregateException(
                        "assembly_remove_host_result_rejected_compensation_failed",
                        validationFailure,
                        compensationFailure);
                }
                throw;
            }
        }

        public ValueTask CompensateAsync(
            PhotonCadSealedMutationDelta mutation,
            string reason,
            CancellationToken cancellationToken = default) =>
            _compensator.CompensateAsync(mutation, reason, cancellationToken);

        private void Validate(
            PhotonCadSealedMutationProviderRequest request,
            PhotonCadSealedMutationDelta mutation)
        {
            if (!ReferenceEquals(request.Request, _expected))
                throw new InvalidOperationException("assembly_remove_host_result_shape_rejected");
            _ = ValidateAssemblyMutationEnvelope(
                _expected,
                mutation,
                PhotonCadAssemblyContract.RemoveCapabilityId,
                "assembly_remove_host_result_shape_rejected",
                "assembly_remove_host_preview_rejected");

            var target = request.BaseOccurrences.SingleOrDefault(value =>
                StringComparer.Ordinal.Equals(value.OccurrenceId, _inputs.OccurrenceId));
            if (target is null || !StringComparer.Ordinal.Equals(target.SourceEntityId, _inputs.SourceEntityId))
                throw new InvalidOperationException("assembly_remove_host_target_rejected");
            var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { target.OccurrenceId };
            bool changed;
            do
            {
                changed = false;
                foreach (var occurrence in request.BaseOccurrences)
                {
                    if (occurrence.ParentOccurrenceId is not null
                        && removed.Contains(occurrence.ParentOccurrenceId)
                        && removed.Add(occurrence.OccurrenceId))
                        changed = true;
                }
            } while (changed);
            var expectedOccurrences = request.BaseOccurrences.Where(value => !removed.Contains(value.OccurrenceId)).ToArray();
            if (expectedOccurrences.Length == 0 || mutation.Occurrences.Count != expectedOccurrences.Length)
                throw new InvalidOperationException("assembly_remove_host_occurrence_count_rejected");
            foreach (var expected in expectedOccurrences)
            {
                var actual = mutation.Occurrences.SingleOrDefault(value =>
                    StringComparer.Ordinal.Equals(value.OccurrenceId, expected.OccurrenceId));
                if (actual is null
                    || !StringComparer.Ordinal.Equals(actual.ParentOccurrenceId, expected.ParentOccurrenceId)
                    || !StringComparer.Ordinal.Equals(actual.PartNumber, expected.PartNumber)
                    || !StringComparer.Ordinal.Equals(actual.SourceEntityId, expected.SourceEntityId)
                    || !SameTransform(actual.Transform, expected.Transform))
                    throw new InvalidOperationException("assembly_remove_host_base_occurrence_changed");
            }

            var expectedQuantities = expectedOccurrences
                .GroupBy(value => value.SourceEntityId, StringComparer.Ordinal)
                .ToDictionary(value => value.Key, value => (double)value.Count(), StringComparer.Ordinal);
            if (mutation.Bom.Count != expectedQuantities.Count
                || mutation.Bom.Any(value => !expectedQuantities.TryGetValue(value.SourceEntityId, out var quantity)
                    || value.Quantity != quantity || value.Unit != PhotonCadBomUnit.Each))
                throw new InvalidOperationException("assembly_remove_host_bom_rejected");
        }

        private static bool SameTransform(IReadOnlyList<double> left, IReadOnlyList<double> right) =>
            left.Count == right.Count && left.Select(NormalizedBits).SequenceEqual(right.Select(NormalizedBits));

        private static long NormalizedBits(double value) => BitConverter.DoubleToInt64Bits(value == 0d ? 0d : value);
    }

    private sealed class GeometryRuntimeMutationProvider : IPhotonCadSealedMutationProvider, IPhotonCadSealedMutationCompensator
    {
        private readonly PhotonCadBridge _owner;
        private readonly ICadRuntimeBroker _broker;
        private readonly RuntimeBinding _binding;
        private readonly CadRuntimeDescription _description;
        private readonly CadOperationRequest _createRequest;
        private readonly string _logicalEntityId;
        private string? _runtimeEntityId;
        private int _applied;

        internal GeometryRuntimeMutationProvider(
            PhotonCadBridge owner,
            ICadRuntimeBroker broker,
            RuntimeBinding binding,
            CadRuntimeDescription description,
            CadOperationRequest createRequest,
            string logicalEntityId)
        {
            _owner = owner;
            _broker = broker;
            _binding = binding;
            _description = description;
            _createRequest = createRequest;
            _logicalEntityId = logicalEntityId;
        }

        public async ValueTask<PhotonCadSealedMutationDelta> ApplyAsync(
            PhotonCadSealedMutationProviderRequest request,
            CancellationToken cancellationToken = default)
        {
            try
            {
                if (Interlocked.Exchange(ref _applied, 1) != 0)
                    throw new InvalidOperationException("A CAD mutation provider is single use.");
                RequireProviderRequest(request);
                var mappedRuntimeEntities = _binding.SnapshotEntities(request.Request.BaseRevision, request.BaseEntities);

                var createWireRequest = new CadOperationRequest(
                    new CadRequestId($"create-{Guid.NewGuid():N}"),
                    _binding.Session,
                    _binding.Project,
                    new CadRevision(request.Request.BaseRevision),
                    CadOperationMode.Scratch,
                    request.Request.CapabilityId,
                    _createRequest.Inputs.Select(input => new CadOperationInput(input.Key, input.Value)),
                    []);
                var created = RequireAccepted(
                    await _broker.ExecuteAsync(createWireRequest, cancellationToken).ConfigureAwait(false),
                    createWireRequest,
                    checked(request.Request.BaseRevision + 1),
                    artifacts: 0);
                var createdEntity = RequireCreatedEntity(created, mappedRuntimeEntities);
                _runtimeEntityId = createdEntity.Id;

                var exportWireRequest = new CadOperationRequest(
                    new CadRequestId($"export-{Guid.NewGuid():N}"),
                    _binding.Session,
                    _binding.Project,
                    new CadRevision(checked(request.Request.BaseRevision + 1)),
                    CadOperationMode.Scratch,
                    CadPinnedCapabilityCatalog.ExportStepCapabilityId,
                    [],
                    [createdEntity.Id]);
                var exported = RequireAccepted(
                    await _broker.ExecuteAsync(exportWireRequest, cancellationToken).ConfigureAwait(false),
                    exportWireRequest,
                    checked(request.Request.BaseRevision + 2),
                    artifacts: 1);
                RequireExportSnapshot(exported, mappedRuntimeEntities, createdEntity);
                var artifact = exported.Artifacts.Single();
                var bytes = await ReadSealedStepAsync(artifact, createdEntity.Id, exported, cancellationToken).ConfigureAwait(false);
                var evidence = ProviderEvidence(request.Request.CapabilityId);
                var createOperationId = $"operation-{Guid.NewGuid():N}";
                var exportOperationId = $"operation-{Guid.NewGuid():N}";
                var createTime = created.Snapshot!.Operations[^1].CreatedAtUtc;
                var exportTime = exported.Snapshot!.Operations[^1].CreatedAtUtc;
                var digest = Sha256(bytes);

                return new PhotonCadSealedMutationDelta(
                    $"mutation-{Guid.NewGuid():N}",
                    request.Request.RequestId,
                    request.Request.SessionId,
                    request.Request.ProjectId,
                    request.Request.BaseRevision,
                    checked(request.Request.BaseRevision + 2),
                    [
                        new PhotonCadAppliedOperationDelta(
                            checked(request.Request.BaseRevision + 1),
                            createOperationId,
                            request.Request.CapabilityId,
                            request.Request.CapabilityId == CadPinnedCapabilityCatalog.BoxCapabilityId ? "Create box" : "Create cylinder",
                            createTime,
                            request.Request.Mode,
                            request.Request.Inputs,
                            request.Request.TargetEntityIds,
                            evidence),
                        new PhotonCadAppliedOperationDelta(
                            checked(request.Request.BaseRevision + 2),
                            exportOperationId,
                            CadPinnedCapabilityCatalog.ExportStepCapabilityId,
                            "Seal authoritative STEP",
                            exportTime,
                            PhotonCadOperationModeV1.Scratch,
                            [],
                            [_logicalEntityId],
                            evidence),
                    ],
                    entities:
                    [
                        new PhotonCadEntityV1(
                            _logicalEntityId,
                            null,
                            PhotonCadEntityKindV1.Body,
                            request.Request.CapabilityId == CadPinnedCapabilityCatalog.BoxCapabilityId ? "Box" : "Cylinder",
                            visible: true,
                            suppressed: false,
                            request.Request.CapabilityId),
                    ],
                    artifacts:
                    [
                        new PhotonCadSealedArtifactDelta(
                            PhotonCadArtifactRoleV1.AuthoritativeGeometry,
                            PhotonCadArtifactKindV1.Step,
                            _logicalEntityId,
                            checked(request.Request.BaseRevision + 2),
                            bytes,
                            bytes.LongLength,
                            digest,
                            "model/step",
                            bounds: null,
                            exportOperationId,
                            evidence),
                    ]);
            }
            catch (Exception failure)
            {
                try { await _owner.EvictBindingAsync(_broker, _binding, "provider_failed").ConfigureAwait(false); }
                catch (Exception cleanupFailure)
                {
                    throw new InvalidOperationException(
                        "The CAD provider failed and its runtime binding could not be revoked.",
                        new AggregateException(failure, cleanupFailure));
                }
                throw;
            }
        }

        public ValueTask CompensateAsync(
            PhotonCadSealedMutationDelta mutation,
            string reason,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(mutation);
            return _owner.EvictBindingAsync(_broker, _binding, reason);
        }

        internal void CommitSucceeded(long resultingRevision)
        {
            var runtimeEntityId = _runtimeEntityId
                ?? throw new InvalidOperationException("The committed CAD entity was not bound to a runtime entity.");
            _binding.CommitEntity(_logicalEntityId, runtimeEntityId, resultingRevision);
        }

        private void RequireProviderRequest(PhotonCadSealedMutationProviderRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (request.Units != PhotonCadProjectUnit.Millimeter
                || request.Request.Mode != PhotonCadOperationModeV1.Scratch
                || request.Request.CapabilityId is not (CadPinnedCapabilityCatalog.BoxCapabilityId or CadPinnedCapabilityCatalog.CylinderCapabilityId)
                || request.Request.TargetEntityIds.Count != 1
                || !StringComparer.Ordinal.Equals(request.Request.TargetEntityIds[0], _logicalEntityId)
                || !StringComparer.Ordinal.Equals(request.Request.SessionId, _binding.ExternalSessionId)
                || !StringComparer.Ordinal.Equals(request.Request.ProjectId, _binding.ExternalProjectId)
                || _createRequest.Project != _binding.Project
                || _createRequest.Session != _binding.Session
                || _createRequest.BaseRevision.Value != request.Request.BaseRevision
                || !StringComparer.Ordinal.Equals(_createRequest.CapabilityId, request.Request.CapabilityId)
                || _createRequest.Mode != CadOperationMode.Scratch
                || _createRequest.TargetEntityIds.Count != 0)
                throw new InvalidOperationException("The CAD provider request is not bound to the typed primitive operation.");
        }

        private CadOperationResult RequireAccepted(
            CadResult<CadOperationResult> response,
            CadOperationRequest request,
            long expectedRevision,
            int artifacts)
        {
            if (!response.Succeeded) throw new InvalidOperationException("The isolated CAD operation was unavailable.");
            var result = response.Value!;
            if (result.RequestId != request.RequestId
                || result.Project != _binding.Project
                || result.BaseRevision.Value != request.BaseRevision.Value
                || result.ResultingRevision.Value != expectedRevision
                || result.Status != CadOperationStatus.Accepted
                || result.Stale
                || result.Snapshot is null
                || result.Snapshot.Session != _binding.Session
                || result.Snapshot.Project != _binding.Project
                || result.Snapshot.Revision.Value != expectedRevision
                || result.Snapshot.Units != CadLengthUnit.Millimeter
                || result.Snapshot.ModeValue != CadProjectMode.Scratch
                || !result.Snapshot.Dirty
                || result.Issues.Count != 0
                || result.Artifacts.Count != artifacts
                || result.Preview is not null
                || result.Verification is not null
                || result.InterchangePackage is not null)
                throw new InvalidOperationException("The isolated CAD operation returned a malformed or foreign result.");
            return result;
        }

        private CadProjectEntity RequireCreatedEntity(
            CadOperationResult result,
            IReadOnlyDictionary<string, string> mappedRuntimeEntities)
        {
            var snapshot = result.Snapshot!;
            if (snapshot.Entities.Count != mappedRuntimeEntities.Count + 1
                || snapshot.Operations.Count != result.ResultingRevision.Value
                || snapshot.Operations[^1].CapabilityId != _createRequest.CapabilityId
                || mappedRuntimeEntities.Values.Any(id => snapshot.Entities.All(entity => !StringComparer.Ordinal.Equals(entity.Id, id))))
                throw new InvalidOperationException("The isolated CAD create result did not preserve the exact runtime state.");
            var created = snapshot.Entities
                .Where(entity => !mappedRuntimeEntities.Values.Contains(entity.Id, StringComparer.Ordinal))
                .ToArray();
            if (created.Length != 1
                || created[0].KindValue != CadEntityKind.Body
                || created[0].ParentId is not null
                || !created[0].Visible
                || created[0].Suppressed
                || !StringComparer.Ordinal.Equals(created[0].SourceCapabilityId, _createRequest.CapabilityId))
                throw new InvalidOperationException("The isolated CAD create result did not identify one exact solid.");
            return created[0];
        }

        private static void RequireExportSnapshot(
            CadOperationResult result,
            IReadOnlyDictionary<string, string> mappedRuntimeEntities,
            CadProjectEntity createdEntity)
        {
            var snapshot = result.Snapshot!;
            if (snapshot.Entities.Count != mappedRuntimeEntities.Count + 1
                || snapshot.Entities.Count(entity => StringComparer.Ordinal.Equals(entity.Id, createdEntity.Id)) != 1
                || mappedRuntimeEntities.Values.Any(id => snapshot.Entities.All(entity => !StringComparer.Ordinal.Equals(entity.Id, id)))
                || snapshot.Operations.Count != result.ResultingRevision.Value
                || !StringComparer.Ordinal.Equals(snapshot.Operations[^1].CapabilityId, CadPinnedCapabilityCatalog.ExportStepCapabilityId))
                throw new InvalidOperationException("The isolated CAD STEP result was not bound to the created solid.");
        }

        private async ValueTask<byte[]> ReadSealedStepAsync(
            CadArtifactHandle artifact,
            string runtimeEntityId,
            CadOperationResult exportResult,
            CancellationToken cancellationToken)
        {
            if (exportResult.Snapshot!.Entities.All(entity => !StringComparer.Ordinal.Equals(entity.Id, runtimeEntityId)))
                throw new InvalidOperationException("The STEP artifact target is not present in the exact runtime snapshot.");
            var request = new CadArtifactReadRequest(
                new CadRequestId($"receipt-{Guid.NewGuid():N}"),
                _binding.Session,
                artifact,
                MaximumSealedStepBytes);
            var receiptResult = await _broker.GetArtifactReceiptAsync(request, cancellationToken).ConfigureAwait(false);
            if (!receiptResult.Succeeded) throw new InvalidOperationException("The STEP artifact receipt was unavailable.");
            var receipt = receiptResult.Value!;
            RequireDescriptor(receipt, artifact);

            var leaseResult = await _broker.OpenArtifactReadAsync(request, cancellationToken).ConfigureAwait(false);
            if (!leaseResult.Succeeded) throw new InvalidOperationException("The STEP artifact lease was unavailable.");
            await using var lease = leaseResult.Value!;
            RequireDescriptor(lease.Descriptor, artifact);
            if (!DescriptorEqual(receipt, lease.Descriptor)
                || !lease.CanRead
                || lease.CanWrite
                || lease.Length != receipt.ByteLength
                || receipt.ByteLength > int.MaxValue)
                throw new InvalidOperationException("The STEP artifact lease did not match its immutable receipt.");

            var bytes = GC.AllocateUninitializedArray<byte>(checked((int)receipt.ByteLength));
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = await lease.ReadAsync(bytes.AsMemory(offset), cancellationToken).ConfigureAwait(false);
                if (read <= 0) throw new EndOfStreamException("The STEP artifact ended before its receipt length.");
                offset = checked(offset + read);
            }
            var trailing = new byte[1];
            if (await lease.ReadAsync(trailing, cancellationToken).ConfigureAwait(false) != 0)
                throw new InvalidOperationException("The STEP artifact exceeded its receipt length.");
            if (!FixedDigestEquals(Sha256(bytes), receipt.ContentDigest))
                throw new CryptographicException("The STEP artifact digest did not match its immutable receipt.");
            return bytes;
        }

        private void RequireDescriptor(CadArtifactDescriptor descriptor, CadArtifactHandle artifact)
        {
            if (descriptor.Session != _binding.Session
                || descriptor.Artifact != artifact
                || descriptor.ByteLength <= 0
                || descriptor.ByteLength > MaximumSealedStepBytes
                || !StringComparer.Ordinal.Equals(descriptor.MediaType, "model/step"))
                throw new InvalidOperationException("The STEP artifact descriptor was malformed or foreign.");
        }

        private PhotonCadProviderEvidence ProviderEvidence(string capabilityId)
        {
            var bundles = _description.ActiveBundles
                ?? throw new InvalidOperationException("The verified CAD bundle identity is missing.");
            var catalog = _description.Catalog
                ?? throw new InvalidOperationException("The verified CAD catalog is missing.");
            var geometry = bundles.Geometry;
            var capability = catalog.Capabilities.SingleOrDefault(value => value.Id == capabilityId)
                ?? throw new InvalidOperationException("The primitive capability identity is missing.");
            if (_description.Availability != CadRuntimeAvailability.Ready
                || !StringComparer.Ordinal.Equals(geometry.BundleId, "photon-cad-geometry")
                || !StringComparer.Ordinal.Equals(geometry.BundleVersion, CadDockerRuntimeIdentity.GeometryBundleVersion)
                || !StringComparer.Ordinal.Equals(geometry.RuntimeProvider, "docker")
                || !StringComparer.Ordinal.Equals(geometry.SourceRevision, CadDockerRuntimeIdentity.GeometryRevision)
                || !StringComparer.Ordinal.Equals(geometry.TargetRuntime, "linux-amd64")
                || !FixedDigestEquals(geometry.ManifestSha256, AcceptedReceiptSha256)
                || !StringComparer.Ordinal.Equals(catalog.CatalogRevision, CadPinnedCapabilityCatalog.Revision)
                || !StringComparer.Ordinal.Equals(capability.Source.Package, "build123d-mcp")
                || !StringComparer.Ordinal.Equals(capability.Source.Version, "0.3.80")
                || !FixedDigestEquals(capability.Source.Digest, CadDockerRuntimeIdentity.GeometrySourceArchiveSha256)
                || !StringComparer.Ordinal.Equals(capability.Source.License, "Apache-2.0"))
                throw new InvalidOperationException("The CAD result is not bound to the accepted geometry bundle.");
            var source = new PhotonCadSourceIdentityV1(
                capability.Source.Package,
                capability.Source.Version,
                GeometryImageSha256,
                capability.Source.License);
            return new PhotonCadProviderEvidence(
                PhotonCadBackendV1.Geometry,
                "photon.cad.geometry.docker.v1",
                [GeometryProtocolId],
                catalog.CatalogRevision,
                geometry.BundleId,
                geometry.ManifestSha256,
                AcceptedReceiptSha256,
                GeometryImageSha256,
                CadDockerRuntimeIdentity.GeometryBaseDigest,
                source);
        }

        private static bool DescriptorEqual(CadArtifactDescriptor left, CadArtifactDescriptor right) =>
            left.Session == right.Session
            && left.Artifact == right.Artifact
            && left.ByteLength == right.ByteLength
            && StringComparer.Ordinal.Equals(left.MediaType, right.MediaType)
            && FixedDigestEquals(left.ContentDigest, right.ContentDigest);
    }

    private sealed class RuntimeBinding
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, string> _runtimeEntities = new(StringComparer.Ordinal);
        private long _revision;
        private int _poisoned;
        private int _evictionStarted;

        internal RuntimeBinding(
            string key,
            string externalSessionId,
            string externalProjectId,
            CadProjectHandle project,
            CadSessionHandle session)
        {
            Key = key;
            ExternalSessionId = externalSessionId;
            ExternalProjectId = externalProjectId;
            Project = project;
            Session = session;
        }

        internal string Key { get; }
        internal string ExternalSessionId { get; }
        internal string ExternalProjectId { get; }
        internal CadProjectHandle Project { get; }
        internal CadSessionHandle Session { get; }
        internal bool IsPoisoned => Volatile.Read(ref _poisoned) != 0;

        internal IReadOnlyDictionary<string, string> SnapshotEntities(
            long expectedRevision,
            IReadOnlyList<PhotonCadProviderBaseEntity> baseEntities)
        {
            lock (_sync)
            {
                if (IsPoisoned || _revision != expectedRevision)
                    throw new InvalidOperationException("The CAD runtime revision does not match the canonical project.");
                var baseIds = baseEntities.Select(value => value.Id).OrderBy(value => value, StringComparer.Ordinal).ToArray();
                var mappedIds = _runtimeEntities.Keys.OrderBy(value => value, StringComparer.Ordinal).ToArray();
                if (!baseIds.SequenceEqual(mappedIds, StringComparer.Ordinal))
                    throw new InvalidOperationException("The CAD runtime entity map does not match the canonical project.");
                return new Dictionary<string, string>(_runtimeEntities, StringComparer.Ordinal);
            }
        }

        internal void CommitEntity(string logicalEntityId, string runtimeEntityId, long resultingRevision)
        {
            lock (_sync)
            {
                if (IsPoisoned || resultingRevision != checked(_revision + 2)
                    || _runtimeEntities.ContainsKey(logicalEntityId)
                    || _runtimeEntities.Values.Contains(runtimeEntityId, StringComparer.Ordinal))
                    throw new InvalidOperationException("The CAD runtime entity commitment is stale or duplicated.");
                _runtimeEntities.Add(logicalEntityId, runtimeEntityId);
                _revision = resultingRevision;
            }
        }

        internal bool TryBeginEviction()
        {
            Volatile.Write(ref _poisoned, 1);
            return Interlocked.CompareExchange(ref _evictionStarted, 1, 0) == 0;
        }
    }

    private static string Sha256(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static bool FixedDigestEquals(string left, string right)
    {
        static string Normalize(string value) => value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? value[7..] : value;
        var leftBytes = System.Text.Encoding.ASCII.GetBytes(Normalize(left).ToLowerInvariant());
        var rightBytes = System.Text.Encoding.ASCII.GetBytes(Normalize(right).ToLowerInvariant());
        return leftBytes.Length == rightBytes.Length
            && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static bool IsSha256Digest(string value) =>
        value.Length == 71 && value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
        && value.AsSpan(7).ToArray().All(Uri.IsHexDigit);

    private sealed record ExternalRuntimeRequest(string RequestId, string SessionId, string ProjectId, long Revision);
    private sealed record PendingPreviewRefreshMarker(
        string RequestId,
        long RendererGeneration,
        string RendererSessionId,
        string CadSessionId,
        string ProjectId,
        long Revision,
        string ProjectContentDigest,
        DateTimeOffset ExpiresAtUtc)
    {
        internal PhotonCadPreviewContext Context => new(
            RendererGeneration, RendererSessionId, CadSessionId, ProjectId, Revision);
    }
}
