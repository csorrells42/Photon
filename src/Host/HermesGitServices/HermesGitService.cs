using System.Collections.Concurrent;

namespace HermesGitServices;

/// <summary>Provider-neutral Phase 1A facade exposed to the desktop host integration lane.</summary>
public sealed class HermesGitService
{
    private static readonly IReadOnlyList<string> Operations =
        ContractCollections.Freeze(new[] { "describe", "resolveRepository", "getStatus", "openGitExtensions" });
    private static readonly IReadOnlyList<string> GitExtensionsSurfaces =
        ContractCollections.Freeze(new[] { GitExtensionsLauncher.BrowseSurface });

    private readonly RepositoryResolver _resolver;
    private readonly GitExecutableLocator _gitLocator;
    private readonly GitExtensionsLocator _gitExtensionsLocator;
    private readonly IGitProcessHost? _gitProcessHost;
    private readonly IGitExtensionsProcessLauncher? _gitExtensionsProcessLauncher;
    private readonly GitCommandBounds _bounds;
    private readonly ConcurrentDictionary<string, ValidatedRepository> _repositories = new(StringComparer.Ordinal);

    public HermesGitService(
        string workspaceAuthorityRoot,
        string? configuredGitPath = null,
        string? configuredGitExtensionsPath = null,
        GitCommandBounds? bounds = null)
        : this(
            new RepositoryResolver(workspaceAuthorityRoot),
            new GitExecutableLocator(configuredGitPath),
            new GitExtensionsLocator(configuredGitExtensionsPath),
            null,
            null,
            bounds)
    {
    }

    internal HermesGitService(
        RepositoryResolver resolver,
        GitExecutableLocator gitLocator,
        GitExtensionsLocator gitExtensionsLocator,
        IGitProcessHost? gitProcessHost,
        IGitExtensionsProcessLauncher? gitExtensionsProcessLauncher,
        GitCommandBounds? bounds = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _gitLocator = gitLocator ?? throw new ArgumentNullException(nameof(gitLocator));
        _gitExtensionsLocator = gitExtensionsLocator ?? throw new ArgumentNullException(nameof(gitExtensionsLocator));
        _gitProcessHost = gitProcessHost;
        _gitExtensionsProcessLauncher = gitExtensionsProcessLauncher;
        _bounds = bounds ?? new GitCommandBounds();
    }

    public SourceControlDescription Describe()
    {
        var git = _gitLocator.Discover().Availability;
        var gitExtensions = _gitExtensionsLocator.Discover().Availability;
        return new SourceControlDescription(
            SourceControlProtocol.Version,
            git,
            gitExtensions,
            Operations,
            gitExtensions.State == ServiceAvailability.Available
                ? GitExtensionsSurfaces
                : Array.Empty<string>());
    }

    public RepositoryResolutionResult ResolveRepository(string requestId, string workspaceRelativePath)
    {
        if (!ValidRequestId(requestId))
            return ResolutionFailure(requestId, "invalid_request_id", "The request identifier is invalid.");

        try
        {
            var repository = _resolver.Resolve(workspaceRelativePath);
            _repositories[repository.RepositoryId] = repository;
            return new RepositoryResolutionResult(
                SourceControlProtocol.Version,
                requestId,
                true,
                repository.RepositoryId,
                repository.DisplayName,
                null);
        }
        catch (RepositoryResolutionException exception)
        {
            return ResolutionFailure(requestId, exception.Code, exception.Message);
        }
    }

    public async Task<GitStatusResult> GetStatusAsync(
        string requestId,
        string repositoryId,
        CancellationToken cancellationToken = default)
    {
        if (!ValidRequestId(requestId)) return Failure("invalid_request_id", "The request identifier is invalid.");
        if (!_repositories.TryGetValue(repositoryId, out var repository))
            return Failure("repository_not_registered", "The repository session is no longer available.");

        try
        {
            _resolver.Revalidate(repository);
        }
        catch (RepositoryResolutionException exception)
        {
            return Failure(exception.Code, exception.Message);
        }

        var git = _gitLocator.Locate();
        if (git is null) return Failure("git_unavailable", "Git is not installed or configured.");
        var raw = await new GitCommandRunner(git, _gitProcessHost, _bounds)
            .RunStatusAsync(repository, cancellationToken)
            .ConfigureAwait(false);
        if (!raw.Succeeded) return new GitStatusResult(false, null, raw.Error);

        try
        {
            var parsed = GitStatusParser.Parse(raw.Output!, _bounds.MaximumStatusEntries);
            var snapshot = new GitStatusSnapshot(
                SourceControlProtocol.Version,
                requestId,
                repository.RepositoryId,
                repository.DisplayName,
                parsed.Branch,
                parsed.Groups,
                parsed.EntryCount,
                false,
                DateTimeOffset.UtcNow);
            return new GitStatusResult(true, snapshot, null);
        }
        catch (GitStatusFormatException exception)
        {
            return Failure("invalid_status_output", exception.Message);
        }
    }

    public Task<GitExtensionsOpenResult> OpenGitExtensionsAsync(
        string repositoryId,
        string surface,
        CancellationToken cancellationToken = default)
    {
        if (!_repositories.TryGetValue(repositoryId, out var repository))
            return Task.FromResult(new GitExtensionsOpenResult(
                false,
                new SourceControlError("repository_not_registered", "The repository session is no longer available.")));
        var executable = _gitExtensionsLocator.Locate();
        if (executable is null)
            return Task.FromResult(new GitExtensionsOpenResult(
                false,
                new SourceControlError("gitextensions_unavailable", "Git Extensions is optional and is not installed or configured.")));
        return new GitExtensionsLauncher(_resolver, executable, _gitExtensionsProcessLauncher)
            .OpenAsync(repository, surface, cancellationToken);
    }

    private static bool ValidRequestId(string requestId) =>
        !string.IsNullOrWhiteSpace(requestId)
        && requestId.Length <= SourceControlProtocol.MaximumRequestIdCharacters
        && requestId.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or ':' or '-');

    private static RepositoryResolutionResult ResolutionFailure(string requestId, string code, string message) =>
        new(SourceControlProtocol.Version, SafeRequestId(requestId), false, null, null, new SourceControlError(code, message));

    private static string SafeRequestId(string requestId) =>
        requestId is { Length: > 0 } && ValidRequestId(requestId)
            ? requestId
            : string.Empty;

    private static GitStatusResult Failure(string code, string message, bool retryable = false) =>
        new(false, null, new SourceControlError(code, message, retryable));
}
