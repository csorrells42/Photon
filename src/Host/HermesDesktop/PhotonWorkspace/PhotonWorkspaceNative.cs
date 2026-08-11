using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace HermesDesktop.PhotonWorkspace;

internal sealed record PhotonWorkspaceFileEvidence(
    uint VolumeSerialNumber,
    ulong FileIndex,
    uint LinkCount,
    long ByteLength,
    DateTimeOffset LastWriteUtc,
    string Sha256,
    byte[]? Content)
{
    internal bool SameObjectAndContent(PhotonWorkspaceFileEvidence other)
    {
        return VolumeSerialNumber == other.VolumeSerialNumber &&
            FileIndex == other.FileIndex &&
            LinkCount == other.LinkCount &&
            ByteLength == other.ByteLength &&
            PhotonWorkspaceGuards.FixedEquals(Sha256, other.Sha256);
    }
}

internal sealed class PhotonWorkspaceVerifiedFile : IDisposable
{
    internal PhotonWorkspaceVerifiedFile(SafeFileHandle handle, PhotonWorkspaceFileEvidence evidence)
    {
        Handle = handle;
        Evidence = evidence;
    }

    internal SafeFileHandle Handle { get; }
    internal PhotonWorkspaceFileEvidence Evidence { get; }

    public void Dispose() => Handle.Dispose();
}

internal sealed class PhotonWorkspaceDirectoryLease : IDisposable
{
    private readonly List<SafeFileHandle> handles;

    internal PhotonWorkspaceDirectoryLease(List<SafeFileHandle> handles, SafeFileHandle parentHandle, string parentPath)
    {
        this.handles = handles;
        ParentHandle = parentHandle;
        ParentPath = parentPath;
    }

    internal SafeFileHandle ParentHandle { get; }
    internal string ParentPath { get; }

    public void Dispose()
    {
        for (var index = handles.Count - 1; index >= 0; index--)
        {
            handles[index].Dispose();
        }
    }
}

