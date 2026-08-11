namespace PhotonCadRuntime;

public enum CadOperationKind
{
    EvaluatePart,
    Measure,
    Preview,
    Verify,
    ExportStep,
    Assemble,
}

public enum CadMeasurementKind
{
    BoundingBox,
    SurfaceArea,
    Volume,
    CenterOfMass,
    MinimumClearance,
}

public enum CadPreviewFormat
{
    Glb,
    Png,
    Svg,
}

public enum CadValidationSeverity
{
    Information,
    Warning,
    Error,
}

public enum CadValidationStatus
{
    Pending,
    Passed,
    PassedWithWarnings,
    Failed,
}

public enum CadVerificationCheck
{
    ValidSolids,
    Interference,
    Dimensions,
    AssemblyStructure,
    ExportReadiness,
}

public enum CadVerificationStatus
{
    Passed,
    Failed,
    Unavailable,
}

public enum CadOperationStatus
{
    Accepted,
    Rejected,
    Unavailable,
}

public sealed class CadPreviewOptions
{
    public CadPreviewOptions(CadPreviewFormat format, int width, int height, string? background = null)
    {
        Format = ContractGuards.EnumValue(format, nameof(format));
        Width = ContractGuards.Range(width, 64, 8_192, nameof(width));
        Height = ContractGuards.Range(height, 64, 8_192, nameof(height));
        Background = NormalizeColor(background);
    }

    public CadPreviewFormat Format { get; }
    public int Width { get; }
    public int Height { get; }
    public string? Background { get; }

    private static string? NormalizeColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = ContractGuards.RequiredText(value, nameof(value), 9).ToLowerInvariant();
        if (normalized.Length is not 7 and not 9 || normalized[0] != '#' ||
            normalized.AsSpan(1).ContainsAnyExcept("0123456789abcdef"))
            throw new CadContractException("invalid_color", nameof(value));
        return normalized;
    }
}

public sealed class CadPreviewDescriptor
{
    public CadPreviewDescriptor(
        CadPreviewHandle preview,
        CadArtifactHandle artifact,
        CadProjectHandle project,
        CadRevision revision,
        CadPreviewFormat format,
        string sha256,
        long byteLength,
        CadLengthUnit units,
        CadBounds bounds,
        int entityCount,
        int? width = null,
        int? height = null)
    {
        Preview = preview ?? throw new CadContractException("required", nameof(preview));
        Artifact = artifact ?? throw new CadContractException("required", nameof(artifact));
        Project = project ?? throw new CadContractException("required", nameof(project));
        Revision = revision ?? throw new CadContractException("required", nameof(revision));
        Format = ContractGuards.EnumValue(format, nameof(format));
        if ((width is null) != (height is null))
            throw new CadContractException("preview_dimensions_incomplete", nameof(width));
        if (format != CadPreviewFormat.Glb && width is null)
            throw new CadContractException("preview_dimensions_required", nameof(width));
        Width = width is null ? null : ContractGuards.Range(width.Value, 1, 8_192, nameof(width));
        Height = height is null ? null : ContractGuards.Range(height.Value, 1, 8_192, nameof(height));
        Sha256 = ContractGuards.Sha256(sha256, nameof(sha256));
        ByteLength = ContractGuards.ByteLength(byteLength, nameof(byteLength));
        if (ByteLength > CadContractLimits.MaximumPreviewBytes)
            throw new CadContractException("preview_too_large", nameof(byteLength));
        Units = ContractGuards.EnumValue(units, nameof(units));
        Bounds = bounds ?? throw new CadContractException("required", nameof(bounds));
        EntityCount = ContractGuards.Range(entityCount, 0, 10_000, nameof(entityCount));
    }

