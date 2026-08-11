using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PhotonCadRuntime;

public sealed class CadDockerRuntimeException : InvalidOperationException
{
    public CadDockerRuntimeException(string code, string? safeDetail = null, Exception? innerException = null)
        : base("The verified Photon CAD Docker runtime operation failed.", innerException)
    {
        Code = ContractGuards.Identifier(code, nameof(code), 96);
        SafeDetail = safeDetail is null
            ? null
            : ContractGuards.RequiredText(safeDetail, nameof(safeDetail), 2_048);
    }

    public string Code { get; }
    public string? SafeDetail { get; }
}

internal sealed class CadBoundedProcessResult
{
    internal CadBoundedProcessResult(int exitCode, byte[] standardOutput, byte[] standardError, bool outputTruncated)
    {
        ExitCode = exitCode;
        StandardOutput = standardOutput;
        StandardError = standardError;
        OutputTruncated = outputTruncated;
    }

    public int ExitCode { get; }
    public byte[] StandardOutput { get; }
    public byte[] StandardError { get; }
    public bool OutputTruncated { get; }
}

internal static class CadDockerProcessRunner
{
    private const int MaximumCommandOutputBytes = 1_048_576;

    public static async ValueTask<CadBoundedProcessResult> ExecuteAsync(
        string verifiedExecutablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(verifiedExecutablePath) || !Path.IsPathFullyQualified(verifiedExecutablePath))
            throw new CadContractException("absolute_local_path_required", nameof(verifiedExecutablePath));
        if (arguments is null || arguments.Count == 0 || arguments.Count > 256 ||
            arguments.Any(argument => argument is null || argument.Length > 4_096 || argument.Any(char.IsControl)))
            throw new CadContractException("invalid_process_arguments", nameof(arguments));
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5))
            throw new CadContractException("invalid_timeout", nameof(timeout));

        using var clientIsolation = CadDockerClientIsolation.Create();
        using var process = new Process
        {
            StartInfo = CreateStartInfo(verifiedExecutablePath, arguments, clientIsolation.DirectoryPath),
        };
        try
        {
            if (!process.Start()) throw new CadDockerRuntimeException("docker_start_failed");
        }
        catch (CadDockerRuntimeException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new CadDockerRuntimeException("docker_start_failed", innerException: exception);
        }

        var stdout = DrainBoundedAsync(process.StandardOutput.BaseStream, MaximumCommandOutputBytes, CancellationToken.None);
        var stderr = DrainBoundedAsync(process.StandardError.BaseStream, MaximumCommandOutputBytes, CancellationToken.None);
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await AwaitDrainAfterKillAsync(stdout, stderr).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new CadDockerRuntimeException("docker_command_timeout");
        }

        var output = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        return new CadBoundedProcessResult(
            process.ExitCode,
            output.Bytes,
            error.Bytes,
            output.Truncated || error.Truncated);
    }

    internal static ProcessStartInfo CreateStartInfo(
        string executablePath,
        IReadOnlyList<string> arguments,
        string dockerConfigDirectory)
    {
        if (!Path.IsPathFullyQualified(dockerConfigDirectory) || !Directory.Exists(dockerConfigDirectory) ||
            (File.GetAttributes(dockerConfigDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new CadContractException("unsafe_docker_config", nameof(dockerConfigDirectory));
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(dockerConfigDirectory);
        startInfo.ArgumentList.Add("--host");
        startInfo.ArgumentList.Add(CadDockerClientIsolation.LocalDockerHost);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        foreach (var name in startInfo.Environment.Keys.ToArray())
        {
            if (name.StartsWith("DOCKER_", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("HTTP_PROXY", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("HTTPS_PROXY", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("ALL_PROXY", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("NO_PROXY", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("SSH_AUTH_SOCK", StringComparison.OrdinalIgnoreCase))
                startInfo.Environment.Remove(name);
        }
        return startInfo;
    }

    internal static async Task<CadBoundedBytes> DrainBoundedAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var retained = new MemoryStream(Math.Min(maximumBytes, 65_536));
        var buffer = new byte[16_384];
        var truncated = false;
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            var writable = Math.Min(count, Math.Max(0, maximumBytes - checked((int)retained.Length)));
            if (writable > 0) retained.Write(buffer, 0, writable);
            if (writable != count) truncated = true;
        }
        return new CadBoundedBytes(retained.ToArray(), truncated);
    }

    internal static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static async Task AwaitDrainAfterKillAsync(
        Task<CadBoundedBytes> stdout,
        Task<CadBoundedBytes> stderr)
    {
        try
        {
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or ObjectDisposedException)
        {
        }
    }
}

internal sealed record CadBoundedBytes(byte[] Bytes, bool Truncated);

public sealed class CadDockerCliImageInspector : ICadDockerImageInspector
{
    public async ValueTask<CadDockerImageInspection> InspectAsync(
        string verifiedDockerExecutablePath,
        string exactTag,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var tag = ContractGuards.RequiredText(exactTag, nameof(exactTag), 256);
        if (tag is not CadDockerRuntimeIdentity.GeometryTag and not CadDockerRuntimeIdentity.AssemblyTag)
            throw new CadContractException("unapproved_image_tag", nameof(exactTag));
        var result = await CadDockerProcessRunner.ExecuteAsync(
            verifiedDockerExecutablePath,
            ["image", "inspect", tag, "--format", "{{json .}}"],
            timeout,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || result.OutputTruncated)
            throw new CadDockerRuntimeException("image_inspect_failed", SafeText(result.StandardError));
        return CadDockerEvidenceParser.ParseImageInspection(TrimSingleFrame(result.StandardOutput));
    }

    private static ReadOnlySpan<byte> TrimSingleFrame(byte[] bytes)
    {
        var start = 0;
        var end = bytes.Length;
        while (start < end && bytes[start] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') start++;
        while (end > start && bytes[end - 1] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') end--;
        var span = bytes.AsSpan(start, end - start);
        if (span.IsEmpty)
            throw new CadContractException("empty_inspection_frame", nameof(bytes));
        if (span.IndexOf((byte)'\n') >= 0 || span.IndexOf((byte)'\r') >= 0)
            throw new CadContractException("multiple_inspection_frames", nameof(bytes));
        return span;
    }

    private static string? SafeText(byte[] bytes)
    {
        if (bytes.Length == 0) return null;
        var text = Encoding.UTF8.GetString(bytes);
        var safe = new string(text.Select(character => char.IsControl(character) ? ' ' : character).ToArray());
        safe = string.Join(' ', safe.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return safe.Length == 0 ? null : safe[..Math.Min(2_048, safe.Length)];
    }
}

internal sealed class CadDockerLaunchPlan
{
    private CadDockerLaunchPlan(
        CadDockerRuntimeRole role,
        string containerName,
        string imageId,
        IReadOnlyList<string> arguments)
    {
        Role = role;
        ContainerName = containerName;
        ImageId = imageId;
        Arguments = arguments;
    }

    public CadDockerRuntimeRole Role { get; }
    public string ContainerName { get; }
    public string ImageId { get; }
    public IReadOnlyList<string> Arguments { get; }

    public static CadDockerLaunchPlan Create(
        CadVerifiedDockerRuntimeEvidence evidence,
        CadDockerRuntimeRole role,
        CadSessionHandle session,
        CadProjectHandle project)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(project);
        role = ContractGuards.EnumValue(role, nameof(role));
        var receipt = role == CadDockerRuntimeRole.Geometry ? evidence.Geometry : evidence.Assembly;
        var entrypoint = role == CadDockerRuntimeRole.Geometry
            ? evidence.Policy.GeometryEntrypoint
            : evidence.Policy.AssemblyEntrypoint;
        var workspace = CadDockerPathSecurity.RequireWorkspace(
            evidence.Settings.WorkspacePath,
            nameof(evidence.Settings.WorkspacePath));
        if (!workspace.Equals(evidence.Settings.WorkspacePath, StringComparison.OrdinalIgnoreCase))
            throw new CadContractException("workspace_identity_changed", nameof(evidence.Settings.WorkspacePath));
        var name = CreateOwnedContainerName(role);
        var mount = $"type=bind,source={workspace},target={CadDockerRuntimeIdentity.WorkspaceMount},bind-propagation=rprivate";
        var arguments = new List<string>
        {
            "run", "-i", "--rm",
            "--name", name,
            "--pull", "never",
            "--platform", CadDockerRuntimeIdentity.Platform,
            "--network", "none",
            "--read-only",
            "--user", CadDockerRuntimeIdentity.RuntimeUser,
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges:true",
            "--pids-limit", evidence.Policy.PidsLimit.ToString(CultureInfo.InvariantCulture),
            "--memory", evidence.Policy.MemoryBytes.ToString(CultureInfo.InvariantCulture),
            "--cpus", (evidence.Policy.NanoCpus / 1_000_000_000d).ToString("0.###", CultureInfo.InvariantCulture),
            "--stop-timeout", evidence.Policy.StopTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
        };
        foreach (var tmpfs in evidence.Policy.Tmpfs)
        {
            arguments.Add("--tmpfs");
            arguments.Add(tmpfs);
        }
        arguments.AddRange([
            "--mount", mount,
            "--env", $"PHOTON_CAD_SESSION_ID={session.Value}",
            "--env", $"PHOTON_CAD_PROJECT_ID={project.Value}",
            "--workdir", CadDockerRuntimeIdentity.WorkspaceMount,
            "--entrypoint", entrypoint[0],
            receipt.ImageId,
        ]);
        arguments.AddRange(entrypoint.Skip(1));
        ValidateArguments(arguments, workspace, receipt.ImageId, name);
        return new CadDockerLaunchPlan(role, name, receipt.ImageId, arguments.AsReadOnly());
    }

    private static string CreateOwnedContainerName(CadDockerRuntimeRole role)
    {
        var suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        return $"photon-cad-{(role == CadDockerRuntimeRole.Geometry ? "g" : "a")}-{suffix}";
    }

    private static void ValidateArguments(
        IReadOnlyList<string> arguments,
        string workspace,
        string imageId,
        string containerName)
    {
        if (!containerName.StartsWith("photon-cad-", StringComparison.Ordinal) ||
            !arguments.Contains("--network") || !arguments.Contains("none") ||
            !arguments.Contains("--read-only") || !arguments.Contains("--cap-drop") ||
            !arguments.Contains("ALL") || !arguments.Contains("no-new-privileges:true") ||
            !arguments.Contains(imageId) || arguments.Contains("--privileged") ||
            arguments.Contains("-p") || arguments.Contains("--publish") ||
            arguments.Any(argument => argument.Contains("docker.sock", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("/root", StringComparison.Ordinal) || argument.Equals("/home", StringComparison.Ordinal)))
            throw new CadContractException("unsafe_docker_launch_plan", nameof(arguments));
        var mountArguments = arguments.Select((value, index) => (value, index))
            .Where(item => item.value == "--mount").Select(item => arguments[item.index + 1]).ToArray();
        if (mountArguments.Length != 1 ||
            mountArguments[0] != $"type=bind,source={workspace},target=/workspace,bind-propagation=rprivate")
            throw new CadContractException("unsafe_mount_plan", nameof(arguments));
        var environmentArguments = arguments.Select((value, index) => (value, index))
            .Where(item => item.value == "--env").Select(item => arguments[item.index + 1]).ToArray();
        if (environmentArguments.Length != 2 ||
            environmentArguments.Count(value => value.StartsWith("PHOTON_CAD_SESSION_ID=", StringComparison.Ordinal)) != 1 ||
            environmentArguments.Count(value => value.StartsWith("PHOTON_CAD_PROJECT_ID=", StringComparison.Ordinal)) != 1)
            throw new CadContractException("unsafe_environment_plan", nameof(arguments));
    }
}

internal sealed class CadDockerContainerProcess : IAsyncDisposable
{
    private readonly CadVerifiedDockerRuntimeEvidence _evidence;
    private readonly Process _process;
    private readonly Task<CadBoundedBytes> _stderrDrain;
    private readonly CadDockerClientIsolation _clientIsolation;
    private int _disposeStarted;
    private Exception? _cleanupFailure;
    private int _cleanupConfirmed;

    private CadDockerContainerProcess(
        CadVerifiedDockerRuntimeEvidence evidence,
        CadDockerLaunchPlan plan,
        Process process,
        Task<CadBoundedBytes> stderrDrain,
        CadDockerClientIsolation clientIsolation)
    {
        _evidence = evidence;
        Plan = plan;
        _process = process;
        _stderrDrain = stderrDrain;
        _clientIsolation = clientIsolation;
    }

    public CadDockerLaunchPlan Plan { get; }
    public Stream StandardInput => _process.StandardInput.BaseStream;
    public Stream StandardOutput => _process.StandardOutput.BaseStream;
    public bool HasExited => _process.HasExited;

    public static async ValueTask<CadDockerContainerProcess> StartAsync(
        CadVerifiedDockerRuntimeEvidence evidence,
        CadDockerRuntimeRole role,
        CadSessionHandle session,
        CadProjectHandle project,
        CancellationToken cancellationToken = default)
    {
        await CadDockerRuntimeEvidenceVerifier.ReverifyDockerExecutableAsync(evidence, cancellationToken).ConfigureAwait(false);
        var plan = CadDockerLaunchPlan.Create(evidence, role, session, project);
        var clientIsolation = CadDockerClientIsolation.Create();
        var process = new Process
        {
            StartInfo = CadDockerProcessRunner.CreateStartInfo(
                evidence.Settings.DockerExecutablePath,
                plan.Arguments,
                clientIsolation.DirectoryPath),
            EnableRaisingEvents = true,
        };
        try
        {
            if (!process.Start()) throw new CadDockerRuntimeException("container_start_failed");
            var stderr = CadDockerProcessRunner.DrainBoundedAsync(
                process.StandardError.BaseStream,
                65_536,
                CancellationToken.None);
            return new CadDockerContainerProcess(evidence, plan, process, stderr, clientIsolation);
        }
        catch
        {
            process.Dispose();
            clientIsolation.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            if (_cleanupFailure is not null)
                throw new CadDockerRuntimeException("container_cleanup_failed", innerException: _cleanupFailure);
            if (Volatile.Read(ref _cleanupConfirmed) == 0)
                throw new CadDockerRuntimeException("container_cleanup_in_progress");
            return;
        }
        Exception? cleanupFailure = null;
        try
        {
            try
            {
                StandardInput.Close();
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
            }

            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(
                Math.Min(30, _evidence.Policy.StopTimeoutSeconds + 15)));
            await CleanupCommandAsync(
                ["stop", "--time", _evidence.Policy.StopTimeoutSeconds.ToString(CultureInfo.InvariantCulture), Plan.ContainerName],
                cleanupTimeout.Token).ConfigureAwait(false);
            await CleanupCommandAsync(["rm", "--force", Plan.ContainerName], cleanupTimeout.Token).ConfigureAwait(false);

            if (!_process.HasExited)
            {
                try
                {
                    await _process.WaitForExitAsync(cleanupTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    CadDockerProcessRunner.TryKill(_process);
                }
            }
            try
            {
                await _stderrDrain.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TimeoutException or IOException or ObjectDisposedException)
            {
            }
            await ConfirmContainerAbsentAsync(cleanupTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is CadDockerRuntimeException or CadContractException or
            OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            cleanupFailure = exception;
        }
        finally
        {
            CadDockerProcessRunner.TryKill(_process);
            _process.Dispose();
            _clientIsolation.Dispose();
        }
        if (cleanupFailure is not null)
        {
            _cleanupFailure = cleanupFailure;
            throw new CadDockerRuntimeException("container_cleanup_failed", innerException: cleanupFailure);
        }
        Volatile.Write(ref _cleanupConfirmed, 1);
    }

    private async ValueTask CleanupCommandAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        await CadDockerRuntimeEvidenceVerifier.ReverifyDockerExecutableAsync(_evidence, cancellationToken).ConfigureAwait(false);
        _ = await CadDockerProcessRunner.ExecuteAsync(
            _evidence.Settings.DockerExecutablePath,
            arguments,
            TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ConfirmContainerAbsentAsync(CancellationToken cancellationToken)
    {
        await CadDockerRuntimeEvidenceVerifier.ReverifyDockerExecutableAsync(_evidence, cancellationToken).ConfigureAwait(false);
        var result = await CadDockerProcessRunner.ExecuteAsync(
            _evidence.Settings.DockerExecutablePath,
            ["ps", "-a", "--filter", $"name=^/{Plan.ContainerName}$", "--format", "{{.Names}}"],
            TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || result.OutputTruncated || result.StandardOutput.Any(value => !char.IsWhiteSpace((char)value)))
            throw new CadDockerRuntimeException("container_cleanup_unconfirmed");
    }
}

internal sealed class CadDockerClientIsolation : IDisposable
{
    internal const string LocalDockerHost = "npipe:////./pipe/docker_engine";
    private const string DirectoryPrefix = "photon-cad-docker-client-";
    private int _disposed;

    private CadDockerClientIsolation(string directoryPath) => DirectoryPath = directoryPath;

    internal string DirectoryPath { get; }

    internal static CadDockerClientIsolation Create()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"{DirectoryPrefix}{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new CadDockerRuntimeException("unsafe_docker_client_config");
        return new CadDockerClientIsolation(directory);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            var expectedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            var actualParent = Path.GetDirectoryName(Path.GetFullPath(DirectoryPath));
            if (actualParent == expectedParent &&
                Path.GetFileName(DirectoryPath).StartsWith(DirectoryPrefix, StringComparison.Ordinal) &&
                Directory.Exists(DirectoryPath) &&
                (File.GetAttributes(DirectoryPath) & FileAttributes.ReparsePoint) == 0 &&
                !Directory.EnumerateFileSystemEntries(DirectoryPath).Any())
                Directory.Delete(DirectoryPath, recursive: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
