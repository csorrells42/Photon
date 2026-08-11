using System.Collections.Concurrent;
using HermesGitServices;

namespace HermesDesktop;

internal sealed class SourceControlBridge : IDisposable
{
    internal const int ProtocolVersion = SourceControlProtocol.Version;
    private const int MaximumWorkspaceRelativePathCharacters = 1_024;

    private readonly HermesGitService _service;
    private readonly Action<object> _post;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new(StringComparer.Ordinal);

    internal SourceControlBridge(string workspaceAuthorityRoot, Action<object> post)
    {
        _service = new HermesGitService(workspaceAuthorityRoot);
        _post = post;
    }

    internal void Describe(int version, string? requestId)
    {
        if (!ValidateEnvelope(version, requestId)) return;
        var description = _service.Describe();
        _post(new
        {
            type = "sourceControl.describe.result",
            version = ProtocolVersion,
            requestId,
            value = new
            {
                protocolVersion = ProtocolVersion,
                git = Availability(description.Git),
                gitExtensions = Availability(description.GitExtensions),
                operations = description.Operations,
                gitExtensionsSurfaces = description.GitExtensionsSurfaces,
            },
        });
    }

    internal void ResolveRepository(int version, string? requestId, string? workspaceRelativePath)
    {
        if (!ValidateEnvelope(version, requestId)) return;
        if (string.IsNullOrWhiteSpace(workspaceRelativePath)
            || workspaceRelativePath.Length > MaximumWorkspaceRelativePathCharacters
            || workspaceRelativePath.Contains('\0'))
        {
            PostError(requestId!, "invalid_workspace_path", "The repository path is invalid.");
            return;
        }

        var result = _service.ResolveRepository(requestId!, workspaceRelativePath);
        _post(new
        {
            type = "sourceControl.repository.resolve.result",
            version = ProtocolVersion,
            requestId,
            value = new
            {
                protocolVersion = ProtocolVersion,
                requestId,
                succeeded = result.Succeeded,
                repositoryId = result.RepositoryId,
                displayName = result.DisplayName,
                error = Error(result.Error),
            },
        });
    }

    internal Task StatusAsync(int version, string? requestId, string? repositoryId) =>
        RunAsync(version, requestId, repositoryId, async cancellationToken =>
        {
            var result = await _service.GetStatusAsync(requestId!, repositoryId!, cancellationToken).ConfigureAwait(false);
            _post(new
            {
                type = "sourceControl.status.result",
                version = ProtocolVersion,
                requestId,
                value = new
                {
                    succeeded = result.Succeeded,
                    snapshot = result.Snapshot is null ? null : Snapshot(result.Snapshot),
                    error = Error(result.Error),
                },
            });
        });

    internal Task OpenGitExtensionsAsync(int version, string? requestId, string? repositoryId, string? surface) =>
        RunAsync(version, requestId, repositoryId, async cancellationToken =>
        {
            if (!string.Equals(surface, GitExtensionsLauncher.BrowseSurface, StringComparison.Ordinal))
            {
                PostError(requestId!, "unsupported_surface", "The requested Git Extensions surface is not supported.");
                return;
            }
            var result = await _service.OpenGitExtensionsAsync(repositoryId!, GitExtensionsLauncher.BrowseSurface, cancellationToken).ConfigureAwait(false);
            _post(new
            {
                type = "sourceControl.gitExtensions.result",
                version = ProtocolVersion,
                requestId,
                value = new { succeeded = result.Succeeded, error = Error(result.Error) },
            });
        });

    internal void Cancel(int version, string? requestId, string? targetRequestId)
    {
        if (!ValidateEnvelope(version, requestId) || !ValidRequestId(targetRequestId)) return;
        if (_pending.TryGetValue(targetRequestId!, out var cancellation)) cancellation.Cancel();
    }

