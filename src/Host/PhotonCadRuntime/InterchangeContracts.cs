namespace PhotonCadRuntime;

public enum CadStepSchema
{
    Ap214,
    Ap242,
}

public enum CadLengthUnit
{
    Millimeter,
    Inch,
}

public enum CadPackageProvenanceKind
{
    Generated,
    Imported,
    Derived,
}

public enum CadPackageFileRole
{
    Manifest,
    PartStep,
    AssemblyStep,
    Bom,
    Drawing,
    Preview,
    ValidationReport,
}

public enum CadAcceptanceStatus
{
    Passed,
    Failed,
    NotRun,
}

public sealed class CadCoordinateSystem
{
    public CadCoordinateSystem(
        string handedness = "right",
        string upAxis = "z",
        string matrixOrder = "row-major",
        string vectorConvention = "column",
        string transformMeaning = "local-to-parent")
    {
        Handedness = RequireExact(handedness, "right", nameof(handedness));
        UpAxis = RequireExact(upAxis, "z", nameof(upAxis));
        MatrixOrder = RequireExact(matrixOrder, "row-major", nameof(matrixOrder));
        VectorConvention = RequireExact(vectorConvention, "column", nameof(vectorConvention));
        TransformMeaning = RequireExact(transformMeaning, "local-to-parent", nameof(transformMeaning));
    }

    public string Handedness { get; }
    public string UpAxis { get; }
    public string MatrixOrder { get; }
    public string VectorConvention { get; }
    public string TransformMeaning { get; }

    public static CadCoordinateSystem Required { get; } = new();

    private static string RequireExact(string value, string expected, string field)
    {
        var normalized = ContractGuards.RequiredText(value, field, 32).ToLowerInvariant();
        if (!normalized.Equals(expected, StringComparison.Ordinal))
            throw new CadContractException("unsupported_coordinate_system", field);
        return normalized;
    }
}

public sealed class CadStepInterchangeProfile
{
    public CadStepInterchangeProfile(
        string id,
        CadStepSchema schema,
        CadLengthUnit lengthUnit,
        bool preserveProductStructure = true,
        CadCoordinateSystem? coordinateSystem = null)
    {
        Id = ContractGuards.Identifier(id, nameof(id));
        Schema = ContractGuards.EnumValue(schema, nameof(schema));
        LengthUnit = ContractGuards.EnumValue(lengthUnit, nameof(lengthUnit));
        PreserveProductStructure = preserveProductStructure;
        CoordinateSystem = coordinateSystem ?? CadCoordinateSystem.Required;
    }

    public CadStepInterchangeProfile(CadStepSchema schema, CadLengthUnit lengthUnit, bool preserveProductStructure = true)
        : this(schema == CadStepSchema.Ap214 ? "step-ap214" : "step-ap242", schema, lengthUnit, preserveProductStructure)
    {
    }

    public string Id { get; }
    public CadStepSchema Schema { get; }
    public CadLengthUnit LengthUnit { get; }
    public bool PreserveProductStructure { get; }
    public CadCoordinateSystem CoordinateSystem { get; }
    public string StepApplicationProtocol => Schema == CadStepSchema.Ap214 ? "AP214" : "AP242";
    public string Geometry => "exact-brep";
}

public sealed class CadSourceIdentity
{
    public CadSourceIdentity(string package, string version, string digest, string license)
    {
        Package = ContractGuards.RequiredText(package, nameof(package), 128);
        Version = ContractGuards.RequiredText(version, nameof(version), 64);
        Digest = ContractGuards.Sha256(digest, nameof(digest));
        License = ContractGuards.RequiredText(license, nameof(license), 64);
    }

    public string Package { get; }
    public string Version { get; }
    public string Digest { get; }
    public string License { get; }
}

public sealed class CadInterchangeProject
{
    public CadInterchangeProject(string projectId, string title, CadRevision revision, CadLengthUnit units)
    {
        ProjectId = ContractGuards.Identifier(projectId, nameof(projectId));
        Title = ContractGuards.RequiredText(title, nameof(title), CadContractLimits.DisplayNameLength);
        Revision = revision ?? throw new CadContractException("required", nameof(revision));
        Units = ContractGuards.EnumValue(units, nameof(units));
    }

