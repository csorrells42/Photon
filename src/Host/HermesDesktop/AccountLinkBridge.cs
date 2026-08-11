using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace HermesDesktop;

internal sealed class AccountLinkBridge
{
    public const int ProtocolVersion = 1;
    private const int MaximumStatusCharacters = 32 * 1024;
    private readonly Action<object> _postMessage;

    public AccountLinkBridge(Action<object> postMessage) => _postMessage = postMessage;

    public async Task PostStatusAsync(int version, string? requestId)
    {
        if (!TryEnvelope(version, requestId, out var id)) return;
        var googleCloud = new GoogleCloudCliAccount();
        var claude = FindExecutable("claude", new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "claude.cmd"),
        });
        var antigravity = FindExecutable("agy", new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agy", "bin", "agy.exe"),
        });
        var claudeLinked = claude is not null && await IsClaudeLinkedAsync(claude);
        _postMessage(new
        {
            type = "accountLink.status.result",
            version = ProtocolVersion,
            requestId = id,
            accounts = new[]
            {
                new { provider = "claude", installed = claude is not null, linked = claudeLinked, status = claude is null ? "Install Claude Code to enable secure account linking." : claudeLinked ? "Claude Code reports an authenticated account." : "Claude Code is installed; sign in to link the account." },
                new { provider = "antigravity", installed = antigravity is not null, linked = false, status = antigravity is null ? "Install Google Antigravity CLI to enable account linking." : "Antigravity is installed; open its official sign-in flow to link the account." },
                new { provider = "google-cloud", installed = googleCloud.IsInstalled, linked = googleCloud.HasApplicationDefaultCredentials, status = !googleCloud.IsInstalled ? "Install the official Google Cloud CLI to connect project-level Gemini telemetry." : googleCloud.HasApplicationDefaultCredentials ? "Google Application Default Credentials are present; Refresh validates telemetry access." : "Google Cloud CLI is installed; link Application Default Credentials to enable Gemini telemetry." },
            },
        });
    }

    public void Open(int version, string? requestId, string? provider)
    {
        if (!TryEnvelope(version, requestId, out var id)) return;
        var normalized = provider?.Trim().ToLowerInvariant();
        if (normalized == "google-cloud")
        {
            var googleCloud = new GoogleCloudCliAccount();
            if (googleCloud.OpenApplicationDefaultLogin())
            {
                _postMessage(new { type = "accountLink.open.result", version = ProtocolVersion, requestId = id, provider = normalized, opened = true });
            }
            else PostError(id, googleCloud.IsInstalled ? "launch_failed" : "not_installed",
                googleCloud.IsInstalled
                    ? "The Google Application Default Credentials login could not be opened."
                    : "The official Google Cloud CLI is not installed.");
            return;
        }
        var executable = normalized switch
        {
            "claude" => FindExecutable("claude", new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "claude.cmd"),
            }),
            "antigravity" => FindExecutable("agy", new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agy", "bin", "agy.exe"),
            }),
            _ => null,
        };
        if (executable is null)
        {
            PostError(id, "not_installed", "The provider's official local sign-in client is not installed.");
            return;
        }

        try
        {
            var start = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            if (normalized == "claude")
            {
                start.ArgumentList.Add("auth");
                start.ArgumentList.Add("login");
            }
            Process.Start(start);
            _postMessage(new { type = "accountLink.open.result", version = ProtocolVersion, requestId = id, provider = normalized, opened = true });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            PostError(id, "launch_failed", "The provider's official sign-in client could not be opened.");
        }
    }

    private static async Task<bool> IsClaudeLinkedAsync(string executable)
    {
        if (!Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("auth");
            start.ArgumentList.Add("status");
            using var process = Process.Start(start);
            if (process is null) return false;
            var outputTask = ReadBoundedAsync(process.StandardOutput, timeout.Token);
            var errorTask = ReadBoundedAsync(process.StandardError, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            _ = await errorTask;
            if (process.ExitCode != 0) return false;
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            return (root.TryGetProperty("loggedIn", out var loggedIn) && loggedIn.ValueKind == JsonValueKind.True)
                || (root.TryGetProperty("authenticated", out var authenticated) && authenticated.ValueKind == JsonValueKind.True)
                || root.TryGetProperty("authMethod", out _);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or JsonException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[4_096];
        var result = new System.Text.StringBuilder();
        while (result.Length < MaximumStatusCharacters)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, MaximumStatusCharacters - result.Length)), cancellationToken);
            if (read == 0) break;
            result.Append(buffer, 0, read);
        }
        return result.ToString();
    }

    private static string? FindExecutable(string stem, IEnumerable<string> fixedCandidates)
    {
        foreach (var candidate in fixedCandidates)
        {
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;
        var extensions = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat" } : new[] { string.Empty };
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                try
                {
                    var candidate = Path.GetFullPath(Path.Combine(directory, stem + extension));
                    if (File.Exists(candidate)) return candidate;
                }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { }
            }
        }
        return null;
    }

    private bool TryEnvelope(int version, string? requestId, out string id)
    {
        id = requestId?.Trim() ?? string.Empty;
        if (version != ProtocolVersion || id.Length is 0 or > 128 || id.Any(character => !(char.IsLetterOrDigit(character) || character is '-' or '_' or ':')))
        {
            PostError(string.Empty, "invalid_request", "The account-link request was invalid.");
            return false;
        }
        return true;
    }

    private void PostError(string requestId, string code, string message) => _postMessage(new
    {
        type = "accountLink.error",
        version = ProtocolVersion,
        requestId,
        code,
        message,
    });
}
