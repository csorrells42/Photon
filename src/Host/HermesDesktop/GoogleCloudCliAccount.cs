using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using HermesUsageCollectors;

namespace HermesDesktop;

internal sealed partial class GoogleCloudCliAccount : IProviderSecretSource
{
    internal const string CredentialReference = "google-cloud-application-default";
    private const int MaximumOutputCharacters = 16 * 1024;

    [GeneratedRegex("^[A-Za-z0-9._~-]{20,8192}$", RegexOptions.CultureInvariant)]
    private static partial Regex AccessTokenRegex();

    internal string? ExecutablePath => FindExecutable();
    internal bool IsInstalled => ExecutablePath is not null;

    internal bool HasApplicationDefaultCredentials
    {
        get
        {
            var applicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return !string.IsNullOrWhiteSpace(applicationData)
                && File.Exists(Path.Combine(applicationData, "gcloud", "application_default_credentials.json"));
        }
    }

    internal async Task<string?> GetProjectAsync(CancellationToken cancellationToken)
    {
        var environment = Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT")
            ?? Environment.GetEnvironmentVariable("GCLOUD_PROJECT");
        if (!string.IsNullOrWhiteSpace(environment)) return environment.Trim();
        return await RunCaptureAsync(["config", "get-value", "project", "--quiet"], cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<ProviderSecret?> GetSecretAsync(
        string credentialReference,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(credentialReference, CredentialReference, StringComparison.Ordinal)) return null;
        var token = await RunCaptureAsync(
            ["auth", "application-default", "print-access-token", "--quiet"], cancellationToken)
            .ConfigureAwait(false);
        return token is not null && AccessTokenRegex().IsMatch(token) ? ProviderSecret.FromString(token) : null;
    }

    internal bool OpenApplicationDefaultLogin()
    {
        var executable = ExecutablePath;
        if (executable is null) return false;
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            start.ArgumentList.Add("auth");
            start.ArgumentList.Add("application-default");
            start.ArgumentList.Add("login");
            return Process.Start(start) is not null;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private async Task<string?> RunCaptureAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var executable = ExecutablePath;
        if (executable is null || executable.Contains('"', StringComparison.Ordinal)
            || arguments.Any(argument => argument.Length is 0 or > 64
                || argument.Any(character => !(char.IsLetterOrDigit(character) || character is '-' or '_' or '.'))))
            return null;

        var command = $"\"\"{executable}\" {string.Join(' ', arguments)}\"";
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/s");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add(command);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        Process? process = null;
        try
        {
            process = Process.Start(start);
            if (process is null) return null;
            var outputTask = ReadBoundedAsync(process.StandardOutput, timeout.Token);
            var errorTask = ReadBoundedAsync(process.StandardError, timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            _ = await errorTask.ConfigureAwait(false);
            return process.ExitCode == 0 && output is not null ? output.Trim() : null;
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException
            or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch { }
            return null;
        }
        finally { process?.Dispose(); }
    }

    private static async Task<string?> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[1024];
        var result = new StringBuilder();
        try
        {
            while (result.Length <= MaximumOutputCharacters)
            {
                var remaining = Math.Min(buffer.Length, MaximumOutputCharacters + 1 - result.Length);
                var read = await reader.ReadAsync(buffer.AsMemory(0, remaining), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                result.Append(buffer, 0, read);
            }
            return result.Length <= MaximumOutputCharacters ? result.ToString() : null;
        }
        finally { Array.Clear(buffer); }
    }

    private static string? FindExecutable()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Google", "Cloud SDK", "google-cloud-sdk", "bin", "gcloud.cmd"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Google", "Cloud SDK", "google-cloud-sdk", "bin", "gcloud.cmd"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Google", "Cloud SDK", "google-cloud-sdk", "bin", "gcloud.cmd"),
        };
        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        return null;
    }
}
