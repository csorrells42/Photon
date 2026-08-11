using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace HermesDesktop;

internal sealed class DocumentBridge
{
    public const int ProtocolVersion = 1;
    public const int MaximumContentCharacters = 1_500_000;
    private const int MaximumRequestIdLength = 128;
    private readonly string _workspaceRoot;
    private readonly string _workspacePrefix;
    private readonly Action<object> _postMessage;

    public DocumentBridge(string workspaceRoot, Action<object> postMessage)
    {
        _workspaceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        _workspacePrefix = _workspaceRoot + Path.DirectorySeparatorChar;
        _postMessage = postMessage;
    }

    public void PickFile(int version, string? requestId)
    {
        if (!TryValidateEnvelope(version, requestId, out var id)) return;
        var dialog = new OpenFileDialog
        {
            Title = "Open a file in Phos Agape Aphthartos",
            InitialDirectory = _workspaceRoot,
            CheckFileExists = true,
            Multiselect = false,
            Filter = "Source and text files|*.cs;*.csproj;*.sln;*.slnx;*.ts;*.tsx;*.js;*.jsx;*.json;*.md;*.py;*.java;*.ino;*.cpp;*.h;*.css;*.html;*.xml;*.yml;*.yaml;*.txt|All files|*.*",
        };
        if (dialog.ShowDialog() != true)
        {
            _postMessage(new { type = "document.pick.result", version = ProtocolVersion, requestId = id, cancelled = true });
            return;
        }
        if (!TryWorkspaceRelativePath(dialog.FileName, allowDirectory: false, out var relative, out var error))
        {
            PostError(id, "file_outside_workspace", error);
            return;
        }
        if (IsBlockedDocumentPath(relative) || TraversesReparsePoint(dialog.FileName))
        {
            PostError(id, "protected_path", "Workbench does not open credential, runtime-data, log, linked, or Git-internal paths.");
            return;
        }
        _postMessage(new { type = "document.pick.result", version = ProtocolVersion, requestId = id, cancelled = false, path = relative });
    }

    public void PickRepository(int version, string? requestId)
    {
        if (!TryValidateEnvelope(version, requestId, out var id)) return;
        var dialog = new OpenFolderDialog
        {
            Title = "Open a Git repository in Phos Agape Aphthartos",
            InitialDirectory = _workspaceRoot,
            Multiselect = false,
        };
        if (dialog.ShowDialog() != true)
        {
            _postMessage(new { type = "document.repository.result", version = ProtocolVersion, requestId = id, cancelled = true });
            return;
        }
        if (!TryWorkspaceRelativePath(dialog.FolderName, allowDirectory: true, out var relative, out var error))
        {
            PostError(id, "repository_outside_workspace", error);
            return;
        }
        var fullPath = Path.GetFullPath(dialog.FolderName);
        if (TraversesReparsePoint(fullPath))
        {
            PostError(id, "untrusted_path", "Repository paths may not traverse a symbolic link or junction.");
            return;
        }
        if (!Directory.Exists(Path.Combine(fullPath, ".git")) && !File.Exists(Path.Combine(fullPath, ".git")))
        {
            PostError(id, "not_a_git_repository", "Select a folder containing a Git repository.");
            return;
        }
        _postMessage(new { type = "document.repository.result", version = ProtocolVersion, requestId = id, cancelled = false, path = relative });
    }

