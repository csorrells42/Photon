namespace HermesDeveloperServices.LanguageTooling.Python;

public sealed class PythonProjectInspector
{
    private const int MaximumSources = 512;
    private const int MaximumDirectories = 1_024;
    private const int MaximumDepth = 12;
    private const long MaximumSourceBytes = 4L * 1024 * 1024;
    private const long MaximumAggregateBytes = 64L * 1024 * 1024;

    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hermes", ".mypy_cache", ".pytest_cache", ".ruff_cache", ".tox", ".venv",
        "__pycache__", "artifacts", "build", "dist", "node_modules", "obj", "venv",
    };

    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".py", ".pyi",
    };

    private static readonly string[] MarkerNames =
    [
        "pyproject.toml", "setup.cfg", "setup.py", "requirements.txt", "Pipfile", "uv.lock",
    ];

    private readonly string _workspaceRoot;

    public PythonProjectInspector(string workspaceRoot) =>
        _workspaceRoot = TrustedToolchainPathPolicy.RequireRoot(workspaceRoot, "python_workspace");

    public PythonProjectInspection Inspect(string projectPath)
    {
        var target = TrustedToolchainPathPolicy.ResolveExistingTarget(_workspaceRoot, projectPath, "python_project");
        if (File.Exists(target)) return InspectSingleFile(target);

        var sources = new List<string>();
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((target, 0));
        var directories = 0;
        long aggregate = 0;
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (++directories > MaximumDirectories)
                throw new PythonToolingAuthorityException("python-project-directory-limit", "The Python project exceeds the directory limit.");

            string[] entries;
            try { entries = Directory.EnumerateFileSystemEntries(current.Path).ToArray(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new PythonToolingAuthorityException("python-project-enumeration-failed", "The Python project could not be enumerated safely.");
            }

            foreach (var entry in entries.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new PythonToolingAuthorityException("python-project-attributes-failed", "A Python project entry could not be validated.");
                }
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                    throw new PythonToolingAuthorityException("python-project-indirection-forbidden", "Python projects may not contain filesystem indirection.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (IgnoredDirectories.Contains(Path.GetFileName(entry))) continue;
                    if (current.Depth >= MaximumDepth)
                        throw new PythonToolingAuthorityException("python-project-depth-limit", "The Python project exceeds the directory-depth limit.");
                    queue.Enqueue((entry, current.Depth + 1));
                    continue;
                }
                if (!SourceExtensions.Contains(Path.GetExtension(entry))) continue;
                aggregate = AddSource(entry, aggregate, sources);
            }
        }

        if (sources.Count == 0)
            throw new PythonToolingAuthorityException("python-project-empty", "The selected target contains no bounded Python sources.");
        var markers = MarkerNames.Where(name => IsNormalTopLevelMarker(target, name)).ToArray();
        return CreateInspection(target, sources, markers, aggregate);
    }

    internal async ValueTask<IReadOnlyList<PythonSourceSnapshot>> SnapshotAsync(
        PythonProjectInspection inspection,
        CancellationToken cancellationToken)
    {
        var snapshots = new List<PythonSourceSnapshot>(inspection.SourcePaths.Count);
        long aggregate = 0;
        foreach (var relativePath in inspection.SourcePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = TrustedToolchainPathPolicy.ResolveExistingFile(_workspaceRoot, relativePath, "python_source");
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length < 0 || stream.Length > MaximumSourceBytes || aggregate > MaximumAggregateBytes - stream.Length)
                throw new PythonToolingAuthorityException("python-source-size-limit", "The Python sources exceed the syntax-check byte limit.");
            if (stream.Length > int.MaxValue)
                throw new PythonToolingAuthorityException("python-source-size-limit", "A Python source exceeds the syntax-check byte limit.");

            var bytes = new byte[(int)stream.Length];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(offset), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    throw new PythonToolingAuthorityException("python-source-changed", "A Python source changed during the syntax check.");
                offset += read;
            }
            if (await stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
                throw new PythonToolingAuthorityException("python-source-changed", "A Python source changed during the syntax check.");
            aggregate += bytes.Length;
            var digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
            snapshots.Add(new PythonSourceSnapshot(relativePath, bytes, digest));
        }
        return snapshots.AsReadOnly();
    }

    private PythonProjectInspection InspectSingleFile(string target)
    {
        if (!SourceExtensions.Contains(Path.GetExtension(target)))
            throw new PythonToolingAuthorityException("python-source-extension-invalid", "The selected file is not a Python source.");
        var sources = new List<string>();
        var aggregate = AddSource(target, 0, sources);
        return CreateInspection(Path.GetDirectoryName(target)!, sources, [], aggregate);
    }

    private long AddSource(string path, long aggregate, List<string> sources)
    {
        if (sources.Count >= MaximumSources)
            throw new PythonToolingAuthorityException("python-project-source-limit", "The Python project exceeds the source-file limit.");
        var length = new FileInfo(path).Length;
        if (length < 0 || length > MaximumSourceBytes || aggregate > MaximumAggregateBytes - length)
            throw new PythonToolingAuthorityException("python-project-size-limit", "The Python project exceeds the source byte limit.");
        var relative = Path.GetRelativePath(_workspaceRoot, path).Replace(Path.DirectorySeparatorChar, '/');
        sources.Add(LanguageToolingRequestPolicy.RequireWorkspacePath(relative));
        return aggregate + length;
    }

    private PythonProjectInspection CreateInspection(
        string projectRoot,
        List<string> sources,
        IReadOnlyList<string> markers,
        long aggregate)
    {
        var ordered = sources.Order(StringComparer.Ordinal).ToArray();
        var kind = markers.Contains("pyproject.toml", StringComparer.OrdinalIgnoreCase) ? "pyproject"
            : markers.Count > 0 ? "python-project"
            : ordered.Length == 1 && Path.GetDirectoryName(
                Path.Combine(_workspaceRoot, ordered[0].Replace('/', Path.DirectorySeparatorChar))) == projectRoot
                ? "single-file"
                : "source-tree";
        return new PythonProjectInspection(kind, ordered, markers, aggregate);
    }

    private static bool IsNormalTopLevelMarker(string root, string marker)
    {
        var path = Path.Combine(root, marker);
        if (!File.Exists(path)) return false;
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) == 0
                && new FileInfo(path).Length <= MaximumSourceBytes;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }
}
