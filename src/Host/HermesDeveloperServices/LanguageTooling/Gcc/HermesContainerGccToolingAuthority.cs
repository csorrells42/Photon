using System.Text.Json;
using HermesDeveloperServices.LanguageTooling.Operations;

namespace HermesDeveloperServices.LanguageTooling.Gcc;

public sealed record HermesContainerGccToolingOptions
{
    public required string DockerExecutablePath { get; init; }
    public required string ExpectedImageId { get; init; }
    public required string WorkspaceRoot { get; init; }
    public string ContainerName { get; init; } = "hermes";
    public string ContainerWorkspaceRoot { get; init; } = "/workspace";
    public string GccExecutablePath { get; init; } = "/usr/bin/gcc";
    public string GxxExecutablePath { get; init; } = "/usr/bin/g++";
    public TimeSpan InspectionTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan CleanupTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public int MaximumRetainedCharacters { get; init; } = 256 * 1024;
    public int MaximumDiagnostics { get; init; } = 1_000;
    public int MaximumSources { get; init; } = 128;
    public int MaximumDirectories { get; init; } = 512;
    public int MaximumDepth { get; init; } = 12;
    public long MaximumSourceBytes { get; init; } = 4 * 1024 * 1024;
    public long MaximumAggregateSourceBytes { get; init; } = 32 * 1024 * 1024;
    public long MaximumArtifactBytes { get; init; } = 128 * 1024 * 1024;
}

/// <summary>
/// Binds the Workbench GNU C/C++ capability to the launcher-owned Hermes container. Renderer input
/// can select only a validated workspace-relative source and debug/release mode; the container,
/// image, executables, arguments, environment, workspace mount, and output name remain host-owned.
/// </summary>
public sealed class HermesContainerGccToolingAuthority : ILanguageToolingEvidenceSource, IGccCompilerAuthority
{
    private readonly HermesContainerGccToolingOptions _options;
    private readonly IGccContainerProcessRunner _runner;
    private readonly string _workspaceRoot;
    private readonly string _expectedImageId;
    private readonly object _stateGate = new();
    private GccContainerRuntimeReceipt? _startedReceipt;
    private bool _disposed;

    public HermesContainerGccToolingAuthority(HermesContainerGccToolingOptions options)
        : this(options, CreateOwnedRunner(options), validateDockerExecutable: true) { }

    internal HermesContainerGccToolingAuthority(HermesContainerGccToolingOptions options, IGccContainerProcessRunner runner)
        : this(options, runner, validateDockerExecutable: false) { }

    private HermesContainerGccToolingAuthority(
        HermesContainerGccToolingOptions options,
        IGccContainerProcessRunner runner,
        bool validateDockerExecutable)
    {
        ArgumentNullException.ThrowIfNull(options);
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        ValidateOptions(options, validateDockerExecutable);
        _workspaceRoot = TrustedToolchainPathPolicy.RequireRoot(options.WorkspaceRoot, "gcc_workspace");
        _expectedImageId = RequireImageId(options.ExpectedImageId);
        _options = options;
    }

    public string ProviderId => LanguageToolingCatalog.Gcc;
    public IReadOnlyCollection<string> CapabilityIds { get; } = ["gcc.compiler"];

    public async ValueTask<IReadOnlyList<LanguageToolingCapabilityStatus>> InspectAsync(
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (!PathsEqual(workspaceRoot, _workspaceRoot))
            return [Unavailable("gcc-workspace-binding-mismatch", "The GNU compiler is bound to a different trusted workspace.")];
        try
        {
            var receipt = await VerifyRuntimeAsync(cancellationToken).ConfigureAwait(false);
            return [new(
                "gcc.compiler",
                LanguageToolingCapabilityState.Available,
                "verified-container-gcc-runtime",
                "The trusted host verified GCC and G++ inside the immutable launcher-owned Hermes runtime.",
                KnownPinnedToolchainEvidenceSource.SafeVersion(receipt.GccVersion))];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            var code = exception is GccContainerAuthorityException authorityException
                ? authorityException.Code
                : "gcc-container-verification-failed";
            return [Unavailable(code, "The immutable Hermes GNU compiler authority is unavailable.")];
        }
    }

