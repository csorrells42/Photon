namespace HermesGitServices;

public sealed record GitExecutableDiscovery(
    SourceControlAvailability Availability,
    string? DisplayVersion = null);

internal sealed record LocatedGitExecutable(string FullPath);

/// <summary>Performs bounded native discovery and never executes a discovered candidate.</summary>
public sealed class GitExecutableLocator
{
    private const int MaximumCandidates = 64;
    private readonly string? _configuredPath;
    private readonly IReadOnlyList<string>? _testCandidates;

    public GitExecutableLocator(string? configuredPath = null)
    {
        _configuredPath = configuredPath;
    }

    internal GitExecutableLocator(IReadOnlyList<string> testCandidates)
    {
        _testCandidates = testCandidates;
    }

    public GitExecutableDiscovery Discover()
    {
        var located = Locate();
        return located is null
            ? new GitExecutableDiscovery(new SourceControlAvailability(
                ServiceAvailability.Unavailable,
                "git_unavailable",
                "Git is not installed or could not be found."))
            : new GitExecutableDiscovery(new SourceControlAvailability(ServiceAvailability.Available, null, null));
    }

    internal LocatedGitExecutable? Locate()
    {
        foreach (var candidate in Candidates().Take(MaximumCandidates))
        {
            try
            {
                if (!Path.IsPathFullyQualified(candidate)) continue;
                var fullPath = Path.GetFullPath(candidate);
                if (!Path.GetFileName(fullPath).Equals("git.exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (!File.Exists(fullPath) || IsReparsePoint(fullPath)) continue;
                return new LocatedGitExecutable(fullPath);
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
            {
                yield return Path.Combine(directory, "git.exe");
            }
        }

        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                 }.Where(root => !string.IsNullOrWhiteSpace(root)))
        {
            yield return Path.Combine(root, "Git", "cmd", "git.exe");
            yield return Path.Combine(root, "Git", "bin", "git.exe");
        }
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