    public CadPreviewHandle Preview { get; }
    public CadArtifactHandle Artifact { get; }
    public CadProjectHandle Project { get; }
    public CadRevision Revision { get; }
    public CadPreviewFormat Format { get; }
    public int? Width { get; }
    public int? Height { get; }
    public string MediaType => Format switch
    {
        CadPreviewFormat.Glb => "model/gltf-binary",
        CadPreviewFormat.Png => "image/png",
        CadPreviewFormat.Svg => "image/svg+xml",
        _ => throw new InvalidOperationException("Unsupported preview format."),
    };
    public string Sha256 { get; }
    public string ContentDigest => Sha256;
    public long ByteLength { get; }
    public CadLengthUnit Units { get; }
    public CadBounds Bounds { get; }
    public int EntityCount { get; }
}

public sealed class CadValidationFinding
{
    public CadValidationFinding(
        CadValidationSeverity severity,
        string code,
        string message,
        IEnumerable<string>? entityIds = null)
    {
        Severity = ContractGuards.EnumValue(severity, nameof(severity));
        Code = ContractGuards.Identifier(code, nameof(code), 96);
        Message = ContractGuards.RequiredText(message, nameof(message), CadContractLimits.MessageLength);
        EntityIds = ContractGuards.Copy(
            (entityIds ?? []).Select(entityId => ContractGuards.Identifier(entityId, nameof(entityIds))),
            nameof(entityIds),
            CadContractLimits.MaximumPackageComponents);
        ContractGuards.RequireUnique(EntityIds, nameof(entityIds));
    }

    public CadValidationSeverity Severity { get; }
    public string SeverityName => Severity switch
    {
        CadValidationSeverity.Information => "info",
        CadValidationSeverity.Warning => "warning",
        CadValidationSeverity.Error => "error",
        _ => throw new InvalidOperationException("Unsupported validation severity."),
    };
    public string Code { get; }
    public string Message { get; }
    public IReadOnlyList<string> EntityIds { get; }
}

public sealed class CadValidationMetric
{
    public CadValidationMetric(string code, decimal value, string unit)
    {
        Code = ContractGuards.Identifier(code, nameof(code), 96);
        Value = value;
        Unit = ContractGuards.Identifier(unit, nameof(unit), 64);
    }

    public string Code { get; }
    public decimal Value { get; }
    public string Unit { get; }
}

public sealed class CadValidationResult
{
    public CadValidationResult(
        string validatorId,
        string validatorVersion,
        CadValidationStatus status,
        DateTimeOffset completedAt,
        IEnumerable<CadValidationFinding> findings,
        IEnumerable<CadValidationMetric>? metrics = null)
    {
        ValidatorId = ContractGuards.Identifier(validatorId, nameof(validatorId));
        ValidatorVersion = ContractGuards.Revision(validatorVersion, nameof(validatorVersion));
        Status = ContractGuards.EnumValue(status, nameof(status));
        CompletedAt = ContractGuards.Utc(completedAt, nameof(completedAt));
        Findings = ContractGuards.Copy(findings, nameof(findings), CadContractLimits.MaximumValidationFindings);
        Metrics = ContractGuards.Copy(metrics ?? [], nameof(metrics), CadContractLimits.MaximumValidationFindings);
        ContractGuards.RequireUnique(Metrics.Select(metric => metric.Code), nameof(metrics));

        if (Status == CadValidationStatus.Passed && Findings.Any(finding => finding.Severity != CadValidationSeverity.Information))
            throw new CadContractException("status_finding_mismatch", nameof(status));
        if (Status == CadValidationStatus.PassedWithWarnings && Findings.All(finding => finding.Severity != CadValidationSeverity.Warning))
            throw new CadContractException("status_finding_mismatch", nameof(status));
        if (Status == CadValidationStatus.Failed && Findings.All(finding => finding.Severity != CadValidationSeverity.Error))
            throw new CadContractException("status_finding_mismatch", nameof(status));
    }

    public string ValidatorId { get; }
    public string ValidatorVersion { get; }
    public CadValidationStatus Status { get; }
    public DateTimeOffset CompletedAt { get; }
    public IReadOnlyList<CadValidationFinding> Findings { get; }
    public IReadOnlyList<CadValidationMetric> Metrics { get; }
}