    public string ProjectId { get; }
    public string Title { get; }
    public CadRevision Revision { get; }
    public CadLengthUnit Units { get; }
    public string UnitName => Units == CadLengthUnit.Millimeter ? "millimeter" : "inch";
}

public sealed class CadPackageChecksum
{
    public CadPackageChecksum(CadRelativePath path, string sha256, long byteLength)
    {
        Path = path ?? throw new CadContractException("required", nameof(path));
        Sha256 = ContractGuards.Sha256(sha256, nameof(sha256));
        if (byteLength < 0 || byteLength > CadContractLimits.MaximumArtifactBytes)
            throw new CadContractException("invalid_byte_length", nameof(byteLength));
        ByteLength = byteLength;
    }

    public string Algorithm => "sha256";
    public CadRelativePath Path { get; }
    public string Sha256 { get; }
    public long ByteLength { get; }
}

public sealed class CadPackageFile
{
    public CadPackageFile(
        CadPackageFileRole role,
        CadRelativePath relativePath,
        string sha256,
        long byteLength,
        string mediaType)
    {
        RoleKind = ContractGuards.EnumValue(role, nameof(role));
        RelativePath = relativePath ?? throw new CadContractException("required", nameof(relativePath));
        Checksum = new CadPackageChecksum(relativePath, sha256, byteLength);
        MediaType = NormalizeMediaType(mediaType);
        if (role is CadPackageFileRole.PartStep or CadPackageFileRole.AssemblyStep)
        {
            if (!RelativePath.Value.EndsWith(".step", StringComparison.OrdinalIgnoreCase) &&
                !RelativePath.Value.EndsWith(".stp", StringComparison.OrdinalIgnoreCase))
                throw new CadContractException("step_extension_required", nameof(relativePath));
            if (!MediaType.Equals("model/step", StringComparison.Ordinal))
                throw new CadContractException("step_media_type_required", nameof(mediaType));
        }
    }

    public CadPackageFileRole RoleKind { get; }
    public string Role => RoleKind switch
    {
        CadPackageFileRole.Manifest => "manifest",
        CadPackageFileRole.PartStep => "part-step",
        CadPackageFileRole.AssemblyStep => "assembly-step",
        CadPackageFileRole.Bom => "bom",
        CadPackageFileRole.Drawing => "drawing",
        CadPackageFileRole.Preview => "preview",
        CadPackageFileRole.ValidationReport => "validation-report",
        _ => throw new InvalidOperationException("Unsupported package file role."),
    };
    public CadRelativePath RelativePath { get; }
    public string Sha256 => Checksum.Sha256;
    public long ByteLength => Checksum.ByteLength;
    public string MediaType { get; }
    public CadPackageChecksum Checksum { get; }

    private static string NormalizeMediaType(string value)
    {
        var normalized = ContractGuards.RequiredText(value, nameof(value), 128).ToLowerInvariant();
        var separator = normalized.IndexOf('/');
        if (separator <= 0 || separator == normalized.Length - 1 ||
            normalized.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '/' and not '-' and not '+' and not '.' and not '_'))
            throw new CadContractException("invalid_media_type", nameof(value));
        return normalized;
    }
}

public sealed class CadBomRow
{
    public CadBomRow(
        string partNumber,
        string description,
        decimal quantity,
        string unit,
        string sourceEntityId)
    {
        PartNumber = ContractGuards.RequiredText(partNumber, nameof(partNumber), CadContractLimits.DisplayNameLength);
        Description = ContractGuards.RequiredText(description, nameof(description), CadContractLimits.DescriptionLength);
        if (quantity <= 0 || quantity > 1_000_000_000m)
            throw new CadContractException("invalid_quantity", nameof(quantity));
        Quantity = quantity;
        Unit = ContractGuards.Identifier(unit, nameof(unit), 16);
        if (Unit is not "each" and not "length")
            throw new CadContractException("invalid_bom_unit", nameof(unit));
        SourceEntityId = ContractGuards.Identifier(sourceEntityId, nameof(sourceEntityId));
    }

    public string PartNumber { get; }
    public string Description { get; }
    public decimal Quantity { get; }
    public string Unit { get; }
    public string SourceEntityId { get; }
}

public sealed class CadRigidTransform
{
    private const double OrthonormalTolerance = 1e-6;

