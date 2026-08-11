using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PhotonCadRuntime;

public sealed class CadWindowsPathInspector : ICadPathInspector
{
    public CadPathInspection Inspect(string absolutePath)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The Photon CAD Windows path inspector requires Windows.");
        if (string.IsNullOrWhiteSpace(absolutePath) || !Path.IsPathFullyQualified(absolutePath))
            throw new CadContractException("absolute_local_path_required", nameof(absolutePath));
        try
        {
            var attributes = File.GetAttributes(absolutePath);
            var isReparse = (attributes & FileAttributes.ReparsePoint) != 0;
            if ((attributes & FileAttributes.Directory) != 0)
                return new CadPathInspection(CadPathEntryKind.Directory, isReparse);

            using var stream = new FileStream(
                absolutePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4_096,
                FileOptions.None);
            if (!GetFileInformationByHandle(stream.SafeFileHandle, out var information))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return new CadPathInspection(CadPathEntryKind.File, isReparse, information.NumberOfLinks > 1);
        }
        catch (FileNotFoundException)
        {
            return new CadPathInspection(CadPathEntryKind.Missing, false);
        }
        catch (DirectoryNotFoundException)
        {
            return new CadPathInspection(CadPathEntryKind.Missing, false);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

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
