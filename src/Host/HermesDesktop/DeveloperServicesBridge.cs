using HermesDeveloperServices;
using HermesDeveloperServices.LanguageTooling;
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
    public const int ProtocolVersion = 2;
    private const int MaximumRequestIdLength = 128;
    private const int MaximumTargetPathLength = 2_048;
    private const int MaximumRevision = 1_000_000_000;
    private const int MaximumDiagnosticTextLength = 2_048;
    private const int MaximumRoslynReceiptBytes = 16 * 1024;
    private const int MaximumLanguageDocuments = 32;

    private readonly string _workspaceRoot;
    private readonly Action<object> _postMessage;
    private readonly DotnetBuildRunner _buildRunner = new();
    private readonly ToolchainProviderRegistry _providers = new();
    private readonly LanguageToolingTrustedRegistry _languageTooling;
    private readonly LanguageToolingHostBridge _languageToolingHost;
    private readonly IDeveloperRoslynHost _roslynHost;
    private readonly object _gate = new();
    private readonly Dictionary<string, CancellationTokenSource> _activeOperations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _languageToolingOperations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LanguageDocument> _languageDocuments = new(StringComparer.Ordinal);
    private string? _latestRequestId;
    private int _latestRevision;
    private bool _disposed;

    public DeveloperServicesBridge(string workspaceRoot, string applicationInstallRoot, Action<object> postMessage)
        : this(workspaceRoot, applicationInstallRoot, postMessage, null)
    {
    }

    internal DeveloperServicesBridge(
        string workspaceRoot,
        string applicationInstallRoot,
        Action<object> postMessage,
        IDeveloperRoslynHost? roslynHost)
    {
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _postMessage = postMessage;
        var installRoot = Path.GetFullPath(applicationInstallRoot);
        var dotnet = new DotnetToolchainProvider();
        var roslyn = CreateRoslynProvider(installRoot);
        var debugger = new HermesDotNetDebuggerProvider(
            installRoot,
            new DenyUnreviewedDebugAuthorizationPolicy());
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
        var arduinoOptions = new ArduinoProviderOptions(
            installRoot,
            "toolchains/arduino",
            "hermes-toolchain-receipt.json",
            "arduino-config.json",
            "developer-services/arduino-state");
        var java = JavaJdtLanguageToolingProvider.CreateUnprovisioned(_workspaceRoot);
        var python = PythonLanguageToolingProvider.CreateFailClosed(_workspaceRoot);
        _languageTooling = LanguageToolingRegistryFactory.Create(
            _workspaceRoot,
            [
                KnownPinnedToolchainEvidenceSource.Create(roslyn, ownsProvider: false),
                KnownPinnedToolchainEvidenceSource.Create(debugger, ownsProvider: false),
                KnownPinnedToolchainEvidenceSource.Create(gcc, ownsProvider: false),
                new ArduinoPinnedEvidenceSource(arduinoOptions),
                java,
                python.EvidenceSource,
            ],
            [
                new GccLanguageToolingOperationHandler(gcc, _workspaceRoot),
                new ArduinoLanguageToolingOperationHandler(arduinoOptions, _workspaceRoot),
                java,
                python.OperationHandler,
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

    public void CancelLanguageTooling(int version, string? requestId, string? targetRequestId)
    {
        var id = requestId?.Trim() ?? string.Empty;
        var target = targetRequestId?.Trim() ?? string.Empty;
        if (version != LanguageToolingProtocol.Version || !ValidLanguageToolingIdentifier(id) || !ValidLanguageToolingIdentifier(target))
        {
            PostLanguageToolingFailure(id, string.Empty, "cancel", "invalid-envelope", "The language-tooling cancellation request is invalid.");
            return;
        }
        bool accepted;
        lock (_gate)
        {
            accepted = _languageToolingOperations.TryGetValue(target, out var cancellation);
            cancellation?.Cancel();
        }
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
        bool accepted;
        lock (_gate)
        {
            accepted = _activeOperations.TryGetValue(id, out var cancellation);
            cancellation?.Cancel();
        }
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
            await _roslynHost.EnsureStartedAsync(_workspaceRoot, CancellationToken.None);
            await _roslynHost.OpenDocumentAsync(uri, revision, documentText, CancellationToken.None);
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

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource[] active;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            active = _activeOperations.Values.Concat(_languageToolingOperations.Values).ToArray();
        }
        foreach (var cancellation in active) cancellation.Cancel();
        _roslynHost.DiagnosticsPublished -= HandleRoslynDiagnostics;
        await _roslynHost.DisposeAsync();
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

    private static bool IsLanguageFailure(Exception exception) => exception is IOException
        or UnauthorizedAccessException or SecurityException or InvalidOperationException
        or ArgumentException or LspProtocolException or LspSessionException or RoslynStaleDocumentException;

    private static RoslynLanguageServerProvider CreateRoslynProvider(string applicationInstallRoot)
    {
        var installerRoot = Path.Combine(applicationInstallRoot, "developer-services", "roslyn");
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

    private sealed class DenyUnreviewedDebugAuthorizationPolicy : IDotNetDebugAuthorizationPolicy
    {
        public ValueTask<bool> AuthorizeLaunchAsync(
            string workspaceRoot,
            DotNetDebugLaunchRequest request,
            CancellationToken cancellationToken) => ValueTask.FromResult(false);

        public ValueTask<bool> AuthorizeAttachAsync(
            string workspaceRoot,
            DotNetDebugAttachRequest request,
            CancellationToken cancellationToken) => ValueTask.FromResult(false);
    }
}

internal interface IDeveloperRoslynHost : IAsyncDisposable
{
    event Action<LspPublishDiagnosticsParams>? DiagnosticsPublished;

    Task EnsureStartedAsync(string workspaceRoot, CancellationToken cancellationToken);

    Task OpenDocumentAsync(string uri, int revision, string text, CancellationToken cancellationToken);

    Task ChangeDocumentAsync(string uri, int revision, string text, CancellationToken cancellationToken);

    Task CloseDocumentAsync(string uri, CancellationToken cancellationToken);
}

internal sealed class DeveloperRoslynHost(RoslynLanguageServerProvider provider) : IDeveloperRoslynHost
{
    private bool _subscribed;

    public event Action<LspPublishDiagnosticsParams>? DiagnosticsPublished;

    public async Task EnsureStartedAsync(string workspaceRoot, CancellationToken cancellationToken)
    {
        await provider.StartAsync(new ToolchainStartContext(
            workspaceRoot,
            [new WorkspacePathMapping(workspaceRoot, workspaceRoot)],
            ToolchainExecutionKind.LocalSidecarProcess), cancellationToken);
        if (_subscribed) return;
        provider.Session.DiagnosticsPublished += ForwardDiagnostics;
        _subscribed = true;
    }

    public Task OpenDocumentAsync(string uri, int revision, string text, CancellationToken cancellationToken) =>
        provider.Session.OpenDocumentAsync(uri, revision, text, cancellationToken);

    public Task ChangeDocumentAsync(string uri, int revision, string text, CancellationToken cancellationToken) =>
        provider.Session.ChangeDocumentAsync(uri, revision, text, cancellationToken);

    public Task CloseDocumentAsync(string uri, CancellationToken cancellationToken) =>
        provider.Session.CloseDocumentAsync(uri, cancellationToken);

    public ValueTask DisposeAsync()
    {
        if (_subscribed && provider.LifecycleState == ToolchainLifecycleState.Ready)
            provider.Session.DiagnosticsPublished -= ForwardDiagnostics;
        _subscribed = false;
        return ValueTask.CompletedTask;
    }

    private void ForwardDiagnostics(LspPublishDiagnosticsParams diagnostics) => DiagnosticsPublished?.Invoke(diagnostics);
}
