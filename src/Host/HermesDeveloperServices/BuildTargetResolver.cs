namespace HermesDeveloperServices;

internal sealed class BuildTargetValidationException : Exception
{
    public BuildTargetValidationException(string code, string safeMessage) : base(safeMessage)
    {
        Code = code;
    }

    public string Code { get; }
}

internal static class BuildTargetResolver
{
    private static readonly HashSet<string> AllowedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".sln", ".slnx", ".csproj" };

    public static (string WorkspaceRoot, string TargetPath) Resolve(BuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.WorkspaceRoot))
        {
            throw new BuildTargetValidationException("invalid_workspace_root", "The workspace root is invalid.");
        }

        if (string.IsNullOrWhiteSpace(request.TargetPath))
        {
            throw new BuildTargetValidationException("invalid_target", "The build target is invalid.");
        }

        string root;
        string target;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.WorkspaceRoot));
            target = Path.GetFullPath(
                Path.IsPathFullyQualified(request.TargetPath)
                    ? request.TargetPath
                    : Path.Combine(root, request.TargetPath));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new BuildTargetValidationException("invalid_path", "The workspace or build target path is invalid.");
        }

        if (!Directory.Exists(root))
        {
            throw new BuildTargetValidationException("workspace_not_found", "The workspace root does not exist.");
        }

        if (!File.Exists(target))
        {
            throw new BuildTargetValidationException("target_not_found", "The build target does not exist.");
        }

        if (!AllowedExtensions.Contains(Path.GetExtension(target)))
        {
            throw new BuildTargetValidationException(
                "unsupported_target",
                "Only existing .sln, .slnx, and .csproj targets can be built.");
        }

        var relative = Path.GetRelativePath(root, target);
        if (Path.IsPathFullyQualified(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new BuildTargetValidationException(
                "target_outside_workspace",
                "The build target is outside the workspace root.");
        }

        RejectReparsePoints(root, target);
        return (root, target);
    }

    private static void RejectReparsePoints(string root, string target)
    {
        var current = root;
        if (IsReparsePoint(current))
        {
            throw ReparsePointFailure();
        }

        var relative = Path.GetRelativePath(root, target);
        foreach (var component in relative.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (IsReparsePoint(current))
            {
                throw ReparsePointFailure();
            }
        }
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static BuildTargetValidationException ReparsePointFailure() =>
        new("reparse_point_not_allowed", "Build paths may not contain filesystem reparse points.");
}
