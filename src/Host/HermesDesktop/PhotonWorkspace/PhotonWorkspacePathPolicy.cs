using System.Globalization;
using System.Text;

namespace HermesDesktop.PhotonWorkspace;

internal sealed class PhotonWorkspacePathPolicy
{
    internal const string TrashPrefix = ".photon-workspace-trash-";

    private static readonly HashSet<string> BlockedSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".ssh", ".aws", ".azure", ".docker", ".gnupg", ".kube", ".hermes",
        "vault", "vaults", "secret", "secrets", "credential", "credentials",
    };

    private static readonly HashSet<string> BlockedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".npmrc", ".pypirc", "auth.json", "credentials.json", "secrets.json", "nuget.config",
        "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519",
    };

    private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cer", ".cert", ".crt", ".der", ".jks", ".key", ".keystore", ".p12", ".pem",
        ".pfx", ".pkcs12", ".pub",
    };

    private static readonly HashSet<string> ReservedBaseNames = CreateReservedBaseNames();
    private readonly string root;
    private readonly string rootPrefix;

    internal PhotonWorkspacePathPolicy(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new PhotonWorkspaceException("workspace_root_invalid");
        }

        try
        {
            this.root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new PhotonWorkspaceException("workspace_root_invalid");
        }

        rootPrefix = this.root + Path.DirectorySeparatorChar;
    }

    internal string Root => root;

    internal string NormalizeFile(string? candidate)
    {
        return Normalize(candidate, allowEmpty: false);
    }

    internal string NormalizeDirectory(string? candidate)
    {
        return Normalize(candidate, allowEmpty: true);
    }

    internal bool TryNormalizeDiscovered(string candidate, out string normalized)
    {
        try
        {
            normalized = Normalize(candidate, allowEmpty: false);
            return true;
        }
        catch (PhotonWorkspaceException)
        {
            normalized = string.Empty;
            return false;
        }
    }

    internal string Resolve(string normalizedRelativePath)
    {
        var systemRelative = normalizedRelativePath.Replace('/', Path.DirectorySeparatorChar);
        var resolved = Path.GetFullPath(Path.Combine(root, systemRelative));
        if (!resolved.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new PhotonWorkspaceException("relative_path_outside_workspace");
        }

        return resolved;
    }

    internal string ToRelative(string absolutePath)
    {
        var relative = Path.GetRelativePath(root, absolutePath).Replace(Path.DirectorySeparatorChar, '/');
        return NormalizeFile(relative);
    }

    private static string Normalize(string? candidate, bool allowEmpty)
    {
        if (candidate is null || candidate.Length > PhotonWorkspaceContract.MaxRelativePathCharacters)
        {
            throw new PhotonWorkspaceException("relative_path_invalid");
        }

        var replaced = candidate.Replace('\\', '/');
        if (replaced.Length == 0)
        {
            if (allowEmpty)
            {
                return string.Empty;
            }

            throw new PhotonWorkspaceException("relative_path_invalid");
        }

        if (replaced.StartsWith("/", StringComparison.Ordinal) ||
            replaced.EndsWith("/", StringComparison.Ordinal) ||
            Path.IsPathRooted(replaced) ||
            replaced.Contains(':', StringComparison.Ordinal))
        {
            throw new PhotonWorkspaceException("relative_path_invalid");
        }

        var segments = replaced.Split('/', StringSplitOptions.None);
        foreach (var segment in segments)
        {
            ValidateSegment(segment);
        }

        return string.Join('/', segments);
    }

    private static void ValidateSegment(string segment)
    {
        if (segment.Length == 0 ||
            segment.Length > PhotonWorkspaceContract.MaxComponentCharacters ||
            segment is "." or ".." ||
            segment.EndsWith(' ') ||
            segment.EndsWith('.') ||
            segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new PhotonWorkspaceException("relative_path_invalid");
        }

        ValidateUnicode(segment);

        var deviceBase = segment.Split('.', 2)[0].TrimEnd(' ', '.');
        if (ReservedBaseNames.Contains(deviceBase))
        {
            throw new PhotonWorkspaceException("relative_path_reserved");
        }

        if (segment.StartsWith(".photon-workspace-", StringComparison.OrdinalIgnoreCase) ||
            BlockedSegments.Contains(segment) ||
            BlockedFileNames.Contains(segment) ||
            segment.Equals(".env", StringComparison.OrdinalIgnoreCase) ||
            segment.StartsWith(".env.", StringComparison.OrdinalIgnoreCase) ||
            BlockedExtensions.Contains(Path.GetExtension(segment)))
        {
            throw new PhotonWorkspaceException("relative_path_protected");
        }
    }

    private static void ValidateUnicode(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var width = 1;
            if (char.IsHighSurrogate(value[index]))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                {
                    throw new PhotonWorkspaceException("relative_path_invalid");
                }

                width = 2;
            }
            else if (char.IsLowSurrogate(value[index]))
            {
                throw new PhotonWorkspaceException("relative_path_invalid");
            }

            var category = CharUnicodeInfo.GetUnicodeCategory(value, index);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate)
            {
                throw new PhotonWorkspaceException("relative_path_invalid");
            }

            index += width - 1;
        }
    }

    private static HashSet<string> CreateReservedBaseNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL", "CLOCK$",
        };

        for (var index = 1; index <= 9; index++)
        {
            names.Add($"COM{index}");
            names.Add($"LPT{index}");
        }

        foreach (var suffix in new[] { "¹", "²", "³" })
        {
            names.Add("COM" + suffix);
            names.Add("LPT" + suffix);
        }

        return names;
    }
}
