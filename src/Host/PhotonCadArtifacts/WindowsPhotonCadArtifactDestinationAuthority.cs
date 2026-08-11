using System.Buffers;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace PhotonCadArtifacts;

internal readonly record struct PhotonCadWindowsFileIdentity(uint VolumeSerialNumber, ulong FileIndex);

internal readonly record struct PhotonCadWindowsFileMetadata(
    PhotonCadWindowsFileIdentity Identity,
    long ByteLength);

internal enum PhotonCadExactDeleteOutcome
{
    Deleted,
    Missing,
    ExactIdentityAbsent,
    Blocked,
}

internal readonly record struct PhotonCadExactDeleteResult(
    PhotonCadExactDeleteOutcome Outcome,
    string ReasonCode);

internal enum PhotonCadExactBindingOutcome
{
    Exact,
    Missing,
    Foreign,
    Blocked,
}

internal readonly record struct PhotonCadExactBindingResult(
    PhotonCadExactBindingOutcome Outcome,
    string ReasonCode);

internal static class PhotonCadWindowsFilePolicy
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint CreateNew = 1;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint FileFlagSequentialScan = 0x08000000;
    private const uint FileFlagWriteThrough = 0x80000000;
    private const uint InvalidFileAttributes = 0xffffffff;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;
    private const int FileDispositionInfoClass = 4;
    private const int FileRenameInfoClass = 3;

    internal static void EnsureOwnedDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PhotonCadArtifactException("windows_host_required");
        var full = RequireAbsoluteLocal(path);
        var parent = Path.GetDirectoryName(full);
        if (!string.IsNullOrWhiteSpace(parent)) RejectReparseTraversal(parent);
        Directory.CreateDirectory(full);
        RejectReparseTraversal(full);
        _ = InspectDirectory(full);
    }

    internal static void RequireMissingRegularTarget(string path, string exactParent)
    {
        var full = RequireExactChild(path, exactParent);
        RejectReparseTraversal(exactParent);
        if (ProbeAttributes(full) is not null)
            throw new PhotonCadArtifactException("destination_exists");
    }

    internal static FileStream CreateNewOwnedFile(string path, string exactParent, int bufferSize)
    {
        var full = RequireExactChild(path, exactParent);
        RejectReparseTraversal(exactParent);
        var handle = CreateFileW(
            full,
            GenericRead | GenericWrite | DeleteAccess,
            FileShareRead | FileShareDelete,
            IntPtr.Zero,
            CreateNew,
            FileFlagOpenReparsePoint | FileFlagOverlapped | FileFlagSequentialScan | FileFlagWriteThrough,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new PhotonCadArtifactException("artifact_partial_create_failed");
        }
        try
        {
            return new FileStream(handle, FileAccess.ReadWrite, bufferSize, isAsync: true);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static PhotonCadWindowsFileIdentity InspectRegularFile(string path, string exactParent)
        => InspectRegularFileMetadata(path, exactParent).Identity;

    internal static PhotonCadWindowsFileMetadata InspectRegularFileMetadata(string path, string exactParent)
    {
        var full = RequireExactChild(path, exactParent);
        RejectReparseTraversal(exactParent);
        using var handle = OpenPath(
            full,
            GenericRead,
            FileShareRead | FileShareWrite | FileShareDelete,
            0,
            "file_identity_unavailable");
        var info = Information(handle);
        var identity = InspectRegularInformation(info);
        return new PhotonCadWindowsFileMetadata(identity, FileLength(info));
    }

    internal static PhotonCadWindowsFileMetadata InspectRegularFileMetadata(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.IsInvalid || handle.IsClosed) throw new PhotonCadArtifactException("file_identity_unavailable");
        var info = Information(handle);
        return new PhotonCadWindowsFileMetadata(InspectRegularInformation(info), FileLength(info));
    }

    internal static void SetDeleteOnClose(SafeFileHandle handle, bool delete)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var disposition = new FileDispositionInformation { DeleteFile = delete ? (byte)1 : (byte)0 };
        if (SetFileInformationByHandle(
            handle,
            FileDispositionInfoClass,
            ref disposition,
            (uint)Marshal.SizeOf<FileDispositionInformation>())) return;
        var error = Marshal.GetLastWin32Error();
        throw new PhotonCadArtifactException(error is ErrorAccessDenied or ErrorSharingViolation
            ? "artifact_cleanup_blocked"
            : "artifact_cleanup_failed");
    }

    internal static void RenameOwnedHandle(
        SafeFileHandle handle,
        string destinationPath,
        string exactParent)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var destination = RequireExactChild(destinationPath, exactParent);
        RequireMissingRegularTarget(destination, exactParent);
        var encoded = System.Text.Encoding.Unicode.GetBytes(destination);
        var headerSize = Marshal.OffsetOf<FileRenameInformation>(nameof(FileRenameInformation.FileName)).ToInt32();
        var bufferSize = checked(Marshal.SizeOf<FileRenameInformation>() + encoded.Length);
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            Marshal.Copy(new byte[bufferSize], 0, buffer, bufferSize);
            Marshal.WriteInt32(buffer, 0, 0);
            Marshal.WriteIntPtr(buffer, Marshal.OffsetOf<FileRenameInformation>(nameof(FileRenameInformation.RootDirectory)).ToInt32(), IntPtr.Zero);
            Marshal.WriteInt32(buffer, Marshal.OffsetOf<FileRenameInformation>(nameof(FileRenameInformation.FileNameLength)).ToInt32(), encoded.Length);
            Marshal.Copy(encoded, 0, IntPtr.Add(buffer, headerSize), encoded.Length);
            if (!SetFileInformationByHandle(handle, FileRenameInfoClass, buffer, (uint)bufferSize))
                throw new PhotonCadArtifactException("artifact_rename_failed");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static void FlushHandle(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!FlushFileBuffers(handle)) throw new PhotonCadArtifactException("artifact_flush_failed");
    }

    internal static void FlushDirectory(string path)
    {
        var full = RequireAbsoluteLocal(path);
        RejectReparseTraversal(full);
        using var handle = CreateFileW(
            full,
            GenericRead | GenericWrite,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid || !FlushFileBuffers(handle))
            throw new PhotonCadArtifactException("directory_flush_unavailable");
    }

    internal static PhotonCadWindowsFileIdentity InspectDirectory(string path)
    {
        var full = RequireAbsoluteLocal(path);
        RejectReparseTraversal(full);
        using var handle = CreateFileW(
            full,
            0,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid) throw new PhotonCadArtifactException("directory_identity_unavailable");
        var info = Information(handle);
        if ((info.FileAttributes & (uint)FileAttributes.Directory) == 0 ||
            (info.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
            throw new PhotonCadArtifactException("unsafe_directory");
        return Identity(info);
    }

    internal static string RequireNewStepDestination(string path)
    {
        var full = RequireAbsoluteLocal(path);
        if (!Path.GetExtension(full).Equals(".step", StringComparison.OrdinalIgnoreCase))
            throw new PhotonCadArtifactException("step_destination_required");
        var parent = Path.GetDirectoryName(full);
        if (string.IsNullOrWhiteSpace(parent) || ProbeAttributes(parent) is not { } parentAttributes ||
            (parentAttributes & FileAttributes.Directory) == 0)
            throw new PhotonCadArtifactException("destination_parent_unavailable");
        RejectReparseTraversal(parent);
        _ = InspectDirectory(parent);
        if (ProbeAttributes(full) is not null)
            throw new PhotonCadArtifactException("destination_exists");
        return full;
    }

    internal static string RequireExactDestination(string path, string expectedParent)
    {
        var full = RequireAbsoluteLocal(path);
        var parent = Path.GetDirectoryName(full) ?? throw new PhotonCadArtifactException("destination_parent_unavailable");
        if (!Path.GetFullPath(parent).Equals(Path.GetFullPath(expectedParent), StringComparison.OrdinalIgnoreCase))
            throw new PhotonCadArtifactException("destination_parent_changed");
        RejectReparseTraversal(parent);
        return full;
    }

    internal static void RejectReparseTraversal(string path)
    {
        var full = RequireAbsoluteLocal(path);
        var root = Path.GetPathRoot(full) ?? throw new PhotonCadArtifactException("invalid_local_path");
        var relative = Path.GetRelativePath(root, full);
        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var attributes = ProbeAttributes(current);
            if (attributes is null) continue;
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new PhotonCadArtifactException("reparse_point_rejected");
        }
    }

    internal static async ValueTask<PhotonCadExactDeleteResult> DeleteIfExactBindingAsync(
        string path,
        string exactParent,
        PhotonCadWindowsFileIdentity expectedIdentity,
        long expectedByteLength,
        string expectedDigest,
        CancellationToken cancellationToken = default)
    {
        var full = RequireExactChild(path, exactParent);
        RejectReparseTraversal(exactParent);
        using var handle = TryOpenDeleteHandle(full, out var missingOrBlocked);
        if (handle is null)
        {
            return missingOrBlocked == PhotonCadExactDeleteOutcome.Missing
                ? new PhotonCadExactDeleteResult(PhotonCadExactDeleteOutcome.Missing, "artifact_absent")
                : new PhotonCadExactDeleteResult(PhotonCadExactDeleteOutcome.Blocked, "artifact_cleanup_blocked");
        }

        var info = Information(handle);
        PhotonCadWindowsFileIdentity identity;
        try
        {
            identity = InspectRegularInformation(info);
        }
        catch (PhotonCadArtifactException)
        {
            return new PhotonCadExactDeleteResult(
                PhotonCadExactDeleteOutcome.ExactIdentityAbsent,
                "artifact_identity_absent");
        }
        if (identity != expectedIdentity)
            return new PhotonCadExactDeleteResult(
                PhotonCadExactDeleteOutcome.ExactIdentityAbsent,
                "artifact_identity_absent");
        if (FileLength(info) != expectedByteLength)
            return new PhotonCadExactDeleteResult(PhotonCadExactDeleteOutcome.Blocked, "artifact_cleanup_binding_changed");

        var digest = await HashHandleAsync(handle, expectedByteLength, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(digest[7..]),
            Convert.FromHexString(PhotonCadArtifactGuards.Digest(expectedDigest, nameof(expectedDigest))[7..])))
            return new PhotonCadExactDeleteResult(PhotonCadExactDeleteOutcome.Blocked, "artifact_cleanup_binding_changed");

        try
        {
            MarkDeleteOnClose(handle);
            return new PhotonCadExactDeleteResult(PhotonCadExactDeleteOutcome.Deleted, "artifact_deleted");
        }
        catch (PhotonCadArtifactException exception) when (exception.Code == "artifact_cleanup_blocked")
        {
            return new PhotonCadExactDeleteResult(PhotonCadExactDeleteOutcome.Blocked, exception.Code);
        }
    }

    internal static async ValueTask<PhotonCadExactBindingResult> VerifyExactBindingAsync(
        string path,
        string exactParent,
        PhotonCadWindowsFileIdentity expectedIdentity,
        long expectedByteLength,
        string expectedDigest,
        CancellationToken cancellationToken = default)
    {
        var full = RequireExactChild(path, exactParent);
        RejectReparseTraversal(exactParent);
        SafeFileHandle handle;
        try
        {
            handle = OpenPath(
                full,
                GenericRead,
                FileShareRead,
                0,
                "file_identity_unavailable");
        }
        catch (PhotonCadArtifactException exception) when (exception.Code == "file_identity_unavailable")
        {
            var attributes = ProbeAttributes(full);
            return attributes is null
                ? new PhotonCadExactBindingResult(PhotonCadExactBindingOutcome.Missing, "exact_output_missing")
                : new PhotonCadExactBindingResult(PhotonCadExactBindingOutcome.Blocked, "exact_output_unavailable");
        }
        using (handle)
        {
            PhotonCadWindowsFileMetadata metadata;
            try
            {
                metadata = InspectRegularFileMetadata(handle);
            }
            catch (PhotonCadArtifactException)
            {
                return new PhotonCadExactBindingResult(PhotonCadExactBindingOutcome.Foreign, "exact_output_foreign");
            }
            if (metadata.Identity != expectedIdentity || metadata.ByteLength != expectedByteLength)
                return new PhotonCadExactBindingResult(PhotonCadExactBindingOutcome.Foreign, "exact_output_foreign");
            var digest = await HashHandleAsync(handle, expectedByteLength, cancellationToken).ConfigureAwait(false);
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(digest[7..]),
                Convert.FromHexString(PhotonCadArtifactGuards.Digest(expectedDigest, nameof(expectedDigest))[7..]))
                ? new PhotonCadExactBindingResult(PhotonCadExactBindingOutcome.Exact, "exact_output_verified")
                : new PhotonCadExactBindingResult(PhotonCadExactBindingOutcome.Foreign, "exact_output_foreign");
        }
    }

    internal static async ValueTask<PhotonCadExactBindingResult> VerifyExactHandleBindingAsync(
        SafeFileHandle handle,
        PhotonCadWindowsFileIdentity expectedIdentity,
        long expectedByteLength,
        string expectedDigest,
        CancellationToken cancellationToken = default)
    {
        PhotonCadWindowsFileMetadata metadata;
        try
        {
            metadata = InspectRegularFileMetadata(handle);
        }
        catch (PhotonCadArtifactException)
        {
            return new PhotonCadExactBindingResult(PhotonCadExactBindingOutcome.Foreign, "exact_output_foreign");
        }
        if (metadata.Identity != expectedIdentity || metadata.ByteLength != expectedByteLength)
            return new PhotonCadExactBindingResult(PhotonCadExactBindingOutcome.Foreign, "exact_output_foreign");
        var digest = await HashHandleAsync(handle, expectedByteLength, cancellationToken).ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(digest[7..]),
            Convert.FromHexString(PhotonCadArtifactGuards.Digest(expectedDigest, nameof(expectedDigest))[7..]))
            ? new PhotonCadExactBindingResult(PhotonCadExactBindingOutcome.Exact, "exact_output_verified")
            : new PhotonCadExactBindingResult(PhotonCadExactBindingOutcome.Foreign, "exact_output_foreign");
    }

    private static PhotonCadWindowsFileIdentity InspectFileHandle(SafeFileHandle handle) =>
        InspectRegularInformation(Information(handle));

    private static PhotonCadWindowsFileIdentity InspectRegularInformation(ByHandleFileInformation info)
    {
        if ((info.FileAttributes & ((uint)FileAttributes.Directory | (uint)FileAttributes.ReparsePoint)) != 0 ||
            info.NumberOfLinks != 1)
            throw new PhotonCadArtifactException("unsafe_regular_file");
        return Identity(info);
    }

    private static long FileLength(ByHandleFileInformation info) => checked(
        (long)(((ulong)info.FileSizeHigh << 32) | info.FileSizeLow));

    private static FileAttributes? ProbeAttributes(string path)
    {
        var attributes = GetFileAttributesW(path);
        if (attributes != InvalidFileAttributes) return (FileAttributes)attributes;
        var error = Marshal.GetLastWin32Error();
        if (error is ErrorFileNotFound or ErrorPathNotFound) return null;
        throw new PhotonCadArtifactException(error == ErrorAccessDenied
            ? "path_inspection_denied"
            : "path_inspection_failed");
    }

    private static SafeFileHandle OpenPath(
        string path,
        uint desiredAccess,
        uint shareMode,
        uint extraFlags,
        string failureCode)
    {
        var handle = CreateFileW(
            path,
            desiredAccess,
            shareMode,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | extraFlags,
            IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        handle.Dispose();
        throw new PhotonCadArtifactException(failureCode);
    }

    private static SafeFileHandle? TryOpenDeleteHandle(
        string path,
        out PhotonCadExactDeleteOutcome missingOrBlocked)
    {
        var handle = CreateFileW(
            path,
            GenericRead | DeleteAccess,
            FileShareRead,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            missingOrBlocked = default;
            return handle;
        }
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        if (error is ErrorFileNotFound or ErrorPathNotFound)
        {
            missingOrBlocked = PhotonCadExactDeleteOutcome.Missing;
            return null;
        }
        if (error is ErrorAccessDenied or ErrorSharingViolation)
        {
            missingOrBlocked = PhotonCadExactDeleteOutcome.Blocked;
            return null;
        }
        throw new PhotonCadArtifactException("path_inspection_failed");
    }

    private static async ValueTask<string> HashHandleAsync(
        SafeFileHandle handle,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(1_048_576);
        long offset = 0;
        try
        {
            while (offset < expectedLength)
            {
                var count = (int)Math.Min(buffer.Length, expectedLength - offset);
                var read = await RandomAccess.ReadAsync(
                    handle,
                    buffer.AsMemory(0, count),
                    offset,
                    cancellationToken).ConfigureAwait(false);
                if (read == 0) throw new PhotonCadArtifactException("artifact_cleanup_binding_changed");
                hash.AppendData(buffer, 0, read);
                offset = checked(offset + read);
            }
            if (await RandomAccess.ReadAsync(handle, buffer.AsMemory(0, 1), offset, cancellationToken).ConfigureAwait(false) != 0)
                throw new PhotonCadArtifactException("artifact_cleanup_binding_changed");
            return $"sha256:{Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()}";
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static void MarkDeleteOnClose(SafeFileHandle handle)
        => SetDeleteOnClose(handle, delete: true);

    private static ByHandleFileInformation Information(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info))
            throw new PhotonCadArtifactException("file_identity_unavailable");
        return info;
    }

    private static PhotonCadWindowsFileIdentity Identity(ByHandleFileInformation info) => new(
        info.VolumeSerialNumber,
        ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);

    private static string RequireExactChild(string path, string exactParent)
    {
        var full = RequireAbsoluteLocal(path);
        var parent = RequireAbsoluteLocal(exactParent);
        if (!string.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase))
            throw new PhotonCadArtifactException("outside_owned_directory");
        return full;
    }

    private static string RequireAbsoluteLocal(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            path.StartsWith("\\\\", StringComparison.Ordinal) || path.Contains('\0'))
            throw new PhotonCadArtifactException("invalid_local_path");
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new PhotonCadArtifactException("invalid_local_path");
        }
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributesW(string fileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        ref FileDispositionInformation fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        IntPtr fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle file);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation
    {
        internal byte DeleteFile;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileRenameInformation
    {
        internal uint ReplaceIfExists;
        internal IntPtr RootDirectory;
        internal uint FileNameLength;
        internal char FileName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }
}

