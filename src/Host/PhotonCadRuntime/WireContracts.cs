using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotonCadRuntime;

public static class CadWireJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 32,
    };

    public static byte[] SerializeDescription(CadRuntimeDescription description) =>
        JsonSerializer.SerializeToUtf8Bytes(CadWireProjection.Description(description), Options);

    public static byte[] SerializeCatalog(CadCapabilityCatalog catalog) =>
        JsonSerializer.SerializeToUtf8Bytes(CadWireProjection.Catalog(catalog), Options);

    public static byte[] SerializePreview(CadPreviewDescriptor preview) =>
        JsonSerializer.SerializeToUtf8Bytes(CadWireProjection.Preview(preview), Options);

    public static byte[] SerializeProjectSnapshot(CadProjectSnapshot snapshot) =>
        JsonSerializer.SerializeToUtf8Bytes(CadWireProjection.ProjectSnapshot(snapshot), Options);

    public static byte[] SerializeOperationRequest(CadOperationRequest request) =>
        JsonSerializer.SerializeToUtf8Bytes(CadWireProjection.OperationRequest(request), Options);

    public static byte[] SerializeOperationResult(CadOperationResult result) =>
        JsonSerializer.SerializeToUtf8Bytes(CadWireProjection.OperationResult(result), Options);

    public static byte[] SerializeArtifactDescriptor(CadArtifactDescriptor descriptor) =>
        JsonSerializer.SerializeToUtf8Bytes(CadWireProjection.ArtifactDescriptor(descriptor), Options);

    public static byte[] SerializeVerificationRequest(CadVerificationRequest request) =>
        JsonSerializer.SerializeToUtf8Bytes(CadWireProjection.VerificationRequest(request), Options);

    public static byte[] SerializeVerificationResult(CadVerificationResult result) =>
        JsonSerializer.SerializeToUtf8Bytes(CadWireProjection.VerificationResult(result), Options);

    public static byte[] SerializeInterchange(CadStepPackageManifest manifest) =>
        JsonSerializer.SerializeToUtf8Bytes(CadWireProjection.Interchange(manifest), Options);
}

public static class CadWireProjection
{
    public static CadWireArtifactDescriptor ArtifactDescriptor(CadArtifactDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return new CadWireArtifactDescriptor(
            descriptor.ContractVersion,
            descriptor.Session.Value,
            descriptor.Artifact.Value,
            descriptor.ContentDigest,
            descriptor.ByteLength,
            descriptor.MediaType);
    }

