using System.Buffers.Binary;
using System.Text.Json;

namespace PhotonCadRuntime.IndustrialProvider;

internal static class GlbValidator
{
    private const uint GlbMagic = 0x46546C67;
    private const uint JsonChunk = 0x4E4F534A;
    private const uint BinChunk = 0x004E4942;

    internal static void Validate(ReadOnlySpan<byte> bytes, IndustrialPreviewCommand command, IndustrialBounds claimedBounds)
    {
        if (bytes.Length < 28 || bytes.Length > ProtocolV1.MaximumGlbBytes
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != GlbMagic
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != 2
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) != bytes.Length)
            throw Failure("glb_header_invalid");
        var offset = 12;
        var json = ReadChunk(bytes, ref offset, JsonChunk);
        var binary = ReadChunk(bytes, ref offset, BinChunk);
        if (offset != bytes.Length || binary.Length == 0) throw Failure("glb_chunk_layout_invalid");
        using var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64,
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw Failure("glb_document_invalid");
        RejectDuplicateMembers(root);
        foreach (var forbidden in new[] { "animations", "images", "skins", "textures", "extensionsUsed", "extensionsRequired" })
        {
            if (root.TryGetProperty(forbidden, out _)) throw Failure("glb_forbidden_member");
        }
        RejectUris(root);
        var asset = root.GetProperty("asset");
        if (asset.GetProperty("version").GetString() != "2.0") throw Failure("glb_version_invalid");
        if (root.GetProperty("scene").GetInt32() != 0) throw Failure("glb_scene_invalid");
        var scenes = root.GetProperty("scenes");
        if (scenes.GetArrayLength() != 1) throw Failure("glb_scene_invalid");
        var roots = scenes[0].GetProperty("nodes");
        var nodes = root.GetProperty("nodes");
        if (nodes.GetArrayLength() != command.Occurrences.Count) throw Failure("glb_entity_count_mismatch");
        var occurrenceIndexes = command.Occurrences
            .Select((occurrence, index) => (occurrence.EntityId, Index: index))
            .ToDictionary(value => value.EntityId, value => value.Index, StringComparer.Ordinal);
        var expectedRoots = command.Occurrences
            .Select((occurrence, index) => (occurrence, index))
            .Where(value => value.occurrence.ParentEntityId is null)
            .Select(value => value.index)
            .ToArray();
        if (roots.GetArrayLength() != expectedRoots.Length
            || !roots.EnumerateArray().Select(value => value.GetInt32()).SequenceEqual(expectedRoots))
            throw Failure("glb_root_set_mismatch");
        var sourceIndexes = command.Sources
            .Select((source, index) => (source.SourcePartId, Index: index))
            .ToDictionary(value => value.SourcePartId, value => value.Index, StringComparer.Ordinal);
        for (var index = 0; index < command.Occurrences.Count; index++)
        {
            var expected = command.Occurrences[index];
            var node = nodes[index];
            var extras = node.GetProperty("extras");
            if (extras.GetProperty("photonEntityId").GetString() != expected.EntityId
                || node.GetProperty("mesh").GetInt32() != sourceIndexes[expected.SourcePartId])
                throw Failure("glb_entity_identity_mismatch");
            var matrix = node.GetProperty("matrix");
            if (matrix.GetArrayLength() != 16) throw Failure("glb_transform_invalid");
            for (var column = 0; column < 4; column++)
            {
                for (var row = 0; row < 4; row++)
                {
                    var glbIndex = column * 4 + row;
                    var expectedValue = expected.Transform[row * 4 + column];
                    var actual = matrix[glbIndex].GetDouble();
                    if (!double.IsFinite(actual)
                        || BitConverter.DoubleToInt64Bits(actual) != BitConverter.DoubleToInt64Bits(expectedValue))
                        throw Failure("glb_transform_mismatch");
                }
            }
            var expectedChildren = command.Occurrences
                .Select((occurrence, childIndex) => (occurrence, childIndex))
                .Where(value => StringComparer.Ordinal.Equals(value.occurrence.ParentEntityId, expected.EntityId))
                .Select(value => value.childIndex)
                .ToArray();
            if (expectedChildren.Length == 0)
            {
                if (node.TryGetProperty("children", out _)) throw Failure("glb_children_mismatch");
            }
            else
            {
                var children = node.GetProperty("children");
                if (children.GetArrayLength() != expectedChildren.Length
                    || !children.EnumerateArray().Select(value => value.GetInt32()).SequenceEqual(expectedChildren)
                    || children.EnumerateArray().Any(value => !occurrenceIndexes.ContainsValue(value.GetInt32())))
                    throw Failure("glb_children_mismatch");
            }
        }
        var meshes = root.GetProperty("meshes");
        if (meshes.GetArrayLength() != command.Sources.Count) throw Failure("glb_source_count_mismatch");
        var buffers = root.GetProperty("buffers");
        if (buffers.GetArrayLength() != 1 || buffers[0].TryGetProperty("uri", out _))
            throw Failure("glb_external_buffer_rejected");
        var declaredBinaryLength = buffers[0].GetProperty("byteLength").GetInt64();
        if (declaredBinaryLength < 1 || declaredBinaryLength > binary.Length) throw Failure("glb_binary_length_invalid");
        ValidateBounds(claimedBounds);
    }

    private static ReadOnlySpan<byte> ReadChunk(ReadOnlySpan<byte> bytes, ref int offset, uint expectedType)
    {
        if (offset > bytes.Length - 8) throw Failure("glb_chunk_missing");
        var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
        var type = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 4)..]);
        if (type != expectedType || length % 4 != 0 || length > int.MaxValue || offset + 8L + length > bytes.Length)
            throw Failure("glb_chunk_invalid");
        offset += 8;
        var result = bytes.Slice(offset, checked((int)length));
        offset += checked((int)length);
        return result;
    }

    private static void RejectUris(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (StringComparer.Ordinal.Equals(property.Name, "uri")) throw Failure("glb_uri_rejected");
                RejectUris(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) RejectUris(item);
        }
    }

    private static void RejectDuplicateMembers(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Failure("glb_duplicate_member");
                RejectDuplicateMembers(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) RejectDuplicateMembers(item);
        }
    }

    private static void ValidateBounds(IndustrialBounds bounds)
    {
        var values = new[]
        {
            bounds.MinimumX, bounds.MinimumY, bounds.MinimumZ,
            bounds.MaximumX, bounds.MaximumY, bounds.MaximumZ,
        };
        if (values.Any(value => !double.IsFinite(value) || Math.Abs(value) > 1_000_000_000)
            || bounds.MinimumX > bounds.MaximumX || bounds.MinimumY > bounds.MaximumY || bounds.MinimumZ > bounds.MaximumZ)
            throw Failure("glb_bounds_invalid");
    }

    private static InvalidDataException Failure(string code) => new(code);
}
