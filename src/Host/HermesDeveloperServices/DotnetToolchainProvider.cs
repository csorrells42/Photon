namespace HermesDeveloperServices;

/// <summary>
/// First provider adapter: guarded dotnet builds today, with Roslyn language services and a provisioned
/// NetCoreDbg DAP sidecar represented as unavailable capabilities until separately integrated.
/// </summary>
public sealed class DotnetToolchainProvider : IToolchainProvider
{
    private bool _disposed;

    public ToolchainProviderDescriptor Descriptor { get; } = new(
        DeveloperServicesProtocol.ToolchainProviderVersion,
        ProviderId: "dotnet",
        DisplayName: ".NET SDK",
        ProviderVersion: "1.0",
        LanguageIds: new[] { "csharp" },
        ProjectKinds: new[] { "sln", "slnx", "csproj" },
        Build: new ToolchainBuildCapabilities(
            Supported: true,
            ProducesDiagnostics: true,
            SupportsCancellation: true,
            TargetKinds: new[] { "sln", "slnx", "csproj" }),
        Lsp: new ToolchainLspCapabilities(false, false, false, false, false, false, false),
        Dap: new ToolchainDapCapabilities(false, false, false, false, false),
        ExecutionKinds: new[] { ToolchainExecutionKind.LocalSidecarProcess });

    public ToolchainLifecycleState LifecycleState { get; private set; } = ToolchainLifecycleState.Created;

    public ToolchainAvailability Availability { get; private set; } = new(
        ToolchainAvailabilityState.Unknown,
        null,
        null,
        DateTimeOffset.MinValue);

    public ValueTask<ToolchainExecutableDiscoveryResult> DiscoverExecutablesAsync(
        ToolchainDiscoveryContext context,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (context.ExecutionKind != ToolchainExecutionKind.LocalSidecarProcess)
        {
            Availability = Unavailable("unsupported_execution_kind", "The .NET adapter requires a local sidecar.");
            return ValueTask.FromResult(new ToolchainExecutableDiscoveryResult(Availability, Array.Empty<ResolvedToolchainExecutable>()));
        }

        var executable = FindOnPath(OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        Availability = executable is null
            ? Unavailable("dotnet_not_found", "The dotnet executable is unavailable.")
            : new ToolchainAvailability(ToolchainAvailabilityState.Available, null, null, DateTimeOffset.UtcNow);
        var resolved = new ResolvedToolchainExecutable(
            "dotnet",
            executable,
            Version: null,
            Availability);
        return ValueTask.FromResult<ToolchainExecutableDiscoveryResult>(
            new(Availability, new[] { resolved }));
    }

    public ValueTask StartAsync(ToolchainStartContext context, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (context.ExecutionKind != ToolchainExecutionKind.LocalSidecarProcess)
        {
            throw new InvalidOperationException("The .NET adapter requires a local sidecar execution kind.");
        }

        if (!Directory.Exists(Path.GetFullPath(context.WorkspaceRoot)))
        {
            throw new InvalidOperationException("The toolchain workspace root does not exist.");
        }

        LifecycleState = ToolchainLifecycleState.Ready;
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        LifecycleState = ToolchainLifecycleState.Stopped;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        LifecycleState = ToolchainLifecycleState.Stopped;
        return ValueTask.CompletedTask;
    }

    private static string? FindOnPath(string executableName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.GetFullPath(Path.Combine(directory, executableName));
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
        }

        return null;
    }

    private static ToolchainAvailability Unavailable(string code, string safeMessage) =>
        new(ToolchainAvailabilityState.Unavailable, code, safeMessage, DateTimeOffset.UtcNow);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
