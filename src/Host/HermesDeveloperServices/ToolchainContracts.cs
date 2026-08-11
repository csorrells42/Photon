namespace HermesDeveloperServices;

public enum ToolchainExecutionKind
{
    LocalSidecarProcess,
    ExistingHermesContainerProcess,
}

public enum ToolchainDeploymentScope
{
    HermesWorkbenchInternalModule,
}

public enum ToolchainAvailabilityState
{
    Unknown,
    Checking,
    Available,
    Unavailable,
    Error,
}

public enum ToolchainLifecycleState
{
    Created,
    Starting,
    Ready,
    Stopping,
    Stopped,
    Faulted,
}

public sealed record ToolchainAvailability(
    ToolchainAvailabilityState State,
    string? Code,
    string? SafeMessage,
    DateTimeOffset CheckedAt);

public sealed record ToolchainBuildCapabilities(
    bool Supported,
    bool ProducesDiagnostics,
    bool SupportsCancellation,
    IReadOnlyList<string> TargetKinds);

public sealed record ToolchainLspCapabilities(
    bool Supported,
    bool SupportsDiagnostics,
    bool SupportsCompletion,
    bool SupportsHover,
    bool SupportsDefinition,
    bool SupportsReferences,
    bool SupportsRename,
    string? ProtocolVersion = null);

public sealed record ToolchainDapCapabilities(
    bool Supported,
    bool SupportsLaunch,
    bool SupportsAttach,
    bool SupportsBreakpoints,
    bool SupportsEvaluation,
    string? ProtocolVersion = null);

public sealed record ToolchainProviderDescriptor(
    int ContractVersion,
    string ProviderId,
    string DisplayName,
    string ProviderVersion,
    IReadOnlyList<string> LanguageIds,
    IReadOnlyList<string> ProjectKinds,
    ToolchainBuildCapabilities Build,
    ToolchainLspCapabilities Lsp,
    ToolchainDapCapabilities Dap,
    IReadOnlyList<ToolchainExecutionKind> ExecutionKinds,
    ToolchainDeploymentScope DeploymentScope = ToolchainDeploymentScope.HermesWorkbenchInternalModule);

public sealed record WorkspacePathMapping(string HostPath, string AdapterPath);

/// <summary>
/// Supplies the explicit host-to-adapter path boundary. Providers must not infer unrelated roots or
/// start sidecars outside the selected execution kind.
/// </summary>
public sealed record ToolchainStartContext(
    string WorkspaceRoot,
    IReadOnlyList<WorkspacePathMapping> PathMappings,
    ToolchainExecutionKind ExecutionKind);

public sealed record ToolchainDiscoveryContext(
    string WorkspaceRoot,
    ToolchainExecutionKind ExecutionKind);

public sealed record ResolvedToolchainExecutable(
    string LogicalName,
    string? ExecutablePath,
    string? Version,
    ToolchainAvailability Availability);

public sealed record ToolchainExecutableDiscoveryResult(
    ToolchainAvailability Availability,
    IReadOnlyList<ResolvedToolchainExecutable> Executables);

public sealed record ToolchainSelectionRequest(
    string LanguageId,
    string? ProjectKind = null,
    bool RequiresBuild = false,
    bool RequiresLsp = false,
    bool RequiresDap = false,
    string? ProviderId = null);

public enum ToolchainSelectionState
{
    Selected,
    NotFound,
    Ambiguous,
}

public sealed record ToolchainSelectionResult(
    ToolchainSelectionState State,
    IToolchainProvider? Provider,
    IReadOnlyList<ToolchainProviderDescriptor> Candidates);

/// <summary>
/// Provider-neutral seam for compiler, language-server, and debugger adapters. Implementations own
/// only their explicitly started sidecars and must honor cancellation without terminating unrelated
/// processes. Every provider is an internal module in the one complete Hermes Workbench installation;
/// it cannot define a separate edition, installer, developer-services host, or Docker container.
/// </summary>
public interface IToolchainProvider : IAsyncDisposable
{
    ToolchainProviderDescriptor Descriptor { get; }

    ToolchainLifecycleState LifecycleState { get; }

    ToolchainAvailability Availability { get; }

    ValueTask<ToolchainExecutableDiscoveryResult> DiscoverExecutablesAsync(
        ToolchainDiscoveryContext context,
        CancellationToken cancellationToken);

    ValueTask StartAsync(ToolchainStartContext context, CancellationToken cancellationToken);

    ValueTask StopAsync(CancellationToken cancellationToken);
}