    private async Task RunAsync(
        int version,
        string? requestId,
        string? repositoryId,
        Func<CancellationToken, Task> operation)
    {
        if (!ValidateEnvelope(version, requestId)) return;
        if (string.IsNullOrWhiteSpace(repositoryId) || repositoryId.Length > 128)
        {
            PostError(requestId!, "invalid_repository_id", "The repository reference is invalid.");
            return;
        }

        using var cancellation = new CancellationTokenSource();
        if (!_pending.TryAdd(requestId!, cancellation))
        {
            PostError(requestId!, "duplicate_request", "A source-control request with this identifier is already running.");
            return;
        }

        try
        {
            await operation(cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            PostError(requestId!, "cancelled", "The source-control request was cancelled.");
        }
        catch (Exception exception)
        {
            DesktopLog.Write($"Source control request failed: {exception.GetType().Name}");
            PostError(requestId!, "unexpected", "The native source-control service encountered an unexpected error.", true);
        }
        finally
        {
            _pending.TryRemove(requestId!, out _);
        }
    }

    private bool ValidateEnvelope(int version, string? requestId)
    {
        if (version == ProtocolVersion && ValidRequestId(requestId)) return true;
        if (ValidRequestId(requestId)) PostError(requestId!, "invalid_envelope", "The source-control request envelope is invalid.");
        return false;
    }

    private static bool ValidRequestId(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= SourceControlProtocol.MaximumRequestIdCharacters
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or ':' or '-');

    private void PostError(string requestId, string code, string message, bool retryable = false) => _post(new
    {
        type = "sourceControl.error",
        version = ProtocolVersion,
        requestId,
        code,
        message,
        retryable,
    });

    private static object Availability(SourceControlAvailability value) => new
    {
        state = value.State switch
        {
            ServiceAvailability.Available => "available",
            ServiceAvailability.Error => "error",
            _ => "unavailable",
        },
        code = value.Code,
        message = value.Message,
        version = value.Version,
    };

    private static object? Error(SourceControlError? value) => value is null ? null : new
    {
        code = value.Code,
        message = value.Message,
        retryable = value.Retryable,
    };

    private static object Snapshot(GitStatusSnapshot value) => new
    {
        protocolVersion = ProtocolVersion,
        requestId = value.RequestId,
        repositoryId = value.RepositoryId,
        displayName = value.DisplayName,
        branch = new
        {
            head = value.Branch.Head,
            upstream = value.Branch.Upstream,
            ahead = value.Branch.Ahead,
            behind = value.Branch.Behind,
            stashCount = value.Branch.StashCount,
            detached = value.Branch.Detached,
            unborn = value.Branch.Unborn,
        },
        groups = new
        {
            staged = Entries(value.Groups.Staged),
            unstaged = Entries(value.Groups.Unstaged),
            untracked = Entries(value.Groups.Untracked),
            conflicted = Entries(value.Groups.Conflicted),
            renamed = Entries(value.Groups.Renamed),
            deleted = Entries(value.Groups.Deleted),
            submodules = Entries(value.Groups.Submodules),
        },
        entryCount = value.EntryCount,
        truncated = value.Truncated,
        observedAtUtc = value.ObservedAtUtc,
    };

    private static object[] Entries(IReadOnlyList<GitChangeEntry> values) => values.Select(value => (object)new
    {
        path = value.Path,
        originalPath = value.OriginalPath,
        kind = value.Kind switch
        {
            GitChangeKind.TypeChanged => "typeChanged",
            _ => char.ToLowerInvariant(value.Kind.ToString()[0]) + value.Kind.ToString()[1..],
        },
        staged = value.Staged,
        unstaged = value.Unstaged,
        untracked = value.Untracked,
        conflicted = value.Conflicted,
        deleted = value.Deleted,
        renamed = value.Renamed,
        submodule = value.Submodule,
    }).ToArray();

    public void Dispose()
    {
        foreach (var cancellation in _pending.Values) cancellation.Cancel();
        foreach (var cancellation in _pending.Values) cancellation.Dispose();
        _pending.Clear();
    }
}