    public static CadWireRuntimeDescription Description(CadRuntimeDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);
        return new CadWireRuntimeDescription(
            description.ContractVersion,
            description.Status,
            description.Reason,
            description.GeometryBundleId,
            description.AssemblyBundleId,
            description.Catalog is null ? null : Catalog(description.Catalog));
    }

    public static CadWireCatalog Catalog(CadCapabilityCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return new CadWireCatalog(
            catalog.ContractVersion,
            catalog.CatalogRevision,
            Utc(catalog.GeneratedAtUtc),
            catalog.Capabilities.Select(Capability).ToArray(),
            new CadWireCatalogCoverage(
                catalog.Coverage.Discovered,
                catalog.Coverage.Available,
                catalog.Coverage.Unavailable,
                catalog.Coverage.UnavailableReasons.ToArray()));
    }

    public static CadWireInterchangeManifest Interchange(CadStepPackageManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return new CadWireInterchangeManifest(
            manifest.SchemaVersion,
            manifest.PackageId,
            manifest.PackageFingerprint,
            new CadWireInterchangeProject(
                manifest.Project.ProjectId,
                manifest.Project.Title,
                manifest.Project.Revision.Value,
                Unit(manifest.Project.Units)),
            new CadWireInterchangeProfile(
                manifest.Profile.Id,
                manifest.Profile.StepApplicationProtocol,
                manifest.Profile.Geometry,
                new CadWireCoordinateSystem(
                    manifest.Profile.CoordinateSystem.Handedness,
                    manifest.Profile.CoordinateSystem.UpAxis,
                    manifest.Profile.CoordinateSystem.MatrixOrder,
                    manifest.Profile.CoordinateSystem.VectorConvention,
                    manifest.Profile.CoordinateSystem.TransformMeaning)),
            Utc(manifest.CreatedAtUtc),
            new CadWireProvenance(
                manifest.Provenance.PhotonVersion,
                Source(manifest.Provenance.GeometryRuntime),
                manifest.Provenance.AssemblyRuntime is null ? null : Source(manifest.Provenance.AssemblyRuntime),
                manifest.Provenance.OperationDigest),
            manifest.Files.Select(file => new CadWirePackageFile(
                file.Role,
                file.RelativePath.Value,
                file.Sha256,
                file.ByteLength,
                file.MediaType)).ToArray(),
            manifest.Bom.Select(row => new CadWireBomRow(
                row.PartNumber,
                row.Description,
                row.Quantity,
                row.Unit,
                row.SourceEntityId)).ToArray(),
            manifest.Occurrences.Select(occurrence => new CadWireOccurrence(
                occurrence.OccurrenceId,
                occurrence.ParentOccurrenceId,
                occurrence.PartNumber,
                occurrence.SourceEntityId,
                occurrence.Transform.Values.ToArray())).ToArray(),
            new CadWirePackageValidation(
                manifest.Validation.Status,
                manifest.Validation.Checks.ToArray(),
                manifest.Validation.Issues.Select(Issue).ToArray()),
            manifest.Acceptance.Select(item => new CadWireAcceptance(
                item.System,
                item.Profile,
                item.Status,
                item.ObservedAtUtc is null ? null : Utc(item.ObservedAtUtc.Value),
                item.ReportDigest)).ToArray());
    }

    public static CadWirePreviewReceipt Preview(CadPreviewDescriptor preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return new CadWirePreviewReceipt(
            preview.Preview.Value,
            preview.Project.Value,
            preview.Revision.Value,
            preview.ContentDigest,
            Unit(preview.Units),
            new CadWireBounds(
                new CadWireVector3(preview.Bounds.Minimum.X, preview.Bounds.Minimum.Y, preview.Bounds.Minimum.Z),
                new CadWireVector3(preview.Bounds.Maximum.X, preview.Bounds.Maximum.Y, preview.Bounds.Maximum.Z)),
            preview.EntityCount);
    }

    public static CadWireProjectSnapshot ProjectSnapshot(CadProjectSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new CadWireProjectSnapshot(
            snapshot.ContractVersion,
            snapshot.Session.Value,
            snapshot.Project.Value,
            snapshot.Revision.Value,
            snapshot.Title,
            Unit(snapshot.Units),
            snapshot.ModeValue == CadProjectMode.Canonical ? "canonical" : "scratch",
            snapshot.Entities.Select(entity => new CadWireProjectEntity(
                entity.Id,
                entity.ParentId,
                EntityKind(entity.KindValue),
                entity.Name,
                entity.Visible,
                entity.Suppressed,
                entity.SourceCapabilityId)).ToArray(),
            snapshot.Operations.Select(operation => new CadWireProjectOperation(
                operation.Id,
                operation.CapabilityId,
                operation.Label,
                Utc(operation.CreatedAtUtc),
                operation.StateValue switch
                {
                    CadOperationRecordState.Proposed => "proposed",
                    CadOperationRecordState.Applied => "applied",
                    CadOperationRecordState.Rejected => "rejected",
                    _ => throw new InvalidOperationException("Unsupported operation record state."),
                })).ToArray(),
            snapshot.Issues.Select(Issue).ToArray(),
            snapshot.Dirty);
    }

    public static CadWireOperationRequest OperationRequest(CadOperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new CadWireOperationRequest(
            request.ContractVersion,
            request.RequestId.Value,
            request.Session.Value,
            request.Project.Value,
            request.BaseRevision.Value,
            request.Mode == CadOperationMode.Suggest ? "suggest" : "scratch",
            request.CapabilityId,
            request.Inputs.ToDictionary(pair => pair.Key, pair => Input(pair.Value), StringComparer.Ordinal),
            request.TargetEntityIds.ToArray());
    }

    public static CadWireOperationResult OperationResult(CadOperationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Status == CadOperationStatus.Accepted && result.Snapshot is null && result.Preview is null)
            throw new CadContractException("renderer_evidence_required", nameof(result));
        return new CadWireOperationResult(
            result.ContractVersion,
            result.RequestId.Value,
            result.Project.Value,
            result.BaseRevision.Value,
            result.ResultingRevision.Value,
            result.Status switch
            {
                CadOperationStatus.Accepted => "accepted",
                CadOperationStatus.Rejected => "rejected",
                CadOperationStatus.Unavailable => "unavailable",
                _ => throw new InvalidOperationException("Unsupported operation status."),
            },
            result.Stale,
            result.Reason,
            result.Snapshot is null ? null : ProjectSnapshot(result.Snapshot),
            result.Preview is null ? null : Preview(result.Preview),
            result.Issues.Select(Issue).ToArray());
    }

    public static CadWireVerificationRequest VerificationRequest(CadVerificationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new CadWireVerificationRequest(
            request.ContractVersion,
            request.RequestId.Value,
            request.Session.Value,
            request.Project.Value,
            request.Revision.Value,
            request.Checks.Select(VerificationCheck).ToArray());
    }

    public static CadWireVerificationResult VerificationResult(CadVerificationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new CadWireVerificationResult(
            result.ContractVersion,
            result.RequestId.Value,
            result.Project.Value,
            result.Revision.Value,
            result.Status switch
            {
                CadVerificationStatus.Passed => "passed",
                CadVerificationStatus.Failed => "failed",
                CadVerificationStatus.Unavailable => "unavailable",
                _ => throw new InvalidOperationException("Unsupported verification status."),
            },
            result.Stale,
            result.Issues.Select(Issue).ToArray(),
            Utc(result.MeasuredAtUtc));
    }

    private static CadWireCapability Capability(CadCapability capability) => new(
        capability.Id,
        capability.Backend == CadBackend.Geometry ? "geometry" : "assembly",
        capability.Category,
        capability.Title,
        capability.Description,
        capability.Operation switch
        {
            CadCapabilityOperationKind.Create => "create",
            CadCapabilityOperationKind.Modify => "modify",
            CadCapabilityOperationKind.Measure => "measure",
            CadCapabilityOperationKind.Validate => "validate",
            CadCapabilityOperationKind.Assemble => "assemble",
            CadCapabilityOperationKind.Drawing => "drawing",
            _ => throw new InvalidOperationException("Unsupported capability operation."),
        },
        capability.Parameters.Select(Parameter).ToArray(),
        Source(capability.Source),
        capability.PreviewSupported,
        capability.Experimental);

    private static CadWireParameter Parameter(CadParameterDefinition parameter) => new(
        parameter.Id,
        parameter.Label,
        parameter.Description,
        parameter.Kind switch
        {
            CadParameterKind.Number => "number",
            CadParameterKind.Integer => "integer",
            CadParameterKind.Boolean => "boolean",
            CadParameterKind.Text => "text",
            CadParameterKind.Choice => "choice",
            CadParameterKind.Vector3 => "vector3",
            CadParameterKind.Entity => "entity",
            CadParameterKind.EntityList => "entity-list",
            _ => throw new InvalidOperationException("Unsupported parameter kind."),
        },
        parameter.Required,
        parameter.Unit switch
        {
            CadParameterUnit.Length => "length",
            CadParameterUnit.Angle => "angle",
            CadParameterUnit.Ratio => "ratio",
            CadParameterUnit.Count => "count",
            null => null,
            _ => throw new InvalidOperationException("Unsupported parameter unit."),
        },
        parameter.Minimum,
        parameter.Maximum,
        parameter.Step,
        Input(parameter.DefaultValue),
        parameter.Choices.Select(choice => new CadWireChoice(choice.Value, choice.Label)).ToArray());

    private static object? Input(CadInputValue? value) => value switch
    {
        null => null,
        CadNullInputValue => null,
        CadNumberInputValue number => number.Value,
        CadIntegerInputValue integer => integer.Value,
        CadBooleanInputValue boolean => boolean.Value,
        CadTextInputValue text => text.Value,
        CadVectorInputValue vector => new CadWireVector3(vector.Value.X, vector.Value.Y, vector.Value.Z),
        CadEntityListInputValue entities => entities.EntityIds.ToArray(),
        _ => throw new InvalidOperationException("Unsupported input value."),
    };

    private static CadWireSourceIdentity Source(CadSourceIdentity source) =>
        new(source.Package, source.Version, source.Digest, source.License);

    private static CadWireIssue Issue(CadValidationFinding issue) =>
        new(issue.Code, issue.SeverityName, issue.Message, issue.EntityIds.ToArray());

    private static string EntityKind(CadEntityKind kind) => kind switch
    {
        CadEntityKind.Body => "body",
        CadEntityKind.Part => "part",
        CadEntityKind.Assembly => "assembly",
        CadEntityKind.Occurrence => "occurrence",
        CadEntityKind.Drawing => "drawing",
        CadEntityKind.Datum => "datum",
        _ => throw new InvalidOperationException("Unsupported entity kind."),
    };

    private static string VerificationCheck(CadVerificationCheck check) => check switch
    {
        CadVerificationCheck.ValidSolids => "valid-solids",
        CadVerificationCheck.Interference => "interference",
        CadVerificationCheck.Dimensions => "dimensions",
        CadVerificationCheck.AssemblyStructure => "assembly-structure",
        CadVerificationCheck.ExportReadiness => "export-readiness",
        _ => throw new InvalidOperationException("Unsupported verification check."),
    };

    private static string Unit(CadLengthUnit unit) => unit == CadLengthUnit.Millimeter ? "millimeter" : "inch";

    private static string Utc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record CadWireSourceIdentity(
    [property: JsonPropertyName("package")] string Package,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("digest")] string Digest,
    [property: JsonPropertyName("license")] string License);

