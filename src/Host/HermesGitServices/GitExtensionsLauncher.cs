using System.Diagnostics;

namespace HermesGitServices;

internal interface IGitExtensionsProcessLauncher
{
    bool TryStart(ProcessStartInfo startInfo);
}

internal sealed class GitExtensionsProcessLauncher : IGitExtensionsProcessLauncher
{
    public bool TryStart(ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo) is not null;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>Launches only the fixed Git Extensions browse surface for a revalidated repository.</summary>
public sealed class GitExtensionsLauncher
{
    public const string BrowseSurface = "browse";

    private readonly RepositoryResolver _resolver;
    private readonly LocatedGitExtensions _executable;
    private readonly IGitExtensionsProcessLauncher _processLauncher;

    internal GitExtensionsLauncher(
        RepositoryResolver resolver,
        LocatedGitExtensions executable,
        IGitExtensionsProcessLauncher? processLauncher = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _executable = executable ?? throw new ArgumentNullException(nameof(executable));
        _processLauncher = processLauncher ?? new GitExtensionsProcessLauncher();
    }

    public Task<GitExtensionsOpenResult> OpenAsync(
        ValidatedRepository repository,
        string surface,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(surface, BrowseSurface, StringComparison.Ordinal))
            return Task.FromResult(Failure("unsupported_surface", "The requested Git Extensions surface is not available."));
        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult(Failure("cancelled", "The launch request was cancelled.", retryable: true));

        try
        {
            _resolver.Revalidate(repository);
        }
        catch (RepositoryResolutionException exception)
        {
            return Task.FromResult(Failure(exception.Code, exception.Message));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _executable.FullPath,
            WorkingDirectory = repository.RootPath,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(BrowseSurface);
        startInfo.ArgumentList.Add(repository.RootPath);

        return Task.FromResult(_processLauncher.TryStart(startInfo)
            ? new GitExtensionsOpenResult(true, null)
            : Failure("gitextensions_start_failed", "Git Extensions could not be started.", retryable: true));
    }

    private static GitExtensionsOpenResult Failure(string code, string message, bool retryable = false) =>
        new(false, new SourceControlError(code, message, retryable));
}
