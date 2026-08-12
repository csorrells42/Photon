using HermesDeveloperServices;
using HermesDeveloperServices.LanguageTooling;
using HermesDeveloperServices.LanguageTooling.Gcc;
using HermesDeveloperServices.LanguageTooling.Java;
using HermesDeveloperServices.LanguageTooling.Operations;
using HermesDeveloperServices.LanguageTooling.Python;
using HermesDotNetDebugger;
using HermesRoslynLanguageServer;
using System.IO;
using System.Security.Cryptography;
using System.Security;
using System.Text.Json;

namespace HermesDesktop;

internal sealed class DeveloperServicesBridge : IAsyncDisposable
{
    private static readonly JsonSerializerOptions DebugJsonOptions = new(JsonSerializerDefaults.Web);
    public const int ProtocolVersion = 2;
    private const int MaximumRequestIdLength = 128;
    private const int MaximumTargetPathLength = 2_048;
    private const int MaximumRevision = 1_000_000_000;
    private const int MaximumDiagnosticTextLength = 2_048;
    private const int MaximumRoslynReceiptBytes = 16 * 1024;
    private const int MaximumLanguageDocuments = 32;
    private const int MaximumDebugTargets = 128;
    private const int MaximumDebugDirectories = 8_192;
    private const int MaximumDebugRuntimeConfigs = 4_096;

    private readonly string _workspaceRoot;
    private readonly Action<object> _postMessage;
    private readonly DotnetBuildRunner _buildRunner = new();
    private readonly ToolchainProviderRegistry _providers = new();
    private readonly LanguageToolingTrustedRegistry _languageTooling;
    private readonly LanguageToolingHostBridge _languageToolingHost;
    private readonly IDeveloperRoslynHost _roslynHost;
    private readonly IDeveloperDebugHost _debugHost;
    private readonly object _gate = new();
    private readonly Dictionary<string, CancellationTokenSource> _activeOperations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _languageToolingOperations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _languageOperations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _debugOperations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LanguageDocument> _languageDocuments = new(StringComparer.Ordinal);
    private string? _latestRequestId;
    private int _latestRevision;
    private bool _disposed;

    public DeveloperServicesBridge(string workspaceRoot, string applicationInstallRoot, Action<object> postMessage)
        : this(workspaceRoot, applicationInstallRoot, postMessage, null, null)
    {
    }

    internal DeveloperServicesBridge(
        string workspaceRoot,
        string applicationInstallRoot,
        Action<object> postMessage,
        IDeveloperRoslynHost? roslynHost,
        IDeveloperDebugHost? debugHost = null)
    {
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _postMessage = postMessage;
        var installRoot = Path.GetFullPath(applicationInstallRoot);
        var dotnet = new DotnetToolchainProvider();
        var roslyn = CreateRoslynProvider(installRoot);
        var debugAuthorization = new DeveloperDebugAuthorizationPolicy();
        var debugger = new HermesDotNetDebuggerProvider(installRoot, debugAuthorization);
        var gcc = new GccToolchainProvider(new GccToolchainProviderOptions
        {
            WorkbenchRoot = installRoot,
        });
        _providers.Register(dotnet);
        _providers.Register(roslyn);
        _providers.Register(debugger);
        _providers.Register(gcc);
        _roslynHost = roslynHost ?? new DeveloperRoslynHost(roslyn);
        _roslynHost.DiagnosticsPublished += HandleRoslynDiagnostics;
        _debugHost = debugHost ?? new DeveloperDotNetDebugHost(_workspaceRoot, debugger, debugAuthorization);
        _debugHost.EventReceived += HandleDebugEvent;
        var arduinoRoot = ResolveArduinoWorkbenchRoot(installRoot, AppContext.BaseDirectory);
        var arduinoOptions = new ArduinoProviderOptions(
            arduinoRoot,
            "toolchains/arduino",
            "hermes-toolchain-receipt.json",
            "arduino-config.json",
            "developer-services/arduino-state");
        var java = CreateJavaProvider(_workspaceRoot, installRoot);
        var python = CreatePythonProvider(_workspaceRoot);
        var pythonLanguage = new SerenaPythonLanguageToolingProvider(_workspaceRoot);
        var gccTooling = CreateGccProvider(_workspaceRoot, gcc);
        var dotnetTooling = DotnetLanguageToolingProvider.Create(dotnet, _workspaceRoot);
        var raspberryPi = new RaspberryPiDesktopTooling(installRoot, new WindowsCredentialVault());
        _languageTooling = LanguageToolingRegistryFactory.Create(
            _workspaceRoot,
            [
                dotnetTooling.EvidenceSource,
                KnownPinnedToolchainEvidenceSource.Create(roslyn, ownsProvider: false),
                KnownPinnedToolchainEvidenceSource.Create(debugger, ownsProvider: false),
                gccTooling.EvidenceSource,
                new ArduinoPinnedEvidenceSource(arduinoOptions),
                raspberryPi,
                java,
                python.EvidenceSource,
                pythonLanguage,
            ],
            [
                dotnetTooling.OperationHandler,
                gccTooling.OperationHandler,
                new ArduinoLanguageToolingOperationHandler(arduinoOptions, _workspaceRoot),
                raspberryPi,
                java,
                python.OperationHandler,
                pythonLanguage,
            ]);
        _languageToolingHost = new LanguageToolingHostBridge(_languageTooling);
    }

    public Task InspectLanguageToolingProviderAsync(int version, string? requestId, string? providerId) =>
        RunLanguageToolingAsync(new InspectLanguageToolingProviderRequest(
            version, requestId ?? string.Empty, "active-workspace", providerId ?? string.Empty));

    public Task InspectLanguageToolingProjectAsync(
        int version,
        string? requestId,
        string? providerId,
        string? projectPath) =>
        RunLanguageToolingAsync(new InspectLanguageToolingProjectRequest(
            version, requestId ?? string.Empty, "active-workspace", providerId ?? string.Empty, projectPath ?? string.Empty));

    public Task StartLanguageToolingSessionAsync(
        int version,
        string? requestId,
        string? providerId,
        string? documentPath) =>
        RunLanguageToolingAsync(new StartLanguageToolingSessionRequest(
            version, requestId ?? string.Empty, "active-workspace", providerId ?? string.Empty, documentPath ?? string.Empty));

    public Task StopLanguageToolingSessionAsync(
        int version,
        string? requestId,
        string? providerId,
        string? sessionId) =>
        RunLanguageToolingAsync(new StopLanguageToolingSessionRequest(
            version, requestId ?? string.Empty, "active-workspace", providerId ?? string.Empty, sessionId ?? string.Empty));

    public Task CompileLanguageToolingAsync(
        int version,
        string? requestId,
        string? providerId,
        string? targetPath,
        string? mode,
        string? boardFqbn) =>
        RunLanguageToolingAsync(new CompileLanguageToolingRequest(
            version,
            requestId ?? string.Empty,
            "active-workspace",
            providerId ?? string.Empty,
            targetPath ?? string.Empty,
            mode ?? string.Empty,
            string.IsNullOrWhiteSpace(boardFqbn) ? null : boardFqbn));

    public Task RunLanguageToolingTestsAsync(
        int version,
        string? requestId,
        string? providerId,
        string? targetPath,
        string? selection) =>
        RunLanguageToolingAsync(new RunLanguageToolingTestsRequest(
            version,
            requestId ?? string.Empty,
            "active-workspace",
            providerId ?? string.Empty,
            targetPath ?? string.Empty,
            string.IsNullOrWhiteSpace(selection) ? null : selection));

    public void CancelLanguageTooling(int version, string? requestId, string? targetRequestId)
    {
        var id = requestId?.Trim() ?? string.Empty;
        var target = targetRequestId?.Trim() ?? string.Empty;
        if (version != LanguageToolingProtocol.Version || !ValidLanguageToolingIdentifier(id) || !ValidLanguageToolingIdentifier(target))
        {
            PostLanguageToolingFailure(id, string.Empty, "cancel", "invalid-envelope", "The language-tooling cancellation request is invalid.");
            return;
        }
        CancellationTokenSource? cancellation;
        bool accepted;
        lock (_gate)
        {
            accepted = _languageToolingOperations.TryGetValue(target, out cancellation);
        }
        cancellation?.Cancel();
        _postMessage(new { type = "developerServices.languageTooling.cancel.result", version = LanguageToolingProtocol.Version, requestId = id, targetRequestId = target, accepted });
    }

