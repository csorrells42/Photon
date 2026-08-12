using System.Security.Cryptography;
using PhotonCadProjects.Codec;

namespace PhotonCadProjects.RuntimeSync;

/// <summary>
/// Sole mapper between a trusted canonical project and provider-owned sealed deltas. Providers
/// never receive or return a complete authoritative project; this mapper performs the only merge.
/// </summary>
public sealed class PhotonCadRuntimeCanonicalMapperV1
{
    private readonly PhotonCadCanonicalProjectCodecV1 _codec;
    private readonly PhotonCadRuntimeSyncPolicy _policy;

    public PhotonCadRuntimeCanonicalMapperV1(
        PhotonCadCanonicalProjectCodecV1 codec,
        PhotonCadRuntimeSyncPolicy? policy = null)
    {
        _codec = codec ?? throw RuntimeSyncGuards.Failure("required", nameof(codec));
        _policy = policy ?? new PhotonCadRuntimeSyncPolicy();
    }

    internal PhotonCadRuntimeSyncPolicy Policy => _policy;

    public PhotonCadSealedMutationProviderRequest PrepareProviderRequest(
        PhotonCadCanonicalMutationBinding binding,
        PhotonCadRuntimeSyncRequest request)
    {
        var state = InspectBoundBase(binding, request);
        long baseBytes = 0;
        foreach (var artifact in state.Artifacts)
        {
            if (artifact.ByteLength > _policy.MaximumBaseArtifactBytes)
                throw RuntimeSyncGuards.Failure("base_artifact_policy_rejected", nameof(binding));
            try { baseBytes = checked(baseBytes + artifact.ByteLength); }
            catch (OverflowException exception)
            {
                throw new PhotonCadRuntimeSyncException("base_artifact_budget_overflow", nameof(binding), exception);
            }
        }
        if (baseBytes > _policy.MaximumBaseArtifactBytesPerRequest)
            throw RuntimeSyncGuards.Failure("base_artifact_policy_rejected", nameof(binding));

        return new PhotonCadSealedMutationProviderRequest(
            request,
            state.Units,
            binding.Current.ContentDigest,
            state.Entities.Select(value => new PhotonCadProviderBaseEntity(value)),
            state.Operations.Select(value => new PhotonCadProviderBaseOperation(value)),
            state.Artifacts.Select(value => new PhotonCadProviderBaseArtifact(value)),
            state.Occurrences.Select(value => new PhotonCadProviderBaseOccurrence(value)),
            state.Bom.Select(value => new PhotonCadProviderBaseBomRow(value)));
    }

