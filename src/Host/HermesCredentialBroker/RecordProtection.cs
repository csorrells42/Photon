using System.Security.Cryptography;

namespace HermesCredentialBroker;

public interface ICredentialRecordProtector
{
    byte[] Protect(byte[] plaintext, byte[] entropy);
    byte[] Unprotect(byte[] protectedBytes, byte[] entropy);
}

public sealed class CurrentUserDpapiRecordProtector : ICredentialRecordProtector
{
    public byte[] Protect(byte[] plaintext, byte[] entropy)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The native credential vault requires Windows DPAPI.");
        }
        return ProtectedData.Protect(plaintext, entropy, DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(byte[] protectedBytes, byte[] entropy)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The native credential vault requires Windows DPAPI.");
        }
        return ProtectedData.Unprotect(protectedBytes, entropy, DataProtectionScope.CurrentUser);
    }
}
