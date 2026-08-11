using System.Globalization;

namespace PhotonCadProjects.Codec;

internal sealed record PhotonCadBlobV1(PhotonCadArtifactKindV1 Kind, string Digest, ReadOnlyMemory<byte> Content)
{
    internal long ByteLength => Content.Length;
}

internal sealed record PhotonCadManifestEncodingV1(byte[] Bytes, IReadOnlyList<PhotonCadBlobV1> Blobs);

internal static class PhotonCadManifestValuesV1
{
    internal static string Unit(PhotonCadProjectUnit value) => value switch
    {
        PhotonCadProjectUnit.Millimeter => "millimeter",
        PhotonCadProjectUnit.Inch => "inch",
        _ => throw PhotonCadFileGuardsV1.Failure("unsupported_enum", nameof(value)),
    };

    internal static PhotonCadProjectUnit Unit(string? value) => value switch
    {
        "millimeter" => PhotonCadProjectUnit.Millimeter,
        "inch" => PhotonCadProjectUnit.Inch,
        _ => throw PhotonCadFileGuardsV1.Failure("unsupported_unit", nameof(value)),
    };

    internal static string BomUnit(PhotonCadBomUnit value) => value switch
    {
        PhotonCadBomUnit.Each => "each",
        PhotonCadBomUnit.Length => "length",
        _ => throw PhotonCadFileGuardsV1.Failure("unsupported_enum", nameof(value)),
    };

    internal static PhotonCadBomUnit BomUnit(string? value) => value switch
    {
        "each" => PhotonCadBomUnit.Each,
        "length" => PhotonCadBomUnit.Length,
        _ => throw PhotonCadFileGuardsV1.Failure("unsupported_bom_unit", nameof(value)),
    };

    internal static string EntityKind(PhotonCadEntityKindV1 value) => value switch
    {
        PhotonCadEntityKindV1.Body => "body",
        PhotonCadEntityKindV1.Part => "part",
        PhotonCadEntityKindV1.Assembly => "assembly",
        PhotonCadEntityKindV1.Occurrence => "occurrence",
        PhotonCadEntityKindV1.Drawing => "drawing",
        PhotonCadEntityKindV1.Datum => "datum",
        _ => throw PhotonCadFileGuardsV1.Failure("unsupported_enum", nameof(value)),
    };

    internal static PhotonCadEntityKindV1 EntityKind(string? value) => value switch
    {
        "body" => PhotonCadEntityKindV1.Body,
        "part" => PhotonCadEntityKindV1.Part,
        "assembly" => PhotonCadEntityKindV1.Assembly,
        "occurrence" => PhotonCadEntityKindV1.Occurrence,
        "drawing" => PhotonCadEntityKindV1.Drawing,
        "datum" => PhotonCadEntityKindV1.Datum,
        _ => throw PhotonCadFileGuardsV1.Failure("unsupported_entity_kind", nameof(value)),
    };

    internal static string OperationState(PhotonCadOperationStateV1 value) => value switch
    {
        PhotonCadOperationStateV1.Proposed => "proposed",
        PhotonCadOperationStateV1.Applied => "applied",
        PhotonCadOperationStateV1.Rejected => "rejected",
        _ => throw PhotonCadFileGuardsV1.Failure("unsupported_enum", nameof(value)),
    };

    internal static PhotonCadOperationStateV1 OperationState(string? value) => value switch
    {
        "proposed" => PhotonCadOperationStateV1.Proposed,
        "applied" => PhotonCadOperationStateV1.Applied,
        "rejected" => PhotonCadOperationStateV1.Rejected,
        _ => throw PhotonCadFileGuardsV1.Failure("unsupported_operation_state", nameof(value)),
    };

    internal static string OperationMode(PhotonCadOperationModeV1 value) => value switch
    {
        PhotonCadOperationModeV1.Suggest => "suggest",
        PhotonCadOperationModeV1.Scratch => "scratch",
        _ => throw PhotonCadFileGuardsV1.Failure("unsupported_enum", nameof(value)),
    };

    internal static PhotonCadOperationModeV1 OperationMode(string? value) => value switch
    {
        "suggest" => PhotonCadOperationModeV1.Suggest,
        "scratch" => PhotonCadOperationModeV1.Scratch,
        _ => throw PhotonCadFileGuardsV1.Failure("unsupported_operation_mode", nameof(value)),
    };

    internal static string InputKind(PhotonCadInputKindV1 value) => value switch
    {
        PhotonCadInputKindV1.Number => "number",
        PhotonCadInputKindV1.Integer => "integer",
        PhotonCadInputKindV1.Boolean => "boolean",
        PhotonCadInputKindV1.Text => "text",
        PhotonCadInputKindV1.Choice => "choice",
        PhotonCadInputKindV1.Vector3 => "vector3",
        PhotonCadInputKindV1.Entity => "entity",
        PhotonCadInputKindV1.EntityList => "entity-list",
        _ => throw PhotonCadFileGuardsV1.Failure("unsupported_enum", nameof(value)),
    };

