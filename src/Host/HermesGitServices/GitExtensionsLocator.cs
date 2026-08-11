namespace HermesGitServices;

public sealed record GitExtensionsDiscovery(SourceControlAvailability Availability);

internal sealed record LocatedGitExtensions(string FullPath);

/// <summary>Performs a bounded check for a separately installed GitExtensions.exe without executing it.</summary>
public sealed class GitExtensionsLocator
{
    private const int MaximumCandidates = 64;
    private readonly string? _configuredPath;
    private readonly IReadOnlyList<string>? _testCandidates;

    public GitExtensionsLocator(string? configuredPath = null)
    {
        _configuredPath = configuredPath;
    }

    internal GitExtensionsLocator(IReadOnlyList<string> testCandidates)
    {
        _testCandidates = testCandidates;
    }

    public GitExtensionsDiscovery Discover()
    {
        var located = Locate();
        return located is null
            ? new GitExtensionsDiscovery(new SourceControlAvailability(
                ServiceAvailability.Unavailable,
                "gitextensions_unavailable",
                "Git Extensions is optional and is not installed or configured."))
            : new GitExtensionsDiscovery(new SourceControlAvailability(ServiceAvailability.Available, null, null));
    }

    internal LocatedGitExtensions? Locate()
    {
        foreach (var candidate in Candidates().Take(MaximumCandidates))
        {
            try
            {
                if (!Path.IsPathFullyQualified(candidate)) continue;
                var fullPath = Path.GetFullPath(candidate);
                if (!Path.GetFileName(fullPath).Equals("GitExtensions.exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (!File.Exists(fullPath)) continue;
                if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0) continue;
                return new LocatedGitExtensions(fullPath);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
            }
        }

        return null;
    }

    private IEnumerable<string> Candidates()
    {
        if (_testCandidates is not null)
        {
            foreach (var candidate in _testCandidates) yield return candidate;
            yield break;
        }

        if (!string.IsNullOrWhiteSpace(_configuredPath)) yield return _configuredPath;
        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return Path.Combine(directory, "GitExtensions.exe");
        }

        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                 }.Where(root => !string.IsNullOrWhiteSpace(root)))
        {
            yield return Path.Combine(root, "GitExtensions", "GitExtensions.exe");
        }
    }
}
