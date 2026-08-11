namespace PhotonCadRuntime;

public enum CadRuntimeRole
{
    Geometry,
    Assembly,
}

public sealed class CadBundleIdentity
{
    public CadBundleIdentity(
        CadRuntimeRole role,
        string bundleId,
        string bundleVersion,
        string runtimeProvider,
        string sourceRevision,
        string targetRuntime,
        string manifestSha256,
        DateTimeOffset createdAt)
    {
        Role = ContractGuards.EnumValue(role, nameof(role));
        BundleId = ContractGuards.Identifier(bundleId, nameof(bundleId));
        BundleVersion = ContractGuards.Revision(bundleVersion, nameof(bundleVersion));
        RuntimeProvider = ContractGuards.Identifier(runtimeProvider, nameof(runtimeProvider));
        SourceRevision = ContractGuards.Revision(sourceRevision, nameof(sourceRevision));
        TargetRuntime = ContractGuards.Identifier(targetRuntime, nameof(targetRuntime));
        ManifestSha256 = ContractGuards.Sha256(manifestSha256, nameof(manifestSha256));
        CreatedAt = ContractGuards.Utc(createdAt, nameof(createdAt));
    }

    public int ContractVersion => CadContractVersions.Bundle;
    public string SchemaId => CadContractVersions.BundleManifestV1;
    public CadRuntimeRole Role { get; }
    public string BundleId { get; }
    public string BundleVersion { get; }
    public string RuntimeProvider { get; }
    public string SourceRevision { get; }
    public string TargetRuntime { get; }
    public string ManifestSha256 { get; }
    public DateTimeOffset CreatedAt { get; }
}

public sealed class CadBundleArtifact
{
    public CadBundleArtifact(CadRelativePath path, string sha256, long byteLength)
    {
        Path = path ?? throw new CadContractException("required", nameof(path));
        Sha256 = ContractGuards.Sha256(sha256, nameof(sha256));
        ByteLength = ContractGuards.ByteLength(byteLength, nameof(byteLength));
    }

    public CadRelativePath Path { get; }
    public string Sha256 { get; }
    public long ByteLength { get; }
}

public sealed class CadRuntimeBundleManifest
{
    public CadRuntimeBundleManifest(CadBundleIdentity identity, IEnumerable<CadBundleArtifact> artifacts)
    {
        Identity = identity ?? throw new CadContractException("required", nameof(identity));
        Artifacts = ContractGuards.Copy(
            artifacts,
            nameof(artifacts),
            CadContractLimits.MaximumBundleArtifacts,
            requireAny: true);
        ContractGuards.RequireUnique(
            Artifacts.Select(artifact => artifact.Path.Value),
            nameof(artifacts),
            StringComparer.OrdinalIgnoreCase);

        long total = 0;
        try
        {
            foreach (var artifact in Artifacts)
                total = checked(total + artifact.ByteLength);
        }
        catch (OverflowException)
        {
            throw new CadContractException("bundle_too_large", nameof(artifacts));
        }

        if (total > CadContractLimits.MaximumBundleBytes)
            throw new CadContractException("bundle_too_large", nameof(artifacts));
        TotalByteLength = total;
    }

    public CadBundleIdentity Identity { get; }
    public IReadOnlyList<CadBundleArtifact> Artifacts { get; }
    public long TotalByteLength { get; }
}

public sealed class CadRuntimeBundleSetIdentity
{
    public CadRuntimeBundleSetIdentity(
        CadBundleIdentity geometry,
        CadBundleIdentity assembly,
        CadRevision activationRevision)
    {
        Geometry = geometry ?? throw new CadContractException("required", nameof(geometry));
        Assembly = assembly ?? throw new CadContractException("required", nameof(assembly));
        ActivationRevision = activationRevision ?? throw new CadContractException("required", nameof(activationRevision));
        if (Geometry.Role != CadRuntimeRole.Geometry)
            throw new CadContractException("wrong_bundle_role", nameof(geometry));
        if (Assembly.Role != CadRuntimeRole.Assembly)
            throw new CadContractException("wrong_bundle_role", nameof(assembly));
        if (Geometry.BundleId.Equals(Assembly.BundleId, StringComparison.OrdinalIgnoreCase))
            throw new CadContractException("duplicate_bundle_identity", nameof(assembly));
    }

    public CadBundleIdentity Geometry { get; }
    public CadBundleIdentity Assembly { get; }
    public CadRevision ActivationRevision { get; }
}
