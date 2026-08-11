using PhotonCadProjects;
using PhotonCadProjects.Codec;
using PhotonCadProjects.RuntimeSync;

namespace PhotonCadRuntime.IndustrialProvider;

internal static class MutationMapperV1
{
    internal static PhotonCadSealedMutationDelta Map(
        PhotonCadRuntimeSyncRequest request,
        IIndustrialPartCommand command,
        IndustrialPrimitiveResponse primitive,
        byte[] step,
        IndustrialPreviewResponse preview,
        byte[] glb,
        IndustrialPreviewCommand previewCommand,
        string? replacesPreviewContentDigest,
        PhotonCadProviderEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(primitive);
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(glb);
        ArgumentNullException.ThrowIfNull(previewCommand);
        ArgumentNullException.ThrowIfNull(evidence);
        if (step.Length != primitive.Artifact.ByteLength || glb.Length != preview.Artifact.ByteLength)
            throw new InvalidDataException("industrial_artifact_length_drift");
        if (step.Length > PhotonCadRuntimeSyncContract.MaximumSealedArtifactBytes
            || glb.Length > PhotonCadRuntimeSyncContract.MaximumSealedArtifactBytes)
            throw new InvalidDataException("industrial_per_artifact_codec_bound_exceeded");
        long aggregate;
        try { aggregate = checked((long)step.Length + glb.Length); }
        catch (OverflowException exception) { throw new InvalidDataException("industrial_aggregate_codec_bound_overflow", exception); }
        if (aggregate > PhotonCadRuntimeSyncContract.MaximumSealedArtifactBytesPerMutation)
            throw new InvalidDataException("industrial_aggregate_codec_bound_exceeded");
        if (request.TargetEntityIds.Count != 1 || !StringComparer.Ordinal.Equals(request.TargetEntityIds[0], command.EntityId))
            throw new InvalidDataException("industrial_root_binding_mismatch");

        var geometryRevision = checked(request.BaseRevision + 1);
        var previewRevision = checked(request.BaseRevision + 2);
        var geometryOperationId = $"industrial-geometry-op-{Guid.NewGuid():N}";
        var previewOperationId = $"industrial-preview-op-{Guid.NewGuid():N}";
        var geometryOperation = new PhotonCadAppliedOperationDelta(
            geometryRevision,
            geometryOperationId,
            request.CapabilityId,
            command.Label,
            DateTimeOffset.UtcNow,
            request.Mode,
            request.Inputs,
            request.TargetEntityIds,
            evidence);
        var previewOperation = new PhotonCadAppliedOperationDelta(
            previewRevision,
            previewOperationId,
            "industrial.preview.glb.v1",
            "Seal complete project preview",
            DateTimeOffset.UtcNow,
            PhotonCadOperationModeV1.Scratch,
            inputs: [],
            previewCommand.Sources.Select(source => source.SourcePartId),
            evidence);
        var stepArtifact = new PhotonCadSealedArtifactDelta(
            PhotonCadArtifactRoleV1.AuthoritativeGeometry,
            PhotonCadArtifactKindV1.Step,
            command.EntityId,
            geometryRevision,
            step,
            step.LongLength,
            primitive.Artifact.ContentDigest,
            "model/step",
            bounds: null,
            geometryOperationId,
            evidence);
        var previewArtifact = new PhotonCadSealedArtifactDelta(
            PhotonCadArtifactRoleV1.ProjectPreview,
            PhotonCadArtifactKindV1.Glb,
            ownerEntityId: null,
            previewRevision,
            glb,
            glb.LongLength,
            preview.Artifact.ContentDigest,
            "model/gltf-binary",
            Bounds(preview.Bounds),
            previewOperationId,
            evidence,
            replacesPreviewContentDigest);
        var newOccurrence = previewCommand.Occurrences.Single(occurrence =>
            StringComparer.Ordinal.Equals(occurrence.SourcePartId, command.EntityId)
            && StringComparer.Ordinal.Equals(occurrence.EntityId, $"{command.EntityId}.occ"));
        return new PhotonCadSealedMutationDelta(
            $"industrial-mutation-{Guid.NewGuid():N}",
            request.RequestId,
            request.SessionId,
            request.ProjectId,
            request.BaseRevision,
            previewRevision,
            [geometryOperation, previewOperation],
            entities:
            [
                new PhotonCadEntityV1(
                    command.EntityId,
                    parentId: null,
                    command.EntityKind,
                    command.EntityName,
                    visible: true,
                    suppressed: false,
                    request.CapabilityId),
            ],
            occurrences:
            [
                new PhotonCadOccurrenceV1(
                    newOccurrence.EntityId,
                    newOccurrence.ParentEntityId,
                    command.PartNumber,
                    command.EntityId,
                    newOccurrence.Transform),
            ],
            bom:
            [
                new PhotonCadBomRow(
                    command.PartNumber,
                    command.Label,
                    1,
                    PhotonCadBomUnit.Each,
                    command.EntityId),
            ],
            artifacts: [stepArtifact, previewArtifact]);
    }

    private static PhotonCadBoundsV1 Bounds(IndustrialBounds value) => new(
        new PhotonCadVector3V1(value.MinimumX, value.MinimumY, value.MinimumZ),
        new PhotonCadVector3V1(value.MaximumX, value.MaximumY, value.MaximumZ));

    internal static readonly double[] IdentityTransform =
    [
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    ];
}