    public CadRigidTransform(IEnumerable<double> rowMajorValues)
    {
        var values = ContractGuards.Copy(rowMajorValues, nameof(rowMajorValues), 16, requireAny: true);
        if (values.Count != 16)
            throw new CadContractException("invalid_transform_size", nameof(rowMajorValues));

        var copy = values.Select((value, index) => ContractGuards.Finite(value, $"transform_{index}", 1_000_000_000)).ToArray();
        if (!Near(copy[12], 0) || !Near(copy[13], 0) || !Near(copy[14], 0) || !Near(copy[15], 1))
            throw new CadContractException("invalid_affine_transform", nameof(rowMajorValues));

        for (var row = 0; row < 3; row++)
        {
            var lengthSquared = 0d;
            for (var column = 0; column < 3; column++)
                lengthSquared += copy[(row * 4) + column] * copy[(row * 4) + column];
            if (!Near(lengthSquared, 1))
                throw new CadContractException("non_rigid_transform", nameof(rowMajorValues));
        }

        for (var first = 0; first < 3; first++)
        {
            for (var second = first + 1; second < 3; second++)
            {
                var dot = 0d;
                for (var column = 0; column < 3; column++)
                    dot += copy[(first * 4) + column] * copy[(second * 4) + column];
                if (!Near(dot, 0))
                    throw new CadContractException("non_rigid_transform", nameof(rowMajorValues));
            }
        }

        var determinant =
            (copy[0] * ((copy[5] * copy[10]) - (copy[6] * copy[9]))) -
            (copy[1] * ((copy[4] * copy[10]) - (copy[6] * copy[8]))) +
            (copy[2] * ((copy[4] * copy[9]) - (copy[5] * copy[8])));
        if (!Near(determinant, 1))
            throw new CadContractException("non_rigid_transform", nameof(rowMajorValues));

        Values = Array.AsReadOnly(copy);
    }

    public IReadOnlyList<double> Values { get; }

    public static CadRigidTransform Identity { get; } = new(
    [
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    ]);

    private static bool Near(double value, double expected) => Math.Abs(value - expected) <= OrthonormalTolerance;
}

public sealed class CadAssemblyOccurrence
{
    public CadAssemblyOccurrence(
        string occurrenceId,
        string? parentOccurrenceId,
        string partNumber,
        string sourceEntityId,
        CadRigidTransform transform)
    {
        OccurrenceId = ContractGuards.Identifier(occurrenceId, nameof(occurrenceId));
        ParentOccurrenceId = parentOccurrenceId is null
            ? null
            : ContractGuards.Identifier(parentOccurrenceId, nameof(parentOccurrenceId));
        PartNumber = ContractGuards.RequiredText(partNumber, nameof(partNumber), CadContractLimits.DisplayNameLength);
        SourceEntityId = ContractGuards.Identifier(sourceEntityId, nameof(sourceEntityId));
        Transform = transform ?? throw new CadContractException("required", nameof(transform));
        if (OccurrenceId == ParentOccurrenceId)
            throw new CadContractException("occurrence_self_parent", nameof(parentOccurrenceId));
    }

    public string OccurrenceId { get; }
    public string? ParentOccurrenceId { get; }
    public string PartNumber { get; }
    public string SourceEntityId { get; }
    public CadRigidTransform Transform { get; }
}

public sealed class CadPackageProvenance
{
    public CadPackageProvenance(
        string photonVersion,
        CadSourceIdentity geometryRuntime,
        string operationDigest,
        CadSourceIdentity? assemblyRuntime = null,
        CadPackageProvenanceKind kind = CadPackageProvenanceKind.Generated,
        string? sourceRevision = null)
    {
        PhotonVersion = ContractGuards.RequiredText(photonVersion, nameof(photonVersion), 64);
        GeometryRuntime = geometryRuntime ?? throw new CadContractException("required", nameof(geometryRuntime));
        AssemblyRuntime = assemblyRuntime;
        OperationDigest = ContractGuards.Sha256(operationDigest, nameof(operationDigest));
        Kind = ContractGuards.EnumValue(kind, nameof(kind));
        SourceRevision = sourceRevision is null ? null : ContractGuards.Revision(sourceRevision, nameof(sourceRevision));
        if (kind != CadPackageProvenanceKind.Generated && SourceRevision is null)
            throw new CadContractException("source_revision_required", nameof(sourceRevision));
    }

