using System.Text.Encodings.Web;
using System.Text.Json;

namespace PhotonCadProjects.Codec;

internal static class PhotonCadManifestWriterV1
{
    internal static PhotonCadManifestEncodingV1 Encode(PhotonCadProjectStateV1 state)
    {
        ArgumentNullException.ThrowIfNull(state);
        PhotonCadProjectSemanticValidatorV1.Validate(state);
        var blobs = BuildBlobs(state.Artifacts);
        var blobIndexes = blobs.Select((blob, index) => (blob.Digest, index))
            .ToDictionary(value => value.Digest, value => value.index, StringComparer.Ordinal);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = false,
            SkipValidation = false,
        }))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", "photon.cad.project");
            writer.WriteNumber("formatVersion", PhotonCadProjectFileV1.FormatVersion);
            writer.WriteNumber("contractVersion", PhotonCadProjectFileV1.ContractVersion);
            writer.WriteString("sessionId", state.SessionId);
            writer.WriteString("projectId", state.ProjectId);
            writer.WriteNumber("revision", state.Revision);
            writer.WriteString("title", state.Title);
            writer.WriteString("units", PhotonCadManifestValuesV1.Unit(state.Units));
            writer.WriteString("mode", "canonical");
            WriteCoordinateSystem(writer);
            WriteEntities(writer, state.Entities);
            WriteOperations(writer, state.Operations);
            WriteOccurrences(writer, state.Occurrences);
            WriteIssues(writer, state.Issues);
            WriteBom(writer, state);
            WriteArtifacts(writer, state.Artifacts, blobIndexes);
            writer.WriteEndObject();
            writer.Flush();
        }

        if (stream.Length <= 0 || stream.Length > PhotonCadProjectFileV1.MaximumManifestBytes)
            throw PhotonCadFileGuardsV1.Failure("manifest_too_large", "manifest");
        return new PhotonCadManifestEncodingV1(stream.ToArray(), Array.AsReadOnly(blobs));
    }

    private static PhotonCadBlobV1[] BuildBlobs(IReadOnlyList<PhotonCadArtifactV1> artifacts)
    {
        var unique = new Dictionary<string, PhotonCadBlobV1>(StringComparer.Ordinal);
        foreach (var artifact in artifacts)
        {
            PhotonCadEmbeddedArtifactValidator.Validate(artifact.Kind, artifact.ContentUnsafe.Span);
            if (unique.TryGetValue(artifact.Digest, out var existing))
            {
                if (existing.Kind != artifact.Kind || !existing.Content.Span.SequenceEqual(artifact.ContentUnsafe.Span))
                    throw PhotonCadFileGuardsV1.Failure("artifact_digest_collision", "artifacts");
                continue;
            }
            unique.Add(artifact.Digest, new PhotonCadBlobV1(artifact.Kind, artifact.Digest, artifact.ContentUnsafe));
        }
        return unique.Values
            .OrderBy(blob => blob.Digest, StringComparer.Ordinal)
            .ThenBy(blob => (byte)blob.Kind)
            .ToArray();
    }

    private static void WriteCoordinateSystem(Utf8JsonWriter writer)
    {
        writer.WritePropertyName("coordinateSystem");
        writer.WriteStartObject();
        writer.WriteString("handedness", "right");
        writer.WriteString("upAxis", "z");
        writer.WriteString("matrixOrder", "row-major");
        writer.WriteString("vectorConvention", "column");
        writer.WriteString("transformMeaning", "local-to-parent");
        writer.WritePropertyName("translationIndices");
        writer.WriteStartArray();
        writer.WriteNumberValue(3);
        writer.WriteNumberValue(7);
        writer.WriteNumberValue(11);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteEntities(Utf8JsonWriter writer, IEnumerable<PhotonCadEntityV1> values)
    {
        writer.WritePropertyName("entities");
        writer.WriteStartArray();
        foreach (var value in values.OrderBy(entity => entity.Id, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("id", value.Id);
            WriteNullableString(writer, "parentId", value.ParentId);
            writer.WriteString("kind", PhotonCadManifestValuesV1.EntityKind(value.Kind));
            writer.WriteString("name", value.Name);
            writer.WriteBoolean("visible", value.Visible);
            writer.WriteBoolean("suppressed", value.Suppressed);
            WriteNullableString(writer, "sourceCapabilityId", value.SourceCapabilityId);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteOperations(Utf8JsonWriter writer, IEnumerable<PhotonCadOperationV1> values)
    {
        writer.WritePropertyName("operations");
        writer.WriteStartArray();
        foreach (var value in values.OrderBy(operation => operation.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteNumber("ordinal", value.Ordinal);
            writer.WriteString("id", value.Id);
            writer.WriteString("capabilityId", value.CapabilityId);
            writer.WriteString("label", value.Label);
            writer.WriteString("createdAtUtc", PhotonCadManifestValuesV1.Timestamp(value.CreatedAtUtc));
            writer.WriteString("state", PhotonCadManifestValuesV1.OperationState(value.State));
            writer.WriteString("mode", PhotonCadManifestValuesV1.OperationMode(value.Mode));
            writer.WritePropertyName("inputs");
            writer.WriteStartArray();
            foreach (var input in value.Inputs.OrderBy(input => input.Id, StringComparer.Ordinal)) WriteInput(writer, input);
            writer.WriteEndArray();
            writer.WritePropertyName("targetEntityIds");
            writer.WriteStartArray();
            foreach (var target in value.TargetEntityIds.OrderBy(target => target, StringComparer.Ordinal)) writer.WriteStringValue(target);
            writer.WriteEndArray();
            writer.WritePropertyName("source");
            WriteSource(writer, value.Source);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteInput(Utf8JsonWriter writer, PhotonCadOperationInputV1 input)
    {
        writer.WriteStartObject();
        writer.WriteString("id", input.Id);
        writer.WriteString("kind", PhotonCadManifestValuesV1.InputKind(input.Value.Kind));
        writer.WritePropertyName("value");
        if (input.Value.Value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            switch (input.Value.Kind)
            {
                case PhotonCadInputKindV1.Number:
                    writer.WriteStringValue(PhotonCadFileGuardsV1.F64Hex((double)input.Value.Value, "value"));
                    break;
                case PhotonCadInputKindV1.Integer:
                    writer.WriteNumberValue((long)input.Value.Value);
                    break;
                case PhotonCadInputKindV1.Boolean:
                    writer.WriteBooleanValue((bool)input.Value.Value);
                    break;
                case PhotonCadInputKindV1.Text:
                case PhotonCadInputKindV1.Choice:
                case PhotonCadInputKindV1.Entity:
                    writer.WriteStringValue((string)input.Value.Value);
                    break;
                case PhotonCadInputKindV1.Vector3:
                    WriteVector(writer, (PhotonCadVector3V1)input.Value.Value);
                    break;
                case PhotonCadInputKindV1.EntityList:
                    writer.WriteStartArray();
                    foreach (var entity in (IReadOnlyList<string>)input.Value.Value) writer.WriteStringValue(entity);
                    writer.WriteEndArray();
                    break;
                default:
                    throw PhotonCadFileGuardsV1.Failure("unsupported_input_kind", "input");
            }
        }
        writer.WriteEndObject();
    }

    private static void WriteOccurrences(Utf8JsonWriter writer, IEnumerable<PhotonCadOccurrenceV1> values)
    {
        writer.WritePropertyName("occurrences");
        writer.WriteStartArray();
        foreach (var value in values.OrderBy(occurrence => occurrence.OccurrenceId, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("occurrenceId", value.OccurrenceId);
            WriteNullableString(writer, "parentOccurrenceId", value.ParentOccurrenceId);
            writer.WriteString("partNumber", value.PartNumber);
            writer.WriteString("sourceEntityId", value.SourceEntityId);
            writer.WritePropertyName("transform");
            writer.WriteStartArray();
            foreach (var component in value.TransformSpan) writer.WriteStringValue(PhotonCadFileGuardsV1.F64Hex(component, "transform"));
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteIssues(Utf8JsonWriter writer, IEnumerable<PhotonCadIssueV1> values)
    {
        writer.WritePropertyName("issues");
        writer.WriteStartArray();
        foreach (var value in values
                     .OrderBy(issue => PhotonCadManifestValuesV1.Severity(issue.Severity), StringComparer.Ordinal)
                     .ThenBy(issue => issue.Code, StringComparer.Ordinal)
                     .ThenBy(issue => issue.Message, StringComparer.Ordinal)
                     .ThenBy(issue => string.Join("\0", issue.EntityIds), StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("code", value.Code);
            writer.WriteString("severity", PhotonCadManifestValuesV1.Severity(value.Severity));
            writer.WriteString("message", value.Message);
            writer.WritePropertyName("entityIds");
            writer.WriteStartArray();
            foreach (var entity in value.EntityIds) writer.WriteStringValue(entity);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteBom(Utf8JsonWriter writer, PhotonCadProjectStateV1 state)
    {
        var rows = state.Bom
            .OrderBy(row => row.SourceEntityId, StringComparer.Ordinal)
            .ThenBy(row => row.PartNumber, StringComparer.Ordinal)
            .ToArray();
        writer.WritePropertyName("bom");
        writer.WriteStartObject();
        writer.WriteString("digest", PhotonCadBomCanonicalizer.Compute(state.Units, rows));
        writer.WritePropertyName("rows");
        writer.WriteStartArray();
        foreach (var row in rows)
        {
            writer.WriteStartObject();
            writer.WriteString("partNumber", row.PartNumber);
            writer.WriteString("description", row.Description);
            writer.WriteString("quantityBits", PhotonCadFileGuardsV1.F64Hex(row.Quantity, "quantity"));
            writer.WriteString("unit", PhotonCadManifestValuesV1.BomUnit(row.Unit));
            writer.WriteString("sourceEntityId", row.SourceEntityId);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteArtifacts(
        Utf8JsonWriter writer,
        IEnumerable<PhotonCadArtifactV1> values,
        IReadOnlyDictionary<string, int> blobIndexes)
    {
        writer.WritePropertyName("artifacts");
        writer.WriteStartArray();
        foreach (var value in values
                     .OrderBy(artifact => PhotonCadManifestValuesV1.ArtifactRole(artifact.Role), StringComparer.Ordinal)
                     .ThenBy(artifact => artifact.OwnerEntityId, StringComparer.Ordinal)
                     .ThenBy(artifact => artifact.Digest, StringComparer.Ordinal)
                     .ThenBy(artifact => artifact.Revision))
        {
            writer.WriteStartObject();
            writer.WriteString("role", PhotonCadManifestValuesV1.ArtifactRole(value.Role));
            writer.WriteString("kind", PhotonCadManifestValuesV1.ArtifactKind(value.Kind));
            writer.WriteString("mediaType", PhotonCadManifestValuesV1.MediaType(value.Kind));
            WriteNullableString(writer, "ownerEntityId", value.OwnerEntityId);
            writer.WriteNumber("revision", value.Revision);
            writer.WriteNumber("blob", blobIndexes[value.Digest]);
            writer.WriteString("digest", value.Digest);
            writer.WriteNumber("byteLength", value.ByteLength);
            writer.WritePropertyName("bounds");
            if (value.Bounds is null) writer.WriteNullValue();
            else WriteBounds(writer, value.Bounds);
            writer.WritePropertyName("provenance");
            WriteProvenance(writer, value.Provenance);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteBounds(Utf8JsonWriter writer, PhotonCadBoundsV1 value)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("minimum");
        WriteVector(writer, value.Minimum);
        writer.WritePropertyName("maximum");
        WriteVector(writer, value.Maximum);
        writer.WriteEndObject();
    }

    private static void WriteVector(Utf8JsonWriter writer, PhotonCadVector3V1 value)
    {
        writer.WriteStartObject();
        writer.WriteString("x", PhotonCadFileGuardsV1.F64Hex(value.X, "x"));
        writer.WriteString("y", PhotonCadFileGuardsV1.F64Hex(value.Y, "y"));
        writer.WriteString("z", PhotonCadFileGuardsV1.F64Hex(value.Z, "z"));
        writer.WriteEndObject();
    }

    private static void WriteProvenance(Utf8JsonWriter writer, PhotonCadArtifactProvenanceV1 value)
    {
        writer.WriteStartObject();
        writer.WriteString("backend", PhotonCadManifestValuesV1.Backend(value.Backend));
        writer.WriteString("bundleId", value.BundleId);
        writer.WriteString("bundleManifestSha256", value.BundleManifestSha256);
        writer.WriteString("capabilityId", value.CapabilityId);
        writer.WriteString("operationId", value.OperationId);
        writer.WritePropertyName("source");
        WriteSource(writer, value.Source);
        writer.WriteEndObject();
    }

    private static void WriteSource(Utf8JsonWriter writer, PhotonCadSourceIdentityV1 value)
    {
        writer.WriteStartObject();
        writer.WriteString("package", value.Package);
        writer.WriteString("version", value.Version);
        writer.WriteString("digest", value.Digest);
        writer.WriteString("license", value.License);
        writer.WriteEndObject();
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string propertyName, string? value)
    {
        writer.WritePropertyName(propertyName);
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value);
    }
}