public sealed class CadVerificationProfile
{
    public CadVerificationProfile(string id, bool requireClosedSolids, bool requireManifoldGeometry, int maximumFindings = 256)
    {
        Id = ContractGuards.Identifier(id, nameof(id));
        RequireClosedSolids = requireClosedSolids;
        RequireManifoldGeometry = requireManifoldGeometry;
        MaximumFindings = ContractGuards.Range(maximumFindings, 1, CadContractLimits.MaximumValidationFindings, nameof(maximumFindings));
    }

    public string Id { get; }
    public bool RequireClosedSolids { get; }
    public bool RequireManifoldGeometry { get; }
    public int MaximumFindings { get; }
}

public sealed class CadVerificationReport
{
    public CadVerificationReport(CadArtifactHandle artifact, IEnumerable<CadValidationResult> results)
    {
        Artifact = artifact ?? throw new CadContractException("required", nameof(artifact));
        Results = ContractGuards.Copy(results, nameof(results), CadContractLimits.MaximumValidationResults, requireAny: true);
        ContractGuards.RequireUnique(Results.Select(result => result.ValidatorId), nameof(results));
        Status = Results.Any(result => result.Status == CadValidationStatus.Failed)
            ? CadValidationStatus.Failed
            : Results.Any(result => result.Status == CadValidationStatus.Pending)
                ? CadValidationStatus.Pending
                : Results.Any(result => result.Status == CadValidationStatus.PassedWithWarnings)
                    ? CadValidationStatus.PassedWithWarnings
                    : CadValidationStatus.Passed;
    }

    public CadArtifactHandle Artifact { get; }
    public IReadOnlyList<CadValidationResult> Results { get; }
    public CadValidationStatus Status { get; }
}

public sealed class CadVerificationRequest
{
    public CadVerificationRequest(
        CadRequestId requestId,
        CadSessionHandle session,
        CadProjectHandle project,
        CadRevision revision,
        IEnumerable<CadVerificationCheck> checks)
    {
        RequestId = requestId ?? throw new CadContractException("required", nameof(requestId));
        Session = session ?? throw new CadContractException("required", nameof(session));
        Project = project ?? throw new CadContractException("required", nameof(project));
        Revision = revision ?? throw new CadContractException("required", nameof(revision));
        Checks = ContractGuards.Copy(
            checks?.Select(check => ContractGuards.EnumValue(check, nameof(checks))),
            nameof(checks),
            5,
            requireAny: true);
        if (Checks.Distinct().Count() != Checks.Count)
            throw new CadContractException("duplicate_item", nameof(checks));
    }

    public int ContractVersion => CadContractVersions.Host;
    public CadRequestId RequestId { get; }
    public CadSessionHandle Session { get; }
    public CadProjectHandle Project { get; }
    public CadRevision Revision { get; }
    public IReadOnlyList<CadVerificationCheck> Checks { get; }
}

public sealed class CadVerificationResult
{
    public CadVerificationResult(
        CadRequestId requestId,
        CadProjectHandle project,
        CadRevision revision,
        CadVerificationStatus status,
        bool stale,
        IEnumerable<CadValidationFinding> issues,
        DateTimeOffset measuredAtUtc)
    {
        RequestId = requestId ?? throw new CadContractException("required", nameof(requestId));
        Project = project ?? throw new CadContractException("required", nameof(project));
        Revision = revision ?? throw new CadContractException("required", nameof(revision));
        Status = ContractGuards.EnumValue(status, nameof(status));
        Stale = stale;
        Issues = ContractGuards.Copy(issues, nameof(issues), CadContractLimits.MaximumValidationFindings);
        MeasuredAtUtc = ContractGuards.Utc(measuredAtUtc, nameof(measuredAtUtc));
        if (status == CadVerificationStatus.Passed && Issues.Any(issue => issue.Severity == CadValidationSeverity.Error))
            throw new CadContractException("status_finding_mismatch", nameof(status));
        if (status == CadVerificationStatus.Failed && Issues.All(issue => issue.Severity != CadValidationSeverity.Error))
            throw new CadContractException("status_finding_mismatch", nameof(status));
    }

