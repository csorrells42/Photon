using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhotonCadRuntime.IndustrialProvider;

internal enum IndustrialPrimitiveKind
{
    Box,
    Cylinder,
}

internal sealed record IndustrialPrimitiveCommand(
    IndustrialPrimitiveKind Kind,
    double LengthMm,
    double WidthMm,
    double HeightMm,
    double RadiusMm,
    string EntityId,
    string PartNumber,
    string Label);

internal sealed record IndustrialArtifactClaim(string Format, string ContentDigest, long ByteLength);

internal sealed record IndustrialBounds(
    double MinimumX,
    double MinimumY,
    double MinimumZ,
    double MaximumX,
    double MaximumY,
    double MaximumZ);

internal sealed record IndustrialPrimitiveResponse(
    IndustrialArtifactClaim Artifact,
    IndustrialBounds Bounds,
    double VolumeMm3);

internal sealed record IndustrialPreviewSource(
    string SourcePartId,
    string InputSlot,
    string ExpectedDigest,
    long ExpectedByteLength);

internal sealed record IndustrialPreviewOccurrence(
    string EntityId,
    string SourcePartId,
    string? ParentEntityId,
    IReadOnlyList<double> Transform);

internal sealed record IndustrialPreviewCommand(
    IReadOnlyList<IndustrialPreviewSource> Sources,
    IReadOnlyList<IndustrialPreviewOccurrence> Occurrences);

internal sealed record IndustrialPreviewResponse(
    IndustrialArtifactClaim Artifact,
    IndustrialBounds Bounds);

internal static class ProtocolV1
{
    internal const string RequestSchema = "photon.cad.industrial.request/v1";
    internal const string ResponseSchema = "photon.cad.industrial.response/v1";
    internal const int MaximumRequestBytes = 1024 * 1024;
    internal const int MaximumResponseBytes = 4 * 1024 * 1024;
    internal const int MaximumStepBytes = 64 * 1024 * 1024;
    internal const int MaximumGlbBytes = 128 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static byte[] SerializePrimitive(IndustrialPrimitiveCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", RequestSchema);
            writer.WriteString("operation", "createPrimitive");
            writer.WriteStartObject("primitive");
            writer.WriteString("kind", command.Kind == IndustrialPrimitiveKind.Box ? "box" : "cylinder");
            writer.WriteStartObject("dimensions");
            if (command.Kind == IndustrialPrimitiveKind.Box)
            {
                writer.WriteNumber("lengthMm", command.LengthMm);
                writer.WriteNumber("widthMm", command.WidthMm);
                writer.WriteNumber("heightMm", command.HeightMm);
            }
            else
            {
                writer.WriteNumber("radiusMm", command.RadiusMm);
                writer.WriteNumber("heightMm", command.HeightMm);
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Bounded(stream.ToArray(), MaximumRequestBytes, "industrial_request_too_large");
    }

    internal static byte[] SerializePreview(IndustrialPreviewCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidatePreviewCommand(command);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", RequestSchema);
            writer.WriteString("operation", "createPreview");
            writer.WriteStartArray("sources");
            foreach (var source in command.Sources)
            {
                writer.WriteStartObject();
                writer.WriteString("sourcePartId", source.SourcePartId);
                writer.WriteString("inputSlot", source.InputSlot);
                writer.WriteString("expectedDigest", source.ExpectedDigest);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("occurrences");
            foreach (var occurrence in command.Occurrences)
            {
                writer.WriteStartObject();
                writer.WriteString("entityId", occurrence.EntityId);
                writer.WriteString("sourcePartId", occurrence.SourcePartId);
                if (occurrence.ParentEntityId is null) writer.WriteNull("parentEntityId");
                else writer.WriteString("parentEntityId", occurrence.ParentEntityId);
                writer.WriteStartArray("transform");
                foreach (var value in occurrence.Transform) writer.WriteNumberValue(value);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartObject("tessellation");
            writer.WriteNumber("linearToleranceMm", 0.1);
            writer.WriteNumber("angularToleranceRad", 0.1);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Bounded(stream.ToArray(), MaximumRequestBytes, "industrial_request_too_large");
    }

    internal static IndustrialPrimitiveResponse ParsePrimitiveResponse(
        ReadOnlyMemory<byte> payload,
        IndustrialPrimitiveCommand command)
    {
        using var document = Parse(payload);
        var root = document.RootElement;
        Exact(root, "schema", "ok", "operation", "artifact", "measurement", "provenance");
        RequireString(root, "schema", ResponseSchema);
        RequireBoolean(root, "ok", true);
        RequireString(root, "operation", "createPrimitive");
        var artifact = Artifact(root.GetProperty("artifact"), "step", MaximumStepBytes);
        var measurement = root.GetProperty("measurement");
        Exact(measurement, "units", "volumeMm3", "solidCount", "bounds");
        RequireString(measurement, "units", "millimeter");
        if (Integer(measurement, "solidCount", 1, 1) != 1) throw Failure("solid_count_not_one");
        var volume = Number(measurement, "volumeMm3", double.Epsilon, double.MaxValue);
        var bounds = Bounds(measurement.GetProperty("bounds"));
        ValidatePrimitiveProvenance(root.GetProperty("provenance"), command);
        return new IndustrialPrimitiveResponse(artifact, bounds, volume);
    }

    internal static IndustrialPreviewResponse ParsePreviewResponse(
        ReadOnlyMemory<byte> payload,
        IndustrialPreviewCommand command)
    {
        using var document = Parse(payload);
        var root = document.RootElement;
        Exact(root, "schema", "ok", "operation", "artifact", "entityCount", "sourceCount", "bounds", "units", "provenance");
        RequireString(root, "schema", ResponseSchema);
        RequireBoolean(root, "ok", true);
        RequireString(root, "operation", "createPreview");
        if (Integer(root, "entityCount", 1, 1024) != command.Occurrences.Count
            || Integer(root, "sourceCount", 1, 256) != command.Sources.Count)
            throw Failure("preview_count_mismatch");
        RequireString(root, "units", "millimeter");
        var artifact = Artifact(root.GetProperty("artifact"), "glb", MaximumGlbBytes);
        var bounds = Bounds(root.GetProperty("bounds"));
        ValidatePreviewProvenance(root.GetProperty("provenance"), command);
        return new IndustrialPreviewResponse(artifact, bounds);
    }

    internal static JsonDocument ParseCatalog(ReadOnlyMemory<byte> payload, string expectedDigest)
    {
        var actual = Sha256(payload.Span);
        if (!FixedDigestEquals(actual, expectedDigest)) throw Failure("catalog_digest_mismatch");
        var document = Parse(payload);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) { document.Dispose(); throw Failure("catalog_not_object"); }
        RejectDuplicateProperties(root);
        return document;
    }

    internal static string Sha256(ReadOnlySpan<byte> payload) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(payload))}";

