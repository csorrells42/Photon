using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PhotonCadRuntime.IndustrialProvider;

internal static class ArtifactReader
{
    internal static async ValueTask<byte[]> ReadSealedAsync(
        string ownedOutputDirectory,
        string fileName,
        IndustrialArtifactClaim claim,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (fileName is not ("model.step" or "preview.glb")) throw Failure("artifact_name_rejected");
        var root = Path.GetFullPath(ownedOutputDirectory);
        if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw Failure("artifact_root_rejected");
        var path = Path.GetFullPath(Path.Combine(root, fileName));
        if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(path), root))
            throw Failure("artifact_path_rejected");
        var info = new FileInfo(path);
        if (!info.Exists || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw Failure("artifact_file_rejected");

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        RequireSingleLink(stream.SafeFileHandle);
        if (stream.Length != claim.ByteLength || stream.Length <= 0 || stream.Length > maximumBytes || stream.Length > int.MaxValue)
            throw Failure("artifact_length_mismatch");
        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (stream.ReadByte() != -1) throw Failure("artifact_identity_changed");
        if (!ProtocolV1.FixedDigestEquals(ProtocolV1.Sha256(bytes), claim.ContentDigest))
            throw Failure("artifact_digest_mismatch");
        if (claim.Format == "step") ValidateStep(bytes);
        return bytes;
    }

    internal static void ValidateStep(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 32 || bytes.IndexOf((byte)0) >= 0) throw Failure("step_payload_invalid");
        var header = "ISO-10303-21;"u8;
        var trailer = "END-ISO-10303-21;"u8;
        if (!bytes.StartsWith(header)) throw Failure("step_header_invalid");
        var trimmed = bytes;
        while (!trimmed.IsEmpty && trimmed[^1] is (byte)'\r' or (byte)'\n' or (byte)' ' or (byte)'\t') trimmed = trimmed[..^1];
        if (!trimmed.EndsWith(trailer)) throw Failure("step_trailer_invalid");
    }

    private static void RequireSingleLink(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("industrial artifact custody requires Windows file identity");
        var data = new FileStandardInfo();
        if (!GetFileInformationByHandleEx(handle, 1, ref data, (uint)Marshal.SizeOf<FileStandardInfo>()))
            throw new IOException("artifact_file_identity_unavailable", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        if (data.Directory || data.NumberOfLinks != 1 || data.DeletePending) throw Failure("artifact_link_identity_rejected");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInfo
    {
        internal long AllocationSize;
        internal long EndOfFile;
        internal uint NumberOfLinks;
        [MarshalAs(UnmanagedType.U1)] internal bool DeletePending;
        [MarshalAs(UnmanagedType.U1)] internal bool Directory;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle hFile,
        int fileInformationClass,
        ref FileStandardInfo fileInformation,
        uint bufferSize);

    private static InvalidDataException Failure(string code) => new(code);
}
