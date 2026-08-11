using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace HermesDeveloperServices;

public sealed partial class ArduinoHostProvider
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> UploadGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ArduinoProviderOptions _options;
    private readonly string _stateRoot;

    private ArduinoHostProvider(ArduinoProviderOptions options, string stateRoot)
    {
        _options = options;
        _stateRoot = stateRoot;
    }

    public static async Task<ArduinoHostProvider> CreateAsync(
        ArduinoProviderOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        await using var lease = await PinnedToolchainReceiptLoader.StageAsync(
            options.WorkbenchRoot,
            options.ToolchainRelativePath,
            options.ReceiptFileName,
            ArduinoProviderOptions.ToolchainId,
            [ArduinoProviderOptions.ExecutableLogicalName],
            cancellationToken).ConfigureAwait(false);
        _ = TrustedToolchainPathPolicy.ResolveExistingFile(
            lease.Staged.Root, options.ConfigurationRelativePath, "arduino_configuration");
        var workbench = TrustedToolchainPathPolicy.RequireRoot(options.WorkbenchRoot, "workbench");
        var state = TrustedToolchainPathPolicy.ResolveExistingTarget(workbench, options.StateRelativePath, "arduino_state");
        if (!Directory.Exists(state))
            throw new TrustedToolchainValidationException("arduino_state_invalid", "The Arduino state root is not a directory.");

        var provider = new ArduinoHostProvider(options, state);
        await provider.VerifyConfigurationAsync(lease, cancellationToken).ConfigureAwait(false);
        await provider.VerifyBoardProfileAsync(lease, cancellationToken).ConfigureAwait(false);
        return provider;
    }

    public static string CompilePermissionScope(ArduinoCompileRequest request) =>
        $"{request.WorkspaceRoot}|{request.SketchRelativePath}|{request.OutputDirectoryRelativePath}|{request.Fqbn}";

    public static string UploadPermissionScope(ArduinoUploadRequest request) =>
        $"{request.WorkspaceRoot}|{request.SketchRelativePath}|{request.OutputDirectoryRelativePath}|{request.Fqbn}|{request.Port}";

    public static string InstallPermissionScope(ArduinoInstallRequest request) =>
        string.IsNullOrWhiteSpace(request.Version) ? request.Package : $"{request.Package}@{request.Version}";

    public async Task<ArduinoInventoryResult> InspectAsync(
        ArduinoInventoryKind kind,
        CancellationToken cancellationToken = default)
    {
        var command = kind switch
        {
            ArduinoInventoryKind.Version => new[] { "version" },
            ArduinoInventoryKind.Cores => new[] { "core", "list" },
            ArduinoInventoryKind.Boards => new[] { "board", "list" },
            ArduinoInventoryKind.Libraries => new[] { "lib", "list" },
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var operation = $"inspect_{kind.ToString().ToLowerInvariant()}";
        try
        {
            await using var lease = await StageAsync(cancellationToken).ConfigureAwait(false);
            var process = await RunCliAsync(lease, command, TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
            var result = Convert(process, operation, ProcessSucceeded(process)
                ? "Arduino CLI returned the requested pinned-store inventory."
                : "Arduino CLI inventory failed.");
            return new(result, ExtractInventoryEntries(process.StandardOutput));
        }
        catch (TrustedToolchainValidationException exception) { return new(EmbeddedHostOperationResult.Failure(operation, exception.Code, exception.Message), []); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return new(EmbeddedHostOperationResult.Failure(operation, "arduino_authority_failed", "Arduino authority could not be established safely."), []); }
    }

    public Task<EmbeddedHostOperationResult> SearchCoresAsync(string query, CancellationToken cancellationToken = default) =>
        SearchAsync("search_cores", ["core", "search", ValidateQuery(query)], cancellationToken);

    public Task<EmbeddedHostOperationResult> SearchLibrariesAsync(string query, CancellationToken cancellationToken = default) =>
        SearchAsync("search_libraries", ["lib", "search", ValidateQuery(query), "--omit-releases-details"], cancellationToken);

    public async Task<EmbeddedHostOperationResult> InstallCoreAsync(
        ArduinoInstallRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Version))
            return EmbeddedHostOperationResult.Failure("install_core", "version_required", "Core installation requires an explicit version.");
        var spec = ValidateCoreSpec(request.Package, request.Version);
        if (!EmbeddedPermissionPolicy.Allows(request.Permission, EmbeddedPermissionKind.InstallCore, InstallPermissionScope(request)))
            return EmbeddedHostOperationResult.PermissionDenied("install_core");
        try
        {
            await using var lease = await StageAsync(cancellationToken).ConfigureAwait(false);
            var process = await RunCliAsync(lease, ["core", "install", spec], TimeSpan.FromMinutes(30), cancellationToken).ConfigureAwait(false);
            return Convert(process, "install_core", ProcessSucceeded(process)
                ? "The explicitly approved Arduino core was installed in the configured state root." : "Arduino core installation failed.");
        }
        catch (TrustedToolchainValidationException exception) { return EmbeddedHostOperationResult.Failure("install_core", exception.Code, exception.Message); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return EmbeddedHostOperationResult.Failure("install_core", "arduino_authority_failed", "Arduino authority could not be established safely."); }
    }

    public async Task<EmbeddedHostOperationResult> InstallLibraryAsync(
        ArduinoInstallRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Version))
            return EmbeddedHostOperationResult.Failure("install_library", "version_required", "Library installation requires an explicit version.");
        var spec = ValidateLibrarySpec(request.Package, request.Version);
        if (!EmbeddedPermissionPolicy.Allows(request.Permission, EmbeddedPermissionKind.InstallLibrary, InstallPermissionScope(request)))
            return EmbeddedHostOperationResult.PermissionDenied("install_library");
        try
        {
            await using var lease = await StageAsync(cancellationToken).ConfigureAwait(false);
            var process = await RunCliAsync(lease, ["lib", "install", spec], TimeSpan.FromMinutes(30), cancellationToken).ConfigureAwait(false);
            return Convert(process, "install_library", ProcessSucceeded(process)
                ? "The explicitly approved Arduino library was installed in the configured state root." : "Arduino library installation failed.");
        }
        catch (TrustedToolchainValidationException exception) { return EmbeddedHostOperationResult.Failure("install_library", exception.Code, exception.Message); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return EmbeddedHostOperationResult.Failure("install_library", "arduino_authority_failed", "Arduino authority could not be established safely."); }
    }

    public async Task<EmbeddedHostOperationResult> CompileAsync(
        ArduinoCompileRequest request,
        CancellationToken cancellationToken = default)
    {
        try { return await CompileCoreAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return EmbeddedHostOperationResult.Failure("compile", "compile_cancelled", "Arduino compilation was cancelled."); }
        catch (TrustedToolchainValidationException exception) { return EmbeddedHostOperationResult.Failure("compile", exception.Code, exception.Message); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return EmbeddedHostOperationResult.Failure("compile", "arduino_path_failed", "Arduino build paths could not be handled safely."); }
    }

    private async Task<EmbeddedHostOperationResult> CompileCoreAsync(
        ArduinoCompileRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!EmbeddedPermissionPolicy.Allows(request.Permission, EmbeddedPermissionKind.CompileWorkspace, CompilePermissionScope(request)))
            return EmbeddedHostOperationResult.PermissionDenied("compile");
        var resolved = ResolveBuildPaths(request.WorkspaceRoot, request.SketchRelativePath, request.OutputDirectoryRelativePath);
        var fqbn = ValidateFqbn(request.Fqbn);
        string? stage = null;
        try
        {
            stage = CreateOutputStage();
            await using var lease = await StageAsync(cancellationToken).ConfigureAwait(false);
            var verification = await VerifyFqbnAsync(lease, fqbn, cancellationToken).ConfigureAwait(false);
            if (!ProcessSucceeded(verification))
                return Convert(verification, "compile", "The explicit FQBN is not available from the pinned Arduino configuration.", resolved.Root);
            var process = await RunCliAsync(lease,
                ["compile", "--fqbn", fqbn, "--output-dir", stage, "--warnings", "all", resolved.Sketch],
                TimeSpan.FromMinutes(30), cancellationToken, resolved.Root).ConfigureAwait(false);
            using var artifacts = OpenFreshArtifacts(stage);
            if (!ProcessSucceeded(process) || artifacts.Count == 0)
                return Convert(process, "compile", "Arduino compilation failed or produced no fresh bounded artifacts.", resolved.Root) with
                { Succeeded = false, FailureCode = process.FailureCode ?? "arduino_artifact_missing" };
            PublishArtifacts(artifacts.Paths, resolved.Output);
            return ConvertBuild(process, "compile", "The Arduino sketch compiled for the verified FQBN.", resolved.Root, resolved.Output);
        }
        finally { DeleteOutputStage(stage, _options.WorkbenchRoot); }
    }

    public async Task<EmbeddedHostOperationResult> UploadAsync(
        ArduinoUploadRequest request,
        CancellationToken cancellationToken = default)
    {
        try { return await UploadCoreAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return EmbeddedHostOperationResult.Failure("upload", "upload_cancelled", "Arduino upload was cancelled."); }
        catch (TrustedToolchainValidationException exception) { return EmbeddedHostOperationResult.Failure("upload", exception.Code, exception.Message); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return EmbeddedHostOperationResult.Failure("upload", "arduino_path_failed", "Arduino upload paths could not be handled safely."); }
    }

    private async Task<EmbeddedHostOperationResult> UploadCoreAsync(
        ArduinoUploadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!EmbeddedPermissionPolicy.Allows(request.Permission, EmbeddedPermissionKind.UploadDevice, UploadPermissionScope(request)))
            return EmbeddedHostOperationResult.PermissionDenied("upload");
        var resolved = ResolveBuildPaths(request.WorkspaceRoot, request.SketchRelativePath, request.OutputDirectoryRelativePath);
        var fqbn = ValidateFqbn(request.Fqbn);
        var port = ValidatePort(request.Port);
        var gate = UploadGates.GetOrAdd(port, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? stage = null;
        try
        {
            stage = CreateOutputStage();
            await using var lease = await StageAsync(cancellationToken).ConfigureAwait(false);
            var verification = await VerifyFqbnAsync(lease, fqbn, cancellationToken).ConfigureAwait(false);
            if (!ProcessSucceeded(verification)) return Convert(verification, "upload", "The explicit FQBN is unavailable.", resolved.Root);
            var boards = await RunCliAsync(lease, ["board", "list"], TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
            if (!ProcessSucceeded(boards) || !JsonContainsExactPortAndFqbn(boards.StandardOutput, port, fqbn))
                return Convert(boards, "upload", "The exact port is not bound to the requested FQBN.", resolved.Root) with
                { Succeeded = false, FailureCode = boards.FailureCode ?? "arduino_port_fqbn_mismatch" };
            var compile = await RunCliAsync(lease,
                ["compile", "--fqbn", fqbn, "--output-dir", stage, "--warnings", "all", resolved.Sketch],
                TimeSpan.FromMinutes(30), cancellationToken, resolved.Root).ConfigureAwait(false);
            using var artifacts = OpenFreshArtifacts(stage);
            if (!ProcessSucceeded(compile) || artifacts.Count == 0)
                return Convert(compile, "upload", "Compilation produced no fresh upload artifacts.", resolved.Root) with
                { Succeeded = false, FailureCode = compile.FailureCode ?? "arduino_artifact_missing" };
            await artifacts.VerifyUnchangedAsync(cancellationToken).ConfigureAwait(false);
            var upload = await RunCliAsync(lease,
                ["upload", "--fqbn", fqbn, "--port", port, "--input-dir", stage, resolved.Sketch],
                TimeSpan.FromMinutes(10), cancellationToken, resolved.Root).ConfigureAwait(false);
            if (ProcessSucceeded(upload)) PublishArtifacts(artifacts.Paths, resolved.Output);
            return ConvertBuild(upload, "upload", ProcessSucceeded(upload)
                ? "Firmware was uploaded to the exact approved port for the verified FQBN."
                : "Arduino upload failed; no hardware success is claimed.", resolved.Root, resolved.Output);
        }
        finally { DeleteOutputStage(stage, _options.WorkbenchRoot); gate.Release(); }
    }

    private async Task VerifyConfigurationAsync(PinnedToolchainPackageLease lease, CancellationToken cancellationToken)
    {
        var process = await RunCliAsync(lease, ["config", "dump"], TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
        if (!ProcessSucceeded(process))
            throw new TrustedToolchainValidationException("arduino_configuration_unreadable", "The pinned Arduino configuration could not be inspected.");
        try
        {
            using var json = JsonDocument.Parse(process.StandardOutput);
            var configuration = json.RootElement;
            if (configuration.ValueKind == JsonValueKind.Object)
            {
                var rootProperties = configuration.EnumerateObject().ToArray();
                if (rootProperties.Length == 1
                    && rootProperties[0].NameEquals("config")
                    && rootProperties[0].Value.ValueKind == JsonValueKind.Object)
                {
                    configuration = rootProperties[0].Value;
                }
            }
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Walk(configuration, string.Empty);
            foreach (var key in new[] { "directories.data", "directories.downloads", "directories.user" })
            {
                if (!values.TryGetValue(key, out var path) || !IsWithinStateRoot(path))
                    throw new TrustedToolchainValidationException("arduino_state_escape", "Arduino configuration directories must stay under the explicit state root.");
            }

            if ((values.TryGetValue("board_manager.enable_unsafe_install", out var boardUnsafe) && IsTrue(boardUnsafe))
                || (values.TryGetValue("library.enable_unsafe_install", out var libraryUnsafe) && IsTrue(libraryUnsafe)))
                throw new TrustedToolchainValidationException("arduino_unsafe_install_enabled", "Unsafe Arduino package installation is not allowed.");

            void Walk(JsonElement element, string prefix)
            {
                if (element.ValueKind != JsonValueKind.Object) return;
                foreach (var property in element.EnumerateObject())
                {
                    var key = string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}.{property.Name}";
                    if (property.Value.ValueKind == JsonValueKind.Object) Walk(property.Value, key);
                    else if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.True or JsonValueKind.False)
                        values[key] = property.Value.ToString();
                }
            }
        }
        catch (JsonException)
        {
            throw new TrustedToolchainValidationException("arduino_configuration_invalid", "Arduino configuration inspection did not return valid JSON.");
        }
    }

    private Task<OwnedToolchainProcessResult> VerifyFqbnAsync(
        PinnedToolchainPackageLease lease, string fqbn, CancellationToken cancellationToken) =>
        RunCliAsync(lease, ["board", "details", "--fqbn", fqbn], TimeSpan.FromMinutes(1), cancellationToken);

    private async Task VerifyBoardProfileAsync(
        PinnedToolchainPackageLease lease,
        CancellationToken cancellationToken)
    {
        var cores = await RunCliAsync(lease, ["core", "list"], TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
        if (!ProcessSucceeded(cores))
            throw new TrustedToolchainValidationException("arduino_core_unavailable", "The pinned Arduino board core could not be inspected.");
        try
        {
            using var document = JsonDocument.Parse(cores.StandardOutput);
            if (!document.RootElement.TryGetProperty("platforms", out var platforms)
                || platforms.ValueKind != JsonValueKind.Array)
                throw new JsonException();
            var matches = platforms.EnumerateArray().Where(platform =>
                platform.ValueKind == JsonValueKind.Object
                && platform.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String
                && id.GetString() == ArduinoProviderOptions.SupportedCore
                && platform.TryGetProperty("installed_version", out var version)
                && version.ValueKind == JsonValueKind.String
                && version.GetString() == ArduinoProviderOptions.SupportedCoreVersion).ToArray();
            if (matches.Length != 1)
                throw new TrustedToolchainValidationException("arduino_core_identity_mismatch", "The exact supported Arduino board core is not installed.");
        }
        catch (JsonException)
        {
            throw new TrustedToolchainValidationException("arduino_core_inventory_invalid", "Arduino core inspection did not return valid bounded JSON.");
        }

        var board = await VerifyFqbnAsync(lease, ArduinoProviderOptions.SupportedFqbn, cancellationToken).ConfigureAwait(false);
        if (!ProcessSucceeded(board))
            throw new TrustedToolchainValidationException("arduino_board_profile_unavailable", "The exact supported Arduino board profile is not installed.");
    }

    private async Task<EmbeddedHostOperationResult> SearchAsync(
        string operation,
        IReadOnlyList<string> command,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var lease = await StageAsync(cancellationToken).ConfigureAwait(false);
            var process = await RunCliAsync(lease, command, TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
            return Convert(process, operation, ProcessSucceeded(process)
                ? "Arduino CLI returned bounded package search results." : "Arduino package search failed.");
        }
        catch (TrustedToolchainValidationException exception) { return EmbeddedHostOperationResult.Failure(operation, exception.Code, exception.Message); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return EmbeddedHostOperationResult.Failure(operation, "arduino_authority_failed", "Arduino authority could not be established safely."); }
    }

    private Task<PinnedToolchainPackageLease> StageAsync(CancellationToken cancellationToken) =>
        PinnedToolchainReceiptLoader.StageAsync(
            _options.WorkbenchRoot,
            _options.ToolchainRelativePath,
            _options.ReceiptFileName,
            ArduinoProviderOptions.ToolchainId,
            [ArduinoProviderOptions.ExecutableLogicalName],
            cancellationToken);

    private Task<OwnedToolchainProcessResult> RunCliAsync(
        PinnedToolchainPackageLease lease,
        IReadOnlyList<string> command,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? workingDirectory = null)
    {
        var configurationPath = TrustedToolchainPathPolicy.ResolveExistingFile(
            lease.Staged.Root, _options.ConfigurationRelativePath, "arduino_configuration");
        var arguments = new List<string> { "--config-file", configurationPath, "--json" };
        arguments.AddRange(command);
        return OwnedToolchainProcessRunner.RunAsync(
            lease.Staged.Executables[ArduinoProviderOptions.ExecutableLogicalName].Path,
            workingDirectory ?? lease.Staged.Root,
            arguments,
            new OwnedToolchainProcessBounds
            {
                Timeout = timeout,
                CleanupTimeout = TimeSpan.FromSeconds(5),
                MaximumRetainedCharacters = 512 * 1024,
            },
            cancellationToken);
    }

    private EmbeddedHostOperationResult ConvertBuild(
        OwnedToolchainProcessResult process,
        string operation,
        string summary,
        string workspaceRoot,
        string outputDirectory) =>
        Convert(process, operation, summary, workspaceRoot) with
        {
            Diagnostics = ArduinoDiagnosticNormalizer.Parse(workspaceRoot, process.StandardOutput, process.StandardError),
            WorkspaceRelativeArtifacts = EnumerateArtifacts(workspaceRoot, outputDirectory),
        };

    private EmbeddedHostOperationResult Convert(
        OwnedToolchainProcessResult process,
        string operation,
        string summary,
        string? workspaceRoot = null)
    {
        var roots = new[] { workspaceRoot ?? string.Empty, _options.WorkbenchRoot, _stateRoot };
        return new(
            ProcessSucceeded(process),
            operation,
            summary,
            process.FailureCode,
            process.ExitCode,
            ArduinoDiagnosticNormalizer.RedactPaths(process.StandardOutput, roots),
            ArduinoDiagnosticNormalizer.RedactPaths(process.StandardError, roots),
            process.OutputTruncated,
            process.DroppedCharacters,
            process.WasCancelled,
            process.TimedOut,
            [],
            []);
    }

    private static (string Root, string Sketch, string Output) ResolveBuildPaths(
        string workspaceRoot,
        string sketchRelativePath,
        string outputRelativePath)
    {
        var root = TrustedToolchainPathPolicy.RequireRoot(workspaceRoot, "workspace");
        var sketch = TrustedToolchainPathPolicy.ResolveExistingTarget(root, sketchRelativePath, "sketch");
        var output = TrustedToolchainPathPolicy.ResolveExistingTarget(root, outputRelativePath, "arduino_output");
        if (!Directory.Exists(output))
            throw new TrustedToolchainValidationException("arduino_output_invalid", "The Arduino output target is not a directory.");
        if (Directory.EnumerateFileSystemEntries(output).Any())
            throw new TrustedToolchainValidationException("arduino_output_not_empty", "The approved Arduino output directory must be empty.");
        return (root, sketch, output);
    }

    private string CreateOutputStage()
    {
        var root = TrustedToolchainPathPolicy.RequireRoot(_options.WorkbenchRoot, "workbench");
        return
        TrustedToolchainPathPolicy.CreateOwnedTemporaryDirectory(
            root, root, ".hermes-arduino-output-", "arduino_output_stage");
    }

    private static ArtifactLease OpenFreshArtifacts(string stage)
    {
        var entries = Directory.EnumerateFileSystemEntries(stage).Take(102).ToArray();
        if (entries.Length is 0 or > 100 || entries.Any(Directory.Exists)) return ArtifactLease.Empty;
        long aggregate = 0;
        var files = new List<LockedArtifact>();
        foreach (var path in entries.Order(StringComparer.OrdinalIgnoreCase))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) { Dispose(files); return ArtifactLease.Empty; }
            var length = new FileInfo(path).Length;
            if (length <= 0 || length > 64L * 1024 * 1024 || aggregate > 256L * 1024 * 1024 - length)
            { Dispose(files); return ArtifactLease.Empty; }
            aggregate += length;
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                var hash = SHA256.HashData(stream);
                stream.Position = 0;
                files.Add(new(path, length, hash, stream));
            }
            catch { stream.Dispose(); Dispose(files); throw; }
        }
        return new(files);

        static void Dispose(IEnumerable<LockedArtifact> values) { foreach (var value in values) value.Stream.Dispose(); }
    }

    private static void PublishArtifacts(IReadOnlyList<string> artifacts, string output)
    {
        foreach (var source in artifacts)
        {
            var destination = Path.Combine(output, Path.GetFileName(source));
            File.Copy(source, destination, overwrite: false);
        }
    }

    private static void DeleteOutputStage(string? stage, string root)
    {
        if (stage is null) return;
        try
        {
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var full = Path.GetFullPath(stage);
            if (full.StartsWith(fullRoot + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                && Path.GetFileName(full).StartsWith(".hermes-arduino-output-", StringComparison.Ordinal)
                && Directory.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0)
                Directory.Delete(full, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static IReadOnlyList<string> EnumerateArtifacts(string root, string output)
    {
        var artifacts = new List<string>();
        foreach (var path in Directory.EnumerateFiles(output).Order(StringComparer.OrdinalIgnoreCase).Take(101))
        {
            if (artifacts.Count == 100) break;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
            if (TrustedToolchainPathPolicy.TryWorkspaceRelative(root, path, out var relative)) artifacts.Add(relative);
        }
        return artifacts;
    }

    private bool IsWithinStateRoot(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate)) return false;
        var canonical = Path.GetFullPath(candidate);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return canonical.Equals(_stateRoot, comparison)
            || canonical.StartsWith(_stateRoot + Path.DirectorySeparatorChar, comparison)
            || canonical.StartsWith(_stateRoot + Path.AltDirectorySeparatorChar, comparison);
    }

    private static bool IsTrue(string value) => bool.TryParse(value, out var result) && result;

    private static string ValidateQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 80 || !SearchPattern().IsMatch(query))
            throw new ArgumentException("Use a bounded package search query without shell syntax.", nameof(query));
        return query;
    }

    private static string ValidateFqbn(string fqbn)
    {
        if (string.IsNullOrWhiteSpace(fqbn) || !FqbnPattern().IsMatch(fqbn))
            throw new ArgumentException("Use an explicit fully qualified board name.", nameof(fqbn));
        return fqbn;
    }

    private static string ValidatePort(string port)
    {
        if (string.IsNullOrWhiteSpace(port) || !PortPattern().IsMatch(port) || port.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Use one exact bounded Arduino port address.", nameof(port));
        return port;
    }

    private static string ValidateCoreSpec(string package, string? version)
    {
        if (string.IsNullOrWhiteSpace(package) || !CorePattern().IsMatch(package))
            throw new ArgumentException("Use an exact Arduino core identifier.", nameof(package));
        return AppendVersion(package, version);
    }

    private static string ValidateLibrarySpec(string package, string? version)
    {
        if (string.IsNullOrWhiteSpace(package) || !LibraryPattern().IsMatch(package))
            throw new ArgumentException("Use an exact Arduino library name.", nameof(package));
        return AppendVersion(package, version);
    }

    private static string AppendVersion(string package, string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return package;
        if (!VersionPattern().IsMatch(version)) throw new ArgumentException("Use a bounded package version.", nameof(version));
        return $"{package}@{version}";
    }

    private static bool JsonContainsExactPortAndFqbn(string json, string expectedPort, string expectedFqbn)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return Walk(document.RootElement);
        }
        catch (JsonException) { return false; }

        bool Walk(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var address = FindString(element, "address");
                var fqbnMatch = element.EnumerateObject()
                    .Where(property => property.Name.Equals("matching_boards", StringComparison.OrdinalIgnoreCase))
                    .Any(property => ContainsFqbn(property.Value));
                if (string.Equals(address, expectedPort, StringComparison.Ordinal) && fqbnMatch) return true;
                return element.EnumerateObject().Any(property => Walk(property.Value));
            }
            return element.ValueKind == JsonValueKind.Array && element.EnumerateArray().Any(Walk);

            bool ContainsFqbn(JsonElement value) => value.ValueKind == JsonValueKind.Array
                && value.EnumerateArray().Any(board => string.Equals(FindString(board, "fqbn"), expectedFqbn, StringComparison.Ordinal));

            static string? FindString(JsonElement value, string name)
            {
                if (value.ValueKind != JsonValueKind.Object) return null;
                foreach (var property in value.EnumerateObject())
                {
                    if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                        return property.Value.GetString();
                    var nested = FindString(property.Value, name);
                    if (nested is not null) return nested;
                }
                return null;
            }
        }
    }

    private static bool ProcessSucceeded(OwnedToolchainProcessResult process) =>
        process.ExitCode == 0 && process.FailureCode is null && !process.WasCancelled && !process.TimedOut;

    private static IReadOnlyList<string> ExtractInventoryEntries(string json)
    {
        var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var document = JsonDocument.Parse(json);
            Walk(document.RootElement);
        }
        catch (JsonException) { }
        return entries.Order(StringComparer.OrdinalIgnoreCase).Take(200).ToArray();

        void Walk(JsonElement element)
        {
            if (entries.Count >= 200) return;
            if (element.ValueKind == JsonValueKind.Object)
                foreach (var property in element.EnumerateObject()) Walk(property.Value);
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var child in element.EnumerateArray()) Walk(child);
            else if (element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 and <= 200 } value)
                entries.Add(value);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9 ._+:-]{0,79}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SearchPattern();
    [GeneratedRegex("^[A-Za-z0-9_.-]+:[A-Za-z0-9_.-]+:[A-Za-z0-9_.-]+(?::[A-Za-z0-9_.=,-]+)?$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex FqbnPattern();
    [GeneratedRegex("^(?:COM[1-9][0-9]{0,2}|/[A-Za-z0-9_./-]{1,150}|[A-Za-z0-9_.-]{1,80}:[0-9]{1,5})$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex PortPattern();
    [GeneratedRegex("^[A-Za-z0-9_.-]+:[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex CorePattern();
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9 ._+&()-]{0,79}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex LibraryPattern();
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._+-]{0,39}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex VersionPattern();

    private sealed record LockedArtifact(string Path, long Length, byte[] Hash, FileStream Stream);

    private sealed class ArtifactLease(IReadOnlyList<LockedArtifact> artifacts) : IDisposable
    {
        public static ArtifactLease Empty => new([]);
        public int Count => artifacts.Count;
        public IReadOnlyList<string> Paths => artifacts.Select(artifact => artifact.Path).ToArray();

        public async Task VerifyUnchangedAsync(CancellationToken token)
        {
            foreach (var artifact in artifacts)
            {
                if (artifact.Stream.Length != artifact.Length)
                    throw new TrustedToolchainValidationException("arduino_artifact_changed", "A staged Arduino artifact changed before upload.");
                artifact.Stream.Position = 0;
                var hash = await SHA256.HashDataAsync(artifact.Stream, token).ConfigureAwait(false);
                artifact.Stream.Position = 0;
                if (!CryptographicOperations.FixedTimeEquals(hash, artifact.Hash))
                    throw new TrustedToolchainValidationException("arduino_artifact_changed", "A staged Arduino artifact changed before upload.");
            }
        }

        public void Dispose() { foreach (var artifact in artifacts) artifact.Stream.Dispose(); }
    }
}