    internal static bool FixedDigestEquals(string left, string right)
    {
        var first = Encoding.ASCII.GetBytes(NormalizeDigest(left));
        var second = Encoding.ASCII.GetBytes(NormalizeDigest(right));
        return first.Length == second.Length && CryptographicOperations.FixedTimeEquals(first, second);
    }

    internal static string NormalizeDigest(string value)
    {
        if (value is null) throw Failure("digest_required");
        var normalized = value.ToLowerInvariant();
        if (normalized.StartsWith("sha256:", StringComparison.Ordinal)) normalized = normalized[7..];
        if (normalized.Length != 64 || normalized.Any(character => !char.IsAsciiHexDigit(character)))
            throw Failure("invalid_digest");
        return $"sha256:{normalized}";
    }

    private static JsonDocument Parse(ReadOnlyMemory<byte> payload)
    {
        Bounded(payload.ToArray(), MaximumResponseBytes, "industrial_response_too_large");
        try
        {
            _ = StrictUtf8.GetString(payload.Span);
            var document = JsonDocument.Parse(payload, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            RejectDuplicateProperties(document.RootElement);
            return document;
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException)
        {
            throw new InvalidDataException("industrial_protocol_invalid_json", exception);
        }
    }

    private static IndustrialArtifactClaim Artifact(JsonElement value, string format, int maximumBytes)
    {
        Exact(value, "format", "contentDigest", "byteLength");
        RequireString(value, "format", format);
        var digest = NormalizeDigest(String(value, "contentDigest"));
        var minimum = format == "glb" ? 20 : 1;
        var length = Integer(value, "byteLength", minimum, maximumBytes);
        return new IndustrialArtifactClaim(format, digest, length);
    }

    private static IndustrialBounds Bounds(JsonElement value)
    {
        Exact(value, "minimum", "maximum");
        var minimum = Vector(value.GetProperty("minimum"));
        var maximum = Vector(value.GetProperty("maximum"));
        if (minimum[0] > maximum[0] || minimum[1] > maximum[1] || minimum[2] > maximum[2])
            throw Failure("invalid_bounds");
        return new IndustrialBounds(minimum[0], minimum[1], minimum[2], maximum[0], maximum[1], maximum[2]);
    }

    private static double[] Vector(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 3) throw Failure("invalid_vector");
        return value.EnumerateArray().Select(item => Number(item, -1_000_000_000, 1_000_000_000)).ToArray();
    }

