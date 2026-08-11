namespace HermesDeveloperServices;

internal sealed class TrustedToolchainValidationException : Exception
{
    public TrustedToolchainValidationException(string code, string safeMessage) : base(safeMessage) => Code = code;

    public string Code { get; }
}

internal sealed record TrustedSourceEnumerationBounds
{
    public int MaximumSources { get; init; } = 512;
    public int MaximumDirectories { get; init; } = 1_024;
    public int MaximumDepth { get; init; } = 12;
    public long MaximumSourceBytes { get; init; } = 4 * 1024 * 1024;
    public long MaximumAggregateSourceBytes { get; init; } = 64 * 1024 * 1024;
}

internal sealed record TrustedPackageEnumerationBounds
{
    public int MaximumFiles { get; init; } = 8_192;
    public int MaximumDirectories { get; init; } = 4_096;
    public int MaximumDepth { get; init; } = 24;
    public long MaximumFileBytes { get; init; } = 512L * 1024 * 1024;
    public long MaximumAggregateBytes { get; init; } = 4L * 1024 * 1024 * 1024;
}

/// <summary>
/// One path authority shared by trusted-host toolchain providers. It accepts only literal relative
/// names, stays beneath the caller's explicit root, and refuses filesystem indirection.
/// </summary>
internal static class TrustedToolchainPathPolicy
{
    private static readonly char[] ForbiddenSyntax =
        ['@', ':', '$', '%', '!', '&', '|', ';', '<', '>', '`', '"', '\'', '\r', '\n', '\t', '*', '?', '{', '}', '[', ']', '(', ')', '^'];

    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "CONIN$", "CONOUT$", "CLOCK$", "CONFIG$",
        "COM\u00B9", "COM\u00B2", "COM\u00B3", "LPT\u00B9", "LPT\u00B2", "LPT\u00B3",
    };

    private static readonly HashSet<string> IgnoredSourceDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hermes", ".vs", "artifacts", "bin", "build", "cmake-build-debug",
        "cmake-build-release", "dist", "node_modules", "obj", "out",
    };

    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".c", ".cc", ".cpp", ".cxx",
    };

    public static string RequireRoot(string root, string codePrefix)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
        {
            throw Failure($"{codePrefix}_root_invalid", "The trusted filesystem root is invalid.");
        }

        string canonical;
        try { canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw Failure($"{codePrefix}_root_invalid", "The trusted filesystem root is invalid.");
        }

        if (!Directory.Exists(canonical))
        {
            throw Failure($"{codePrefix}_root_missing", "The trusted filesystem root does not exist.");
        }

        RejectAbsoluteRootChain(canonical, codePrefix);
        return canonical;
    }

    public static string ResolveExistingFile(string root, string relativePath, string codePrefix)
    {
        var resolved = ResolveRelative(root, relativePath, codePrefix);
        if (!File.Exists(resolved))
        {
            throw Failure($"{codePrefix}_file_missing", "The trusted file does not exist.");
        }

        RejectReparsePath(root, resolved, codePrefix);
        return resolved;
    }

    public static string ResolveExistingTarget(string root, string relativePath, string codePrefix)
    {
        var resolved = ResolveRelative(root, relativePath, codePrefix);
        if (!File.Exists(resolved) && !Directory.Exists(resolved))
        {
            throw Failure($"{codePrefix}_target_missing", "The requested target does not exist.");
        }

        RejectReparsePath(root, resolved, codePrefix);
        return resolved;
    }

    public static string ResolveOutputFile(string root, string relativePath, string codePrefix)
    {
        var resolved = ResolveRelative(root, relativePath, codePrefix);
        var parent = Path.GetDirectoryName(resolved);
        if (parent is null || !Directory.Exists(parent))
        {
            throw Failure($"{codePrefix}_output_parent_missing", "The output directory does not exist.");
        }

        RejectReparsePath(root, parent, codePrefix);
        if (File.Exists(resolved)) RejectReparsePath(root, resolved, codePrefix);
        return resolved;
    }

    public static string CreateOwnedTemporaryDirectory(
        string root,
        string parent,
        string literalPrefix,
        string codePrefix)
    {
        if (string.IsNullOrWhiteSpace(literalPrefix)
            || literalPrefix.IndexOfAny(ForbiddenSyntax) >= 0
            || literalPrefix.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw Failure($"{codePrefix}_prefix_invalid", "The provider temporary-directory prefix is invalid.");
        }
        var trustedRoot = RequireRoot(root, codePrefix);
        var trustedParent = Path.GetFullPath(parent);
        if (!Directory.Exists(trustedParent) || !IsWithin(trustedRoot, trustedParent))
            throw Failure($"{codePrefix}_parent_invalid", "The provider temporary-directory parent is invalid.");
        RejectReparsePath(trustedRoot, trustedParent, codePrefix);

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var candidate = Path.Combine(trustedParent, $"{literalPrefix}{Guid.NewGuid():N}");
            if (Directory.Exists(candidate) || File.Exists(candidate)) continue;
            try { Directory.CreateDirectory(candidate); }
            catch (IOException) { continue; }
            RejectReparsePath(trustedRoot, candidate, codePrefix);
            return candidate;
        }
        throw Failure($"{codePrefix}_creation_failed", "A private provider directory could not be created.");
    }

    public static long RequireNormalFile(
        string root,
        string path,
        string codePrefix,
        long minimumBytes,
        long maximumBytes)
    {
        if (!Path.IsPathFullyQualified(path) || !IsWithin(root, Path.GetFullPath(path)) || !File.Exists(path))
            throw Failure($"{codePrefix}_missing", "The expected provider file was not created.");
        RejectReparsePath(root, path, codePrefix);
        FileAttributes attributes;
        long length;
        try
        {
            attributes = File.GetAttributes(path);
            length = new FileInfo(path).Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Failure($"{codePrefix}_unreadable", "The provider file could not be validated.");
        }
        if ((attributes & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0
            || length < minimumBytes
            || length > maximumBytes)
        {
            throw Failure($"{codePrefix}_invalid", "The provider file is not a bounded normal file.");
        }
        return length;
    }

    public static IReadOnlyList<string> EnumeratePackageFiles(
        string root,
        string excludedReceiptPath,
        TrustedPackageEnumerationBounds bounds)
    {
        ValidatePackageBounds(bounds);
        var files = new List<string>();
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        var directories = 0;
        long aggregate = 0;
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (++directories > bounds.MaximumDirectories)
                throw Failure("package_directory_limit", "The pinned package exceeds its directory limit.");
            string[] entries;
            try { entries = Directory.EnumerateFileSystemEntries(current.Path).ToArray(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw Failure("package_enumeration_failed", "The pinned package could not be enumerated safely.");
            }
            foreach (var entry in entries.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw Failure("package_attributes_failed", "A pinned package entry could not be validated.");
                }
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                    throw Failure("package_indirection_forbidden", "Pinned packages may contain only normal files and directories.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (current.Depth >= bounds.MaximumDepth)
                        throw Failure("package_depth_limit", "The pinned package exceeds its depth limit.");
                    queue.Enqueue((entry, current.Depth + 1));
                    continue;
                }
                if (PathsEqual(entry, excludedReceiptPath)) continue;
                if (files.Count >= bounds.MaximumFiles)
                    throw Failure("package_file_limit", "The pinned package exceeds its file limit.");
                var length = new FileInfo(entry).Length;
                if (length < 0 || length > bounds.MaximumFileBytes || aggregate > bounds.MaximumAggregateBytes - length)
                    throw Failure("package_size_limit", "The pinned package exceeds its byte limit.");
                aggregate += length;
                files.Add(entry);
            }
        }
        return files.OrderBy(path => Path.GetRelativePath(root, path), StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<string> EnumerateSources(
        string root,
        string target,
        TrustedSourceEnumerationBounds bounds)
    {
        ValidateBounds(bounds);
        if (File.Exists(target))
        {
            ValidateSource(target, bounds, 0);
            return [target];
        }

        var sources = new List<string>();
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((target, 0));
        var visitedDirectories = 0;
        long aggregateBytes = 0;

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (++visitedDirectories > bounds.MaximumDirectories)
                throw Failure("source_directory_limit", "The native source tree exceeds the directory limit.");

            string[] entries;
            try { entries = Directory.EnumerateFileSystemEntries(current.Path).ToArray(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw Failure("source_enumeration_failed", "The native source tree could not be enumerated safely.");
            }

            foreach (var entry in entries.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw Failure("source_attributes_failed", "A native source entry could not be validated.");
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw Failure("source_reparse_point", "Native source paths may not contain reparse points.");

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (IgnoredSourceDirectories.Contains(Path.GetFileName(entry))) continue;
                    if (current.Depth >= bounds.MaximumDepth)
                        throw Failure("source_depth_limit", "The native source tree exceeds the depth limit.");
                    queue.Enqueue((entry, current.Depth + 1));
                    continue;
                }

                if (!SourceExtensions.Contains(Path.GetExtension(entry))) continue;
                if (sources.Count >= bounds.MaximumSources)
                    throw Failure("source_count_limit", "The native source tree exceeds the source-file limit.");
                aggregateBytes = ValidateSource(entry, bounds, aggregateBytes);
                sources.Add(entry);
            }
        }

        return sources.OrderBy(path => Path.GetRelativePath(root, path), StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static bool TryWorkspaceRelative(string root, string candidate, out string relative)
    {
        relative = string.Empty;
        try
        {
            var canonical = Path.GetFullPath(Path.IsPathFullyQualified(candidate) ? candidate : Path.Combine(root, candidate));
            if (!IsWithin(root, canonical)) return false;
            RejectReparsePath(root, canonical, "diagnostic");
            relative = Path.GetRelativePath(root, canonical).Replace(Path.DirectorySeparatorChar, '/');
            return relative.Length > 0 && !relative.Equals(".", StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException
                                            or IOException or UnauthorizedAccessException
                                            or TrustedToolchainValidationException)
        {
            return false;
        }
    }

    private static string ResolveRelative(string root, string relativePath, string codePrefix)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathFullyQualified(relativePath)
            || relativePath.StartsWith('\\')
            || relativePath.StartsWith('/')
            || relativePath.IndexOfAny(ForbiddenSyntax) >= 0)
        {
            throw Failure($"{codePrefix}_path_invalid", "Only literal workspace-relative paths are accepted.");
        }

        var components = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (components.Length == 0 || components.Any(IsUnsafeComponent))
        {
            throw Failure($"{codePrefix}_path_invalid", "The relative path contains an unsafe component.");
        }

        string resolved;
        try { resolved = Path.GetFullPath(Path.Combine(root, relativePath)); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw Failure($"{codePrefix}_path_invalid", "The relative path is invalid.");
        }

        if (!IsWithin(root, resolved))
            throw Failure($"{codePrefix}_path_outside_root", "The relative path escapes its trusted root.");
        return resolved;
    }

    private static bool IsUnsafeComponent(string component)
    {
        if (component is "." or ".." || component.EndsWith(' ') || component.EndsWith('.')) return true;
        var stem = component.Split('.')[0].TrimEnd(' ', '.');
        return DeviceNames.Contains(stem) || component.StartsWith('-');
    }

    private static long ValidateSource(string path, TrustedSourceEnumerationBounds bounds, long aggregateBytes)
    {
        if (!SourceExtensions.Contains(Path.GetExtension(path)))
            throw Failure("source_extension_unsupported", "Only .c, .cc, .cpp, and .cxx sources are accepted.");
        long length;
        try { length = new FileInfo(path).Length; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Failure("source_read_failed", "A native source file could not be read safely.");
        }
        if (length > bounds.MaximumSourceBytes)
            throw Failure("source_size_limit", "A native source file exceeds the size limit.");
        if (aggregateBytes > bounds.MaximumAggregateSourceBytes - length)
            throw Failure("source_aggregate_limit", "The native sources exceed the aggregate size limit.");
        return aggregateBytes + length;
    }

    private static void RejectReparsePath(string root, string target, string codePrefix)
    {
        if (!IsWithin(root, target))
            throw Failure($"{codePrefix}_path_outside_root", "The path escapes its trusted root.");

        var current = root;
        RejectIfReparse(current, codePrefix);
        var relative = Path.GetRelativePath(root, target);
        foreach (var component in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (File.Exists(current) || Directory.Exists(current)) RejectIfReparse(current, codePrefix);
        }
    }

    private static void RejectIfReparse(string path, string codePrefix)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw Failure($"{codePrefix}_reparse_point", "Trusted toolchain paths may not contain reparse points.");
    }

    private static void RejectAbsoluteRootChain(string root, string codePrefix)
    {
        var chain = new Stack<string>();
        for (var current = new DirectoryInfo(root); current is not null; current = current.Parent)
            chain.Push(current.FullName);
        while (chain.Count > 0) RejectIfReparse(chain.Pop(), codePrefix);
    }

    private static bool IsWithin(string root, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.Equals(root, comparison)
            || path.StartsWith(root + Path.DirectorySeparatorChar, comparison)
            || path.StartsWith(root + Path.AltDirectorySeparatorChar, comparison);
    }

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.GetFullPath(left),
        Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void ValidatePackageBounds(TrustedPackageEnumerationBounds bounds)
    {
        if (bounds.MaximumFiles is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(bounds.MaximumFiles));
        if (bounds.MaximumDirectories is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(bounds.MaximumDirectories));
        if (bounds.MaximumDepth is < 0 or > 64) throw new ArgumentOutOfRangeException(nameof(bounds.MaximumDepth));
        if (bounds.MaximumFileBytes is < 1 or > 2L * 1024 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(bounds.MaximumFileBytes));
        if (bounds.MaximumAggregateBytes is < 1 or > 16L * 1024 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(bounds.MaximumAggregateBytes));
    }

    private static void ValidateBounds(TrustedSourceEnumerationBounds bounds)
    {
        if (bounds.MaximumSources is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(bounds.MaximumSources));
        if (bounds.MaximumDirectories is < 1 or > 20_000) throw new ArgumentOutOfRangeException(nameof(bounds.MaximumDirectories));
        if (bounds.MaximumDepth is < 0 or > 64) throw new ArgumentOutOfRangeException(nameof(bounds.MaximumDepth));
        if (bounds.MaximumSourceBytes is < 1 or > 64 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(bounds.MaximumSourceBytes));
        if (bounds.MaximumAggregateSourceBytes is < 1 or > 1024L * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(bounds.MaximumAggregateSourceBytes));
    }

    private static TrustedToolchainValidationException Failure(string code, string message) => new(code, message);
}
