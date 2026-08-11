using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HermesDeveloperServices;

internal sealed record OwnedToolchainProcessBounds
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
    public int MaximumRetainedCharacters { get; init; } = 256 * 1024;
    public TimeSpan CleanupTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

internal sealed record OwnedToolchainProcessResult(
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    bool OutputTruncated,
    long DroppedCharacters,
    bool WasCancelled,
    bool TimedOut,
    string? FailureCode);

/// <summary>
/// Runs one already-pinned executable directly. Output is drained with fixed-size buffers, and on
/// Windows the entire process family is assigned to a kill-on-close Job Object before trusted output
/// is consumed. Every shutdown and drain wait is bounded.
/// </summary>
internal static class OwnedToolchainProcessRunner
{
    private const int DrainBufferCharacters = 4 * 1024;

    public static async Task<OwnedToolchainProcessResult> RunAsync(
        string executable,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        OwnedToolchainProcessBounds bounds,
        CancellationToken cancellationToken)
    {
        Validate(executable, workingDirectory, arguments, bounds);
        var budget = new SharedCharacterBudget(bounds.MaximumRetainedCharacters);
        var stdout = new FixedTextCapture(budget);
        var stderr = new FixedTextCapture(budget);
        using var timeout = new CancellationTokenSource(bounds.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        Process? process = null;
        WindowsKillOnCloseJob? job = null;
        Task stdoutTask = Task.CompletedTask;
        Task stderrTask = Task.CompletedTask;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                job = WindowsKillOnCloseJob.Create();
                if (job is null) return Failed("process_ownership_failed", stdout, stderr, budget);
            }
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
            SanitizeEnvironment(startInfo, Path.GetDirectoryName(executable)!);

            process = new Process { StartInfo = startInfo };
            if (!process.Start()) return Failed("process_start_failed", stdout, stderr, budget);
            if (job is not null && !job.TryAssign(process))
            {
                job.Dispose();
                job = null;
                StopOwnedTree(process);
                await WaitForExitBoundedAsync(process, bounds.CleanupTimeout).ConfigureAwait(false);
                return Failed("process_ownership_failed", stdout, stderr, budget);
            }

            stdoutTask = DrainFixedAsync(process.StandardOutput, stdout);
            stderrTask = DrainFixedAsync(process.StandardError, stderr);
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
            }

