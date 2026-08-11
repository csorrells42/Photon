using System.Collections.ObjectModel;

namespace HermesDeveloperServices.LanguageTooling;

/// <summary>
/// Host-owned registry that turns only verified local evidence into renderer-visible availability.
/// Catalog entries without a source remain explicitly unavailable; declarations are never proof.
/// </summary>
public sealed class LanguageToolingTrustedRegistry : IAsyncDisposable
{
    private readonly string _workspaceRoot;
    private readonly Dictionary<string, ILanguageToolingEvidenceSource> _sourcesByCapability =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, ILanguageToolingOperationHandler> _handlersByOperation =
        new(StringComparer.Ordinal);
    private readonly List<ILanguageToolingEvidenceSource> _ownedSources = [];
    private bool _disposed;

    public LanguageToolingTrustedRegistry(string workspaceRoot)
    {
        _workspaceRoot = TrustedToolchainPathPolicy.RequireRoot(workspaceRoot, "workspace");
    }

    public void RegisterEvidenceSource(ILanguageToolingEvidenceSource source)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(source);
        var provider = LanguageToolingCatalog.RequireProvider(source.ProviderId);
        if (source.CapabilityIds.Count == 0)
            throw new ArgumentException("An evidence source must declare at least one capability.", nameof(source));
        var normalized = source.CapabilityIds.Select(id =>
            LanguageToolingCatalog.RequireCapability(provider.Id, id).Id).ToArray();
        if (normalized.Distinct(StringComparer.Ordinal).Count() != normalized.Length)
            throw new ArgumentException("An evidence source declared a duplicate capability.", nameof(source));
        if (normalized.Any(_sourcesByCapability.ContainsKey))
            throw new InvalidOperationException("A language-tooling capability already has a trusted evidence source.");