public sealed record CadWireVector3(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("z")] double Z);

public sealed record CadWireChoice(
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("label")] string Label);

public sealed record CadWireParameter(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("required")] bool Required,
    [property: JsonPropertyName("unit")] string? Unit,
    [property: JsonPropertyName("minimum")] double? Minimum,
    [property: JsonPropertyName("maximum")] double? Maximum,
    [property: JsonPropertyName("step")] double? Step,
    [property: JsonPropertyName("defaultValue")] object? DefaultValue,
    [property: JsonPropertyName("choices")] IReadOnlyList<CadWireChoice>? Choices);

public sealed record CadWireCapability(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("backend")] string Backend,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("parameters")] IReadOnlyList<CadWireParameter> Parameters,
    [property: JsonPropertyName("source")] CadWireSourceIdentity Source,
    [property: JsonPropertyName("previewSupported")] bool PreviewSupported,
    [property: JsonPropertyName("experimental")] bool Experimental);

public sealed record CadWireCatalogCoverage(
    [property: JsonPropertyName("discovered")] int Discovered,
    [property: JsonPropertyName("available")] int Available,
    [property: JsonPropertyName("unavailable")] int Unavailable,
    [property: JsonPropertyName("unavailableReasons")] IReadOnlyList<string> UnavailableReasons);