    public PhotonCadCanonicalProject Apply(
        PhotonCadCanonicalMutationBinding binding,
        PhotonCadRuntimeSyncRequest request,
        PhotonCadSealedMutationDelta mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        var current = InspectBoundBase(binding, request);
        ValidateMutationBinding(request, mutation);
        ValidatePolicy(mutation);

        var operationsById = mutation.Operations.ToDictionary(value => value.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in mutation.Artifacts)
        {
            if (!operationsById.TryGetValue(artifact.OperationId, out var operation)
                || artifact.Revision != operation.AppliedRevision
                || !artifact.Evidence.Equivalent(operation.Evidence))
                throw RuntimeSyncGuards.Failure("artifact_operation_evidence_mismatch", nameof(mutation));
            if (artifact.Role == PhotonCadArtifactRoleV1.AuthoritativeGeometry
                && (artifact.OwnerEntityId is null
                    || !operation.TargetEntityIds.Contains(artifact.OwnerEntityId, StringComparer.Ordinal)))
                throw RuntimeSyncGuards.Failure("artifact_owner_not_targeted_by_operation", nameof(mutation));
        }

        var entities = current.Entities.ToList();
        var entityIds = new HashSet<string>(entities.Select(value => value.Id), StringComparer.OrdinalIgnoreCase);
        var originatingOperation = mutation.Operations[0];
        foreach (var entity in mutation.Entities)
        {
            if (!entityIds.Add(entity.Id)) throw RuntimeSyncGuards.Failure("entity_suffix_collision", nameof(mutation));
            if (entity.SourceCapabilityId is null
                || !StringComparer.Ordinal.Equals(entity.SourceCapabilityId, request.CapabilityId))
                throw RuntimeSyncGuards.Failure("entity_source_capability_mismatch", nameof(mutation));
            if (!originatingOperation.TargetEntityIds.Contains(entity.Id, StringComparer.Ordinal))
                throw RuntimeSyncGuards.Failure("entity_not_targeted_by_source_operation", nameof(mutation));
            entities.Add(entity);
        }
        if (request.TargetEntityIds.Any(value => !entityIds.Contains(value)))
            throw RuntimeSyncGuards.Failure("request_target_missing_after_merge", nameof(mutation));

        var operations = current.Operations.ToList();
        var operationIds = new HashSet<string>(operations.Select(value => value.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var operation in mutation.Operations)
        {
            if (!operationIds.Add(operation.Id)) throw RuntimeSyncGuards.Failure("operation_suffix_collision", nameof(mutation));
            operations.Add(new PhotonCadOperationV1(
                operations.Count,
                operation.Id,
                operation.CapabilityId,
                operation.Label,
                operation.CreatedAtUtc,
                PhotonCadOperationStateV1.Applied,
                operation.Mode,
                operation.Inputs.Select(value => new PhotonCadOperationInputV1(value.Id, value.Value.ToCanonical())),
                operation.TargetEntityIds,
                CloneSource(operation.Evidence.Source)));
        }

        var occurrences = MergeOccurrences(current.Occurrences, mutation);
        var issues = mutation.IssueMergeMode == PhotonCadCollectionMergeMode.ReplaceAll
            ? mutation.Issues.ToList()
            : current.Issues.Concat(mutation.Issues).ToList();
        var bom = MergeBom(current.Bom, mutation);
        var artifacts = MergeArtifacts(current.Artifacts, mutation, operationsById);

        PhotonCadCanonicalProject updated;
        try
        {
            updated = _codec.Encode(new PhotonCadProjectStateV1(
                current.SessionId,
                current.ProjectId,
                mutation.ResultingRevision,
                current.Title,
                current.Units,
                entities,
                operations,
                occurrences,
                issues,
                bom,
                artifacts,
                dirty: true));
        }
        catch (PhotonCadProjectException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new PhotonCadRuntimeSyncException("canonical_merge_failed", nameof(mutation), exception);
        }

        if (!updated.Dirty || updated.Revision != mutation.ResultingRevision)
            throw RuntimeSyncGuards.Failure("canonical_merge_binding_mismatch", nameof(mutation));
        var inspected = _codec.Inspect(updated);
        ValidatePriorStatePreserved(current, inspected, mutation);
        return updated;
    }

    public void ValidateSaved(
        PhotonCadCanonicalProject updated,
        PhotonCadCanonicalProject saved,
        PhotonCadSealedMutationDelta mutation)
    {
        ArgumentNullException.ThrowIfNull(updated);
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(mutation);
        if (saved.Dirty
            || !StringComparer.Ordinal.Equals(saved.SessionId, mutation.SessionId)
            || !StringComparer.Ordinal.Equals(saved.ProjectId, mutation.ProjectId)
            || saved.Revision != mutation.ResultingRevision
            || !StringComparer.Ordinal.Equals(saved.DisplayName, updated.DisplayName)
            || saved.Units != updated.Units
            || !RuntimeSyncGuards.FixedDigestEquals(saved.ContentDigest, updated.ContentDigest)
            || !RuntimeSyncGuards.FixedDigestEquals(saved.BomDigest, updated.BomDigest)
            || !BytesEqual(saved.CanonicalBytes.Span, updated.CanonicalBytes.Span))
            throw RuntimeSyncGuards.Failure("saved_project_binding_mismatch", nameof(saved));
        _ = _codec.Inspect(saved);
    }

    private PhotonCadProjectStateV1 InspectBoundBase(
        PhotonCadCanonicalMutationBinding binding,
        PhotonCadRuntimeSyncRequest request)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(request);
        var current = binding.Current;
        if (current.Dirty
            || !StringComparer.Ordinal.Equals(current.SessionId, request.SessionId)
            || !StringComparer.Ordinal.Equals(current.ProjectId, request.ProjectId)
            || current.Revision != request.BaseRevision)
            throw RuntimeSyncGuards.Failure("canonical_base_binding_mismatch", nameof(binding));
        var state = _codec.Inspect(current);
        if (state.Dirty) throw RuntimeSyncGuards.Failure("dirty_canonical_base_rejected", nameof(binding));

        var entityIds = new HashSet<string>(state.Entities.Select(value => value.Id), StringComparer.Ordinal);
        // Entity-valued inputs describe pre-existing geometry and must bind before execution.
        // Target IDs may intentionally name entities created by the provider; Apply validates
        // those against the trusted base plus the returned new-entity suffix before commit.
        if (request.Inputs.SelectMany(value => value.Value.ReferencedEntityIds()).Any(value => !entityIds.Contains(value)))
            throw RuntimeSyncGuards.Failure("request_entity_not_in_canonical_base", nameof(request));
        return state;
    }