    public async Task SaveAsync(int version, string? requestId, string? relativePath, string? content, string? expectedSha256, bool saveAs)
    {
        if (!TryValidateEnvelope(version, requestId, out var id)) return;
        if (content is null || content.Length > MaximumContentCharacters)
        {
            PostError(id, "content_too_large", "The editor content is too large to save through the desktop bridge.");
            return;
        }

        string target;
        if (saveAs)
        {
            var suggested = string.IsNullOrWhiteSpace(relativePath) ? "untitled.txt" : Path.GetFileName(relativePath);
            var dialog = new SaveFileDialog
            {
                Title = "Save a file in Phos Agape Aphthartos",
                InitialDirectory = _workspaceRoot,
                FileName = suggested,
                OverwritePrompt = true,
                AddExtension = false,
                Filter = "All files|*.*",
            };
            if (dialog.ShowDialog() != true)
            {
                _postMessage(new { type = "document.save.result", version = ProtocolVersion, requestId = id, cancelled = true });
                return;
            }
            target = dialog.FileName;
        }
        else if (!TryResolveRelativeFile(relativePath, out target, out var resolveError))
        {
            PostError(id, "invalid_path", resolveError);
            return;
        }

        if (!TryWorkspaceRelativePath(target, allowDirectory: false, out var normalizedRelative, out var pathError))
        {
            PostError(id, "file_outside_workspace", pathError);
            return;
        }
        if (IsBlockedDocumentPath(normalizedRelative))
        {
            PostError(id, "protected_path", "Workbench does not open or write credential, runtime-data, log, or Git-internal paths.");
            return;
        }

        var parent = Path.GetDirectoryName(target)!;
        if (!Directory.Exists(parent) || TraversesReparsePoint(target))
        {
            PostError(id, "untrusted_path", "The selected file path is unavailable or is not a regular local path.");
            return;
        }
        var targetExisted = File.Exists(target);
        string? targetPrecondition;
        if (saveAs)
        {
            targetPrecondition = targetExisted ? ComputeSha256(target) : null;
        }
        else
        {
            var expected = expectedSha256?.Trim().ToLowerInvariant() ?? string.Empty;
            if (!targetExisted || expected.Length != 64 || expected.Any(character => !Uri.IsHexDigit(character)))
            {
                PostError(id, "invalid_precondition", "Reopen the file before saving so its current identity can be verified.");
                return;
            }
            targetPrecondition = ComputeSha256(target);
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(targetPrecondition), Convert.FromHexString(expected)))
            {
                PostError(id, "external_change", "The file changed outside the Workbench. Reopen it before saving.");
                return;
            }
        }
        var temporary = Path.Combine(parent, $".{Path.GetFileName(target)}.photon-save-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false)).ConfigureAwait(false);
            if (TraversesReparsePoint(target))
                throw new IOException("The destination path changed while the file was being saved.");
            if (targetExisted)
            {
                if (!File.Exists(target) || !string.Equals(ComputeSha256(target), targetPrecondition, StringComparison.Ordinal))
                {
                    PostError(id, "external_change", "The destination changed before the save completed. No file was overwritten.");
                    return;
                }
                File.Move(temporary, target, overwrite: true);
            }
            else
            {
                if (File.Exists(target))
                {
                    PostError(id, "external_change", "The destination was created before the save completed. No file was overwritten.");
                    return;
                }
                File.Move(temporary, target, overwrite: false);
            }
            var savedSha256 = ComputeSha256(target);
            _postMessage(new { type = "document.save.result", version = ProtocolVersion, requestId = id, cancelled = false, path = normalizedRelative, sha256 = savedSha256 });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            PostError(id, "save_failed", "The selected file could not be saved.");
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private bool TryResolveRelativeFile(string? relativePath, out string fullPath, out string error)
    {
        fullPath = string.Empty;
        error = "Select an open workspace file before saving.";
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Length > 2_048 || Path.IsPathRooted(relativePath) || relativePath.Contains('\0')) return false;
        fullPath = Path.GetFullPath(Path.Combine(_workspaceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        return TryWorkspaceRelativePath(fullPath, allowDirectory: false, out _, out error);
    }

    private bool TryWorkspaceRelativePath(string suppliedPath, bool allowDirectory, out string relativePath, out string error)
    {
        relativePath = string.Empty;
        error = "The selected path must remain inside the current workspace.";
        string fullPath;
        try { fullPath = Path.GetFullPath(suppliedPath); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        if (!fullPath.Equals(_workspaceRoot, StringComparison.OrdinalIgnoreCase)
            && !fullPath.StartsWith(_workspacePrefix, StringComparison.OrdinalIgnoreCase)) return false;
        if (allowDirectory ? !Directory.Exists(fullPath) : Directory.Exists(fullPath)) return false;
        relativePath = Path.GetRelativePath(_workspaceRoot, fullPath).Replace('\\', '/');
        if (relativePath == ".") relativePath = ".";
        error = string.Empty;
        return true;
    }

    internal static bool IsBlockedDocumentPath(string relativePath)
    {
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment => segment.Equals(".git", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("data", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("logs", StringComparison.OrdinalIgnoreCase)
            || segment.Equals(".env", StringComparison.OrdinalIgnoreCase)
            || segment.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("credentials.json", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("auth.json", StringComparison.OrdinalIgnoreCase)
            || segment.Equals(".npmrc", StringComparison.OrdinalIgnoreCase)
            || segment.Equals(".pypirc", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("nuget.config", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(segment).Equals(".key", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(segment).Equals(".pem", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(segment).Equals(".pfx", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(segment).Equals(".p12", StringComparison.OrdinalIgnoreCase));
    }

    internal bool TraversesReparsePoint(string path)
    {
        var relative = Path.GetRelativePath(_workspaceRoot, Path.GetFullPath(path));
        var current = _workspaceRoot;
        if (HasReparsePoint(current)) return true;
        foreach (var component in relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if ((Directory.Exists(current) || File.Exists(current)) && HasReparsePoint(current)) return true;
        }
        return false;
    }

    private static bool HasReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private bool TryValidateEnvelope(int version, string? requestId, out string id)
    {
        id = requestId?.Trim() ?? string.Empty;
        if (version != ProtocolVersion)
        {
            PostError(id, "unsupported_version", $"Document protocol {version} is not supported.");
            return false;
        }
        if (id.Length is 0 or > MaximumRequestIdLength || id.Any(character => !(char.IsLetterOrDigit(character) || character is '-' or '_' or ':')))
        {
            PostError(string.Empty, "invalid_request_id", "The document request identifier is invalid.");
            return false;
        }
        return true;
    }

    private void PostError(string requestId, string code, string message) => _postMessage(new
    {
        type = "document.error",
        version = ProtocolVersion,
        requestId,
        code,
        message,
    });
}
