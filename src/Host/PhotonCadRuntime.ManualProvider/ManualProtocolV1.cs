using System.Text;
using System.Text.Json;
using PhotonCadRuntime.IndustrialProvider;

namespace PhotonCadRuntime.ManualProvider;

internal static class ManualProtocolV1
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static byte[] Serialize(
        PhotonCadManualCommand command,
        IndustrialPreviewSource? source,
        ManualReplayFeature? replay = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", ProtocolV1.RequestSchema);
            switch (command.Kind)
            {
                case PhotonCadManualOperationKind.SketchExtrudeAdd:
                    writer.WriteString("operation", "manualSketchExtrudeAdd");
                    if (!command.CreatesEntity) WriteSource(writer, source);
                    WriteAnyProfile(writer, command.Execution);
                    writer.WriteNumber("depthMm", SketchDepth(command.Execution));
                    break;
                case PhotonCadManualOperationKind.SketchExtrudeCut:
                    writer.WriteString("operation", "manualSketchExtrudeCut");
                    WriteSource(writer, source);
                    WriteAnyProfile(writer, command.Execution);
                    writer.WriteNumber("depthMm", SketchDepth(command.Execution));
                    break;
                case PhotonCadManualOperationKind.HoleCut:
                    writer.WriteString("operation", "manualHoleCut");
                    WriteSource(writer, source);
                    var hole = command.Execution as ManualHoleParameters
                        ?? throw Failure("manual_hole_parameters_missing");
                    writer.WriteStartObject("hole");
                    writer.WriteNumber("radiusMm", hole.RadiusMm);
                    writer.WriteNumber("depthMm", hole.DepthMm);
                    writer.WriteNumber("xMm", hole.XMm);
                    writer.WriteNumber("yMm", hole.YMm);
                    writer.WriteNumber("zMm", hole.ZMm);
                    writer.WriteEndObject();
                    break;
                case PhotonCadManualOperationKind.LinearPattern:
                case PhotonCadManualOperationKind.CircularPattern:
                    WriteSource(writer, source);
                    var pattern = command.Execution as ManualPatternParameters
                        ?? throw Failure("manual_pattern_parameters_missing");
                    writer.WriteString("operation", command.Kind == PhotonCadManualOperationKind.LinearPattern
                        ? "manualLinearPattern" : "manualCircularPattern");
                    WriteReplayFeature(writer, replay);
                    writer.WriteNumber("count", pattern.Count);
                    if (command.Kind == PhotonCadManualOperationKind.LinearPattern)
                        writer.WriteNumber("spacingMm", pattern.SpacingMm);
                    else
                        writer.WriteNumber("angleDegrees", pattern.AngleDegrees);
                    break;
                default:
                    throw Failure("manual_operation_not_installed");
            }
            writer.WriteEndObject();
        }
        var bytes = stream.ToArray();
        if (bytes.Length is <= 0 or > ProtocolV1.MaximumRequestBytes)
            throw Failure("manual_request_size_rejected");
        return bytes;
    }

    private static void WriteReplayFeature(Utf8JsonWriter writer, ManualReplayFeature? replay)
    {
        if (replay is null) throw Failure("manual_pattern_seed_missing");
        writer.WriteStartObject("seed");
        switch (replay.Kind)
        {
            case PhotonCadManualOperationKind.SketchExtrudeCut:
                writer.WriteString("kind", "sketchExtrudeCut");
                WriteProfile(writer, replay.Parameters as ManualSketchParameters
                    ?? throw Failure("manual_pattern_seed_invalid"));
                writer.WriteNumber("depthMm", ((ManualSketchParameters)replay.Parameters).DepthMm);
                break;
            case PhotonCadManualOperationKind.HoleCut:
                writer.WriteString("kind", "holeCut");
                var hole = replay.Parameters as ManualHoleParameters
                    ?? throw Failure("manual_pattern_seed_invalid");
                writer.WriteNumber("radiusMm", hole.RadiusMm);
                writer.WriteNumber("depthMm", hole.DepthMm);
                writer.WriteNumber("xMm", hole.XMm);
                writer.WriteNumber("yMm", hole.YMm);
                writer.WriteNumber("zMm", hole.ZMm);
                break;
            default:
                throw Failure("manual_pattern_seed_kind_unsupported");
        }
        writer.WriteEndObject();
    }

    internal static IndustrialPrimitiveResponse ParseResponse(
        ReadOnlyMemory<byte> payload,
        PhotonCadManualCommand command)
    {
        using var document = Parse(payload);
        var root = document.RootElement;
        Exact(root, "schema", "ok", "operation", "artifact", "measurement", "provenance");
        RequireString(root, "schema", ProtocolV1.ResponseSchema);
        RequireBoolean(root, "ok", true);
        RequireString(root, "operation", Operation(command.Kind));
        var artifact = root.GetProperty("artifact");
        Exact(artifact, "format", "contentDigest", "byteLength");
        RequireString(artifact, "format", "step");
        var digest = String(artifact, "contentDigest");
        _ = ProtocolV1.NormalizeDigest(digest);
        var byteLength = Integer(artifact, "byteLength", 1, ProtocolV1.MaximumStepBytes);
        var measurement = root.GetProperty("measurement");
        Exact(measurement, "units", "volumeMm3", "solidCount", "bounds");
        RequireString(measurement, "units", "millimeter");
        var volume = Number(measurement, "volumeMm3", double.Epsilon, double.MaxValue);
        if (Integer(measurement, "solidCount", 1, 1) != 1)
            throw Failure("manual_solid_count_invalid");
        var bounds = Bounds(measurement.GetProperty("bounds"));
        ValidateProvenance(root.GetProperty("provenance"), command);
        return new IndustrialPrimitiveResponse(
            new IndustrialArtifactClaim("step", digest, byteLength),
            bounds,
            volume);
    }

    private static void WriteSource(Utf8JsonWriter writer, IndustrialPreviewSource? source)
    {
        if (source is null) throw Failure("manual_source_missing");
        writer.WriteStartObject("source");
        writer.WriteString("inputSlot", source.InputSlot);
        writer.WriteString("expectedDigest", source.ExpectedDigest);
        writer.WriteEndObject();
    }

    private static void WriteProfile(Utf8JsonWriter writer, ManualSketchParameters profile)
    {
        writer.WriteStartObject("profile");
        writer.WriteString("kind", profile.ProfileKind);
        writer.WriteString("plane", profile.Plane);
        if (StringComparer.Ordinal.Equals(profile.ProfileKind, "rectangle"))
        {
            writer.WriteNumber("widthMm", profile.WidthMm);
            writer.WriteNumber("heightMm", profile.HeightMm);
        }
        else
        {
            writer.WriteNumber("radiusMm", profile.RadiusMm);
        }
        writer.WriteEndObject();
    }

    private static void WriteAnyProfile(Utf8JsonWriter writer, ManualExecutionParameters execution)
    {
        if (execution is ManualSketchParameters sketch)
        {
            WriteProfile(writer, sketch);
            return;
        }
        if (execution is not ManualMouseSketchParameters mouse) throw Failure("manual_sketch_parameters_missing");
        var mouseSketch = mouse.Sketch;
        writer.WriteStartObject("profile");
        writer.WriteString("kind", mouseSketch.ProfileKind);
        WriteVector(writer, "originMm", mouseSketch.OriginXMm, mouseSketch.OriginYMm, mouseSketch.OriginZMm);
        WriteVector(writer, "xDirection", mouseSketch.XDirectionX, mouseSketch.XDirectionY, mouseSketch.XDirectionZ);
        WriteVector(writer, "normal", mouseSketch.NormalX, mouseSketch.NormalY, mouseSketch.NormalZ);
        writer.WriteStartArray("points");
        foreach (var point in mouseSketch.Points)
        {
            writer.WriteStartObject();
            writer.WriteNumber("x", point.XMm);
            writer.WriteNumber("y", point.YMm);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        if (StringComparer.Ordinal.Equals(mouseSketch.ProfileKind, "filletedPolygon"))
        {
            writer.WriteStartArray("cornerRadiiMm");
            foreach (var radius in mouseSketch.CornerRadiiMm) writer.WriteNumberValue(radius);
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }

    private static void WriteVector(Utf8JsonWriter writer, string property, double x, double y, double z)
    {
        writer.WriteStartObject(property);
        writer.WriteNumber("x", x);
        writer.WriteNumber("y", y);
        writer.WriteNumber("z", z);
        writer.WriteEndObject();
    }

    private static double SketchDepth(ManualExecutionParameters execution) => execution switch
    {
        ManualSketchParameters sketch => sketch.DepthMm,
        ManualMouseSketchParameters mouse => mouse.DepthMm,
        _ => throw Failure("manual_sketch_parameters_missing"),
    };

    private static ManualSketchParameters RequireSketch(PhotonCadManualCommand command) =>
        command.Execution as ManualSketchParameters
        ?? throw Failure("manual_sketch_parameters_missing");

    private static void ValidateProvenance(JsonElement provenance, PhotonCadManualCommand command)
    {
        RequireString(provenance, "generator", "manual");
        RequireString(provenance, "kind", command.Kind switch
        {
            PhotonCadManualOperationKind.SketchExtrudeAdd => "sketchExtrudeAdd",
            PhotonCadManualOperationKind.SketchExtrudeCut => "sketchExtrudeCut",
            PhotonCadManualOperationKind.HoleCut => "holeCut",
            PhotonCadManualOperationKind.LinearPattern => "linearPattern",
            PhotonCadManualOperationKind.CircularPattern => "circularPattern",
            _ => throw Failure("manual_operation_not_installed"),
        });
        switch (command.Kind)
        {
            case PhotonCadManualOperationKind.SketchExtrudeAdd:
                Exact(provenance, command.CreatesEntity
                    ? ["generator", "kind", "profile", "depthMm"]
                    : ["generator", "kind", "profile", "depthMm", "sourceVolumeMm3"]);
                ValidateAnyProfile(provenance.GetProperty("profile"), command.Execution);
                RequireExactNumber(provenance, "depthMm", SketchDepth(command.Execution));
                if (!command.CreatesEntity) _ = Number(provenance, "sourceVolumeMm3", double.Epsilon, double.MaxValue);
                break;
            case PhotonCadManualOperationKind.SketchExtrudeCut:
                Exact(provenance, "generator", "kind", "profile", "depthMm", "sourceVolumeMm3");
                ValidateAnyProfile(provenance.GetProperty("profile"), command.Execution);
                RequireExactNumber(provenance, "depthMm", SketchDepth(command.Execution));
                _ = Number(provenance, "sourceVolumeMm3", double.Epsilon, double.MaxValue);
                break;
            case PhotonCadManualOperationKind.HoleCut:
                Exact(provenance, "generator", "kind", "hole", "sourceVolumeMm3");
                var expected = command.Execution as ManualHoleParameters
                    ?? throw Failure("manual_hole_parameters_missing");
                var hole = provenance.GetProperty("hole");
                Exact(hole, "depthMm", "radiusMm", "xMm", "yMm", "zMm");
                RequireExactNumber(hole, "depthMm", expected.DepthMm);
                RequireExactNumber(hole, "radiusMm", expected.RadiusMm);
                RequireExactNumber(hole, "xMm", expected.XMm);
                RequireExactNumber(hole, "yMm", expected.YMm);
                RequireExactNumber(hole, "zMm", expected.ZMm);
                _ = Number(provenance, "sourceVolumeMm3", double.Epsilon, double.MaxValue);
                break;
            case PhotonCadManualOperationKind.LinearPattern:
                Exact(provenance, "generator", "kind", "seedKind", "count", "spacingMm", "sourceVolumeMm3");
                RequirePatternProvenance(provenance, command, "linearPattern", "spacingMm");
                break;
            case PhotonCadManualOperationKind.CircularPattern:
                Exact(provenance, "generator", "kind", "seedKind", "count", "angleDegrees", "sourceVolumeMm3");
                RequirePatternProvenance(provenance, command, "circularPattern", "angleDegrees");
                break;
        }
    }

    private static void RequirePatternProvenance(
        JsonElement provenance,
        PhotonCadManualCommand command,
        string kind,
        string distanceField)
    {
        var pattern = command.Execution as ManualPatternParameters
            ?? throw Failure("manual_pattern_parameters_missing");
        RequireString(provenance, "kind", kind);
        _ = String(provenance, "seedKind");
        if (Integer(provenance, "count", 2, 256) != pattern.Count)
            throw Failure("manual_response_parameter_mismatch");
        RequireExactNumber(provenance, distanceField,
            command.Kind == PhotonCadManualOperationKind.LinearPattern ? pattern.SpacingMm : pattern.AngleDegrees);
        _ = Number(provenance, "sourceVolumeMm3", double.Epsilon, double.MaxValue);
    }

    private static void ValidateProfile(JsonElement value, ManualSketchParameters expected)
    {
        if (StringComparer.Ordinal.Equals(expected.ProfileKind, "rectangle"))
        {
            Exact(value, "heightMm", "kind", "plane", "widthMm");
            RequireExactNumber(value, "heightMm", expected.HeightMm);
            RequireExactNumber(value, "widthMm", expected.WidthMm);
        }
        else
        {
            Exact(value, "kind", "plane", "radiusMm");
            RequireExactNumber(value, "radiusMm", expected.RadiusMm);
        }
        RequireString(value, "kind", expected.ProfileKind);
        RequireString(value, "plane", expected.Plane);
    }

    private static void ValidateAnyProfile(JsonElement value, ManualExecutionParameters execution)
    {
        if (execution is ManualSketchParameters sketch)
        {
            ValidateProfile(value, sketch);
            return;
        }
        if (execution is not ManualMouseSketchParameters mouse) throw Failure("manual_sketch_parameters_missing");
        var expected = mouse.Sketch;
        Exact(value, StringComparer.Ordinal.Equals(expected.ProfileKind, "filletedPolygon")
            ? ["cornerRadiiMm", "kind", "normal", "originMm", "points", "xDirection"]
            : ["kind", "normal", "originMm", "points", "xDirection"]);
        RequireString(value, "kind", expected.ProfileKind);
        ValidateVector(value.GetProperty("originMm"), expected.OriginXMm, expected.OriginYMm, expected.OriginZMm);
        ValidateVector(value.GetProperty("xDirection"), expected.XDirectionX, expected.XDirectionY, expected.XDirectionZ);
        ValidateVector(value.GetProperty("normal"), expected.NormalX, expected.NormalY, expected.NormalZ);
        var points = value.GetProperty("points");
        if (points.ValueKind != JsonValueKind.Array || points.GetArrayLength() != expected.Points.Count)
            throw Failure("manual_response_parameter_mismatch");
        var index = 0;
        foreach (var point in points.EnumerateArray())
        {
            Exact(point, "x", "y");
            RequireExactNumber(point, "x", expected.Points[index].XMm);
            RequireExactNumber(point, "y", expected.Points[index].YMm);
            index++;
        }
        if (StringComparer.Ordinal.Equals(expected.ProfileKind, "filletedPolygon"))
        {
            var radii = value.GetProperty("cornerRadiiMm");
            if (radii.ValueKind != JsonValueKind.Array || radii.GetArrayLength() != expected.CornerRadiiMm.Count)
                throw Failure("manual_response_parameter_mismatch");
            var radiusIndex = 0;
            foreach (var radius in radii.EnumerateArray())
            {
                if (radius.ValueKind != JsonValueKind.Number || !radius.TryGetDouble(out var millimeters)
                    || !double.IsFinite(millimeters) || Math.Abs(millimeters - expected.CornerRadiiMm[radiusIndex]) > 0.000000001)
                    throw Failure("manual_response_parameter_mismatch");
                radiusIndex++;
            }
        }
    }

    private static void ValidateVector(JsonElement value, double x, double y, double z)
    {
        Exact(value, "x", "y", "z");
        RequireExactNumber(value, "x", x);
        RequireExactNumber(value, "y", y);
        RequireExactNumber(value, "z", z);
    }

    private static JsonDocument Parse(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length is <= 0 or > ProtocolV1.MaximumResponseBytes)
            throw Failure("manual_response_size_rejected");
        try
        {
            _ = StrictUtf8.GetString(payload.Span);
            var document = JsonDocument.Parse(payload, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            RejectDuplicates(document.RootElement);
            return document;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("manual_response_invalid_json", exception);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("manual_response_invalid_utf8", exception);
        }
    }

    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Failure("manual_response_duplicate_member");
                RejectDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
        }
    }

    private static string Operation(PhotonCadManualOperationKind kind) => kind switch
    {
        PhotonCadManualOperationKind.SketchExtrudeAdd => "manualSketchExtrudeAdd",
        PhotonCadManualOperationKind.SketchExtrudeCut => "manualSketchExtrudeCut",
        PhotonCadManualOperationKind.HoleCut => "manualHoleCut",
        PhotonCadManualOperationKind.LinearPattern => "manualLinearPattern",
        PhotonCadManualOperationKind.CircularPattern => "manualCircularPattern",
        _ => throw Failure("manual_operation_not_installed"),
    };

    private static IndustrialBounds Bounds(JsonElement value)
    {
        Exact(value, "minimum", "maximum");
        var minimum = Vector(value.GetProperty("minimum"));
        var maximum = Vector(value.GetProperty("maximum"));
        if (minimum[0] > maximum[0] || minimum[1] > maximum[1] || minimum[2] > maximum[2])
            throw Failure("manual_bounds_invalid");
        return new IndustrialBounds(minimum[0], minimum[1], minimum[2], maximum[0], maximum[1], maximum[2]);
    }

    private static double[] Vector(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 3)
            throw Failure("manual_vector_invalid");
        return value.EnumerateArray().Select(item => Number(item, -1_000_000_000, 1_000_000_000)).ToArray();
    }

    private static void Exact(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Failure("manual_response_object_required");
        var actual = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != expected.Length || expected.Any(name => !actual.Contains(name, StringComparer.Ordinal)))
            throw Failure("manual_response_member_mismatch");
    }

    private static string String(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String
            || property.GetString() is not { } result)
            throw Failure("manual_response_string_invalid");
        return result;
    }

    private static void RequireString(JsonElement value, string name, string expected)
    {
        if (!StringComparer.Ordinal.Equals(String(value, name), expected))
            throw Failure("manual_response_value_mismatch");
    }

    private static void RequireBoolean(JsonElement value, string name, bool expected)
    {
        if (!value.TryGetProperty(name, out var property)
            || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || property.GetBoolean() != expected)
            throw Failure("manual_response_boolean_invalid");
    }

    private static long Integer(JsonElement value, string name, long minimum, long maximum)
    {
        if (!value.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt64(out var result)
            || result < minimum || result > maximum)
            throw Failure("manual_response_integer_invalid");
        return result;
    }

    private static double Number(JsonElement value, string name, double minimum, double maximum) =>
        value.TryGetProperty(name, out var property)
            ? Number(property, minimum, maximum)
            : throw Failure("manual_response_number_missing");

    private static double Number(JsonElement value, double minimum, double maximum)
    {
        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var result)
            || !double.IsFinite(result)
            || result < minimum || result > maximum)
            throw Failure("manual_response_number_invalid");
        return result;
    }

    private static void RequireExactNumber(JsonElement value, string name, double expected)
    {
        var actual = Number(value, name, -1_000_000_000, 1_000_000_000);
        if (BitConverter.DoubleToInt64Bits(actual) != BitConverter.DoubleToInt64Bits(expected))
            throw Failure("manual_response_parameter_mismatch");
    }

    private static InvalidDataException Failure(string code) => new(code);
}
