using System.Text.Json;

namespace PhotonCadProjects.Codec;

internal static class PhotonCadManifestReaderV1
{
    private static readonly string[] RootProperties =
    [
        "schema", "formatVersion", "contractVersion", "sessionId", "projectId", "revision", "title", "units", "mode",
        "coordinateSystem", "entities", "operations", "occurrences", "issues", "bom", "artifacts",
    ];

    internal static PhotonCadProjectStateV1 Parse(ReadOnlyMemory<byte> manifest, IReadOnlyList<PhotonCadBlobV1> blobs)
    {
        if (manifest.Length <= 0 || manifest.Length > PhotonCadProjectFileV1.MaximumManifestBytes)
            throw PhotonCadFileGuardsV1.Failure("invalid_manifest_length", "manifest");
        try
        {
            using var document = JsonDocument.Parse(manifest, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = PhotonCadProjectFileV1.MaximumJsonDepth,
            });
            var root = document.RootElement;
            RequireProperties(root, "manifest", RootProperties);
            RequireString(root, "schema", "photon.cad.project");
            RequireInt32(root, "formatVersion", PhotonCadProjectFileV1.FormatVersion);
            RequireInt32(root, "contractVersion", PhotonCadProjectFileV1.ContractVersion);
            RequireString(root, "mode", "canonical");
            ValidateCoordinateSystem(root.GetProperty("coordinateSystem"));

            var entities = ParseEntities(root.GetProperty("entities"));
            var operations = ParseOperations(root.GetProperty("operations"));
            var occurrences = ParseOccurrences(root.GetProperty("occurrences"));
            var issues = ParseIssues(root.GetProperty("issues"));
            var (bomDigest, bom) = ParseBom(root.GetProperty("bom"));
            var artifacts = ParseArtifacts(root.GetProperty("artifacts"), blobs);
            var state = new PhotonCadProjectStateV1(
                RequiredString(root, "sessionId"),
                RequiredString(root, "projectId"),
                RequiredSafeInteger(root, "revision"),
                RequiredString(root, "title"),
                PhotonCadManifestValuesV1.Unit(RequiredString(root, "units")),
                entities,
                operations,
                occurrences,
                issues,
                bom,
                artifacts,
                dirty: false);
            var computedBomDigest = PhotonCadBomCanonicalizer.Compute(state.Units, state.Bom);
            if (!PhotonCadFileGuardsV1.FixedDigestEquals(bomDigest, computedBomDigest))
                throw PhotonCadFileGuardsV1.Failure("bom_digest_mismatch", "bom");
            return state;
        }
        catch (JsonException exception)
        {
            throw new PhotonCadProjectException("invalid_manifest_json", "manifest", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new PhotonCadProjectException("invalid_manifest_type", "manifest", exception);
        }
        catch (OverflowException exception)
        {
            throw new PhotonCadProjectException("manifest_integer_overflow", "manifest", exception);
        }
    }

    private static void ValidateCoordinateSystem(JsonElement value)
    {
        RequireProperties(value, "coordinateSystem", "handedness", "upAxis", "matrixOrder", "vectorConvention", "transformMeaning", "translationIndices");
        RequireString(value, "handedness", "right");
        RequireString(value, "upAxis", "z");
        RequireString(value, "matrixOrder", "row-major");
        RequireString(value, "vectorConvention", "column");
        RequireString(value, "transformMeaning", "local-to-parent");
        var indices = RequireArray(value.GetProperty("translationIndices"), "translationIndices", 3);
        if (indices.Length != 3 || indices[0].GetInt32() != 3 || indices[1].GetInt32() != 7 || indices[2].GetInt32() != 11)
            throw PhotonCadFileGuardsV1.Failure("invalid_translation_indices", "coordinateSystem");
    }