    private static void ValidatePrimitiveProvenance(JsonElement value, IndustrialPrimitiveCommand command)
    {
        Exact(value, "generator", "kind", "parameters");
        RequireString(value, "generator", "primitive");
        RequireString(value, "kind", command.Kind == IndustrialPrimitiveKind.Box ? "box" : "cylinder");
        var parameters = value.GetProperty("parameters");
        if (command.Kind == IndustrialPrimitiveKind.Box)
        {
            Exact(parameters, "heightMm", "lengthMm", "widthMm");
            SameNumber(parameters, "lengthMm", command.LengthMm);
            SameNumber(parameters, "widthMm", command.WidthMm);
            SameNumber(parameters, "heightMm", command.HeightMm);
        }
        else
        {
            Exact(parameters, "heightMm", "radiusMm");
            SameNumber(parameters, "radiusMm", command.RadiusMm);
            SameNumber(parameters, "heightMm", command.HeightMm);
        }
    }

    private static void ValidatePreviewProvenance(JsonElement value, IndustrialPreviewCommand command)
    {
        Exact(value, "sources", "occurrences", "tessellation");
        var sources = value.GetProperty("sources");
        if (sources.ValueKind != JsonValueKind.Array || sources.GetArrayLength() != command.Sources.Count)
            throw Failure("preview_source_mismatch");
        for (var index = 0; index < command.Sources.Count; index++)
        {
            var source = sources[index];
            var expected = command.Sources[index];
            Exact(source, "sourcePartId", "contentDigest", "byteLength");
            RequireString(source, "sourcePartId", expected.SourcePartId);
            if (!FixedDigestEquals(String(source, "contentDigest"), expected.ExpectedDigest)
                || Integer(source, "byteLength", 1, MaximumStepBytes) != expected.ExpectedByteLength)
                throw Failure("preview_source_mismatch");
        }
        var occurrences = value.GetProperty("occurrences");
        if (occurrences.ValueKind != JsonValueKind.Array || occurrences.GetArrayLength() != command.Occurrences.Count)
            throw Failure("preview_occurrence_mismatch");
        for (var occurrenceIndex = 0; occurrenceIndex < command.Occurrences.Count; occurrenceIndex++)
        {
            var occurrence = occurrences[occurrenceIndex];
            var expected = command.Occurrences[occurrenceIndex];
            Exact(occurrence, "entityId", "sourcePartId", "parentEntityId", "transform");
            RequireString(occurrence, "entityId", expected.EntityId);
            RequireString(occurrence, "sourcePartId", expected.SourcePartId);
            var parent = occurrence.GetProperty("parentEntityId");
            if (expected.ParentEntityId is null)
            {
                if (parent.ValueKind != JsonValueKind.Null) throw Failure("preview_parent_mismatch");
            }
            else if (parent.ValueKind != JsonValueKind.String
                || !StringComparer.Ordinal.Equals(parent.GetString(), expected.ParentEntityId))
            {
                throw Failure("preview_parent_mismatch");
            }
            var transform = occurrence.GetProperty("transform");
            if (transform.ValueKind != JsonValueKind.Array || transform.GetArrayLength() != 16)
                throw Failure("invalid_transform");
            for (var transformIndex = 0; transformIndex < 16; transformIndex++)
            {
                if (BitConverter.DoubleToInt64Bits(Number(transform[transformIndex], -1_000_000_000, 1_000_000_000))
                    != BitConverter.DoubleToInt64Bits(expected.Transform[transformIndex]))
                    throw Failure("preview_transform_mismatch");
            }
        }
        var tessellation = value.GetProperty("tessellation");
        Exact(tessellation, "linearToleranceMm", "angularToleranceRad");
        SameNumber(tessellation, "linearToleranceMm", 0.1);
        SameNumber(tessellation, "angularToleranceRad", 0.1);
    }