    private static void ValidateMutationBinding(
        PhotonCadRuntimeSyncRequest request,
        PhotonCadSealedMutationDelta mutation)
    {
        if (!StringComparer.Ordinal.Equals(mutation.RequestId, request.RequestId)
            || !StringComparer.Ordinal.Equals(mutation.SessionId, request.SessionId)
            || !StringComparer.Ordinal.Equals(mutation.ProjectId, request.ProjectId)
            || mutation.BaseRevision != request.BaseRevision)
            throw RuntimeSyncGuards.Failure("mutation_request_binding_mismatch", nameof(mutation));

        var first = mutation.Operations[0];
        if (!StringComparer.Ordinal.Equals(first.CapabilityId, request.CapabilityId)
            || first.Mode != request.Mode
            || !first.TargetEntityIds.SequenceEqual(request.TargetEntityIds, StringComparer.Ordinal)
            || first.Inputs.Count != request.Inputs.Count)
            throw RuntimeSyncGuards.Failure("first_operation_request_mismatch", nameof(mutation));
        for (var index = 0; index < first.Inputs.Count; index++)
        {
            if (!StringComparer.Ordinal.Equals(first.Inputs[index].Id, request.Inputs[index].Id)
                || !first.Inputs[index].Value.Equivalent(request.Inputs[index].Value))
                throw RuntimeSyncGuards.Failure("first_operation_request_mismatch", nameof(mutation));
        }
    }

    private void ValidatePolicy(PhotonCadSealedMutationDelta mutation)
    {
        if (mutation.TotalArtifactBytes > _policy.MaximumMutationArtifactBytes
            || mutation.Artifacts.Any(value => value.ByteLength > _policy.MaximumArtifactBytes))
            throw RuntimeSyncGuards.Failure("sealed_artifact_policy_rejected", nameof(mutation));
    }

    private static List<PhotonCadOccurrenceV1> MergeOccurrences(
        IReadOnlyList<PhotonCadOccurrenceV1> current,
        PhotonCadSealedMutationDelta mutation)
    {
        if (mutation.OccurrenceMergeMode == PhotonCadCollectionMergeMode.ReplaceAll)
            return mutation.Occurrences.ToList();
        var result = current.ToList();
        var ids = new HashSet<string>(result.Select(value => value.OccurrenceId), StringComparer.OrdinalIgnoreCase);
        foreach (var value in mutation.Occurrences)
        {
            if (!ids.Add(value.OccurrenceId)) throw RuntimeSyncGuards.Failure("occurrence_suffix_collision", nameof(mutation));
            result.Add(value);
        }
        return result;
    }

    private static List<PhotonCadBomRow> MergeBom(
        IReadOnlyList<PhotonCadBomRow> current,
        PhotonCadSealedMutationDelta mutation)
    {
        if (mutation.BomMergeMode == PhotonCadCollectionMergeMode.ReplaceAll)
            return mutation.Bom.ToList();
        var result = current.ToList();
        var keys = new HashSet<string>(result.Select(BomKey), StringComparer.Ordinal);
        foreach (var value in mutation.Bom)
        {
            if (!keys.Add(BomKey(value))) throw RuntimeSyncGuards.Failure("bom_suffix_collision", nameof(mutation));
            result.Add(value);
        }
        return result;
    }

    private static List<PhotonCadArtifactV1> MergeArtifacts(
        IReadOnlyList<PhotonCadArtifactV1> current,
        PhotonCadSealedMutationDelta mutation,
        IReadOnlyDictionary<string, PhotonCadAppliedOperationDelta> operationsById)
    {
        var result = current.ToList();
        foreach (var delta in mutation.Artifacts)
        {
            var index = result.FindIndex(value => SameArtifactSlot(value, delta));
            if (index >= 0)
            {
                if (delta.ReplacesContentDigest is null
                    || !RuntimeSyncGuards.FixedDigestEquals(delta.ReplacesContentDigest, result[index].Digest))
                    throw RuntimeSyncGuards.Failure("artifact_replacement_binding_mismatch", nameof(mutation));
            }
            else if (delta.ReplacesContentDigest is not null)
            {
                throw RuntimeSyncGuards.Failure("artifact_replacement_target_missing", nameof(mutation));
            }

            var operation = operationsById[delta.OperationId];
            var artifact = new PhotonCadArtifactV1(
                delta.Role,
                delta.Kind,
                delta.OwnerEntityId,
                delta.Revision,
                delta.ContentUnsafe,
                delta.Bounds,
                new PhotonCadArtifactProvenanceV1(
                    delta.Evidence.Backend,
                    delta.Evidence.BundleId,
                    delta.Evidence.BundleManifestSha256,
                    operation.CapabilityId,
                    operation.Id,
                    CloneSource(delta.Evidence.Source)));
            if (index >= 0) result[index] = artifact;
            else result.Add(artifact);
        }
        return result;
    }

