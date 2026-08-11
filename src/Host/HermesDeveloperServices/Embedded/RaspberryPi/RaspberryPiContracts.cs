namespace HermesDeveloperServices;

public sealed record RaspberryPiProviderOptions(
    string WorkbenchRoot,
    string ToolchainRelativePath,
    string ReceiptFileName,
    string CredentialRoot,
    string IdentityRelativePath,
    string KnownHostsRelativePath)
{
    public const string ToolchainId = "windows-openssh-client";
    public const string SshLogicalName = "ssh";
    public const string ScpLogicalName = "scp";
}

public sealed record RaspberryPiHostKeyTrust(
    string Algorithm,
    string Sha256Fingerprint);

/// <summary>
/// Identifies one explicitly trusted host. Credential material is bound only in host-side provider
/// options and is never accepted through this operation contract or returned in results.
/// </summary>
public sealed record RaspberryPiTrustedHost(
    string Host,
    string User,
    int Port,
    RaspberryPiHostKeyTrust HostKey);

public sealed record RaspberryPiPackageQuery(
    RaspberryPiTrustedHost Target,
    string Query);

public sealed record RaspberryPiDeployRequest(
    RaspberryPiTrustedHost Target,
    string WorkspaceRoot,
    string SourceRelativePath,
    string RemoteAbsolutePath,
    EmbeddedOperationPermission Permission);
