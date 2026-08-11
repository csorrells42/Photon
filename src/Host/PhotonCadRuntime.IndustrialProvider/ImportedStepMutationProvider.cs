using System.Security.Cryptography;
using PhotonCadProjects;
using PhotonCadProjects.Codec;
using PhotonCadProjects.RuntimeSync;

namespace PhotonCadRuntime.IndustrialProvider;

internal sealed class ImportedStepPartCommand
{
    internal const string Capability = "external.step.import.v1";
    private readonly byte[] _stepContent;

    internal ImportedStepPartCommand(
        string entityId,
        string partNumber,
        string displayName,
        ReadOnlyMemory<byte> stepContent,
        string stepContentDigest,
        PhotonCadProviderEvidence importEvidence)
    {
        if (string.IsNullOrWhiteSpace(entityId)) throw new ArgumentException("import_entity_required", nameof(entityId));
        if (stepContent.Length is < 48 or > ProtocolV1.MaximumStepBytes)
            throw new ArgumentOutOfRangeException(nameof(stepContent));
        _stepContent = stepContent.ToArray();
        var actual = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(_stepContent))}";
        StepContentDigest = ProtocolV1.NormalizeDigest(stepContentDigest);
        if (!ProtocolV1.FixedDigestEquals(actual, StepContentDigest))
            throw new InvalidDataException("import_step_digest_mismatch");
        EntityId = entityId;
        PartNumber = partNumber;
        DisplayName = displayName;
        ImportEvidence = importEvidence ?? throw new ArgumentNullException(nameof(importEvidence));
    }

    internal string EntityId { get; }
    internal string PartNumber { get; }
    internal string DisplayName { get; }
    internal string StepContentDigest { get; }
    internal PhotonCadProviderEvidence ImportEvidence { get; }
    internal ReadOnlyMemory<byte> StepContent => _stepContent.ToArray();
}

internal sealed class ImportedStepMutationProvider : IPhotonCadSealedMutationProvider
{
    private readonly PhotonCadRuntimeSyncRequest _boundRequest;
    private readonly ImportedStepPartCommand _command;
    private readonly IIndustrialContainerRunner _runner;
    private readonly VerifiedIndustrialEvidence _industrialEvidence;
    private int _started;
    private int _revoked;
    private string? _mutationId;

    internal ImportedStepMutationProvider(
        PhotonCadRuntimeSyncRequest boundRequest,
        ImportedStepPartCommand command,
        IIndustrialContainerRunner runner,
        VerifiedIndustrialEvidence industrialEvidence)
    {
        _boundRequest = boundRequest ?? throw new ArgumentNullException(nameof(boundRequest));
        _command = command ?? throw new ArgumentNullException(nameof(command));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _industrialEvidence = industrialEvidence ?? throw new ArgumentNullException(nameof(industrialEvidence));
    }

    public async ValueTask<PhotonCadSealedMutationDelta> ApplyAsync(
        PhotonCadSealedMutationProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) throw Failure("import_provider_single_use");
        if (Volatile.Read(ref _revoked) != 0) throw Failure("import_provider_revoked");
        RequireBoundRequest(request);

