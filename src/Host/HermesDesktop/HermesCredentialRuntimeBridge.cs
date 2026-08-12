using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HermesCredentialBroker.Runtime;

namespace HermesDesktop;

internal sealed partial class HermesCredentialRuntimeBridge : IAsyncDisposable
{
    private const string ProfilePath = "/run/photon-credentials/profile.json";
    private const string BootstrapPath = "/run/photon-credentials/bootstrap.bin";
    private const int MaximumDockerOutputCharacters = 64 * 1024;
    internal const int CredentialGatewayPort = 9119;
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SessionRenewalInterval = TimeSpan.FromMinutes(8);
    private static readonly TimeSpan InspectionInterval = TimeSpan.FromSeconds(2);
    private const string WriteProfileScript =
        "umask 077; install -d -m 700 -o 10000 -g 10000 /run/photon-credentials; " +
        "t=/run/photon-credentials/.profile.tmp; rm -f \"$t\"; cat > \"$t\"; " +
        "chown 10000:10000 \"$t\"; chmod 600 \"$t\"; mv -f \"$t\" " + ProfilePath;
    private const string WriteBootstrapScript =
        "umask 077; install -d -m 700 -o 10000 -g 10000 /run/photon-credentials; " +
        "t=/run/photon-credentials/.bootstrap.tmp; rm -f \"$t\"; cat > \"$t\"; " +
        "chown 10000:10000 \"$t\"; chmod 600 \"$t\"; mv -f \"$t\" " + BootstrapPath;
    private const string CleanupScript =
        "rm -f /run/photon-credentials/.profile.tmp /run/photon-credentials/.bootstrap.tmp " +
        ProfilePath + " " + BootstrapPath;

    private readonly string _installRoot;
    private readonly Uri _workbenchUri;
    private readonly HermesConnectionsBridge _connections;
    private readonly string _dockerExecutable;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _activeCancellation;
    private Task _activeTask = Task.CompletedTask;
    private bool _disposed;

    internal HermesCredentialRuntimeBridge(
        string installRoot,
        Uri workbenchUri,
        HermesConnectionsBridge connections)
    {
        _installRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
        _workbenchUri = workbenchUri ?? throw new ArgumentNullException(nameof(workbenchUri));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        _dockerExecutable = Path.GetFullPath(Path.Combine(programFiles, "Docker", "Docker", "resources", "bin", "docker.exe"));
    }

    internal async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StopCoreAsync().ConfigureAwait(false);
            var bindings = await _connections.GetRuntimeBindingsAsync(cancellationToken).ConfigureAwait(false);
            if (bindings.Count == 0) return;

            var runtime = await InspectRuntimeAsync(cancellationToken).ConfigureAwait(false);
            var expiresAt = DateTimeOffset.UtcNow.Add(SessionLifetime);
            CredentialRuntimeBootstrap? bootstrap = CredentialRuntimeBootstrap.Create(new CredentialContainerBinding(
                runtime.ContainerId,
                runtime.ImageId,
                _connections.Principal,
                expiresAt));
            var profileBytes = SerializeProfile(runtime, bootstrap.SessionId, expiresAt, bindings);
            try
            {
                await ExecInputAsync(runtime.ContainerId, WriteProfileScript, profileBytes, cancellationToken).ConfigureAwait(false);
                await ExecBootstrapAsync(runtime.ContainerId, bootstrap, cancellationToken).ConfigureAwait(false);
                var reinspection = await InspectRuntimeAsync(cancellationToken).ConfigureAwait(false);
                if (!runtime.ExactlyMatches(reinspection))
                    throw new CredentialRuntimeException("container_binding_changed", "The inspected Hermes runtime changed during credential bootstrap.");

                var active = new CancellationTokenSource();
                _activeCancellation = active;
                _activeTask = RunSessionAsync(reinspection, bootstrap, active.Token);
                _ = RenewSessionAsync(active);
                bootstrap = null;
                return;
            }
            catch
            {
                await CleanupAsync(runtime.ContainerId).ConfigureAwait(false);
                throw;
            }
            finally
            {
                bootstrap?.Dispose();
                CryptographicOperations.ZeroMemory(profileBytes);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RunSessionAsync(
        VerifiedHermesRuntime runtime,
        CredentialRuntimeBootstrap bootstrap,
        CancellationToken cancellationToken)
    {
        using var ownedBootstrap = bootstrap;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var client = new CredentialRuntimeClient(() =>
                new DockerExecCredentialRuntimeSocket(_dockerExecutable, runtime.ContainerId));
            var endpoint = BuildCredentialEndpoint(_workbenchUri, bootstrap.SessionId);
            var channel = client.RunAsync(endpoint, bootstrap, _connections.LeaseResolver, linked.Token);
            var monitor = MonitorIdentityAsync(runtime, linked);
            await Task.WhenAny(channel, monitor).ConfigureAwait(false);
            linked.Cancel();
            await channel.ConfigureAwait(false);
            await monitor.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            var code = exception is CredentialRuntimeException runtimeException
                ? runtimeException.Code
                : exception.GetType().Name;
            DesktopLog.Write($"Native credential runtime stopped safely: {code}");
        }
        finally
        {
            await CleanupAsync(runtime.ContainerId).ConfigureAwait(false);
        }
    }

