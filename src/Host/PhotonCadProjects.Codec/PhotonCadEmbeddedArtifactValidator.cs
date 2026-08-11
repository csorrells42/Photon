using System.Buffers.Binary;
using System.Text.Json;

namespace PhotonCadProjects.Codec;

internal static class PhotonCadEmbeddedArtifactValidator
{
    private static ReadOnlySpan<byte> StepStart => "ISO-10303-21;"u8;
    private static ReadOnlySpan<byte> StepHeader => "HEADER;"u8;
    private static ReadOnlySpan<byte> StepData => "DATA;"u8;
    private static ReadOnlySpan<byte> StepEndSection => "ENDSEC;"u8;
    private static ReadOnlySpan<byte> StepEnd => "END-ISO-10303-21;"u8;

    internal static void Validate(PhotonCadArtifactKindV1 kind, ReadOnlySpan<byte> content)
    {
        switch (kind)
        {
            case PhotonCadArtifactKindV1.Step:
                ValidateStep(content);
                break;
            case PhotonCadArtifactKindV1.Glb:
                ValidateGlb(content);
                break;
            default:
                throw PhotonCadFileGuardsV1.Failure("unsupported_artifact_kind", "artifact");
        }
    }

    private static void ValidateStep(ReadOnlySpan<byte> content)
    {
        if (!content.StartsWith(StepStart)) throw PhotonCadFileGuardsV1.Failure("invalid_step_initial_marker", "artifact");
        ValidateStepBytes(content);
        var cursor = StepStart.Length;
        cursor = FindStepMarker(content, cursor, StepHeader);
        cursor = FindStepMarker(content, cursor, StepEndSection);
        cursor = FindStepMarker(content, cursor, StepData);
        cursor = FindStepMarker(content, cursor, StepEndSection);
        cursor = FindStepMarker(content, cursor, StepEnd);
        for (var index = cursor; index < content.Length; index++)
        {
            if (content[index] is not (0x09 or 0x0a or 0x0d or 0x20))
                throw PhotonCadFileGuardsV1.Failure("step_appended_content", "artifact");
        }
    }

    private static void ValidateStepBytes(ReadOnlySpan<byte> content)
    {
        var quoted = false;
        var comment = false;
        for (var index = 0; index < content.Length; index++)
        {
            var value = content[index];
            if (value < 0x20 && value is not (0x09 or 0x0a or 0x0d))
                throw PhotonCadFileGuardsV1.Failure("invalid_step_character", "artifact");
            if (comment)
            {
                if (value == (byte)'*' && index + 1 < content.Length && content[index + 1] == (byte)'/')
                {
                    comment = false;
                    index++;
                }
                continue;
            }
            if (quoted)
            {
                if (value == (byte)'\'' && index + 1 < content.Length && content[index + 1] == (byte)'\'') index++;
                else if (value == (byte)'\'') quoted = false;
                continue;
            }
            if (value == (byte)'/' && index + 1 < content.Length && content[index + 1] == (byte)'*')
            {
                comment = true;
                index++;
            }
            else if (value == (byte)'\'') quoted = true;
        }
        if (quoted || comment) throw PhotonCadFileGuardsV1.Failure("unterminated_step_lexical_item", "artifact");
    }

    private static int FindStepMarker(ReadOnlySpan<byte> content, int start, ReadOnlySpan<byte> marker)
    {
        var quoted = false;
        var comment = false;
        for (var index = start; index <= content.Length - marker.Length; index++)
        {
            var value = content[index];
            if (comment)
            {
                if (value == (byte)'*' && index + 1 < content.Length && content[index + 1] == (byte)'/')
                {
                    comment = false;
                    index++;
                }
                continue;
            }
            if (quoted)
            {
                if (value == (byte)'\'' && index + 1 < content.Length && content[index + 1] == (byte)'\'') index++;
                else if (value == (byte)'\'') quoted = false;
                continue;
            }
            if (value == (byte)'/' && index + 1 < content.Length && content[index + 1] == (byte)'*')
            {
                comment = true;
                index++;
                continue;
            }
            if (value == (byte)'\'')
            {
                quoted = true;
                continue;
            }
            if (content[index..].StartsWith(marker)) return index + marker.Length;
        }
        throw PhotonCadFileGuardsV1.Failure("step_marker_missing", "artifact");
    }

    private static void ValidateGlb(ReadOnlySpan<byte> content)
    {
        if (content.Length < 20
            || BinaryPrimitives.ReadUInt32LittleEndian(content) != 0x46546c67
            || BinaryPrimitives.ReadUInt32LittleEndian(content[4..]) != 2
            || BinaryPrimitives.ReadUInt32LittleEndian(content[8..]) != content.Length)
            throw PhotonCadFileGuardsV1.Failure("invalid_glb_header", "artifact");

        var cursor = 12;
        var chunk = 0;
        var jsonSeen = false;
        var binSeen = false;
        while (cursor < content.Length)
        {
            if (content.Length - cursor < 8) throw PhotonCadFileGuardsV1.Failure("truncated_glb_chunk", "artifact");
            var length = BinaryPrimitives.ReadUInt32LittleEndian(content[cursor..]);
            var kind = BinaryPrimitives.ReadUInt32LittleEndian(content[(cursor + 4)..]);
            if ((length & 3) != 0) throw PhotonCadFileGuardsV1.Failure("unaligned_glb_chunk", "artifact");
            cursor = checked(cursor + 8);
            if (length > int.MaxValue || length > content.Length - cursor)
                throw PhotonCadFileGuardsV1.Failure("invalid_glb_chunk_length", "artifact");
            var payload = content.Slice(cursor, checked((int)length));
            cursor = checked(cursor + (int)length);
            if (kind == 0x4e4f534a)
            {
                if (chunk != 0 || jsonSeen) throw PhotonCadFileGuardsV1.Failure("invalid_glb_json_chunk", "artifact");
                jsonSeen = true;
                ValidateGlbJson(payload);
            }
            else if (kind == 0x004e4942)
            {
                if (!jsonSeen || binSeen) throw PhotonCadFileGuardsV1.Failure("invalid_glb_bin_chunk", "artifact");
                binSeen = true;
            }
            else
            {
                throw PhotonCadFileGuardsV1.Failure("unsupported_glb_chunk", "artifact");
            }
            chunk++;
        }
        if (!jsonSeen || cursor != content.Length) throw PhotonCadFileGuardsV1.Failure("invalid_glb_layout", "artifact");
    }

    private static void ValidateGlbJson(ReadOnlySpan<byte> payload)
    {
        var end = payload.Length;
        while (end > 0 && payload[end - 1] is 0x20 or 0x00) end--;
        if (end == 0) throw PhotonCadFileGuardsV1.Failure("empty_glb_json", "artifact");
        try
        {
            using var document = JsonDocument.Parse(payload[..end].ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw PhotonCadFileGuardsV1.Failure("invalid_glb_json", "artifact");
            RejectUris(document.RootElement);
        }
        catch (JsonException exception)
        {
            throw new PhotonCadProjectException("invalid_glb_json", "artifact", exception);
        }
    }

    private static void RejectUris(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    if (StringComparer.Ordinal.Equals(property.Name, "uri"))
                        throw PhotonCadFileGuardsV1.Failure("external_glb_resource", "artifact");
                    RejectUris(property.Value);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray()) RejectUris(item);
                break;
        }
    }
}
