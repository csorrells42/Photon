using PhotonCadProjects;
using PhotonCadProjects.Codec;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace PhotonCadVerification;

public sealed class PhotonCadCanonicalVerifier
{
    private readonly PhotonCadCanonicalProjectCodecV1 _codec = new();

    public async ValueTask<PhotonCadVerificationReport> VerifyAsync(
        Stream canonicalStream,
        PhotonCadVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(canonicalStream);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!canonicalStream.CanRead || !canonicalStream.CanSeek)
            return Failure("canonical_stream_not_seekable");
        long remaining;
        try { remaining = checked(canonicalStream.Length - canonicalStream.Position); }
        catch (Exception exception) when (exception is IOException or NotSupportedException or OverflowException)
        {
            return Failure("canonical_stream_unavailable");
        }
        if (remaining != request.ExpectedByteLength || remaining is < 1 or > PhotonCadVerificationContract.MaximumCanonicalBytes)
            return Failure("canonical_length_mismatch");
        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)remaining));
        var offset = 0;
        while (offset < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await canonicalStream.ReadAsync(bytes.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0) return Failure("canonical_truncated");
            offset += read;
        }
        if (canonicalStream.ReadByte() != -1) return Failure("canonical_appended_content");
        return VerifyOwned(bytes, request, cancellationToken);
    }

    public PhotonCadVerificationReport Verify(
        ReadOnlyMemory<byte> canonicalBytes,
        PhotonCadVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (canonicalBytes.Length != request.ExpectedByteLength
            || canonicalBytes.Length is < 1 or > PhotonCadVerificationContract.MaximumCanonicalBytes)
            return Failure("canonical_length_mismatch");
        return VerifyOwned(canonicalBytes.ToArray(), request, cancellationToken);
    }

    private PhotonCadVerificationReport VerifyOwned(
        ReadOnlyMemory<byte> canonicalBytes,
        PhotonCadVerificationRequest request,
        CancellationToken cancellationToken)
    {
        if (!FixedDigestEquals(Sha256(canonicalBytes.Span), request.ExpectedFileSha256))
            return Failure("canonical_file_digest_mismatch");

        PhotonCadCanonicalProject project;
        PhotonCadProjectStateV1 state;
        try
        {
            project = _codec.Decode(canonicalBytes);
            state = _codec.Inspect(project);
        }
        catch (Exception exception) when (exception is PhotonCadProjectException or InvalidDataException or JsonException or OverflowException)
        {
            return Failure("canonical_decode_rejected");
        }
        cancellationToken.ThrowIfCancellationRequested();

        var checks = new List<PhotonCadVerificationCheck>();
        Check(checks, "canonical_binding",
            FixedDigestEquals(project.ContentDigest, request.ExpectedContentDigest)
            && FixedDigestEquals(project.BomDigest, request.ExpectedBomDigest)
            && (!request.RequireClean || !project.Dirty),
            "canonical_binding_verified", "canonical_binding_mismatch", 1);

        VerifyAssemblyStructure(state, checks);
        VerifyTransforms(state, checks);
        VerifyOccurrenceCoverage(state, checks);
        VerifyBom(state, project, checks);
        VerifyEntityArtifactConsistency(state, checks);
        VerifyOperationProvenance(state, checks);
        VerifyArtifacts(state, checks);
        VerifyPreview(state, checks);
        VerifyStepExportReadiness(state, checks);

        checks.Add(new("solid_validity", PhotonCadVerificationStatus.Unavailable,
            "geometry_container_required", state.Artifacts.Count(value => value.Role == PhotonCadArtifactRoleV1.AuthoritativeGeometry)));
        checks.Add(new("interference", PhotonCadVerificationStatus.Unavailable,
            "geometry_container_required", state.Occurrences.Count));
        checks.Add(new("dimensional_conformance", PhotonCadVerificationStatus.Unavailable,
            "geometry_container_required", state.Entities.Count));

        var verified = checks.All(value => value.Status != PhotonCadVerificationStatus.Failed);
        var complete = checks.All(value => value.Status == PhotonCadVerificationStatus.Passed);
        return new PhotonCadVerificationReport(
            verified,
            complete,
            verified ? "canonical_checks_passed_geometry_checks_unavailable" : "canonical_verification_failed",
            project.ProjectId,
            project.Revision,
            checks);
    }

    private static void VerifyAssemblyStructure(PhotonCadProjectStateV1 state, List<PhotonCadVerificationCheck> checks)
    {
        var occurrences = state.Occurrences.ToDictionary(value => value.OccurrenceId, StringComparer.OrdinalIgnoreCase);
        var roots = state.Occurrences.Where(value => value.ParentOccurrenceId is null).ToArray();
        var valid = state.Occurrences.Count == 0 || roots.Length == 1;
        if (valid)
        {
            foreach (var occurrence in state.Occurrences)
            {
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var cursor = occurrence;
                while (cursor.ParentOccurrenceId is not null)
                {
                    if (!visited.Add(cursor.OccurrenceId)
                        || !occurrences.TryGetValue(cursor.ParentOccurrenceId, out cursor!))
                    {
                        valid = false;
                        break;
                    }
                }
                if (!valid) break;
            }
        }
        Check(checks, "assembly_structure", valid, "assembly_dag_verified", "assembly_dag_invalid", state.Occurrences.Count);
    }

    private static void VerifyTransforms(PhotonCadProjectStateV1 state, List<PhotonCadVerificationCheck> checks)
    {
        var valid = state.Occurrences.All(value => IsRigid(value.Transform));
        Check(checks, "rigid_transforms", valid, "rigid_transforms_verified", "rigid_transform_invalid", state.Occurrences.Count);
    }

    private static void VerifyOccurrenceCoverage(PhotonCadProjectStateV1 state, List<PhotonCadVerificationCheck> checks)
    {
        var geometryOwners = state.Artifacts
            .Where(value => value.Role == PhotonCadArtifactRoleV1.AuthoritativeGeometry)
            .Select(value => value.OwnerEntityId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sources = state.Occurrences.Select(value => value.SourceEntityId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var valid = sources.IsSubsetOf(geometryOwners)
            && state.Occurrences.All(value => geometryOwners.Contains(value.SourceEntityId));
        Check(checks, "occurrence_source_coverage", valid,
            "occurrence_sources_resolve_geometry", "occurrence_source_coverage_mismatch", sources.Count);
    }

    private static void VerifyBom(
        PhotonCadProjectStateV1 state,
        PhotonCadCanonicalProject project,
        List<PhotonCadVerificationCheck> checks)
    {
        var grouped = state.Occurrences.GroupBy(value => value.SourceEntityId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(value => value.Key, value => value.ToArray(), StringComparer.OrdinalIgnoreCase);
        var rowGroups = state.Bom.GroupBy(value => value.SourceEntityId, StringComparer.OrdinalIgnoreCase).ToArray();
        var valid = rowGroups.All(value => value.Count() == 1);
        var rows = valid
            ? rowGroups.ToDictionary(value => value.Key, value => value.Single(), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, PhotonCadBomRow>(StringComparer.OrdinalIgnoreCase);
        valid &= grouped.Count == rows.Count;
        foreach (var (source, occurrences) in grouped)
        {
            if (!rows.TryGetValue(source, out var row)
                || row.Unit != PhotonCadBomUnit.Each
                || BitConverter.DoubleToInt64Bits(row.Quantity) != BitConverter.DoubleToInt64Bits((double)occurrences.Length)
                || occurrences.Any(value => !StringComparer.Ordinal.Equals(value.PartNumber, row.PartNumber)))
            {
                valid = false;
                break;
            }
        }
        var digest = PhotonCadBomCanonicalizer.Compute(state.Units, state.Bom);
        valid &= FixedDigestEquals(digest, project.BomDigest);
        Check(checks, "bom_consistency", valid, "bom_recomputed_and_bound", "bom_recomputation_mismatch", state.Bom.Count);
    }

    private static void VerifyArtifacts(PhotonCadProjectStateV1 state, List<PhotonCadVerificationCheck> checks)
    {
        var valid = true;
        foreach (var artifact in state.Artifacts)
        {
            if (artifact.ByteLength != artifact.Content.Length || !FixedDigestEquals(Sha256(artifact.Content.Span), artifact.Digest))
            {
                valid = false;
                break;
            }
            try
            {
                if (artifact.Kind == PhotonCadArtifactKindV1.Step) ValidateStep(artifact.Content.Span);
                else if (artifact.Kind == PhotonCadArtifactKindV1.Glb) _ = ReadGlbJson(artifact.Content.Span);
                else valid = false;
            }
            catch (Exception exception) when (exception is InvalidDataException or JsonException or OverflowException)
            {
                valid = false;
                break;
            }
        }
        Check(checks, "sealed_artifacts", valid, "artifact_digest_length_media_verified", "artifact_binding_invalid", state.Artifacts.Count);
    }

    private static void VerifyEntityArtifactConsistency(
        PhotonCadProjectStateV1 state,
        List<PhotonCadVerificationCheck> checks)
    {
        var geometryEntities = state.Entities
            .Where(value => value.Kind is PhotonCadEntityKindV1.Body or PhotonCadEntityKindV1.Part or PhotonCadEntityKindV1.Assembly)
            .Select(value => value.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var owners = state.Artifacts
            .Where(value => value.Role == PhotonCadArtifactRoleV1.AuthoritativeGeometry)
            .Select(value => value.OwnerEntityId!)
            .ToArray();
        var valid = owners.Distinct(StringComparer.OrdinalIgnoreCase).Count() == owners.Length
            && geometryEntities.SetEquals(owners);
        Check(checks, "entity_artifact_consistency", valid,
            "geometry_entities_have_exact_step_owners", "geometry_entity_artifact_mismatch", geometryEntities.Count);
    }

    private static void VerifyOperationProvenance(
        PhotonCadProjectStateV1 state,
        List<PhotonCadVerificationCheck> checks)
    {
        var operations = state.Operations.ToDictionary(value => value.Id, StringComparer.OrdinalIgnoreCase);
        var valid = true;
        foreach (var artifact in state.Artifacts)
        {
            if (!operations.TryGetValue(artifact.Provenance.OperationId, out var operation)
                || operation.State != PhotonCadOperationStateV1.Applied
                || !StringComparer.Ordinal.Equals(operation.CapabilityId, artifact.Provenance.CapabilityId)
                || !SameSource(operation.Source, artifact.Provenance.Source))
            {
                valid = false;
                break;
            }
        }
        Check(checks, "operation_provenance_binding", valid,
            "artifact_operations_and_sources_bound", "artifact_operation_provenance_mismatch", state.Artifacts.Count);
    }

    private static void VerifyPreview(PhotonCadProjectStateV1 state, List<PhotonCadVerificationCheck> checks)
    {
        var preview = state.Artifacts.SingleOrDefault(value => value.Role == PhotonCadArtifactRoleV1.ProjectPreview);
        if (preview is null)
        {
            checks.Add(new("preview_entity_coverage", PhotonCadVerificationStatus.Unavailable,
                "project_preview_absent", state.Occurrences.Count));
            return;
        }
        try
        {
            using var json = ReadGlbJson(preview.Content.Span);
            var root = json.RootElement;
            var nodes = root.GetProperty("nodes");
            var actual = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var node in nodes.EnumerateArray())
            {
                var id = node.GetProperty("extras").GetProperty("photonEntityId").GetString();
                if (id is null || !actual.TryAdd(id, node)) throw new InvalidDataException("preview_entity_duplicate");
            }
            var expected = state.Occurrences.ToDictionary(value => value.OccurrenceId, StringComparer.Ordinal);
            var valid = actual.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expected.Keys);
            foreach (var (id, occurrence) in expected)
            {
                if (!actual.TryGetValue(id, out var node) || !MatrixMatchesGlb(node.GetProperty("matrix"), occurrence.Transform))
                {
                    valid = false;
                    break;
                }
            }
            Check(checks, "preview_entity_coverage", valid,
                "preview_entities_and_transforms_verified", "preview_entity_coverage_mismatch", actual.Count);
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Check(checks, "preview_entity_coverage", false,
                "preview_entities_and_transforms_verified", "preview_entity_coverage_mismatch", state.Occurrences.Count);
        }
    }

    private static void VerifyStepExportReadiness(PhotonCadProjectStateV1 state, List<PhotonCadVerificationCheck> checks)
    {
        var geometry = state.Artifacts.Where(value => value.Role == PhotonCadArtifactRoleV1.AuthoritativeGeometry).ToArray();
        var requiredOwners = state.Entities
            .Where(value => value.Visible && !value.Suppressed
                && value.Kind is PhotonCadEntityKindV1.Body or PhotonCadEntityKindV1.Part or PhotonCadEntityKindV1.Assembly)
            .Select(value => value.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actualOwners = geometry.Select(value => value.OwnerEntityId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var valid = geometry.Length > 0 && requiredOwners.SetEquals(actualOwners);
        Check(checks, "generic_step_export_readiness", valid,
            "sealed_generic_step_sources_ready", "generic_step_sources_incomplete", geometry.Length);
    }

    private static bool MatrixMatchesGlb(JsonElement matrix, IReadOnlyList<double> expected)
    {
        if (matrix.ValueKind != JsonValueKind.Array || matrix.GetArrayLength() != 16) return false;
        var values = matrix.EnumerateArray().Select(value => value.GetDouble()).ToArray();
        for (var column = 0; column < 4; column++)
        {
            for (var row = 0; row < 4; row++)
            {
                var actual = values[(column * 4) + row];
                if (!double.IsFinite(actual)
                    || BitConverter.DoubleToInt64Bits(actual) != BitConverter.DoubleToInt64Bits(expected[(row * 4) + column]))
                    return false;
            }
        }
        return true;
    }

    private static bool IsRigid(IReadOnlyList<double> matrix)
    {
        if (matrix.Count != 16 || matrix.Any(value => !double.IsFinite(value))
            || !Near(matrix[12], 0) || !Near(matrix[13], 0) || !Near(matrix[14], 0) || !Near(matrix[15], 1)) return false;
        for (var row = 0; row < 3; row++)
        {
            var length = 0d;
            for (var column = 0; column < 3; column++) length += matrix[(row * 4) + column] * matrix[(row * 4) + column];
            if (!Near(length, 1)) return false;
            for (var other = row + 1; other < 3; other++)
            {
                var dot = 0d;
                for (var column = 0; column < 3; column++) dot += matrix[(row * 4) + column] * matrix[(other * 4) + column];
                if (!Near(dot, 0)) return false;
            }
        }
        var determinant = matrix[0] * ((matrix[5] * matrix[10]) - (matrix[6] * matrix[9]))
            - matrix[1] * ((matrix[4] * matrix[10]) - (matrix[6] * matrix[8]))
            + matrix[2] * ((matrix[4] * matrix[9]) - (matrix[5] * matrix[8]));
        return Near(determinant, 1);
    }

    private static void ValidateStep(ReadOnlySpan<byte> content)
    {
        var end = content.Length;
        while (end > 0 && content[end - 1] is 0x09 or 0x0a or 0x0d or 0x20) end--;
        content = content[..end];
        if (!content.StartsWith("ISO-10303-21;"u8) || !content.EndsWith("END-ISO-10303-21;"u8))
            throw new InvalidDataException("step_envelope_invalid");
        var quoted = false;
        var comment = false;
        var header = false;
        var data = false;
        for (var index = 0; index < content.Length; index++)
        {
            var value = content[index];
            if (value < 0x20 && value is not (0x09 or 0x0a or 0x0d)) throw new InvalidDataException("step_character_invalid");
            if (comment)
            {
                if (value == '*' && index + 1 < content.Length && content[index + 1] == '/') { comment = false; index++; }
                continue;
            }
            if (quoted)
            {
                if (value == '\'' && index + 1 < content.Length && content[index + 1] == '\'') index++;
                else if (value == '\'') quoted = false;
                continue;
            }
            if (value == '/' && index + 1 < content.Length && content[index + 1] == '*') { comment = true; index++; continue; }
            if (value == '\'') { quoted = true; continue; }
            header |= content[index..].StartsWith("HEADER;"u8);
            data |= content[index..].StartsWith("DATA;"u8);
        }
        if (quoted || comment || !header || !data) throw new InvalidDataException("step_structure_invalid");
    }

    private static JsonDocument ReadGlbJson(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0x46546c67
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != 2
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) != bytes.Length)
            throw new InvalidDataException("glb_header_invalid");
        var offset = 12;
        if (offset > bytes.Length - 8) throw new InvalidDataException("glb_json_missing");
        var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
        var type = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 4)..]);
        if (type != 0x4e4f534a || (length & 3) != 0 || length > int.MaxValue || offset + 8L + length > bytes.Length)
            throw new InvalidDataException("glb_json_invalid");
        offset += 8;
        var json = bytes.Slice(offset, checked((int)length));
        offset += checked((int)length);
        while (offset < bytes.Length)
        {
            if (offset > bytes.Length - 8) throw new InvalidDataException("glb_chunk_invalid");
            var chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
            var chunkType = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 4)..]);
            if (chunkType != 0x004e4942 || (chunkLength & 3) != 0 || chunkLength > int.MaxValue || offset + 8L + chunkLength > bytes.Length)
                throw new InvalidDataException("glb_chunk_invalid");
            offset += checked(8 + (int)chunkLength);
        }
        var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64,
        });
        RejectExternalUris(document.RootElement);
        if (document.RootElement.GetProperty("asset").GetProperty("version").GetString() != "2.0")
        {
            document.Dispose();
            throw new InvalidDataException("glb_version_invalid");
        }
        return document;
    }

    private static void RejectExternalUris(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name) || StringComparer.Ordinal.Equals(property.Name, "uri"))
                    throw new InvalidDataException("glb_member_invalid");
                RejectExternalUris(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) RejectExternalUris(item);
        }
    }

    private static bool SameSource(PhotonCadSourceIdentityV1 left, PhotonCadSourceIdentityV1 right) =>
        StringComparer.Ordinal.Equals(left.Package, right.Package)
        && StringComparer.Ordinal.Equals(left.Version, right.Version)
        && FixedDigestEquals(left.Digest, right.Digest)
        && StringComparer.Ordinal.Equals(left.License, right.License);

    private static void Check(
        List<PhotonCadVerificationCheck> checks,
        string id,
        bool passed,
        string passReason,
        string failReason,
        long count) => checks.Add(new(id,
            passed ? PhotonCadVerificationStatus.Passed : PhotonCadVerificationStatus.Failed,
            passed ? passReason : failReason,
            count));

    private static PhotonCadVerificationReport Failure(string reason) => new(
        verified: false,
        complete: false,
        reason,
        projectId: null,
        revision: null,
        [new("canonical_integrity", PhotonCadVerificationStatus.Failed, reason, 1)]);

    private static string Sha256(ReadOnlySpan<byte> value) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(value))}";

    private static bool FixedDigestEquals(string left, string right)
    {
        var a = System.Text.Encoding.ASCII.GetBytes(VerificationGuards.Digest(left, nameof(left)));
        var b = System.Text.Encoding.ASCII.GetBytes(VerificationGuards.Digest(right, nameof(right)));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static bool Near(double left, double right) => Math.Abs(left - right) <= 1e-9;
}
