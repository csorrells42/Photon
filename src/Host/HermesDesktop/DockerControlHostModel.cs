namespace HermesDesktop;

internal enum DockerControlService
{
    Hermes,
    MemoryVector,
    Serena,
    ModelRunner,
}

internal enum DockerControlMutationKind
{
    StartStack,
    StopStack,
    StartService,
    StopService,
    RestartService,
    UnloadModel,
}

internal sealed record DockerControlPort(string Address, int HostPort, int ContainerPort, string Protocol);

internal sealed record DockerControlImage(
    string? ImageId,
    string? ApprovedDigest,
    string? OciRevision,
    string Verification);

internal sealed record DockerControlResources(
    double? CpuPercent,
    string? MemoryUsage,
    string? MemoryLimit,
    double? MemoryPercent,
    string? NetworkIo,
    string? BlockIo,
    int? Pids);

internal sealed record DockerControlServiceEvidence(
    DockerControlService Id,
    string State,
    string Health,
    string? ContainerId,
    string? Version,
    DockerControlImage? Image,
    IReadOnlyList<DockerControlPort> Ports,
    DockerControlResources? Resources,
    bool Manageable);

internal sealed record DockerControlModel(
    string Reference,
    string? ModelId,
    string? Size,
    string? Format,
    string? Parameters,
    bool Loaded,
    string? Backend,
    string? Mode);

internal sealed record DockerControlModelRunner(
    string State,
    string? Version,
    string? Endpoint,
    string? Kind,
    string? DiskUsage,
    bool UnloadAvailable,
    IReadOnlyList<DockerControlModel> Models,
    string Message);

internal sealed record DockerControlVolumeEvidence(
    string Role,
    string State,
    bool Persistent);

internal sealed record DockerControlWorkflowEvidence(
    string Kind,
    string State,
    DateTimeOffset CompletedAtUtc,
    string Summary);

internal sealed record DockerControlHostSnapshot(
    DateTimeOffset ObservedAtUtc,
    string EngineState,
    string? EngineVersion,
    string ComposeState,
    string? DefinitionFingerprint,
    string? UpstreamRevision,
    string RuntimeProtocol,
    IReadOnlyList<DockerControlServiceEvidence> Services,
    IReadOnlyList<DockerControlVolumeEvidence> Volumes,
    DockerControlModelRunner? ModelRunner,
    DockerControlWorkflowEvidence? LastWorkflow);

internal sealed record DockerControlLogLine(DateTimeOffset? TimestampUtc, string Stream, string Text);

internal sealed record DockerControlLogs(IReadOnlyList<DockerControlLogLine> Entries, bool Truncated);

internal sealed record DockerControlMutation(
    DockerControlMutationKind Kind,
    DockerControlService? Service,
    IReadOnlyList<DockerControlService> Targets,
    string? Model = null);

internal sealed record DockerControlMutationOutcome(bool Succeeded, string Message);

internal interface IDockerControlRunner : IAsyncDisposable
{
    Task<DockerControlHostSnapshot> CaptureAsync(CancellationToken cancellationToken);
    Task<DockerControlLogs> ReadLogsAsync(DockerControlService service, int maximumLines, CancellationToken cancellationToken);
    Task<DockerControlMutationOutcome> ExecuteAsync(DockerControlMutation mutation, CancellationToken cancellationToken);
}

internal sealed class DockerControlUnavailableException(string code, string safeMessage) : Exception(safeMessage)
{
    internal string Code { get; } = code;
}
