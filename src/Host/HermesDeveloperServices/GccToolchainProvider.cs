using System.Security.Cryptography;

namespace HermesDeveloperServices;

public sealed record GccToolchainProviderOptions
{
    public required string WorkbenchRoot { get; init; }
    public string ToolchainRelativePath { get; init; } = "toolchains/gcc";
    public string ReceiptFileName { get; init; } = "hermes-toolchain-receipt.json";
    public TimeSpan DiscoveryTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public GccBuildRunnerOptions Build { get; init; } = new();
}

public sealed record GccBuildRunnerOptions
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public int MaximumRetainedCharacters { get; init; } = 256 * 1024;
    public int MaximumDiagnostics { get; init; } = 2_000;
    public int MaximumSources { get; init; } = 512;
    public int MaximumDirectories { get; init; } = 1_024;
    public int MaximumDepth { get; init; } = 12;
    public long MaximumSourceBytes { get; init; } = 4 * 1024 * 1024;
    public long MaximumAggregateSourceBytes { get; init; } = 64 * 1024 * 1024;
    public long MaximumArtifactBytes { get; init; } = 512L * 1024 * 1024;
}

/// <summary>
/// TargetPath is one literal workspace-relative C/C++ source or source directory. OutputPath is a
/// literal workspace-relative file whose parent already exists. No flags or environment are accepted.
/// </summary>
public sealed record GccBuildRequest(
    string WorkspaceRoot,
    string TargetPath,
    string OutputPath,
    BuildConfiguration Configuration = BuildConfiguration.Debug);

public sealed record GccExecutableIdentity(
    string LogicalName,
    string Path,
    string Sha256,
    string Version,
    string DumpMachine);

public sealed record GccToolchainIdentity(
    string Root,
    string ReceiptPath,
    GccExecutableIdentity Gcc,
    GccExecutableIdentity Gxx,
    string DumpMachine);

/// <summary>
/// Trusted-host GNU C/C++ provider. Every probe and build uses a fresh authenticated package stage;
/// build output is published only after a new bounded normal artifact has been verified.
/// </summary>
public sealed class GccToolchainProvider : IToolchainProvider
{
    private static readonly string[] RequiredExecutables = ["gcc", "g++"];
    private readonly GccToolchainProviderOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly object _activeGate = new();
    private readonly HashSet<Task> _activeOperations = [];
    private CancellationTokenSource? _lifetime;
    private string? _workspaceRoot;
    private bool _disposed;

    public GccToolchainProvider(GccToolchainProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        _options = options;
    }

    public ToolchainProviderDescriptor Descriptor { get; } = new(
        DeveloperServicesProtocol.ToolchainProviderVersion,
        ProviderId: "gcc",
        DisplayName: "Pinned GNU C/C++",
        ProviderVersion: "1.0",
        LanguageIds: ["c", "cpp"],
        ProjectKinds: ["c-source", "cpp-source", "native-source-tree"],
        Build: new ToolchainBuildCapabilities(
            Supported: true,
            ProducesDiagnostics: true,
            SupportsCancellation: true,
            TargetKinds: ["c-source", "cpp-source", "native-source-tree"]),
        Lsp: new ToolchainLspCapabilities(false, false, false, false, false, false, false),
        Dap: new ToolchainDapCapabilities(false, false, false, false, false),
        ExecutionKinds: [ToolchainExecutionKind.LocalSidecarProcess]);

    public ToolchainLifecycleState LifecycleState { get; private set; } = ToolchainLifecycleState.Created;

    public ToolchainAvailability Availability { get; private set; } = new(
        ToolchainAvailabilityState.Unknown,
        null,
        null,
        DateTimeOffset.MinValue);

    public GccToolchainIdentity? Identity { get; private set; }

    public async ValueTask<ToolchainExecutableDiscoveryResult> DiscoverExecutablesAsync(
        ToolchainDiscoveryContext context,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(context);
        var operation = DiscoverCoreAsync(context, cancellationToken);
        Track(operation);
        try { return await operation.ConfigureAwait(false); }
        finally { Untrack(operation); }
    }