    internal static PhotonCadInputKindV1 InputKind(string? value) => value switch
    {
        "number" => PhotonCadInputKindV1.Number,
        "integer" => PhotonCadInputKindV1.Integer,
        "boolean" => PhotonCadInputKindV1.Boolean,
        "text" => PhotonCadInputKindV1.Text,
        "choice" => PhotonCadInputKindV1.Choice,
        "vector3" => PhotonCadInputKindV1.Vector3,
        "entity" => PhotonCadInputKindV1.Entity,
        "entity-list" => PhotonCadInputKindV1.EntityList,
        _ => throw PhotonCadFileGuardsV1.Failure("unsupported_input_kind", nameof(value)),
    };

    internal static string Severity(PhotonCadIssueSeverityV1 value) => value switch
    {
        PhotonCadIssueSeverityV1.Information => "info",
        PhotonCadIssueSeverityV1.Warning => "warning",
        PhotonCadIssueSeverityV1.Error => "error",
        _ => throw PhotonCadFileGuardsV1.Failure("unsupported_enum", nameof(value)),
    };

    internal static PhotonCadIssueSeverityV1 Severity(string? value) => value switch
    {
        "info" => PhotonCadIssueSeverityV1.Information,
        "warning" => PhotonCadIssueSeverityV1.Warning,
        "error" => PhotonCadIssueSeverityV1.Error,
        _ => throw PhotonCadFileGuardsV1.Failure("unsupported_issue_severity", nameof(value)),
    };

    internal static string ArtifactRole(PhotonCadArtifactRoleV1 value) => value switch
    {
        PhotonCadArtifactRoleV1.AuthoritativeGeometry => "authoritative-geometry",
        PhotonCadArtifactRoleV1.ProjectPreview => "project-preview",
        _ => throw PhotonCadFileGuardsV1.Failure("unsupported_enum", nameof(value)),
    };

    internal static PhotonCadArtifactRoleV1 ArtifactRole(string? value) => value switch
    {
        "authoritative-geometry" => PhotonCadArtifactRoleV1.AuthoritativeGeometry,
        "project-preview" => PhotonCadArtifactRoleV1.ProjectPreview,
        _ => throw PhotonCadFileGuardsV1.Failure("unsupported_artifact_role", nameof(value)),
    };

    internal static string ArtifactKind(PhotonCadArtifactKindV1 value) => value switch
    {
        PhotonCadArtifactKindV1.Step => "step",
        PhotonCadArtifactKindV1.Glb => "glb",
        _ => throw PhotonCadFileGuardsV1.Failure("unsupported_enum", nameof(value)),
    };

    internal static PhotonCadArtifactKindV1 ArtifactKind(string? value) => value switch
    {
        "step" => PhotonCadArtifactKindV1.Step,
        "glb" => PhotonCadArtifactKindV1.Glb,
        _ => throw PhotonCadFileGuardsV1.Failure("unsupported_artifact_kind", nameof(value)),
    };

    internal static string MediaType(PhotonCadArtifactKindV1 value) => value switch
    {
        PhotonCadArtifactKindV1.Step => "model/step",
        PhotonCadArtifactKindV1.Glb => "model/gltf-binary",
        _ => throw PhotonCadFileGuardsV1.Failure("unsupported_enum", nameof(value)),
    };

    internal static string Backend(PhotonCadBackendV1 value) => value switch
    {
        PhotonCadBackendV1.Geometry => "geometry",
        PhotonCadBackendV1.Assembly => "assembly",
        _ => throw PhotonCadFileGuardsV1.Failure("unsupported_enum", nameof(value)),
    };

    internal static PhotonCadBackendV1 Backend(string? value) => value switch
    {
        "geometry" => PhotonCadBackendV1.Geometry,
        "assembly" => PhotonCadBackendV1.Assembly,
        _ => throw PhotonCadFileGuardsV1.Failure("unsupported_backend", nameof(value)),
    };

    internal static string Timestamp(DateTimeOffset value) =>
        PhotonCadFileGuardsV1.Utc(value, nameof(value)).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    internal static DateTimeOffset Timestamp(string? value, string field)
    {
        if (value is null || !DateTimeOffset.TryParseExact(
                value,
                "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var result))
            throw PhotonCadFileGuardsV1.Failure("invalid_timestamp", field);
        return PhotonCadFileGuardsV1.Utc(result, field);
    }
}
