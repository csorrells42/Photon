namespace HermesGitServices;

public sealed class GitStatusFormatException : Exception
{
    internal GitStatusFormatException(string message) : base(message)
    {
    }
}

public static class GitStatusParser
{
    private const int MaximumPathCharacters = 32 * 1024;

    public static (GitBranchStatus Branch, GitStatusGroups Groups, int EntryCount) Parse(
        string value,
        int maximumEntries = 10_000)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (maximumEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        if (value.Length != 0 && value[^1] != '\0') throw Invalid("Status output is not NUL-terminated.");

        string? head = null;
        string? upstream = null;
        var ahead = 0;
        var behind = 0;
        var stash = 0;
        var detached = false;
        var unborn = false;
        var entries = new List<GitChangeEntry>();
        var records = value.Split('\0');
        for (var index = 0; index < records.Length - 1; index++)
        {
            var record = records[index];
            if (record.Length == 0) continue;
            if (record.StartsWith("# ", StringComparison.Ordinal))
            {
                ParseHeader(record, ref head, ref upstream, ref ahead, ref behind, ref stash, ref detached, ref unborn);
                continue;
            }

            GitChangeEntry? entry;
            switch (record[0])
            {
                case '1':
                    entry = ParseOrdinary(record);
                    break;
                case '2':
                    if (++index >= records.Length - 1) throw Invalid("Rename record is missing its original path.");
                    entry = ParseRename(record, records[index]);
                    break;
                case 'u':
                    entry = ParseUnmerged(record);
                    break;
                case '?':
                    entry = CreateUntracked(ReadPath(record, 2));
                    break;
                case '!':
                    continue;
                default:
                    throw Invalid("Status output contains an unsupported record type.");
            }

            entries.Add(entry);
            if (entries.Count > maximumEntries) throw Invalid("Status output exceeds the entry limit.");
        }

        var groups = new GitStatusGroups(
            Freeze(entries.Where(entry => entry.Staged)),
            Freeze(entries.Where(entry => entry.Unstaged)),
            Freeze(entries.Where(entry => entry.Untracked)),
            Freeze(entries.Where(entry => entry.Conflicted)),
            Freeze(entries.Where(entry => entry.Renamed)),
            Freeze(entries.Where(entry => entry.Deleted)),
            Freeze(entries.Where(entry => entry.Submodule)));
        return (new GitBranchStatus(head, upstream, ahead, behind, stash, detached, unborn), groups, entries.Count);
    }

    private static void ParseHeader(
        string record,
        ref string? head,
        ref string? upstream,
        ref int ahead,
        ref int behind,
        ref int stash,
        ref bool detached,
        ref bool unborn)
    {
        if (record.StartsWith("# branch.oid ", StringComparison.Ordinal))
        {
            unborn = record[13..] == "(initial)";
            return;
        }

        if (record.StartsWith("# branch.head ", StringComparison.Ordinal))
        {
            var value = record[14..];
            detached = value == "(detached)";
            head = detached ? null : BoundedHeader(value);
            return;
        }

        if (record.StartsWith("# branch.upstream ", StringComparison.Ordinal))
        {
            upstream = BoundedHeader(record[18..]);
            return;
        }

        if (record.StartsWith("# branch.ab ", StringComparison.Ordinal))
        {
            var parts = record[12..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2
                || !int.TryParse(parts[0].TrimStart('+'), out ahead)
                || !int.TryParse(parts[1].TrimStart('-'), out behind)
                || ahead < 0 || behind < 0)
            {
                throw Invalid("Branch divergence is malformed.");
            }
            return;
        }

        if (record.StartsWith("# stash ", StringComparison.Ordinal)
            && (!int.TryParse(record[8..], out stash) || stash < 0))
        {
            throw Invalid("Stash count is malformed.");
        }
    }

    private static GitChangeEntry ParseOrdinary(string record)
    {
        var (fields, path) = ReadPrefix(record, 8);
        return CreateTracked(fields[1], fields[2], path, originalPath: null, unmerged: false);
    }

    private static GitChangeEntry ParseRename(string record, string originalPath)
    {
        var (fields, path) = ReadPrefix(record, 9);
        ValidatePath(originalPath);
        return CreateTracked(fields[1], fields[2], path, originalPath, unmerged: false);
    }

    private static GitChangeEntry ParseUnmerged(string record)
    {
        var (fields, path) = ReadPrefix(record, 10);
        return CreateTracked(fields[1], fields[2], path, originalPath: null, unmerged: true);
    }

    private static GitChangeEntry CreateTracked(
        string xy,
        string submoduleState,
        string path,
        string? originalPath,
        bool unmerged)
    {
        if (xy.Length != 2) throw Invalid("A tracked record has an invalid XY state.");
        ValidatePath(path);
        var index = xy[0];
        var worktree = xy[1];
        var submodule = !submoduleState.StartsWith('N');
        var conflicted = unmerged || index == 'U' || worktree == 'U';
        var renamed = index == 'R' || worktree == 'R';
        var copied = index == 'C' || worktree == 'C';
        var deleted = index == 'D' || worktree == 'D';
        var kind = conflicted ? GitChangeKind.Conflicted
            : submodule ? GitChangeKind.Submodule
            : renamed ? GitChangeKind.Renamed
            : copied ? GitChangeKind.Copied
            : deleted ? GitChangeKind.Deleted
            : index == 'A' || worktree == 'A' ? GitChangeKind.Added
            : index == 'T' || worktree == 'T' ? GitChangeKind.TypeChanged
            : GitChangeKind.Modified;
        return new GitChangeEntry(
            path,
            originalPath,
            kind,
            Staged: unmerged || index != '.',
            Unstaged: unmerged || worktree != '.',
            Untracked: false,
            Conflicted: conflicted,
            Deleted: deleted,
            Renamed: renamed || copied,
            Submodule: submodule);
    }

    private static GitChangeEntry CreateUntracked(string path)
    {
        ValidatePath(path);
        return new GitChangeEntry(
            path, null, GitChangeKind.Untracked,
            Staged: false, Unstaged: false, Untracked: true, Conflicted: false,
            Deleted: false, Renamed: false, Submodule: false);
    }

    private static (string[] Fields, string Path) ReadPrefix(string record, int fieldCount)
    {
        var fields = new string[fieldCount];
        var cursor = 0;
        for (var field = 0; field < fieldCount; field++)
        {
            var separator = record.IndexOf(' ', cursor);
            if (separator < 0) throw Invalid("A status record is missing fields.");
            fields[field] = record[cursor..separator];
            cursor = separator + 1;
        }

        var path = record[cursor..];
        ValidatePath(path);
        return (fields, path);
    }

    private static string ReadPath(string record, int offset)
    {
        if (record.Length < offset) throw Invalid("A status path is missing.");
        var path = record[offset..];
        ValidatePath(path);
        return path;
    }

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaximumPathCharacters || path.Contains('\0'))
            throw Invalid("A status path is invalid.");
        if (Path.IsPathFullyQualified(path)
            || path == ".."
            || path.StartsWith("../", StringComparison.Ordinal)
            || path.StartsWith("..\\", StringComparison.Ordinal))
        {
            throw Invalid("A status path escapes the repository.");
        }
    }

    private static string BoundedHeader(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Contains('\0'))
            throw Invalid("A status header is invalid.");
        return value;
    }

    private static IReadOnlyList<GitChangeEntry> Freeze(IEnumerable<GitChangeEntry> values) =>
        ContractCollections.Freeze(values);

    private static GitStatusFormatException Invalid(string message) => new(message);
}
