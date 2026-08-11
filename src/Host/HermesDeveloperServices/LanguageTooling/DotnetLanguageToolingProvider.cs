namespace HermesDeveloperServices.LanguageTooling;

/// <summary>
/// Projects the existing guarded .NET build and test runners into the provider-neutral language-
/// tooling contract. The caller supplies only a workspace-relative target and an optional bounded
/// test filter; executable, verbs, environment, and working directory remain host-owned.
/// </summary>
public static class DotnetLanguageToolingProvider
{
    public static DotnetLanguageToolingComponents Create(
        DotnetToolchainProvider provider,
        string workspaceRoot)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return new(
            new DotnetSdkEvidenceSource(provider),
            new DotnetLanguageToolingOperationHandler(workspaceRoot));
    }
}

public sealed record DotnetLanguageToolingComponents(
    ILanguageToolingEvidenceSource EvidenceSource,
    ILanguageToolingOperationHandler OperationHandler);

public sealed class DotnetSdkEvidenceSource(DotnetToolchainProvider provider) : ILanguageToolingEvidenceSource
{
    private readonly DotnetToolchainProvider _provider = provider ?? throw new ArgumentNullException(nameof(provider));

    public string ProviderId => LanguageToolingCatalog.Dotnet;

    public IReadOnlyCollection<string> CapabilityIds { get; } = ["dotnet.compiler", "dotnet.tests"];

    public async ValueTask<IReadOnlyList<LanguageToolingCapabilityStatus>> InspectAsync(
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        var discovery = await _provider.DiscoverExecutablesAsync(
            new ToolchainDiscoveryContext(workspaceRoot, ToolchainExecutionKind.LocalSidecarProcess),
            cancellationToken).ConfigureAwait(false);
        var executable = discovery.Executables.FirstOrDefault(item =>
            item.LogicalName.Equals("dotnet", StringComparison.Ordinal)
            && item.Availability.State == ToolchainAvailabilityState.Available
            && item.ExecutablePath is not null
            && Path.IsPathFullyQualified(item.ExecutablePath)
            && File.Exists(item.ExecutablePath));
        if (discovery.Availability.State != ToolchainAvailabilityState.Available || executable is null)
        {
            var code = KnownPinnedToolchainEvidenceSource.SafeCode(
                discovery.Availability.Code,
                "dotnet-sdk-unavailable");
            var message = KnownPinnedToolchainEvidenceSource.SafeMessage(
                discovery.Availability.SafeMessage,
                "The trusted host could not locate the .NET SDK.");
            return CapabilityIds.Select(id => new LanguageToolingCapabilityStatus(
                id,
                LanguageToolingCapabilityState.Unavailable,
                code,
                message)).ToArray();
        }

        return CapabilityIds.Select(id => new LanguageToolingCapabilityStatus(
            id,
            LanguageToolingCapabilityState.Available,
            "host-discovered-dotnet-sdk",
            id == "dotnet.compiler"
                ? "The trusted host verified the fixed .NET build operation."
                : "The trusted host verified the fixed .NET test operation.",
            executable.Version)).ToArray();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class DotnetLanguageToolingOperationHandler : ILanguageToolingOperationHandler
{
    private readonly string _workspaceRoot;
    private readonly DotnetBuildRunner _runner;

    public DotnetLanguageToolingOperationHandler(string workspaceRoot, DotnetBuildRunner? runner = null)
    {
        _workspaceRoot = TrustedToolchainPathPolicy.RequireRoot(workspaceRoot, "workspace");
        _runner = runner ?? new DotnetBuildRunner();
    }

    public string ProviderId => LanguageToolingCatalog.Dotnet;

    public IReadOnlyCollection<string> Operations { get; } = ["compile", "run-tests"];

    public async ValueTask<LanguageToolingOperationResult> ExecuteAsync(
        LanguageToolingHostRequest request,
        CancellationToken cancellationToken)
    {
        _ = LanguageToolingRequestPolicy.Validate(request);
        BuildResult result;
        switch (request)
        {
            case CompileLanguageToolingRequest compile when compile.ProviderId == ProviderId:
                result = await _runner.BuildAsync(new BuildRequest(
                    _workspaceRoot,
                    compile.TargetPath,
                    compile.Mode == "release" ? BuildConfiguration.Release : BuildConfiguration.Debug),
                    cancellationToken).ConfigureAwait(false);
                break;
            case RunLanguageToolingTestsRequest tests when tests.ProviderId == ProviderId:
                result = await _runner.TestAsync(new DotnetTestRequest(
                    _workspaceRoot,
                    tests.TargetPath,
                    BuildConfiguration.Debug,
                    tests.Selection), cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new LanguageToolingRequestException(
                    "operation-mismatch",
                    "The .NET handler accepts only typed compile and test requests.");
        }

        var operation = request is RunLanguageToolingTestsRequest ? "test" : "build";
        return new LanguageToolingOperationResult(
            result.Succeeded,
            result.FailureCode ?? "ok",
            result.Succeeded
                ? $"The .NET {operation} operation completed successfully."
                : result.FailureMessage ?? $"The .NET {operation} operation did not complete successfully.",
            result.Diagnostics.Select(diagnostic => new LanguageToolingDiagnostic(
                RelativeDiagnosticPath(diagnostic.FilePath),
                diagnostic.Severity.ToString().ToLowerInvariant(),
                diagnostic.Code,
                diagnostic.Message,
                diagnostic.Range.Start.Line,
                diagnostic.Range.Start.Column,
                diagnostic.Range.End.Line,
                diagnostic.Range.End.Column)).ToArray());
    }

    private string RelativeDiagnosticPath(string path) =>
        TrustedToolchainPathPolicy.TryWorkspaceRelative(_workspaceRoot, path, out var relative)
            ? relative
            : string.Empty;
}