public sealed record CadWireCatalog(
    [property: JsonPropertyName("contractVersion")] int ContractVersion,
    [property: JsonPropertyName("catalogRevision")] string CatalogRevision,
    [property: JsonPropertyName("generatedAtUtc")] string GeneratedAtUtc,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<CadWireCapability> Capabilities,
    [property: JsonPropertyName("coverage")] CadWireCatalogCoverage Coverage);

public sealed record CadWireRuntimeDescription(
    [property: JsonPropertyName("contractVersion")] int ContractVersion,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("geometryBundleId")] string? GeometryBundleId,
    [property: JsonPropertyName("assemblyBundleId")] string? AssemblyBundleId,
    [property: JsonPropertyName("catalog")] CadWireCatalog? Catalog);

public sealed record CadWireBounds(
    [property: JsonPropertyName("minimum")] CadWireVector3 Minimum,
    [property: JsonPropertyName("maximum")] CadWireVector3 Maximum);

public sealed record CadWirePreviewReceipt(
    [property: JsonPropertyName("previewId")] string PreviewId,
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("contentDigest")] string ContentDigest,
    [property: JsonPropertyName("units")] string Units,
    [property: JsonPropertyName("bounds")] CadWireBounds Bounds,
    [property: JsonPropertyName("entityCount")] int EntityCount);

