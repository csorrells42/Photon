using System.Text;
using System.Text.Json;

namespace PhotonCadRuntime.IndustrialProvider;

internal sealed record IndustrialStepAssemblyDefinitionClaim(
    string EntityId,
    string? ParentEntityId,
    string Kind,
    string PartNumber,
    string DisplayName,
    bool PreviewSource,
    IndustrialArtifactClaim Artifact,
    string? OutputFile);

internal sealed record IndustrialStepAssemblyOccurrenceClaim(
    string OccurrenceId,
    string? ParentOccurrenceId,
    string SourceEntityId,
    string PartNumber,
    IReadOnlyList<double> Transform);

internal sealed record IndustrialStepAssemblyInspection(
    string SourceDigest,
    string RootEntityId,
    IReadOnlyList<IndustrialStepAssemblyDefinitionClaim> Definitions,
    IReadOnlyList<IndustrialStepAssemblyOccurrenceClaim> Occurrences);

internal static class StepAssemblyProtocol
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static byte[] SerializeInspection(string inputSlot, string expectedDigest)
    {
        Token(inputSlot, 64, "inspection_slot_invalid");
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", ProtocolV1.RequestSchema);
            writer.WriteString("operation", "inspectStepAssembly");
            writer.WriteStartObject("source");
            writer.WriteString("inputSlot", inputSlot);
            writer.WriteString("expectedDigest", ProtocolV1.NormalizeDigest(expectedDigest));
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        var result = stream.ToArray();
        if (result.Length > ProtocolV1.MaximumRequestBytes) throw Failure("industrial_request_too_large");
        return result;
    }

    internal static IndustrialStepAssemblyInspection ParseInspection(
        ReadOnlyMemory<byte> payload,
        string expectedDigest,
        long expectedByteLength)
    {
        if (payload.Length is <= 0 or > ProtocolV1.MaximumResponseBytes) throw Failure("industrial_response_too_large");
        JsonDocument document;
        try
        {
            _ = StrictUtf8.GetString(payload.Span);
            document = JsonDocument.Parse(payload, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException)
        {
            throw new InvalidDataException("industrial_protocol_invalid_json", exception);
        }
        using (document)
        {
            var root = document.RootElement;
            RejectDuplicates(root);
            Exact(root, "schema", "ok", "operation", "sourceDigest", "rootEntityId", "definitionCount", "occurrenceCount", "definitions", "occurrences", "provenance");
            Require(root, "schema", ProtocolV1.ResponseSchema);
            if (root.GetProperty("ok").ValueKind != JsonValueKind.True) throw Failure("inspection_not_ok");
            Require(root, "operation", "inspectStepAssembly");
            var sourceDigest = ProtocolV1.NormalizeDigest(Text(root, "sourceDigest"));
            if (!ProtocolV1.FixedDigestEquals(sourceDigest, expectedDigest)) throw Failure("inspection_source_digest_mismatch");
            var rootEntityId = Identifier(Text(root, "rootEntityId"));
            var definitionsElement = root.GetProperty("definitions");
            var occurrencesElement = root.GetProperty("occurrences");
            if (definitionsElement.ValueKind != JsonValueKind.Array || definitionsElement.GetArrayLength() is < 2 or > 256
                || occurrencesElement.ValueKind != JsonValueKind.Array || occurrencesElement.GetArrayLength() is < 2 or > 1024
                || Integer(root, "definitionCount", 2, 256) != definitionsElement.GetArrayLength()
                || Integer(root, "occurrenceCount", 2, 1024) != occurrencesElement.GetArrayLength())
                throw Failure("inspection_count_mismatch");

            var definitions = definitionsElement.EnumerateArray().Select(ParseDefinition).ToArray();
            var occurrences = occurrencesElement.EnumerateArray().Select(ParseOccurrence).ToArray();
            var provenance = root.GetProperty("provenance");
            Exact(provenance, "sourceDigest", "sourceByteLength");
            if (!ProtocolV1.FixedDigestEquals(Text(provenance, "sourceDigest"), expectedDigest)
                || Integer(provenance, "sourceByteLength", 1, ProtocolV1.MaximumStepBytes) != expectedByteLength)
                throw Failure("inspection_provenance_mismatch");
            Validate(rootEntityId, definitions, occurrences, expectedDigest, expectedByteLength);
            return new IndustrialStepAssemblyInspection(sourceDigest, rootEntityId, definitions, occurrences);
        }
    }

    private static IndustrialStepAssemblyDefinitionClaim ParseDefinition(JsonElement value)
    {
        Exact(value, "entityId", "parentEntityId", "kind", "partNumber", "displayName", "previewSource", "artifact", "outputFile");
        var kind = Text(value, "kind");
        if (kind is not ("part" or "assembly")) throw Failure("inspection_definition_kind_invalid");
        var preview = value.GetProperty("previewSource");
        if (preview.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Failure("inspection_preview_flag_invalid");
        var artifact = value.GetProperty("artifact");
        Exact(artifact, "format", "contentDigest", "byteLength");
        Require(artifact, "format", "step");
        return new IndustrialStepAssemblyDefinitionClaim(
            Identifier(Text(value, "entityId")),
            OptionalIdentifier(value.GetProperty("parentEntityId")),
            kind,
            Token(Text(value, "partNumber"), 64, "inspection_part_number_invalid"),
            DisplayName(Text(value, "displayName")),
            preview.GetBoolean(),
            new IndustrialArtifactClaim(
                "step",
                ProtocolV1.NormalizeDigest(Text(artifact, "contentDigest")),
                Integer(artifact, "byteLength", 1, ProtocolV1.MaximumStepBytes)),
            OptionalOutput(value.GetProperty("outputFile")));
    }

    private static IndustrialStepAssemblyOccurrenceClaim ParseOccurrence(JsonElement value)
    {
        Exact(value, "occurrenceId", "parentOccurrenceId", "sourceEntityId", "partNumber", "transform");
        var transform = value.GetProperty("transform");
        if (transform.ValueKind != JsonValueKind.Array || transform.GetArrayLength() != 16)
            throw Failure("inspection_transform_invalid");
        var values = transform.EnumerateArray().Select(item => Number(item, -1_000_000_000, 1_000_000_000)).ToArray();
        ValidateRigid(values);
        return new IndustrialStepAssemblyOccurrenceClaim(
            Identifier(Text(value, "occurrenceId")),
            OptionalIdentifier(value.GetProperty("parentOccurrenceId")),
            Identifier(Text(value, "sourceEntityId")),
            Token(Text(value, "partNumber"), 64, "inspection_part_number_invalid"),
            values);
    }

    private static void Validate(
        string rootEntityId,
        IReadOnlyList<IndustrialStepAssemblyDefinitionClaim> definitions,
        IReadOnlyList<IndustrialStepAssemblyOccurrenceClaim> occurrences,
        string expectedDigest,
        long expectedLength)
    {
        if (!definitions.Select(value => value.EntityId).SequenceEqual(definitions.Select(value => value.EntityId).OrderBy(value => value, StringComparer.Ordinal), StringComparer.Ordinal)
            || definitions.Select(value => value.EntityId).Distinct(StringComparer.Ordinal).Count() != definitions.Count
            || !occurrences.Select(value => value.OccurrenceId).SequenceEqual(occurrences.Select(value => value.OccurrenceId).OrderBy(value => value, StringComparer.Ordinal), StringComparer.Ordinal)
            || occurrences.Select(value => value.OccurrenceId).Distinct(StringComparer.Ordinal).Count() != occurrences.Count)
            throw Failure("inspection_order_or_identity_invalid");
        var byEntity = definitions.ToDictionary(value => value.EntityId, StringComparer.Ordinal);
        if (!byEntity.TryGetValue(rootEntityId, out var root) || root.Kind != "assembly" || root.ParentEntityId is not null
            || root.OutputFile is not null || !ProtocolV1.FixedDigestEquals(root.Artifact.ContentDigest, expectedDigest)
            || root.Artifact.ByteLength != expectedLength)
            throw Failure("inspection_root_invalid");
        var outputFiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            if (definition.Kind == "part" != definition.PreviewSource
                || definition.EntityId != rootEntityId && definition.OutputFile is null
                || definition.OutputFile is not null && !outputFiles.Add(definition.OutputFile)
                || definition.ParentEntityId is not null && !byEntity.ContainsKey(definition.ParentEntityId))
                throw Failure("inspection_definition_invalid");
            WalkEntity(definition, byEntity);
        }
        var byOccurrence = occurrences.ToDictionary(value => value.OccurrenceId, StringComparer.Ordinal);
        if (occurrences.Count(value => value.ParentOccurrenceId is null) != 1) throw Failure("inspection_root_occurrence_invalid");
        foreach (var occurrence in occurrences)
        {
            if (!byEntity.TryGetValue(occurrence.SourceEntityId, out var definition)
                || !StringComparer.Ordinal.Equals(definition.PartNumber, occurrence.PartNumber)
                || occurrence.ParentOccurrenceId is not null && !byOccurrence.ContainsKey(occurrence.ParentOccurrenceId))
                throw Failure("inspection_occurrence_invalid");
            WalkOccurrence(occurrence, byOccurrence);
        }
        var rootOccurrence = occurrences.Single(value => value.ParentOccurrenceId is null);
        if (!StringComparer.Ordinal.Equals(rootOccurrence.SourceEntityId, rootEntityId)
            || !definitions.Where(value => value.Kind == "part").All(part => occurrences.Any(value => value.SourceEntityId == part.EntityId)))
            throw Failure("inspection_coverage_invalid");
    }

    private static void WalkEntity(IndustrialStepAssemblyDefinitionClaim start, IReadOnlyDictionary<string, IndustrialStepAssemblyDefinitionClaim> values)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = start;
        for (var depth = 0; current.ParentEntityId is not null; depth++)
        {
            if (depth >= 64 || !seen.Add(current.EntityId) || !values.TryGetValue(current.ParentEntityId, out current!))
                throw Failure("inspection_entity_tree_invalid");
        }
    }

    private static void WalkOccurrence(IndustrialStepAssemblyOccurrenceClaim start, IReadOnlyDictionary<string, IndustrialStepAssemblyOccurrenceClaim> values)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = start;
        for (var depth = 0; current.ParentOccurrenceId is not null; depth++)
        {
            if (depth >= 64 || !seen.Add(current.OccurrenceId) || !values.TryGetValue(current.ParentOccurrenceId, out current!))
                throw Failure("inspection_occurrence_tree_invalid");
        }
    }

    private static void ValidateRigid(IReadOnlyList<double> matrix)
    {
        const double tolerance = 1e-9;
        if (Math.Abs(matrix[12]) > tolerance || Math.Abs(matrix[13]) > tolerance || Math.Abs(matrix[14]) > tolerance || Math.Abs(matrix[15] - 1) > tolerance)
            throw Failure("inspection_transform_not_rigid");
        for (var row = 0; row < 3; row++)
        {
            var norm = 0.0;
            for (var column = 0; column < 3; column++) norm += matrix[row * 4 + column] * matrix[row * 4 + column];
            if (Math.Abs(norm - 1) > tolerance) throw Failure("inspection_transform_not_rigid");
            for (var other = row + 1; other < 3; other++)
            {
                var dot = 0.0;
                for (var column = 0; column < 3; column++) dot += matrix[row * 4 + column] * matrix[other * 4 + column];
                if (Math.Abs(dot) > tolerance) throw Failure("inspection_transform_not_rigid");
            }
        }
    }

    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Failure("duplicate_protocol_member");
                RejectDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
        }
    }

    private static void Exact(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Failure("object_required");
        var actual = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != names.Length || names.Any(name => !actual.Contains(name, StringComparer.Ordinal)))
            throw Failure("unexpected_protocol_member");
    }

    private static string Text(JsonElement value, string name) => value.GetProperty(name).ValueKind == JsonValueKind.String
        ? value.GetProperty(name).GetString()!
        : throw Failure($"{name}_invalid");

    private static void Require(JsonElement value, string name, string expected)
    {
        if (!StringComparer.Ordinal.Equals(Text(value, name), expected)) throw Failure($"{name}_mismatch");
    }

    private static long Integer(JsonElement value, string name, long minimum, long maximum) => Integer(value.GetProperty(name), minimum, maximum);
    private static long Integer(JsonElement value, long minimum, long maximum) => value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var result) && result >= minimum && result <= maximum
            ? result : throw Failure("invalid_integer");
    private static double Number(JsonElement value, double minimum, double maximum) => value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var result) && double.IsFinite(result) && result >= minimum && result <= maximum
            ? result : throw Failure("invalid_number");
    private static string Identifier(string value) => Token(value, 128, "inspection_identifier_invalid", allowColon: true);
    private static string? OptionalIdentifier(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => Identifier(value.GetString()!),
        _ => throw Failure("inspection_optional_identifier_invalid"),
    };
    private static string? OptionalOutput(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } result
            || result.Length != "definition-0000.step".Length
            || !result.StartsWith("definition-", StringComparison.Ordinal)
            || !result.EndsWith(".step", StringComparison.Ordinal)
            || result.AsSpan(11, 4).IndexOfAnyExceptInRange('0', '9') >= 0)
            throw Failure("inspection_output_file_invalid");
        return result;
    }
    private static string DisplayName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl)) throw Failure("inspection_display_name_invalid");
        return value;
    }
    private static string Token(string value, int maximum, string code, bool allowColon = false)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || !char.IsAsciiLetterOrDigit(value[0])
            || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-' and not '.' && (!allowColon || character != ':')))
            throw Failure(code);
        return value;
    }
    private static InvalidDataException Failure(string code) => new(code);
}
