using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace HermesDesktop;

internal sealed class CodexAppServerBridge(string workspacePath, Action<object> postMessage) : IAsyncDisposable
{
    public const int ProtocolVersion = 1;
    private const int MaximumMessageCharacters = 1024 * 1024;
    private static readonly HashSet<string> AllowedClientMethods = new(StringComparer.Ordinal)
    {
        "initialize",
        "account/read",
        "account/login/start",
        "account/login/cancel",
        "account/logout",
        "account/rateLimits/read",
        "account/usage/read",
        "model/list",
        "thread/start",
        "thread/resume",
        "thread/list",
        "thread/read",
        "thread/archive",
        "thread/unarchive",
        "turn/start",
        "turn/steer",
        "turn/interrupt",
        "skills/list",
        "app/list",
        "app/installed",
        "mcpServer/oauth/login",
    };

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _pendingLock = new();
    private readonly HashSet<string> _pendingServerRequests = new(StringComparer.Ordinal);
    private Process? _process;
    private CancellationTokenSource? _shutdown;
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private bool _initializeSent;

    public async Task StartAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (_process is { HasExited: false } running)
            {
                PostReady(running);
                return;
            }

            var executable = FindCodexExecutable();
            if (executable is null)
            {
                postMessage(new
                {
                    type = "codex.unavailable",
                    version = ProtocolVersion,
                    message = "Codex was not found. Install Codex Desktop, the Codex CLI, or the OpenAI VS Code extension, then restart Phos Agape Aphthartos.",
                });
                return;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = workspacePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            startInfo.ArgumentList.Add("app-server");
            startInfo.ArgumentList.Add("--listen");
            startInfo.ArgumentList.Add("stdio://");

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.Exited += ProcessExited;
            if (!process.Start()) throw new InvalidOperationException("Codex app-server did not start.");

            _process = process;
            _shutdown = new CancellationTokenSource();
            _initializeSent = false;
            lock (_pendingLock) _pendingServerRequests.Clear();
            _stdoutTask = ReadProtocolAsync(process.StandardOutput, _shutdown.Token);
            _stderrTask = ReadDiagnosticsAsync(process.StandardError, _shutdown.Token);
            PostReady(process);
        }
        catch (Exception exception)
        {
            postMessage(new { type = "codex.error", version = ProtocolVersion, message = exception.Message });
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task SendAsync(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            PostError("Codex protocol messages must be JSON objects.");
            return;
        }

        var json = payload.GetRawText();
        if (json.Length > MaximumMessageCharacters)
        {
            PostError("Codex protocol message exceeded the 1 MB host limit.");
            return;
        }

        if (!ValidateOutbound(payload, out var responseId, out var error))
        {
            PostError(error);
            return;
        }

        await _writeGate.WaitAsync();
        try
        {
            var process = _process;
            if (process is null || process.HasExited)
            {
                PostError("Start the Codex panel before sending a request.");
                return;
            }

            await process.StandardInput.WriteLineAsync(json);
            await process.StandardInput.FlushAsync();
            if (responseId is not null)
            {
                lock (_pendingLock) _pendingServerRequests.Remove(responseId);
            }
        }
        catch (Exception exception)
        {
            PostError(exception.Message);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            var process = _process;
            var shutdown = _shutdown;
            var stdoutTask = _stdoutTask;
            var stderrTask = _stderrTask;
            _process = null;
            _shutdown = null;
            _stdoutTask = null;
            _stderrTask = null;
            _initializeSent = false;
            lock (_pendingLock) _pendingServerRequests.Clear();

            if (process is null) return;
            process.Exited -= ProcessExited;
            shutdown?.Cancel();
            try { process.StandardInput.Close(); } catch { }
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            if (stdoutTask is not null || stderrTask is not null)
            {
                try { await Task.WhenAll(stdoutTask ?? Task.CompletedTask, stderrTask ?? Task.CompletedTask); }
                catch (OperationCanceledException) { }
            }
            process.Dispose();
            shutdown?.Dispose();
            postMessage(new { type = "codex.exit", version = ProtocolVersion, exitCode = 0, message = "Codex app-server stopped." });
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private bool ValidateOutbound(JsonElement payload, out string? responseId, out string error)
    {
        responseId = null;
        error = string.Empty;
        var hasMethod = payload.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String;
        var hasId = payload.TryGetProperty("id", out var idElement);

        if (!hasMethod)
        {
            if (!hasId || (!payload.TryGetProperty("result", out _) && !payload.TryGetProperty("error", out _)))
            {
                error = "Codex response messages require an id and result or error payload.";
                return false;
            }
            responseId = idElement.GetRawText();
            lock (_pendingLock)
            {
                if (!_pendingServerRequests.Contains(responseId))
                {
                    error = "Codex response id does not match a pending approval or input request.";
                    return false;
                }
            }
            return true;
        }

        var method = methodElement.GetString() ?? string.Empty;
        if (method == "initialized")
        {
            if (hasId)
            {
                error = "The initialized notification must not include an id.";
                return false;
            }
            return true;
        }
        if (!AllowedClientMethods.Contains(method))
        {
            error = $"Codex method is not enabled by host adapter v{ProtocolVersion}: {method}";
            return false;
        }
        if (!hasId)
        {
            error = $"Codex request {method} requires an id.";
            return false;
        }
        if (method == "initialize")
        {
            if (_initializeSent)
            {
                error = "Codex app-server can only be initialized once per connection.";
                return false;
            }
            _initializeSent = true;
        }
        if (method == "account/login/start" && !IsSafeLoginRequest(payload))
        {
            error = "The Workbench only starts Codex-managed ChatGPT sign-in. API keys never pass through the web renderer.";
            return false;
        }
        if ((method == "thread/start" || method == "turn/start") && !HasSafeWorkspacePolicy(payload))
        {
            error = "Codex requests must stay inside the Hermes workspace and may not request danger-full-access.";
            return false;
        }
        return true;
    }

    private bool HasSafeWorkspacePolicy(JsonElement payload)
    {
        if (!payload.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object) return true;
        if (parameters.TryGetProperty("sandbox", out var sandbox) && sandbox.ValueKind == JsonValueKind.String && sandbox.GetString() == "danger-full-access") return false;
        if (parameters.TryGetProperty("cwd", out var cwd) && cwd.ValueKind == JsonValueKind.String)
        {
            try
            {
                var requested = Path.GetFullPath(cwd.GetString() ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var root = Path.GetFullPath(workspacePath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!requested.StartsWith(root, StringComparison.OrdinalIgnoreCase) && !requested.Equals(root, StringComparison.OrdinalIgnoreCase)) return false;
            }
            catch { return false; }
        }
        return true;
    }

    private static bool IsSafeLoginRequest(JsonElement payload)
    {
        if (!payload.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object) return false;
        return parameters.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.String
            && type.GetString() is "chatgpt" or "chatgptDeviceCode";
    }

    private async Task ReadProtocolAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null) break;
                if (line.Length > MaximumMessageCharacters)
                {
                    PostError("Codex emitted a protocol frame larger than the 1 MB host limit.");
                    continue;
                }
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var payload = document.RootElement.Clone();
                    TrackServerRequest(payload);
                    postMessage(new { type = "codex.protocol", version = ProtocolVersion, payload });
                }
                catch (JsonException)
                {
                    PostError("Codex emitted an invalid protocol frame.");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { PostError(exception.Message); }
    }

    private async Task ReadDiagnosticsAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null) break;
                if (!string.IsNullOrWhiteSpace(line)) DesktopLog.Write($"Codex app-server: {line}");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { DesktopLog.Write($"Codex diagnostics reader failed: {exception.Message}"); }
    }

    private void TrackServerRequest(JsonElement payload)
    {
        if (!payload.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String) return;
        if (!payload.TryGetProperty("id", out var id)) return;
        lock (_pendingLock) _pendingServerRequests.Add(id.GetRawText());
    }

    private void ProcessExited(object? sender, EventArgs eventArgs)
    {
        if (sender is not Process process) return;
        int? exitCode = null;
        try { exitCode = process.ExitCode; } catch { }
        postMessage(new { type = "codex.exit", version = ProtocolVersion, exitCode, message = "Codex app-server exited." });
    }

    private void PostReady(Process process) => postMessage(new
    {
        type = "codex.ready",
        version = ProtocolVersion,
        processId = process.Id,
        executable = Path.GetFileName(process.StartInfo.FileName),
        cwd = workspacePath,
        initialized = _initializeSent,
    });

    private void PostError(string message) => postMessage(new { type = "codex.error", version = ProtocolVersion, message });

    internal static string? FindCodexExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("HERMES_CODEX_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);

        var candidates = new List<string>();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        AddCandidates(candidates, Path.Combine(localAppData, "OpenAI", "Codex", "bin"), "*", "codex.exe");

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        AddCandidates(candidates, Path.Combine(profile, ".vscode", "extensions"), "openai.chatgpt-*-win32-x64", Path.Combine("bin", "windows-x86_64", "codex.exe"));

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, "codex.exe");
                if (File.Exists(candidate)) candidates.Add(candidate);
            }
            catch { }
        }

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .OrderByDescending(SafeLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static DateTime SafeLastWriteTimeUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static void AddCandidates(List<string> candidates, string root, string directoryPattern, string relativeExecutable)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var directory in Directory.EnumerateDirectories(root, directoryPattern, SearchOption.TopDirectoryOnly))
            {
                var candidate = Path.Combine(directory, relativeExecutable);
                if (File.Exists(candidate)) candidates.Add(candidate);
            }
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _writeGate.Dispose();
        _lifecycleGate.Dispose();
    }
}