            // Closing the Job Object terminates every still-running descendant, including children
            // which inherited redirected pipe handles after the compiler process itself exited.
            job?.Dispose();
            job = null;
            if (!OperatingSystem.IsWindows() && (cancelled || timedOut)) StopOwnedTree(process);
            var exited = await WaitForExitBoundedAsync(process, bounds.CleanupTimeout).ConfigureAwait(false);
            var drained = await WaitForTasksBoundedAsync(stdoutTask, stderrTask, bounds.CleanupTimeout).ConfigureAwait(false);
            int? exitCode = exited && process.HasExited ? process.ExitCode : null;
            var failure = !exited || !drained
                ? "process_cleanup_timeout"
                : cancelled ? "process_cancelled"
                : timedOut ? "process_timeout"
                : exitCode == 0 ? null : "process_failed";
            return new(
                exitCode,
                stdout.ToString(),
                stderr.ToString(),
                budget.Dropped > 0,
                budget.Dropped,
                cancelled,
                timedOut,
                failure);
        }
        catch (OperationCanceledException)
        {
            job?.Dispose();
            job = null;
            if (process is { HasExited: false } && !OperatingSystem.IsWindows()) StopOwnedTree(process);
            if (process is not null) await WaitForExitBoundedAsync(process, bounds.CleanupTimeout).ConfigureAwait(false);
            await WaitForTasksBoundedAsync(stdoutTask, stderrTask, bounds.CleanupTimeout).ConfigureAwait(false);
            var cancelled = cancellationToken.IsCancellationRequested;
            return new(null, stdout.ToString(), stderr.ToString(), budget.Dropped > 0, budget.Dropped,
                cancelled, !cancelled && timeout.IsCancellationRequested, cancelled ? "process_cancelled" : "process_timeout");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            job?.Dispose();
            job = null;
            if (process is { HasExited: false } && !OperatingSystem.IsWindows()) StopOwnedTree(process);
            if (process is not null) await WaitForExitBoundedAsync(process, bounds.CleanupTimeout).ConfigureAwait(false);
            await WaitForTasksBoundedAsync(stdoutTask, stderrTask, bounds.CleanupTimeout).ConfigureAwait(false);
            return Failed("process_start_failed", stdout, stderr, budget);
        }
        finally
        {
            job?.Dispose();
            process?.Dispose();
        }
    }

    private static async Task DrainFixedAsync(StreamReader reader, FixedTextCapture capture)
    {
        var buffer = new char[DrainBufferCharacters];
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
                if (read == 0) return;
                capture.Append(new string(buffer, 0, read));
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
    }

    private static async Task<bool> WaitForExitBoundedAsync(Process process, TimeSpan cleanupTimeout)
    {
        if (process.HasExited) return true;
        using var cancellation = new CancellationTokenSource(cleanupTimeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (InvalidOperationException) { return process.HasExited; }
    }

    private static async Task<bool> WaitForTasksBoundedAsync(Task stdout, Task stderr, TimeSpan cleanupTimeout)
    {
        try
        {
            await Task.WhenAll(stdout, stderr).WaitAsync(cleanupTimeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException) { return false; }
    }

    private static void SanitizeEnvironment(ProcessStartInfo startInfo, string executableDirectory)
    {
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        var temp = Path.GetTempPath();
        startInfo.Environment.Clear();
        startInfo.Environment["PATH"] = OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(systemRoot)
            ? string.Join(Path.PathSeparator, executableDirectory, Path.Combine(systemRoot, "System32"))
            : string.Join(Path.PathSeparator, executableDirectory, "/usr/bin", "/bin");
        startInfo.Environment["LC_ALL"] = "C";
        startInfo.Environment["LANG"] = "C";
        startInfo.Environment["NO_COLOR"] = "1";
        startInfo.Environment["TEMP"] = temp;
        startInfo.Environment["TMP"] = temp;
        if (!string.IsNullOrWhiteSpace(systemRoot))
        {
            startInfo.Environment["SystemRoot"] = systemRoot;
            startInfo.Environment["WINDIR"] = systemRoot;
        }
    }

    private static void StopOwnedTree(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private static OwnedToolchainProcessResult Failed(
        string code,
        FixedTextCapture stdout,
        FixedTextCapture stderr,
        SharedCharacterBudget budget) =>
        new(null, stdout.ToString(), stderr.ToString(), budget.Dropped > 0, budget.Dropped, false, false, code);

    private static void Validate(
        string executable,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        OwnedToolchainProcessBounds bounds)
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            throw new ArgumentException("The pinned executable is invalid.", nameof(executable));
        if (!Path.IsPathFullyQualified(workingDirectory) || !Directory.Exists(workingDirectory))
            throw new ArgumentException("The process working directory is invalid.", nameof(workingDirectory));
        if (arguments.Any(argument => argument is null || argument.StartsWith('@') || argument.Contains('\0') || argument.Contains('\r') || argument.Contains('\n')))
            throw new ArgumentException("Response-file and control-character arguments are forbidden.", nameof(arguments));
        if (bounds.Timeout <= TimeSpan.Zero || bounds.Timeout > TimeSpan.FromMinutes(30))
            throw new ArgumentOutOfRangeException(nameof(bounds.Timeout));
        if (bounds.CleanupTimeout <= TimeSpan.Zero || bounds.CleanupTimeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(bounds.CleanupTimeout));
        if (bounds.MaximumRetainedCharacters is < 1 or > 8 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(bounds.MaximumRetainedCharacters));
    }

    private sealed class FixedTextCapture
    {
        private readonly object _gate = new();
        private readonly SharedCharacterBudget _budget;
        private readonly StringBuilder _builder = new();

        public FixedTextCapture(SharedCharacterBudget budget) => _budget = budget;

        public void Append(string value)
        {
            var retained = _budget.Reserve(value.Length);
            if (retained == 0) return;
            lock (_gate) _builder.Append(value, 0, retained);
        }

        public override string ToString()
        {
            lock (_gate) return _builder.ToString();
        }
    }

    private sealed class WindowsKillOnCloseJob : SafeHandleZeroOrMinusOneIsInvalid
    {
        private const uint ExtendedLimitInformationClass = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;

        private WindowsKillOnCloseJob() : base(ownsHandle: true) { }

        public static WindowsKillOnCloseJob? Create()
        {
            var raw = CreateJobObject(IntPtr.Zero, null);
            if (raw == IntPtr.Zero || raw == new IntPtr(-1)) return null;
            var job = new WindowsKillOnCloseJob();
            job.SetHandle(raw);
            var information = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose,
                },
            };
            var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
            var pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(information, pointer, fDeleteOld: false);
                if (!SetInformationJobObject(job, ExtendedLimitInformationClass, pointer, (uint)size))
                {
                    job.Dispose();
                    return null;
                }
            }
            finally { Marshal.FreeHGlobal(pointer); }
            return job;
        }

        public bool TryAssign(Process process) => AssignProcessToJobObject(this, process.Handle);

        protected override bool ReleaseHandle() => CloseHandle(handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(
            WindowsKillOnCloseJob job,
            uint informationClass,
            IntPtr information,
            uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(WindowsKillOnCloseJob job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }
    }
}
