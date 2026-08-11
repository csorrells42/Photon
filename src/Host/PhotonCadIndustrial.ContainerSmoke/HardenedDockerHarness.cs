using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal sealed class HardenedDockerHarness
{
    private const int DefaultCommandOutputLimit = 16 * 1024 * 1024;
    private readonly string _dockerConfig;
    private readonly string _runId;
    private readonly string _cidRoot;
    private int _invocation;

    public HardenedDockerHarness(string dockerConfig, string runId, string cidRoot)
    {
        _dockerConfig = Path.GetFullPath(dockerConfig);
        _runId = runId;
        _cidRoot = Path.GetFullPath(cidRoot);
        Directory.CreateDirectory(_cidRoot);
    }

    public Task<ProcessResult> RunDockerAsync(
        IEnumerable<string> arguments,
        byte[]? standardInput = null,
        string? workingDirectory = null,
        TimeSpan? timeout = null,
        int stdoutLimit = DefaultCommandOutputLimit,
        int stderrLimit = DefaultCommandOutputLimit) =>
        RunProcessAsync(
            arguments,
            standardInput,
            workingDirectory,
            timeout ?? TimeSpan.FromSeconds(30),
            stdoutLimit,
            stderrLimit);

    public async Task<ProcessResult> RunAdapterAsync(
        string exactImageId,
        string inputDirectory,
        string outputDirectory,
        byte[] request,
        TimeSpan? timeout = null)
    {
        return await RunContainerAsync(
            exactImageId,
            inputDirectory,
            outputDirectory,
            request,
            timeout ?? TimeSpan.FromSeconds(75),
            stdoutLimit: 4 * 1024 * 1024 + 2,
            stderrLimit: 1 * 1024 * 1024,
            entrypoint: null,
            command: Array.Empty<string>());
    }

    public async Task<ProcessResult> RunWorkloadAsync(
        string exactImageId,
        string inputDirectory,
        string outputDirectory,
        string pythonSource,
        TimeSpan timeout,
        int stdoutLimit,
        int stderrLimit)
    {
        return await RunContainerAsync(
            exactImageId,
            inputDirectory,
            outputDirectory,
            standardInput: null,
            timeout,
            stdoutLimit,
            stderrLimit,
            "/opt/photon/venv/bin/python",
            new[] { "-c", pythonSource });
    }

    public async Task AssertNoRunContainersAsync()
    {
        var result = await RunDockerAsync(
            new[] { "container", "ls", "-aq", "--filter", $"label=io.photon.cad.smoke-run={_runId}" },
            timeout: TimeSpan.FromSeconds(30),
            stdoutLimit: 256 * 1024,
            stderrLimit: 256 * 1024);
        RequireSuccess(result, "residual container query");
        Require(string.IsNullOrWhiteSpace(result.StandardOutput), "a smoke container was left behind");
        Require(!Directory.EnumerateFiles(_cidRoot).Any(), "a smoke CID file was left behind");
    }

    private async Task<ProcessResult> RunContainerAsync(
        string exactImageId,
        string inputDirectory,
        string outputDirectory,
        byte[]? standardInput,
        TimeSpan timeout,
        int stdoutLimit,
        int stderrLimit,
        string? entrypoint,
        IReadOnlyCollection<string> command)
    {
        Require(Regex.IsMatch(exactImageId, "^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant), "container must run by exact image ID");
        var input = Path.GetFullPath(inputDirectory);
        var output = Path.GetFullPath(outputDirectory);
        Require(Directory.Exists(input) && Directory.Exists(output), "container mount directory is missing");
        Require(!input.Contains(',', StringComparison.Ordinal) && !output.Contains(',', StringComparison.Ordinal), "mount path cannot contain a comma");

        var ordinal = Interlocked.Increment(ref _invocation);
        var name = $"photon-cad-industrial-{_runId}-{ordinal.ToString("x8", CultureInfo.InvariantCulture)}";
        var cidPath = Path.Combine(_cidRoot, name + ".cid");
        Require(!File.Exists(cidPath), "CID path already exists");
        await RequireContainerAbsentAsync(name);

        var arguments = new List<string>
        {
            "run", "--rm", "-i",
            "--name", name,
            "--cidfile", cidPath,
            "--label", $"io.photon.cad.smoke-run={_runId}",
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
            "--mount", $"type=bind,source={input},target=/photon-input,readonly",
            "--mount", $"type=bind,source={output},target=/photon-output",
        };
        if (entrypoint is not null)
        {
            arguments.Add("--entrypoint");
            arguments.Add(entrypoint);
        }
        arguments.Add(exactImageId);
        arguments.AddRange(command);

        ProcessResult? result = null;
        Exception? operationFailure = null;
        try
        {
            result = await RunProcessAsync(arguments, standardInput, null, timeout, stdoutLimit, stderrLimit);
        }
        catch (Exception exception)
        {
            operationFailure = exception;
        }
        Exception? cleanupFailure = null;
        try
        {
            await KillRemoveAndVerifyAsync(name, cidPath);
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
        }
        if (operationFailure is not null && cleanupFailure is not null)
        {
            throw new AggregateException(operationFailure, cleanupFailure);
        }
        if (operationFailure is not null)
        {
            ExceptionDispatchInfo.Capture(operationFailure).Throw();
        }
        if (cleanupFailure is not null)
        {
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }
        return result ?? throw new InvalidOperationException("container invocation produced no result");
    }

    private async Task RequireContainerAbsentAsync(string name)
    {
        var inspect = await RunDockerAsync(
            new[] { "container", "inspect", name, "--format", "{{json .}}" },
            timeout: TimeSpan.FromSeconds(15),
            stdoutLimit: 256 * 1024,
            stderrLimit: 256 * 1024);
        Require(inspect.ExitCode != 0, $"container name collision: {name}");
    }

    private async Task KillRemoveAndVerifyAsync(string name, string cidPath)
    {
        string? cidFromFile = null;
        if (File.Exists(cidPath))
        {
            cidFromFile = (await File.ReadAllTextAsync(cidPath, Encoding.ASCII)).Trim();
            Require(Regex.IsMatch(cidFromFile, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant), "invalid CID file");
        }

        var inspect = await RunDockerAsync(
            new[] { "container", "inspect", name, "--format", "{{json .}}" },
            timeout: TimeSpan.FromSeconds(15),
            stdoutLimit: 512 * 1024,
            stderrLimit: 256 * 1024);
        if (inspect.ExitCode == 0)
        {
            using var document = JsonDocument.Parse(inspect.StandardOutput);
            var root = document.RootElement;
            var exactId = root.GetProperty("Id").GetString()
                ?? throw new InvalidOperationException("inspected container has no ID");
            Require(Regex.IsMatch(exactId, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant), "inspected container has invalid ID");
            Require(cidFromFile is null || exactId == cidFromFile, "CID/name identity mismatch");
            var labels = root.GetProperty("Config").GetProperty("Labels");
            Require(labels.GetProperty("io.photon.cad.smoke-run").GetString() == _runId, "refusing cleanup of a foreign container");
            var kill = await RunDockerAsync(
                new[] { "container", "kill", exactId },
                timeout: TimeSpan.FromSeconds(20),
                stdoutLimit: 256 * 1024,
                stderrLimit: 256 * 1024);
            Require(kill.ExitCode == 0 || kill.StandardError.Contains("is not running", StringComparison.OrdinalIgnoreCase), "exact container kill failed");
            var remove = await RunDockerAsync(
                new[] { "container", "rm", "-f", exactId },
                timeout: TimeSpan.FromSeconds(20),
                stdoutLimit: 256 * 1024,
                stderrLimit: 256 * 1024);
            Require(remove.ExitCode == 0 || remove.StandardError.Contains("No such container", StringComparison.OrdinalIgnoreCase), "exact container removal failed");
        }

        var verifyName = await RunDockerAsync(
            new[] { "container", "inspect", name, "--format", "{{.Id}}" },
            timeout: TimeSpan.FromSeconds(15),
            stdoutLimit: 64 * 1024,
            stderrLimit: 64 * 1024);
        Require(verifyName.ExitCode != 0, $"container residue remained: {name}");
        if (cidFromFile is not null)
        {
            var verifyCid = await RunDockerAsync(
                new[] { "container", "inspect", cidFromFile, "--format", "{{.Id}}" },
                timeout: TimeSpan.FromSeconds(15),
                stdoutLimit: 64 * 1024,
                stderrLimit: 64 * 1024);
            Require(verifyCid.ExitCode != 0, $"container CID residue remained: {cidFromFile}");
        }
        if (File.Exists(cidPath))
        {
            File.Delete(cidPath);
        }
        Require(!File.Exists(cidPath), "CID file cleanup failed");
    }

    private async Task<ProcessResult> RunProcessAsync(
        IEnumerable<string> commandArguments,
        byte[]? standardInput,
        string? workingDirectory,
        TimeSpan timeout,
        int stdoutLimit,
        int stderrLimit)
    {
        Require(timeout > TimeSpan.Zero, "process timeout must be positive");
        Require(stdoutLimit > 0 && stderrLimit > 0, "process output limits must be positive");
        var startInfo = new ProcessStartInfo
        {
            FileName = "docker.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = standardInput is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(_dockerConfig);
        startInfo.ArgumentList.Add("--host");
        startInfo.ArgumentList.Add("npipe:////./pipe/docker_engine");
        foreach (var argument in commandArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        Require(process.Start(), "docker process failed to start");
        var stdoutTask = ReadCappedAsync(process.StandardOutput.BaseStream, stdoutLimit, process, "stdout");
        var stderrTask = ReadCappedAsync(process.StandardError.BaseStream, stderrLimit, process, "stderr");
        var inputTask = standardInput is null
            ? Task.CompletedTask
            : WriteInputAsync(process, standardInput);
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
            await inputTask;
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new ProcessResult(process.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException exception) when (cancellation.IsCancellationRequested)
        {
            TryKill(process);
            await ObserveExitAsync(process);
            await ObserveAsync(inputTask);
            await ObserveAsync(stdoutTask);
            await ObserveAsync(stderrTask);
            throw new DockerDeadlineExceededException($"docker timed out after {timeout.TotalMilliseconds:0} ms", exception);
        }
        catch
        {
            TryKill(process);
            await ObserveExitAsync(process);
            await ObserveAsync(inputTask);
            await ObserveAsync(stdoutTask);
            await ObserveAsync(stderrTask);
            throw;
        }
    }

    private static async Task WriteInputAsync(Process process, byte[] input)
    {
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(input);
            await process.StandardInput.BaseStream.FlushAsync();
        }
        finally
        {
            process.StandardInput.Close();
        }
    }

    private static async Task<byte[]> ReadCappedAsync(Stream stream, int maximumBytes, Process process, string channel)
    {
        using var output = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var count = await stream.ReadAsync(buffer);
            if (count == 0)
            {
                return output.ToArray();
            }
            if (output.Length + count > maximumBytes)
            {
                TryKill(process);
                throw new DockerOutputLimitExceededException($"docker {channel} exceeded {maximumBytes} bytes");
            }
            output.Write(buffer, 0, count);
        }
    }

    private static async Task ObserveExitAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync();
        }
        catch
        {
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static void RequireSuccess(ProcessResult result, string operation) =>
        Require(result.ExitCode == 0, $"{operation} failed ({result.ExitCode}): {result.StandardError}");

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

internal sealed record ProcessResult(int ExitCode, byte[] StandardOutputBytes, byte[] StandardErrorBytes)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public string StandardOutput => StrictUtf8.GetString(StandardOutputBytes);

    public string StandardError => StrictUtf8.GetString(StandardErrorBytes);
}

internal sealed class DockerDeadlineExceededException : TimeoutException
{
    public DockerDeadlineExceededException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed class DockerOutputLimitExceededException : IOException
{
    public DockerOutputLimitExceededException(string message)
        : base(message)
    {
    }
}