internal sealed class PhotonWorkspaceNativeRoot : IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint ReplaceFileWriteThrough = 0x00000002;
    private const int FileRenameInfo = 3;
    private const int FileCaseSensitiveInfo = 23;
    private const uint FileCsFlagCaseSensitiveDir = 0x00000001;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorFileExists = 80;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorAlreadyExists = 183;
    private const int ErrorNotSupported = 50;
    private const uint InvalidFileAttributes = 0xffffffff;

    private readonly PhotonWorkspacePathPolicy paths;
    private readonly SafeFileHandle rootHandle;
    private int disposed;

    internal PhotonWorkspaceNativeRoot(PhotonWorkspacePathPolicy paths)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PhotonWorkspaceException("workspace_windows_required");
        }

        this.paths = paths;
        ValidateRootPath(paths.Root);
        var candidateRoot = OpenDirectoryHandle(paths.Root);
        try
        {
            VerifyDirectoryHandle(candidateRoot, paths.Root);
            rootHandle = candidateRoot;
        }
        catch
        {
            candidateRoot.Dispose();
            throw;
        }
    }

    internal PhotonWorkspaceDirectoryLease HoldParent(string normalizedRelativePath)
    {
        ThrowIfDisposed();
        var components = normalizedRelativePath.Split('/');
        var handles = new List<SafeFileHandle>();
        var current = paths.Root;
        try
        {
            for (var index = 0; index < components.Length - 1; index++)
            {
                current = Path.Combine(current, components[index]);
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(current);
                }
                catch
                {
                    throw new PhotonWorkspaceException("workspace_directory_unavailable");
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new PhotonWorkspaceException("workspace_reparse_rejected");
                }

                var handle = OpenDirectoryHandle(current);
                try
                {
                    VerifyDirectoryHandle(handle, current);
                    handles.Add(handle);
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }
            }

            var parentHandle = handles.Count == 0 ? rootHandle : handles[^1];
            return new PhotonWorkspaceDirectoryLease(handles, parentHandle, current);
        }
        catch
        {
            foreach (var handle in handles)
            {
                handle.Dispose();
            }

            throw;
        }
    }

    internal PhotonWorkspaceVerifiedFile OpenFile(
        string normalizedRelativePath,
        bool includeContent,
        PhotonWorkspaceDirectoryLease? heldParent = null)
    {
        ThrowIfDisposed();
        using var ownedParent = heldParent is null ? HoldParent(normalizedRelativePath) : null;
        var absolutePath = paths.Resolve(normalizedRelativePath);
        var handle = CreateFileW(
            absolutePath,
            GenericRead | DeleteAccess,
            FileShareRead,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new PhotonWorkspaceException(error is ErrorFileNotFound or ErrorPathNotFound
                ? "workspace_file_not_found"
                : "workspace_file_unavailable");
        }

        try
        {
            VerifyFinalPath(handle, absolutePath);
            var evidence = ReadEvidence(handle, includeContent);
            if (evidence.LinkCount != 1)
            {
                throw new PhotonWorkspaceException("workspace_hardlink_rejected");
            }

            return new PhotonWorkspaceVerifiedFile(handle, evidence);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal PhotonWorkspaceVerifiedFile WriteStage(
        PhotonWorkspaceDirectoryLease parent,
        byte[] content,
        out string stagePath,
        out string stageLeaf)
    {
        ThrowIfDisposed();
        for (var attempt = 0; attempt < 8; attempt++)
        {
            stageLeaf = ".photon-workspace-stage-" + PhotonWorkspaceGuards.OpaqueToken();
            stagePath = Path.Combine(parent.ParentPath, stageLeaf);
            try
            {
                using (var stream = new FileStream(
                    stagePath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.WriteThrough))
                {
                    stream.Write(content);
                    stream.Flush(flushToDisk: true);
                }

                var handle = CreateFileW(
                    stagePath,
                    GenericRead | DeleteAccess,
                    0,
                    IntPtr.Zero,
                    OpenExisting,
                    FileFlagOpenReparsePoint,
                    IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    handle.Dispose();
                    TryDeleteOwnedInternal(stagePath);
                    throw new PhotonWorkspaceException("workspace_stage_unavailable");
                }

                try
                {
                    VerifyFinalPath(handle, stagePath);
                    var evidence = ReadEvidence(handle, includeContent: false);
                    if (evidence.LinkCount != 1 ||
                        evidence.ByteLength != content.LongLength ||
                        !PhotonWorkspaceGuards.FixedEquals(evidence.Sha256, PhotonWorkspaceGuards.Sha256(content)))
                    {
                        throw new PhotonWorkspaceException("workspace_stage_verification_failed");
                    }

                    return new PhotonWorkspaceVerifiedFile(handle, evidence);
                }
                catch
                {
                    handle.Dispose();
                    TryDeleteOwnedInternal(stagePath);
                    throw;
                }
            }
            catch (IOException exception) when ((exception.HResult & 0xffff) is ErrorFileExists or ErrorAlreadyExists)
            {
                continue;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                TryDeleteOwnedInternal(stagePath);
                throw new PhotonWorkspaceException("workspace_stage_unavailable");
            }
        }

        stagePath = string.Empty;
        stageLeaf = string.Empty;
        throw new PhotonWorkspaceException("workspace_stage_unavailable");
    }

    internal void RenameExact(
        PhotonWorkspaceVerifiedFile source,
        SafeFileHandle destinationDirectory,
        string destinationLeaf)
    {
        ThrowIfDisposed();
        if (destinationLeaf.IndexOfAny(new[] { '\\', '/', ':' }) >= 0 || destinationLeaf.Length == 0)
        {
            throw new PhotonWorkspaceException("workspace_internal_contract_invalid");
        }

        var destinationPath = Path.Combine(GetFinalPath(destinationDirectory), destinationLeaf);
        var fileNameBytes = Encoding.Unicode.GetBytes(destinationPath);
        var rootOffset = Marshal.OffsetOf<FileRenameInformation>(nameof(FileRenameInformation.RootDirectory)).ToInt32();
        var nameLengthOffset = Marshal.OffsetOf<FileRenameInformation>(nameof(FileRenameInformation.FileNameLength)).ToInt32();
        var nameOffset = Marshal.OffsetOf<FileRenameInformation>(nameof(FileRenameInformation.FileName)).ToInt32();
        var bufferSize = checked(Marshal.SizeOf<FileRenameInformation>() + fileNameBytes.Length);
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            for (var index = 0; index < bufferSize; index++)
            {
                Marshal.WriteByte(buffer, index, 0);
            }

            Marshal.WriteInt32(buffer, 0, 0);
            Marshal.WriteIntPtr(buffer, rootOffset, IntPtr.Zero);
            Marshal.WriteInt32(buffer, nameLengthOffset, fileNameBytes.Length);
            Marshal.Copy(fileNameBytes, 0, IntPtr.Add(buffer, nameOffset), fileNameBytes.Length);
            if (!SetFileInformationByHandle(source.Handle, FileRenameInfo, buffer, (uint)bufferSize))
            {
                throw new PhotonWorkspaceException("workspace_target_conflict");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal void ReplaceWithRollback(
        string targetPath,
        string stagePath,
        string rollbackPath)
    {
        ThrowIfDisposed();
        if (!ReplaceFileW(targetPath, stagePath, rollbackPath, ReplaceFileWriteThrough, IntPtr.Zero, IntPtr.Zero))
        {
            throw new PhotonWorkspaceException("workspace_target_conflict");
        }
    }

    internal void FlushDirectory(SafeFileHandle directoryHandle)
    {
        ThrowIfDisposed();
        if (!FlushFileBuffers(directoryHandle))
        {
            var error = Marshal.GetLastWin32Error();
            if (error is not ErrorInvalidParameter and not ErrorNotSupported)
            {
                throw new PhotonWorkspaceException("workspace_durability_unavailable");
            }
        }
    }

    internal SafeFileHandle CreateHeldInternalDirectory(out string absolutePath, out string leaf)
    {
        ThrowIfDisposed();
        for (var attempt = 0; attempt < 8; attempt++)
        {
            leaf = PhotonWorkspacePathPolicy.TrashPrefix + PhotonWorkspaceGuards.OpaqueToken();
            absolutePath = Path.Combine(paths.Root, leaf);
            try
            {
                if (!CreateDirectoryW(absolutePath, IntPtr.Zero))
                {
                    continue;
                }

                var handle = OpenDirectoryHandle(absolutePath);
                try
                {
                    VerifyDirectoryHandle(handle, absolutePath);
                    FlushDirectory(rootHandle);
                    return handle;
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }
            }
            catch (IOException)
            {
                continue;
            }
        }

        absolutePath = string.Empty;
        leaf = string.Empty;
        throw new PhotonWorkspaceException("workspace_trash_unavailable");
    }

    internal PhotonWorkspaceVerifiedFile OpenInternalFile(string absolutePath, bool includeContent)
    {
        ThrowIfDisposed();
        EnsureOwnedInternalPath(absolutePath);
        var handle = CreateFileW(
            absolutePath,
            GenericRead | DeleteAccess,
            FileShareRead,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new PhotonWorkspaceException("workspace_trash_receipt_stale");
        }

        try
        {
            VerifyFinalPath(handle, absolutePath);
            var evidence = ReadEvidence(handle, includeContent);
            if (evidence.LinkCount != 1)
            {
                throw new PhotonWorkspaceException("workspace_hardlink_rejected");
            }

            return new PhotonWorkspaceVerifiedFile(handle, evidence);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal void RequireAbsent(string absolutePath)
    {
        ThrowIfDisposed();
        var attributes = GetFileAttributesW(absolutePath);
        if (attributes != InvalidFileAttributes)
        {
            throw new PhotonWorkspaceException("workspace_target_conflict");
        }

        var error = Marshal.GetLastWin32Error();
        if (error is not ErrorFileNotFound and not ErrorPathNotFound)
        {
            throw new PhotonWorkspaceException("workspace_target_unavailable");
        }
    }

    internal void TryDeleteOwnedInternal(string absolutePath)
    {
        try
        {
            EnsureOwnedInternalPath(absolutePath);
            File.Delete(absolutePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PhotonWorkspaceException)
        {
            // Cleanup is best effort. User files are never targeted here.
        }
    }

    internal static bool EvidenceMatches(
        PhotonWorkspaceFileEvidence actual,
        PhotonWorkspaceFileEvidence expected)
    {
        return actual.SameObjectAndContent(expected);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            rootHandle.Dispose();
        }
    }

    private static PhotonWorkspaceFileEvidence ReadEvidence(SafeFileHandle handle, bool includeContent)
    {
        if (!GetFileInformationByHandle(handle, out var info))
        {
            throw new PhotonWorkspaceException("workspace_file_unavailable");
        }

        if ((info.FileAttributes & (FileAttributeDirectory | FileAttributeReparsePoint)) != 0)
        {
            throw new PhotonWorkspaceException("workspace_file_type_rejected");
        }

        var length = checked(((long)info.FileSizeHigh << 32) | info.FileSizeLow);
        if (length < 0 || length > PhotonWorkspaceContract.MaxFileBytes)
        {
            throw new PhotonWorkspaceException("workspace_file_too_large");
        }

        byte[]? content = includeContent ? new byte[checked((int)length)] : null;
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long offset = 0;
        while (offset < length)
        {
            var requested = (int)Math.Min(buffer.Length, length - offset);
            var read = RandomAccess.Read(handle, buffer.AsSpan(0, requested), offset);
            if (read <= 0)
            {
                throw new PhotonWorkspaceException("workspace_file_changed");
            }

            hasher.AppendData(buffer, 0, read);
            if (content is not null)
            {
                buffer.AsSpan(0, read).CopyTo(content.AsSpan(checked((int)offset), read));
            }

            offset += read;
        }

        if (RandomAccess.Read(handle, buffer.AsSpan(0, 1), length) != 0)
        {
            throw new PhotonWorkspaceException("workspace_file_changed");
        }

        var fileTime = checked(((long)info.LastWriteTimeHigh << 32) | info.LastWriteTimeLow);
        return new PhotonWorkspaceFileEvidence(
            info.VolumeSerialNumber,
            ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow,
            info.NumberOfLinks,
            length,
            new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime)),
            Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant(),
            content);
    }

    private static SafeFileHandle OpenDirectoryHandle(string path)
    {
        var handle = CreateFileW(
            path,
            GenericRead | GenericWrite | FileReadAttributes,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new PhotonWorkspaceException("workspace_directory_unavailable");
        }

        return handle;
    }

    private static void VerifyDirectoryHandle(SafeFileHandle handle, string expectedPath)
    {
        VerifyFinalPath(handle, expectedPath);
        if (!GetFileInformationByHandle(handle, out var info) ||
            (info.FileAttributes & FileAttributeDirectory) == 0 ||
            (info.FileAttributes & FileAttributeReparsePoint) != 0)
        {
            throw new PhotonWorkspaceException("workspace_reparse_rejected");
        }

        if (GetFileInformationByHandleEx(
                handle,
                FileCaseSensitiveInfo,
                out FileCaseSensitiveInformation caseInfo,
                (uint)Marshal.SizeOf<FileCaseSensitiveInformation>()))
        {
            if ((caseInfo.Flags & FileCsFlagCaseSensitiveDir) != 0)
            {
                throw new PhotonWorkspaceException("workspace_case_sensitive_directory_rejected");
            }
        }
        else
        {
            var error = Marshal.GetLastWin32Error();
            if (error is not ErrorInvalidParameter and not ErrorNotSupported)
            {
                throw new PhotonWorkspaceException("workspace_directory_unavailable");
            }
        }
    }

    private static void VerifyFinalPath(SafeFileHandle handle, string expectedPath)
    {
        var finalPath = GetFinalPath(handle);
        if (!string.Equals(
                Path.TrimEndingDirectorySeparator(finalPath),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedPath)),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new PhotonWorkspaceException("workspace_identity_mismatch");
        }
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        var builder = new StringBuilder(32_768);
        var length = GetFinalPathNameByHandleW(handle, builder, (uint)builder.Capacity, 0);
        if (length == 0 || length >= builder.Capacity)
        {
            throw new PhotonWorkspaceException("workspace_identity_unavailable");
        }

        var finalPath = builder.ToString();
        if (finalPath.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
        {
            throw new PhotonWorkspaceException("workspace_remote_path_rejected");
        }

        if (finalPath.StartsWith("\\\\?\\", StringComparison.Ordinal))
        {
            finalPath = finalPath[4..];
        }

        return Path.GetFullPath(finalPath);
    }

    private static void ValidateRootPath(string root)
    {
        if (!Path.IsPathFullyQualified(root) ||
            root.StartsWith("\\\\", StringComparison.Ordinal) ||
            root.StartsWith("\\\\?\\", StringComparison.Ordinal) ||
            !Directory.Exists(root))
        {
            throw new PhotonWorkspaceException("workspace_root_invalid");
        }

        var driveRoot = Path.GetPathRoot(root);
        if (string.IsNullOrEmpty(driveRoot) ||
            string.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(driveRoot), StringComparison.OrdinalIgnoreCase))
        {
            throw new PhotonWorkspaceException("workspace_root_too_broad");
        }

        try
        {
            var drive = new DriveInfo(driveRoot);
            if (!drive.IsReady || !drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            {
                throw new PhotonWorkspaceException("workspace_ntfs_required");
            }
        }
        catch (PhotonWorkspaceException)
        {
            throw;
        }
        catch
        {
            throw new PhotonWorkspaceException("workspace_root_invalid");
        }
    }

    private void EnsureOwnedInternalPath(string absolutePath)
    {
        var full = Path.GetFullPath(absolutePath);
        var rootPrefix = Path.TrimEndingDirectorySeparator(paths.Root) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new PhotonWorkspaceException("workspace_internal_contract_invalid");
        }

        var relative = Path.GetRelativePath(paths.Root, full);
        var components = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
        var directInternal = Path.GetFileName(full).StartsWith(".photon-workspace-", StringComparison.OrdinalIgnoreCase);
        var trashInternal = components.Length == 2 &&
            components[0].StartsWith(PhotonWorkspacePathPolicy.TrashPrefix, StringComparison.OrdinalIgnoreCase) &&
            !components[1].Contains(':', StringComparison.Ordinal);
        if (!directInternal && !trashInternal)
        {
            throw new PhotonWorkspaceException("workspace_internal_contract_invalid");
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            throw new PhotonWorkspaceException("workspace_service_closed");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal uint CreationTimeLow;
        internal uint CreationTimeHigh;
        internal uint LastAccessTimeLow;
        internal uint LastAccessTimeHigh;
        internal uint LastWriteTimeLow;
        internal uint LastWriteTimeHigh;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileCaseSensitiveInformation
    {
        internal uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileRenameInformation
    {
        internal uint ReplaceIfExists;
        internal IntPtr RootDirectory;
        internal uint FileNameLength;
        internal char FileName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int informationClass,
        out FileCaseSensitiveInformation information,
        uint bufferSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder path,
        uint pathLength,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int informationClass,
        IntPtr information,
        uint bufferSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReplaceFileW(
        string replacedFileName,
        string replacementFileName,
        string backupFileName,
        uint replaceFlags,
        IntPtr exclude,
        IntPtr reserved);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle file);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, IntPtr securityAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributesW(string path);
}