    public int ContractVersion => CadContractVersions.Host;
    public CadRequestId RequestId { get; }
    public CadProjectHandle Project { get; }
    public CadRevision Revision { get; }
    public CadVerificationStatus Status { get; }
    public bool Stale { get; }
    public IReadOnlyList<CadValidationFinding> Issues { get; }
    public DateTimeOffset MeasuredAtUtc { get; }
}

public abstract class CadOperation
{
    protected CadOperation(CadOperationKind kind) => Kind = ContractGuards.EnumValue(kind, nameof(kind));
    public CadOperationKind Kind { get; }
}

public sealed class CadEvaluatePartOperation : CadOperation
{
    public CadEvaluatePartOperation(CadArtifactHandle definition, CadArtifactHandle destination)
        : base(CadOperationKind.EvaluatePart)
    {
        Definition = definition ?? throw new CadContractException("required", nameof(definition));
        Destination = destination ?? throw new CadContractException("required", nameof(destination));
        if (Definition == Destination)
            throw new CadContractException("in_place_write_rejected", nameof(destination));
    }

    public CadArtifactHandle Definition { get; }
    public CadArtifactHandle Destination { get; }
}

public sealed class CadMeasureOperation : CadOperation
{
    public CadMeasureOperation(CadArtifactHandle artifact, IEnumerable<CadMeasurementKind> measurements)
        : base(CadOperationKind.Measure)
    {
        Artifact = artifact ?? throw new CadContractException("required", nameof(artifact));
        Measurements = ContractGuards.Copy(
            measurements?.Select(measurement => ContractGuards.EnumValue(measurement, nameof(measurements))),
            nameof(measurements),
            16,
            requireAny: true);
        if (Measurements.Distinct().Count() != Measurements.Count)
            throw new CadContractException("duplicate_item", nameof(measurements));
    }

    public CadArtifactHandle Artifact { get; }
    public IReadOnlyList<CadMeasurementKind> Measurements { get; }
}

public sealed class CadPreviewOperation : CadOperation
{
    public CadPreviewOperation(CadArtifactHandle artifact, CadPreviewOptions options)
        : base(CadOperationKind.Preview)
    {
        Artifact = artifact ?? throw new CadContractException("required", nameof(artifact));
        Options = options ?? throw new CadContractException("required", nameof(options));
    }

    public CadArtifactHandle Artifact { get; }
    public CadPreviewOptions Options { get; }
}

public sealed class CadVerifyOperation : CadOperation
{
    public CadVerifyOperation(CadArtifactHandle artifact, CadVerificationProfile profile)
        : base(CadOperationKind.Verify)
    {
        Artifact = artifact ?? throw new CadContractException("required", nameof(artifact));
        Profile = profile ?? throw new CadContractException("required", nameof(profile));
    }

    public CadArtifactHandle Artifact { get; }
    public CadVerificationProfile Profile { get; }
}

public sealed class CadExportStepOperation : CadOperation
{
    public CadExportStepOperation(CadArtifactHandle artifact, CadArtifactHandle destination, CadStepInterchangeProfile profile)
        : base(CadOperationKind.ExportStep)
    {
        Artifact = artifact ?? throw new CadContractException("required", nameof(artifact));
        Destination = destination ?? throw new CadContractException("required", nameof(destination));
        Profile = profile ?? throw new CadContractException("required", nameof(profile));
        if (Artifact == Destination)
            throw new CadContractException("in_place_write_rejected", nameof(destination));
    }

    public CadArtifactHandle Artifact { get; }
    public CadArtifactHandle Destination { get; }
    public CadStepInterchangeProfile Profile { get; }
}

public sealed class CadAssembleOperation : CadOperation
{
    public CadAssembleOperation(CadPackageHandle package, CadArtifactHandle destination)
        : base(CadOperationKind.Assemble)
    {
        Package = package ?? throw new CadContractException("required", nameof(package));
        Destination = destination ?? throw new CadContractException("required", nameof(destination));
    }

