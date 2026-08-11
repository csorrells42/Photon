namespace HermesDeveloperServices;

/// <summary>
/// Finds a bounded set of build entry points without following reparse points or generated trees.
/// The returned paths are workspace-relative and use forward slashes for the renderer contract.
/// </summary>
public static class BuildTargetDiscovery
{
    private static readonly HashSet<string> AllowedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".sln", ".slnx", ".csproj" };

    private static readonly HashSet<string> IgnoredDirectories =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".pnpm-store", ".serena", ".vscode", "artifacts", "bin", "data", "dist",
            "logs", "node_modules", "obj", "source",
        };

    public static IReadOnlyList<string> Discover(
        string workspaceRoot,
        int maximumTargets = 128,
        int maximumDirectories = 512,
        int maximumDepth = 8)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            throw new ArgumentException("The workspace root is required.", nameof(workspaceRoot));
        }
        if (maximumTargets is < 1 or > 2_000) throw new ArgumentOutOfRangeException(nameof(maximumTargets));
        if (maximumDirectories is < 1 or > 20_000) throw new ArgumentOutOfRangeException(nameof(maximumDirectories));
        if (maximumDepth is < 0 or > 64) throw new ArgumentOutOfRangeException(nameof(maximumDepth));

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        if (!Directory.Exists(root)) return Array.Empty<string>();
        if (IsReparsePoint(root)) return Array.Empty<string>();

        var targets = new List<string>();
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        var visitedDirectories = 0;

        while (queue.Count > 0 && visitedDirectories < maximumDirectories && targets.Count < maximumTargets)
        {
            var current = queue.Dequeue();
            visitedDirectories++;
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(current.Path).ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (current.Depth < maximumDepth && !IgnoredDirectories.Contains(Path.GetFileName(entry)))
                    {
                        queue.Enqueue((entry, current.Depth + 1));
                    }
                    continue;
                }

                if (!AllowedExtensions.Contains(Path.GetExtension(entry))) continue;
                var relative = Path.GetRelativePath(root, entry).Replace(Path.DirectorySeparatorChar, '/');
                if (relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathFullyQualified(relative)) continue;
                targets.Add(relative);
                if (targets.Count >= maximumTargets) break;
            }
        }

        return targets
            .OrderBy(path => Path.GetExtension(path).Equals(".csproj", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
