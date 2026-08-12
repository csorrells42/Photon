using PhotonCadProjects;
using PhotonCadProjects.Codec;
using PhotonCadProjects.RuntimeSync;

namespace PhotonCadRuntime.IndustrialProvider;

internal sealed record ImportedStepAssemblyDefinition(
    IndustrialStepAssemblyDefinitionClaim Claim,
    ReadOnlyMemory<byte> Content);

internal sealed class ImportedStepAssemblyCommand
{
    internal const string Capability = "external.step.assembly.import.v1";

    internal ImportedStepAssemblyCommand(
        string rootEntityId,
        IReadOnlyList<ImportedStepAssemblyDefinition> definitions,
        IReadOnlyList<IndustrialStepAssemblyOccurrenceClaim> occurrences,
        PhotonCadProviderEvidence importEvidence)
    {
        RootEntityId = rootEntityId;
        Definitions = definitions.Select(value => new ImportedStepAssemblyDefinition(value.Claim, value.Content.ToArray())).ToArray();
        Occurrences = occurrences.ToArray();
        ImportEvidence = importEvidence ?? throw new ArgumentNullException(nameof(importEvidence));
    }

    internal string RootEntityId { get; }
    internal IReadOnlyList<ImportedStepAssemblyDefinition> Definitions { get; }
    internal IReadOnlyList<IndustrialStepAssemblyOccurrenceClaim> Occurrences { get; }
    internal PhotonCadProviderEvidence ImportEvidence { get; }
}

internal sealed class ImportedStepAssemblyMutationProvider : IPhotonCadSealedMutationProvider
{
    private readonly PhotonCadRuntimeSyncRequest _boundRequest;
    private readonly ImportedStepAssemblyCommand _command;
    private readonly IIndustrialContainerRunner _runner;
    private readonly VerifiedIndustrialEvidence _industrialEvidence;
    private int _started;
    private int _revoked;
    private string? _mutationId;

    internal ImportedStepAssemblyMutationProvider(
        PhotonCadRuntimeSyncRequest boundRequest,
        ImportedStepAssemblyCommand command,
        IIndustrialContainerRunner runner,
        VerifiedIndustrialEvidence industrialEvidence)
    {
        _boundRequest = boundRequest;
        _command = command;
        _runner = runner;
        _industrialEvidence = industrialEvidence;
    }

    public async ValueTask<PhotonCadSealedMutationDelta> ApplyAsync(
        PhotonCadSealedMutationProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) throw Failure("assembly_import_provider_single_use");
        if (Volatile.Read(ref _revoked) != 0) throw Failure("assembly_import_provider_revoked");
        RequireBoundRequest(request);