    private async Task<ToolchainExecutableDiscoveryResult> DiscoverCoreAsync(
        ToolchainDiscoveryContext context,
        CancellationToken cancellationToken)
    {
        if (context.ExecutionKind != ToolchainExecutionKind.LocalSidecarProcess)
        {
            Availability = Unavailable("unsupported_execution_kind", "The GNU toolchain requires a local trusted-host process.");
            return new(Availability, []);
        }
        Availability = new(ToolchainAvailabilityState.Checking, null, null, DateTimeOffset.UtcNow);
        try
        {
            var identity = await DiscoverIdentityAsync(cancellationToken).ConfigureAwait(false);
            lock (_stateGate) Identity = identity;
            Availability = new(ToolchainAvailabilityState.Available, null, null, DateTimeOffset.UtcNow);
            return new(
                Availability,
                [
                    new("gcc", identity.Gcc.Path, $"{identity.Gcc.Version} [{identity.Gcc.DumpMachine}]", Availability),
                    new("g++", identity.Gxx.Path, $"{identity.Gxx.Version} [{identity.Gxx.DumpMachine}]", Availability),
                ]);
        }
        catch (OperationCanceledException) { throw; }
        catch (TrustedToolchainValidationException exception)
        {
            lock (_stateGate) Identity = null;
            Availability = Unavailable(exception.Code, exception.Message);
            return new(Availability, []);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            lock (_stateGate) Identity = null;
            Availability = new(ToolchainAvailabilityState.Error, "toolchain_read_failed", "The pinned GNU toolchain could not be validated.", DateTimeOffset.UtcNow);
            return new(Availability, []);
        }
    }