    private static void ValidatePriorStatePreserved(
        PhotonCadProjectStateV1 prior,
        PhotonCadProjectStateV1 merged,
        PhotonCadSealedMutationDelta mutation)
    {
        foreach (var entity in prior.Entities)
        {
            var candidate = merged.Entities.SingleOrDefault(value => StringComparer.Ordinal.Equals(value.Id, entity.Id));
            if (candidate is null || !SameEntity(entity, candidate))
                throw RuntimeSyncGuards.Failure("prior_entity_not_preserved", nameof(mutation));
        }
        foreach (var operation in prior.Operations)
        {
            var candidate = merged.Operations.SingleOrDefault(value => StringComparer.Ordinal.Equals(value.Id, operation.Id));
            if (candidate is null || candidate.Ordinal != operation.Ordinal
                || !StringComparer.Ordinal.Equals(candidate.CapabilityId, operation.CapabilityId))
                throw RuntimeSyncGuards.Failure("prior_operation_not_preserved", nameof(mutation));
        }
        if (mutation.OccurrenceMergeMode == PhotonCadCollectionMergeMode.Append
            && prior.Occurrences.Any(value => !merged.Occurrences.Any(candidate => SameOccurrence(value, candidate))))
            throw RuntimeSyncGuards.Failure("prior_occurrence_not_preserved", nameof(mutation));
        if (mutation.BomMergeMode == PhotonCadCollectionMergeMode.Append
            && prior.Bom.Any(value => !merged.Bom.Any(candidate => SameBom(value, candidate))))
            throw RuntimeSyncGuards.Failure("prior_bom_not_preserved", nameof(mutation));
    }

    private static bool SameArtifactSlot(PhotonCadArtifactV1 existing, PhotonCadSealedArtifactDelta incoming) =>
        existing.Role == incoming.Role
        && (incoming.Role == PhotonCadArtifactRoleV1.ProjectPreview
            || StringComparer.OrdinalIgnoreCase.Equals(existing.OwnerEntityId, incoming.OwnerEntityId));

    private static bool SameEntity(PhotonCadEntityV1 left, PhotonCadEntityV1 right) =>
        StringComparer.Ordinal.Equals(left.Id, right.Id)
        && StringComparer.Ordinal.Equals(left.ParentId, right.ParentId)
        && left.Kind == right.Kind
        && StringComparer.Ordinal.Equals(left.Name, right.Name)
        && left.Visible == right.Visible
        && left.Suppressed == right.Suppressed
        && StringComparer.Ordinal.Equals(left.SourceCapabilityId, right.SourceCapabilityId);

    private static bool SameOccurrence(PhotonCadOccurrenceV1 left, PhotonCadOccurrenceV1 right) =>
        StringComparer.Ordinal.Equals(left.OccurrenceId, right.OccurrenceId)
        && StringComparer.Ordinal.Equals(left.ParentOccurrenceId, right.ParentOccurrenceId)
        && StringComparer.Ordinal.Equals(left.PartNumber, right.PartNumber)
        && StringComparer.Ordinal.Equals(left.SourceEntityId, right.SourceEntityId)
        && left.Transform.Zip(right.Transform).All(value =>
            BitConverter.DoubleToInt64Bits(value.First) == BitConverter.DoubleToInt64Bits(value.Second));

    private static bool SameBom(PhotonCadBomRow left, PhotonCadBomRow right) =>
        StringComparer.Ordinal.Equals(left.PartNumber, right.PartNumber)
        && StringComparer.Ordinal.Equals(left.Description, right.Description)
        && BitConverter.DoubleToInt64Bits(left.Quantity) == BitConverter.DoubleToInt64Bits(right.Quantity)
        && left.Unit == right.Unit
        && StringComparer.Ordinal.Equals(left.SourceEntityId, right.SourceEntityId);

    private static string BomKey(PhotonCadBomRow value) => $"{value.SourceEntityId}\0{value.PartNumber}";

    private static PhotonCadSourceIdentityV1 CloneSource(PhotonCadSourceIdentityV1 value) =>
        new(value.Package, value.Version, value.Digest, value.License);

    private static bool BytesEqual(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) =>
        left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
}
