using PhotonCadProjects;
using PhotonCadProjects.Codec;
using PhotonCadProjects.RuntimeSync;

namespace PhotonCadRuntime.IndustrialProvider;

internal sealed class AssemblyMutationProvider : IPhotonCadSealedMutationProvider, IPhotonCadSealedMutationCompensator
{
    private const int MaximumPreviewSources = 256;
    private const int MaximumPreviewOccurrences = 1_024;
    private readonly PhotonCadRuntimeSyncRequest _boundRequest;
    private readonly AssemblyMutationCommand _command;
    private readonly IIndustrialContainerRunner _runner;
    private readonly VerifiedIndustrialEvidence _evidence;
    private int _started;
    private int _revoked;
    private string? _mutationId;

    internal AssemblyMutationProvider(
        PhotonCadRuntimeSyncRequest boundRequest,
        AssemblyMutationCommand command,
        IIndustrialContainerRunner runner,
        VerifiedIndustrialEvidence evidence)
    {
        _boundRequest = boundRequest;
        _command = command;
        _runner = runner;
        _evidence = evidence;
    }

    public async ValueTask<PhotonCadSealedMutationDelta> ApplyAsync(
        PhotonCadSealedMutationProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) throw AssemblyGuards.Failure("assembly_provider_single_use");
        if (Volatile.Read(ref _revoked) != 0) throw AssemblyGuards.Failure("assembly_provider_revoked");
        RequireBoundRequest(request);
        var previewDigest = RequirePreview(request);
        var parts = ResolvePartMetadata(request);
        var occurrences = UpdatedOccurrences(request, parts);
        ValidateAssembly(request, occurrences);
        var bom = AssemblyBomDeriver.Derive(parts, occurrences);
        var previewPlan = BuildPreview(request, occurrences);
        byte[] glb;
        IndustrialPreviewResponse preview;
        await using (var invocation = await _runner.ExecuteAsync(
            ProtocolV1.SerializePreview(previewPlan.Command),
            previewPlan.Inputs,
            cancellationToken).ConfigureAwait(false))
        {
            preview = ProtocolV1.ParsePreviewResponse(invocation.Response, previewPlan.Command);
            glb = await ArtifactReader.ReadSealedAsync(
                invocation.OutputDirectory,
                "preview.glb",
                preview.Artifact,
                ProtocolV1.MaximumGlbBytes,
                cancellationToken).ConfigureAwait(false);
        }
        GlbValidator.Validate(glb, previewPlan.Command, preview.Bounds);
        var mutation = AssemblyMutationMapperV1.Map(
            _boundRequest,
            _command,
            occurrences,
            bom,
            preview,
            glb,
            previewDigest,
            _evidence.ProviderEvidence);
        Volatile.Write(ref _mutationId, mutation.MutationId);
        return mutation;
    }

    public ValueTask CompensateAsync(
        PhotonCadSealedMutationDelta mutation,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 128 || reason.Any(char.IsControl))
            throw new ArgumentException("assembly_compensation_reason_invalid", nameof(reason));
        var expected = Volatile.Read(ref _mutationId);
        if (expected is null || !StringComparer.Ordinal.Equals(expected, mutation.MutationId)
            || !StringComparer.Ordinal.Equals(mutation.RequestId, _boundRequest.RequestId))
            throw AssemblyGuards.Failure("assembly_compensation_binding_mismatch");
        Interlocked.Exchange(ref _revoked, 1);
        return ValueTask.CompletedTask;
    }

    private void RequireBoundRequest(PhotonCadSealedMutationProviderRequest request)
    {
        if (!ReferenceEquals(request.Request, _boundRequest)
            || request.Units != PhotonCadProjectUnit.Millimeter
            || request.Request.Mode != PhotonCadOperationModeV1.Scratch
            || request.Request.TargetEntityIds.Count != 1
            || !StringComparer.Ordinal.Equals(request.Request.TargetEntityIds[0], _command.SourceEntityId)
            || !request.ExistingEntityIds.Contains(_command.SourceEntityId, StringComparer.Ordinal))
            throw AssemblyGuards.Failure("assembly_provider_request_not_bound");
    }

    private static string RequirePreview(PhotonCadSealedMutationProviderRequest request)
    {
        var previews = request.BaseArtifacts.Where(value => value.Role == PhotonCadArtifactRoleV1.ProjectPreview).ToArray();
        if (previews.Length != 1) throw AssemblyGuards.Failure("assembly_base_preview_not_unique");
        var preview = previews[0];
        if (preview.Kind != PhotonCadArtifactKindV1.Glb
            || preview.OwnerEntityId is not null
            || preview.Bounds is null
            || preview.Revision != request.Request.BaseRevision
            || !StringComparer.Ordinal.Equals(preview.MediaType, "model/gltf-binary")
            || preview.ByteLength != preview.Content.Length
            || !ProtocolV1.FixedDigestEquals(ProtocolV1.Sha256(preview.Content.Span), preview.ContentDigest))
            throw AssemblyGuards.Failure("assembly_base_preview_invalid");
        return preview.ContentDigest;
    }

    private IReadOnlyList<PhotonCadOccurrenceV1> UpdatedOccurrences(
        PhotonCadSealedMutationProviderRequest request,
        IReadOnlyDictionary<string, AssemblyPartMetadata> parts)
    {
        var current = request.BaseOccurrences.Select(value => new PhotonCadOccurrenceV1(
            value.OccurrenceId,
            value.ParentOccurrenceId,
            value.PartNumber,
            value.SourceEntityId,
            AssemblyGuards.RigidTransform(value.Transform, nameof(request.BaseOccurrences)))).ToList();
        var match = current.Where(value => StringComparer.Ordinal.Equals(value.OccurrenceId, _command.OccurrenceId)).ToArray();
        if (_command.Kind == AssemblyMutationKind.Place)
        {
            if (match.Length != 0) throw AssemblyGuards.Failure("assembly_occurrence_duplicate");
            if (!current.Any(value => StringComparer.Ordinal.Equals(value.OccurrenceId, _command.ParentOccurrenceId)))
                throw AssemblyGuards.Failure("assembly_parent_missing");
            if (!parts.TryGetValue(_command.SourceEntityId, out var template))
                throw AssemblyGuards.Failure("assembly_part_number_source_missing");
            current.Add(new PhotonCadOccurrenceV1(
                _command.OccurrenceId,
                _command.ParentOccurrenceId,
                template.PartNumber,
                _command.SourceEntityId,
                _command.Transform));
        }
        else if (_command.Kind == AssemblyMutationKind.Transform)
        {
            if (match.Length != 1 || !StringComparer.Ordinal.Equals(match[0].SourceEntityId, _command.SourceEntityId))
                throw AssemblyGuards.Failure("assembly_transform_target_missing");
            var index = current.FindIndex(value => StringComparer.Ordinal.Equals(value.OccurrenceId, _command.OccurrenceId));
            current[index] = new PhotonCadOccurrenceV1(
                match[0].OccurrenceId,
                match[0].ParentOccurrenceId,
                match[0].PartNumber,
                match[0].SourceEntityId,
                _command.Transform);
        }
        else
        {
            if (match.Length != 1 || !StringComparer.Ordinal.Equals(match[0].SourceEntityId, _command.SourceEntityId))
                throw AssemblyGuards.Failure("assembly_remove_target_missing");
            var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { match[0].OccurrenceId };
            bool changed;
            do
            {
                changed = false;
                foreach (var occurrence in current)
                {
                    if (occurrence.ParentOccurrenceId is not null
                        && removed.Contains(occurrence.ParentOccurrenceId)
                        && removed.Add(occurrence.OccurrenceId))
                        changed = true;
                }
            } while (changed);
            current.RemoveAll(value => removed.Contains(value.OccurrenceId));
            if (current.Count == 0) throw AssemblyGuards.Failure("assembly_last_occurrence_removal_rejected");
        }
        return current.OrderBy(value => value.OccurrenceId, StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyDictionary<string, AssemblyPartMetadata> ResolvePartMetadata(
        PhotonCadSealedMutationProviderRequest request)
    {
        var result = new Dictionary<string, AssemblyPartMetadata>(StringComparer.Ordinal);
        foreach (var group in request.BaseBom.GroupBy(value => value.SourceEntityId, StringComparer.Ordinal))
        {
            var rows = group.ToArray();
            if (rows.Length != 1) throw AssemblyGuards.Failure("assembly_bom_source_ambiguous");
            result.Add(group.Key, new AssemblyPartMetadata(rows[0].PartNumber, rows[0].Description, rows[0].Unit));
        }

        foreach (var entity in request.BaseEntities)
        {
            if (result.ContainsKey(entity.Id)) continue;
            var recovered = RecoverPartMetadata(entity);
            if (recovered is not null) result.Add(entity.Id, recovered);
        }
        return result;
    }

    private static AssemblyPartMetadata? RecoverPartMetadata(PhotonCadProviderBaseEntity entity)
    {
        var capability = entity.SourceCapabilityId;
        if (StringComparer.Ordinal.Equals(capability, "geometry.box.create.v1"))
            return new AssemblyPartMetadata("BOX", "Create Box", PhotonCadBomUnit.Each);
        if (StringComparer.Ordinal.Equals(capability, "geometry.cylinder.create.v1"))
            return new AssemblyPartMetadata("CYLINDER", "Create Cylinder", PhotonCadBomUnit.Each);
        if (capability is not null
            && capability.StartsWith("bdw_", StringComparison.Ordinal)
            && capability.Length >= 16)
            return new AssemblyPartMetadata(
                $"BDW-{capability[4..16].ToUpperInvariant()}",
                $"Create {entity.Name}",
                PhotonCadBomUnit.Each);
        return null;
    }

    private static void ValidateAssembly(
        PhotonCadSealedMutationProviderRequest request,
        IReadOnlyList<PhotonCadOccurrenceV1> occurrences)
    {
        ValidateOccurrenceDag(occurrences);
        var entities = request.BaseEntities.Select(value => value.Id).ToHashSet(StringComparer.Ordinal);
        var geometry = new Dictionary<string, PhotonCadProviderBaseArtifact>(StringComparer.Ordinal);
        foreach (var artifact in request.BaseArtifacts.Where(value => value.Role == PhotonCadArtifactRoleV1.AuthoritativeGeometry))
        {
            if (artifact.Kind != PhotonCadArtifactKindV1.Step
                || artifact.OwnerEntityId is null
                || artifact.Bounds is not null
                || artifact.Revision > request.Request.BaseRevision
                || !StringComparer.Ordinal.Equals(artifact.MediaType, "model/step")
                || artifact.ByteLength != artifact.Content.Length
                || !ProtocolV1.FixedDigestEquals(ProtocolV1.Sha256(artifact.Content.Span), artifact.ContentDigest)
                || !geometry.TryAdd(artifact.OwnerEntityId, artifact))
                throw AssemblyGuards.Failure("assembly_base_geometry_invalid");
        }
        var usedSources = occurrences.Select(value => value.SourceEntityId).ToHashSet(StringComparer.Ordinal);
        if (!usedSources.All(entities.Contains) || !usedSources.All(geometry.ContainsKey))
            throw AssemblyGuards.Failure("assembly_source_coverage_invalid");
    }

    internal static void ValidateOccurrenceDag(IReadOnlyList<PhotonCadOccurrenceV1> occurrences)
    {
        if (occurrences.Count is < 1 or > MaximumPreviewOccurrences)
            throw AssemblyGuards.Failure("assembly_occurrence_count_invalid");
        var byId = new Dictionary<string, PhotonCadOccurrenceV1>(StringComparer.OrdinalIgnoreCase);
        foreach (var occurrence in occurrences)
        {
            if (!byId.TryAdd(occurrence.OccurrenceId, occurrence)) throw AssemblyGuards.Failure("assembly_occurrence_duplicate");
            _ = AssemblyGuards.RigidTransform(occurrence.Transform, nameof(occurrences));
        }
        if (occurrences.Count(value => value.ParentOccurrenceId is null) != 1)
            throw AssemblyGuards.Failure("assembly_root_count_invalid");
        foreach (var occurrence in occurrences)
        {
            if (occurrence.ParentOccurrenceId is not null && !byId.ContainsKey(occurrence.ParentOccurrenceId))
                throw AssemblyGuards.Failure("assembly_parent_missing");
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var depth = 0;
            for (var cursor = occurrence; cursor is not null;)
            {
                if (++depth > PhotonCadProjectFileV1.MaximumEntityTreeDepth)
                    throw AssemblyGuards.Failure("assembly_tree_too_deep");
                if (!visited.Add(cursor.OccurrenceId)) throw AssemblyGuards.Failure("assembly_cycle_detected");
                cursor = cursor.ParentOccurrenceId is null ? null : byId[cursor.ParentOccurrenceId];
            }
        }
    }

    private static CompletePreviewPlan BuildPreview(
        PhotonCadSealedMutationProviderRequest request,
        IReadOnlyList<PhotonCadOccurrenceV1> occurrences)
    {
        var usedSources = occurrences.Select(value => value.SourceEntityId).ToHashSet(StringComparer.Ordinal);
        if (usedSources.Count is < 1 or > MaximumPreviewSources)
            throw AssemblyGuards.Failure("assembly_preview_source_count_invalid");
        var parts = request.BaseArtifacts
            .Where(value => value.Role == PhotonCadArtifactRoleV1.AuthoritativeGeometry)
            .Where(value => value.OwnerEntityId is not null && usedSources.Contains(value.OwnerEntityId))
            .OrderBy(value => value.OwnerEntityId, StringComparer.Ordinal)
            .ToArray();
        var sources = new List<IndustrialPreviewSource>(parts.Length);
        var inputs = new List<IndustrialInputArtifact>(parts.Length);
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            var slot = $"part{index:D4}";
            sources.Add(new IndustrialPreviewSource(part.OwnerEntityId!, slot, part.ContentDigest, part.ByteLength));
            inputs.Add(new IndustrialInputArtifact(slot, part.Content.ToArray(), part.ContentDigest));
        }
        var previewOccurrences = occurrences.Select(value => new IndustrialPreviewOccurrence(
            value.OccurrenceId,
            value.SourceEntityId,
            value.ParentOccurrenceId,
            value.Transform.ToArray())).ToArray();
        return new CompletePreviewPlan(new IndustrialPreviewCommand(sources, previewOccurrences), inputs);
    }

    private sealed record CompletePreviewPlan(
        IndustrialPreviewCommand Command,
        IReadOnlyList<IndustrialInputArtifact> Inputs);
}