public sealed record CadWireProjectEntity(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("parentId"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ParentId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("visible")] bool Visible,
    [property: JsonPropertyName("suppressed")] bool Suppressed,
    [property: JsonPropertyName("sourceCapabilityId")] string? SourceCapabilityId);

public sealed record CadWireProjectOperation(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("capabilityId")] string CapabilityId,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("createdAtUtc")] string CreatedAtUtc,
    [property: JsonPropertyName("state")] string State);

public sealed record CadWireProjectSnapshot(
    [property: JsonPropertyName("contractVersion")] int ContractVersion,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("units")] string Units,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("entities")] IReadOnlyList<CadWireProjectEntity> Entities,
    [property: JsonPropertyName("operations")] IReadOnlyList<CadWireProjectOperation> Operations,
    [property: JsonPropertyName("issues")] IReadOnlyList<CadWireIssue> Issues,
    [property: JsonPropertyName("dirty")] bool Dirty);

public sealed record CadWireOperationRequest(
    [property: JsonPropertyName("contractVersion")] int ContractVersion,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("baseRevision")] long BaseRevision,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("capabilityId")] string CapabilityId,
    [property: JsonPropertyName("inputs")] IReadOnlyDictionary<string, object?> Inputs,
    [property: JsonPropertyName("targetEntityIds")] IReadOnlyList<string> TargetEntityIds);

public sealed record CadWireOperationResult(
    [property: JsonPropertyName("contractVersion")] int ContractVersion,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("baseRevision")] long BaseRevision,
    [property: JsonPropertyName("resultingRevision")] long ResultingRevision,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("stale")] bool Stale,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("snapshot")] CadWireProjectSnapshot? Snapshot,
    [property: JsonPropertyName("preview")] CadWirePreviewReceipt? Preview,
    [property: JsonPropertyName("issues")] IReadOnlyList<CadWireIssue> Issues);

public sealed record CadWireArtifactDescriptor(
    [property: JsonPropertyName("contractVersion")] int ContractVersion,
    [property: JsonPropertyName("sessionHandle")] string SessionHandle,
    [property: JsonPropertyName("artifactHandle")] string ArtifactHandle,
    [property: JsonPropertyName("contentDigest")] string ContentDigest,
    [property: JsonPropertyName("byteLength")] long ByteLength,
    [property: JsonPropertyName("mediaType")] string MediaType);

public sealed record CadWireVerificationRequest(
    [property: JsonPropertyName("contractVersion")] int ContractVersion,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("checks")] IReadOnlyList<string> Checks);

