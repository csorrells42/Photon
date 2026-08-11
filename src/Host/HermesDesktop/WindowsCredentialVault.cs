using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security;
using System.Text;

namespace HermesDesktop;

internal sealed record CredentialMetadata(
    string Provider,
    string CredentialId,
    DateTimeOffset? UpdatedAt);

internal static class NativeCredentialProtocol
{
    internal const int Version = 1;
    internal const string TargetPrefix = "HermesWorkbench:v1:";

    internal static bool IsValidProvider(string? value) =>
        IsValidIdentifier(value, 64);

    internal static bool IsValidCredentialId(string? value) =>
        IsValidIdentifier(value, 128);

    internal static string BuildTargetName(string provider, string credentialId)
    {
        if (!IsValidProvider(provider)) throw new ArgumentException("Invalid provider identifier.", nameof(provider));
        if (!IsValidCredentialId(credentialId)) throw new ArgumentException("Invalid credential identifier.", nameof(credentialId));
        return $"{TargetPrefix}{provider}:{credentialId}";
    }

    internal static bool TryParseTargetName(string? targetName, out string provider, out string credentialId)
    {
        provider = string.Empty;
        credentialId = string.Empty;
        if (targetName is null || !targetName.StartsWith(TargetPrefix, StringComparison.Ordinal)) return false;

        var remainder = targetName[TargetPrefix.Length..];
        var separator = remainder.IndexOf(':');
        if (separator <= 0 || separator == remainder.Length - 1 || remainder.IndexOf(':', separator + 1) >= 0) return false;

        provider = remainder[..separator];
        credentialId = remainder[(separator + 1)..];
        return IsValidProvider(provider) && IsValidCredentialId(credentialId);
    }

    private static bool IsValidIdentifier(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength) return false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            var allowed = character is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '-' or '_' or '.';
            if (!allowed) return false;
        }
        return true;
    }
}

internal sealed class WindowsCredentialVault
{
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int MaximumCredentialBlobBytes = 2560;

    internal IReadOnlyList<CredentialMetadata> List()
    {
        if (!CredEnumerateW($"{NativeCredentialProtocol.TargetPrefix}*", 0, out var count, out var credentials))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return Array.Empty<CredentialMetadata>();
            throw new Win32Exception(error, "Windows Credential Manager could not enumerate Hermes credentials.");
        }

        try
        {
            var result = new List<CredentialMetadata>(checked((int)count));
            for (var index = 0; index < count; index++)
            {
                var credentialPointer = Marshal.ReadIntPtr(credentials, checked((int)index * IntPtr.Size));
                var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
                if (!NativeCredentialProtocol.TryParseTargetName(credential.TargetName, out var provider, out var credentialId)) continue;
                result.Add(new CredentialMetadata(provider, credentialId, ConvertLastWritten(credential.LastWritten)));
            }
            return result
                .OrderBy(item => item.Provider, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.CredentialId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally
        {
            CredFree(credentials);
        }
    }

    internal CredentialMetadata Save(string provider, string credentialId, SecureString secret)
    {
        var targetName = NativeCredentialProtocol.BuildTargetName(provider, credentialId);
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.Length == 0) throw new ArgumentException("Credential secret cannot be empty.", nameof(secret));
        var blobSize = checked(secret.Length * sizeof(char));
        if (blobSize > MaximumCredentialBlobBytes)
            throw new ArgumentException($"Credential secret exceeds {MaximumCredentialBlobBytes / sizeof(char)} characters.", nameof(secret));

        var secretPointer = Marshal.SecureStringToGlobalAllocUnicode(secret);
        try
        {
            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = targetName,
                Comment = "Managed by Hermes Workbench",
                CredentialBlobSize = checked((uint)blobSize),
                CredentialBlob = secretPointer,
                Persist = CredentialPersistLocalMachine,
                UserName = provider,
            };

            if (!CredWriteW(ref credential, 0))
            {
                var error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error, "Windows Credential Manager could not save the provider credential.");
            }
        }
        finally
        {
            Marshal.ZeroFreeGlobalAllocUnicode(secretPointer);
        }

        return new CredentialMetadata(provider, credentialId, DateTimeOffset.UtcNow);
    }

    internal bool Delete(string provider, string credentialId)
    {
        var targetName = NativeCredentialProtocol.BuildTargetName(provider, credentialId);
        if (CredDeleteW(targetName, CredentialTypeGeneric, 0)) return true;

        var error = Marshal.GetLastWin32Error();
        if (error == ErrorNotFound) return false;
        throw new Win32Exception(error, "Windows Credential Manager could not delete the provider credential.");
    }

    // This is intentionally native-only. Renderer messages can list metadata but can never read a secret.
    internal string? ReadSecret(string provider, string credentialId)
    {
        var targetName = NativeCredentialProtocol.BuildTargetName(provider, credentialId);
        if (!CredReadW(targetName, CredentialTypeGeneric, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return null;
            throw new Win32Exception(error, "Windows Credential Manager could not read the provider credential.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return string.Empty;
            var secretBytes = new byte[checked((int)credential.CredentialBlobSize)];
            try
            {
                Marshal.Copy(credential.CredentialBlob, secretBytes, 0, secretBytes.Length);
                return Encoding.Unicode.GetString(secretBytes);
            }
            finally
            {
                Array.Clear(secretBytes);
            }
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    private static DateTimeOffset? ConvertLastWritten(FILETIME value)
    {
        var fileTime = ((long)value.dwHighDateTime << 32) | (uint)value.dwLowDateTime;
        if (fileTime <= 0) return null;
        try { return DateTimeOffset.FromFileTime(fileTime); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWriteW(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(string target, uint type, uint reservedFlag, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDeleteW(string target, uint type, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredEnumerateW(string filter, uint flags, out uint count, out IntPtr credentials);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
