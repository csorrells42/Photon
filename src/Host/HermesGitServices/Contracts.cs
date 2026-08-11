using System.Collections.ObjectModel;

namespace HermesGitServices;

public static class SourceControlProtocol
{
    public const int Version = 1;
    public const int MaximumRendererFrameCharacters = 2 * 1024 * 1024;
    public const int MaximumRequestIdCharacters = 128;
}

public enum ServiceAvailability
{
    Available,
    Unavailable,
    Error,
}

public sealed record SourceControlAvailability(
    ServiceAvailability State,
    string? Code,
    string? Message,
    string? Version = null);

public sealed record SourceControlDescription(
    int ProtocolVersion,
    SourceControlAvailability Git,
    SourceControlAvailability GitExtensions,
    IReadOnlyList<string> Operations,
    IReadOnlyList<string> GitExtensionsSurfaces);

public sealed record SourceControlError(string Code, string Message, bool Retryable = false);

public sealed record RepositoryResolutionResult(
    int ProtocolVersion,
    string RequestId,
    bool Succeeded,
    string? RepositoryId,
    string? DisplayName,
    SourceControlError? Error);

public enum GitChangeKind
{
    Modified,
    Added,
    Deleted,
    Renamed,
    Copied,
    TypeChanged,
    Untracked,
    Conflicted,
    Submodule,
}

public sealed record GitChangeEntry(
    string Path,
    string? OriginalPath,
    GitChangeKind Kind,
    bool Staged,
    bool Unstaged,
    bool Untracked,
    bool Conflicted,
    bool Deleted,
    bool Renamed,
    bool Submodule);

public sealed record GitStatusGroups(
    IReadOnlyList<GitChangeEntry> Staged,
    IReadOnlyList<GitChangeEntry> Unstaged,
    IReadOnlyList<GitChangeEntry> Untracked,
    IReadOnlyList<GitChangeEntry> Conflicted,
    IReadOnlyList<GitChangeEntry> Renamed,
    IReadOnlyList<GitChangeEntry> Deleted,
    IReadOnlyList<GitChangeEntry> Submodules);

public sealed record GitBranchStatus(
    string? Head,
    string? Upstream,
    int Ahead,
    int Behind,
    int StashCount,
    bool Detached,
    bool Unborn);

public sealed record GitStatusSnapshot(
    int ProtocolVersion,
    string RequestId,
    string RepositoryId,
    string DisplayName,
    GitBranchStatus Branch,
    GitStatusGroups Groups,
    int EntryCount,
    bool Truncated,
    DateTimeOffset ObservedAtUtc);

public sealed record GitStatusResult(
    bool Succeeded,
    GitStatusSnapshot? Snapshot,
    SourceControlError? Error);

public sealed record GitExtensionsOpenResult(
    bool Succeeded,
    SourceControlError? Error);

internal static class ContractCollections
{
    internal static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values) =>
        new ReadOnlyCollection<T>(values.ToArray());
}
