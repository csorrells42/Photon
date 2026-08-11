using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PhotonCadPreviews;
using PhotonCadProjects;
using PhotonCadProjects.Codec;
using PhotonCadProjects.DesktopAdapter;
using PhotonCadProjects.RuntimeSync;
using PhotonCadProjects.Windows;
using PhotonCadRuntime;
using PhotonCadRuntime.IndustrialProvider;

namespace HermesDesktop;

internal sealed record IndustrialMutationRequest(
    string RequestId,
    string SessionId,
    string ProjectId,
    long BaseRevision,
    string EntityId,
    string CapabilityId,
    IReadOnlyDictionary<string, double> Inputs);

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
    internal const string IndustrialEvidenceSelectionFileName = "evidence-selection.json";
    internal const string PreviewResourcePathPrefix = "/api/photon-cad/previews/";
    internal const string IndustrialImageSha256 = "sha256:eda304290edbf75c33352e3df857a40539c48e20508d4025f63f7d4508ff25f9";
    private const string GeometryImageSha256 = "33d9c839840115640b08dd3c4142b7f29624329408155fe1484e1d88c3891703";
    private const string GeometryProtocolId = "mcp-2025-06-18";
    private const long MaximumSealedStepBytes = 64L * 1024 * 1024;

    private const int MaximumRequestCharacters = 128;
    private const int MaximumActiveCoreRequests = 8;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);

    private readonly string _installRoot;
    private readonly Action<object> _post;
    private readonly Func<CancellationToken, ValueTask<ICadRuntimeBroker>> _brokerFactory;
    private readonly Func<IndustrialMutationRequest, CancellationToken, ValueTask<IndustrialMutationBinding>>? _industrialBindingFactory;
    private readonly bool _legacyBrokerTestMode;
    private readonly PhotonCadProjectDesktopDispatcher _projects;
    private readonly PhotonCadCanonicalProjectCodecV1? _projectCodec;
    private readonly PhotonCadWindowsDesktopProjectHost? _projectHost;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _bindingGate = new(1, 1);
    private readonly SemaphoreSlim _resetGate = new(1, 1);
    private readonly SemaphoreSlim _coreCapacity = new(MaximumActiveCoreRequests, MaximumActiveCoreRequests);
    private readonly Dictionary<string, RuntimeBinding> _bindings = new(StringComparer.Ordinal);
    private readonly object _brokerLock = new();
    private readonly object _previewLock = new();
    private readonly object _rendererLock = new();
    private readonly PhotonCadPreviewCustody _previewCustody;
    private readonly Dictionary<string, PhotonCadPreviewReceipt> _previewReceipts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PhotonCadPreviewContext> _previewResources = new(StringComparer.Ordinal);
    private Task<ICadRuntimeBroker>? _brokerTask;
    private Task<PhotonCadIndustrialProviderRuntime>? _industrialRuntimeTask;
    private TaskCompletionSource? _handlersDrained;
    private string? _runtimeWorkspace;
    private string? _industrialWorkspace;
    private string _rendererSessionId = "renderer-unavailable";
    private long _rendererEpoch;
    private long _completedResetEpoch = -1;
    private int _inflightHandlers;
    private bool _rendererAdmissionOpen;
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
        Func<IndustrialMutationRequest, CancellationToken, ValueTask<IndustrialMutationBinding>>? industrialBindingFactory = null)
    {
        _installRoot = Path.GetFullPath(installRoot ?? throw new ArgumentNullException(nameof(installRoot)));
        ArgumentNullException.ThrowIfNull(post);
        _post = message =>
        {
            lock (_rendererLock)
            {
                if (_disposed || !_rendererAdmissionOpen) return;
                LogProjectFrame(message);
                post(message);
            }
        };
        _brokerFactory = brokerFactory ?? ProvisionBrokerAsync;
        _industrialBindingFactory = industrialBindingFactory;
        _legacyBrokerTestMode = brokerFactory is not null && industrialBindingFactory is null;
        if ((runtimeProjectHost is null) != (projectCodec is null))
            throw new ArgumentException("The runtime project host and codec must be supplied together.");
        if (projects is null)
        {
            _projectCodec = projectCodec ?? new PhotonCadCanonicalProjectCodecV1();
            _projectHost = runtimeProjectHost ?? new PhotonCadWindowsDesktopProjectHost(_projectCodec, projectDialog);
            _projects = new PhotonCadProjectDesktopDispatcher(
                _projectHost,
                new PhotonCadProjectWireProjection(_projectCodec),
                _post);
        }
        else
        {
            _projects = projects;
            _projectHost = runtimeProjectHost;
            _projectCodec = projectCodec;
        }
        var previewOrigin = workbenchOrigin ?? new Uri("https://127.0.0.1:9119/", UriKind.Absolute);
        _previewCustody = new PhotonCadPreviewCustody(
            _projectCodec ?? new PhotonCadCanonicalProjectCodecV1(),
            new PhotonCadPreviewCustodyOptions(previewOrigin, PreviewResourcePathPrefix));
    }

    private static void LogProjectFrame(object message)
    {
        try
        {
            var frame = JsonSerializer.SerializeToElement(message);
            if (!frame.TryGetProperty("type", out var typeElement)
                || typeElement.GetString() is not { } type
                || !type.StartsWith("photonCad.project.", StringComparison.Ordinal))
            {
                return;
            }

            string? status = null;
            string? reason = null;
            if (frame.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Object)
            {
                if (value.TryGetProperty("status", out var statusElement)) status = statusElement.GetString();
                if (value.TryGetProperty("reason", out var reasonElement)) reason = reasonElement.GetString();
            }
            else if (frame.TryGetProperty("code", out var codeElement))
            {
                status = "error";
                reason = codeElement.GetString();
            }

            // Project status and reason are already identifier-bounded. Never serialize the full
            // frame: it can carry opaque handles and project metadata that diagnostics do not need.
            DesktopLog.Write($"Photon CAD project frame: type={type}, status={status ?? "none"}, reason={reason ?? "none"}");
        }
        catch
        {
            // Diagnostics cannot affect the host-to-renderer result path.
        }
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
                case "photonCad.cancel":
                    Cancel(message);
                    break;
                case "photonCad.preview.resolve":
                    await ResolvePreviewAsync(message).ConfigureAwait(false);
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
            if (type is "photonCad.project.open" or "photonCad.project.reopen" or "photonCad.project.refresh"
                or "photonCad.project.saveAs" or "photonCad.project.close") RevokeAllPreviews();
            // PhotonCadProjectDesktopDispatcher begins/reserves synchronously before its first await.
            // Starting it while holding the renderer gate means reset either sees and cancels the
            // reservation, or closes admission first and this old-generation request is dropped.
            return _projects.HandleAsync(type, message);
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
                    await EnsureIndustrialProviderReadyAsync(cancellationToken).ConfigureAwait(false);
                    _post(new
                    {
                        type = "photonCad.describe.result",
                        version = ProtocolVersion,
                        requestId,
                        value = CadWireProjection.Description(IndustrialRendererDescription()),
                    });
                }
                catch (Exception exception) when (IsIndustrialAvailabilityFailure(exception))
                {
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

    private async Task ExecuteIndustrialAsync(
        ExternalRuntimeRequest external,
        CadOperationMode mode,
        string capabilityId,
        JsonElement inputsElement,
        JsonElement targetsElement,
        CancellationToken cancellationToken)
    {
        if (_projectHost is null || _projectCodec is null
            || mode != CadOperationMode.Scratch
            || capabilityId is not (CadPinnedCapabilityCatalog.BoxCapabilityId or CadPinnedCapabilityCatalog.CylinderCapabilityId)
            || targetsElement.GetArrayLength() != 0)
        {
            PostOperationUnavailable(external, "industrial_persisted_operation_unavailable");
            return;
        }

        IReadOnlyDictionary<string, double> inputs;
        try { inputs = ParseIndustrialInputs(capabilityId, inputsElement); }
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
            inputs);
        IndustrialMutationBinding binding;
        try { binding = await CreateIndustrialBindingAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (IsIndustrialAvailabilityFailure(exception))
        {
            PostOperationUnavailable(external, "industrial_runtime_unavailable");
            return;
        }

        var mapper = new PhotonCadRuntimeCanonicalMapperV1(_projectCodec);
        var registration = _projectHost.CreateRuntimeProjectSynchronizer(binding.Provider, binding.Compensator, mapper);
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

    private async ValueTask<IndustrialMutationBinding> CreateIndustrialBindingAsync(
        IndustrialMutationRequest request,
        CancellationToken cancellationToken)
    {
        if (_industrialBindingFactory is not null)
            return await _industrialBindingFactory(request, cancellationToken).ConfigureAwait(false);
        var runtime = await EnsureIndustrialProviderReadyAsync(cancellationToken).ConfigureAwait(false);
        var bound = request.CapabilityId == CadPinnedCapabilityCatalog.BoxCapabilityId
            ? runtime.BindBox(request.RequestId, request.SessionId, request.ProjectId, request.BaseRevision, request.EntityId,
                request.Inputs["length"], request.Inputs["width"], request.Inputs["height"])
            : runtime.BindCylinder(request.RequestId, request.SessionId, request.ProjectId, request.BaseRevision, request.EntityId,
                request.Inputs["radius"], request.Inputs["height"]);
        return new IndustrialMutationBinding(bound.Request, bound.Provider, bound.Compensator);
    }

    private Task<PhotonCadIndustrialProviderRuntime> EnsureIndustrialProviderReadyAsync(CancellationToken cancellationToken)
    {
        lock (_brokerLock) _industrialRuntimeTask ??= CreateIndustrialRuntimeAsync();
        return _industrialRuntimeTask.WaitAsync(cancellationToken);
    }

    private async Task<PhotonCadIndustrialProviderRuntime> CreateIndustrialRuntimeAsync()
    {
        var assets = Path.Combine(_installRoot, InstalledIndustrialAssetRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var evidence = Path.Combine(assets, IndustrialEvidenceSelectionFileName);
        var docker = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Docker", "Docker", "resources", "bin", "docker.exe");
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

    private static CadRuntimeDescription IndustrialRendererDescription()
    {
        var generated = DateTimeOffset.UtcNow;
        var pinned = CadPinnedCapabilityCatalog.Create(generated);
        var capabilities = pinned.Capabilities
            .Where(capability => capability.Id is CadPinnedCapabilityCatalog.BoxCapabilityId or CadPinnedCapabilityCatalog.CylinderCapabilityId)
            .Select(capability => new CadCapability(
                capability.Id, capability.Backend, capability.Category, capability.Title, capability.Description,
                capability.Operation, capability.Parameters, capability.Source, previewSupported: true, capability.Experimental))
            .ToArray();
        var catalog = new CadCapabilityCatalog(
            pinned.CatalogRevision,
            generated,
            capabilities,
            new CadCatalogCoverage(6, capabilities.Length, 6 - capabilities.Length,
                ["Only persisted box and cylinder creation with a sealed complete-project GLB preview is mounted."]));
        var digest = IndustrialImageSha256[7..];
        var receipt = "70969065e209b454e4149235ad2662686629d7a23c44d584938c8a672bb2edb4";
        var bundles = new CadRuntimeBundleSetIdentity(
            new CadBundleIdentity(CadRuntimeRole.Geometry, "photon-cad-industrial-geometry", "0.1.0", "docker",
                "industrial-v1", "linux-amd64", digest, generated),
            new CadBundleIdentity(CadRuntimeRole.Assembly, "photon-cad-industrial-preview", "0.1.0", "docker",
                "industrial-v1", "linux-amd64", receipt, generated),
            new CadRevision(0));
        return new CadRuntimeDescription(CadRuntimeAvailability.Ready, "ready", "The verified industrial CAD runtime is ready.", bundles, catalog);
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
            contexts = _previewReceipts.Values.Select(value => value.Context).Distinct().ToArray();
            _previewReceipts.Clear();
            _previewResources.Clear();
        }
        foreach (var context in contexts) _previewCustody.RevokeProject(context);
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
        var docker = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Docker", "Docker", "resources", "bin", "docker.exe");
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
            snapshot = new
            {
                contractVersion = ProtocolVersion,
                sessionId = state.SessionId,
                projectId = state.ProjectId,
                revision = state.Revision,
                title = state.Title,
                units = state.Units == PhotonCadProjectUnit.Millimeter ? "millimeter" : "inch",
                mode = "canonical",
                entities = ExternalCommittedEntities(state),
                operations = state.Operations.Select(operation => new
                {
                    id = operation.Id,
                    capabilityId = operation.CapabilityId,
                    label = operation.Label,
                    createdAtUtc = Utc(operation.CreatedAtUtc),
                    state = operation.State.ToString().ToLowerInvariant(),
                }).ToArray(),
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
                dirty = false,
            },
        };
        if (preview is null) return projected;
        var extended = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in JsonSerializer.SerializeToElement(projected).EnumerateObject())
            extended[property.Name] = property.Value.Clone();
        extended["preview"] = new
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
        return extended;
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> ExternalCommittedEntities(
        PhotonCadProjectStateV1 state)
    {
        var projected = new List<IReadOnlyDictionary<string, object?>>(state.Entities.Count + state.Occurrences.Count);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceCapabilities = state.Entities.ToDictionary(
            entity => entity.Id,
            entity => entity.SourceCapabilityId,
            StringComparer.Ordinal);
        foreach (var entity in state.Entities)
        {
            if (!ids.Add(entity.Id)) throw new InvalidOperationException("The canonical CAD inventory contains duplicate identifiers.");
            projected.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = entity.Id,
                ["parentId"] = entity.ParentId,
                ["kind"] = entity.Kind.ToString().ToLowerInvariant(),
                ["name"] = entity.Name,
                ["visible"] = entity.Visible,
                ["suppressed"] = entity.Suppressed,
                ["sourceCapabilityId"] = entity.SourceCapabilityId,
            });
        }
        foreach (var occurrence in state.Occurrences)
        {
            if (!ids.Add(occurrence.OccurrenceId)
                || !sourceCapabilities.TryGetValue(occurrence.SourceEntityId, out var sourceCapabilityId))
                throw new InvalidOperationException("The canonical CAD occurrence inventory is not bound to one exact source entity.");
            projected.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = occurrence.OccurrenceId,
                ["parentId"] = occurrence.ParentOccurrenceId,
                ["kind"] = "occurrence",
                ["name"] = occurrence.PartNumber,
                ["visible"] = true,
                ["suppressed"] = false,
                ["sourceCapabilityId"] = sourceCapabilityId,
            });
        }
        return projected;
    }

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
            DesktopLog.Write($"Photon CAD bridge failed safely: {exception.GetType().Name}");
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
        if (string.IsNullOrEmpty(candidate) || candidate.Length > MaximumRequestCharacters || !char.IsAsciiLetterOrDigit(candidate[0])
            || candidate.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-' and not '.' and not ':')) return false;
        value = candidate;
        return true;
    }

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

    private sealed record ExternalRuntimeRequest(string RequestId, string SessionId, string ProjectId, long Revision);
}