        var step = _command.StepContent.ToArray();
        var source = new IndustrialPreviewSource(
            _command.EntityId,
            "part0000",
            _command.StepContentDigest,
            step.LongLength);
        var occurrence = new IndustrialPreviewOccurrence(
            $"{_command.EntityId}.occ",
            _command.EntityId,
            ParentEntityId: null,
            MutationMapperV1.IdentityTransform.ToArray());
        var previewCommand = new IndustrialPreviewCommand([source], [occurrence]);
        var payload = ProtocolV1.SerializePreview(previewCommand);
        IndustrialPreviewResponse preview;
        byte[] glb;
        await using (var invocation = await _runner.ExecuteAsync(
            payload,
            [new IndustrialInputArtifact(source.InputSlot, step, _command.StepContentDigest)],
            cancellationToken).ConfigureAwait(false))
        {
            preview = ProtocolV1.ParsePreviewResponse(invocation.Response, previewCommand);
            glb = await ArtifactReader.ReadSealedAsync(
                invocation.OutputDirectory,
                "preview.glb",
                preview.Artifact,
                ProtocolV1.MaximumGlbBytes,
                cancellationToken).ConfigureAwait(false);
        }
        GlbValidator.Validate(glb, previewCommand, preview.Bounds);
        var mutation = Map(preview, glb, previewCommand, step);
        Volatile.Write(ref _mutationId, mutation.MutationId);
        return mutation;
    }

    internal ValueTask RevokeAsync(
        PhotonCadSealedMutationDelta mutation,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 128 || reason.Any(char.IsControl))
            throw new ArgumentException("import_compensation_reason_invalid", nameof(reason));
        var expected = Volatile.Read(ref _mutationId);
        if (expected is null
            || !StringComparer.Ordinal.Equals(expected, mutation.MutationId)
            || !StringComparer.Ordinal.Equals(mutation.RequestId, _boundRequest.RequestId))
            throw Failure("import_compensation_binding_mismatch");
        Interlocked.Exchange(ref _revoked, 1);
        return ValueTask.CompletedTask;
    }

    private void RequireBoundRequest(PhotonCadSealedMutationProviderRequest request)
    {
        if (!ReferenceEquals(request.Request, _boundRequest)
            || request.Units != PhotonCadProjectUnit.Millimeter
            || request.Request.Mode != PhotonCadOperationModeV1.Scratch
            || request.Request.BaseRevision != 0
            || request.BaseEntities.Count != 0
            || request.BaseArtifacts.Count != 0
            || request.BaseOccurrences.Count != 0
            || request.BaseBom.Count != 0
            || request.Request.TargetEntityIds.Count != 1
            || !StringComparer.Ordinal.Equals(request.Request.TargetEntityIds[0], _command.EntityId))
            throw Failure("import_provider_request_not_bound");
    }

    private PhotonCadSealedMutationDelta Map(
        IndustrialPreviewResponse preview,
        byte[] glb,
        IndustrialPreviewCommand previewCommand,
        byte[] step)
    {
        if (glb.LongLength != preview.Artifact.ByteLength
            || !ProtocolV1.FixedDigestEquals(ProtocolV1.Sha256(glb), preview.Artifact.ContentDigest))
            throw new InvalidDataException("import_preview_artifact_drift");
        long aggregate;
        try { aggregate = checked(step.LongLength + glb.LongLength); }
        catch (OverflowException exception) { throw new InvalidDataException("import_artifact_budget_overflow", exception); }
        if (aggregate > PhotonCadRuntimeSyncContract.MaximumSealedArtifactBytesPerMutation)
            throw new InvalidDataException("import_artifact_budget_exceeded");

        var importRevision = checked(_boundRequest.BaseRevision + 1);
        var previewRevision = checked(_boundRequest.BaseRevision + 2);
        var importOperationId = $"step-import-op-{Guid.NewGuid():N}";
        var previewOperationId = $"step-import-preview-op-{Guid.NewGuid():N}";
        var importOperation = new PhotonCadAppliedOperationDelta(
            importRevision,
            importOperationId,
            ImportedStepPartCommand.Capability,
            "Import verified STEP Part-21 part",
            DateTimeOffset.UtcNow,
            PhotonCadOperationModeV1.Scratch,
            _boundRequest.Inputs,
            _boundRequest.TargetEntityIds,
            _command.ImportEvidence);
        var previewOperation = new PhotonCadAppliedOperationDelta(
            previewRevision,
            previewOperationId,
            "industrial.preview.glb.v1",
            "Seal imported part preview",
            DateTimeOffset.UtcNow,
            PhotonCadOperationModeV1.Scratch,
            inputs: [],
            [_command.EntityId],
            _industrialEvidence.ProviderEvidence);
        var stepArtifact = new PhotonCadSealedArtifactDelta(
            PhotonCadArtifactRoleV1.AuthoritativeGeometry,
            PhotonCadArtifactKindV1.Step,
            _command.EntityId,
            importRevision,
            step,
            step.LongLength,
            _command.StepContentDigest,
            "model/step",
            bounds: null,
            importOperationId,
            _command.ImportEvidence);
        var previewArtifact = new PhotonCadSealedArtifactDelta(
            PhotonCadArtifactRoleV1.ProjectPreview,
            PhotonCadArtifactKindV1.Glb,
            ownerEntityId: null,
            previewRevision,
            glb,
            glb.LongLength,
            preview.Artifact.ContentDigest,
            "model/gltf-binary",
            new PhotonCadBoundsV1(
                new PhotonCadVector3V1(preview.Bounds.MinimumX, preview.Bounds.MinimumY, preview.Bounds.MinimumZ),
                new PhotonCadVector3V1(preview.Bounds.MaximumX, preview.Bounds.MaximumY, preview.Bounds.MaximumZ)),
            previewOperationId,
            _industrialEvidence.ProviderEvidence);
        return new PhotonCadSealedMutationDelta(
            $"step-import-mutation-{Guid.NewGuid():N}",
            _boundRequest.RequestId,
            _boundRequest.SessionId,
            _boundRequest.ProjectId,
            _boundRequest.BaseRevision,
            previewRevision,
            [importOperation, previewOperation],
            entities:
            [
                new PhotonCadEntityV1(
                    _command.EntityId,
                    parentId: null,
                    PhotonCadEntityKindV1.Part,
                    _command.DisplayName,
                    visible: true,
                    suppressed: false,
                    ImportedStepPartCommand.Capability),
            ],
            occurrences:
            [
                new PhotonCadOccurrenceV1(
                    previewCommand.Occurrences[0].EntityId,
                    parentOccurrenceId: null,
                    _command.PartNumber,
                    _command.EntityId,
                    MutationMapperV1.IdentityTransform),
            ],
            bom:
            [
                new PhotonCadBomRow(
                    _command.PartNumber,
                    _command.DisplayName,
                    1,
                    PhotonCadBomUnit.Each,
                    _command.EntityId),
            ],
            artifacts: [stepArtifact, previewArtifact]);
    }

    private static InvalidOperationException Failure(string code) => new(code);
}

internal sealed class ImportedStepMutationCompensator(ImportedStepMutationProvider provider)
    : IPhotonCadSealedMutationCompensator
{
    private readonly ImportedStepMutationProvider _provider = provider ?? throw new ArgumentNullException(nameof(provider));

    public ValueTask CompensateAsync(
        PhotonCadSealedMutationDelta mutation,
        string reason,
        CancellationToken cancellationToken = default) =>
        _provider.RevokeAsync(mutation, reason, cancellationToken);
}