/// <summary>
/// Native UI implementations return a host-only absolute path. The artifact
/// authority never serializes it or exposes it through a public descriptor.
/// </summary>
public interface IPhotonCadNativeDestinationPicker
{
    ValueTask<string?> PickNewStepPathAsync(
        string suggestedFileName,
        CancellationToken cancellationToken = default);
}

public sealed class WindowsPhotonCadArtifactDestinationAuthority : IPhotonCadArtifactDestinationAuthority
{
    private const int BufferSize = 1_048_576;
    private readonly IPhotonCadNativeDestinationPicker _picker;
    private readonly IPhotonCadArtifactContextAuthority _contextAuthority;
    private readonly TimeProvider _timeProvider;
    private readonly PhotonCadMonotonicClock _clock;
    private readonly TimeSpan _timeToLive;
    private readonly PhotonCadDestinationTransactionJournal _journal;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, DestinationEntry> _destinations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PhotonCadDestinationTransaction> _recovered = new(StringComparer.Ordinal);
    private int _disposeStarted;
    private bool _disposed;

    public WindowsPhotonCadArtifactDestinationAuthority(
        IPhotonCadNativeDestinationPicker picker,
        IPhotonCadArtifactContextAuthority contextAuthority,
        string journalRoot,
        TimeProvider? timeProvider = null,
        TimeSpan? timeToLive = null)
        : this(picker, contextAuthority, journalRoot, timeProvider, timeToLive, null)
    {
    }

