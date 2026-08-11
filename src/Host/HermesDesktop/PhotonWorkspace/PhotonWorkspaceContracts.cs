using System.Security.Cryptography;
using System.Text;

namespace HermesDesktop.PhotonWorkspace;

public static class PhotonWorkspaceContract
{
    public const int Version = 1;
    public const int MaxRelativePathCharacters = 1_024;
    public const int MaxComponentCharacters = 255;
    public const int MaxFileBytes = 8 * 1024 * 1024;
    public const int MaxListResults = 1_000;
    public const int MaxEnumeratedEntries = 25_000;
    public const int MaxLiveSnapshots = 4_096;
    public const int MaxRecentRequests = 4_096;
    public const int MaxTrashReceipts = 256;
    public const int MaxPendingOperations = 64;
    public const int MaxPendingMutations = 16;
    public static readonly TimeSpan SnapshotLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan RequestLifetime = TimeSpan.FromMinutes(15);
}

public sealed class PhotonWorkspaceException : Exception
{
    public PhotonWorkspaceException(string code)
        : base(PhotonWorkspaceGuards.FixedCode(code))
    {
        Code = Message;
    }

    public string Code { get; }
}

public sealed record PhotonWorkspaceRequestContext(
    int Version,
    string RequestId,
    string SessionId,
    string PrincipalId,
    string GenerationId);

public enum PhotonWorkspaceEntryKind
{
    File,
    Directory,
}

public enum PhotonWorkspaceCommitStatus
{
    NotCommitted,
    Committed,
    CommittedReadbackRequired,
}

public sealed record PhotonWorkspaceListRequest(
    PhotonWorkspaceRequestContext Context,
    string RelativeDirectory,
    bool Recursive,
    string? After,
    int Limit = 200);

public sealed record PhotonWorkspaceEntry(
    string RelativePath,
    PhotonWorkspaceEntryKind Kind,
    long? ByteLength,
    DateTimeOffset LastWriteUtc);

public sealed record PhotonWorkspaceListResult(
    IReadOnlyList<PhotonWorkspaceEntry> Entries,
    string? NextAfter,
    int OmittedProtectedEntries,
    bool ScanLimitReached);

public sealed record PhotonWorkspaceReadRequest(
    PhotonWorkspaceRequestContext Context,
    string RelativePath);

public sealed record PhotonWorkspaceSnapshot(
    string RelativePath,
    string FileHandle,
    string Revision,
    string Sha256,
    long ByteLength,
    DateTimeOffset LastWriteUtc,
    byte[] Content);

public sealed record PhotonWorkspacePrecondition(
    string FileHandle,
    string Revision,
    string Sha256);

public sealed record PhotonWorkspaceCreateRequest(
    PhotonWorkspaceRequestContext Context,
    string RelativePath,
    byte[] Content);

public sealed record PhotonWorkspaceUpdateRequest(
    PhotonWorkspaceRequestContext Context,
    string RelativePath,
    PhotonWorkspacePrecondition Precondition,
    byte[] Content);

public sealed record PhotonWorkspaceMutationResult(
    PhotonWorkspaceCommitStatus Status,
    PhotonWorkspaceSnapshot? Snapshot);

public sealed record PhotonWorkspaceMoveToTrashRequest(
    PhotonWorkspaceRequestContext Context,
    string RelativePath,
    PhotonWorkspacePrecondition Precondition);

public sealed record PhotonWorkspaceTrashReceipt(
    string Receipt,
    string Revision,
    string OriginalRelativePath,
    string Sha256,
    long ByteLength,
    string RecoveryScope);

public sealed record PhotonWorkspaceMoveToTrashResult(
    PhotonWorkspaceCommitStatus Status,
    PhotonWorkspaceTrashReceipt? TrashReceipt);

public sealed record PhotonWorkspaceRestoreRequest(
    PhotonWorkspaceRequestContext Context,
    string Receipt,
    string Revision);

public sealed record PhotonWorkspaceRestoreResult(
    PhotonWorkspaceCommitStatus Status,
    PhotonWorkspaceSnapshot? Snapshot);

public sealed record PhotonWorkspaceDeleteRequest(
    PhotonWorkspaceRequestContext Context,
    string RelativePath,
    PhotonWorkspacePrecondition Precondition);

public sealed record PhotonWorkspaceDeleteResult(
    bool Available,
    PhotonWorkspaceCommitStatus Status,
    string ReasonCode);

public sealed record PhotonWorkspaceCapabilities(
    int Version,
    bool List,
    bool Read,
    bool Create,
    bool Update,
    bool MoveToTrash,
    bool RestoreFromTrash,
    bool DestructiveDelete);

internal static class PhotonWorkspaceGuards
{
    private const int MaxIdentifierCharacters = 256;

    internal static string FixedCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) ||
            code.Length > 80 ||
            code.Any(character => !(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')))
        {
            return "workspace_error";
        }

        return code;
    }

    internal static string Identifier(string? value, string code)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxIdentifierCharacters)
        {
            throw new PhotonWorkspaceException(code);
        }

        foreach (var character in value)
        {
            if (char.IsControl(character) || char.IsSurrogate(character))
            {
                throw new PhotonWorkspaceException(code);
            }
        }

        return value;
    }

    internal static byte[] Content(byte[]? content)
    {
        if (content is null || content.LongLength > PhotonWorkspaceContract.MaxFileBytes)
        {
            throw new PhotonWorkspaceException("content_invalid_or_too_large");
        }

        return content.ToArray();
    }

    internal static string OpaqueToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    }

    internal static string RequireOpaqueToken(string? value, string code)
    {
        if (value is null || value.Length != 64 || value.Any(character => !(character is >= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new PhotonWorkspaceException(code);
        }

        return value;
    }

    internal static string RequireSha256(string? value, string code)
    {
        return RequireOpaqueToken(value, code);
    }

    internal static string Sha256(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    internal static bool FixedEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
            CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    internal static DateTimeOffset Utc(DateTime value)
    {
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }
}