    public string PhotonVersion { get; }
    public CadSourceIdentity GeometryRuntime { get; }
    public CadSourceIdentity? AssemblyRuntime { get; }
    public string OperationDigest { get; }
    public CadPackageProvenanceKind Kind { get; }
    public string? SourceRevision { get; }
}

public sealed class CadPackageValidation
{
    public CadPackageValidation(
        CadValidationStatus status,
        IEnumerable<string> checks,
        IEnumerable<CadValidationFinding> issues)
    {
        if (status is not CadValidationStatus.Passed and not CadValidationStatus.Failed)
            throw new CadContractException("final_validation_status_required", nameof(status));
        StatusKind = ContractGuards.EnumValue(status, nameof(status));
        Checks = ContractGuards.Copy(
            checks?.Select(check => ContractGuards.Identifier(check, nameof(checks), 96)),
            nameof(checks),
            CadContractLimits.MaximumValidationResults,
            requireAny: true);
        ContractGuards.RequireUnique(Checks, nameof(checks));
        Issues = ContractGuards.Copy(issues, nameof(issues), CadContractLimits.MaximumValidationFindings);
        if (status == CadValidationStatus.Passed && Issues.Any(issue => issue.Severity == CadValidationSeverity.Error))
            throw new CadContractException("status_finding_mismatch", nameof(status));
        if (status == CadValidationStatus.Failed && Issues.All(issue => issue.Severity != CadValidationSeverity.Error))
            throw new CadContractException("status_finding_mismatch", nameof(status));
    }

    public CadValidationStatus StatusKind { get; }
    public string Status => StatusKind == CadValidationStatus.Passed ? "passed" : "failed";
    public IReadOnlyList<string> Checks { get; }
    public IReadOnlyList<CadValidationFinding> Issues { get; }
}

public sealed class CadAcceptanceResult
{
    public CadAcceptanceResult(
        string system,
        string profile,
        CadAcceptanceStatus status,
        DateTimeOffset? observedAtUtc = null,
        string? reportDigest = null)
    {
        System = ContractGuards.RequiredText(system, nameof(system), 128);
        Profile = ContractGuards.RequiredText(profile, nameof(profile), 128);
        StatusKind = ContractGuards.EnumValue(status, nameof(status));
        ObservedAtUtc = observedAtUtc is null ? null : ContractGuards.Utc(observedAtUtc.Value, nameof(observedAtUtc));
        ReportDigest = reportDigest is null ? null : ContractGuards.Sha256(reportDigest, nameof(reportDigest));
        if (status == CadAcceptanceStatus.NotRun && (ObservedAtUtc is not null || ReportDigest is not null))
            throw new CadContractException("not_run_evidence_rejected", nameof(status));
        if (status != CadAcceptanceStatus.NotRun && ObservedAtUtc is null)
            throw new CadContractException("acceptance_timestamp_required", nameof(observedAtUtc));
    }

    public string System { get; }
    public string Profile { get; }
    public CadAcceptanceStatus StatusKind { get; }
    public string Status => StatusKind switch
    {
        CadAcceptanceStatus.Passed => "passed",
        CadAcceptanceStatus.Failed => "failed",
        CadAcceptanceStatus.NotRun => "not-run",
        _ => throw new InvalidOperationException("Unsupported acceptance status."),
    };
    public DateTimeOffset? ObservedAtUtc { get; }
    public string? ReportDigest { get; }
}