    internal WindowsPhotonCadArtifactDestinationAuthority(
        IPhotonCadNativeDestinationPicker picker,
        IPhotonCadArtifactContextAuthority contextAuthority,
        string journalRoot,
        TimeProvider? timeProvider,
        TimeSpan? timeToLive,
        IPhotonCadDestinationJournalProtector? journalProtector = null)
    {
        _picker = picker ?? throw new ArgumentNullException(nameof(picker));
        _contextAuthority = contextAuthority ?? throw new ArgumentNullException(nameof(contextAuthority));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _clock = new PhotonCadMonotonicClock(_timeProvider);
        _timeToLive = timeToLive ?? TimeSpan.FromMinutes(10);
        if (_timeToLive < TimeSpan.FromSeconds(30) || _timeToLive > TimeSpan.FromMinutes(30))
            throw new PhotonCadArtifactException("invalid_destination_ttl");
        _journal = new PhotonCadDestinationTransactionJournal(
            journalRoot,
            protector: journalProtector,
            timeProvider: _timeProvider);
        foreach (var transaction in _journal.Load())
        {
            if (!_recovered.TryAdd(transaction.DestinationHandle.Value, transaction))
                throw new PhotonCadArtifactException("duplicate_destination_transaction");
        }
    }

    public async ValueTask<PhotonCadDestinationDescriptor?> PickAsync(
        PhotonCadArtifactDescriptor artifact,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        PhotonCadArtifactGuards.CurrentContext(_contextAuthority, artifact.Context);
        var selected = await _picker.PickNewStepPathAsync(artifact.DisplayName, cancellationToken).ConfigureAwait(false);
        if (selected is null) return null;
        var target = PhotonCadWindowsFilePolicy.RequireNewStepDestination(selected);
        var parent = Path.GetDirectoryName(target)!;
        var parentIdentity = PhotonCadWindowsFilePolicy.InspectDirectory(parent);
        var label = PhotonCadArtifactGuards.DisplayName(Path.GetFileName(target));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            PhotonCadArtifactGuards.CurrentContext(_contextAuthority, artifact.Context);
            var handle = PhotonCadDestinationHandle.New();
            var deadline = _clock.Start(_timeToLive);
            var expires = deadline.DisplayExpiresAtUtc;
            if (expires > artifact.ExpiresAtUtc) expires = artifact.ExpiresAtUtc;
            var descriptor = new PhotonCadDestinationDescriptor(handle, artifact.ArtifactHandle, artifact.Context, label, expires);
            _destinations.Add(handle.Value, new DestinationEntry(descriptor, deadline, target, parent, parentIdentity));
            return descriptor;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<PhotonCadDestinationDescriptor> ResolveAsync(
        PhotonCadDestinationHandle handle,
        PhotonCadArtifactDescriptor artifact,
        PhotonCadArtifactContext context,
        CancellationToken cancellationToken = default)
    {
        PhotonCadArtifactGuards.CurrentContext(_contextAuthority, context);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_destinations.TryGetValue(handle.Value, out _))
                return RequiredEntry(handle, artifact, context).Descriptor;
            var recovered = RequiredRecovered(handle, artifact, context);
            _ = await VerifyRecoveredAsync(recovered, cancellationToken).ConfigureAwait(false);
            return RecoveredDescriptor(recovered);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<PhotonCadDestinationCommitReceipt> CommitAsync(
        PhotonCadDestinationHandle handle,
        PhotonCadArtifactDescriptor artifact,
        PhotonCadArtifactContext context,
        Stream verifiedContent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verifiedContent);
        if (!verifiedContent.CanRead) throw new PhotonCadArtifactException("verified_content_required");
        PhotonCadArtifactGuards.CurrentContext(_contextAuthority, context);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_recovered.TryGetValue(handle.Value, out var recovered))
            {
                RequiredRecovered(handle, artifact, context);
                _ = await VerifyRecoveredAsync(recovered, cancellationToken).ConfigureAwait(false);
                var receipt = RecoveredReceipt(recovered);
                if (!await _journal.RetireAsync(recovered, CancellationToken.None).ConfigureAwait(false))
                    throw new PhotonCadArtifactException("destination_receipt_recovery_required");
                _recovered.Remove(handle.Value);
                return receipt;
            }
            var entry = RequiredEntry(handle, artifact, context);
            _destinations.Remove(handle.Value);
            if (verifiedContent.CanSeek && verifiedContent.Position != 0)
                throw new PhotonCadArtifactException("verified_content_not_rewound");
            return await CommitCoreAsync(entry, artifact, verifiedContent, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask RevokeAsync(PhotonCadDestinationHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_disposed) _destinations.Remove(handle.Value);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask RevokeContextAsync(PhotonCadArtifactContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            foreach (var entry in _destinations.Values.Where(value => value.Descriptor.Context == context).ToArray())
                _destinations.Remove(entry.Descriptor.DestinationHandle.Value);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            _destinations.Clear();
            _recovered.Clear();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private DestinationEntry RequiredEntry(
        PhotonCadDestinationHandle handle,
        PhotonCadArtifactDescriptor artifact,
        PhotonCadArtifactContext context)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(context);
        if (!_destinations.TryGetValue(handle.Value, out var entry) ||
            entry.Descriptor.Context != context ||
            entry.Descriptor.ArtifactHandle != artifact.ArtifactHandle ||
            _clock.IsExpired(entry.Deadline))
            throw new PhotonCadArtifactException("destination_unavailable");
        var target = PhotonCadWindowsFilePolicy.RequireExactDestination(entry.TargetPath, entry.ParentPath);
        if (!target.Equals(entry.TargetPath, StringComparison.OrdinalIgnoreCase) ||
            PhotonCadWindowsFilePolicy.InspectDirectory(entry.ParentPath) != entry.ParentIdentity)
            throw new PhotonCadArtifactException("destination_identity_changed");
        PhotonCadWindowsFilePolicy.RequireMissingRegularTarget(target, entry.ParentPath);
        return entry;
    }

    private PhotonCadDestinationTransaction RequiredRecovered(
        PhotonCadDestinationHandle handle,
        PhotonCadArtifactDescriptor artifact,
        PhotonCadArtifactContext context)
    {
        if (!_recovered.TryGetValue(handle.Value, out var transaction) ||
            transaction.ArtifactHandle != artifact.ArtifactHandle || transaction.Context != context ||
            transaction.ByteLength != artifact.ByteLength ||
            !transaction.ContentDigest.Equals(artifact.ContentDigest, StringComparison.Ordinal) ||
            _journal.IsExpired(transaction))
            throw new PhotonCadArtifactException("destination_recovery_unavailable");
        return transaction;
    }

    private static PhotonCadDestinationDescriptor RecoveredDescriptor(PhotonCadDestinationTransaction transaction) => new(
        transaction.DestinationHandle,
        transaction.ArtifactHandle,
        transaction.Context,
        transaction.DisplayLabel,
        DateTimeOffset.FromUnixTimeMilliseconds(transaction.ExpiresUnixMilliseconds).ToUniversalTime());

    private PhotonCadDestinationCommitReceipt RecoveredReceipt(PhotonCadDestinationTransaction transaction) => new(
        PhotonCadCommitReceiptHandle.New(),
        transaction.ArtifactHandle,
        transaction.ContentDigest,
        transaction.ByteLength,
        transaction.DisplayLabel,
        _clock.UtcNow());

    private async ValueTask<PhotonCadExactBindingResult> VerifyRecoveredAsync(
        PhotonCadDestinationTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (PhotonCadWindowsFilePolicy.InspectDirectory(transaction.ParentPath) != transaction.ParentIdentity)
            throw new PhotonCadArtifactException("destination_recovery_mismatch");
        var result = await PhotonCadWindowsFilePolicy.VerifyExactBindingAsync(
            transaction.TargetPath,
            transaction.ParentPath,
            transaction.OutputIdentity,
            transaction.ByteLength,
            transaction.ContentDigest,
            cancellationToken).ConfigureAwait(false);
        if (result.Outcome == PhotonCadExactBindingOutcome.Exact) return result;
        throw new PhotonCadArtifactException(result.Outcome switch
        {
            PhotonCadExactBindingOutcome.Missing => "destination_recovery_incomplete",
            PhotonCadExactBindingOutcome.Foreign => "destination_recovery_mismatch",
            _ => "destination_recovery_unavailable",
        });
    }

    private async ValueTask<PhotonCadDestinationCommitReceipt> CommitCoreAsync(
        DestinationEntry entry,
        PhotonCadArtifactDescriptor artifact,
        Stream source,
        CancellationToken cancellationToken)
    {
        var temporary = Path.Combine(
            entry.ParentPath,
            $".{entry.Descriptor.DisplayLabel}.photon-{Guid.NewGuid():N}.tmp");
        PhotonCadWindowsFilePolicy.RequireMissingRegularTarget(temporary, entry.ParentPath);
        PhotonCadDestinationTransaction? transaction = null;
        try
        {
            await using var output = PhotonCadWindowsFilePolicy.CreateNewOwnedFile(
                temporary,
                entry.ParentPath,
                BufferSize);
            var outputIdentity = PhotonCadWindowsFilePolicy
                .InspectRegularFileMetadata(output.SafeFileHandle)
                .Identity;
            PhotonCadWindowsFilePolicy.SetDeleteOnClose(output.SafeFileHandle, delete: true);
            var (digest, length) = await CopyAndHashAsync(source, output, artifact.ByteLength, cancellationToken)
                .ConfigureAwait(false);
            if (length != artifact.ByteLength || !digest.Equals(artifact.ContentDigest, StringComparison.Ordinal))
                throw new PhotonCadArtifactException("destination_digest_mismatch");
            cancellationToken.ThrowIfCancellationRequested();
            if (PhotonCadWindowsFilePolicy.InspectDirectory(entry.ParentPath) != entry.ParentIdentity)
                throw new PhotonCadArtifactException("destination_changed");
            PhotonCadWindowsFilePolicy.RequireMissingRegularTarget(entry.TargetPath, entry.ParentPath);
            transaction = _journal.Reserve(
                entry.Descriptor,
                entry.TargetPath,
                entry.ParentPath,
                entry.ParentIdentity,
                temporary,
                outputIdentity,
                length,
                digest);
            _recovered.Add(transaction.DestinationHandle.Value, transaction);
            PhotonCadWindowsFilePolicy.SetDeleteOnClose(output.SafeFileHandle, delete: false);
            PhotonCadWindowsFilePolicy.RenameOwnedHandle(output.SafeFileHandle, entry.TargetPath, entry.ParentPath);
            PhotonCadWindowsFilePolicy.FlushHandle(output.SafeFileHandle);
            PhotonCadWindowsFilePolicy.FlushDirectory(entry.ParentPath);
            var exact = await PhotonCadWindowsFilePolicy.VerifyExactHandleBindingAsync(
                output.SafeFileHandle,
                outputIdentity,
                artifact.ByteLength,
                artifact.ContentDigest,
                CancellationToken.None).ConfigureAwait(false);
            if (exact.Outcome != PhotonCadExactBindingOutcome.Exact)
                throw new PhotonCadArtifactException("destination_identity_changed");
            var receipt = new PhotonCadDestinationCommitReceipt(
                PhotonCadCommitReceiptHandle.New(),
                artifact.ArtifactHandle,
                artifact.ContentDigest,
                artifact.ByteLength,
                entry.Descriptor.DisplayLabel,
                _clock.UtcNow());
            if (!await _journal.RetireAsync(transaction, CancellationToken.None).ConfigureAwait(false))
                throw new PhotonCadArtifactException("destination_receipt_recovery_required");
            _recovered.Remove(transaction.DestinationHandle.Value);
            return receipt;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PhotonCadArtifactException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new PhotonCadArtifactException("destination_commit_failed");
        }
    }

    private static async ValueTask<(string Digest, long Length)> CopyAndHashAsync(
        Stream source,
        FileStream output,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long total = 0;
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                total = checked(total + read);
                if (total > expectedLength) throw new PhotonCadArtifactException("destination_size_exceeded");
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
            return ($"sha256:{Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()}", total);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WindowsPhotonCadArtifactDestinationAuthority));
    }

    private sealed record DestinationEntry(
        PhotonCadDestinationDescriptor Descriptor,
        PhotonCadDeadline Deadline,
        string TargetPath,
        string ParentPath,
        PhotonCadWindowsFileIdentity ParentIdentity);
}
