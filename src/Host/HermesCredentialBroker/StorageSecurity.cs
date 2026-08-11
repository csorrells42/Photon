using System.Security.AccessControl;
using System.Security.Principal;

namespace HermesCredentialBroker;

public interface ICredentialStorageSecurity
{
    void PrepareRoot(string rootPath, string expectedUserSid);
    void SecureFile(string rootPath, string filePath, string expectedUserSid);
    void ValidatePath(string rootPath, string path, string expectedUserSid);
}

public sealed class StrictWindowsCredentialStorageSecurity : ICredentialStorageSecurity
{
    public void PrepareRoot(string rootPath, string expectedUserSid)
    {
        EnsureWindows();
        var sid = ValidateCurrentUser(expectedUserSid);
        Directory.CreateDirectory(rootPath);
        RejectReparsePoints(rootPath, rootPath);

        var security = new DirectorySecurity();
        security.SetOwner(sid);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            sid,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(rootPath).SetAccessControl(security);
        ValidateAcl(new DirectoryInfo(rootPath).GetAccessControl(), sid);
    }

    public void SecureFile(string rootPath, string filePath, string expectedUserSid)
    {
        EnsureWindows();
        var sid = ValidateCurrentUser(expectedUserSid);
        ValidateConfinement(rootPath, filePath);
        RejectReparsePoints(rootPath, filePath);

        var security = new FileSecurity();
        security.SetOwner(sid);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(filePath).SetAccessControl(security);
        ValidateAcl(new FileInfo(filePath).GetAccessControl(), sid);
    }

    public void ValidatePath(string rootPath, string path, string expectedUserSid)
    {
        EnsureWindows();
        var sid = ValidateCurrentUser(expectedUserSid);
        ValidateConfinement(rootPath, path);
        RejectReparsePoints(rootPath, path);
        ValidateAcl(new DirectoryInfo(rootPath).GetAccessControl(), sid);
        if (File.Exists(path)) ValidateAcl(new FileInfo(path).GetAccessControl(), sid);
    }

    private static void ValidateConfinement(string rootPath, string path)
    {
        var rootWithoutSeparator = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar);
        var root = rootWithoutSeparator + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(path);
        if (!candidate.Equals(rootWithoutSeparator, StringComparison.OrdinalIgnoreCase)
            && !candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new CredentialBrokerException("storage_path_escape", "The credential storage path escaped its trusted root.");
        }
    }

    private static void RejectReparsePoints(string rootPath, string path)
    {
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar);
        var candidate = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(root, candidate);
        var current = root;
        Check(current);
        if (!relative.Equals(".", StringComparison.Ordinal))
        {
            foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                current = Path.Combine(current, segment);
                if (File.Exists(current) || Directory.Exists(current)) Check(current);
            }
        }

        static void Check(string candidatePath)
        {
            if ((File.GetAttributes(candidatePath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new CredentialBrokerException("storage_reparse_point", "Credential storage cannot use a reparse point.");
            }
        }
    }

    private static SecurityIdentifier ValidateCurrentUser(string expectedUserSid)
    {
        var expected = new SecurityIdentifier(ContractText.Sid(expectedUserSid));
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        if (identity.User is null || !identity.User.Equals(expected))
        {
            throw new CredentialBrokerException("principal_mismatch", "The credential vault principal does not match the current Windows user.");
        }
        return expected;
    }

    private static void ValidateAcl(FileSystemSecurity security, SecurityIdentifier expected)
    {
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !owner.Equals(expected))
        {
            throw new CredentialBrokerException("storage_owner_mismatch", "Credential storage has an unexpected owner.");
        }
        if (!security.AreAccessRulesProtected)
        {
            throw new CredentialBrokerException("storage_acl_inherited", "Credential storage must not inherit access rules.");
        }

        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow
                && rule.IdentityReference is SecurityIdentifier sid
                && !sid.Equals(expected))
            {
                throw new CredentialBrokerException("storage_acl_too_broad", "Credential storage grants access to an unexpected principal.");
            }
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The native credential vault requires Windows.");
    }
}