public sealed record CadWireVerificationResult(
    [property: JsonPropertyName("contractVersion")] int ContractVersion,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("stale")] bool Stale,
    [property: JsonPropertyName("issues")] IReadOnlyList<CadWireIssue> Issues,
    [property: JsonPropertyName("measuredAtUtc")] string MeasuredAtUtc);

public sealed record CadWireInterchangeProject(
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("units")] string Units);

public sealed record CadWireCoordinateSystem(
    [property: JsonPropertyName("handedness")] string Handedness,
    [property: JsonPropertyName("upAxis")] string UpAxis,
    [property: JsonPropertyName("matrixOrder")] string MatrixOrder,
    [property: JsonPropertyName("vectorConvention")] string VectorConvention,
    [property: JsonPropertyName("transformMeaning")] string TransformMeaning);

public sealed record CadWireInterchangeProfile(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("stepApplicationProtocol")] string StepApplicationProtocol,
    [property: JsonPropertyName("geometry")] string Geometry,
    [property: JsonPropertyName("coordinateSystem")] CadWireCoordinateSystem CoordinateSystem);

public sealed record CadWireProvenance(
    [property: JsonPropertyName("photonVersion")] string PhotonVersion,
    [property: JsonPropertyName("geometryRuntime")] CadWireSourceIdentity GeometryRuntime,
    [property: JsonPropertyName("assemblyRuntime")] CadWireSourceIdentity? AssemblyRuntime,
    [property: JsonPropertyName("operationDigest")] string OperationDigest);

public sealed record CadWirePackageFile(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("relativePath")] string RelativePath,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("byteLength")] long ByteLength,
    [property: JsonPropertyName("mediaType")] string MediaType);

public sealed record CadWireBomRow(
    [property: JsonPropertyName("partNumber")] string PartNumber,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("quantity")] decimal Quantity,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("sourceEntityId")] string SourceEntityId);

public sealed record CadWireOccurrence(
    [property: JsonPropertyName("occurrenceId")] string OccurrenceId,
    [property: JsonPropertyName("parentOccurrenceId"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ParentOccurrenceId,
    [property: JsonPropertyName("partNumber")] string PartNumber,
    [property: JsonPropertyName("sourceEntityId")] string SourceEntityId,
    [property: JsonPropertyName("transform")] IReadOnlyList<double> Transform);

public sealed record CadWireIssue(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("entityIds")] IReadOnlyList<string> EntityIds);

public sealed record CadWirePackageValidation(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("checks")] IReadOnlyList<string> Checks,
    [property: JsonPropertyName("issues")] IReadOnlyList<CadWireIssue> Issues);

public sealed record CadWireAcceptance(
    [property: JsonPropertyName("system")] string System,
    [property: JsonPropertyName("profile")] string Profile,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("observedAtUtc")] string? ObservedAtUtc,
    [property: JsonPropertyName("reportDigest")] string? ReportDigest);

public sealed record CadWireInterchangeManifest(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("packageId")] string PackageId,
    [property: JsonPropertyName("packageFingerprint")] string PackageFingerprint,
    [property: JsonPropertyName("project")] CadWireInterchangeProject Project,
    [property: JsonPropertyName("profile")] CadWireInterchangeProfile Profile,
    [property: JsonPropertyName("createdAtUtc")] string CreatedAtUtc,
    [property: JsonPropertyName("provenance")] CadWireProvenance Provenance,
    [property: JsonPropertyName("files")] IReadOnlyList<CadWirePackageFile> Files,
    [property: JsonPropertyName("bom")] IReadOnlyList<CadWireBomRow> Bom,
    [property: JsonPropertyName("occurrences")] IReadOnlyList<CadWireOccurrence> Occurrences,
    [property: JsonPropertyName("validation")] CadWirePackageValidation Validation,
    [property: JsonPropertyName("acceptance")] IReadOnlyList<CadWireAcceptance> Acceptance);