    private static void ValidatePreviewCommand(IndustrialPreviewCommand command)
    {
        if (command.Sources.Count is < 1 or > 256 || command.Occurrences.Count is < 1 or > 1024)
            throw Failure("preview_command_count_rejected");
        if (!command.Sources.Select(source => source.SourcePartId)
            .SequenceEqual(command.Sources.Select(source => source.SourcePartId).OrderBy(value => value, StringComparer.Ordinal), StringComparer.Ordinal)
            || command.Sources.Select(source => source.SourcePartId).Distinct(StringComparer.Ordinal).Count() != command.Sources.Count
            || command.Sources.Select(source => source.InputSlot).Distinct(StringComparer.Ordinal).Count() != command.Sources.Count)
            throw Failure("preview_source_order_rejected");
        var sourceIds = command.Sources.Select(source => source.SourcePartId).ToHashSet(StringComparer.Ordinal);
        foreach (var source in command.Sources)
        {
            _ = NormalizeDigest(source.ExpectedDigest);
            if (source.ExpectedByteLength is < 1 or > MaximumStepBytes) throw Failure("preview_source_length_rejected");
        }
        if (!command.Occurrences.Select(occurrence => occurrence.EntityId)
            .SequenceEqual(command.Occurrences.Select(occurrence => occurrence.EntityId).OrderBy(value => value, StringComparer.Ordinal), StringComparer.Ordinal)
            || command.Occurrences.Select(occurrence => occurrence.EntityId).Distinct(StringComparer.Ordinal).Count() != command.Occurrences.Count)
            throw Failure("preview_occurrence_order_rejected");
        var occurrenceIds = command.Occurrences.Select(occurrence => occurrence.EntityId).ToHashSet(StringComparer.Ordinal);
        var usedSources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var occurrence in command.Occurrences)
        {
            if (!sourceIds.Contains(occurrence.SourcePartId)
                || occurrence.ParentEntityId is not null && !occurrenceIds.Contains(occurrence.ParentEntityId)
                || occurrence.Transform.Count != 16
                || occurrence.Transform.Any(value => !double.IsFinite(value) || Math.Abs(value) > 1_000_000_000))
                throw Failure("preview_occurrence_rejected");
            usedSources.Add(occurrence.SourcePartId);
        }
        if (!usedSources.SetEquals(sourceIds)) throw Failure("preview_source_unreferenced");
    }

    private static void Exact(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Failure("object_required");
        var names = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Length != expected.Length || expected.Any(name => !names.Contains(name, StringComparer.Ordinal)))
            throw Failure("unexpected_protocol_member");
    }

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Failure("duplicate_protocol_member");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
        }
    }

    private static string String(JsonElement value, string name)
    {
        var item = value.GetProperty(name);
        return item.ValueKind == JsonValueKind.String && item.GetString() is { } result
            ? result
            : throw Failure($"{name}_invalid");
    }

    private static void RequireString(JsonElement value, string name, string expected)
    {
        if (!StringComparer.Ordinal.Equals(String(value, name), expected)) throw Failure($"{name}_mismatch");
    }

    private static void RequireBoolean(JsonElement value, string name, bool expected)
    {
        var item = value.GetProperty(name);
        if (item.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || item.GetBoolean() != expected)
            throw Failure($"{name}_mismatch");
    }

    private static long Integer(JsonElement value, string name, long minimum, long maximum) =>
        Integer(value.GetProperty(name), minimum, maximum);

    private static long Integer(JsonElement item, long minimum, long maximum)
    {
        if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt64(out var result) || result < minimum || result > maximum)
            throw Failure("invalid_integer");
        return result;
    }

    private static double Number(JsonElement value, string name, double minimum, double maximum) =>
        Number(value.GetProperty(name), minimum, maximum);

    private static double Number(JsonElement item, double minimum, double maximum)
    {
        if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out var result)
            || !double.IsFinite(result) || result < minimum || result > maximum)
            throw Failure("invalid_number");
        return result;
    }

    private static void SameNumber(JsonElement value, string name, double expected)
    {
        var actual = Number(value, name, double.MinValue, double.MaxValue);
        if (BitConverter.DoubleToInt64Bits(actual) != BitConverter.DoubleToInt64Bits(expected))
            throw Failure($"{name}_mismatch");
    }

    private static byte[] Bounded(byte[] payload, int maximum, string code)
    {
        if (payload.Length <= 0 || payload.Length > maximum) throw Failure(code);
        return payload;
    }

    private static InvalidDataException Failure(string code) => new(code);
}