        foreach (var capability in normalized) _sourcesByCapability.Add(capability, source);
        _ownedSources.Add(source);
    }

    public void RegisterOperationHandler(ILanguageToolingOperationHandler handler)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(handler);
        _ = LanguageToolingCatalog.RequireProvider(handler.ProviderId);
        if (handler.Operations.Count == 0)
            throw new ArgumentException("An operation handler must declare at least one operation.", nameof(handler));
        var normalized = handler.Operations.Select(LanguageToolingRequestPolicy.RequireOperationName).ToArray();
        if (normalized.Distinct(StringComparer.Ordinal).Count() != normalized.Length)
            throw new ArgumentException("An operation handler declared a duplicate operation.", nameof(handler));
        var keys = normalized.Select(operation => OperationKey(handler.ProviderId, operation)).ToArray();
        if (keys.Any(_handlersByOperation.ContainsKey))
            throw new InvalidOperationException("A language-tooling operation already has a trusted handler.");
        foreach (var key in keys)
        {
            _handlersByOperation.Add(key, handler);
        }
    }

    public async ValueTask<IReadOnlyList<LanguageToolingProviderEvidence>> DescribeAllAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var results = new List<LanguageToolingProviderEvidence>(LanguageToolingCatalog.Definitions.Count);
        foreach (var provider in LanguageToolingCatalog.Definitions)
            results.Add(await DescribeProviderAsync(provider.Id, cancellationToken).ConfigureAwait(false));
        return new ReadOnlyCollection<LanguageToolingProviderEvidence>(results);
    }

    public async ValueTask<LanguageToolingProviderEvidence> DescribeProviderAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var definition = LanguageToolingCatalog.RequireProvider(providerId);
        var checkedAt = DateTimeOffset.UtcNow;
        var statuses = new Dictionary<string, LanguageToolingCapabilityStatus>(StringComparer.Ordinal);
        var sources = definition.Capabilities
            .Select(item => _sourcesByCapability.GetValueOrDefault(item.Id))
            .Where(item => item is not null)
            .Distinct(ReferenceEqualityComparer.Instance)
            .Cast<ILanguageToolingEvidenceSource>()
            .ToArray();

        foreach (var source in sources)
        {
            IReadOnlyList<LanguageToolingCapabilityStatus> inspected;
            try
            {
                inspected = await source.InspectAsync(_workspaceRoot, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or TrustedToolchainValidationException
                or ArgumentException
                or System.Security.SecurityException
                or System.Text.Json.JsonException
                or System.Security.Cryptography.CryptographicException)
            {
                inspected = source.CapabilityIds.Select(id => new LanguageToolingCapabilityStatus(
                    id,
                    LanguageToolingCapabilityState.Error,
                    "trusted-inspection-failed",
                    "The trusted host could not inspect this runtime.")).ToArray();
            }

            var expected = source.CapabilityIds.ToHashSet(StringComparer.Ordinal);
            var returned = inspected.GroupBy(item => item.CapabilityId, StringComparer.Ordinal).ToArray();
            foreach (var capability in expected)
            {
                var exact = returned.FirstOrDefault(group => group.Key.Equals(capability, StringComparison.Ordinal));
                statuses[capability] = exact is not null && exact.Count() == 1
                    ? NormalizeStatus(exact.Single(), capability)
                    : MissingEvidence(capability, "invalid-trusted-evidence");
            }
        }

        var capabilities = definition.Capabilities.Select(item =>
            statuses.GetValueOrDefault(item.Id) ?? MissingEvidence(item.Id, "trusted-runtime-not-registered")).ToArray();
        return new LanguageToolingProviderEvidence(
            LanguageToolingProtocol.Contract,
            "trusted-host",
            $"desktop:{definition.Id}:{Guid.NewGuid():N}",
            definition.Id,
            checkedAt,
            capabilities);
    }

    public async ValueTask<LanguageToolingOperationResult> ExecuteAsync(
        LanguageToolingHostRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var validated = LanguageToolingRequestPolicy.Validate(request);
        if (validated is InspectLanguageToolingProviderRequest)
            throw new LanguageToolingRequestException(
                "inspect-result-shape",
                "Provider inspection uses the evidence response shape.");
        var capabilityId = LanguageToolingCatalog.CapabilityFor(validated);
        var evidence = await DescribeProviderAsync(validated.ProviderId, cancellationToken).ConfigureAwait(false);
        var capability = evidence.Capabilities.Single(item => item.CapabilityId.Equals(capabilityId, StringComparison.Ordinal));
        if (capability.Availability != LanguageToolingCapabilityState.Available)
            throw new LanguageToolingRequestException(
                "capability-unavailable",
                capability.SafeMessage);
        if (!_handlersByOperation.TryGetValue(OperationKey(validated.ProviderId, validated.Operation), out var handler))
            throw new LanguageToolingRequestException(
                "operation-not-wired",
                "The trusted host has not registered this structured operation.");

        var result = await handler.ExecuteAsync(validated, cancellationToken).ConfigureAwait(false);
        return LanguageToolingResultPolicy.Normalize(result);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var source in _ownedSources)
            await source.DisposeAsync().ConfigureAwait(false);
        _ownedSources.Clear();
        _sourcesByCapability.Clear();
        _handlersByOperation.Clear();
    }

    private static LanguageToolingCapabilityStatus NormalizeStatus(
        LanguageToolingCapabilityStatus status,
        string expectedId)
    {
        if (!status.CapabilityId.Equals(expectedId, StringComparison.Ordinal))
            return MissingEvidence(expectedId, "invalid-trusted-evidence");
        return new LanguageToolingCapabilityStatus(
            expectedId,
            Enum.IsDefined(status.Availability) ? status.Availability : LanguageToolingCapabilityState.Unavailable,
            KnownPinnedToolchainEvidenceSource.SafeCode(status.Code, "runtime-not-provisioned"),
            KnownPinnedToolchainEvidenceSource.SafeMessage(status.SafeMessage, "The pinned runtime is unavailable."),
            KnownPinnedToolchainEvidenceSource.SafeVersion(status.Version));
    }

    private static LanguageToolingCapabilityStatus MissingEvidence(string capabilityId, string code) => new(
        capabilityId,
        LanguageToolingCapabilityState.Unavailable,
        code,
        "The trusted desktop host has no verified pinned runtime evidence for this capability.");

    private static string OperationKey(string providerId, string operation) => $"{providerId}\0{operation}";
}

public static class LanguageToolingRequestPolicy
{
    private const int MaximumIdentifierLength = 128;
    private const int MaximumPathLength = 2_048;

    public static LanguageToolingHostRequest Validate(LanguageToolingHostRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Version != LanguageToolingProtocol.Version)
            throw new LanguageToolingRequestException("unsupported-version", "The language-tooling protocol version is unsupported.");
        RequireIdentifier(request.RequestId, "request identifier");
        RequireIdentifier(request.WorkspaceId, "workspace identifier");
        _ = LanguageToolingCatalog.RequireProvider(request.ProviderId);
        _ = RequireOperationName(request.Operation);

