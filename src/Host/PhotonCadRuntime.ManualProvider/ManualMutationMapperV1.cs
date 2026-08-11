using PhotonCadProjects;
using PhotonCadProjects.Codec;
using PhotonCadProjects.RuntimeSync;
using PhotonCadRuntime.IndustrialProvider;

namespace PhotonCadRuntime.ManualProvider;

internal static class ManualMutationMapperV1
{
    internal static PhotonCadSealedMutationDelta Map(
        PhotonCadSealedMutationProviderRequest providerRequest,
        PhotonCadManualCommand command,
        IndustrialPrimitiveResponse geometry,
        byte[] step,
        IndustrialPreviewResponse preview,
        byte[] glb,
        IndustrialPreviewCommand previewCommand,
        string? replacedStepDigest,
        string? replacedPreviewDigest,
        PhotonCadProviderEvidence evidence,
        PhotonCadOccurrenceV1? createdOccurrence,
        PhotonCadBomRow? createdBom)
    {
        ArgumentNullException.ThrowIfNull(providerRequest);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(glb);
        ArgumentNullException.ThrowIfNull(previewCommand);
        ArgumentNullException.ThrowIfNull(evidence);
        if (step.Length != geometry.Artifact.ByteLength || glb.Length != preview.Artifact.ByteLength)
            throw Failure("manual_artifact_length_drift");
        long aggregate;
        try { aggregate = checked((long)step.Length + glb.Length); }
        catch (OverflowException exception) { throw new InvalidDataException("manual_artifact_budget_overflow", exception); }
        if (aggregate > PhotonCadRuntimeSyncContract.MaximumSealedArtifactBytesPerMutation)
            throw Failure("manual_artifact_budget_rejected");
        if (command.CreatesEntity != (replacedStepDigest is null)
            || command.CreatesEntity != (createdOccurrence is not null)
            || command.CreatesEntity != (createdBom is not null))
            throw Failure("manual_mutation_shape_invalid");

        var request = providerRequest.Request;
        var geometryRevision = checked(request.BaseRevision + 1);
        var previewRevision = checked(request.BaseRevision + 2);
        var geometryOperationId = $"manual-geometry-op-{Guid.NewGuid():N}";
        var previewOperationId = $"manual-preview-op-{Guid.NewGuid():N}";
        var geometryOperation = new PhotonCadAppliedOperationDelta(
            geometryRevision,
            geometryOperationId,
            request.CapabilityId,
            Label(command.Kind),
            DateTimeOffset.UtcNow,
            request.Mode,
            request.Inputs,
            request.TargetEntityIds,
            evidence);
        var previewOperation = new PhotonCadAppliedOperationDelta(
            previewRevision,
            previewOperationId,
            "industrial.preview.glb.v1",
            "Seal complete manual CAD preview",
            DateTimeOffset.UtcNow,
            PhotonCadOperationModeV1.Scratch,
            inputs: [],
            previewCommand.Sources.Select(value => value.SourcePartId),
            evidence);
        var stepArtifact = new PhotonCadSealedArtifactDelta(
            PhotonCadArtifactRoleV1.AuthoritativeGeometry,
            PhotonCadArtifactKindV1.Step,
            command.TargetEntityId,
            geometryRevision,
            step,
            step.LongLength,
            geometry.Artifact.ContentDigest,
            "model/step",
            bounds: null,
            geometryOperationId,
            evidence,
            replacedStepDigest);
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
            replacedPreviewDigest);
        return new PhotonCadSealedMutationDelta(
            $"manual-mutation-{Guid.NewGuid():N}",
            request.RequestId,
            request.SessionId,
            request.ProjectId,
            request.BaseRevision,
            previewRevision,
            [geometryOperation, previewOperation],
            entities: command.CreatesEntity
                ? [new PhotonCadEntityV1(
                    command.TargetEntityId,
                    parentId: null,
                    PhotonCadEntityKindV1.Body,
                    "Manual solid",
                    visible: true,
                    suppressed: false,
                    request.CapabilityId)]
                : [],
            occurrences: createdOccurrence is null ? [] : [createdOccurrence],
            bom: createdBom is null ? [] : [createdBom],
            artifacts: [stepArtifact, previewArtifact]);
    }

    private static string Label(PhotonCadManualOperationKind kind) => kind switch
    {
        PhotonCadManualOperationKind.SketchExtrudeAdd => "Sketch and extrude solid",
        PhotonCadManualOperationKind.SketchExtrudeCut => "Sketch cut solid",
        PhotonCadManualOperationKind.HoleCut => "Cut hole",
        _ => throw Failure("manual_operation_not_installed"),
    };

    private static PhotonCadBoundsV1 Bounds(IndustrialBounds value) => new(
        new PhotonCadVector3V1(value.MinimumX, value.MinimumY, value.MinimumZ),
        new PhotonCadVector3V1(value.MaximumX, value.MaximumY, value.MaximumZ));

    private static InvalidDataException Failure(string code) => new(code);
}
