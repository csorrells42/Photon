using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PhotonCadProjects.Windows;

internal sealed record PhotonCadWindowsPathSnapshot(
    string ExactPath,
    string ParentPath,
    string CanonicalPathFingerprint,
    string VerifiedChainFingerprint,
    string VolumeIdentity,
    string ParentFileIdentity,
    string? EntryFileIdentity,
    bool Exists,
    int HardLinkCount,
    DateTimeOffset InspectedAtUtc);

internal static class PhotonCadWindowsNative
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileFlagSequentialScan = 0x08000000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint InvalidFileAttributes = 0xffffffff;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint FileCaseSensitiveDir = 0x00000001;
    private const uint MoveFileReplaceExisting = 0x00000001;
    private const uint MoveFileWriteThrough = 0x00000008;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAlreadyExists = 183;
    private const int MaximumPathCharacters = 32_000;

    internal static bool IsSupported => OperatingSystem.IsWindows();

    internal static string CanonicalizeLocalFilePath(string candidate)
    {
        EnsureWindows();
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > MaximumPathCharacters)
            throw Failure("absolute_local_path_required", nameof(candidate));
        string normalized;
        try { normalized = candidate.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException exception) { throw Failure("invalid_unicode", nameof(candidate), exception); }
        if (normalized.StartsWith("\\\\", StringComparison.Ordinal)
            || normalized.StartsWith("//", StringComparison.Ordinal)
            || normalized.StartsWith("\\?\\", StringComparison.Ordinal)
            || normalized.StartsWith("\\.\\", StringComparison.Ordinal)
            || normalized.StartsWith("\\??\\", StringComparison.Ordinal)
            || !Path.IsPathFullyQualified(normalized))
            throw Failure("absolute_local_path_required", nameof(candidate));
        var suppliedRoot = Path.GetPathRoot(normalized);
        if (suppliedRoot is null || normalized[suppliedRoot.Length..]
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment is "." or ".."))
            throw Failure("path_traversal_rejected", nameof(candidate));
        string full;
        try { full = Path.GetFullPath(normalized); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw Failure("absolute_local_path_required", nameof(candidate), exception);
        }
        var root = Path.GetPathRoot(full);
        if (root is null || root.Length != 3 || !char.IsAsciiLetter(root[0]) || root[1] != ':' || root[2] != Path.DirectorySeparatorChar)
            throw Failure("local_ntfs_path_required", nameof(candidate));
        if (full.AsSpan(root.Length).IndexOf(':') >= 0)
            throw Failure("alternate_data_stream_rejected", nameof(candidate));
        var segments = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(IsAmbiguousSegment))
            throw Failure("ambiguous_path_rejected", nameof(candidate));
        var parent = Path.GetDirectoryName(full);
        if (parent is null || !Directory.Exists(parent))
            throw Failure("parent_directory_missing", nameof(candidate));
        return Path.TrimEndingDirectorySeparator(full);
    }

    internal static PhotonCadWindowsPathSnapshot InspectExactPath(string exactPath)
    {
        exactPath = CanonicalizeLocalFilePath(exactPath);
        var parent = Path.GetDirectoryName(exactPath)!;
        var chain = InspectDirectoryChain(parent);
        var attributes = GetFileAttributesW(exactPath);
        var exists = attributes != InvalidFileAttributes;
        if (!exists)
        {
            var error = Marshal.GetLastWin32Error();
            if (error is not ErrorFileNotFound and not ErrorPathNotFound)
                throw NativeFailure("target_attribute_inspection_failed", nameof(exactPath), error);
            var canonicalMissing = Path.Combine(chain.CanonicalParentPath, Path.GetFileName(exactPath));
            return new PhotonCadWindowsPathSnapshot(
                exactPath,
                parent,
                DigestPath(canonicalMissing),
                DigestChain(chain.Identities, $"missing:{Path.GetFileName(exactPath).ToUpperInvariant()}"),
                chain.VolumeIdentity,
                chain.ParentFileIdentity,
                null,
                false,
                0,
                DateTimeOffset.UtcNow);
        }
        if ((attributes & FileAttributeDirectory) != 0)
            throw Failure("file_target_required", nameof(exactPath));
        if ((attributes & FileAttributeReparsePoint) != 0)
            throw Failure("reparse_point_rejected", nameof(exactPath));
        // Deny writers and namespace replacement while capturing identity plus content. The
        // content digest is folded into the verified fingerprint so in-place edits are version
        // conflicts even when NTFS keeps the same file ID.
        using var handle = OpenExistingFile(exactPath, GenericRead, FileShare.Read);
        var finalPath = FinalPath(handle);
        if (!PathsEqual(finalPath, exactPath))
            throw Failure("exact_path_mismatch", nameof(exactPath));
        var identity = FileIdentity(handle);
        var standard = StandardInfo(handle);
        if (standard.Directory != 0) throw Failure("file_target_required", nameof(exactPath));
        if (standard.NumberOfLinks != 1) throw Failure("hard_link_rejected", nameof(exactPath));
        if (standard.EndOfFile is <= 0 or > PhotonCadProjectContract.MaximumCanonicalProjectBytes)
            throw Failure("invalid_content_length", nameof(exactPath));
        var contentDigest = HashHandle(handle, standard.EndOfFile);
        return new PhotonCadWindowsPathSnapshot(
            exactPath,
            parent,
            DigestPath(finalPath),
            DigestChain(chain.Identities, $"{identity.FileIdentity}\n{standard.EndOfFile}\n{contentDigest}"),
            identity.VolumeIdentity,
            chain.ParentFileIdentity,
            identity.FileIdentity,
            true,
            checked((int)standard.NumberOfLinks),
            DateTimeOffset.UtcNow);
    }

    internal static SafeFileHandle OpenReadLocked(string exactPath)
    {
        exactPath = CanonicalizeLocalFilePath(exactPath);
        var handle = CreateFileW(
            exactPath,
            GenericRead,
            FileShare.Read,
            IntPtr.Zero,
            FileMode.Open,
            FileFlagOpenReparsePoint | FileFlagSequentialScan,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw NativeFailure("target_open_failed", nameof(exactPath), error);
        }
        return handle;
    }

    internal static long FileLength(SafeFileHandle handle)
    {
        var info = StandardInfo(handle);
        if (info.Directory != 0 || info.EndOfFile <= 0 || info.EndOfFile > PhotonCadProjectContract.MaximumCanonicalProjectBytes)
            throw Failure("invalid_content_length", nameof(handle));
        return info.EndOfFile;
    }

    internal static PhotonCadWindowsPathSnapshot InspectLockedFile(string exactPath, SafeFileHandle handle)
    {
        var path = InspectExactPath(exactPath);
        var identity = FileIdentity(handle);
        var standard = StandardInfo(handle);
        if (!path.Exists || path.EntryFileIdentity != identity.FileIdentity || path.VolumeIdentity != identity.VolumeIdentity)
            throw Failure("file_identity_changed", nameof(exactPath));
        if (standard.NumberOfLinks != 1) throw Failure("hard_link_rejected", nameof(exactPath));
        return path with { InspectedAtUtc = DateTimeOffset.UtcNow };
    }

    internal static void WriteDurableNewFile(string exactPath, ReadOnlySpan<byte> bytes)
    {
        exactPath = CanonicalizeLocalFilePath(exactPath);
        using var stream = new FileStream(
            exactPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.WriteThrough | FileOptions.SequentialScan);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    internal static void ReplaceDurableFile(string temporaryPath, string destinationPath)
    {
        temporaryPath = CanonicalizeLocalFilePath(temporaryPath);
        destinationPath = CanonicalizeLocalFilePath(destinationPath);
        if (!MoveFileExW(temporaryPath, destinationPath, MoveFileReplaceExisting | MoveFileWriteThrough))
            throw NativeFailure("durable_replace_failed", nameof(destinationPath), Marshal.GetLastWin32Error());
    }

    internal static void CommitDurableFile(string stagePath, string targetPath, bool replaceExisting)
    {
        stagePath = CanonicalizeLocalFilePath(stagePath);
        targetPath = CanonicalizeLocalFilePath(targetPath);
        var flags = MoveFileWriteThrough | (replaceExisting ? MoveFileReplaceExisting : 0u);
        if (!MoveFileExW(stagePath, targetPath, flags))
        {
            var error = Marshal.GetLastWin32Error();
            throw NativeFailure(error == ErrorAlreadyExists ? "target_exists" : "atomic_move_failed", nameof(targetPath), error);
        }
    }

    internal static void MoveDurableFile(string sourcePath, string destinationPath)
    {
        sourcePath = CanonicalizeLocalFilePath(sourcePath);
        destinationPath = CanonicalizeLocalFilePath(destinationPath);
        if (!MoveFileExW(sourcePath, destinationPath, MoveFileWriteThrough))
            throw NativeFailure("durable_move_failed", nameof(destinationPath), Marshal.GetLastWin32Error());
    }

    internal static bool DeleteIfPresent(string exactPath)
    {
        exactPath = CanonicalizeLocalFilePath(exactPath);
        if (!File.Exists(exactPath)) return false;
        if (!DeleteFileW(exactPath))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorFileNotFound) return false;
            throw NativeFailure("durable_delete_failed", nameof(exactPath), error);
        }
        return true;
    }

    internal static void RequireDirectoryDurability(string directoryPath)
    {
        directoryPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
        using var handle = OpenDirectory(directoryPath, GenericWrite);
        if (!FlushFileBuffers(handle))
            throw NativeFailure("directory_flush_unavailable", nameof(directoryPath), Marshal.GetLastWin32Error());
    }

    internal static string Sha256(ReadOnlySpan<byte> bytes) => $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";

    internal static bool FixedDigestEquals(string left, string right)
    {
        var leftBytes = Encoding.ASCII.GetBytes(left);
        var rightBytes = Encoding.ASCII.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    internal static string NewOpaqueToken(int byteCount = 32) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(byteCount))
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    internal static string NewTransactionId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    private static DirectoryChain InspectDirectoryChain(string parentPath)
    {
        var root = Path.GetPathRoot(parentPath) ?? throw Failure("local_ntfs_path_required", nameof(parentPath));
        var identities = new List<string>();
        string? volumeIdentity = null;
        string? parentIdentity = null;
        var cursor = root;
        foreach (var segment in parentPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Prepend(string.Empty))
        {
            if (segment.Length > 0) cursor = Path.Combine(cursor, segment);
            using var handle = OpenDirectory(cursor);
            var final = FinalPath(handle);
            if (!PathsEqual(final, cursor)) throw Failure("exact_path_mismatch", nameof(parentPath));
            var attributes = GetFileAttributesW(cursor);
            if (attributes == InvalidFileAttributes)
                throw NativeFailure("directory_attribute_inspection_failed", nameof(parentPath), Marshal.GetLastWin32Error());
            if ((attributes & FileAttributeReparsePoint) != 0) throw Failure("reparse_point_rejected", nameof(parentPath));
            if (!TryCaseSensitiveInfo(handle, out var caseSensitive))
                throw Failure("case_sensitivity_unverifiable", nameof(parentPath));
            if (caseSensitive) throw Failure("case_sensitive_directory_rejected", nameof(parentPath));
            var identity = FileIdentity(handle);
            volumeIdentity ??= identity.VolumeIdentity;
            if (volumeIdentity != identity.VolumeIdentity) throw Failure("cross_volume_path_chain", nameof(parentPath));
            identities.Add(identity.FileIdentity);
            parentIdentity = identity.FileIdentity;
            if (identities.Count == 1) RequireNtfs(handle, parentPath);
        }
        return new DirectoryChain(
            FinalDirectoryPath(parentPath),
            volumeIdentity ?? throw Failure("volume_identity_missing", nameof(parentPath)),
            parentIdentity ?? throw Failure("parent_identity_missing", nameof(parentPath)),
            identities);
    }

    private static string FinalDirectoryPath(string directoryPath)
    {
        using var handle = OpenDirectory(directoryPath);
        return FinalPath(handle);
    }

    private static SafeFileHandle OpenDirectory(string path, uint desiredAccess = FileReadAttributes)
    {
        var handle = CreateFileW(
            path,
            desiredAccess,
            FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw NativeFailure("directory_open_failed", nameof(path), error);
        }
        return handle;
    }

    private static SafeFileHandle OpenExistingFile(string path, uint access, FileShare share)
    {
        var handle = CreateFileW(path, access, share, IntPtr.Zero, FileMode.Open, FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw NativeFailure("file_open_failed", nameof(path), error);
        }
        return handle;
    }

    private static NativeFileIdentity FileIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, FileInfoByHandleClass.FileIdInfo, out FileIdInfo info, Marshal.SizeOf<FileIdInfo>()))
            throw NativeFailure("file_identity_unavailable", nameof(handle), Marshal.GetLastWin32Error());
        return new NativeFileIdentity($"vol-{info.VolumeSerialNumber:x16}", $"fid-{info.FileIdHigh:x16}{info.FileIdLow:x16}");
    }

    private static FileStandardInfo StandardInfo(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, FileInfoByHandleClass.FileStandardInfo, out FileStandardInfo info, Marshal.SizeOf<FileStandardInfo>()))
            throw NativeFailure("file_standard_info_unavailable", nameof(handle), Marshal.GetLastWin32Error());
        return info;
    }

    private static string HashHandle(SafeFileHandle handle, long length)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        long offset = 0;
        while (offset < length)
        {
            var requested = checked((int)Math.Min(buffer.Length, length - offset));
            var read = RandomAccess.Read(handle, buffer.AsSpan(0, requested), offset);
            if (read == 0) throw Failure("unexpected_end_of_file", nameof(handle));
            hash.AppendData(buffer, 0, read);
            offset += read;
        }
        if (RandomAccess.Read(handle, buffer.AsSpan(0, 1), offset) != 0)
            throw Failure("file_grew_during_inspection", nameof(handle));
        return $"sha256:{Convert.ToHexStringLower(hash.GetHashAndReset())}";
    }

    private static bool TryCaseSensitiveInfo(SafeFileHandle handle, out bool caseSensitive)
    {
        caseSensitive = false;
        if (!GetFileInformationByHandleEx(handle, FileInfoByHandleClass.FileCaseSensitiveInfo, out FileCaseSensitiveInfo info, Marshal.SizeOf<FileCaseSensitiveInfo>()))
            return false;
        caseSensitive = (info.Flags & FileCaseSensitiveDir) != 0;
        return true;
    }

    private static void RequireNtfs(SafeFileHandle handle, string field)
    {
        var fileSystem = new StringBuilder(32);
        if (!GetVolumeInformationByHandleW(handle, null, 0, out _, out _, out _, fileSystem, fileSystem.Capacity))
            throw NativeFailure("volume_information_unavailable", field, Marshal.GetLastWin32Error());
        if (!string.Equals(fileSystem.ToString(), "NTFS", StringComparison.OrdinalIgnoreCase))
            throw Failure("ntfs_volume_required", field);
    }

    private static string FinalPath(SafeFileHandle handle)
    {
        var capacity = 512;
        while (capacity <= MaximumPathCharacters)
        {
            var buffer = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandleW(handle, buffer, buffer.Capacity, 0);
            if (length == 0) throw NativeFailure("final_path_unavailable", nameof(handle), Marshal.GetLastWin32Error());
            if (length < capacity) return NormalizeFinalPath(buffer.ToString());
            capacity = checked((int)length + 1);
        }
        throw Failure("path_too_long", nameof(handle));
    }

    private static string NormalizeFinalPath(string value)
    {
        if (value.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
            throw Failure("network_path_rejected", nameof(value));
        if (value.StartsWith("\\\\?\\", StringComparison.Ordinal)) value = value[4..];
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }

    private static string DigestPath(string value) => Sha256(Encoding.UTF8.GetBytes(value.Normalize(NormalizationForm.FormC).ToUpperInvariant()));

    private static string DigestChain(IEnumerable<string> identities, string entry) =>
        Sha256(Encoding.ASCII.GetBytes(string.Join("\n", identities.Append(entry))));

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);

    private static bool IsAmbiguousSegment(string segment)
    {
        if (segment is "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.')) return true;
        var stem = segment.Split('.')[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && stem[3] is >= '1' and <= '9');
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows()) throw Failure("windows_storage_unavailable", "platform");
    }

    private static PhotonCadProjectException Failure(string code, string field, Exception? inner = null) =>
        inner is null ? new PhotonCadProjectException(code, field) : new PhotonCadProjectException(code, field, inner);

    private static PhotonCadProjectException NativeFailure(string code, string field, int error) =>
        Failure(code, field, new Win32Exception(error));

    private sealed record DirectoryChain(string CanonicalParentPath, string VolumeIdentity, string ParentFileIdentity, IReadOnlyList<string> Identities);
    private sealed record NativeFileIdentity(string VolumeIdentity, string FileIdentity);

    private enum FileInfoByHandleClass
    {
        FileStandardInfo = 1,
        FileIdInfo = 18,
        FileCaseSensitiveInfo = 23,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        internal ulong VolumeSerialNumber;
        internal ulong FileIdLow;
        internal ulong FileIdHigh;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInfo
    {
        internal long AllocationSize;
        internal long EndOfFile;
        internal uint NumberOfLinks;
        internal byte DeletePending;
        internal byte Directory;
        internal ushort Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileCaseSensitiveInfo
    {
        internal uint Flags;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, FileShare shareMode, IntPtr securityAttributes, FileMode creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, FileInfoByHandleClass fileInformationClass, out FileIdInfo fileInformation, int bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, FileInfoByHandleClass fileInformationClass, out FileStandardInfo fileInformation, int bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, FileInfoByHandleClass fileInformationClass, out FileCaseSensitiveInfo fileInformation, int bufferSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder filePath, int filePathLength, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationByHandleW(SafeFileHandle file, StringBuilder? volumeNameBuffer, int volumeNameSize, out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags, StringBuilder fileSystemNameBuffer, int fileSystemNameSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributesW(string fileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(string existingFileName, string newFileName, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteFileW(string fileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle file);
}