        switch (request)
        {
            case InspectLanguageToolingProviderRequest:
                break;
            case StartLanguageToolingSessionRequest value:
                _ = RequireWorkspacePath(value.DocumentPath);
                break;
            case StopLanguageToolingSessionRequest value:
                RequireIdentifier(value.SessionId, "language session identifier");
                break;
            case InspectLanguageToolingProjectRequest value:
                _ = RequireWorkspacePath(value.ProjectPath);
                break;
            case CompileLanguageToolingRequest value:
                _ = RequireWorkspacePath(value.TargetPath);
                if (value.Mode is not ("debug" or "release" or "check"))
                    throw new LanguageToolingRequestException("invalid-mode", "The compile mode is invalid.");
                if (value.BoardFqbn is not null
                    && (!value.ProviderId.Equals(LanguageToolingCatalog.Arduino, StringComparison.Ordinal)
                        || !IsBoardFqbn(value.BoardFqbn)))
                    throw new LanguageToolingRequestException("invalid-board", "The Arduino board identifier is invalid.");
                break;
            case RunLanguageToolingTestsRequest value:
                _ = RequireWorkspacePath(value.TargetPath);
                if (value.Selection is not null
                    && (value.Selection.Length is 0 or > 512
                        || value.Selection.Any(character => !(char.IsAsciiLetterOrDigit(character)
                            || "_./:\\[](),-".Contains(character, StringComparison.Ordinal)))))
                    throw new LanguageToolingRequestException("invalid-selection", "The test selection is invalid.");
                break;
            case StartLanguageToolingDebugRequest value:
                _ = RequireWorkspacePath(value.ProgramPath);
                break;
            case InspectLanguageToolingRemoteTargetRequest value:
                RequireIdentifier(value.TargetId, "remote target identifier");
                break;
            case DeployLanguageToolingFileRequest value:
                RequireIdentifier(value.TargetId, "remote target identifier");
                RequireIdentifier(value.DestinationId, "remote destination identifier");
                _ = RequireWorkspacePath(value.SourcePath);
                break;
            default:
                throw new LanguageToolingRequestException("unsupported-operation", "The language-tooling request type is unsupported.");
        }
        return request;
    }

    public static string RequireOperationName(string value)
    {
        if (value is not ("inspect-provider"
            or "start-language-session"
            or "stop-language-session"
            or "inspect-project"
            or "compile"
            or "run-tests"
            or "start-debug"
            or "inspect-remote-target"
            or "deploy-file"))
            throw new LanguageToolingRequestException("unsupported-operation", "The language-tooling operation is unsupported.");
        return value;
    }

    internal static string RequireWorkspacePath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim().Replace('\\', '/');
        var segments = normalized.Split('/');
        if (normalized.Length > MaximumPathLength
            || normalized.StartsWith('/')
            || (normalized.Length >= 2 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':')
            || segments.Any(segment => segment.Length == 0 || segment is "." or "..")
            || normalized.Any(char.IsControl))
            throw new LanguageToolingRequestException("invalid-path", "Tooling paths must stay workspace-relative.");
        return normalized;
    }

    private static void RequireIdentifier(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaximumIdentifierLength
            || !value.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '.' or '_' or ':' or '-'))
            throw new LanguageToolingRequestException("invalid-identifier", $"The {label} is invalid.");
    }

    private static bool IsBoardFqbn(string value)
    {
        if (value.Length is 0 or > 256 || value.Any(character => !(char.IsAsciiLetterOrDigit(character)
            || character is '.' or '_' or '-' or ':'))) return false;
        var parts = value.Split(':');
        return parts.Length is >= 3 and <= 6 && parts.All(part => part.Length > 0);
    }
}

internal static class LanguageToolingResultPolicy
{
    internal static LanguageToolingOperationResult Normalize(LanguageToolingOperationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var diagnostics = (result.Diagnostics ?? []).Take(2_000).Select(item => new LanguageToolingDiagnostic(
            NormalizeDiagnosticPath(item.FilePath),
            NormalizeSeverity(item.Severity),
            KnownPinnedToolchainEvidenceSource.SafeCode(item.Code, "diagnostic"),
            KnownPinnedToolchainEvidenceSource.SafeMessage(item.Message, "A tooling diagnostic was reported."),
            Math.Clamp(item.StartLine, 1, 1_000_000),
            Math.Clamp(item.StartColumn, 1, 1_000_000),
            Math.Clamp(item.EndLine, 1, 1_000_000),
            Math.Clamp(item.EndColumn, 1, 1_000_000))).ToArray();
        var artifacts = (result.ArtifactPaths ?? []).Take(128)
            .Select(LanguageToolingRequestPolicy.RequireWorkspacePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var sessionId = result.SessionId;
        if (sessionId is not null && (sessionId.Length is 0 or > 128
            || !sessionId.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '.' or '_' or ':' or '-')))
            throw new LanguageToolingRequestException("invalid-result", "The trusted handler returned an invalid session identifier.");
        return new LanguageToolingOperationResult(
            result.Succeeded,
            KnownPinnedToolchainEvidenceSource.SafeCode(result.Code, result.Succeeded ? "ok" : "operation-failed"),
            KnownPinnedToolchainEvidenceSource.SafeMessage(result.SafeMessage, result.Succeeded
                ? "The structured operation completed."
                : "The structured operation failed."),
            diagnostics,
            artifacts,
            sessionId);
    }

    private static string NormalizeSeverity(string severity) => severity.ToLowerInvariant() switch
    {
        "error" => "error",
        "warning" => "warning",
        "info" => "info",
        _ => "hint",
    };

    private static string NormalizeDiagnosticPath(string path) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty : LanguageToolingRequestPolicy.RequireWorkspacePath(path);
}
