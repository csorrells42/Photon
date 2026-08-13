using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;

namespace PhotonCadRuntime.IndustrialProvider;

internal sealed record IndustrialInputArtifact(string Slot, ReadOnlyMemory<byte> Content, string Digest);

internal interface IIndustrialContainerRunner
{
    ValueTask<IndustrialContainerInvocation> ExecuteAsync(
        ReadOnlyMemory<byte> request,
        IReadOnlyList<IndustrialInputArtifact> inputs,
        CancellationToken cancellationToken);
}

internal sealed class IndustrialContainerInvocation : IAsyncDisposable
{
    private readonly Func<ValueTask> _cleanup;
    private int _disposed;

    internal IndustrialContainerInvocation(byte[] response, string outputDirectory, Func<ValueTask> cleanup)
    {
        Response = response.ToArray();
        OutputDirectory = Path.GetFullPath(outputDirectory);
        _cleanup = cleanup;
    }

    internal ReadOnlyMemory<byte> Response { get; }
    internal string OutputDirectory { get; }

    public ValueTask DisposeAsync() => Interlocked.Exchange(ref _disposed, 1) == 0
        ? _cleanup()
        : ValueTask.CompletedTask;
}

internal sealed class ContainerRunner : IIndustrialContainerRunner
{
    private const int MaximumStdoutBytes = ProtocolV1.MaximumResponseBytes;
    private const int MaximumStderrBytes = 1024 * 1024;
    private readonly string _dockerExecutable;
    private readonly string _dockerConfigDirectory;
    private readonly string _workspaceRoot;
    private readonly string _exactImageId;
    private readonly TimeSpan _timeout;

    internal ContainerRunner(
        string dockerExecutable,
        string dockerConfigDirectory,
        string workspaceRoot,
        string exactImageId,
        TimeSpan timeout)
    {
        _dockerExecutable = RequireRegularFile(dockerExecutable, "docker_executable_rejected");
        _dockerConfigDirectory = RequireDirectory(dockerConfigDirectory, create: false, "docker_config_rejected");
        _workspaceRoot = RequireDirectory(workspaceRoot, create: true, "industrial_workspace_rejected");
        _exactImageId = ProtocolV1.NormalizeDigest(exactImageId);
        if (timeout < TimeSpan.FromSeconds(1) || timeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        _timeout = timeout;
    }

    public async ValueTask<IndustrialContainerInvocation> ExecuteAsync(
        ReadOnlyMemory<byte> request,
        IReadOnlyList<IndustrialInputArtifact> inputs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (request.Length <= 0 || request.Length > ProtocolV1.MaximumRequestBytes) throw Failure("industrial_request_size_rejected");
        await VerifyImageIdentityAsync(cancellationToken).ConfigureAwait(false);
        var jobId = Guid.NewGuid().ToString("N");
        var jobRoot = Path.Combine(_workspaceRoot, $"job-{jobId}");
        var inputRoot = Path.Combine(jobRoot, "input");
        var outputRoot = Path.Combine(jobRoot, "output");
        Directory.CreateDirectory(inputRoot);
        Directory.CreateDirectory(outputRoot);
        RequireOwnedDirectory(jobRoot);
        RequireOwnedDirectory(inputRoot);
        RequireOwnedDirectory(outputRoot);
        try
        {
            WriteInputs(inputRoot, inputs);
            var name = $"photon-cad-industrial-provider-{jobId}";
            var arguments = new List<string>
            {
                "run", "--rm", "-i",
                "--name", name,
                "--label", $"io.photon.cad.provider-job={jobId}",
                "--network", "none",
                "--read-only",
                "--user", "65532:65532",
                "--cap-drop", "ALL",
                "--security-opt", "no-new-privileges:true",
                "--pids-limit", "64",
                "--memory", "2g",
                "--cpus", "2",
                "--tmpfs", "/tmp:rw,nosuid,nodev,noexec,size=256m,mode=0700,uid=65532,gid=65532",
                "--tmpfs", "/session:rw,nosuid,nodev,noexec,size=256m,mode=0700,uid=65532,gid=65532",
                "--mount", $"type=bind,source={MountPath(inputRoot)},target=/photon-input,readonly",
                "--mount", $"type=bind,source={MountPath(outputRoot)},target=/photon-output",
                _exactImageId,
            };
            ProcessResult result;
            try
            {
                result = await RunDockerAsync(arguments, request.ToArray(), _timeout, MaximumStdoutBytes, MaximumStderrBytes, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception operationFailure)
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await RemoveExactNamedContainerAsync(name, cleanup.Token).ConfigureAwait(false);
                }
                catch (Exception cleanupFailure) { throw new AggregateException(operationFailure, cleanupFailure); }
                throw;
            }
            if (result.ExitCode != 0 || result.StandardError.Length != 0)
                throw Failure(ContainerFailureCode(result.StandardOutput));
            if (result.StandardOutput.Length <= 0) throw Failure("industrial_container_empty_response");
            return new IndustrialContainerInvocation(
                result.StandardOutput,
                outputRoot,
                () => { DeleteOwnedJob(jobRoot); return ValueTask.CompletedTask; });
        }
        catch
        {
            DeleteOwnedJob(jobRoot);
            throw;
        }
    }