    public async ValueTask StartAsync(ToolchainStartContext context, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(context);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (context.ExecutionKind != ToolchainExecutionKind.LocalSidecarProcess)
                throw new InvalidOperationException("The GNU toolchain requires a local trusted-host process.");
            LifecycleState = ToolchainLifecycleState.Starting;
            var workspaceRoot = TrustedToolchainPathPolicy.RequireRoot(context.WorkspaceRoot, "workspace");
            var discovery = await DiscoverExecutablesAsync(
                new ToolchainDiscoveryContext(workspaceRoot, context.ExecutionKind),
                cancellationToken).ConfigureAwait(false);
            if (discovery.Availability.State != ToolchainAvailabilityState.Available || Identity is null)
            {
                LifecycleState = ToolchainLifecycleState.Faulted;
                throw new InvalidOperationException(discovery.Availability.SafeMessage ?? "The GNU toolchain is unavailable.");
            }
            lock (_stateGate)
            {
                _lifetime?.Dispose();
                _lifetime = new CancellationTokenSource();
                _workspaceRoot = workspaceRoot;
                LifecycleState = ToolchainLifecycleState.Ready;
            }
        }
        catch
        {
            if (LifecycleState == ToolchainLifecycleState.Starting) LifecycleState = ToolchainLifecycleState.Faulted;
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task<BuildResult> BuildAsync(GccBuildRequest request, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        var operation = BuildCoreAsync(request, cancellationToken);
        Track(operation);
        try { return await operation.ConfigureAwait(false); }
        finally { Untrack(operation); }
    }

    private async Task<BuildResult> BuildCoreAsync(GccBuildRequest request, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        GccToolchainIdentity identity;
        string startedWorkspace;
        CancellationToken lifetimeToken;
        lock (_stateGate)
        {
            if (LifecycleState != ToolchainLifecycleState.Ready || Identity is null || _workspaceRoot is null || _lifetime is null)
                return Failure(startedAt, "provider_not_ready", "The GNU toolchain provider is not ready.");
            identity = Identity;
            startedWorkspace = _workspaceRoot;
            lifetimeToken = _lifetime.Token;
        }

        string workspaceRoot;
        string target;
        string output;
        IReadOnlyList<string> sources;
        try
        {
            workspaceRoot = TrustedToolchainPathPolicy.RequireRoot(request.WorkspaceRoot, "workspace");
            if (!PathsEqual(workspaceRoot, startedWorkspace))
                return Failure(startedAt, "workspace_mismatch", "The build workspace differs from the started provider workspace.");
            target = TrustedToolchainPathPolicy.ResolveExistingTarget(workspaceRoot, request.TargetPath, "target");
            output = TrustedToolchainPathPolicy.ResolveOutputFile(workspaceRoot, request.OutputPath, "output");
            if (OperatingSystem.IsWindows() && !Path.GetExtension(output).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                return Failure(startedAt, "output_extension_unsupported", "Windows GNU builds must produce a workspace-relative .exe output.");
            sources = TrustedToolchainPathPolicy.EnumerateSources(workspaceRoot, target, SourceBounds());
            if (sources.Count == 0) return Failure(startedAt, "sources_not_found", "No bounded C or C++ sources were found.");
            if (sources.Any(source => PathsEqual(source, output)))
                return Failure(startedAt, "output_overlaps_source", "The native output may not overwrite a source file.");
        }
        catch (TrustedToolchainValidationException exception)
        {
            return Failure(startedAt, exception.Code, exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failure(startedAt, "path_validation_failed", "The native build paths could not be validated.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeToken);
        PinnedToolchainPackageLease lease;
        try
        {
            lease = await PinnedToolchainReceiptLoader.StageAsync(
                _options.WorkbenchRoot,
                _options.ToolchainRelativePath,
                _options.ReceiptFileName,
                "gcc",
                RequiredExecutables,
                linked.Token).ConfigureAwait(false);
            RequireUnchangedIdentity(identity, lease.Source);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(startedAt);
        }
        catch (TrustedToolchainValidationException exception)
        {
            return Failure(startedAt, exception.Code, exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failure(startedAt, "toolchain_stage_failed", "The pinned GNU package could not be staged safely.");
        }

        await using (lease.ConfigureAwait(false))
        {
            var usesCpp = sources.Any(source => !Path.GetExtension(source).Equals(".c", StringComparison.OrdinalIgnoreCase));
            var stagedExecutable = lease.Staged.Executables[usesCpp ? "g++" : "gcc"].Path;
            var outputParent = Path.GetDirectoryName(output)!;
            string temporaryDirectory;
            try
            {
                temporaryDirectory = TrustedToolchainPathPolicy.CreateOwnedTemporaryDirectory(
                    workspaceRoot,
                    outputParent,
                    ".hermes-gcc-output-",
                    "build_output");
            }
            catch (TrustedToolchainValidationException exception)
            {
                return Failure(startedAt, exception.Code, exception.Message);
            }

            var temporaryOutput = Path.Combine(
                temporaryDirectory,
                OperatingSystem.IsWindows() ? "artifact.exe" : "artifact");
            try
            {
                var arguments = CreateArguments(sources, temporaryOutput, request.Configuration, usesCpp);
                var process = await OwnedToolchainProcessRunner.RunAsync(
                    stagedExecutable,
                    workspaceRoot,
                    arguments,
                    new OwnedToolchainProcessBounds
                    {
                        Timeout = _options.Build.Timeout,
                        CleanupTimeout = _options.Build.StopTimeout,
                        MaximumRetainedCharacters = _options.Build.MaximumRetainedCharacters,
                    },
                    linked.Token).ConfigureAwait(false);
                var diagnostics = ParseDiagnostics(process, workspaceRoot);
                var processFailure = ClassifyProcessFailure(process);
                if (processFailure.Code is not null)
                {
                    return CreateResult(startedAt, process, diagnostics, processFailure.Code, processFailure.Message);
                }

                try
                {
                    await PublishFreshArtifactAsync(
                        workspaceRoot,
                        temporaryOutput,
                        output,
                        linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return CreateResult(startedAt, process, diagnostics, "build_cancelled", "The native build was cancelled.", wasCancelled: true);
                }
                catch (TrustedToolchainValidationException exception)
                {
                    return CreateResult(startedAt, process, diagnostics, exception.Code, exception.Message);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return CreateResult(startedAt, process, diagnostics, "artifact_publish_failed", "The verified native artifact could not be published atomically.");
                }
                return CreateResult(startedAt, process, diagnostics, null, null);
            }
            finally
            {
                DeleteBuildTemporaryDirectory(workspaceRoot, temporaryDirectory, temporaryOutput);
            }
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CancellationTokenSource? lifetime;
            lock (_stateGate)
            {
                LifecycleState = ToolchainLifecycleState.Stopping;
                lifetime = _lifetime;
                lifetime?.Cancel();
            }
            var stopped = await WaitForActiveOperationsAsync(_options.Build.StopTimeout, cancellationToken).ConfigureAwait(false);
            lock (_stateGate)
            {
                lifetime?.Dispose();
                if (ReferenceEquals(_lifetime, lifetime)) _lifetime = null;
                _workspaceRoot = null;
                LifecycleState = stopped ? ToolchainLifecycleState.Stopped : ToolchainLifecycleState.Faulted;
            }
            if (!stopped) throw new TimeoutException("The owned GNU process family did not stop within the bounded wait.");
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? lifetime;
        lock (_stateGate)
        {
            if (_disposed) return;
            _disposed = true;
            lifetime = _lifetime;
            lifetime?.Cancel();
            LifecycleState = ToolchainLifecycleState.Stopping;
        }
        await WaitForActiveOperationsAsync(_options.Build.StopTimeout, CancellationToken.None).ConfigureAwait(false);
        lock (_stateGate)
        {
            lifetime?.Dispose();
            _lifetime = null;
            _workspaceRoot = null;
            LifecycleState = ToolchainLifecycleState.Stopped;
        }
        _gate.Dispose();
    }

    private async Task<GccToolchainIdentity> DiscoverIdentityAsync(CancellationToken cancellationToken)
    {
        await using var lease = await PinnedToolchainReceiptLoader.StageAsync(
            _options.WorkbenchRoot,
            _options.ToolchainRelativePath,
            _options.ReceiptFileName,
            "gcc",
            RequiredExecutables,
            cancellationToken).ConfigureAwait(false);
        var stagedGcc = lease.Staged.Executables["gcc"];
        var stagedGxx = lease.Staged.Executables["g++"];
        RequireExpectedName(stagedGcc.Path, "gcc");
        RequireExpectedName(stagedGxx.Path, "g++");
        var gccProbe = await ProbeAsync(stagedGcc, "gcc", lease.Staged.Root, cancellationToken).ConfigureAwait(false);
        var gxxProbe = await ProbeAsync(stagedGxx, "g++", lease.Staged.Root, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(gccProbe.DumpMachine, gxxProbe.DumpMachine, StringComparison.Ordinal))
            throw new TrustedToolchainValidationException("dumpmachine_mismatch", "GCC and G++ report different target machines.");
        var sourceGcc = lease.Source.Executables["gcc"];
        var sourceGxx = lease.Source.Executables["g++"];
        return new(
            lease.Source.Root,
            lease.Source.ReceiptPath,
            gccProbe with { Path = sourceGcc.Path, Sha256 = sourceGcc.Sha256 },
            gxxProbe with { Path = sourceGxx.Path, Sha256 = sourceGxx.Sha256 },
            gccProbe.DumpMachine);
    }

    private async Task<GccExecutableIdentity> ProbeAsync(
        ValidatedPinnedExecutable executable,
        string expectedName,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var bounds = new OwnedToolchainProcessBounds
        {
            Timeout = _options.DiscoveryTimeout,
            CleanupTimeout = _options.Build.StopTimeout,
            MaximumRetainedCharacters = 16 * 1024,
        };
        var versionResult = await OwnedToolchainProcessRunner.RunAsync(
            executable.Path, workingDirectory, ["--version"], bounds, cancellationToken).ConfigureAwait(false);
        var machineResult = await OwnedToolchainProcessRunner.RunAsync(
            executable.Path, workingDirectory, ["-dumpmachine"], bounds, cancellationToken).ConfigureAwait(false);
        if (versionResult.ExitCode != 0
            || machineResult.ExitCode != 0
            || versionResult.FailureCode is not null
            || machineResult.FailureCode is not null)
        {
            throw new TrustedToolchainValidationException("compiler_probe_failed", "A pinned GNU compiler did not answer its identity probes.");
        }
        var version = FirstLine(versionResult.StandardOutput, 512);
        var machine = FirstLine(machineResult.StandardOutput, 128);
        var versionMatches = expectedName == "gcc"
            ? version.Contains("gcc", StringComparison.OrdinalIgnoreCase)
            : version.Contains("g++", StringComparison.OrdinalIgnoreCase) || version.Contains("c++", StringComparison.OrdinalIgnoreCase);
        if (!versionMatches || machine.Length == 0 || machine.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '+')))
            throw new TrustedToolchainValidationException("compiler_identity_invalid", "A pinned executable did not identify as the expected GNU compiler.");
        return new(expectedName, executable.Path, executable.Sha256, version, machine);
    }

    private async Task PublishFreshArtifactAsync(
        string workspaceRoot,
        string temporaryOutput,
        string destination,
        CancellationToken cancellationToken)
    {
        var length = TrustedToolchainPathPolicy.RequireNormalFile(
            workspaceRoot,
            temporaryOutput,
            "artifact",
            minimumBytes: 1,
            maximumBytes: _options.Build.MaximumArtifactBytes);
        var expectedHash = await HashNormalFileAsync(temporaryOutput, length, cancellationToken).ConfigureAwait(false);
        var revalidatedDestination = TrustedToolchainPathPolicy.ResolveOutputFile(
            workspaceRoot,
            Path.GetRelativePath(workspaceRoot, destination),
            "output");
        File.Move(temporaryOutput, revalidatedDestination, overwrite: true);
        var publishedLength = TrustedToolchainPathPolicy.RequireNormalFile(
            workspaceRoot,
            revalidatedDestination,
            "published_artifact",
            minimumBytes: 1,
            maximumBytes: _options.Build.MaximumArtifactBytes);
        if (publishedLength != length)
            throw new TrustedToolchainValidationException("artifact_publish_integrity", "The published artifact length changed.");
        var publishedHash = await HashNormalFileAsync(revalidatedDestination, publishedLength, cancellationToken).ConfigureAwait(false);
        if (!HashesEqual(expectedHash, publishedHash))
            throw new TrustedToolchainValidationException("artifact_publish_integrity", "The published artifact identity changed.");
    }

    private static async Task<string> HashNormalFileAsync(string path, long expectedLength, CancellationToken cancellationToken)
    {
        const int bufferSize = 64 * 1024;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[bufferSize];
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (total > expectedLength)
                throw new TrustedToolchainValidationException("artifact_size_changed", "The native artifact exceeded its validated length.");
            hash.AppendData(buffer, 0, read);
        }
        if (total != expectedLength)
            throw new TrustedToolchainValidationException("artifact_size_changed", "The native artifact changed while being validated.");
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private IReadOnlyList<BuildDiagnostic> ParseDiagnostics(OwnedToolchainProcessResult process, string workspaceRoot)
    {
        var diagnostics = new List<BuildDiagnostic>();
        var seen = new HashSet<BuildDiagnostic>();
        foreach (var line in EnumerateLines(process.StandardError).Concat(EnumerateLines(process.StandardOutput)))
        {
            if (diagnostics.Count >= _options.Build.MaximumDiagnostics) break;
            if (GccDiagnosticParser.TryParse(line, workspaceRoot, out var diagnostic)
                && diagnostic is not null
                && seen.Add(diagnostic))
            {
                diagnostics.Add(diagnostic);
            }
        }
        return diagnostics;
    }

    private static IReadOnlyList<string> CreateArguments(
        IReadOnlyList<string> sources,
        string output,
        BuildConfiguration configuration,
        bool usesCpp)
    {
        var arguments = new List<string>
        {
            usesCpp ? "-std=c++20" : "-std=c17",
            "-Wall", "-Wextra", "-Wpedantic", "-fdiagnostics-color=never", "-fno-diagnostics-show-caret",
        };
        arguments.Add(configuration == BuildConfiguration.Debug ? "-O0" : "-O2");
        arguments.Add(configuration == BuildConfiguration.Debug ? "-g3" : "-g");
        arguments.AddRange(sources);
        arguments.Add("-o");
        arguments.Add(output);
        return arguments;
    }

    private TrustedSourceEnumerationBounds SourceBounds() => new()
    {
        MaximumSources = _options.Build.MaximumSources,
        MaximumDirectories = _options.Build.MaximumDirectories,
        MaximumDepth = _options.Build.MaximumDepth,
        MaximumSourceBytes = _options.Build.MaximumSourceBytes,
        MaximumAggregateSourceBytes = _options.Build.MaximumAggregateSourceBytes,
    };

    private static (string? Code, string? Message) ClassifyProcessFailure(OwnedToolchainProcessResult process)
    {
        if (process.WasCancelled) return ("build_cancelled", "The native build was cancelled.");
        if (process.TimedOut) return ("build_timeout", "The native build exceeded its time limit.");
        if (process.FailureCode is "process_cleanup_timeout") return ("build_cleanup_timeout", "The owned compiler process family did not stop cleanly.");
        if (process.FailureCode is "process_ownership_failed") return ("build_process_ownership", "The compiler process family could not be placed under host ownership.");
        if (process.FailureCode is not null || process.ExitCode != 0) return ("build_failed", "The GNU compiler reported a build failure.");
        return (null, null);
    }

    private static BuildResult CreateResult(
        DateTimeOffset startedAt,
        OwnedToolchainProcessResult process,
        IReadOnlyList<BuildDiagnostic> diagnostics,
        string? failureCode,
        string? failureMessage,
        bool? wasCancelled = null) => new(
            Succeeded: failureCode is null,
            ExitCode: process.ExitCode,
            Diagnostics: BuildResult.Freeze(diagnostics),
            Output: new BuildOutput(process.StandardOutput, process.StandardError, process.OutputTruncated, process.DroppedCharacters),
            WasCancelled: wasCancelled ?? process.WasCancelled,
            FailureCode: failureCode,
            FailureMessage: failureMessage,
            StartedAt: startedAt,
            CompletedAt: DateTimeOffset.UtcNow);

    private static void RequireUnchangedIdentity(GccToolchainIdentity identity, ValidatedPinnedToolchain source)
    {
        if (!HashesEqual(identity.Gcc.Sha256, source.Executables["gcc"].Sha256)
            || !HashesEqual(identity.Gxx.Sha256, source.Executables["g++"].Sha256))
        {
            throw new TrustedToolchainValidationException("toolchain_identity_changed", "The GNU toolchain identity changed after discovery.");
        }
    }

    private void Track(Task operation)
    {
        lock (_activeGate) _activeOperations.Add(operation);
    }

    private void Untrack(Task operation)
    {
        lock (_activeGate) _activeOperations.Remove(operation);
    }

    private async Task<bool> WaitForActiveOperationsAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        Task[] active;
        lock (_activeGate) active = _activeOperations.ToArray();
        if (active.Length == 0) return true;
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await Task.WhenAll(active).WaitAsync(linked.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static void DeleteBuildTemporaryDirectory(string workspaceRoot, string directory, string artifact)
    {
        try
        {
            var relative = Path.GetRelativePath(workspaceRoot, directory);
            if (!Path.GetFileName(directory).StartsWith(".hermes-gcc-output-", StringComparison.Ordinal)
                || relative.StartsWith("..", StringComparison.Ordinal)
                || !Directory.Exists(directory)
                || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                return;
            }
            if (File.Exists(artifact) && (File.GetAttributes(artifact) & FileAttributes.ReparsePoint) == 0)
                File.Delete(artifact);
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static IEnumerable<string> EnumerateLines(string text)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line) yield return line;
    }

    private static string FirstLine(string text, int maximumCharacters)
    {
        var line = EnumerateLines(text).FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim() ?? string.Empty;
        return line.Length <= maximumCharacters ? line : line[..maximumCharacters];
    }

    private static void RequireExpectedName(string path, string expected)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (!name.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new TrustedToolchainValidationException("compiler_name_mismatch", "The receipt executable name does not match its compiler identity.");
    }

    private static bool HashesEqual(string left, string right)
    {
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right)); }
        catch (FormatException) { return false; }
    }

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static ToolchainAvailability Unavailable(string code, string message) =>
        new(ToolchainAvailabilityState.Unavailable, code, message, DateTimeOffset.UtcNow);

    private static BuildResult Failure(DateTimeOffset startedAt, string code, string message) => new(
        false, null, [], new BuildOutput(string.Empty, string.Empty, false, 0), false,
        code, message, startedAt, DateTimeOffset.UtcNow);

    private static BuildResult Cancelled(DateTimeOffset startedAt) => new(
        false, null, [], new BuildOutput(string.Empty, string.Empty, false, 0), true,
        "build_cancelled", "The native build was cancelled.", startedAt, DateTimeOffset.UtcNow);

    private static void ValidateOptions(GccToolchainProviderOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.WorkbenchRoot)) throw new ArgumentException("A Workbench root is required.", nameof(options));
        if (options.DiscoveryTimeout <= TimeSpan.Zero || options.DiscoveryTimeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(options.DiscoveryTimeout));
        var build = options.Build ?? throw new ArgumentException("Build bounds are required.", nameof(options));
        if (build.Timeout <= TimeSpan.Zero || build.Timeout > TimeSpan.FromMinutes(30)) throw new ArgumentOutOfRangeException(nameof(build.Timeout));
        if (build.StopTimeout <= TimeSpan.Zero || build.StopTimeout > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(build.StopTimeout));
        if (build.MaximumRetainedCharacters is < 1 or > 8 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(build.MaximumRetainedCharacters));
        if (build.MaximumDiagnostics is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(build.MaximumDiagnostics));
        if (build.MaximumArtifactBytes is < 1 or > 2L * 1024 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(build.MaximumArtifactBytes));
        if (build.MaximumSources is < 1 or > 10_000
            || build.MaximumDirectories is < 1 or > 20_000
            || build.MaximumDepth is < 0 or > 64
            || build.MaximumSourceBytes is < 1 or > 64 * 1024 * 1024
            || build.MaximumAggregateSourceBytes is < 1 or > 1024L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(options.Build));
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