        var sources = _command.Definitions
            .Where(value => value.Claim.PreviewSource)
            .OrderBy(value => value.Claim.EntityId, StringComparer.Ordinal)
            .Select((value, index) => new IndustrialPreviewSource(
                value.Claim.EntityId,
                $"part{index:0000}",
                value.Claim.Artifact.ContentDigest,
                value.Claim.Artifact.ByteLength))
            .ToArray();
        var assemblyIds = _command.Definitions
            .Where(value => !value.Claim.PreviewSource)
            .Select(value => value.Claim.EntityId)
            .ToHashSet(StringComparer.Ordinal);
        var occurrences = _command.Occurrences
            .OrderBy(value => value.OccurrenceId, StringComparer.Ordinal)
            .Select(value => new IndustrialPreviewOccurrence(
                value.OccurrenceId,
                assemblyIds.Contains(value.SourceEntityId) ? null : value.SourceEntityId,
                value.ParentOccurrenceId,
                value.Transform))
            .ToArray();
        var previewCommand = new IndustrialPreviewCommand(sources, occurrences);
        var sourceContent = _command.Definitions.ToDictionary(value => value.Claim.EntityId, StringComparer.Ordinal);
        var inputs = sources.Select(source => new IndustrialInputArtifact(
            source.InputSlot,
            sourceContent[source.SourcePartId].Content,
            source.ExpectedDigest)).ToArray();
        IndustrialPreviewResponse preview;
        byte[] glb;
        await using (var invocation = await _runner.ExecuteAsync(
            ProtocolV1.SerializePreview(previewCommand),
            inputs,
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
        var mutation = Map(preview, glb);
        Volatile.Write(ref _mutationId, mutation.MutationId);
        return mutation;
    }

    internal ValueTask RevokeAsync(PhotonCadSealedMutationDelta mutation, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 128 || reason.Any(char.IsControl))
            throw new ArgumentException("assembly_import_compensation_reason_invalid", nameof(reason));
        if (!StringComparer.Ordinal.Equals(Volatile.Read(ref _mutationId), mutation.MutationId)
            || !StringComparer.Ordinal.Equals(mutation.RequestId, _boundRequest.RequestId))
            throw Failure("assembly_import_compensation_binding_mismatch");
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
            || !request.Request.TargetEntityIds.SequenceEqual(
                _command.Definitions.Select(value => value.Claim.EntityId), StringComparer.Ordinal))
            throw Failure("assembly_import_provider_request_not_bound");
    }

    private PhotonCadSealedMutationDelta Map(IndustrialPreviewResponse preview, byte[] glb)
    {
        long aggregate = glb.LongLength;
        try
        {
            foreach (var definition in _command.Definitions) aggregate = checked(aggregate + definition.Content.Length);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("assembly_import_artifact_budget_overflow", exception);
        }
        if (aggregate > PhotonCadRuntimeSyncContract.MaximumSealedArtifactBytesPerMutation)
            throw new InvalidDataException("assembly_import_artifact_budget_exceeded");

        var importRevision = checked(_boundRequest.BaseRevision + 1);
        var previewRevision = checked(_boundRequest.BaseRevision + 2);
        var importOperationId = $"step-assembly-import-op-{Guid.NewGuid():N}";
        var previewOperationId = $"step-assembly-preview-op-{Guid.NewGuid():N}";
        var entityIds = _command.Definitions.Select(value => value.Claim.EntityId).ToArray();
        var importOperation = new PhotonCadAppliedOperationDelta(
            importRevision,
            importOperationId,
            ImportedStepAssemblyCommand.Capability,
            "Import verified STEP Part-21 assembly hierarchy",
            DateTimeOffset.UtcNow,
            PhotonCadOperationModeV1.Scratch,
            _boundRequest.Inputs,
            entityIds,
            _command.ImportEvidence);
        var previewOperation = new PhotonCadAppliedOperationDelta(
            previewRevision,
            previewOperationId,
            PhotonCadAssemblyContract.PreviewCapabilityId,
            "Seal imported assembly preview",
            DateTimeOffset.UtcNow,
            PhotonCadOperationModeV1.Scratch,
            inputs: [],
            entityIds,
            _industrialEvidence.ProviderEvidence);
        var entities = _command.Definitions.Select(value => new PhotonCadEntityV1(
            value.Claim.EntityId,
            value.Claim.ParentEntityId,
            value.Claim.Kind == "assembly" ? PhotonCadEntityKindV1.Assembly : PhotonCadEntityKindV1.Part,
            value.Claim.DisplayName,
            visible: true,
            suppressed: false,
            ImportedStepAssemblyCommand.Capability)).ToArray();
        var occurrences = _command.Occurrences.Select(value => new PhotonCadOccurrenceV1(
            value.OccurrenceId,
            value.ParentOccurrenceId,
            value.PartNumber,
            value.SourceEntityId,
            value.Transform)).ToArray();
        var definitions = _command.Definitions.ToDictionary(value => value.Claim.EntityId, StringComparer.Ordinal);
        var bom = _command.Occurrences
            .Where(value => definitions[value.SourceEntityId].Claim.Kind == "part")
            .GroupBy(value => value.SourceEntityId, StringComparer.Ordinal)
            .Select(group => new PhotonCadBomRow(
                definitions[group.Key].Claim.PartNumber,
                definitions[group.Key].Claim.DisplayName,
                group.LongCount(),
                PhotonCadBomUnit.Each,
                group.Key))
            .OrderBy(value => value.PartNumber, StringComparer.Ordinal)
            .ToArray();
        var artifacts = _command.Definitions.Select(value => new PhotonCadSealedArtifactDelta(
            PhotonCadArtifactRoleV1.AuthoritativeGeometry,
            PhotonCadArtifactKindV1.Step,
            value.Claim.EntityId,
            importRevision,
            value.Content,
            value.Content.Length,
            value.Claim.Artifact.ContentDigest,
            "model/step",
            bounds: null,
            importOperationId,
            _command.ImportEvidence)).Append(new PhotonCadSealedArtifactDelta(
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
                _industrialEvidence.ProviderEvidence)).ToArray();
        return new PhotonCadSealedMutationDelta(
            $"step-assembly-import-mutation-{Guid.NewGuid():N}",
            _boundRequest.RequestId,
            _boundRequest.SessionId,
            _boundRequest.ProjectId,
            _boundRequest.BaseRevision,
            previewRevision,
            [importOperation, previewOperation],
            entities,
            occurrences,
            bom: bom,
            artifacts: artifacts);
    }

    private static InvalidOperationException Failure(string code) => new(code);
}

internal sealed class ImportedStepAssemblyMutationCompensator(ImportedStepAssemblyMutationProvider provider)
    : IPhotonCadSealedMutationCompensator
{
    public ValueTask CompensateAsync(
        PhotonCadSealedMutationDelta mutation,
        string reason,
        CancellationToken cancellationToken = default) => provider.RevokeAsync(mutation, reason, cancellationToken);
}