    private async Task RenewSessionAsync(CancellationTokenSource owner)
    {
        try
        {
            await Task.Delay(SessionRenewalInterval, owner.Token).ConfigureAwait(false);
            if (!owner.IsCancellationRequested) await RestartAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (owner.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            DesktopLog.Write($"Native credential runtime renewal failed safely: {exception.GetType().Name}");
        }
    }

    private async Task MonitorIdentityAsync(VerifiedHermesRuntime expected, CancellationTokenSource cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            await Task.Delay(InspectionInterval, cancellation.Token).ConfigureAwait(false);
            VerifiedHermesRuntime current;
            try { current = await InspectRuntimeAsync(cancellation.Token).ConfigureAwait(false); }
            catch { cancellation.Cancel(); return; }
            if (!expected.ExactlyMatches(current)) { cancellation.Cancel(); return; }
        }
    }

    internal static Uri BuildCredentialEndpoint(Uri workbenchUri, string sessionId)
    {
        if (!DesktopOptions.IsTrustedWorkbenchUri(workbenchUri))
            throw new CredentialRuntimeException("endpoint_untrusted", "The Workbench credential endpoint is not trusted.");
        var builder = new UriBuilder(workbenchUri)
        {
            Scheme = workbenchUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Host = "127.0.0.1",
            Port = CredentialGatewayPort,
            Path = "/api/workbench/credentials/v2",
            Query = $"session={Uri.EscapeDataString(sessionId)}",
            Fragment = string.Empty,
        };
        return builder.Uri;
    }

    private byte[] SerializeProfile(
        VerifiedHermesRuntime runtime,
        string sessionId,
        DateTimeOffset expiresAt,
        IReadOnlyList<CredentialRuntimeBindingMetadata> bindings)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", "photon.workbench.credential-profile/v1");
            writer.WriteString("sessionId", sessionId);
            writer.WriteString("containerId", runtime.ContainerId);
            writer.WriteString("imageDigest", runtime.ImageId);
            writer.WriteString("profileId", _connections.Principal.ProfileId);
            writer.WriteNumber("expiresAtUnix", expiresAt.ToUnixTimeSeconds());
            writer.WriteStartObject("env");
            foreach (var binding in bindings.OrderBy(entry => entry.EnvironmentName, StringComparer.Ordinal))
            {
                writer.WriteStartObject(binding.EnvironmentName);
                writer.WriteString("connection_ref", binding.ConnectionReference);
                writer.WriteString("purpose", binding.Purpose);
                writer.WriteNumber("revision", binding.Revision);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        if (buffer.Length is <= 0 or > 64 * 1024)
            throw new CredentialRuntimeException("profile_metadata_size", "The credential profile metadata is invalid.");
        return buffer.ToArray();
    }

