using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace HermesDesktop;

internal sealed record WorkspaceSearchFile(string RelativePath, string FullPath, long Length);
internal sealed record WorkspaceSearchEnumeration(IReadOnlyList<WorkspaceSearchFile> Files, bool Truncated);

/// <summary>
/// Native workspace-search path authority. Renderer paths and provider classifications
/// are never treated as evidence of root containment, regular-file identity, or safety.
/// </summary>
internal sealed partial class WorkspaceSearchPathPolicy
{
    internal const int MaximumRelativePathCharacters = 1_024;
    internal const int MaximumFileBytes = 2 * 1024 * 1024;
    internal const int MaximumEnumeratedFiles = 25_000;
    internal const long MaximumReadBytes = 64L * 1024 * 1024;

    private static readonly HashSet<string> BlockedSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".aws", ".azure", ".cache", ".docker", ".gnupg", ".hermes", ".kube", ".next",
        ".nuxt", ".pnpm-store", ".serena", ".ssh", ".yarn", "artifacts", "bin", "build", "cache",
        "caches", "coverage", "data", "debug", "dist", "logs", "node_modules", "obj", "out",
        "release", "runtime", "target", "temp", "tmp", "vault",
    };

    private static readonly HashSet<string> BlockedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".npmrc", ".pypirc", "auth.json", "credentials.json", "secrets.json", "nuget.config",
        "id_dsa", "id_ecdsa", "id_ed25519", "id_rsa",
    };

    private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cer", ".cert", ".crt", ".der", ".jks", ".key", ".keystore", ".p12", ".pem", ".pfx", ".pkcs12", ".pub",
    };

    private readonly string _workspaceRoot;
    private readonly string _workspacePrefix;

    internal WorkspaceSearchPathPolicy(string workspaceRoot)
    {
        _workspaceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        _workspacePrefix = _workspaceRoot + Path.DirectorySeparatorChar;
        if (!Directory.Exists(_workspaceRoot) || HasReparsePoint(_workspaceRoot))
        {
            throw new ArgumentException("The workspace-search root must be an existing non-linked directory.", nameof(workspaceRoot));
        }
    }

    internal string WorkspaceRoot => _workspaceRoot;

    internal WorkspaceSearchEnumeration EnumerateRegularFiles(CancellationToken cancellationToken, Action<int>? reportProgress = null)
    {
        var files = new List<WorkspaceSearchFile>();
        var pending = new Stack<string>();
        pending.Push(_workspaceRoot);
        var truncated = false;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            if (TraversesReparsePoint(directory)) continue;

            string[] entries;
            try { entries = Directory.GetFileSystemEntries(directory); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
            Array.Sort(entries, ComparePaths);

            for (var index = entries.Length - 1; index >= 0; index--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = entries[index];
                var relative = Path.GetRelativePath(_workspaceRoot, entry).Replace('\\', '/');
                if (!IsSafeRelativePath(relative)) continue;

                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                    continue;
                }
                if ((attributes & (FileAttributes.Device | FileAttributes.Offline)) != 0) continue;
                if (!TryResolveRegularFile(relative, out var file)) continue;
                files.Add(file);
                if (files.Count >= MaximumEnumeratedFiles)
                {
                    truncated = true;
                    pending.Clear();
                    break;
                }
                if (files.Count % 64 == 0) reportProgress?.Invoke(files.Count);
            }
        }

        files.Sort((left, right) => CompareRelativePaths(left.RelativePath, right.RelativePath));
        reportProgress?.Invoke(files.Count);
        return new WorkspaceSearchEnumeration(files, truncated);
    }

    internal bool TryResolveRegularFile(string? relativePath, out WorkspaceSearchFile file)
    {
        file = new WorkspaceSearchFile(string.Empty, string.Empty, 0);
        if (!IsSafeRelativePath(relativePath)) return false;
        var normalized = relativePath!.Replace('\\', '/');
        string fullPath;
        try { fullPath = Path.GetFullPath(Path.Combine(_workspaceRoot, normalized.Replace('/', Path.DirectorySeparatorChar))); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        if (!IsInsideRoot(fullPath) || TraversesReparsePoint(fullPath)) return false;

        try
        {
            var attributes = File.GetAttributes(fullPath);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device | FileAttributes.Offline)) != 0) return false;
            var length = new FileInfo(fullPath).Length;
            if (length < 0 || length > MaximumFileBytes) return false;
            file = new WorkspaceSearchFile(normalized, fullPath, length);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    internal async Task<string?> ReadUtf8TextAsync(WorkspaceSearchFile file, CancellationToken cancellationToken)
    {
        if (!TryResolveRegularFile(file.RelativePath, out var current)
            || !current.FullPath.Equals(file.FullPath, StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            await using var stream = new FileStream(
                current.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > MaximumFileBytes || TraversesReparsePoint(current.FullPath)) return null;
            var bytes = new byte[checked((int)stream.Length)];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(offset), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                offset += read;
            }
            if (offset != bytes.Length || Array.IndexOf(bytes, (byte)0) >= 0) return null;
            try { return new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { return null; }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    internal async Task<string?> ReadVerifiedLineAsync(string relativePath, int zeroBasedLine, int maximumCharacters, CancellationToken cancellationToken)
    {
        if (zeroBasedLine < 0 || !TryResolveRegularFile(relativePath, out var file)) return null;
        var content = await ReadUtf8TextAsync(file, cancellationToken).ConfigureAwait(false);
        if (content is null) return null;
        var lineIndex = 0;
        var start = 0;
        for (var index = 0; index <= content.Length; index++)
        {
            if (index < content.Length && content[index] != '\n') continue;
            if (lineIndex == zeroBasedLine)
            {
                var end = index > start && content[index - 1] == '\r' ? index - 1 : index;
                return SanitizePreview(content[start..end], maximumCharacters);
            }
            lineIndex++;
            start = index + 1;
        }
        return null;
    }

    internal bool TraversesReparsePoint(string path)
    {
        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return true; }
        if (!IsInsideRoot(fullPath) || HasReparsePoint(_workspaceRoot)) return true;
        var relative = Path.GetRelativePath(_workspaceRoot, fullPath);
        var current = _workspaceRoot;
        foreach (var component in relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            try
            {
                if ((Directory.Exists(current) || File.Exists(current)) && HasReparsePoint(current)) return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return true; }
        }
        return false;
    }

    internal static bool IsSafeRelativePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Length > MaximumRelativePathCharacters
            || relativePath.Contains('\0') || Path.IsPathRooted(relativePath)
            || relativePath.StartsWith('/') || relativePath.StartsWith('\\')) return false;
        var segments = relativePath.Replace('\\', '/').Split('/');
        if (segments.Any(segment => string.IsNullOrEmpty(segment) || segment is "." or "..")) return false;
        return segments.All(IsSafeSegment);
    }

    internal static string SanitizePreview(string value, int maximumCharacters)
    {
        var boundedMaximum = Math.Clamp(maximumCharacters, 1, 1_024);
        var builder = new StringBuilder(Math.Min(value.Length, boundedMaximum));
        foreach (var character in value)
        {
            if (builder.Length >= boundedMaximum) break;
            builder.Append(char.IsControl(character) || IsDirectionalControl(character) ? ' ' : character);
        }
        return builder.ToString();
    }

    private static bool IsSafeSegment(string segment)
    {
        if (segment.Length == 0 || segment.EndsWith('.') || segment.EndsWith(' ')
            || segment.Any(character => character < 32 || character is '<' or '>' or ':' or '"' or '|' or '?' or '*'
                || IsDirectionalControl(character))) return false;
        var canonical = segment.ToLowerInvariant();
        if (BlockedSegments.Contains(canonical) || BlockedFileNames.Contains(canonical)
            || canonical == ".env" || canonical.StartsWith(".env.", StringComparison.Ordinal)
            || BlockedExtensions.Contains(Path.GetExtension(canonical))) return false;
        return !WindowsReservedName().IsMatch(canonical);
    }

    private static bool IsDirectionalControl(char character) =>
        character is >= '\u200b' and <= '\u200f'
        || character is >= '\u202a' and <= '\u202e'
        || character is >= '\u2060' and <= '\u2069'
        || character == '\ufeff';

    private bool IsInsideRoot(string fullPath) =>
        fullPath.Equals(_workspaceRoot, StringComparison.OrdinalIgnoreCase)
        || fullPath.StartsWith(_workspacePrefix, StringComparison.OrdinalIgnoreCase);

    private static bool HasReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static int ComparePaths(string left, string right) =>
        StringComparer.OrdinalIgnoreCase.Compare(left, right) is var comparison && comparison != 0
            ? comparison
            : StringComparer.Ordinal.Compare(left, right);

    private static int CompareRelativePaths(string left, string right) => ComparePaths(left, right);

    [GeneratedRegex(@"^(?:con|prn|aux|nul|com[1-9¹²³]|lpt[1-9¹²³])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex WindowsReservedName();
}