public sealed class CadStepPackageManifest
{
    public CadStepPackageManifest(
        string packageId,
        string packageFingerprint,
        CadInterchangeProject project,
        CadStepInterchangeProfile profile,
        DateTimeOffset createdAtUtc,
        CadPackageProvenance provenance,
        IEnumerable<CadPackageFile> files,
        IEnumerable<CadBomRow> bom,
        IEnumerable<CadAssemblyOccurrence> occurrences,
        CadPackageValidation validation,
        IEnumerable<CadAcceptanceResult>? acceptance = null)
    {
        PackageId = ContractGuards.Identifier(packageId, nameof(packageId));
        PackageFingerprint = ContractGuards.Sha256(packageFingerprint, nameof(packageFingerprint));
        Project = project ?? throw new CadContractException("required", nameof(project));
        Profile = profile ?? throw new CadContractException("required", nameof(profile));
        CreatedAtUtc = ContractGuards.Utc(createdAtUtc, nameof(createdAtUtc));
        Provenance = provenance ?? throw new CadContractException("required", nameof(provenance));
        Files = ContractGuards.Copy(files, nameof(files), CadContractLimits.MaximumChecksums, requireAny: true);
        Bom = ContractGuards.Copy(bom, nameof(bom), CadContractLimits.MaximumBomItems);
        Occurrences = ContractGuards.Copy(
            occurrences,
            nameof(occurrences),
            CadContractLimits.MaximumOccurrences);
        Validation = validation ?? throw new CadContractException("required", nameof(validation));
        Acceptance = ContractGuards.Copy(acceptance ?? [], nameof(acceptance), 256);

        ContractGuards.RequireUnique(
            Files.Select(file => file.RelativePath.Value),
            nameof(files),
            StringComparer.OrdinalIgnoreCase);
        if (Files.All(file => file.RoleKind is not CadPackageFileRole.PartStep and not CadPackageFileRole.AssemblyStep))
            throw new CadContractException("step_file_required", nameof(files));
        if (Project.Units != Profile.LengthUnit)
            throw new CadContractException("profile_unit_mismatch", nameof(profile));
        if (Occurrences.Count > 1 && !Profile.PreserveProductStructure)
            throw new CadContractException("product_structure_required", nameof(profile));
        ValidateBom();
        ValidateOccurrences();
        ContractGuards.RequireUnique(
            Acceptance.Select(item => $"{item.System}\n{item.Profile}"),
            nameof(acceptance),
            StringComparer.OrdinalIgnoreCase);
    }

    public int SchemaVersion => CadContractVersions.Interchange;
    public string SchemaId => CadContractVersions.StepPackageV1;
    public string PackageId { get; }
    public string PackageFingerprint { get; }
    public CadInterchangeProject Project { get; }
    public CadStepInterchangeProfile Profile { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public CadPackageProvenance Provenance { get; }
    public IReadOnlyList<CadPackageFile> Files { get; }
    public IReadOnlyList<CadBomRow> Bom { get; }
    public IReadOnlyList<CadAssemblyOccurrence> Occurrences { get; }
    public CadPackageValidation Validation { get; }
    public IReadOnlyList<CadAcceptanceResult> Acceptance { get; }

    private void ValidateBom()
    {
        var entities = Occurrences.Select(occurrence => occurrence.SourceEntityId).ToHashSet(StringComparer.Ordinal);
        if (Bom.Any(row => !entities.Contains(row.SourceEntityId)))
            throw new CadContractException("bom_entity_missing", nameof(Bom));
    }

    private void ValidateOccurrences()
    {
        ContractGuards.RequireUnique(Occurrences.Select(occurrence => occurrence.OccurrenceId), nameof(Occurrences));
        if (Occurrences.Count == 0)
        {
            if (Bom.Count != 0)
                throw new CadContractException("bom_without_occurrence", nameof(Bom));
            return;
        }

        var byId = Occurrences.ToDictionary(occurrence => occurrence.OccurrenceId, StringComparer.Ordinal);
        if (Occurrences.Count(occurrence => occurrence.ParentOccurrenceId is null) != 1)
            throw new CadContractException("single_occurrence_root_required", nameof(Occurrences));

        foreach (var occurrence in Occurrences)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var cursor = occurrence;
            while (cursor.ParentOccurrenceId is not null)
            {
                if (!visited.Add(cursor.OccurrenceId) || !byId.TryGetValue(cursor.ParentOccurrenceId, out cursor))
                    throw new CadContractException("invalid_occurrence_graph", nameof(Occurrences));
            }
        }
    }
}

public sealed class CadInterchangePackageDescriptor
{
    public CadInterchangePackageDescriptor(
        CadPackageHandle package,
        CadArtifactHandle archive,
        CadStepPackageManifest manifest,
        string manifestSha256)
    {
        Package = package ?? throw new CadContractException("required", nameof(package));
        Archive = archive ?? throw new CadContractException("required", nameof(archive));
        Manifest = manifest ?? throw new CadContractException("required", nameof(manifest));
        ManifestSha256 = ContractGuards.Sha256(manifestSha256, nameof(manifestSha256));
    }

    public CadPackageHandle Package { get; }
    public CadArtifactHandle Archive { get; }
    public CadStepPackageManifest Manifest { get; }
    public string ManifestSha256 { get; }
}