    public async ValueTask StartAsync(string workspaceRoot, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (!PathsEqual(workspaceRoot, _workspaceRoot))
            throw new InvalidOperationException("The GNU compiler start request used a different trusted workspace.");
        var receipt = await VerifyRuntimeAsync(cancellationToken).ConfigureAwait(false);
        lock (_stateGate) _startedReceipt = receipt;
    }

    public async Task<BuildResult> BuildAsync(GccBuildRequest request, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        var startedAt = DateTimeOffset.UtcNow;
        GccContainerRuntimeReceipt startedReceipt;
        lock (_stateGate)
        {
            if (_startedReceipt is null)
                return Failure(startedAt, "provider-not-ready", "The immutable GNU compiler authority is not ready.");
            startedReceipt = _startedReceipt;
        }

        string output;
        IReadOnlyList<string> sources;
        try
        {
            var requestRoot = TrustedToolchainPathPolicy.RequireRoot(request.WorkspaceRoot, "gcc_workspace");
            if (!PathsEqual(requestRoot, _workspaceRoot))
                return Failure(startedAt, "workspace-mismatch", "The GNU build workspace differs from the verified runtime mount.");
            var target = TrustedToolchainPathPolicy.ResolveExistingTarget(_workspaceRoot, request.TargetPath, "gcc_target");
            output = TrustedToolchainPathPolicy.ResolveOutputFile(_workspaceRoot, request.OutputPath, "gcc_output");
            sources = TrustedToolchainPathPolicy.EnumerateSources(_workspaceRoot, target, new TrustedSourceEnumerationBounds
            {
                MaximumSources = _options.MaximumSources,
                MaximumDirectories = _options.MaximumDirectories,
                MaximumDepth = _options.MaximumDepth,
                MaximumSourceBytes = _options.MaximumSourceBytes,
                MaximumAggregateSourceBytes = _options.MaximumAggregateSourceBytes,
            });
            if (sources.Count == 0)
                return Failure(startedAt, "sources-not-found", "No bounded C or C++ source was found.");
        }
        catch (TrustedToolchainValidationException exception)
        {
            return Failure(startedAt, exception.Code, exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Failure(startedAt, "gcc-path-validation-failed", "The GNU build paths could not be validated.");
        }

        string? pendingOutput = null;
        try
        {
            var currentReceipt = await VerifyRuntimeAsync(cancellationToken).ConfigureAwait(false);
            if (currentReceipt != startedReceipt)
                return Failure(startedAt, "gcc-runtime-binding-changed", "The immutable GNU runtime identity changed after verification.");

            pendingOutput = Path.Combine(Path.GetDirectoryName(output)!, $".hermes-gcc-artifact-{Guid.NewGuid():N}.pending");
            var sourceArguments = sources.Select(ToContainerRelativePath).ToArray();
            var pendingArgument = ToContainerRelativePath(pendingOutput);
            var usesCpp = sources.Any(source => !Path.GetExtension(source).Equals(".c", StringComparison.OrdinalIgnoreCase));
            var arguments = CreateCompileArguments(currentReceipt.ContainerId, usesCpp, request.Configuration, sourceArguments, pendingArgument);
            var process = await _runner.RunAsync(arguments, _workspaceRoot, _options.OperationTimeout, cancellationToken).ConfigureAwait(false);
            var diagnostics = ParseDiagnostics(process, _workspaceRoot, _options.MaximumDiagnostics);
            var outputCapture = new BuildOutput(process.StandardOutput, process.StandardError, process.OutputTruncated, process.DroppedCharacters);

            if (process.WasCancelled || cancellationToken.IsCancellationRequested)
                return Result(startedAt, process.ExitCode, diagnostics, outputCapture, true, "gcc-build-cancelled", "The GNU build was cancelled.");
            if (process.TimedOut)
                return Result(startedAt, process.ExitCode, diagnostics, outputCapture, false, "gcc-build-timeout", "The GNU build exceeded its fixed timeout.");
            if (process.OutputTruncated)
                return Result(startedAt, process.ExitCode, diagnostics, outputCapture, false, "gcc-output-limit", "The GNU compiler exceeded its bounded output limit.");
            if (process.FailureCode is not null || process.ExitCode != 0)
                return Result(startedAt, process.ExitCode, diagnostics, outputCapture, false, "gcc-build-failed", "The GNU compiler reported a build failure.");

            _ = TrustedToolchainPathPolicy.RequireNormalFile(
                _workspaceRoot, pendingOutput, "gcc_artifact", minimumBytes: 1, maximumBytes: _options.MaximumArtifactBytes);
            if (File.Exists(output))
                return Result(startedAt, process.ExitCode, diagnostics, outputCapture, false, "gcc-output-collision", "The host-owned GNU output path was unexpectedly occupied.");
            File.Move(pendingOutput, output, overwrite: false);
            pendingOutput = null;
            _ = TrustedToolchainPathPolicy.RequireNormalFile(
                _workspaceRoot, output, "gcc_artifact", minimumBytes: 1, maximumBytes: _options.MaximumArtifactBytes);
            return Result(startedAt, process.ExitCode, diagnostics, outputCapture, false, null, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result(startedAt, null, [], EmptyOutput(), true, "gcc-build-cancelled", "The GNU build was cancelled.");
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            var code = exception is GccContainerAuthorityException authorityException
                ? authorityException.Code
                : exception is TrustedToolchainValidationException validationException
                    ? validationException.Code
                    : "gcc-authority-failed";
            return Result(startedAt, null, [], EmptyOutput(), false, code, "The immutable GNU compiler authority could not complete safely.");
        }
        finally { DeletePendingArtifact(pendingOutput); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_stateGate)
        {
            _disposed = true;
            _startedReceipt = null;
        }
        return ValueTask.CompletedTask;
    }

    private async Task<GccContainerRuntimeReceipt> VerifyRuntimeAsync(CancellationToken cancellationToken)
    {
        var inspection = await _runner.RunAsync(
            ["inspect", "--type", "container", _options.ContainerName],
            _workspaceRoot,
            _options.InspectionTimeout,
            cancellationToken).ConfigureAwait(false);
        if (inspection.ExitCode != 0 || inspection.FailureCode is not null || inspection.OutputTruncated)
            throw new GccContainerAuthorityException("gcc-container-unavailable", "The launcher-owned Hermes container is unavailable.");

        string containerId;
        try
        {
            using var document = JsonDocument.Parse(inspection.StandardOutput, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() != 1)
                throw new JsonException();
            var container = document.RootElement[0];
            containerId = container.GetProperty("Id").GetString() ?? string.Empty;
            var imageId = RequireImageId(container.GetProperty("Image").GetString() ?? string.Empty);
            if (containerId.Length != 64
                || !containerId.All(character => character is >= 'a' and <= 'f' or >= '0' and <= '9')
                || !string.Equals(imageId, _expectedImageId, StringComparison.Ordinal)
                || container.GetProperty("State").GetProperty("Running").ValueKind != JsonValueKind.True)
                throw new GccContainerAuthorityException("gcc-container-binding-mismatch", "The running container does not match the launcher-approved runtime identity.");
            var mounts = container.GetProperty("Mounts").EnumerateArray().Where(mount =>
                mount.GetProperty("Destination").GetString() == _options.ContainerWorkspaceRoot).ToArray();
            if (mounts.Length != 1
                || mounts[0].GetProperty("RW").ValueKind != JsonValueKind.True
                || !PathsEqual(mounts[0].GetProperty("Source").GetString() ?? string.Empty, _workspaceRoot))
                throw new GccContainerAuthorityException("gcc-workspace-binding-mismatch", "The Hermes container is not bound to the trusted workspace.");
        }
        catch (GccContainerAuthorityException) { throw; }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            throw new GccContainerAuthorityException("gcc-container-inspection-invalid", "Docker returned invalid GNU runtime evidence.");
        }

        var gccVersion = await ProbeAsync(containerId, _options.GccExecutablePath, "--version", "gcc", cancellationToken).ConfigureAwait(false);
        var gxxVersion = await ProbeAsync(containerId, _options.GxxExecutablePath, "--version", "g++", cancellationToken).ConfigureAwait(false);
        var gccMachine = await ProbeAsync(containerId, _options.GccExecutablePath, "-dumpmachine", null, cancellationToken).ConfigureAwait(false);
        var gxxMachine = await ProbeAsync(containerId, _options.GxxExecutablePath, "-dumpmachine", null, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(gccMachine, gxxMachine, StringComparison.Ordinal))
            throw new GccContainerAuthorityException("gcc-machine-mismatch", "GCC and G++ reported different target machines.");
        return new(containerId, _expectedImageId, gccVersion, gxxVersion, gccMachine);
    }

    private async Task<string> ProbeAsync(
        string containerId,
        string executable,
        string argument,
        string? requiredIdentity,
        CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            ["exec", "--user", "10000", "--workdir", _options.ContainerWorkspaceRoot, containerId, executable, argument],
            _workspaceRoot,
            _options.InspectionTimeout,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || result.FailureCode is not null || result.OutputTruncated)
            throw new GccContainerAuthorityException("gcc-probe-failed", "A fixed GNU compiler identity probe failed.");
        var line = FirstLine(result.StandardOutput, requiredIdentity is null ? 128 : 512);
        if (requiredIdentity is not null && !line.Contains(requiredIdentity, StringComparison.OrdinalIgnoreCase))
            throw new GccContainerAuthorityException("gcc-probe-invalid", "A fixed GNU executable returned the wrong identity.");
        if (requiredIdentity is null && (line.Length == 0 || line.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '+'))))
            throw new GccContainerAuthorityException("gcc-machine-invalid", "The GNU target-machine identity is invalid.");
        return line;
    }

    private IReadOnlyList<string> CreateCompileArguments(
        string containerId,
        bool usesCpp,
        BuildConfiguration configuration,
        IReadOnlyList<string> sources,
        string pendingOutput)
    {
        var arguments = new List<string>
        {
            "exec", "--user", "10000", "--workdir", _options.ContainerWorkspaceRoot,
            containerId, usesCpp ? _options.GxxExecutablePath : _options.GccExecutablePath,
            usesCpp ? "-std=c++20" : "-std=c17",
            "-Wall", "-Wextra", "-pedantic-errors", "-fdiagnostics-color=never", "-fno-diagnostics-show-caret",
        };
        arguments.AddRange(configuration == BuildConfiguration.Release ? ["-O2", "-DNDEBUG"] : ["-O0", "-g"]);
        arguments.AddRange(sources);
        arguments.Add("-o");
        arguments.Add(pendingOutput);
        return arguments;
    }

    private static IReadOnlyList<BuildDiagnostic> ParseDiagnostics(
        GccContainerProcessResult process,
        string workspaceRoot,
        int maximumDiagnostics)
    {
        var diagnostics = new List<BuildDiagnostic>();
        foreach (var raw in string.Concat(process.StandardError, "\n", process.StandardOutput).Split('\n'))
        {
            if (diagnostics.Count >= maximumDiagnostics) break;
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("/workspace/", StringComparison.Ordinal)) line = line[11..];
            if (GccDiagnosticParser.TryParse(line, workspaceRoot, out var diagnostic) && diagnostic is not null)
                diagnostics.Add(diagnostic);
        }
        return BuildResult.Freeze(diagnostics);
    }

    private string ToContainerRelativePath(string hostPath)
    {
        if (!TrustedToolchainPathPolicy.TryWorkspaceRelative(_workspaceRoot, hostPath, out var relative))
            throw new GccContainerAuthorityException("gcc-container-path-invalid", "A GNU build path escaped the trusted workspace.");
        return relative;
    }

    private static BuildResult Failure(DateTimeOffset startedAt, string code, string message) =>
        Result(startedAt, null, [], EmptyOutput(), false, code, message);

    private static BuildResult Result(
        DateTimeOffset startedAt,
        int? exitCode,
        IReadOnlyList<BuildDiagnostic> diagnostics,
        BuildOutput output,
        bool wasCancelled,
        string? failureCode,
        string? failureMessage) => new(
            failureCode is null,
            exitCode,
            BuildResult.Freeze(diagnostics),
            output,
            wasCancelled,
            failureCode,
            failureMessage,
            startedAt,
            DateTimeOffset.UtcNow);

    private static BuildOutput EmptyOutput() => new(string.Empty, string.Empty, false, 0);

    private static LanguageToolingCapabilityStatus Unavailable(string code, string message) => new(
        "gcc.compiler",
        LanguageToolingCapabilityState.Unavailable,
        KnownPinnedToolchainEvidenceSource.SafeCode(code, "gcc-container-unavailable"),
        KnownPinnedToolchainEvidenceSource.SafeMessage(message, "The immutable GNU compiler runtime is unavailable."));

    private static string FirstLine(string value, int maximumLength)
    {
        var line = value.Replace('\0', ' ').Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim()).FirstOrDefault(item => item.Length > 0) ?? string.Empty;
        return line[..Math.Min(line.Length, maximumLength)];
    }

    private static string RequireImageId(string value)
    {
        if (value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal)
            || !value[7..].All(character => character is >= 'a' and <= 'f' or >= '0' and <= '9'))
            throw new ArgumentException("The Hermes runtime image ID is invalid.", nameof(value));
        return value;
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static bool IsExpectedFailure(Exception exception) => exception is
        GccContainerAuthorityException or TrustedToolchainValidationException or IOException
        or UnauthorizedAccessException or ArgumentException or InvalidOperationException
        or System.Security.SecurityException or JsonException;

    private static void DeletePendingArtifact(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    private static IGccContainerProcessRunner CreateOwnedRunner(HermesContainerGccToolingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var executable = Path.GetFullPath(options.DockerExecutablePath);
        if (!Path.IsPathFullyQualified(executable)
            || !Path.GetFileName(executable).Equals(OperatingSystem.IsWindows() ? "docker.exe" : "docker", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(executable))
            throw new ArgumentException("The fixed Docker executable is unavailable.", nameof(options));
        return new OwnedGccContainerProcessRunner(executable, options.CleanupTimeout, options.MaximumRetainedCharacters);
    }

    private static void ValidateOptions(HermesContainerGccToolingOptions options, bool validateDockerExecutable)
    {
        if (validateDockerExecutable && string.IsNullOrWhiteSpace(options.DockerExecutablePath))
            throw new ArgumentException("The fixed Docker executable is unavailable.", nameof(options));
        if (options.ContainerName != "hermes"
            || options.ContainerWorkspaceRoot != "/workspace"
            || options.GccExecutablePath != "/usr/bin/gcc"
            || options.GxxExecutablePath != "/usr/bin/g++")
            throw new ArgumentException("The GNU container identity and paths must match the fixed Hermes contract.", nameof(options));
        if (options.InspectionTimeout <= TimeSpan.Zero || options.InspectionTimeout > TimeSpan.FromSeconds(30)
            || options.OperationTimeout <= TimeSpan.Zero || options.OperationTimeout > TimeSpan.FromMinutes(10)
            || options.CleanupTimeout <= TimeSpan.Zero || options.CleanupTimeout > TimeSpan.FromSeconds(30)
            || options.MaximumRetainedCharacters is < 4_096 or > 1024 * 1024
            || options.MaximumDiagnostics is < 1 or > 2_000
            || options.MaximumSources is < 1 or > 512
            || options.MaximumDirectories is < 1 or > 2_048
            || options.MaximumDepth is < 1 or > 24
            || options.MaximumSourceBytes is < 1 or > 16 * 1024 * 1024
            || options.MaximumAggregateSourceBytes < options.MaximumSourceBytes
            || options.MaximumAggregateSourceBytes > 128 * 1024 * 1024
            || options.MaximumArtifactBytes is < 1 or > 512L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

internal sealed record GccContainerRuntimeReceipt(
    string ContainerId,
    string ImageId,
    string GccVersion,
    string GxxVersion,
    string DumpMachine);

internal sealed class GccContainerAuthorityException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

internal sealed record GccContainerProcessResult(
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    bool OutputTruncated,
    long DroppedCharacters,
    bool WasCancelled,
    bool TimedOut,
    string? FailureCode);

internal interface IGccContainerProcessRunner
{
    Task<GccContainerProcessResult> RunAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal sealed class OwnedGccContainerProcessRunner(
    string dockerExecutable,
    TimeSpan cleanupTimeout,
    int maximumRetainedCharacters) : IGccContainerProcessRunner
{
    public async Task<GccContainerProcessResult> RunAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var result = await OwnedToolchainProcessRunner.RunAsync(
            dockerExecutable,
            workingDirectory,
            arguments,
            new OwnedToolchainProcessBounds
            {
                Timeout = timeout,
                CleanupTimeout = cleanupTimeout,
                MaximumRetainedCharacters = maximumRetainedCharacters,
            },
            cancellationToken).ConfigureAwait(false);
        return new(
            result.ExitCode,
            result.StandardOutput,
            result.StandardError,
            result.OutputTruncated,
            result.DroppedCharacters,
            result.WasCancelled,
            result.TimedOut,
            result.FailureCode);
    }
}
