namespace HermesDeveloperServices.LanguageTooling.Operations;

/// <summary>
/// Projects the typed language-tooling compile intent onto the pinned GCC provider. Renderer input
/// cannot select an executable, arguments, environment, or output path.
/// </summary>
public sealed class GccLanguageToolingOperationHandler : ILanguageToolingOperationHandler
{
    private readonly IGccCompilerAuthority _authority;
    private readonly string _workspaceRoot;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private bool _started;

    public GccLanguageToolingOperationHandler(GccToolchainProvider provider, string workspaceRoot)
        : this(new GccCompilerAuthority(provider), workspaceRoot)
    {
    }

    internal GccLanguageToolingOperationHandler(IGccCompilerAuthority authority, string workspaceRoot)
    {
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _workspaceRoot = TrustedToolchainPathPolicy.RequireRoot(workspaceRoot, "workspace");
    }

    public string ProviderId => LanguageToolingCatalog.Gcc;

    public IReadOnlyCollection<string> Operations { get; } = ["compile"];

    public async ValueTask<LanguageToolingOperationResult> ExecuteAsync(
        LanguageToolingHostRequest request,
        CancellationToken cancellationToken)
    {
        if (request is not CompileLanguageToolingRequest compile || compile.ProviderId != ProviderId)
            throw new LanguageToolingRequestException("operation-mismatch", "The GCC handler accepts only typed GCC compile requests.");

        var outputDirectory = TrustedToolchainPathPolicy.CreateOwnedTemporaryDirectory(
            _workspaceRoot, _workspaceRoot, ".hermes-gcc-release-", "gcc_release");
        var outputRelative = Path.GetRelativePath(_workspaceRoot, Path.Combine(
            outputDirectory,
            OperatingSystem.IsWindows() ? "artifact.exe" : "artifact"));
        try
        {
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            var result = await _authority.BuildAsync(new GccBuildRequest(
                _workspaceRoot,
                compile.TargetPath,
                outputRelative,
                compile.Mode == "release" ? BuildConfiguration.Release : BuildConfiguration.Debug), cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded) DeleteEmptyReleaseDirectory(_workspaceRoot, outputDirectory, ".hermes-gcc-release-");
            return new LanguageToolingOperationResult(
                result.Succeeded,
                result.FailureCode ?? "ok",
                result.Succeeded ? "The pinned GNU compiler produced a verified workspace artifact."
                    : result.FailureMessage ?? "The pinned GNU compiler did not complete successfully.",
                result.Diagnostics.Select(diagnostic => new LanguageToolingDiagnostic(
                    RelativeDiagnosticPath(diagnostic.FilePath),
                    diagnostic.Severity.ToString().ToLowerInvariant(),
                    diagnostic.Code,
                    diagnostic.Message,
                    diagnostic.Range.Start.Line,
                    diagnostic.Range.Start.Column,
                    diagnostic.Range.End.Line,
                    diagnostic.Range.End.Column)).ToArray(),
                result.Succeeded ? [outputRelative.Replace(Path.DirectorySeparatorChar, '/')] : []);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DeleteEmptyReleaseDirectory(_workspaceRoot, outputDirectory, ".hermes-gcc-release-");
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or TrustedToolchainValidationException)
        {
            DeleteEmptyReleaseDirectory(_workspaceRoot, outputDirectory, ".hermes-gcc-release-");
            return new(false, "gcc-authority-failed", "The pinned GNU compiler authority could not be established safely.");
        }
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_started) return;
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_started) return;
            await _authority.StartAsync(_workspaceRoot, cancellationToken).ConfigureAwait(false);
            _started = true;
        }
        finally { _startGate.Release(); }
    }

    private string RelativeDiagnosticPath(string path) =>
        TrustedToolchainPathPolicy.TryWorkspaceRelative(_workspaceRoot, path, out var relative)
            ? relative
            : string.Empty;

    internal static void DeleteEmptyReleaseDirectory(string workspaceRoot, string directory, string prefix)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
            var full = Path.GetFullPath(directory);
            var relative = Path.GetRelativePath(root, full);
            if (!relative.Equals("..", StringComparison.Ordinal)
                && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && Path.GetFileName(full).StartsWith(prefix, StringComparison.Ordinal)
                && Directory.Exists(full)
                && (File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0
                && !Directory.EnumerateFileSystemEntries(full).Any()) Directory.Delete(full);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
    }
}

internal interface IGccCompilerAuthority
{
    ValueTask StartAsync(string workspaceRoot, CancellationToken cancellationToken);

    Task<BuildResult> BuildAsync(GccBuildRequest request, CancellationToken cancellationToken);
}

internal sealed class GccCompilerAuthority(GccToolchainProvider provider) : IGccCompilerAuthority
{
    public ValueTask StartAsync(string workspaceRoot, CancellationToken cancellationToken) =>
        provider.StartAsync(new ToolchainStartContext(
            workspaceRoot,
            [new WorkspacePathMapping(workspaceRoot, workspaceRoot)],
            ToolchainExecutionKind.LocalSidecarProcess), cancellationToken);

    public Task<BuildResult> BuildAsync(GccBuildRequest request, CancellationToken cancellationToken) =>
        provider.BuildAsync(request, cancellationToken);
}
