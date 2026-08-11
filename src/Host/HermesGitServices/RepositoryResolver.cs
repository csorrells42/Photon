using System.Security.Cryptography;

namespace HermesGitServices;

public sealed class ValidatedRepository
{
    internal ValidatedRepository(string repositoryId, string displayName, string rootPath, string authorityRoot)
    {
        RepositoryId = repositoryId;
        DisplayName = displayName;
        RootPath = rootPath;
        AuthorityRoot = authorityRoot;
    }

    public string RepositoryId { get; }

    public string DisplayName { get; }

    internal string RootPath { get; }

    internal string AuthorityRoot { get; }
}

public sealed class RepositoryResolutionException : Exception
{
    internal RepositoryResolutionException(string code, string message) : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>Resolves one renderer-relative candidate under a fixed native workspace authority.</summary>
public sealed class RepositoryResolver
{
    private readonly string _authorityRoot;
    private readonly StringComparison _pathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public RepositoryResolver(string workspaceAuthorityRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceAuthorityRoot);
        _authorityRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceAuthorityRoot));
        if (!Directory.Exists(_authorityRoot))
        {
            throw new DirectoryNotFoundException("The source-control workspace authority does not exist.");
        }

        RejectReparsePoints(_authorityRoot, _authorityRoot);
    }

    public ValidatedRepository Resolve(string workspaceRelativePath)
    {
        RejectRendererPath(workspaceRelativePath);
        var candidate = Path.GetFullPath(Path.Combine(_authorityRoot, workspaceRelativePath));
        EnsureInsideAuthority(candidate);
        if (!Directory.Exists(candidate))
        {
            throw Failure("path_not_found", "The requested repository path does not exist.");
        }

        RejectReparsePoints(_authorityRoot, candidate);
        var current = new DirectoryInfo(candidate);
        while (current is not null && IsInsideAuthority(current.FullName))
        {
            var metadata = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(metadata))
            {
                RejectReparsePoints(_authorityRoot, metadata);
                return Create(current.FullName);
            }

            if (File.Exists(metadata))
            {
                ValidateGitFile(current.FullName, metadata);
                return Create(current.FullName);
            }

            if (current.FullName.Equals(_authorityRoot, _pathComparison)) break;
            current = current.Parent;
        }

        throw Failure("not_a_repository", "The selected workspace path is not inside a Git repository.");
    }

    internal void Revalidate(ValidatedRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        if (!repository.AuthorityRoot.Equals(_authorityRoot, _pathComparison))
        {
            throw Failure("repository_authority_changed", "The repository authority is no longer valid.");
        }

        EnsureInsideAuthority(repository.RootPath);
        if (!Directory.Exists(repository.RootPath))
        {
            throw Failure("repository_missing", "The repository is no longer available.");
        }

        RejectReparsePoints(_authorityRoot, repository.RootPath);
        var metadata = Path.Combine(repository.RootPath, ".git");
        if (Directory.Exists(metadata)) RejectReparsePoints(_authorityRoot, metadata);
        else if (File.Exists(metadata)) ValidateGitFile(repository.RootPath, metadata);
        else throw Failure("repository_changed", "The selected path is no longer a Git repository.");
    }

    private ValidatedRepository Create(string rootPath)
    {
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        EnsureInsideAuthority(canonical);
        var opaqueId = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        return new ValidatedRepository(opaqueId, Path.GetFileName(canonical), canonical, _authorityRoot);
    }

    private void ValidateGitFile(string repositoryRoot, string metadataFile)
    {
        RejectReparsePoints(_authorityRoot, metadataFile);
        string value;
        try
        {
            value = File.ReadAllText(metadataFile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Failure("repository_metadata_unavailable", "Repository metadata could not be validated.");
        }

        const string prefix = "gitdir:";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || value.Length > 32 * 1024 || value.Contains('\0'))
        {
            throw Failure("repository_metadata_invalid", "Repository metadata is invalid.");
        }

        var declared = value[prefix.Length..].Trim();
        var gitDirectory = Path.GetFullPath(Path.IsPathFullyQualified(declared)
            ? declared
            : Path.Combine(repositoryRoot, declared));
        EnsureInsideAuthority(gitDirectory);
        if (!Directory.Exists(gitDirectory))
        {
            throw Failure("repository_metadata_missing", "Repository metadata is unavailable.");
        }

        RejectReparsePoints(_authorityRoot, gitDirectory);
    }

    private static void RejectRendererPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || path.Contains('\0'))
            throw Failure("invalid_path", "The repository path is invalid.");
        if (Path.IsPathFullyQualified(path) || Path.IsPathRooted(path))
            throw Failure("rooted_path_rejected", "Repository paths must be workspace-relative.");
        var components = path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        if (components.Any(component => component is "." or "..") && path != ".")
            throw Failure("path_traversal_rejected", "Parent traversal is not allowed.");
    }

    private void EnsureInsideAuthority(string path)
    {
        if (!IsInsideAuthority(path))
            throw Failure("outside_workspace", "The repository path is outside the workspace authority.");
    }

    private bool IsInsideAuthority(string path)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return fullPath.Equals(_authorityRoot, _pathComparison)
            || fullPath.StartsWith(_authorityRoot + Path.DirectorySeparatorChar, _pathComparison);
    }

    private void RejectReparsePoints(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var current = root;
        if (IsReparsePoint(current)) throw ReparseFailure();
        foreach (var component in relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if ((Directory.Exists(current) || File.Exists(current)) && IsReparsePoint(current)) throw ReparseFailure();
        }
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static RepositoryResolutionException ReparseFailure() =>
        Failure("reparse_point_rejected", "Repository paths may not traverse a symbolic link or junction.");

    private static RepositoryResolutionException Failure(string code, string message) => new(code, message);
}