    private async Task RunLanguageToolingAsync(LanguageToolingHostRequest request)
    {
        var id = request.RequestId?.Trim() ?? string.Empty;
        if (request.Version != LanguageToolingProtocol.Version || !ValidLanguageToolingIdentifier(id))
        {
            PostLanguageToolingFailure(id, request.ProviderId, request.Operation, "invalid-envelope", "The language-tooling request envelope is invalid.");
            return;
        }
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        lock (_gate)
        {
            if (_disposed)
            {
                PostLanguageToolingFailure(id, request.ProviderId, request.Operation, "host-stopping", "Developer services are shutting down.");
                return;
            }
            if (!_languageToolingOperations.TryAdd(id, cancellation))
            {
                PostLanguageToolingFailure(id, request.ProviderId, request.Operation, "duplicate-request", "That language-tooling request is already active.");
                return;
            }
        }
        try
        {
            var response = await _languageToolingHost.HandleAsync(request, cancellation.Token);
            _postMessage(new
            {
                type = "developerServices.languageTooling.result",
                version = response.Version,
                requestId = response.RequestId,
                providerId = response.ProviderId,
                operation = response.Operation,
                succeeded = response.Succeeded,
                code = response.Code,
                message = response.SafeMessage,
                evidence = response.Evidence is null ? null : new
                {
                    contract = response.Evidence.Contract,
                    source = response.Evidence.Source,
                    evidenceId = response.Evidence.EvidenceId,
                    providerId = response.Evidence.ProviderId,
                    checkedAt = response.Evidence.CheckedAt.UtcDateTime.ToString("O"),
                    capabilities = response.Evidence.Capabilities.Select(item => new { capabilityId = item.CapabilityId, availability = item.Availability.ToString().ToLowerInvariant(), code = item.Code, detail = item.SafeMessage, version = item.Version }),
                },
                result = response.Result is null ? null : new
                {
                    succeeded = response.Result.Succeeded,
                    code = response.Result.Code,
                    message = response.Result.SafeMessage,
                    diagnostics = response.Result.Diagnostics,
                    artifacts = response.Result.ArtifactPaths,
                    sessionId = response.Result.SessionId,
                },
            });
        }
        finally
        {
            lock (_gate) _languageToolingOperations.Remove(id);
        }
    }

    private void PostLanguageToolingFailure(string requestId, string providerId, string operation, string code, string message) =>
        _postMessage(new { type = "developerServices.languageTooling.result", version = LanguageToolingProtocol.Version, requestId, providerId, operation, succeeded = false, code, message });

