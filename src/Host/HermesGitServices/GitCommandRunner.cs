using System.Diagnostics;
using System.Text;

namespace HermesGitServices;

public sealed record GitCommandBounds
{
    public int MaximumStandardOutputCharacters { get; init; } = 8 * 1024 * 1024;
    public int MaximumStandardErrorCharacters { get; init; } = 1024 * 1024;
    public int MaximumStatusEntries { get; init; } = 10_000;
    public TimeSpan ExecutionTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

internal sealed record GitProcessResult(
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    bool StandardOutputTruncated,
    bool StandardErrorTruncated,
    bool WasCancelled,
    bool TimedOut,
    bool StartFailed = false);

internal interface IGitProcessHost
{
    Task<GitProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        GitCommandBounds bounds,
        CancellationToken cancellationToken);
}

internal sealed class OwnedGitProcessHost : IGitProcessHost
{
    public async Task<GitProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        GitCommandBounds bounds,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start()) return FailedStart();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return FailedStart();
        }

        var stdout = new BoundedCharacterCapture(bounds.MaximumStandardOutputCharacters);
        var stderr = new BoundedCharacterCapture(bounds.MaximumStandardErrorCharacters);
        var stdoutTask = DrainAsync(process.StandardOutput, stdout);
        var stderrTask = DrainAsync(process.StandardError, stderr);
        using var timeout = new CancellationTokenSource(bounds.ExecutionTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        var cancelled = false;
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = cancellationToken.IsCancellationRequested;
            timedOut = !cancelled && timeout.IsCancellationRequested;
            StopOwnedProcess(process);
            try
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }
        }

        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        return new GitProcessResult(
            process.HasExited ? process.ExitCode : null,
            stdout.Value,
            stderr.Value,
            stdout.Truncated,
            stderr.Truncated,
            cancelled,
            timedOut);
    }

    private static async Task DrainAsync(StreamReader reader, BoundedCharacterCapture capture)
    {
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0) return;
            capture.Append(buffer.AsSpan(0, read));
        }
    }

    private static void StopOwnedProcess(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static GitProcessResult FailedStart() =>
        new(null, string.Empty, string.Empty, false, false, false, false, StartFailed: true);
}

internal sealed class BoundedCharacterCapture
{
    private readonly int _maximum;
    private readonly StringBuilder _value;

    internal BoundedCharacterCapture(int maximum)
    {
        _maximum = maximum;
        _value = new StringBuilder(Math.Min(maximum, 16 * 1024));
    }

    internal bool Truncated { get; private set; }

    internal string Value => _value.ToString();

    internal void Append(ReadOnlySpan<char> value)
    {
        var remaining = _maximum - _value.Length;
        if (remaining > 0) _value.Append(value[..Math.Min(remaining, value.Length)]);
        if (value.Length > remaining) Truncated = true;
    }
}

internal sealed record GitRawStatusResult(
    bool Succeeded,
    string? Output,
    SourceControlError? Error);

/// <summary>Builds and runs the single allowlisted Phase 1A Git status command.</summary>
public sealed class GitCommandRunner
{
    private static readonly string[] RedirectingVariables =
    {
        "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE",
        "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_EXEC_PATH",
        "GIT_CONFIG_NOSYSTEM", "GIT_CONFIG_GLOBAL", "GIT_CONFIG_SYSTEM", "GIT_CONFIG_COUNT",
        "GIT_PAGER", "PAGER", "GIT_EDITOR", "EDITOR", "VISUAL", "GIT_ASKPASS",
        "SSH_ASKPASS", "GCM_INTERACTIVE", "GIT_SSH", "GIT_SSH_COMMAND",
    };

    private readonly LocatedGitExecutable _git;
    private readonly IGitProcessHost _processHost;
    private readonly GitCommandBounds _bounds;

    internal GitCommandRunner(
        LocatedGitExecutable git,
        IGitProcessHost? processHost = null,
        GitCommandBounds? bounds = null)
    {
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _processHost = processHost ?? new OwnedGitProcessHost();
        _bounds = bounds ?? new GitCommandBounds();
        ValidateBounds(_bounds);
    }

    internal async Task<GitRawStatusResult> RunStatusAsync(
        ValidatedRepository repository,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var startInfo = CreateStatusStartInfo(repository);
        var result = await _processHost.RunAsync(startInfo, _bounds, cancellationToken).ConfigureAwait(false);
        if (result.StartFailed)
            return Failure("git_start_failed", "Git could not be started.", retryable: true);
        if (result.WasCancelled)
            return Failure("cancelled", "The status refresh was cancelled.", retryable: true);
        if (result.TimedOut)
            return Failure("timeout", "Git status exceeded the time limit.", retryable: true);
        if (result.StandardOutputTruncated)
            return Failure("output_too_large", "Git status exceeded the output limit.");
        if (result.ExitCode != 0)
        {
            if (result.StandardError.Contains("dubious ownership", StringComparison.OrdinalIgnoreCase)
                || result.StandardError.Contains("unsafe repository", StringComparison.OrdinalIgnoreCase))
            {
                return Failure(
                    "unsafe_ownership",
                    "Git refused this repository because its ownership is not trusted. Hermes did not change safe.directory.");
            }

            return Failure("status_failed", "Git could not read repository status.", retryable: true);
        }

        return new GitRawStatusResult(true, result.StandardOutput, null);
    }

    internal ProcessStartInfo CreateStatusStartInfo(ValidatedRepository repository)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _git.FullPath,
            WorkingDirectory = repository.RootPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false, true),
            StandardErrorEncoding = new UTF8Encoding(false, true),
        };

        foreach (var argument in new[]
                 {
                     "--no-pager",
                     "--no-optional-locks",
                     "-c",
                     "core.fsmonitor=false",
                     "-C",
                     repository.RootPath,
                     "status",
                     "--porcelain=v2",
                     "--branch",
                     "--show-stash",
                     "-z",
                     "--untracked-files=all",
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        SanitizeEnvironment(startInfo);
        return startInfo;
    }

    private static void SanitizeEnvironment(ProcessStartInfo startInfo)
    {
        foreach (var key in startInfo.Environment.Keys.ToArray())
        {
            if (RedirectingVariables.Contains(key, StringComparer.OrdinalIgnoreCase)
                || key.StartsWith("GIT_CONFIG_KEY_", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("GIT_CONFIG_VALUE_", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.Environment.Remove(key);
            }
        }

        startInfo.Environment["GIT_PAGER"] = "cat";
        startInfo.Environment["PAGER"] = "cat";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        startInfo.Environment["NO_COLOR"] = "1";
    }

    private static void ValidateBounds(GitCommandBounds bounds)
    {
        if (bounds.MaximumStandardOutputCharacters is <= 0 or > 64 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(bounds.MaximumStandardOutputCharacters));
        if (bounds.MaximumStandardErrorCharacters is <= 0 or > 8 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(bounds.MaximumStandardErrorCharacters));
        if (bounds.MaximumStatusEntries is <= 0 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(bounds.MaximumStatusEntries));
        if (bounds.ExecutionTimeout <= TimeSpan.Zero || bounds.ExecutionTimeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(bounds.ExecutionTimeout));
    }

    private static GitRawStatusResult Failure(string code, string message, bool retryable = false) =>
        new(false, null, new SourceControlError(code, message, retryable));
}
