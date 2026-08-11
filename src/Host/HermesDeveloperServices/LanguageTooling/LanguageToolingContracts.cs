namespace HermesDeveloperServices.LanguageTooling;

public static class LanguageToolingProtocol
{
    public const int Version = 1;
    public const string Contract = "language-tooling-providers/v1";
}

public enum LanguageToolingCapabilityState
{
    Available,
    Unavailable,
    Error,
}

public sealed record LanguageToolingCapabilityStatus(
    string CapabilityId,
    LanguageToolingCapabilityState Availability,
    string Code,
    string SafeMessage,
    string? Version = null);

public sealed record LanguageToolingProviderEvidence(
    string Contract,
    string Source,
    string EvidenceId,
    string ProviderId,
    DateTimeOffset CheckedAt,
    IReadOnlyList<LanguageToolingCapabilityStatus> Capabilities);

public sealed record LanguageToolingCapabilityDefinition(
    string Id,
    string Kind,
    string Label);

public sealed record LanguageToolingProviderDefinition(
    string Id,
    string Label,
    IReadOnlyList<LanguageToolingCapabilityDefinition> Capabilities);

/// <summary>
/// Renderer requests are typed product intents. They deliberately contain no executable, command,
/// argument-vector, environment, download URL, package source, credential, or raw remote path.
/// </summary>
public abstract record LanguageToolingHostRequest(
    int Version,
    string RequestId,
    string WorkspaceId,
    string ProviderId)
{
    public abstract string Operation { get; }
}

public sealed record InspectLanguageToolingProviderRequest(
    int Version,
    string RequestId,
    string WorkspaceId,
    string ProviderId)
    : LanguageToolingHostRequest(Version, RequestId, WorkspaceId, ProviderId)
{
    public override string Operation => "inspect-provider";
}

public sealed record StartLanguageToolingSessionRequest(
    int Version,
    string RequestId,
    string WorkspaceId,
    string ProviderId,
    string DocumentPath)
    : LanguageToolingHostRequest(Version, RequestId, WorkspaceId, ProviderId)
{
    public override string Operation => "start-language-session";
}

public sealed record StopLanguageToolingSessionRequest(
    int Version,
    string RequestId,
    string WorkspaceId,
    string ProviderId,
    string SessionId)
    : LanguageToolingHostRequest(Version, RequestId, WorkspaceId, ProviderId)
{
    public override string Operation => "stop-language-session";
}

public sealed record InspectLanguageToolingProjectRequest(
    int Version,
    string RequestId,
    string WorkspaceId,
    string ProviderId,
    string ProjectPath)
    : LanguageToolingHostRequest(Version, RequestId, WorkspaceId, ProviderId)
{
    public override string Operation => "inspect-project";
}

public sealed record CompileLanguageToolingRequest(
    int Version,
    string RequestId,
    string WorkspaceId,
    string ProviderId,
    string TargetPath,
    string Mode,
    string? BoardFqbn = null)
    : LanguageToolingHostRequest(Version, RequestId, WorkspaceId, ProviderId)
{
    public override string Operation => "compile";
}

public sealed record RunLanguageToolingTestsRequest(
    int Version,
    string RequestId,
    string WorkspaceId,
    string ProviderId,
    string TargetPath,
    string? Selection = null)
    : LanguageToolingHostRequest(Version, RequestId, WorkspaceId, ProviderId)
{
    public override string Operation => "run-tests";
}

public sealed record StartLanguageToolingDebugRequest(
    int Version,
    string RequestId,
    string WorkspaceId,
    string ProviderId,
    string ProgramPath,
    bool StopAtEntry)
    : LanguageToolingHostRequest(Version, RequestId, WorkspaceId, ProviderId)
{
    public override string Operation => "start-debug";
}

public sealed record InspectLanguageToolingRemoteTargetRequest(
    int Version,
    string RequestId,
    string WorkspaceId,
    string ProviderId,
    string TargetId)
    : LanguageToolingHostRequest(Version, RequestId, WorkspaceId, ProviderId)
{
    public override string Operation => "inspect-remote-target";
}

public sealed record DeployLanguageToolingFileRequest(
    int Version,
    string RequestId,
    string WorkspaceId,
    string ProviderId,
    string TargetId,
    string SourcePath,
    string DestinationId)
    : LanguageToolingHostRequest(Version, RequestId, WorkspaceId, ProviderId)
{
    public override string Operation => "deploy-file";
}

public sealed record LanguageToolingDiagnostic(
    string FilePath,
    string Severity,
    string Code,
    string Message,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn);

public sealed record LanguageToolingOperationResult(
    bool Succeeded,
    string Code,
    string SafeMessage,
    IReadOnlyList<LanguageToolingDiagnostic>? Diagnostics = null,
    IReadOnlyList<string>? ArtifactPaths = null,
    string? SessionId = null);

public sealed class LanguageToolingRequestException : Exception
{
    public LanguageToolingRequestException(string code, string message) : base(message) => Code = code;

    public string Code { get; }
}

public interface ILanguageToolingEvidenceSource : IAsyncDisposable
{
    string ProviderId { get; }

    IReadOnlyCollection<string> CapabilityIds { get; }

    ValueTask<IReadOnlyList<LanguageToolingCapabilityStatus>> InspectAsync(
        string workspaceRoot,
        CancellationToken cancellationToken);
}

public interface ILanguageToolingOperationHandler
{
    string ProviderId { get; }

    IReadOnlyCollection<string> Operations { get; }

    ValueTask<LanguageToolingOperationResult> ExecuteAsync(
        LanguageToolingHostRequest request,
        CancellationToken cancellationToken);
}
