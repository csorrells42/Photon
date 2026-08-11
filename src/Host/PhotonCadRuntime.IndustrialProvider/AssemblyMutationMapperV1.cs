using PhotonCadProjects;
using PhotonCadProjects.Codec;
using PhotonCadProjects.RuntimeSync;

namespace PhotonCadRuntime.IndustrialProvider;

internal static class AssemblyMutationMapperV1
{
    internal static PhotonCadSealedMutationDelta Map(
        PhotonCadRuntimeSyncRequest request,
        AssemblyMutationCommand command,
        IReadOnlyList<PhotonCadOccurrenceV1> occurrences,
        IReadOnlyList<PhotonCadBomRow> bom,
        IndustrialPreviewResponse preview,
        byte[] glb,
        string replacesPreviewContentDigest,
        PhotonCadProviderEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(occurrences);
        ArgumentNullException.ThrowIfNull(bom);
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(glb);
        ArgumentNullException.ThrowIfNull(evidence);
        if (glb.Length != preview.Artifact.ByteLength)
            throw new InvalidDataException("assembly_preview_length_drift");
        if (glb.Length > PhotonCadRuntimeSyncContract.MaximumSealedArtifactBytesPerMutation)
            throw new InvalidDataException("assembly_preview_codec_bound_exceeded");

        var assemblyRevision = checked(request.BaseRevision + 1);
        var previewRevision = checked(request.BaseRevision + 2);
        var assemblyOperationId = $"assembly-op-{Guid.NewGuid():N}";
        var previewOperationId = $"assembly-preview-op-{Guid.NewGuid():N}";
        var assemblyOperation = new PhotonCadAppliedOperationDelta(
            assemblyRevision,
            assemblyOperationId,
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
            PhotonCadAssemblyContract.PreviewCapabilityId,
            "Seal complete assembly preview",
            DateTimeOffset.UtcNow,
            PhotonCadOperationModeV1.Scratch,
            inputs: [],
            occurrences.Select(value => value.SourceEntityId).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal),
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
        return new PhotonCadSealedMutationDelta(
            $"assembly-mutation-{Guid.NewGuid():N}",
            request.RequestId,
            request.SessionId,
            request.ProjectId,
            request.BaseRevision,
            previewRevision,
            [assemblyOperation, previewOperation],
            occurrences: occurrences,
            bom: bom,
            artifacts: [previewArtifact],
            occurrenceMergeMode: PhotonCadCollectionMergeMode.ReplaceAll,
            bomMergeMode: PhotonCadCollectionMergeMode.ReplaceAll);
    }

    private static PhotonCadBoundsV1 Bounds(IndustrialBounds value) => new(
        new PhotonCadVector3V1(value.MinimumX, value.MinimumY, value.MinimumZ),
        new PhotonCadVector3V1(value.MaximumX, value.MaximumY, value.MaximumZ));
}