    private async Task VerifyImageIdentityAsync(CancellationToken cancellationToken)
    {
        var result = await RunDockerAsync(
            ["image", "inspect", _exactImageId, "--format", "{{.Id}}"],
            null,
            TimeSpan.FromSeconds(30),
            1024,
            64 * 1024,
            cancellationToken).ConfigureAwait(false);
        var identity = Encoding.ASCII.GetString(result.StandardOutput).Trim();
        if (result.ExitCode != 0 || !ProtocolV1.FixedDigestEquals(identity, _exactImageId))
            throw Failure("industrial_exact_image_unavailable");
    }

    private void WriteInputs(string inputRoot, IReadOnlyList<IndustrialInputArtifact> inputs)
    {
        if (inputs.Count > 256) throw Failure("industrial_input_count_rejected");
        var slots = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        foreach (var input in inputs)
        {
            if (input.Slot.Length is < 1 or > 64 || !char.IsAsciiLetterLower(input.Slot[0])
                || input.Slot.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
                throw Failure("industrial_input_slot_rejected");
            if (!slots.Add(input.Slot)) throw Failure("industrial_input_slot_duplicate");
            if (input.Content.Length <= 0 || input.Content.Length > ProtocolV1.MaximumStepBytes
                || !ProtocolV1.FixedDigestEquals(ProtocolV1.Sha256(input.Content.Span), input.Digest))
                throw Failure("industrial_input_artifact_rejected");
            total = checked(total + input.Content.Length);
            if (total > PhotonCadProjects.RuntimeSync.PhotonCadRuntimeSyncContract.MaximumBaseArtifactBytesPerRequest)
                throw Failure("industrial_input_budget_rejected");
            var path = Path.Combine(inputRoot, input.Slot + ".step");
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(input.Content.Span);
            stream.Flush(flushToDisk: true);
        }
    }

    private async Task RemoveExactNamedContainerAsync(string name, CancellationToken cancellationToken)
    {
        var inspect = await RunDockerAsync(
            ["container", "inspect", name, "--format", "{{index .Config.Labels \"io.photon.cad.provider-job\"}}"],
            null, TimeSpan.FromSeconds(15), 1024, 64 * 1024, cancellationToken).ConfigureAwait(false);
        if (inspect.ExitCode != 0) return;
        var expectedJob = name["photon-cad-industrial-provider-".Length..];
        if (!StringComparer.Ordinal.Equals(Encoding.ASCII.GetString(inspect.StandardOutput).Trim(), expectedJob))
            throw Failure("industrial_container_cleanup_identity_mismatch");
        var remove = await RunDockerAsync(
            ["container", "rm", "-f", name],
            null, TimeSpan.FromSeconds(20), 64 * 1024, 64 * 1024, cancellationToken).ConfigureAwait(false);
        if (remove.ExitCode != 0 && !Encoding.UTF8.GetString(remove.StandardError).Contains("No such container", StringComparison.OrdinalIgnoreCase))
            throw Failure("industrial_container_cleanup_failed");
    }

    private async Task<ProcessResult> RunDockerAsync(
        IReadOnlyList<string> arguments,
        byte[]? standardInput,
        TimeSpan timeout,
        int stdoutLimit,
        int stderrLimit,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _dockerExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = standardInput is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(_dockerConfigDirectory);
        startInfo.ArgumentList.Add("--host");
        startInfo.ArgumentList.Add("npipe:////./pipe/docker_engine");
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw Failure("docker_process_start_failed");
        var stdoutTask = ReadCappedAsync(process.StandardOutput.BaseStream, stdoutLimit, process, cancellationToken);
        var stderrTask = ReadCappedAsync(process.StandardError.BaseStream, stderrLimit, process, cancellationToken);
        var inputTask = standardInput is null ? Task.CompletedTask : WriteInputAsync(process, standardInput, cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await inputTask.ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
        }
        catch
        {
            TryKill(process);
            await ObserveAsync(inputTask).ConfigureAwait(false);
            await ObserveAsync(stdoutTask).ConfigureAwait(false);
            await ObserveAsync(stderrTask).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task WriteInputAsync(Process process, byte[] input, CancellationToken cancellationToken)
    {
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(input, cancellationToken).ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { process.StandardInput.Close(); }
    }

    private static async Task<byte[]> ReadCappedAsync(Stream stream, int maximum, Process process, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(Math.Min(maximum, 64 * 1024));
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return output.ToArray();
            if (output.Length + read > maximum) { TryKill(process); throw Failure("docker_output_limit_exceeded"); }
            output.Write(buffer, 0, read);
        }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { }
    }

    private static async Task ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch { }
    }

    private string MountPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.Contains(',')) throw Failure("industrial_mount_path_rejected");
        return full;
    }

    private void RequireOwnedDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        var prefix = _workspaceRoot + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw Failure("industrial_workspace_identity_rejected");
    }

    private void DeleteOwnedJob(string jobRoot)
    {
        if (!Directory.Exists(jobRoot)) return;
        var full = Path.GetFullPath(jobRoot);
        var prefix = _workspaceRoot + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(full).StartsWith("job-", StringComparison.Ordinal)
            || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw Failure("industrial_cleanup_path_rejected");
        foreach (var entry in Directory.EnumerateFileSystemEntries(full, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw Failure("industrial_cleanup_link_rejected");
        }
        Directory.Delete(full, recursive: true);
    }

    private static string RequireRegularFile(string path, string code)
    {
        var full = Path.GetFullPath(path);
        var info = new FileInfo(full);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0) throw Failure(code);
        return full;
    }

    private static string RequireDirectory(string path, bool create, string code)
    {
        var full = Path.GetFullPath(path);
        if (create) Directory.CreateDirectory(full);
        if (!Directory.Exists(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw Failure(code);
        return full.TrimEnd(Path.DirectorySeparatorChar);
    }

    private static InvalidOperationException Failure(string code) => new(code);

    private static string ContainerFailureCode(byte[] standardOutput)
    {
        try
        {
            using var document = JsonDocument.Parse(standardOutput);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.False
                || !root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.String)
                return "industrial_container_failed";
            return error.GetString() switch
            {
                "timeout" => "industrial_container_timeout",
                "invalid-parameter" => "industrial_container_invalid_parameter",
                "operation-failed" => "industrial_container_operation_failed",
                "artifact-invalid" => "industrial_container_artifact_invalid",
                "resource-limit" => "industrial_container_resource_limit",
                "invalid-request" => "industrial_container_invalid_request",
                "unsupported-operation" => "industrial_container_unsupported_operation",
                "dependency-unavailable" => "industrial_container_dependency_unavailable",
                "internal-error" => "industrial_container_internal_error",
                "request-too-large" => "industrial_container_request_too_large",
                _ => "industrial_container_failed",
            };
        }
        catch (JsonException)
        {
            return "industrial_container_failed";
        }
    }

    private sealed record ProcessResult(int ExitCode, byte[] StandardOutput, byte[] StandardError);
}