    private async Task<VerifiedHermesRuntime> InspectRuntimeAsync(CancellationToken cancellationToken)
    {
        if (!TrustedExecutable(_dockerExecutable))
            throw new CredentialRuntimeException("docker_unavailable", "The fixed Docker runtime is unavailable.");
        var commitPath = TrustedFile(Path.Combine(_installRoot, "logs", "runtime-generations", "runtime-generation.current.json"), 256 * 1024);
        using var commit = StrictJson(await File.ReadAllBytesAsync(commitPath, cancellationToken).ConfigureAwait(false));
        var commitRoot = commit.RootElement;
        RequireExactProperties(commitRoot, "schemaId", "protocolVersion", "committedAtUtc", "currentGenerationId", "currentImageId", "currentLockSha256", "currentEnvSha256", "deployment", "previous");
        RequireString(commitRoot, "schemaId", "photon.runtime.generation-commit/v2");
        RequireInteger(commitRoot, "protocolVersion", 1);
        var generationId = RequirePattern(commitRoot, "currentGenerationId", GenerationPattern());
        var imageId = RequirePattern(commitRoot, "currentImageId", ImagePattern());
        var lockHash = RequirePattern(commitRoot, "currentLockSha256", HexPattern());
        var envHash = RequirePattern(commitRoot, "currentEnvSha256", HexPattern());
        var deployment = commitRoot.GetProperty("deployment");
        RequireExactProperties(deployment, "schemaId", "composeSha256", "fingerprintSha256", "requiredEnvironment");
        RequireString(deployment, "schemaId", "photon.runtime.deployment/v1");
        var composeHash = RequirePattern(deployment, "composeSha256", HexPattern());
        var composePath = TrustedFile(Path.Combine(_installRoot, "docker-compose.yml"), 1024 * 1024);
        if (!FixedHashEquals(composeHash, Sha256(composePath)))
            throw new CredentialRuntimeException("runtime_deployment_mismatch", "The verified Hermes deployment changed.");

        var generationRoot = Path.Combine(_installRoot, "logs", "runtime-generations", generationId);
        TrustedDirectory(generationRoot);
        var lockPath = TrustedFile(Path.Combine(generationRoot, "runtime-image.lock.json"), 512 * 1024, requireReadOnly: true);
        var envPath = TrustedFile(Path.Combine(generationRoot, "runtime-image.env"), 1024, requireReadOnly: true);
        if (!FixedHashEquals(lockHash, Sha256(lockPath)) || !FixedHashEquals(envHash, Sha256(envPath)))
            throw new CredentialRuntimeException("runtime_generation_hash_mismatch", "The verified Hermes generation changed.");
        using (var generationLock = StrictJson(await File.ReadAllBytesAsync(lockPath, cancellationToken).ConfigureAwait(false)))
        {
            var root = generationLock.RootElement;
            RequireString(root, "schemaId", "photon.runtime.image-lock/v1");
            RequireInteger(root, "protocolVersion", 1);
            RequireString(root, "generationId", generationId);
            RequireString(root, "imageId", imageId);
        }
        var env = (await File.ReadAllTextAsync(envPath, cancellationToken).ConfigureAwait(false)).Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!StringComparer.Ordinal.Equals(env, $"HERMES_IMAGE_REFERENCE={imageId}\n"))
            throw new CredentialRuntimeException("runtime_generation_env_mismatch", "The verified Hermes generation environment changed.");

        var containerId = (await RunDockerAsync(["compose", "--project-directory", _installRoot, "--file", composePath, "ps", "--quiet", "gateway"], imageId, null, cancellationToken).ConfigureAwait(false)).Trim();
        if (!ContainerPattern().IsMatch(containerId))
            throw new CredentialRuntimeException("invalid_container_id", "Docker returned an invalid Hermes container identity.");
        var inspected = await RunDockerAsync(["inspect", "--type", "container", containerId], imageId, null, cancellationToken).ConfigureAwait(false);
        using var containers = StrictJson(Encoding.UTF8.GetBytes(inspected));
        if (containers.RootElement.ValueKind != JsonValueKind.Array || containers.RootElement.GetArrayLength() != 1)
            throw new CredentialRuntimeException("container_inspection_invalid", "Docker returned invalid Hermes container evidence.");
        var container = containers.RootElement[0];
        if (!StringComparer.Ordinal.Equals(container.GetProperty("Id").GetString(), containerId)
            || !StringComparer.Ordinal.Equals(container.GetProperty("Image").GetString(), imageId)
            || container.GetProperty("State").GetProperty("Running").ValueKind != JsonValueKind.True)
            throw new CredentialRuntimeException("container_binding_mismatch", "The running Hermes container does not match the verified generation.");
        RequireLoopbackGatewayPort(container);