    private static PhotonCadEntityV1[] ParseEntities(JsonElement value)
    {
        var items = RequireArray(value, "entities", PhotonCadProjectFileV1.MaximumEntities);
        var result = new PhotonCadEntityV1[items.Length];
        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            RequireProperties(item, "entity", "id", "parentId", "kind", "name", "visible", "suppressed", "sourceCapabilityId");
            result[index] = new PhotonCadEntityV1(
                RequiredString(item, "id"),
                NullableString(item, "parentId"),
                PhotonCadManifestValuesV1.EntityKind(RequiredString(item, "kind")),
                RequiredString(item, "name"),
                RequiredBoolean(item, "visible"),
                RequiredBoolean(item, "suppressed"),
                NullableString(item, "sourceCapabilityId"));
        }
        return result;
    }

    private static PhotonCadOperationV1[] ParseOperations(JsonElement value)
    {
        var items = RequireArray(value, "operations", PhotonCadProjectFileV1.MaximumOperations);
        var result = new PhotonCadOperationV1[items.Length];
        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            RequireProperties(item, "operation", "ordinal", "id", "capabilityId", "label", "createdAtUtc", "state", "mode", "inputs", "targetEntityIds", "source");
            var inputsJson = RequireArray(item.GetProperty("inputs"), "inputs", PhotonCadProjectFileV1.MaximumInputsPerOperation);
            var inputs = new PhotonCadOperationInputV1[inputsJson.Length];
            for (var inputIndex = 0; inputIndex < inputs.Length; inputIndex++) inputs[inputIndex] = ParseInput(inputsJson[inputIndex]);
            result[index] = new PhotonCadOperationV1(
                RequiredInt32(item, "ordinal"),
                RequiredString(item, "id"),
                RequiredString(item, "capabilityId"),
                RequiredString(item, "label"),
                PhotonCadManifestValuesV1.Timestamp(RequiredString(item, "createdAtUtc"), "createdAtUtc"),
                PhotonCadManifestValuesV1.OperationState(RequiredString(item, "state")),
                PhotonCadManifestValuesV1.OperationMode(RequiredString(item, "mode")),
                inputs,
                ReadStringArray(item.GetProperty("targetEntityIds"), "targetEntityIds", PhotonCadProjectFileV1.MaximumTargetsPerOperation),
                ParseSource(item.GetProperty("source")));
        }
        return result;
    }

    private static PhotonCadOperationInputV1 ParseInput(JsonElement item)
    {
        RequireProperties(item, "input", "id", "kind", "value");
        var kind = PhotonCadManifestValuesV1.InputKind(RequiredString(item, "kind"));
        var value = item.GetProperty("value");
        PhotonCadInputValueV1 parsed;
        if (value.ValueKind == JsonValueKind.Null)
        {
            parsed = PhotonCadInputValueV1.Null(kind);
        }
        else
        {
            parsed = kind switch
            {
                PhotonCadInputKindV1.Number => PhotonCadInputValueV1.Number(PhotonCadFileGuardsV1.ParseF64Hex(RequiredStringValue(value, "value"), "value")),
                PhotonCadInputKindV1.Integer => PhotonCadInputValueV1.Integer(value.GetInt64()),
                PhotonCadInputKindV1.Boolean => PhotonCadInputValueV1.Boolean(value.GetBoolean()),
                PhotonCadInputKindV1.Text => PhotonCadInputValueV1.Text(RequiredStringValue(value, "value")),
                PhotonCadInputKindV1.Choice => PhotonCadInputValueV1.Choice(RequiredStringValue(value, "value")),
                PhotonCadInputKindV1.Vector3 => PhotonCadInputValueV1.Vector3(ParseVector(value)),
                PhotonCadInputKindV1.Entity => PhotonCadInputValueV1.Entity(RequiredStringValue(value, "value")),
                PhotonCadInputKindV1.EntityList => PhotonCadInputValueV1.EntityList(ReadStringArray(value, "value", 1_000)),
                _ => throw PhotonCadFileGuardsV1.Failure("unsupported_input_kind", "input"),
            };
        }
        return new PhotonCadOperationInputV1(RequiredString(item, "id"), parsed);
    }

    private static PhotonCadOccurrenceV1[] ParseOccurrences(JsonElement value)
    {
        var items = RequireArray(value, "occurrences", PhotonCadProjectFileV1.MaximumOccurrences);
        var result = new PhotonCadOccurrenceV1[items.Length];
        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            RequireProperties(item, "occurrence", "occurrenceId", "parentOccurrenceId", "partNumber", "sourceEntityId", "transform");
            var transformJson = RequireArray(item.GetProperty("transform"), "transform", 16);
            if (transformJson.Length != 16) throw PhotonCadFileGuardsV1.Failure("invalid_transform", "transform");
            var transform = transformJson.Select(component => PhotonCadFileGuardsV1.ParseF64Hex(RequiredStringValue(component, "transform"), "transform")).ToArray();
            result[index] = new PhotonCadOccurrenceV1(
                RequiredString(item, "occurrenceId"),
                NullableString(item, "parentOccurrenceId"),
                RequiredString(item, "partNumber"),
                RequiredString(item, "sourceEntityId"),
                transform);
        }
        return result;
    }

    private static PhotonCadIssueV1[] ParseIssues(JsonElement value)
    {
        var items = RequireArray(value, "issues", PhotonCadProjectFileV1.MaximumIssues);
        var result = new PhotonCadIssueV1[items.Length];
        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            RequireProperties(item, "issue", "code", "severity", "message", "entityIds");
            result[index] = new PhotonCadIssueV1(
                RequiredString(item, "code"),
                PhotonCadManifestValuesV1.Severity(RequiredString(item, "severity")),
                RequiredString(item, "message"),
                ReadStringArray(item.GetProperty("entityIds"), "entityIds", PhotonCadProjectFileV1.MaximumEntities));
        }
        return result;
    }

    private static (string Digest, PhotonCadBomRow[] Rows) ParseBom(JsonElement value)
    {
        RequireProperties(value, "bom", "digest", "rows");
        var items = RequireArray(value.GetProperty("rows"), "bom", PhotonCadProjectContract.MaximumBomRows);
        var result = new PhotonCadBomRow[items.Length];
        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            RequireProperties(item, "bomRow", "partNumber", "description", "quantityBits", "unit", "sourceEntityId");
            result[index] = new PhotonCadBomRow(
                RequiredString(item, "partNumber"),
                RequiredString(item, "description"),
                PhotonCadFileGuardsV1.ParseF64Hex(RequiredString(item, "quantityBits"), "quantityBits"),
                PhotonCadManifestValuesV1.BomUnit(RequiredString(item, "unit")),
                RequiredString(item, "sourceEntityId"));
        }
        return (PhotonCadFileGuardsV1.Digest(RequiredString(value, "digest"), "digest"), result);
    }

    private static PhotonCadArtifactV1[] ParseArtifacts(JsonElement value, IReadOnlyList<PhotonCadBlobV1> blobs)
    {
        var items = RequireArray(value, "artifacts", PhotonCadProjectFileV1.MaximumBlobCount);
        var result = new PhotonCadArtifactV1[items.Length];
        var used = new bool[blobs.Count];
        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            RequireProperties(item, "artifact", "role", "kind", "mediaType", "ownerEntityId", "revision", "blob", "digest", "byteLength", "bounds", "provenance");
            var blobIndex = RequiredInt32(item, "blob");
            if (blobIndex < 0 || blobIndex >= blobs.Count) throw PhotonCadFileGuardsV1.Failure("artifact_blob_missing", "artifact");
            var blob = blobs[blobIndex];
            var kind = PhotonCadManifestValuesV1.ArtifactKind(RequiredString(item, "kind"));
            var digest = PhotonCadFileGuardsV1.Digest(RequiredString(item, "digest"), "digest");
            var byteLength = RequiredSafeInteger(item, "byteLength");
            if (kind != blob.Kind || !StringComparer.Ordinal.Equals(RequiredString(item, "mediaType"), PhotonCadManifestValuesV1.MediaType(kind))
                || byteLength != blob.ByteLength || !PhotonCadFileGuardsV1.FixedDigestEquals(digest, blob.Digest))
                throw PhotonCadFileGuardsV1.Failure("artifact_blob_binding_mismatch", "artifact");
            used[blobIndex] = true;
            result[index] = new PhotonCadArtifactV1(
                PhotonCadManifestValuesV1.ArtifactRole(RequiredString(item, "role")),
                kind,
                NullableString(item, "ownerEntityId"),
                RequiredSafeInteger(item, "revision"),
                blob.Content,
                item.GetProperty("bounds").ValueKind == JsonValueKind.Null ? null : ParseBounds(item.GetProperty("bounds")),
                ParseProvenance(item.GetProperty("provenance")));
        }
        if (used.Any(value => !value)) throw PhotonCadFileGuardsV1.Failure("unreferenced_blob", "artifacts");
        return result;
    }

    private static PhotonCadBoundsV1 ParseBounds(JsonElement value)
    {
        RequireProperties(value, "bounds", "minimum", "maximum");
        return new PhotonCadBoundsV1(ParseVector(value.GetProperty("minimum")), ParseVector(value.GetProperty("maximum")));
    }

    private static PhotonCadVector3V1 ParseVector(JsonElement value)
    {
        RequireProperties(value, "vector", "x", "y", "z");
        return new PhotonCadVector3V1(
            PhotonCadFileGuardsV1.ParseF64Hex(RequiredString(value, "x"), "x"),
            PhotonCadFileGuardsV1.ParseF64Hex(RequiredString(value, "y"), "y"),
            PhotonCadFileGuardsV1.ParseF64Hex(RequiredString(value, "z"), "z"));
    }

    private static PhotonCadArtifactProvenanceV1 ParseProvenance(JsonElement value)
    {
        RequireProperties(value, "provenance", "backend", "bundleId", "bundleManifestSha256", "capabilityId", "operationId", "source");
        return new PhotonCadArtifactProvenanceV1(
            PhotonCadManifestValuesV1.Backend(RequiredString(value, "backend")),
            RequiredString(value, "bundleId"),
            RequiredString(value, "bundleManifestSha256"),
            RequiredString(value, "capabilityId"),
            RequiredString(value, "operationId"),
            ParseSource(value.GetProperty("source")));
    }

    private static PhotonCadSourceIdentityV1 ParseSource(JsonElement value)
    {
        RequireProperties(value, "source", "package", "version", "digest", "license");
        return new PhotonCadSourceIdentityV1(
            RequiredString(value, "package"),
            RequiredString(value, "version"),
            RequiredString(value, "digest"),
            RequiredString(value, "license"));
    }

    private static void RequireProperties(JsonElement value, string field, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) throw PhotonCadFileGuardsV1.Failure("object_required", field);
        var actual = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
            throw PhotonCadFileGuardsV1.Failure("noncanonical_object_properties", field);
    }

    private static JsonElement[] RequireArray(JsonElement value, string field, int maximum)
    {
        if (value.ValueKind != JsonValueKind.Array) throw PhotonCadFileGuardsV1.Failure("array_required", field);
        var length = value.GetArrayLength();
        if (length > maximum) throw PhotonCadFileGuardsV1.Failure("collection_too_large", field);
        return value.EnumerateArray().ToArray();
    }

    private static string[] ReadStringArray(JsonElement value, string field, int maximum) =>
        RequireArray(value, field, maximum).Select(item => RequiredStringValue(item, field)).ToArray();

    private static string RequiredString(JsonElement value, string property) => RequiredStringValue(value.GetProperty(property), property);

    private static string RequiredStringValue(JsonElement value, string field)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } result)
            throw PhotonCadFileGuardsV1.Failure("string_required", field);
        return result;
    }

    private static string? NullableString(JsonElement value, string property)
    {
        var item = value.GetProperty(property);
        return item.ValueKind == JsonValueKind.Null ? null : RequiredStringValue(item, property);
    }

    private static bool RequiredBoolean(JsonElement value, string property)
    {
        var item = value.GetProperty(property);
        if (item.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw PhotonCadFileGuardsV1.Failure("boolean_required", property);
        return item.GetBoolean();
    }

    private static int RequiredInt32(JsonElement value, string property)
    {
        var item = value.GetProperty(property);
        if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var result))
            throw PhotonCadFileGuardsV1.Failure("integer_required", property);
        return result;
    }

    private static long RequiredSafeInteger(JsonElement value, string property)
    {
        var item = value.GetProperty(property);
        if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt64(out var result))
            throw PhotonCadFileGuardsV1.Failure("integer_required", property);
        return PhotonCadFileGuardsV1.SafeInteger(result, property);
    }

    private static void RequireString(JsonElement value, string property, string expected)
    {
        if (!StringComparer.Ordinal.Equals(RequiredString(value, property), expected))
            throw PhotonCadFileGuardsV1.Failure("unsupported_constant", property);
    }

    private static void RequireInt32(JsonElement value, string property, int expected)
    {
        if (RequiredInt32(value, property) != expected) throw PhotonCadFileGuardsV1.Failure("unsupported_version", property);
    }
}