    private static bool ValidLanguageToolingIdentifier(string value) => value.Length is > 0 and <= MaximumRequestIdLength
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or ':' or '.');

    public async Task DescribeAsync(int version, string? requestId)
    {
        if (!TryValidateEnvelope(version, requestId, out var id)) return;

        var providers = await Task.WhenAll(_providers.GetDescriptors().Select(DescribeProviderAsync));
        var languageTooling = await _languageTooling.DescribeAllAsync();
        var buildProvider = providers.FirstOrDefault(item =>
            item.Descriptor.ProviderId.Equals("dotnet", StringComparison.OrdinalIgnoreCase));
        if (buildProvider is null)
        {
            PostError(id, "provider_unavailable", "The .NET build provider is unavailable.", false);
            return;
        }

        _postMessage(new
        {
            type = "developerServices.describe.result",
            version = ProtocolVersion,
            requestId = id,
            workspaceRoot = _workspaceRoot,
            targets = BuildTargetDiscovery.Discover(_workspaceRoot),
            providers = providers.Select(item => new
            {
                descriptor = item.Descriptor,
                availability = item.Availability,
            }).Select(item => new
            {
                contractVersion = item.descriptor.ContractVersion,
                providerId = item.descriptor.ProviderId,
                displayName = item.descriptor.DisplayName,
                providerVersion = item.descriptor.ProviderVersion,
                languageIds = item.descriptor.LanguageIds,
                projectKinds = item.descriptor.ProjectKinds,
                build = new
                {
                    supported = item.descriptor.Build.Supported,
                    producesDiagnostics = item.descriptor.Build.ProducesDiagnostics,
                    supportsCancellation = item.descriptor.Build.SupportsCancellation,
                    targetKinds = item.descriptor.Build.TargetKinds,
                },
                lsp = new { supported = item.descriptor.Lsp.Supported },
                dap = new { supported = item.descriptor.Dap.Supported },
                availability = new
                {
                    state = item.availability.State.ToString().ToLowerInvariant(),
                    code = item.availability.Code,
                    message = item.availability.SafeMessage,
                },
            }),
            languageTooling = languageTooling.Select(provider => new
            {
                contract = provider.Contract,
                source = provider.Source,
                evidenceId = provider.EvidenceId,
                providerId = provider.ProviderId,
                checkedAt = provider.CheckedAt.UtcDateTime.ToString("O"),
                capabilities = provider.Capabilities.Select(capability => new
                {
                    capabilityId = capability.CapabilityId,
                    availability = capability.Availability.ToString().ToLowerInvariant(),
                    code = capability.Code,
                    detail = capability.SafeMessage,
                    version = capability.Version,
                }),
            }),
            availability = new
            {
                state = buildProvider.Availability.State.ToString().ToLowerInvariant(),
                code = buildProvider.Availability.Code,
                message = buildProvider.Availability.SafeMessage,
            },
        });
    }

    private async Task<ProviderDescription> DescribeProviderAsync(ToolchainProviderDescriptor descriptor)
    {
        var selection = _providers.Select(new ToolchainSelectionRequest(
            LanguageId: descriptor.LanguageIds[0],
            ProviderId: descriptor.ProviderId));
        if (selection.Provider is null)
        {
            return new ProviderDescription(descriptor, new ToolchainAvailability(
                ToolchainAvailabilityState.Unavailable,
                "provider-unavailable",
                "This toolchain provider is unavailable.",
                DateTimeOffset.UtcNow));
        }

        try
        {
            var discovery = await selection.Provider.DiscoverExecutablesAsync(
                new ToolchainDiscoveryContext(_workspaceRoot, ToolchainExecutionKind.LocalSidecarProcess),
                CancellationToken.None);
            return new ProviderDescription(descriptor, discovery.Availability);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new ProviderDescription(descriptor, new ToolchainAvailability(
                ToolchainAvailabilityState.Error,
                "provider-discovery-failed",
                "This toolchain provider could not be inspected.",
                DateTimeOffset.UtcNow));
        }
    }

    public Task BuildAsync(
        int version,
        string? requestId,
        int revision,
        string? targetPath,
        string? configuration) =>
        RunAsync(DeveloperOperation.Build, version, requestId, revision, targetPath, configuration);

    public Task AnalyzeAsync(
        int version,
        string? requestId,
        int revision,
        string? targetPath,
        string? configuration) =>
        RunAsync(DeveloperOperation.Analyze, version, requestId, revision, targetPath, configuration);

    private async Task RunAsync(
        DeveloperOperation operation,
        int version,
        string? requestId,
        int revision,
        string? targetPath,
        string? configuration)
    {
        if (!TryValidateEnvelope(version, requestId, out var id)) return;
        if (revision is < 0 or > MaximumRevision)
        {
            PostError(id, "invalid_revision", "The developer-services revision is invalid.", false);
            return;
        }
        if (!TryValidateWorkspaceRelativeTarget(targetPath, out var validatedTarget))
        {
            PostError(id, "invalid_target", "Select a .sln, .slnx, or .csproj file inside this workspace.", false);
            return;
        }
        if (!TryParseConfiguration(configuration, out var buildConfiguration))
        {
            PostError(id, "invalid_configuration", "Select the Debug or Release configuration.", false);
            return;
        }
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            if (_disposed)
            {
                PostError(id, "host_stopping", "Developer services are shutting down.", true);
                return;
            }
            if (_activeOperations.ContainsKey(id))
            {
                PostError(id, "duplicate_request", "The developer-services request identifier is already active.", false);
                return;
            }
            foreach (var active in _activeOperations.Values) active.Cancel();
            cancellation = new CancellationTokenSource();
            _activeOperations.Add(id, cancellation);
            _latestRequestId = id;
            _latestRevision = revision;
        }

        var operationName = operation == DeveloperOperation.Build ? "build" : "analyze";
        _postMessage(new
        {
            type = $"developerServices.{operationName}.started",
            version = ProtocolVersion,
            requestId = id,
            revision,
            operation = operationName,
        });
        try
        {
            var result = await _buildRunner.BuildAsync(
                new BuildRequest(_workspaceRoot, validatedTarget, buildConfiguration),
                cancellation.Token);
            bool stale;
            lock (_gate)
            {
                stale = !_latestRequestId?.Equals(id, StringComparison.Ordinal) == true
                    || _latestRevision != revision;
            }
            _postMessage(new
            {
                type = $"developerServices.{operationName}.result",
                version = ProtocolVersion,
                requestId = id,
                revision,
                operation = operationName,
                workspaceRoot = _workspaceRoot,
                succeeded = result.Succeeded,
                exitCode = result.ExitCode,
                wasCancelled = result.WasCancelled,
                stale,
                failureCode = result.FailureCode,
                failureMessage = SanitizeText(result.FailureMessage, 1_024),
                startedAt = result.StartedAt,
                completedAt = result.CompletedAt,
                diagnostics = result.Diagnostics.Select(diagnostic => new
                {
                    filePath = SanitizeDiagnosticPath(diagnostic.FilePath),
                    severity = diagnostic.Severity.ToString().ToLowerInvariant(),
                    code = SanitizeText(diagnostic.Code, 128),
                    message = SanitizeText(diagnostic.Message, MaximumDiagnosticTextLength),
                    source = SanitizeText(diagnostic.Source, 64),
                    range = new
                    {
                        start = new
                        {
                            line = ClampPosition(diagnostic.Range.Start.Line),
                            column = ClampPosition(diagnostic.Range.Start.Column),
                        },
                        end = new
                        {
                            line = ClampPosition(diagnostic.Range.End.Line),
                            column = ClampPosition(diagnostic.Range.End.Column),
                        },
                    },
                }),
                output = new
                {
                    standardOutput = result.Output.StandardOutput,
                    standardError = result.Output.StandardError,
                    truncated = result.Output.Truncated,
                    droppedCharacters = result.Output.DroppedCharacters,
                },
            });
        }
        finally
        {
            lock (_gate) _activeOperations.Remove(id);
            cancellation.Dispose();
        }
    }

    public void Cancel(int version, string? requestId)
    {
        if (!TryValidateEnvelope(version, requestId, out var id)) return;
        CancellationTokenSource? cancellation;
        bool accepted;
        lock (_gate)
        {
            accepted = _activeOperations.TryGetValue(id, out cancellation);
        }
        cancellation?.Cancel();
        _postMessage(new
        {
            type = "developerServices.cancel.result",
            version = ProtocolVersion,
            requestId = id,
            accepted,
        });
    }

    public async Task OpenLanguageDocumentAsync(
        int version,
        string? requestId,
        int revision,
        string? documentPath,
        string? text)
    {
        if (!TryValidateEnvelope(version, requestId, out var id)) return;
        if (!TryValidateLanguageRevision(revision, id)
            || !TryValidateLanguageDocument(documentPath, out var relativePath, out var uri, id)
            || !TryValidateDocumentText(text, id, out var documentText)) return;

        var sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var document = new LanguageDocument(sessionId, relativePath, uri, revision);
        lock (_gate)
        {
            if (_disposed)
            {
                PostError(id, "host_stopping", "Developer services are shutting down.", true);
                return;
            }
            if (_languageDocuments.Count >= MaximumLanguageDocuments
                || _languageDocuments.Values.Any(candidate => candidate.Uri.Equals(uri, StringComparison.OrdinalIgnoreCase)))
            {
                PostError(id, "language_document_busy", "That C# document already has an active language session.", true);
                return;
            }
            _languageDocuments.Add(sessionId, document);
        }

        try
        {
            await _roslynHost.OpenDocumentAsync(_workspaceRoot, uri, revision, documentText, CancellationToken.None);
            LspPublishDiagnosticsParams? pending;
            lock (_gate)
            {
                document.Ready = true;
                pending = document.PendingDiagnostics;
                document.PendingDiagnostics = null;
            }
            _postMessage(new
            {
                type = "developerServices.language.open.result",
                version = ProtocolVersion,
                requestId = id,
                sessionId,
                documentPath = relativePath,
                revision,
            });
            if (pending is not null) PostLanguageDiagnostics(document, pending);
        }
        catch (Exception exception) when (IsLanguageFailure(exception))
        {
            lock (_gate) _languageDocuments.Remove(sessionId);
            PostError(id, "roslyn_unavailable", "The verified Roslyn language service could not open this document.", true);
        }
    }

    public async Task ChangeLanguageDocumentAsync(
        int version,
        string? requestId,
        string? sessionId,
        int revision,
        string? documentPath,
        string? text)
    {
        if (!TryValidateEnvelope(version, requestId, out var id)) return;
        if (!TryValidateLanguageRevision(revision, id)
            || !TryValidateSessionId(sessionId, id, out var validatedSessionId)
            || !TryValidateLanguageDocument(documentPath, out var relativePath, out var uri, id)
            || !TryValidateDocumentText(text, id, out var documentText)) return;

        LanguageDocument document;
        int previousRevision;
        lock (_gate)
        {
            if (!_languageDocuments.TryGetValue(validatedSessionId, out document!)
                || !document.Ready
                || !document.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase)
                || !document.Uri.Equals(uri, StringComparison.OrdinalIgnoreCase))
            {
                PostError(id, "language_session_invalid", "The C# language session is unavailable or does not own this document.", false);
                return;
            }
            if (revision <= document.Revision)
            {
                PostError(id, "stale_revision", "The C# document revision is stale.", false);
                return;
            }
            previousRevision = document.Revision;
            document.Revision = revision;
        }

        try
        {
            await _roslynHost.ChangeDocumentAsync(uri, revision, documentText, CancellationToken.None);
            _postMessage(new
            {
                type = "developerServices.language.change.result",
                version = ProtocolVersion,
                requestId = id,
                sessionId = validatedSessionId,
                documentPath = relativePath,
                revision,
            });
        }
        catch (Exception exception) when (IsLanguageFailure(exception))
        {
            lock (_gate)
            {
                if (_languageDocuments.TryGetValue(validatedSessionId, out var current) && current.Revision == revision)
                    current.Revision = previousRevision;
            }
            PostError(id, "roslyn_change_failed", "The verified Roslyn language service could not accept this document revision.", true);
        }
    }

    public async Task CloseLanguageDocumentAsync(
        int version,
        string? requestId,
        string? sessionId,
        string? documentPath)
    {
        if (!TryValidateEnvelope(version, requestId, out var id)) return;
        if (!TryValidateSessionId(sessionId, id, out var validatedSessionId)
            || !TryValidateLanguageDocument(documentPath, out var relativePath, out var uri, id)) return;
        LanguageDocument document;
        lock (_gate)
        {
            if (!_languageDocuments.TryGetValue(validatedSessionId, out document!)
                || !document.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase)
                || !document.Uri.Equals(uri, StringComparison.OrdinalIgnoreCase))
            {
                PostError(id, "language_session_invalid", "The C# language session is unavailable or does not own this document.", false);
                return;
            }
            _languageDocuments.Remove(validatedSessionId);
        }
        try
        {
            if (document.Ready) await _roslynHost.CloseDocumentAsync(uri, CancellationToken.None);
            _postMessage(new { type = "developerServices.language.close.result", version = ProtocolVersion, requestId = id, sessionId = validatedSessionId });
        }
        catch (Exception exception) when (IsLanguageFailure(exception))
        {
            PostError(id, "roslyn_close_failed", "The C# language document was retired locally, but Roslyn did not acknowledge closure.", false);
        }
    }

    public async Task RunLanguageOperationAsync(
        int version,
        string? requestId,
        string? sessionId,
        int revision,
        string? documentPath,
        string? operation,
        int line,
        int character,
        int endLine,
        int endCharacter,
        string? newName,
        bool includeDeclaration)
    {
        if (!TryValidateEnvelope(version, requestId, out var id)) return;
        if (!TryValidateSessionId(sessionId, id, out var validatedSessionId)
            || !TryValidateLanguageRevision(revision, id)
            || !TryValidateLanguageDocument(documentPath, out var relativePath, out var uri, id)
            || !TryValidateLanguageOperation(operation, line, character, endLine, endCharacter, newName, id,
                out var validatedOperation, out var position, out var range, out var validatedName)) return;

        LanguageDocument document;
        lock (_gate)
        {
            if (!_languageDocuments.TryGetValue(validatedSessionId, out document!)
                || !document.Ready
                || document.Revision != revision
                || !document.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase)
                || !document.Uri.Equals(uri, StringComparison.OrdinalIgnoreCase))
            {
                PostError(id, "language_session_stale", "The C# language request does not match the active document revision.", true);
                return;
            }
            if (_disposed)
            {
                PostError(id, "host_stopping", "Developer services are shutting down.", true);
                return;
            }
            if (_languageOperations.ContainsKey(id))
            {
                PostError(id, "duplicate_request", "That C# language request is already active.", false);
                return;
            }
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        lock (_gate) _languageOperations.Add(id, cancellation);
        try
        {
            RoslynLanguageResult? result = validatedOperation switch
            {
                "completion" => await _roslynHost.CompletionAsync(uri, revision, position, cancellation.Token),
                "hover" => await _roslynHost.HoverAsync(uri, revision, position, cancellation.Token),
                "definition" => await _roslynHost.DefinitionAsync(uri, revision, position, cancellation.Token),
                "references" => await _roslynHost.ReferencesAsync(uri, revision, position, includeDeclaration, cancellation.Token),
                "rename" => await _roslynHost.RenameAsync(uri, revision, position, validatedName!, cancellation.Token),
                "code-actions" => await _roslynHost.CodeActionsAsync(uri, revision, range, cancellation.Token),
                _ => throw new InvalidOperationException("The C# language operation is unavailable."),
            };
            _postMessage(new
            {
                type = "developerServices.language.result",
                version = ProtocolVersion,
                requestId = id,
                sessionId = validatedSessionId,
                documentPath = relativePath,
                revision,
                operation = validatedOperation,
                result = ProjectLanguageResult(result?.Value),
            });
        }
        catch (OperationCanceledException)
        {
            PostError(id, "language_cancelled", "The C# language request was cancelled.", true);
        }
        catch (Exception exception) when (IsLanguageFailure(exception))
        {
            PostError(id, "roslyn_request_failed", "The verified Roslyn language request could not be completed.", true);
        }
        finally
        {
            lock (_gate) _languageOperations.Remove(id);
        }
    }

    public void CancelLanguageOperation(int version, string? requestId, string? targetRequestId)
    {
        if (!TryValidateEnvelope(version, requestId, out var id)) return;
        var target = targetRequestId?.Trim() ?? string.Empty;
        if (!ValidLanguageToolingIdentifier(target))
        {
            PostError(id, "invalid_cancel", "The C# language cancellation request is invalid.", false);
            return;
        }
        CancellationTokenSource? cancellation;
        bool accepted;
        lock (_gate)
        {
            accepted = _languageOperations.TryGetValue(target, out cancellation);
        }
        cancellation?.Cancel();
        _postMessage(new
        {
            type = "developerServices.language.cancel.result",
            version = ProtocolVersion,
            requestId = id,
            targetRequestId = target,
            accepted,
        });
    }

    public Task DescribeDebugTargetsAsync(int version, string? requestId, string? configuration) =>
        RunDebugOperationAsync(version, requestId, "targets", async cancellationToken =>
        {
            if (!TryParseConfiguration(configuration, out var parsed))
                throw new ArgumentException("Select the Debug or Release configuration.");
            return await Task.Run(() => DiscoverDebugTargets(parsed, cancellationToken), cancellationToken).ConfigureAwait(false);
        });

    public Task LaunchDebugAsync(
        int version,
        string? requestId,
        string? programPath,
        string? workingDirectory,
        IReadOnlyList<string> arguments,
        bool stopAtEntry) =>
        RunDebugOperationAsync(version, requestId, "launch", async cancellationToken =>
        {
            var program = ResolveDebugFile(programPath, ".dll", ".exe");
            var directory = ResolveDebugDirectory(workingDirectory, Path.GetDirectoryName(program)!);
            ValidateDebugArguments(arguments);
            await _debugHost.LaunchAsync(new DotNetDebugLaunchRequest(program, directory, arguments, stopAtEntry), cancellationToken).ConfigureAwait(false);
            return DebugSessionProjection();
        });

    public Task AttachDebugAsync(int version, string? requestId, int processId) =>
        RunDebugOperationAsync(version, requestId, "attach", async cancellationToken =>
        {
            await _debugHost.AttachAsync(new DotNetDebugAttachRequest(processId), cancellationToken).ConfigureAwait(false);
            return DebugSessionProjection();
        });

    public Task SetDebugBreakpointsAsync(
        int version,
        string? requestId,
        string? sourcePath,
        IReadOnlyList<DapSourceBreakpoint> breakpoints) =>
        RunDebugOperationAsync(version, requestId, "set-breakpoints", async cancellationToken =>
        {
            var source = ResolveDebugFile(sourcePath, ".cs");
            var result = await _debugHost.SetBreakpointsAsync(source, breakpoints, cancellationToken).ConfigureAwait(false);
            return result.Take(2_048).Select(breakpoint => new
            {
                id = breakpoint.Id,
                verified = breakpoint.Verified,
                message = SanitizeText(breakpoint.Message, 2_048),
                path = SanitizeDiagnosticPath(breakpoint.Source?.Path),
                line = breakpoint.Line,
                column = breakpoint.Column,
            }).ToArray();
        });

    public Task ConfigurationDoneDebugAsync(int version, string? requestId) =>
        RunDebugOperationAsync(version, requestId, "configuration-done", async cancellationToken =>
        {
            await _debugHost.ConfigurationDoneAsync(cancellationToken).ConfigureAwait(false);
            return DebugSessionProjection();
        });

    public Task GetDebugThreadsAsync(int version, string? requestId) =>
        RunDebugOperationAsync(version, requestId, "threads", async cancellationToken =>
        {
            var result = await _debugHost.GetThreadsAsync(cancellationToken).ConfigureAwait(false);
            return result.Take(1_000).Select(thread => new { id = thread.Id, name = SanitizeText(thread.Name, 512) }).ToArray();
        });

    public Task GetDebugStackTraceAsync(int version, string? requestId, int threadId, int startFrame, int levels) =>
        RunDebugOperationAsync(version, requestId, "stack-trace", async cancellationToken =>
        {
            var result = await _debugHost.GetStackTraceAsync(
                threadId,
                startFrame < 0 ? null : startFrame,
                levels < 0 ? null : levels,
                cancellationToken).ConfigureAwait(false);
            return result.Take(1_000).Select(frame => new
            {
                id = frame.Id,
                name = SanitizeText(frame.Name, 1_024),
                path = SanitizeDiagnosticPath(frame.Source?.Path),
                line = ClampPosition(frame.Line),
                column = ClampPosition(frame.Column),
                endLine = frame.EndLine,
                endColumn = frame.EndColumn,
            }).ToArray();
        });

    public Task GetDebugScopesAsync(int version, string? requestId, int frameId) =>
        RunDebugOperationAsync(version, requestId, "scopes", async cancellationToken =>
        {
            var result = await _debugHost.GetScopesAsync(frameId, cancellationToken).ConfigureAwait(false);
            return result.Take(256).Select(scope => new
            {
                name = SanitizeText(scope.Name, 512),
                variablesReference = scope.VariablesReference,
                expensive = scope.Expensive,
                presentationHint = SanitizeText(scope.PresentationHint, 128),
            }).ToArray();
        });

    public Task GetDebugVariablesAsync(int version, string? requestId, int variablesReference, int start, int count) =>
        RunDebugOperationAsync(version, requestId, "variables", async cancellationToken =>
        {
            var result = await _debugHost.GetVariablesAsync(
                variablesReference,
                start < 0 ? null : start,
                count < 0 ? null : count,
                cancellationToken).ConfigureAwait(false);
            return result.Take(1_000).Select(variable => new
            {
                name = SanitizeText(variable.Name, 512),
                value = SanitizeText(variable.Value, 16 * 1024),
                type = SanitizeText(variable.Type, 512),
                variablesReference = variable.VariablesReference,
                evaluateName = SanitizeText(variable.EvaluateName, 2_048),
            }).ToArray();
        });

    public Task EvaluateDebugAsync(int version, string? requestId, string? expression, int frameId, string? context) =>
        RunDebugOperationAsync(version, requestId, "evaluate", async cancellationToken =>
        {
            var result = await _debugHost.EvaluateAsync(
                expression ?? string.Empty,
                frameId < 0 ? null : frameId,
                context ?? string.Empty,
                cancellationToken).ConfigureAwait(false);
            return new
            {
                result = SanitizeText(result.Result, 64 * 1024),
                type = SanitizeText(result.Type, 512),
                variablesReference = result.VariablesReference,
            };
        });

    public Task ContinueDebugAsync(int version, string? requestId, int threadId) =>
        RunDebugControlAsync(version, requestId, "continue", token => _debugHost.ContinueAsync(threadId, token));

    public Task StepOverDebugAsync(int version, string? requestId, int threadId) =>
        RunDebugControlAsync(version, requestId, "step-over", token => _debugHost.StepOverAsync(threadId, token));

    public Task StepIntoDebugAsync(int version, string? requestId, int threadId) =>
        RunDebugControlAsync(version, requestId, "step-into", token => _debugHost.StepIntoAsync(threadId, token));

    public Task StepOutDebugAsync(int version, string? requestId, int threadId) =>
        RunDebugControlAsync(version, requestId, "step-out", token => _debugHost.StepOutAsync(threadId, token));

    public Task DisconnectDebugAsync(int version, string? requestId) =>
        RunDebugControlAsync(version, requestId, "disconnect", token => _debugHost.DisconnectAsync(token));

    public void CancelDebugOperation(int version, string? requestId, string? targetRequestId)
    {
        if (!TryValidateEnvelope(version, requestId, out var id)) return;
        var target = targetRequestId?.Trim() ?? string.Empty;
        if (!ValidLanguageToolingIdentifier(target))
        {
            PostError(id, "invalid_cancel", "The .NET debugger cancellation request is invalid.", false);
            return;
        }
        CancellationTokenSource? cancellation;
        bool accepted;
        lock (_gate)
        {
            accepted = _debugOperations.TryGetValue(target, out cancellation);
        }
        cancellation?.Cancel();
        _postMessage(new { type = "developerServices.debug.cancel.result", version = ProtocolVersion, requestId = id, targetRequestId = target, accepted });
    }

    private Task RunDebugControlAsync(int version, string? requestId, string operation, Func<CancellationToken, Task> action) =>
        RunDebugOperationAsync(version, requestId, operation, async cancellationToken =>
        {
            await action(cancellationToken).ConfigureAwait(false);
            return DebugSessionProjection();
        });

    private async Task RunDebugOperationAsync(
        int version,
        string? requestId,
        string operation,
        Func<CancellationToken, Task<object?>> action)
    {
        if (!TryValidateEnvelope(version, requestId, out var id)) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        lock (_gate)
        {
            if (_disposed)
            {
                PostError(id, "host_stopping", "Developer services are shutting down.", true);
                return;
            }
            if (_debugOperations.ContainsKey(id))
            {
                PostError(id, "duplicate_request", "That .NET debugger request is already active.", false);
                return;
            }
            _debugOperations.Add(id, cancellation);
        }
        try
        {
            var result = await action(cancellation.Token).ConfigureAwait(false);
            _postMessage(new
            {
                type = "developerServices.debug.result",
                version = ProtocolVersion,
                requestId = id,
                operation,
                state = DebugStateName(_debugHost.State),
                result,
            });
        }
        catch (OperationCanceledException)
        {
            PostError(id, "debug_cancelled", "The .NET debugger request was cancelled.", true);
        }
        catch (Exception exception) when (IsDebugFailure(exception))
        {
            PostError(id, "debug_failed", "The .NET debugger could not complete that operation.", true);
        }
        finally
        {
            lock (_gate) _debugOperations.Remove(id);
        }
    }

    private IReadOnlyList<string> DiscoverDebugTargets(BuildConfiguration configuration, CancellationToken cancellationToken) =>
        DiscoverDebugTargetsCore(_workspaceRoot, configuration, cancellationToken);

    internal static IReadOnlyList<string> DiscoverDebugTargetsCore(
        string workspaceRoot,
        BuildConfiguration configuration,
        CancellationToken cancellationToken,
        Func<string, string[]>? enumerateFiles = null,
        Func<string, string[]>? enumerateDirectories = null,
        Func<string, FileAttributes>? getAttributes = null)
    {
        var root = Path.GetFullPath(workspaceRoot);
        var marker = $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}{configuration}{Path.DirectorySeparatorChar}";
        var targets = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        var visitedDirectories = 0;
        var visitedRuntimeConfigs = 0;
        enumerateFiles ??= directory => Directory.EnumerateFiles(directory, "*.runtimeconfig.json", SearchOption.TopDirectoryOnly)
            .Take(MaximumDebugRuntimeConfigs)
            .ToArray();
        enumerateDirectories ??= directory => Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly)
            .Take(MaximumDebugDirectories)
            .ToArray();
        getAttributes ??= File.GetAttributes;

        while (pending.Count > 0
               && visitedDirectories < MaximumDebugDirectories
               && visitedRuntimeConfigs < MaximumDebugRuntimeConfigs
               && targets.Count < MaximumDebugTargets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            visitedDirectories++;
            if (!directory.Equals(root, StringComparison.OrdinalIgnoreCase)
                && IsSkippedDebugDiscoveryDirectory(Path.GetFileName(directory))) continue;

            try
            {
                if ((getAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            }
            catch (Exception exception) when (IsDebugDiscoveryFileSystemFailure(exception))
            {
                continue;
            }

            string[] runtimeConfigs;
            try
            {
                runtimeConfigs = enumerateFiles(directory);
            }
            catch (Exception exception) when (IsDebugDiscoveryFileSystemFailure(exception))
            {
                runtimeConfigs = [];
            }
            foreach (var runtimeConfig in runtimeConfigs.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++visitedRuntimeConfigs > MaximumDebugRuntimeConfigs) break;
                if (!runtimeConfig.Contains(marker, StringComparison.OrdinalIgnoreCase)) continue;
                var stem = runtimeConfig[..^".runtimeconfig.json".Length];
                var candidate = File.Exists(stem + ".exe") ? stem + ".exe" : stem + ".dll";
                if (!File.Exists(candidate) || TraversesReparsePoint(root, candidate)) continue;
                var relative = Path.GetRelativePath(root, candidate).Replace(Path.DirectorySeparatorChar, '/');
                if (TryValidateWorkspaceRelativeTarget(relative, out _)) targets.Add(relative);
                if (targets.Count >= MaximumDebugTargets) break;
            }

            string[] children;
            try
            {
                children = enumerateDirectories(directory);
            }
            catch (Exception exception) when (IsDebugDiscoveryFileSystemFailure(exception))
            {
                children = [];
            }
            foreach (var child in children.OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (pending.Count + visitedDirectories >= MaximumDebugDirectories) break;
                pending.Push(child);
            }
        }

        return targets.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsSkippedDebugDiscoveryDirectory(string name) => name.ToLowerInvariant() is
        ".git" or ".pnpm-store" or ".serena" or ".vscode" or "data" or "logs" or "node_modules" or "runtime-assets";

    private static bool IsDebugDiscoveryFileSystemFailure(Exception exception) => exception is
        IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException;

    private static bool TraversesReparsePoint(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath);
        var current = root;
        foreach (var component in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, component);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            }
            catch (Exception exception) when (IsDebugDiscoveryFileSystemFailure(exception))
            {
                return true;
            }
        }
        return false;
    }

    private string ResolveDebugFile(string? path, params string[] allowedExtensions)
    {
        if (!TryValidateWorkspaceRelativeTarget(path, out var relative)) throw new ArgumentException("Select a workspace-relative debugger file.");
        var fullPath = Path.GetFullPath(Path.Combine(_workspaceRoot, relative));
        if (!File.Exists(fullPath) || !allowedExtensions.Contains(Path.GetExtension(fullPath), StringComparer.OrdinalIgnoreCase)
            || TraversesDebugReparsePoint(fullPath)) throw new ArgumentException("The debugger file is unavailable.");
        return fullPath;
    }

    private string ResolveDebugDirectory(string? path, string fallback)
    {
        if (string.IsNullOrWhiteSpace(path)) return fallback;
        if (!TryValidateWorkspaceRelativeTarget(path, out var relative)) throw new ArgumentException("Select a workspace-relative debugger directory.");
        var fullPath = Path.GetFullPath(Path.Combine(_workspaceRoot, relative));
        if (!Directory.Exists(fullPath) || TraversesDebugReparsePoint(fullPath)) throw new ArgumentException("The debugger directory is unavailable.");
        return fullPath;
    }

    private bool TraversesDebugReparsePoint(string fullPath)
    {
        var relative = Path.GetRelativePath(_workspaceRoot, fullPath);
        var current = _workspaceRoot;
        foreach (var component in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, component);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        }
        return false;
    }

    private static void ValidateDebugArguments(IReadOnlyList<string> arguments)
    {
        if (arguments.Count > 128 || arguments.Any(value => value is null || value.Length > 4_096 || value.Contains('\0'))
            || arguments.Sum(value => value.Length) > 32 * 1024) throw new ArgumentException("The debugger arguments exceed the supported bounds.");
    }

    private object DebugSessionProjection()
    {
        var capabilities = _debugHost.Capabilities;
        return new
        {
            state = DebugStateName(_debugHost.State),
            capabilities = capabilities is null ? null : new
            {
                supportsConfigurationDoneRequest = capabilities.SupportsConfigurationDoneRequest,
                supportsConditionalBreakpoints = capabilities.SupportsConditionalBreakpoints,
                supportsHitConditionalBreakpoints = capabilities.SupportsHitConditionalBreakpoints,
                supportsEvaluateForHovers = capabilities.SupportsEvaluateForHovers,
                supportsCancelRequest = capabilities.SupportsCancelRequest,
            },
        };
    }

    private static string DebugStateName(DapSessionState? state) => state?.ToString().ToLowerInvariant() ?? "inactive";

    private static bool IsDebugFailure(Exception exception) => exception is IOException or UnauthorizedAccessException
        or SecurityException or ArgumentException or InvalidOperationException or DapProtocolException
        or DapSessionException or DotNetDebuggerAuthorizationException or DotNetDebuggerUnavailableException
        or DotNetDebuggerOperationException or ObjectDisposedException;

    private void HandleDebugEvent(DapEvent value)
    {
        try
        {
            object? body = value.Event switch
            {
                "stopped" => ProjectStoppedEvent(value.Body),
                "continued" => ProjectContinuedEvent(value.Body),
                "output" => ProjectOutputEvent(value.Body),
                "initialized" or "terminated" or "exited" => null,
                _ => null,
            };
            if (value.Event is not ("stopped" or "continued" or "output" or "initialized" or "terminated" or "exited")) return;
            _postMessage(new
            {
                type = "developerServices.debug.event",
                version = ProtocolVersion,
                @event = value.Event,
                state = DebugStateName(_debugHost.State),
                body,
            });
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            _postMessage(new
            {
                type = "developerServices.debug.event",
                version = ProtocolVersion,
                @event = "faulted",
                state = "faulted",
                body = new { message = "The .NET debugger emitted a malformed event." },
            });
        }
    }

    private static object? ProjectStoppedEvent(JsonElement? body)
    {
        var stopped = body?.Deserialize<DapStoppedEvent>(DebugJsonOptions);
        return stopped is null ? null : new
        {
            reason = SanitizeText(stopped.Reason, 128),
            threadId = stopped.ThreadId,
            description = SanitizeText(stopped.Description, 1_024),
            allThreadsStopped = stopped.AllThreadsStopped,
        };
    }

    private static object? ProjectContinuedEvent(JsonElement? body)
    {
        var continued = body?.Deserialize<DapContinuedEvent>(DebugJsonOptions);
        return continued is null ? null : new
        {
            threadId = continued.ThreadId,
            allThreadsContinued = continued.AllThreadsContinued,
        };
    }

    private static object? ProjectOutputEvent(JsonElement? body)
    {
        if (body is not { ValueKind: JsonValueKind.Object } value) return null;
        var category = value.TryGetProperty("category", out var categoryValue) ? categoryValue.GetString() : null;
        var output = value.TryGetProperty("output", out var outputValue) ? outputValue.GetString() : null;
        return new { category = SanitizeText(category, 64), output = SanitizeText(output, 64 * 1024) };
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource[] active;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            active = _activeOperations.Values.Concat(_languageToolingOperations.Values).Concat(_languageOperations.Values).Concat(_debugOperations.Values).ToArray();
        }
        foreach (var cancellation in active) cancellation.Cancel();
        _roslynHost.DiagnosticsPublished -= HandleRoslynDiagnostics;
        _debugHost.EventReceived -= HandleDebugEvent;
        await _roslynHost.DisposeAsync();
        await _debugHost.DisposeAsync();
        await _languageTooling.DisposeAsync();
        await _providers.DisposeAsync();
    }

    private bool TryValidateEnvelope(int version, string? requestId, out string id)
    {
        id = requestId?.Trim() ?? string.Empty;
        if (version != ProtocolVersion)
        {
            PostError(id, "unsupported_version", $"Developer services protocol {version} is not supported.", false);
            return false;
        }
        if (id.Length is 0 or > MaximumRequestIdLength || id.Any(character => !(char.IsLetterOrDigit(character) || character is '-' or '_' or ':')))
        {
            PostError(string.Empty, "invalid_request_id", "The developer-services request identifier is invalid.", false);
            return false;
        }
        return true;
    }

    private static bool TryValidateWorkspaceRelativeTarget(string? targetPath, out string validated)
    {
        validated = targetPath?.Trim() ?? string.Empty;
        if (validated.Length is 0 or > MaximumTargetPathLength
            || validated.Contains('\0')
            || Path.IsPathRooted(validated)
            || Path.IsPathFullyQualified(validated)) return false;
        return !validated.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries)
            .Any(component => component is "." or "..");
    }

    private static bool TryParseConfiguration(string? configuration, out BuildConfiguration parsed)
    {
        if (configuration?.Equals("debug", StringComparison.OrdinalIgnoreCase) == true)
        {
            parsed = BuildConfiguration.Debug;
            return true;
        }
        if (configuration?.Equals("release", StringComparison.OrdinalIgnoreCase) == true)
        {
            parsed = BuildConfiguration.Release;
            return true;
        }
        parsed = default;
        return false;
    }

    private string SanitizeDiagnosticPath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || filePath.Length > MaximumTargetPathLength) return string.Empty;
        try
        {
            var fullPath = Path.GetFullPath(
                Path.IsPathFullyQualified(filePath) ? filePath : Path.Combine(_workspaceRoot, filePath));
            var relative = Path.GetRelativePath(_workspaceRoot, fullPath);
            if (Path.IsPathFullyQualified(relative)
                || relative.Equals("..", StringComparison.Ordinal)
                || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)) return string.Empty;
            return relative.Replace(Path.DirectorySeparatorChar, '/');
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Empty;
        }
    }

    private static string? SanitizeText(string? value, int maximumLength)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var sanitized = new string(value.Where(character => character is not '\0' and not '\r').ToArray());
        return sanitized.Length <= maximumLength ? sanitized : sanitized[..maximumLength];
    }

    private static int ClampPosition(int value) => Math.Clamp(value, 1, 1_000_000);

    private void HandleRoslynDiagnostics(LspPublishDiagnosticsParams published)
    {
        LanguageDocument? document;
        lock (_gate)
        {
            document = _languageDocuments.Values.FirstOrDefault(candidate =>
                candidate.Uri.Equals(published.Uri, StringComparison.OrdinalIgnoreCase));
            if (document is null || published.Version != document.Revision) return;
            if (!document.Ready)
            {
                document.PendingDiagnostics = published;
                return;
            }
        }
        PostLanguageDiagnostics(document, published);
    }

    private void PostLanguageDiagnostics(LanguageDocument document, LspPublishDiagnosticsParams published) =>
        _postMessage(new
        {
            type = "developerServices.language.diagnostics",
            version = ProtocolVersion,
            sessionId = document.SessionId,
            documentPath = document.RelativePath,
            revision = document.Revision,
            diagnostics = published.Diagnostics.Take(RoslynLanguageSession.MaximumDiagnostics).Select(diagnostic => new
            {
                filePath = document.RelativePath,
                severity = diagnostic.Severity == 1 ? "error" : diagnostic.Severity == 2 ? "warning" : "info",
                code = SanitizeText(DiagnosticCode(diagnostic.Code), 128),
                message = SanitizeText(diagnostic.Message, MaximumDiagnosticTextLength),
                source = "roslyn",
                range = new
                {
                    start = new { line = ClampPosition(diagnostic.Range.Start.Line + 1), column = ClampPosition(diagnostic.Range.Start.Character + 1) },
                    end = new { line = ClampPosition(diagnostic.Range.End.Line + 1), column = ClampPosition(diagnostic.Range.End.Character + 1) },
                },
            }),
        });

    private static string DiagnosticCode(JsonElement? code) => code is not { } value
        ? string.Empty
        : value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty
        : value.ValueKind == JsonValueKind.Number ? value.GetRawText()
        : string.Empty;

    private bool TryValidateLanguageDocument(string? path, out string relativePath, out string uri, string requestId)
    {
        relativePath = string.Empty;
        uri = string.Empty;
        if (!TryValidateWorkspaceRelativeTarget(path, out var candidate)
            || !candidate.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            PostError(requestId, "invalid_document", "Select a workspace-relative C# document.", false);
            return false;
        }
        try
        {
            var fullPath = Path.GetFullPath(Path.Combine(_workspaceRoot, candidate));
            var relative = Path.GetRelativePath(_workspaceRoot, fullPath);
            if (Path.IsPathFullyQualified(relative) || relative.Equals("..", StringComparison.Ordinal)
                || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || !File.Exists(fullPath)) throw new IOException();
            var current = _workspaceRoot;
            foreach (var component in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                current = Path.Combine(current, component);
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException();
            }
            relativePath = relative.Replace(Path.DirectorySeparatorChar, '/');
            uri = new Uri(fullPath).AbsoluteUri;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
        {
            PostError(requestId, "invalid_document", "The C# document is unavailable or outside the workspace boundary.", false);
            return false;
        }
    }

    private bool TryValidateLanguageRevision(int revision, string requestId)
    {
        if (revision is >= 0 and <= MaximumRevision) return true;
        PostError(requestId, "invalid_revision", "The C# document revision is invalid.", false);
        return false;
    }

    private bool TryValidateSessionId(string? sessionId, string requestId, out string validated)
    {
        validated = sessionId?.Trim() ?? string.Empty;
        if (validated.Length == 48 && validated.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')) return true;
        PostError(requestId, "invalid_session", "The C# language-session identifier is invalid.", false);
        return false;
    }

    private bool TryValidateDocumentText(string? text, string requestId, out string validated)
    {
        validated = text ?? string.Empty;
        if (text is not null && text.Length <= RoslynLanguageSession.MaximumDocumentCharacters && !text.Contains('\0')) return true;
        PostError(requestId, "invalid_document_text", "The C# document text exceeds the safe language-service boundary.", false);
        return false;
    }

    private bool TryValidateLanguageOperation(
        string? operation,
        int line,
        int character,
        int endLine,
        int endCharacter,
        string? newName,
        string requestId,
        out string validatedOperation,
        out LspPosition position,
        out LspRange range,
        out string? validatedName)
    {
        validatedOperation = operation?.Trim().ToLowerInvariant() ?? string.Empty;
        position = new LspPosition(line, character);
        range = new LspRange(position, new LspPosition(endLine, endCharacter));
        validatedName = null;
        if (validatedOperation is not ("completion" or "hover" or "definition" or "references" or "rename" or "code-actions")
            || line is < 0 or > 1_000_000 || character is < 0 or > 1_000_000)
        {
            PostError(requestId, "invalid_language_operation", "The C# language operation is invalid.", false);
            return false;
        }
        if (validatedOperation == "code-actions"
            && (endLine is < 0 or > 1_000_000 || endCharacter is < 0 or > 1_000_000
                || endLine < line || (endLine == line && endCharacter < character)))
        {
            PostError(requestId, "invalid_language_range", "The C# code-action range is invalid.", false);
            return false;
        }
        if (validatedOperation == "rename")
        {
            validatedName = newName?.Trim();
            if (string.IsNullOrWhiteSpace(validatedName) || validatedName.Length > 512 || validatedName.Contains('\0'))
            {
                PostError(requestId, "invalid_rename", "The C# rename target is invalid.", false);
                return false;
            }
        }
        return true;
    }

    private object? ProjectLanguageResult(JsonElement? value) => value is null
        ? null
        : ProjectLanguageValue(value.Value, null, 0);

    private object? ProjectLanguageValue(JsonElement value, string? propertyName, int depth)
    {
        if (depth > 64) throw new LspProtocolException("The Roslyn language result is too deeply nested.");
        return value.ValueKind switch
        {
            JsonValueKind.Object => ProjectLanguageObject(value, depth + 1),
            JsonValueKind.Array => value.EnumerateArray().Select(item => ProjectLanguageValue(item, null, depth + 1)).ToArray(),
            JsonValueKind.String when propertyName is "uri" or "targetUri" => ProjectLanguageUri(value.GetString()),
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => throw new LspProtocolException("The Roslyn language result contains an unsupported value."),
        };
    }

    private Dictionary<string, object?> ProjectLanguageObject(JsonElement value, int depth)
    {
        var projected = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            var name = property.Name.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                ? ProjectLanguageUri(property.Name)
                : property.Name;
            if (name.Length == 0) continue;
            projected[name] = ProjectLanguageValue(property.Value, property.Name, depth);
        }
        return projected;
    }

    private string ProjectLanguageUri(string? value)
    {
        try
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !uri.IsFile) return string.Empty;
            var fullPath = Path.GetFullPath(uri.LocalPath);
            var relative = Path.GetRelativePath(_workspaceRoot, fullPath);
            if (Path.IsPathFullyQualified(relative) || relative.Equals("..", StringComparison.Ordinal)
                || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || !relative.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return string.Empty;
            return relative.Replace(Path.DirectorySeparatorChar, '/');
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or UriFormatException)
        {
            return string.Empty;
        }
    }

    private static bool IsLanguageFailure(Exception exception) => exception is IOException
        or UnauthorizedAccessException or SecurityException or InvalidOperationException
        or ArgumentException or LspProtocolException or LspSessionException or RoslynStaleDocumentException;

    private static (
        ILanguageToolingEvidenceSource EvidenceSource,
        ILanguageToolingOperationHandler OperationHandler) CreateGccProvider(
            string workspaceRoot,
            GccToolchainProvider fallbackProvider)
    {
        try
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var docker = Path.GetFullPath(Path.Combine(programFiles, "Docker", "Docker", "resources", "bin", "docker.exe"));
            var imageId = Environment.GetEnvironmentVariable("HERMES_IMAGE_REFERENCE")?.Trim() ?? string.Empty;
            var authority = new HermesContainerGccToolingAuthority(new HermesContainerGccToolingOptions
            {
                DockerExecutablePath = docker,
                ExpectedImageId = imageId,
                WorkspaceRoot = workspaceRoot,
            });
            return (authority, new GccLanguageToolingOperationHandler(authority, workspaceRoot));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException
            or UnauthorizedAccessException or SecurityException)
        {
            return (
                KnownPinnedToolchainEvidenceSource.Create(fallbackProvider, ownsProvider: false),
                new GccLanguageToolingOperationHandler(fallbackProvider, workspaceRoot));
        }
    }

    private static PythonLanguageToolingComponents CreatePythonProvider(string workspaceRoot)
    {
        try
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var docker = Path.GetFullPath(Path.Combine(programFiles, "Docker", "Docker", "resources", "bin", "docker.exe"));
            var imageId = Environment.GetEnvironmentVariable("HERMES_IMAGE_REFERENCE")?.Trim() ?? string.Empty;
            var authority = new HermesContainerPythonToolingAuthority(new HermesContainerPythonToolingOptions
            {
                DockerExecutablePath = docker,
                ExpectedImageId = imageId,
                WorkspaceRoot = workspaceRoot,
            });
            return PythonLanguageToolingProvider.CreateReceiptBound(workspaceRoot, authority, authority);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException
            or UnauthorizedAccessException or SecurityException)
        {
            return PythonLanguageToolingProvider.CreateFailClosed(workspaceRoot);
        }
    }

    private static JavaJdtLanguageToolingProvider CreateJavaProvider(string workspaceRoot, string applicationInstallRoot)
    {
        var relative = JavaJdtProvisioning.BundleRelativePath.Replace('/', Path.DirectorySeparatorChar);
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, relative),
            Path.Combine(applicationInstallRoot, relative),
        };
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var root = Path.GetFullPath(candidate);
                if (Directory.Exists(root)
                    && File.Exists(Path.Combine(root, JavaJdtProvisioning.ReceiptFileName)))
                    return JavaJdtLanguageToolingProvider.CreateProvisioned(workspaceRoot, root);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException
                or UnauthorizedAccessException or SecurityException)
            { }
        }
        return JavaJdtLanguageToolingProvider.CreateUnprovisioned(workspaceRoot);
    }

    private static RoslynLanguageServerProvider CreateRoslynProvider(string applicationInstallRoot)
    {
        var installerRoot = ResolveRoslynInstallerRoot(applicationInstallRoot, AppContext.BaseDirectory);
        var executablePath = Path.Combine(installerRoot, "Microsoft.CodeAnalysis.LanguageServer.exe");
        var dotnetRoot = Path.Combine(installerRoot, "dotnet");
        var expectedSha256 = new string('0', 64);
        var packageVersion = "unprovisioned";
        var receiptPath = Path.Combine(installerRoot, "provider.json");

        try
        {
            var receipt = new FileInfo(receiptPath);
            if (receipt.Exists
                && receipt.Length is > 0 and <= MaximumRoslynReceiptBytes
                && (receipt.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                using var document = JsonDocument.Parse(File.ReadAllText(receiptPath));
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("packageVersion", out var versionElement)
                    && versionElement.ValueKind == JsonValueKind.String
                    && root.TryGetProperty("executableSha256", out var hashElement)
                    && hashElement.ValueKind == JsonValueKind.String)
                {
                    var candidateVersion = versionElement.GetString()?.Trim() ?? string.Empty;
                    var candidateHash = hashElement.GetString()?.Trim().ToLowerInvariant() ?? string.Empty;
                    if (candidateVersion.Length is > 0 and <= 128
                        && !candidateVersion.Contains('\0')
                        && candidateHash.Length == 64
                        && candidateHash.All(Uri.IsHexDigit))
                    {
                        packageVersion = candidateVersion;
                        expectedSha256 = candidateHash;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or JsonException)
        {
            // Discovery reports the fixed provider unavailable; renderer input never configures this path.
        }

        return new RoslynLanguageServerProvider(new RoslynLanguageServerConfiguration(
            installerRoot,
            executablePath,
            expectedSha256,
            packageVersion,
            dotnetRoot));
    }

    internal static string ResolveRoslynInstallerRoot(string applicationInstallRoot, string binaryRoot)
    {
        var configured = Path.Combine(applicationInstallRoot, "developer-services", "roslyn");
        if (File.Exists(Path.Combine(configured, "provider.json"))) return configured;

        // The source-tree launcher keeps HERMES_INSTALL_ROOT at the bundle root for Docker/CAD,
        // while the self-contained desktop owns provider assets beside its executable.
        var besideExecutable = Path.Combine(
            Path.GetFullPath(binaryRoot),
            "developer-services",
            "roslyn");
        return File.Exists(Path.Combine(besideExecutable, "provider.json"))
            ? besideExecutable
            : configured;
    }

    internal static string ResolveArduinoWorkbenchRoot(string applicationInstallRoot, string binaryRoot)
    {
        static bool HasReceipt(string root) => File.Exists(Path.Combine(
            root,
            "toolchains",
            "arduino",
            "hermes-toolchain-receipt.json"));

        var configured = Path.GetFullPath(applicationInstallRoot);
        if (HasReceipt(configured)) return configured;

        var besideExecutable = Path.GetFullPath(binaryRoot);
        return HasReceipt(besideExecutable) ? besideExecutable : configured;
    }

    private void PostError(string requestId, string code, string message, bool retryable) =>
        _postMessage(new
        {
            type = "developerServices.error",
            version = ProtocolVersion,
            requestId,
            code,
            message,
            retryable,
        });

    private sealed record ProviderDescription(
        ToolchainProviderDescriptor Descriptor,
        ToolchainAvailability Availability);

    private sealed class LanguageDocument(string sessionId, string relativePath, string uri, int revision)
    {
        public string SessionId { get; } = sessionId;
        public string RelativePath { get; } = relativePath;
        public string Uri { get; } = uri;
        public int Revision { get; set; } = revision;
        public bool Ready { get; set; }
        public LspPublishDiagnosticsParams? PendingDiagnostics { get; set; }
    }

    private enum DeveloperOperation
    {
        Build,
        Analyze,
    }

}

internal interface IDeveloperRoslynHost : IAsyncDisposable
{
    event Action<LspPublishDiagnosticsParams>? DiagnosticsPublished;

    Task OpenDocumentAsync(string workspaceRoot, string uri, int revision, string text, CancellationToken cancellationToken);

    Task ChangeDocumentAsync(string uri, int revision, string text, CancellationToken cancellationToken);

    Task CloseDocumentAsync(string uri, CancellationToken cancellationToken);

    Task<RoslynLanguageResult?> CompletionAsync(string uri, int revision, LspPosition position, CancellationToken cancellationToken);

    Task<RoslynLanguageResult?> HoverAsync(string uri, int revision, LspPosition position, CancellationToken cancellationToken);

    Task<RoslynLanguageResult?> DefinitionAsync(string uri, int revision, LspPosition position, CancellationToken cancellationToken);

    Task<RoslynLanguageResult?> ReferencesAsync(string uri, int revision, LspPosition position, bool includeDeclaration, CancellationToken cancellationToken);

    Task<RoslynLanguageResult?> RenameAsync(string uri, int revision, LspPosition position, string newName, CancellationToken cancellationToken);

    Task<RoslynLanguageResult?> CodeActionsAsync(string uri, int revision, LspRange range, CancellationToken cancellationToken);
}

internal sealed class DeveloperRoslynHost(RoslynLanguageServerProvider provider) : IDeveloperRoslynHost
{
    private const int MaximumWorkspaceCandidates = 64;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly HashSet<string> _openDocuments = new(StringComparer.OrdinalIgnoreCase);
    private bool _subscribed;
    private string? _workspacePath;

    public event Action<LspPublishDiagnosticsParams>? DiagnosticsPublished;

    public async Task OpenDocumentAsync(
        string workspaceRoot,
        string uri,
        int revision,
        string text,
        CancellationToken cancellationToken)
    {
        var resolvedRoot = Path.GetFullPath(workspaceRoot);
        var documentPath = Path.GetFullPath(new Uri(uri).LocalPath);
        var selectedWorkspace = SelectWorkspacePath(resolvedRoot, documentPath);
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_workspacePath is not null
                && !_workspacePath.Equals(selectedWorkspace, StringComparison.OrdinalIgnoreCase))
            {
                if (_openDocuments.Count != 0)
                    throw new InvalidOperationException("Close the active Roslyn project documents before opening a different project.");
                if (_subscribed && provider.LifecycleState == ToolchainLifecycleState.Ready)
                    provider.Session.DiagnosticsPublished -= ForwardDiagnostics;
                _subscribed = false;
                await provider.StopAsync(cancellationToken);
                _workspacePath = null;
            }

            await provider.StartAsync(new ToolchainStartContext(
                resolvedRoot,
                [new WorkspacePathMapping(resolvedRoot, resolvedRoot)],
                ToolchainExecutionKind.LocalSidecarProcess), cancellationToken);
            if (!_subscribed)
            {
                provider.Session.DiagnosticsPublished += ForwardDiagnostics;
                _subscribed = true;
            }
            if (_workspacePath is null)
            {
                await provider.Session.OpenSolutionAsync(selectedWorkspace, cancellationToken);
                _workspacePath = selectedWorkspace;
            }
            await provider.Session.OpenDocumentAsync(uri, revision, text, cancellationToken);
            _openDocuments.Add(uri);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public Task ChangeDocumentAsync(string uri, int revision, string text, CancellationToken cancellationToken) =>
        provider.Session.ChangeDocumentAsync(uri, revision, text, cancellationToken);

    public async Task CloseDocumentAsync(string uri, CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await provider.Session.CloseDocumentAsync(uri, cancellationToken);
            _openDocuments.Remove(uri);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public Task<RoslynLanguageResult?> CompletionAsync(string uri, int revision, LspPosition position, CancellationToken cancellationToken) =>
        provider.Session.CompletionAsync(uri, revision, position, cancellationToken);

    public Task<RoslynLanguageResult?> HoverAsync(string uri, int revision, LspPosition position, CancellationToken cancellationToken) =>
        provider.Session.HoverAsync(uri, revision, position, cancellationToken);

    public Task<RoslynLanguageResult?> DefinitionAsync(string uri, int revision, LspPosition position, CancellationToken cancellationToken) =>
        provider.Session.DefinitionAsync(uri, revision, position, cancellationToken);

    public Task<RoslynLanguageResult?> ReferencesAsync(string uri, int revision, LspPosition position, bool includeDeclaration, CancellationToken cancellationToken) =>
        provider.Session.ReferencesAsync(uri, revision, position, includeDeclaration, cancellationToken);

    public Task<RoslynLanguageResult?> RenameAsync(string uri, int revision, LspPosition position, string newName, CancellationToken cancellationToken) =>
        provider.Session.RenameAsync(uri, revision, position, newName, cancellationToken);

    public Task<RoslynLanguageResult?> CodeActionsAsync(string uri, int revision, LspRange range, CancellationToken cancellationToken) =>
        provider.Session.CodeActionsAsync(uri, revision, range, cancellationToken: cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (_subscribed && provider.LifecycleState == ToolchainLifecycleState.Ready)
                provider.Session.DiagnosticsPublished -= ForwardDiagnostics;
            _subscribed = false;
            _openDocuments.Clear();
            _workspacePath = null;
        }
        finally
        {
            _lifecycleGate.Release();
            _lifecycleGate.Dispose();
        }
    }

    private void ForwardDiagnostics(LspPublishDiagnosticsParams diagnostics) => DiagnosticsPublished?.Invoke(diagnostics);

    private static string SelectWorkspacePath(string workspaceRoot, string documentPath)
    {
        var relative = Path.GetRelativePath(workspaceRoot, documentPath);
        if (Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The Roslyn document is outside the active workspace.");

        var rootSolutions = EnumerateWorkspaceFiles(workspaceRoot, "*.sln")
            .Concat(EnumerateWorkspaceFiles(workspaceRoot, "*.slnx"))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumWorkspaceCandidates + 1)
            .ToArray();
        if (rootSolutions.Length == 1) return rootSolutions[0];
        if (rootSolutions.Length > MaximumWorkspaceCandidates)
            throw new InvalidOperationException("The Roslyn workspace candidate limit was exceeded.");

        var current = Directory.GetParent(documentPath)?.FullName
            ?? throw new InvalidOperationException("The Roslyn document directory is unavailable.");
        while (true)
        {
            var projects = EnumerateWorkspaceFiles(current, "*.csproj")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Take(MaximumWorkspaceCandidates + 1)
                .ToArray();
            if (projects.Length == 1) return projects[0];
            if (projects.Length > 1)
                throw new InvalidOperationException("The Roslyn project selection is ambiguous for this document.");
            if (current.Equals(workspaceRoot, StringComparison.OrdinalIgnoreCase)) break;
            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || !IsWithinRoot(workspaceRoot, parent)) break;
            current = parent;
        }

        if (rootSolutions.Length > 1)
            throw new InvalidOperationException("The Roslyn solution selection is ambiguous for this document.");
        throw new InvalidOperationException("No Roslyn solution or C# project owns this document.");
    }

    private static IEnumerable<string> EnumerateWorkspaceFiles(string directory, string pattern)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("The Roslyn workspace path traverses a reparse point.");
        return Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly)
            .Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0);
    }

    private static bool IsWithinRoot(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative)
            && !relative.Equals("..", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }
}
