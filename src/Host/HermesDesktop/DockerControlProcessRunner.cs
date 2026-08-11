using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HermesDesktop;

internal sealed partial class DockerControlProcessRunner : IDockerControlRunner
{
    private const int MaximumCommandOutputCharacters = 256 * 1024;
    private const int MaximumLogOutputCharacters = 64 * 1024;
    private const long MaximumTrustedConfigurationBytes = 1024 * 1024;
    private const string RuntimeProtocol = "docker-control/v1";
    private readonly string _trustedRoot;
    private readonly string _composePath;
    private readonly string _dockerExecutable;

    internal DockerControlProcessRunner(string trustedStackRoot)
    {
        if (string.IsNullOrWhiteSpace(trustedStackRoot)) throw new ArgumentException("A trusted stack root is required.", nameof(trustedStackRoot));
        _trustedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trustedStackRoot));
        _composePath = Path.Combine(_trustedRoot, "docker-compose.yml");
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        _dockerExecutable = Path.GetFullPath(Path.Combine(programFiles, "Docker", "Docker", "resources", "bin", "docker.exe"));
    }

    public async Task<DockerControlHostSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        var observedAt = DateTimeOffset.UtcNow;
        if (!IsTrustedFile(_composePath) || !IsTrustedExecutable(_dockerExecutable))
            return UnavailableSnapshot(observedAt, File.Exists(_dockerExecutable) ? "unavailable" : "unavailable");

        var engine = await RunAsync(DockerCliOperation.EngineVersion, null, 0, cancellationToken).ConfigureAwait(false);
        if (engine.ExitCode != 0)
            return UnavailableSnapshot(observedAt, "unavailable");

        var engineVersion = SafeIdentity(engine.StandardOutput);
        var configured = await ReadConfiguredServicesAsync(cancellationToken).ConfigureAwait(false);
        var services = new List<DockerControlServiceEvidence>();
        var volumes = new Dictionary<string, DockerControlVolumeEvidence>(StringComparer.Ordinal)
        {
            ["data"] = new("data", "unknown", true),
            ["workspace"] = new("workspace", "unknown", true),
        };

        if (configured.Contains("gateway"))
            services.Add(await ReadComposeServiceAsync(DockerControlService.Hermes, "gateway", ApprovedHermesDigest(), volumes, cancellationToken).ConfigureAwait(false));
        else
            services.Add(UnavailableService(DockerControlService.Hermes));

        if (configured.Contains("memory-vector"))
            services.Add(await ReadComposeServiceAsync(DockerControlService.MemoryVector, "memory-vector", ApprovedServiceDigest("memory-vector"), volumes, cancellationToken).ConfigureAwait(false));
        else
            services.Add(UnavailableService(DockerControlService.MemoryVector));

        services.Add(SerenaEvidence());
        var modelRunner = await ReadModelRunnerAsync(cancellationToken).ConfigureAwait(false);

        var composeState = services.Any(service => service.Manageable && service.State == "degraded")
            ? "degraded"
            : services.Any(service => service.Manageable && service.State == "running")
                ? "running"
                : configured.Count > 0 ? "stopped" : "unavailable";
        return new DockerControlHostSnapshot(
            observedAt,
            "running",
            engineVersion,
            composeState,
            HashFile(_composePath),
            services.FirstOrDefault(service => service.Id == DockerControlService.Hermes)?.Image?.OciRevision,
            RuntimeProtocol,
            services,
            volumes.Values.OrderBy(volume => volume.Role, StringComparer.Ordinal).ToArray(),
            modelRunner,
            null);
    }

    public async Task<DockerControlLogs> ReadLogsAsync(DockerControlService service, int maximumLines, CancellationToken cancellationToken)
    {
        if (maximumLines is < 1 or > 200) throw new DockerControlUnavailableException("invalid_log_limit", "The Docker log limit is invalid.");
        if (service == DockerControlService.Serena)
            throw new DockerControlUnavailableException("service_unavailable", "Serena is supervised separately and Docker logs are unavailable here.");
        var composeService = ComposeService(service);
        if (!await IsComposeServiceConfiguredAsync(composeService, cancellationToken).ConfigureAwait(false))
            throw new DockerControlUnavailableException("service_unavailable", "The requested product service is not configured.");
        var result = await RunAsync(DockerCliOperation.Logs, service, maximumLines, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new DockerControlUnavailableException("logs_unavailable", "Bounded Docker logs are unavailable.");
        var entries = SplitLines(result.StandardOutput)
            .Take(maximumLines)
            .Select(line => new DockerControlLogLine(null, "stdout", line))
            .Concat(SplitLines(result.StandardError).Take(maximumLines).Select(line => new DockerControlLogLine(null, "stderr", line)))
            .Take(maximumLines)
            .ToArray();
        return new DockerControlLogs(entries, result.Truncated || entries.Length >= maximumLines);
    }

    public async Task<DockerControlMutationOutcome> ExecuteAsync(DockerControlMutation mutation, CancellationToken cancellationToken)
    {
        if (mutation.Kind is DockerControlMutationKind.StartService or DockerControlMutationKind.StopService or DockerControlMutationKind.RestartService && mutation.Service is null)
            throw new DockerControlUnavailableException("invalid_mutation", "The reviewed service target is invalid.");
        if (mutation.Kind == DockerControlMutationKind.UnloadModel)
        {
            if (!SafeModelReference(mutation.Model, out var model))
                throw new DockerControlUnavailableException("invalid_mutation", "The reviewed model target is invalid.");
            var modelResult = await RunAsync(DockerCliOperation.ModelUnload, DockerControlService.ModelRunner, 0, cancellationToken, modelReference: model).ConfigureAwait(false);
            return new(modelResult.ExitCode == 0, modelResult.ExitCode == 0 ? "The reviewed model was unloaded." : "Docker Model Runner could not unload the reviewed model.");
        }
        if (mutation.Targets.Count == 0 || mutation.Targets.Any(target => target == DockerControlService.Serena))
            throw new DockerControlUnavailableException("service_unavailable", "A reviewed Docker target is unavailable.");
        foreach (var target in mutation.Targets)
        {
            if (!await IsComposeServiceConfiguredAsync(ComposeService(target), cancellationToken).ConfigureAwait(false))
                throw new DockerControlUnavailableException("service_unavailable", "A reviewed Docker target is no longer configured.");
        }

        var operation = mutation.Kind switch
        {
            DockerControlMutationKind.StartStack => DockerCliOperation.Start,
            DockerControlMutationKind.StopStack => DockerCliOperation.Stop,
            DockerControlMutationKind.StartService => DockerCliOperation.Start,
            DockerControlMutationKind.StopService => DockerCliOperation.Stop,
            DockerControlMutationKind.RestartService => DockerCliOperation.Restart,
            _ => throw new DockerControlUnavailableException("invalid_mutation", "The reviewed Docker operation is invalid."),
        };
        var result = await RunAsync(operation, mutation.Service, 0, cancellationToken, mutation.Targets).ConfigureAwait(false);
        var succeeded = result.ExitCode == 0;
        return new DockerControlMutationOutcome(
            succeeded,
            succeeded ? "The reviewed Docker operation completed." : "The reviewed Docker operation failed.");
    }

    private async Task<HashSet<string>> ReadConfiguredServicesAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync(DockerCliOperation.ComposeServices, null, 0, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) return [];
        return SplitLines(result.StandardOutput)
            .Where(service => service is "gateway" or "memory-vector")
            .ToHashSet(StringComparer.Ordinal);
    }

    private async Task<bool> IsComposeServiceConfiguredAsync(string service, CancellationToken cancellationToken) =>
        (await ReadConfiguredServicesAsync(cancellationToken).ConfigureAwait(false)).Contains(service);

    private async Task<DockerControlServiceEvidence> ReadComposeServiceAsync(
        DockerControlService id,
        string composeService,
        string? approvedDigest,
        IDictionary<string, DockerControlVolumeEvidence> volumes,
        CancellationToken cancellationToken)
    {
        var ps = await RunAsync(DockerCliOperation.ComposePs, id, 0, cancellationToken).ConfigureAwait(false);
        var state = "stopped";
        var health = "unknown";
        if (ps.ExitCode == 0 && TryFirstJsonObject(ps.StandardOutput, out var psObject))
        {
            state = NormalizeState(GetString(psObject, "State"));
            health = NormalizeHealth(GetString(psObject, "Health"));
        }

        var containerIdResult = await RunAsync(DockerCliOperation.ComposeContainerId, id, 0, cancellationToken).ConfigureAwait(false);
        var containerId = containerIdResult.ExitCode == 0 ? SafeContainerId(containerIdResult.StandardOutput.Trim()) : null;
        DockerControlImage? image = approvedDigest is null ? new(null, null, null, "unverified") : new(null, approvedDigest, null, "unverified");
        IReadOnlyList<DockerControlPort> ports = [];
        DockerControlResources? resources = null;
        if (containerId is not null)
        {
            var inspect = await RunAsync(DockerCliOperation.ContainerInspect, id, 0, cancellationToken, containerId: containerId).ConfigureAwait(false);
            if (inspect.ExitCode == 0 && TryFirstJsonObject(inspect.StandardOutput, out var container))
            {
                var imageId = NormalizeSha256(GetString(container, "Image"));
                ports = ReadPorts(container);
                ReadVolumes(container, volumes);
                if (imageId is not null)
                {
                    var imageInspect = await RunAsync(DockerCliOperation.ImageInspect, id, 0, cancellationToken, imageId: imageId).ConfigureAwait(false);
                    if (imageInspect.ExitCode == 0 && TryFirstJsonObject(imageInspect.StandardOutput, out var inspectedImage))
                    {
                        var revision = ReadImageRevision(inspectedImage);
                        var verified = approvedDigest is not null && ReadRepoDigests(inspectedImage).Any(digest => digest.EndsWith(approvedDigest, StringComparison.OrdinalIgnoreCase));
                        image = new(imageId, approvedDigest, revision, approvedDigest is null ? "unverified" : verified ? "verified" : "mismatch");
                    }
                }
            }
            var stats = await RunAsync(DockerCliOperation.ContainerStats, id, 0, cancellationToken, containerId: containerId).ConfigureAwait(false);
            if (stats.ExitCode == 0 && TryFirstJsonObject(stats.StandardOutput, out var statsObject))
                resources = ReadResources(statsObject);
        }
        return new DockerControlServiceEvidence(id, state, health, containerId, null, image, ports, resources, true);
    }

    private DockerControlServiceEvidence SerenaEvidence()
    {
        var settingsPath = Path.Combine(_trustedRoot, "launcher.settings.json");
        var configured = false;
        try
        {
            if (IsTrustedFile(settingsPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(settingsPath));
                configured = document.RootElement.TryGetProperty("SerenaExecutable", out var executable)
                    && executable.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(executable.GetString());
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { }
        return new DockerControlServiceEvidence(
            DockerControlService.Serena,
            configured ? "unknown" : "unavailable",
            "unknown",
            null,
            null,
            null,
            [],
            null,
            false);
    }

    private string? ApprovedHermesDigest() => ApprovedServiceDigest("gateway");

    private string? ApprovedServiceDigest(string service)
    {
        try
        {
            if (!IsTrustedFile(_composePath) || service is not ("gateway" or "memory-vector")) return null;
            var insideService = false;
            foreach (var line in File.ReadLines(_composePath))
            {
                if (line.Equals($"  {service}:", StringComparison.Ordinal)) { insideService = true; continue; }
                if (!insideService) continue;
                if (line.Length >= 2 && line[0] == ' ' && line[1] == ' ' && (line.Length == 2 || line[2] != ' ')) return null;
                if (!line.TrimStart().StartsWith("image:", StringComparison.Ordinal)) continue;
                var match = ApprovedDigestRegex().Match(line);
                return match.Success ? $"sha256:{match.Groups[1].Value.ToLowerInvariant()}" : null;
            }
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    private async Task<DockerControlModelRunner> ReadModelRunnerAsync(CancellationToken cancellationToken)
    {
        var status = await RunAsync(DockerCliOperation.ModelStatus, null, 0, cancellationToken).ConfigureAwait(false);
        if (status.ExitCode != 0 || !TryFirstJsonObject(status.StandardOutput, out var statusObject))
            return new("unavailable", null, null, null, null, false, [], "Docker Model Runner is not installed, stopped, or unavailable.");

        var running = statusObject.TryGetProperty("running", out var runningValue) && runningValue.ValueKind is JsonValueKind.True;
        var endpoint = SafeLoopbackEndpoint(GetString(statusObject, "endpointHost"));
        var kind = SafeDisplay(GetString(statusObject, "kind"), 64);
        var versionResult = await RunAsync(DockerCliOperation.ModelVersion, null, 0, cancellationToken).ConfigureAwait(false);
        var version = versionResult.ExitCode == 0 ? ReadModelRunnerVersion(versionResult.StandardOutput) : null;
        var list = await RunAsync(DockerCliOperation.ModelList, null, 0, cancellationToken).ConfigureAwait(false);
        var pulled = list.ExitCode == 0 ? ReadPulledModels(list.StandardOutput) : [];
        var ps = await RunAsync(DockerCliOperation.ModelPs, null, 0, cancellationToken).ConfigureAwait(false);
        var loaded = ps.ExitCode == 0 ? ReadLoadedModels(ps.StandardOutput) : [];
        var models = MergeModels(pulled, loaded);
        var disk = await RunAsync(DockerCliOperation.ModelDf, null, 0, cancellationToken).ConfigureAwait(false);
        var diskUsage = disk.ExitCode == 0 ? ReadModelDiskUsage(disk.StandardOutput) : null;
        return new(
            running ? "running" : "stopped",
            version,
            endpoint,
            kind,
            diskUsage,
            running && models.Any(model => model.Loaded),
            models,
            running ? "Docker Model Runner is available. Loading is not exposed because this CLI has no bounded load-only authority." : "Docker Model Runner is installed but not running.");
    }

    private static IReadOnlyList<DockerControlModel> ReadPulledModels(string json)
    {
        var result = new List<DockerControlModel>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
            foreach (var item in document.RootElement.EnumerateArray().Take(64))
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array) continue;
                var reference = tags.EnumerateArray().Where(tag => tag.ValueKind == JsonValueKind.String).Select(tag => tag.GetString()).FirstOrDefault(value => SafeModelReference(value, out _));
                if (!SafeModelReference(reference, out var safeReference)) continue;
                var modelId = NormalizeSha256(GetString(item, "id"));
                var config = item.TryGetProperty("config", out var candidateConfig) && candidateConfig.ValueKind == JsonValueKind.Object ? candidateConfig : default;
                result.Add(new(
                    safeReference,
                    modelId,
                    config.ValueKind == JsonValueKind.Object ? SafeDisplay(GetString(config, "size"), 64) : null,
                    config.ValueKind == JsonValueKind.Object ? SafeIdentity(GetString(config, "format") ?? string.Empty) : null,
                    config.ValueKind == JsonValueKind.Object ? SafeDisplay(GetString(config, "parameters"), 64) : null,
                    false,
                    null,
                    null));
            }
        }
        catch (JsonException) { return []; }
        return result;
    }

    private static IReadOnlyList<DockerControlModel> ReadLoadedModels(string table)
    {
        var result = new List<DockerControlModel>();
        foreach (var line in SplitLines(table).Skip(1).Take(64))
        {
            var columns = Regex.Split(line.Trim(), "\\s{2,}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
            if (columns.Length < 3 || !SafeModelReference(columns[0], out var reference)) continue;
            result.Add(new(reference, null, null, null, null, true, SafeIdentity(columns[1]), SafeIdentity(columns[2])));
        }
        return result;
    }

    private static IReadOnlyList<DockerControlModel> MergeModels(IReadOnlyList<DockerControlModel> pulled, IReadOnlyList<DockerControlModel> loaded)
    {
        var result = new List<DockerControlModel>();
        foreach (var model in pulled)
        {
            var active = loaded.FirstOrDefault(candidate => ModelNamesMatch(model.Reference, candidate.Reference));
            result.Add(active is null ? model : model with { Loaded = true, Backend = active.Backend, Mode = active.Mode });
        }
        foreach (var model in loaded.Where(active => !pulled.Any(local => ModelNamesMatch(local.Reference, active.Reference)))) result.Add(model);
        return result.Take(64).ToArray();
    }

    private static bool ModelNamesMatch(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
        || string.Equals(left[(left.LastIndexOf('/') + 1)..], right[(right.LastIndexOf('/') + 1)..], StringComparison.OrdinalIgnoreCase);

    private static string? ReadModelRunnerVersion(string value)
    {
        var server = Regex.Match(value, @"(?ms)^Server:\s*.*?^\s*Version:\s*(\S+)", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
        return server.Success ? SafeIdentity(server.Groups[1].Value) : null;
    }

    private static string? ReadModelDiskUsage(string value)
    {
        var line = SplitLines(value).FirstOrDefault(candidate => candidate.StartsWith("Models", StringComparison.OrdinalIgnoreCase));
        if (line is null) return null;
        var columns = Regex.Split(line.Trim(), "\\s{2,}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
        return columns.Length >= 2 ? SafeDisplay(columns[1], 64) : null;
    }

    private async Task<DockerProcessResult> RunAsync(
        DockerCliOperation operation,
        DockerControlService? service,
        int maximumLines,
        CancellationToken cancellationToken,
        IReadOnlyList<DockerControlService>? targets = null,
        string? containerId = null,
        string? imageId = null,
        string? modelReference = null)
    {
        if (!IsTrustedExecutable(_dockerExecutable) || !IsTrustedFile(_composePath))
            throw new DockerControlUnavailableException("docker_unavailable", "The fixed Docker runtime is unavailable.");
        var start = new ProcessStartInfo
        {
            FileName = _dockerExecutable,
            WorkingDirectory = _trustedRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        AddArguments(start.ArgumentList, operation, service, maximumLines, targets, containerId, imageId, modelReference);
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new DockerControlUnavailableException("docker_unavailable", "The fixed Docker runtime could not start.");
            var maximum = operation == DockerCliOperation.Logs ? MaximumLogOutputCharacters : MaximumCommandOutputCharacters;
            var stdout = ReadBoundedAsync(process.StandardOutput, maximum, cancellationToken);
            var stderr = ReadBoundedAsync(process.StandardError, maximum, cancellationToken);
            try { await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                TryKill(process);
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
                throw;
            }
            var output = await stdout.ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);
            return new DockerProcessResult(process.ExitCode, output.Text, error.Text, output.Truncated || error.Truncated);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            throw new DockerControlUnavailableException("docker_unavailable", "The fixed Docker runtime is unavailable.");
        }
    }

    private void AddArguments(
        Collection<string> arguments,
        DockerCliOperation operation,
        DockerControlService? service,
        int maximumLines,
        IReadOnlyList<DockerControlService>? targets,
        string? containerId,
        string? imageId,
        string? modelReference)
    {
        if (operation == DockerCliOperation.EngineVersion)
        {
            arguments.Add("version"); arguments.Add("--format"); arguments.Add("{{.Server.Version}}"); return;
        }
        if (operation == DockerCliOperation.ContainerInspect)
        {
            if (SafeContainerId(containerId) is null) throw new DockerControlUnavailableException("invalid_identity", "Docker returned an invalid container identity.");
            arguments.Add("inspect"); arguments.Add("--type"); arguments.Add("container"); arguments.Add(containerId!); return;
        }
        if (operation == DockerCliOperation.ImageInspect)
        {
            if (NormalizeSha256(imageId) is null) throw new DockerControlUnavailableException("invalid_identity", "Docker returned an invalid image identity.");
            arguments.Add("image"); arguments.Add("inspect"); arguments.Add(imageId!); return;
        }
        if (operation == DockerCliOperation.ContainerStats)
        {
            if (SafeContainerId(containerId) is null) throw new DockerControlUnavailableException("invalid_identity", "Docker returned an invalid container identity.");
            arguments.Add("stats"); arguments.Add("--no-stream"); arguments.Add("--format"); arguments.Add("{{json .}}"); arguments.Add(containerId!); return;
        }
        if (operation is DockerCliOperation.ModelStatus or DockerCliOperation.ModelVersion or DockerCliOperation.ModelList or DockerCliOperation.ModelPs or DockerCliOperation.ModelDf or DockerCliOperation.ModelUnload)
        {
            arguments.Add("model");
            switch (operation)
            {
                case DockerCliOperation.ModelStatus: arguments.Add("status"); arguments.Add("--json"); break;
                case DockerCliOperation.ModelVersion: arguments.Add("version"); break;
                case DockerCliOperation.ModelList: arguments.Add("list"); arguments.Add("--json"); break;
                case DockerCliOperation.ModelPs: arguments.Add("ps"); break;
                case DockerCliOperation.ModelDf: arguments.Add("df"); break;
                case DockerCliOperation.ModelUnload:
                    if (!SafeModelReference(modelReference, out var model)) throw new DockerControlUnavailableException("invalid_identity", "The reviewed model identity is invalid.");
                    arguments.Add("unload"); arguments.Add(model); break;
            }
            return;
        }
        arguments.Add("compose");
        arguments.Add("--project-directory"); arguments.Add(_trustedRoot);
        arguments.Add("--file"); arguments.Add(_composePath);
        switch (operation)
        {
            case DockerCliOperation.ComposeServices:
                arguments.Add("config"); arguments.Add("--services"); break;
            case DockerCliOperation.ComposePs:
                arguments.Add("ps"); arguments.Add("--all"); arguments.Add("--format"); arguments.Add("json"); arguments.Add(ComposeService(service)); break;
            case DockerCliOperation.ComposeContainerId:
                arguments.Add("ps"); arguments.Add("--quiet"); arguments.Add(ComposeService(service)); break;
            case DockerCliOperation.Logs:
                arguments.Add("logs"); arguments.Add("--no-color"); arguments.Add("--no-log-prefix"); arguments.Add("--tail"); arguments.Add(maximumLines.ToString(CultureInfo.InvariantCulture)); arguments.Add(ComposeService(service)); break;
            case DockerCliOperation.Start:
                arguments.Add("up"); arguments.Add("--detach"); arguments.Add("--no-build"); arguments.Add("--pull"); arguments.Add("never"); arguments.Add("--no-deps"); AddTargets(arguments, targets); break;
            case DockerCliOperation.Stop:
                arguments.Add("stop"); arguments.Add("--timeout"); arguments.Add("20"); AddTargets(arguments, targets); break;
            case DockerCliOperation.Restart:
                arguments.Add("restart"); arguments.Add("--timeout"); arguments.Add("20"); arguments.Add(ComposeService(service)); break;
            default: throw new DockerControlUnavailableException("invalid_operation", "The fixed Docker operation is invalid.");
        }
    }

    private static void AddTargets(Collection<string> arguments, IReadOnlyList<DockerControlService>? targets)
    {
        if (targets is null || targets.Count is 0 or > 3) throw new DockerControlUnavailableException("invalid_target", "The reviewed Docker target set is invalid.");
        foreach (var target in targets.OrderBy(value => value)) arguments.Add(ComposeService(target));
    }

    private static string ComposeService(DockerControlService? service) => service switch
    {
        DockerControlService.Hermes => "gateway",
        DockerControlService.MemoryVector => "memory-vector",
        _ => throw new DockerControlUnavailableException("service_unavailable", "The requested service is not managed by Docker Control Center."),
    };

    private bool IsTrustedFile(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(_trustedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) return false;
            var info = new FileInfo(full);
            return info.Length <= MaximumTrustedConfigurationBytes && !TraversesReparsePoint(full);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private static bool IsTrustedExecutable(string path)
    {
        try { return Path.IsPathFullyQualified(path) && File.Exists(path) && !TraversesReparsePoint(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private DockerControlHostSnapshot UnavailableSnapshot(DateTimeOffset observedAt, string engineState) => new(
        observedAt, engineState, null, "unavailable", IsTrustedFile(_composePath) ? HashFile(_composePath) : null, null,
        RuntimeProtocol,
        [UnavailableService(DockerControlService.Hermes), UnavailableService(DockerControlService.MemoryVector), SerenaEvidence()],
        [new("data", "unknown", true), new("workspace", "unknown", true)],
        new("unavailable", null, null, null, null, false, [], "Docker Model Runner status is unavailable while the Docker engine is unavailable."),
        null);

    private static DockerControlServiceEvidence UnavailableService(DockerControlService service) =>
        new(service, "unavailable", "unknown", null, null, null, [], null, false);

    private static string NormalizeState(string? value) => value?.ToLowerInvariant() switch
    {
        "running" => "running",
        "exited" or "dead" or "created" => "stopped",
        "restarting" or "paused" or "removing" => "degraded",
        _ => "unknown",
    };

    private static string NormalizeHealth(string? value) => value?.ToLowerInvariant() switch
    {
        "healthy" => "healthy",
        "unhealthy" => "unhealthy",
        "starting" => "starting",
        "" or null => "not-configured",
        _ => "unknown",
    };

    private static string? SafeIdentity(string value)
    {
        var normalized = value.Trim();
        return normalized.Length is > 0 and <= 128 && normalized.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '+' or ':' or '/' or '-') ? normalized : null;
    }

    private static string? SafeContainerId(string? value) =>
        value is not null && value.Length is >= 12 and <= 64 && value.All(Uri.IsHexDigit) ? value.ToLowerInvariant() : null;

    private static string? NormalizeSha256(string? value) =>
        value is not null && Sha256Regex().IsMatch(value) ? value.ToLowerInvariant() : null;

    private static bool SafeModelReference(string? value, out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 and <= 256
            && char.IsAsciiLetterOrDigit(normalized[0])
            && normalized.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '/' or ':' or '@' or '+' or '-');
    }

    private static string? SafeLoopbackEndpoint(string? value)
    {
        if (value is null || value.Length > 256 || !Uri.TryCreate(value, UriKind.Absolute, out var endpoint)) return null;
        return endpoint.Scheme is "http" or "https" && endpoint.IsLoopback ? endpoint.AbsoluteUri : null;
    }

    private static string? SafeDisplay(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        return normalized.Length <= maximum && normalized.All(character => char.IsAsciiLetterOrDigit(character) || character is ' ' or '.' or '_' or '+' or ':' or '/' or '@' or '(' or ')' or '-') ? normalized : null;
    }

    private static string HashFile(string path) => $"sha256:{Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))}";

    private static IReadOnlyList<string> SplitLines(string value) => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool TryFirstJsonObject(string value, out JsonElement element)
    {
        element = default;
        try
        {
            using var document = JsonDocument.Parse(value);
            var candidate = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().FirstOrDefault()
                : document.RootElement;
            if (candidate.ValueKind != JsonValueKind.Object) return false;
            element = candidate.Clone();
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static IReadOnlyList<DockerControlPort> ReadPorts(JsonElement container)
    {
        var result = new List<DockerControlPort>();
        if (!container.TryGetProperty("NetworkSettings", out var network) || network.ValueKind != JsonValueKind.Object
            || !network.TryGetProperty("Ports", out var ports) || ports.ValueKind != JsonValueKind.Object) return result;
        foreach (var mapping in ports.EnumerateObject())
        {
            var split = mapping.Name.Split('/');
            if (split.Length != 2 || !int.TryParse(split[0], NumberStyles.None, CultureInfo.InvariantCulture, out var containerPort)
                || containerPort is < 1 or > 65535 || split[1] is not ("tcp" or "udp") || mapping.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in mapping.Value.EnumerateArray())
            {
                var address = GetString(item, "HostIp");
                var hostPortText = GetString(item, "HostPort");
                if (address is not ("127.0.0.1" or "::1") || !int.TryParse(hostPortText, NumberStyles.None, CultureInfo.InvariantCulture, out var hostPort)
                    || hostPort is < 1 or > 65535) continue;
                result.Add(new(address, hostPort, containerPort, split[1]));
            }
        }
        return result.Take(16).ToArray();
    }

    private static void ReadVolumes(JsonElement container, IDictionary<string, DockerControlVolumeEvidence> volumes)
    {
        if (!container.TryGetProperty("Mounts", out var mounts) || mounts.ValueKind != JsonValueKind.Array) return;
        foreach (var mount in mounts.EnumerateArray())
        {
            var destination = GetString(mount, "Destination");
            if (destination == "/opt/data") volumes["data"] = new("data", "mounted", true);
            else if (destination == "/workspace") volumes["workspace"] = new("workspace", "mounted", true);
        }
    }

    private static string? ReadImageRevision(JsonElement image)
    {
        if (!image.TryGetProperty("Config", out var config) || config.ValueKind != JsonValueKind.Object
            || !config.TryGetProperty("Labels", out var labels) || labels.ValueKind != JsonValueKind.Object) return null;
        return labels.TryGetProperty("org.opencontainers.image.revision", out var revision) && revision.ValueKind == JsonValueKind.String
            ? SafeIdentity(revision.GetString() ?? string.Empty) : null;
    }

    private static IReadOnlyList<string> ReadRepoDigests(JsonElement image)
    {
        if (!image.TryGetProperty("RepoDigests", out var values) || values.ValueKind != JsonValueKind.Array) return [];
        return values.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).Take(32).ToArray();
    }

    private static DockerControlResources? ReadResources(JsonElement stats)
    {
        static double? Percent(JsonElement element, string name)
        {
            var raw = GetString(element, name)?.Trim().TrimEnd('%');
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value is >= 0 and <= 1_000_000 ? value : null;
        }
        var memoryParts = (GetString(stats, "MemUsage") ?? string.Empty).Split(" / ", StringSplitOptions.TrimEntries);
        var memoryUsage = memoryParts.Length > 0 ? SafeDisplay(memoryParts[0], 64) : null;
        var memoryLimit = memoryParts.Length > 1 ? SafeDisplay(memoryParts[1], 64) : null;
        var pids = int.TryParse(GetString(stats, "PIDs"), NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPids) && parsedPids >= 0 ? parsedPids : null as int?;
        var resources = new DockerControlResources(
            Percent(stats, "CPUPerc"),
            memoryUsage,
            memoryLimit,
            Percent(stats, "MemPerc"),
            SafeDisplay(GetString(stats, "NetIO"), 64),
            SafeDisplay(GetString(stats, "BlockIO"), 64),
            pids);
        return resources.CpuPercent is null && resources.MemoryUsage is null && resources.MemoryPercent is null
            && resources.NetworkIo is null && resources.BlockIo is null && resources.Pids is null ? null : resources;
    }

    private static async Task<BoundedText> ReadBoundedAsync(StreamReader reader, int maximumCharacters, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(Math.Min(maximumCharacters, 8_192));
        var buffer = new char[4_096];
        var truncated = false;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            var remaining = maximumCharacters - builder.Length;
            if (remaining > 0) builder.Append(buffer, 0, Math.Min(remaining, read));
            if (read > remaining) truncated = true;
        }
        return new(builder.ToString(), truncated);
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private static bool TraversesReparsePoint(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }
        return false;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private enum DockerCliOperation
    {
        EngineVersion, ComposeServices, ComposePs, ComposeContainerId, ContainerInspect, ImageInspect, ContainerStats,
        ModelStatus, ModelVersion, ModelList, ModelPs, ModelDf, ModelUnload,
        Logs, Start, Stop, Restart,
    }
    private sealed record DockerProcessResult(int ExitCode, string StandardOutput, string StandardError, bool Truncated);
    private sealed record BoundedText(string Text, bool Truncated);

    [GeneratedRegex("^sha256:[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Sha256Regex();

    [GeneratedRegex("@sha256:([a-fA-F0-9]{64})", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex ApprovedDigestRegex();
}