        var image = await RunDockerAsync(["image", "inspect", imageId], imageId, null, cancellationToken).ConfigureAwait(false);
        using var images = StrictJson(Encoding.UTF8.GetBytes(image));
        if (images.RootElement.ValueKind != JsonValueKind.Array || images.RootElement.GetArrayLength() != 1
            || !StringComparer.Ordinal.Equals(images.RootElement[0].GetProperty("Id").GetString(), imageId))
            throw new CredentialRuntimeException("image_binding_mismatch", "The Hermes image does not match the verified generation.");
        return new VerifiedHermesRuntime(containerId, imageId);
    }

    internal static void RequireLoopbackGatewayPort(JsonElement container)
    {
        var ports = container.GetProperty("NetworkSettings").GetProperty("Ports");
        if (!ports.TryGetProperty($"{CredentialGatewayPort}/tcp", out var bindings)
            || bindings.ValueKind != JsonValueKind.Array
            || bindings.GetArrayLength() != 1)
            throw new CredentialRuntimeException("runtime_port_untrusted", "The Hermes credential endpoint is not published on one fixed port.");
        var binding = bindings[0];
        if (!StringComparer.Ordinal.Equals(binding.GetProperty("HostIp").GetString(), "127.0.0.1")
            || !int.TryParse(binding.GetProperty("HostPort").GetString(), out var port)
            || port != CredentialGatewayPort)
            throw new CredentialRuntimeException("runtime_port_untrusted", "The Hermes credential endpoint is not loopback-bound.");
    }

    private async Task ExecInputAsync(string containerId, string script, byte[] input, CancellationToken cancellationToken)
    {
        await RunDockerAsync(["exec", "-i", "--user", "0", containerId, "/bin/sh", "-ceu", script], null, input, cancellationToken).ConfigureAwait(false);
    }

    private async Task ExecBootstrapAsync(string containerId, CredentialRuntimeBootstrap bootstrap, CancellationToken cancellationToken)
    {
        await RunDockerAsync(["exec", "-i", "--user", "0", containerId, "/bin/sh", "-ceu", WriteBootstrapScript], null, null, cancellationToken, bootstrap).ConfigureAwait(false);
    }

    private async Task<string> RunDockerAsync(
        IReadOnlyList<string> arguments,
        string? imageReference,
        byte[]? input,
        CancellationToken cancellationToken,
        CredentialRuntimeBootstrap? bootstrap = null)
    {
        var start = new ProcessStartInfo
        {
            FileName = _dockerExecutable,
            WorkingDirectory = _installRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = input is not null || bootstrap is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (imageReference is not null) start.Environment["HERMES_IMAGE_REFERENCE"] = imageReference;
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new CredentialRuntimeException("docker_unavailable", "The fixed Docker runtime could not start.");
        var outputTask = ReadBoundedAsync(process.StandardOutput, cancellationToken);
        var errorTask = ReadBoundedAsync(process.StandardError, cancellationToken);
        try
        {
            if (input is not null)
            {
                await process.StandardInput.BaseStream.WriteAsync(input, cancellationToken).ConfigureAwait(false);
                await process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            else if (bootstrap is not null)
            {
                await bootstrap.WriteToAsync(process.StandardInput.BaseStream, cancellationToken).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        var output = await outputTask.ConfigureAwait(false);
        _ = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new CredentialRuntimeException("docker_operation_failed", "The fixed Docker credential operation failed.");
        return output;
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var result = new StringBuilder();
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (result.Length + count > MaximumDockerOutputCharacters)
                throw new CredentialRuntimeException("docker_output_too_large", "Docker returned too much output.");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }

    private async Task CleanupAsync(string containerId)
    {
        if (!ContainerPattern().IsMatch(containerId)) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await RunDockerAsync(["exec", "--user", "0", containerId, "/bin/sh", "-ceu", CleanupScript], null, null, timeout.Token).ConfigureAwait(false); }
        catch { }
    }

    private async Task StopCoreAsync()
    {
        var cancellation = _activeCancellation;
        var active = _activeTask;
        _activeCancellation = null;
        _activeTask = Task.CompletedTask;
        if (cancellation is null) return;
        cancellation.Cancel();
        try { await active.ConfigureAwait(false); } catch { }
        cancellation.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private string TrustedFile(string path, long maximumBytes, bool requireReadOnly = false)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(_installRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new CredentialRuntimeException("trusted_path_escape", "A trusted runtime file escaped the install root.");
        var info = new FileInfo(full);
        if (!info.Exists || info.Length is <= 0 || info.Length > maximumBytes || TraversesReparsePoint(full) || requireReadOnly && !info.IsReadOnly)
            throw new CredentialRuntimeException("trusted_file_invalid", "A verified runtime file is invalid.");
        return full;
    }

    private void TrustedDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(_installRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(full) || TraversesReparsePoint(full))
            throw new CredentialRuntimeException("trusted_directory_invalid", "A verified runtime directory is invalid.");
    }

    private static bool TrustedExecutable(string path)
    {
        try { return Path.IsPathFullyQualified(path) && File.Exists(path) && !TraversesReparsePoint(path); }
        catch { return false; }
    }

    private static bool TraversesReparsePoint(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current) break;
            current = parent;
        }
        return false;
    }

    private static JsonDocument StrictJson(byte[] bytes)
    {
        try { return JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow }); }
        catch (JsonException) { throw new CredentialRuntimeException("runtime_json_invalid", "Verified runtime evidence is invalid."); }
    }

    private static void RequireExactProperties(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new CredentialRuntimeException("runtime_json_invalid", "Verified runtime evidence is invalid.");
        var actual = element.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != names.Length || actual.Distinct(StringComparer.Ordinal).Count() != names.Length || actual.Any(name => !names.Contains(name, StringComparer.Ordinal)))
            throw new CredentialRuntimeException("runtime_json_invalid", "Verified runtime evidence is invalid.");
    }

    private static string RequirePattern(JsonElement element, string name, Regex pattern)
    {
        var value = element.GetProperty(name).GetString();
        if (value is null || !pattern.IsMatch(value)) throw new CredentialRuntimeException("runtime_json_invalid", "Verified runtime evidence is invalid.");
        return value;
    }

    private static void RequireString(JsonElement element, string name, string expected)
    {
        if (!StringComparer.Ordinal.Equals(element.GetProperty(name).GetString(), expected))
            throw new CredentialRuntimeException("runtime_json_invalid", "Verified runtime evidence is invalid.");
    }

    private static void RequireInteger(JsonElement element, string name, int expected)
    {
        if (!element.GetProperty(name).TryGetInt32(out var value) || value != expected)
            throw new CredentialRuntimeException("runtime_json_invalid", "Verified runtime evidence is invalid.");
    }

    private static string Sha256(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static bool FixedHashEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }

    private sealed record VerifiedHermesRuntime(string ContainerId, string ImageId)
    {
        internal bool ExactlyMatches(VerifiedHermesRuntime other) =>
            StringComparer.Ordinal.Equals(ContainerId, other.ContainerId)
            && StringComparer.Ordinal.Equals(ImageId, other.ImageId);
    }

    [GeneratedRegex("^[a-f0-9]{64}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex ContainerPattern();

    [GeneratedRegex("^sha256:[a-f0-9]{64}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex ImagePattern();

    [GeneratedRegex("^[a-f0-9]{64}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex HexPattern();

    [GeneratedRegex("^[a-f0-9]{16}-[a-f0-9]{12}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex GenerationPattern();
}