    public CadPackageHandle Package { get; }
    public CadArtifactHandle Destination { get; }
}

public sealed class CadTypedOperationRequest
{
    public CadTypedOperationRequest(CadRequestId requestId, CadSessionHandle session, CadRevision expectedRevision, CadOperation operation)
    {
        RequestId = requestId ?? throw new CadContractException("required", nameof(requestId));
        Session = session ?? throw new CadContractException("required", nameof(session));
        ExpectedRevision = expectedRevision ?? throw new CadContractException("required", nameof(expectedRevision));
        Operation = operation ?? throw new CadContractException("required", nameof(operation));
    }

    public CadRequestId RequestId { get; }
    public CadSessionHandle Session { get; }
    public CadRevision ExpectedRevision { get; }
    public CadOperation Operation { get; }
}

public sealed class CadOperationResult
{
    public CadOperationResult(
        CadRequestId requestId,
        CadProjectHandle project,
        CadRevision baseRevision,
        CadRevision resultingRevision,
        CadOperationStatus status,
        bool stale,
        string reason,
        IEnumerable<CadValidationFinding>? issues = null,
        IEnumerable<CadArtifactHandle>? artifacts = null,
        CadProjectSnapshot? snapshot = null,
        CadPreviewDescriptor? preview = null,
        CadVerificationReport? verification = null,
        CadInterchangePackageDescriptor? interchangePackage = null)
    {
        RequestId = requestId ?? throw new CadContractException("required", nameof(requestId));
        Project = project ?? throw new CadContractException("required", nameof(project));
        BaseRevision = baseRevision ?? throw new CadContractException("required", nameof(baseRevision));
        ResultingRevision = resultingRevision ?? throw new CadContractException("required", nameof(resultingRevision));
        Status = ContractGuards.EnumValue(status, nameof(status));
        Stale = stale;
        Reason = ContractGuards.Identifier(reason, nameof(reason), 96);
        Issues = ContractGuards.Copy(issues ?? [], nameof(issues), CadContractLimits.MaximumValidationFindings);
        Artifacts = ContractGuards.Copy(artifacts ?? [], nameof(artifacts), CadContractLimits.MaximumArtifactsPerOperation);
        ContractGuards.RequireUnique(Artifacts.Select(artifact => artifact.Value), nameof(artifacts));
        Snapshot = snapshot;
        Preview = preview;
        Verification = verification;
        InterchangePackage = interchangePackage;
        if (Status == CadOperationStatus.Accepted && (Stale || ResultingRevision.Value < BaseRevision.Value))
            throw new CadContractException("invalid_accepted_result", nameof(status));
        if (Snapshot is not null && (Snapshot.Project != Project || Snapshot.Revision != ResultingRevision))
            throw new CadContractException("snapshot_result_mismatch", nameof(snapshot));
        if (Preview is not null && (Preview.Project != Project || Preview.Revision != ResultingRevision))
            throw new CadContractException("preview_result_mismatch", nameof(preview));
        if (Status != CadOperationStatus.Accepted &&
            (ResultingRevision != BaseRevision || Snapshot is not null || Preview is not null))
            throw new CadContractException("invalid_nonaccepted_result", nameof(status));
    }

    public int ContractVersion => CadContractVersions.Host;
    public CadRequestId RequestId { get; }
    public CadProjectHandle Project { get; }
    public CadRevision BaseRevision { get; }
    public CadRevision ResultingRevision { get; }
    public CadOperationStatus Status { get; }
    public bool Stale { get; }
    public string Reason { get; }
    public IReadOnlyList<CadValidationFinding> Issues { get; }
    public IReadOnlyList<CadArtifactHandle> Artifacts { get; }
    public CadProjectSnapshot? Snapshot { get; }
    public CadPreviewDescriptor? Preview { get; }
    public CadVerificationReport? Verification { get; }
    public CadInterchangePackageDescriptor? InterchangePackage { get; }
}
