using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace HermesDesktop;

internal sealed class WindowsAdministratorBridge
{
    internal const int ProtocolVersion = 1;
    private const int MaximumScriptCharacters = 16 * 1024;
    private const int MaximumReasonCharacters = 512;
    private const int MaximumOutputBytes = 256 * 1024;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(30);

    private readonly string _workspaceRoot;
    private readonly Action<object> _postMessage;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    internal WindowsAdministratorBridge(string workspaceRoot, Action<object> postMessage)
    {
        _workspaceRoot = Path.GetFullPath(workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot)));
        _postMessage = postMessage ?? throw new ArgumentNullException(nameof(postMessage));
    }

    internal async Task RunAsync(int version, string? requestId, string? script, string? reason)
    {
        var id = Normalize(requestId, 128);
        if (version != ProtocolVersion || id.Length == 0)
        {
            PostResult(id, false, "invalid-request", "The Windows Administrator request is invalid.");
            return;
        }

        var operation = (script ?? string.Empty).Trim();
        var explanation = Normalize(reason, MaximumReasonCharacters);
        if (operation.Length == 0 || operation.Length > MaximumScriptCharacters || operation.IndexOf('\0') >= 0)
        {
            PostResult(id, false, "invalid-script", "The requested administrator operation is empty or exceeds the supported size.");
            return;
        }
        if (explanation.Length == 0)
        {
            PostResult(id, false, "reason-required", "Photon must explain why Windows Administrator access is required.");
            return;
        }

        if (!await _operationGate.WaitAsync(0).ConfigureAwait(false))
        {
            PostResult(id, false, "administrator-busy", "Another Windows Administrator operation is already awaiting consent or completion.");
            return;
        }

        try
        {
            await ExecuteAsync(id, operation).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task ExecuteAsync(string requestId, string script)
    {
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        if (!File.Exists(powershell))
        {
            PostResult(requestId, false, "powershell-unavailable", "Windows PowerShell is unavailable for the administrator operation.");
            return;
        }

        Process? process = null;
        var preserveForRunningProcess = false;
        string? operationRoot = null;
        try
        {
            operationRoot = Path.Combine(Path.GetTempPath(), "HermesAdministrator", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(operationRoot);
            var wrapperPath = Path.Combine(operationRoot, "operation.ps1");
            var outputPath = Path.Combine(operationRoot, "output.txt");
            var resultPath = Path.Combine(operationRoot, "result.txt");
            await File.WriteAllTextAsync(
                wrapperPath,
                CreateWrapper(script, outputPath, resultPath),
                new UTF8Encoding(false)).ConfigureAwait(false);

            process = Process.Start(new ProcessStartInfo
            {
                FileName = powershell,
                Arguments = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{wrapperPath}\"",
                WorkingDirectory = _workspaceRoot,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process is null)
            {
                PostResult(requestId, false, "administrator-start-failed", "Windows did not start the requested administrator operation.");
                return;
            }

            try
            {
                await process.WaitForExitAsync().WaitAsync(OperationTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                preserveForRunningProcess = true;
                PostResult(requestId, false, "administrator-still-running", "The administrator operation is still running after 30 minutes. Windows retains control of that process.");
                return;
            }

            var exitCode = process.ExitCode;
            var output = ReadBoundedOutput(outputPath, out var outputTruncated);
            if (File.Exists(resultPath)
                && int.TryParse((await File.ReadAllTextAsync(resultPath).ConfigureAwait(false)).Trim(), out var reportedExitCode))
            {
                exitCode = reportedExitCode;
            }
            _postMessage(new
            {
                type = "developerServices.windowsAdministrator.run.result",
                version = ProtocolVersion,
                requestId,
                succeeded = exitCode == 0,
                code = exitCode == 0 ? "completed" : "administrator-operation-failed",
                message = exitCode == 0
                    ? "The Windows Administrator operation completed."
                    : $"The Windows Administrator operation exited with code {exitCode}.",
                exitCode,
                output,
                outputTruncated,
            });
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            PostResult(requestId, false, "consent-declined", "Windows Administrator consent was declined or cancelled.");
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException)
        {
            DesktopLog.Write($"Windows Administrator operation failed: {exception.GetType().Name}");
            PostResult(requestId, false, "administrator-start-failed", "Windows could not start or complete the requested administrator operation.");
        }
        finally
        {
            process?.Dispose();
            if (!preserveForRunningProcess && operationRoot is not null) TryDeleteOperationRoot(operationRoot);
        }
    }

    private static string CreateWrapper(string script, string outputPath, string resultPath)
    {
        static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        return $$"""
        $ErrorActionPreference = 'Stop'
        $hermesExitCode = 0
        try {
            $hermesOutput = & {
        {{script}}
            } *>&1 | Out-String -Width 4096
            if ($null -ne $LASTEXITCODE) { $hermesExitCode = [int]$LASTEXITCODE }
        }
        catch {
            $hermesExitCode = 1
            $hermesOutput = ($_ | Out-String -Width 4096)
        }
        [System.IO.File]::WriteAllText({{Quote(outputPath)}}, [string]$hermesOutput, [System.Text.UTF8Encoding]::new($false))
        [System.IO.File]::WriteAllText({{Quote(resultPath)}}, [string]$hermesExitCode, [System.Text.UTF8Encoding]::new($false))
        exit $hermesExitCode
        """;
    }

    private static string ReadBoundedOutput(string path, out bool truncated)
    {
        truncated = false;
        if (!File.Exists(path)) return string.Empty;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var length = (int)Math.Min(stream.Length, MaximumOutputBytes);
        var bytes = new byte[length];
        var read = stream.Read(bytes, 0, length);
        truncated = stream.Length > MaximumOutputBytes;
        return Encoding.UTF8.GetString(bytes, 0, read);
    }

    private void PostResult(string requestId, bool succeeded, string code, string message) => _postMessage(new
    {
        type = "developerServices.windowsAdministrator.run.result",
        version = ProtocolVersion,
        requestId,
        succeeded,
        code,
        message,
        exitCode = (int?)null,
        output = string.Empty,
        outputTruncated = false,
    });

    private static string Normalize(string? value, int maximum)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized[..Math.Min(normalized.Length, maximum)];
    }

    private static void TryDeleteOperationRoot(string operationRoot)
    {
        try { Directory.Delete(operationRoot, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
