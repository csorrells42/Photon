using System.Text;

namespace PhotonCadRuntime;

public sealed record CadRelativePath
{
    public CadRelativePath(string value)
    {
        Value = Normalize(value);
        Segments = Array.AsReadOnly(Value.Split('/'));
    }

    public string Value { get; }
    public IReadOnlyList<string> Segments { get; }
    public override string ToString() => Value;

    private static string Normalize(string? candidate)
    {
        if (candidate is null)
            throw new CadContractException("required", nameof(candidate));
        if (candidate.Length == 0 || candidate.Length > CadContractLimits.RelativePathLength ||
            !string.Equals(candidate, candidate.Trim(), StringComparison.Ordinal))
            throw new CadContractException("invalid_relative_path", nameof(candidate));

        string normalized;
        try
        {
            normalized = candidate.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            throw new CadContractException("invalid_unicode", nameof(candidate));
        }

        if (IsDeviceOrNetworkPath(normalized) || Path.IsPathRooted(normalized) || normalized[0] is '/' or '\\')
            throw new CadContractException("rooted_path_rejected", nameof(candidate));
        if (normalized.Contains(':', StringComparison.Ordinal))
            throw new CadContractException("alternate_stream_rejected", nameof(candidate));

        normalized = normalized.Replace('\\', '/');
        var segments = normalized.Split('/');
        if (segments.Length == 0 || segments.Any(segment => segment.Length == 0))
            throw new CadContractException("invalid_relative_path", nameof(candidate));

        foreach (var segment in segments)
            ValidateSegment(segment, nameof(candidate));

        var canonical = string.Join('/', segments);
        if (canonical.Length > CadContractLimits.RelativePathLength)
            throw new CadContractException("invalid_relative_path", nameof(candidate));
        return canonical;
    }

    private static bool IsDeviceOrNetworkPath(string value) =>
        value.StartsWith("\\\\", StringComparison.Ordinal) ||
        value.StartsWith("//", StringComparison.Ordinal) ||
        value.StartsWith("\\?\\", StringComparison.Ordinal) ||
        value.StartsWith("\\.\\", StringComparison.Ordinal) ||
        value.StartsWith("\\??\\", StringComparison.Ordinal) ||
        value.StartsWith("//?/", StringComparison.Ordinal) ||
        value.StartsWith("//./", StringComparison.Ordinal);

    private static void ValidateSegment(string segment, string field)
    {
        if (segment is "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.'))
            throw new CadContractException("path_traversal_rejected", field);
        if (segment.Length > 255)
            throw new CadContractException("path_segment_too_long", field);

        foreach (var character in segment)
        {
            if (character < 32 || character is '<' or '>' or '"' or '|' or '?' or '*' or ':' or '/' or '\\')
                throw new CadContractException("invalid_path_character", field);
        }

        var stem = segment.Split('.', 2)[0];
        if (IsReservedDeviceName(stem))
            throw new CadContractException("device_path_rejected", field);
    }

    private static bool IsReservedDeviceName(string stem)
    {
        if (stem.Equals("con", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("prn", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("aux", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("nul", StringComparison.OrdinalIgnoreCase))
            return true;

        if (stem.Length == 4 && stem[3] is >= '1' and <= '9')
        {
            var prefix = stem[..3];
            return prefix.Equals("com", StringComparison.OrdinalIgnoreCase) ||
                prefix.Equals("lpt", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}

public enum CadPathEntryKind
{
    Missing,
    File,
    Directory,
    Other,
}

public sealed record CadPathInspection(CadPathEntryKind Kind, bool IsReparsePoint, bool HasMultipleHardLinks = false);

public interface ICadPathInspector
{
    CadPathInspection Inspect(string absolutePath);
}

public sealed class CadResolvedPath
{
    internal CadResolvedPath(string trustedRoot, CadRelativePath relativePath, string absolutePath)
    {
        TrustedRoot = trustedRoot;
        RelativePath = relativePath;
        AbsolutePath = absolutePath;
    }

    public string TrustedRoot { get; }
    public CadRelativePath RelativePath { get; }
    public string AbsolutePath { get; }
}

public static class CadPathPolicy
{
    public static CadResolvedPath ResolveUnderTrustedRoot(
        string trustedRoot,
        CadRelativePath relativePath,
        ICadPathInspector inspector)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(inspector);
        if (string.IsNullOrWhiteSpace(trustedRoot) || !Path.IsPathFullyQualified(trustedRoot) || IsUntrustedRoot(trustedRoot))
            throw new CadContractException("invalid_trusted_root", nameof(trustedRoot));

        string root;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trustedRoot));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new CadContractException("invalid_trusted_root", nameof(trustedRoot));
        }

        var rootInspection = Inspect(inspector, root);
        if (rootInspection.Kind != CadPathEntryKind.Directory)
            throw new CadContractException("trusted_root_missing", nameof(trustedRoot));
        RejectLink(rootInspection, nameof(trustedRoot));

        var current = root;
        for (var index = 0; index < relativePath.Segments.Count; index++)
        {
            current = Path.Combine(current, relativePath.Segments[index]);
            var inspection = Inspect(inspector, current);
            RejectLink(inspection, nameof(relativePath));
            if (index < relativePath.Segments.Count - 1 &&
                inspection.Kind is not CadPathEntryKind.Directory and not CadPathEntryKind.Missing)
                throw new CadContractException("path_parent_not_directory", nameof(relativePath));
        }

        var absolutePath = Path.GetFullPath(current);
        var rootPrefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!absolutePath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new CadContractException("outside_trusted_root", nameof(relativePath));

        return new CadResolvedPath(root, relativePath, absolutePath);
    }

    private static CadPathInspection Inspect(ICadPathInspector inspector, string path)
    {
        try
        {
            return inspector.Inspect(path) ?? throw new CadContractException("path_inspection_failed", nameof(path));
        }
        catch (CadContractException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new CadContractException("path_inspection_failed", nameof(path));
        }
    }

    private static void RejectLink(CadPathInspection inspection, string field)
    {
        if (inspection.IsReparsePoint)
            throw new CadContractException("reparse_point_rejected", field);
        if (inspection.HasMultipleHardLinks)
            throw new CadContractException("hard_link_rejected", field);
    }

    private static bool IsUntrustedRoot(string value) =>
        value.StartsWith("\\\\", StringComparison.Ordinal) ||
        value.StartsWith("//", StringComparison.Ordinal) ||
        value.StartsWith("\\?\\", StringComparison.Ordinal) ||
        value.StartsWith("\\.\\", StringComparison.Ordinal) ||
        value.StartsWith("\\??\\", StringComparison.Ordinal);
}
