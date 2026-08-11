namespace PhotonCadRuntime;

public static class CadContractVersions
{
    public const int Host = 1;
    public const int Bundle = 1;
    public const int Interchange = 1;
    public const string HostV1 = "photon.cad.host/v1";
    public const string BundleManifestV1 = "photon.cad.bundle/v1";
    public const string StepPackageV1 = "photon.cad.step-package/v1";
}

public enum CadRuntimeAvailability
{
    NotProvisioned,
    Ready,
    Rejected,
}

public enum CadProjectState
{
    Available,
    ReadOnly,
    Closed,
}

public enum CadSessionState
{
    Created,
    Opening,
    Ready,
    Busy,
    Closing,
    Closed,
    Faulted,
}

public sealed record CadRequestId
{
    public CadRequestId(string value) => Value = ContractGuards.Identifier(value, nameof(value));
    public string Value { get; }
    public static CadRequestId New() => new($"req_{Guid.NewGuid():N}");
    public override string ToString() => Value;
}

public sealed record CadWorkspaceHandle
{
    public CadWorkspaceHandle(string value) => Value = ContractGuards.OpaqueHandle(value, nameof(value), "wsp");
    public string Value { get; }
    public static CadWorkspaceHandle New() => new($"wsp_{Guid.NewGuid():N}");
    public override string ToString() => Value;
}

public sealed record CadProjectHandle
{
    public CadProjectHandle(string value) => Value = ContractGuards.OpaqueHandle(value, nameof(value), "prj");
    public string Value { get; }
    public static CadProjectHandle New() => new($"prj_{Guid.NewGuid():N}");
    public override string ToString() => Value;
}

public sealed record CadSessionHandle
{
    public CadSessionHandle(string value) => Value = ContractGuards.OpaqueHandle(value, nameof(value), "ses");
    public string Value { get; }
    public static CadSessionHandle New() => new($"ses_{Guid.NewGuid():N}");
    public override string ToString() => Value;
}

public sealed record CadArtifactHandle
{
    public CadArtifactHandle(string value) => Value = ContractGuards.OpaqueHandle(value, nameof(value), "art");
    public string Value { get; }
    public static CadArtifactHandle New() => new($"art_{Guid.NewGuid():N}");
    public override string ToString() => Value;
}

public sealed record CadPreviewHandle
{
    public CadPreviewHandle(string value) => Value = ContractGuards.OpaqueHandle(value, nameof(value), "prv");
    public string Value { get; }
    public static CadPreviewHandle New() => new($"prv_{Guid.NewGuid():N}");
    public override string ToString() => Value;
}

public sealed record CadPackageHandle
{
    public CadPackageHandle(string value) => Value = ContractGuards.OpaqueHandle(value, nameof(value), "pkg");
    public string Value { get; }
    public static CadPackageHandle New() => new($"pkg_{Guid.NewGuid():N}");
    public override string ToString() => Value;
}

public sealed record CadRevision
{
    public CadRevision(long value)
    {
        if (value < 0 || value > 9_007_199_254_740_991L)
            throw new CadContractException("invalid_revision", nameof(value));
        Value = value;
    }

    public long Value { get; }
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed class CadOpenProjectRequest
{
    public CadOpenProjectRequest(CadRequestId requestId, CadWorkspaceHandle workspace, string displayName)
    {
        RequestId = requestId ?? throw new CadContractException("required", nameof(requestId));
        Workspace = workspace ?? throw new CadContractException("required", nameof(workspace));
        DisplayName = ContractGuards.RequiredText(displayName, nameof(displayName), CadContractLimits.DisplayNameLength);
    }

    public CadRequestId RequestId { get; }
    public CadWorkspaceHandle Workspace { get; }
    public string DisplayName { get; }
}

public sealed class CadProjectDescriptor
{
    public CadProjectDescriptor(CadProjectHandle project, string displayName, CadRevision revision, CadProjectState state)
    {
        Project = project ?? throw new CadContractException("required", nameof(project));
        DisplayName = ContractGuards.RequiredText(displayName, nameof(displayName), CadContractLimits.DisplayNameLength);
        Revision = revision ?? throw new CadContractException("required", nameof(revision));
        State = ContractGuards.EnumValue(state, nameof(state));
    }

    public CadProjectHandle Project { get; }
    public string DisplayName { get; }
    public CadRevision Revision { get; }
    public CadProjectState State { get; }
}

public sealed class CadStartSessionRequest
{
    public CadStartSessionRequest(CadRequestId requestId, CadProjectHandle project, CadRevision expectedRevision)
    {
        RequestId = requestId ?? throw new CadContractException("required", nameof(requestId));
        Project = project ?? throw new CadContractException("required", nameof(project));
        ExpectedRevision = expectedRevision ?? throw new CadContractException("required", nameof(expectedRevision));
    }

    public CadRequestId RequestId { get; }
    public CadProjectHandle Project { get; }
    public CadRevision ExpectedRevision { get; }
}

public sealed class CadSessionDescriptor
{
    public CadSessionDescriptor(CadSessionHandle session, CadProjectHandle project, CadSessionState state, CadRevision revision)
    {
        Session = session ?? throw new CadContractException("required", nameof(session));
        Project = project ?? throw new CadContractException("required", nameof(project));
        State = ContractGuards.EnumValue(state, nameof(state));
        Revision = revision ?? throw new CadContractException("required", nameof(revision));
    }

    public CadSessionHandle Session { get; }
    public CadProjectHandle Project { get; }
    public CadSessionState State { get; }
    public CadRevision Revision { get; }
}
