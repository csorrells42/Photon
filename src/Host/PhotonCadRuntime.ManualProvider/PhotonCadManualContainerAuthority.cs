using System.Security.Cryptography;
using System.Text;
using PhotonCadProjects;
using PhotonCadProjects.Codec;
using PhotonCadProjects.RuntimeSync;
using PhotonCadRuntime.IndustrialProvider;

namespace PhotonCadRuntime.ManualProvider;

internal sealed class PhotonCadManualContainerAuthority : IPhotonCadManualGeometryAuthority
{
    private const int MaximumPreviewSources = 256;
    private const int MaximumPreviewOccurrences = 1_024;
    private readonly IIndustrialContainerRunner _runner;
    private readonly VerifiedManualEvidence _evidence;

    private PhotonCadManualContainerAuthority(
        IIndustrialContainerRunner runner,
        VerifiedManualEvidence evidence)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
    }

    internal static async ValueTask<PhotonCadManualContainerAuthority> CreateAsync(
        string dockerExecutable,
        string dockerConfigDirectory,
        string workspaceRoot,
        string evidenceSelectionPath,
        TimeSpan? operationTimeout,
        CancellationToken cancellationToken)
    {
        var evidence = await ManualEvidenceVerifier.VerifyAsync(evidenceSelectionPath, cancellationToken).ConfigureAwait(false);
        var runner = new ContainerRunner(
            dockerExecutable,
            dockerConfigDirectory,
            workspaceRoot,
            evidence.DerivedImageId,
            operationTimeout ?? TimeSpan.FromSeconds(75));
        return new PhotonCadManualContainerAuthority(runner, evidence);
    }

    internal static PhotonCadManualContainerAuthority CreateForSmoke(
        IIndustrialContainerRunner runner,
        VerifiedManualEvidence evidence) => new(runner, evidence);

    public async ValueTask<PhotonCadSealedMutationDelta> ExecuteAsync(
        PhotonCadManualAuthorityRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Command.Kind is not (PhotonCadManualOperationKind.SketchExtrudeAdd
            or PhotonCadManualOperationKind.SketchExtrudeCut
            or PhotonCadManualOperationKind.HoleCut
            or PhotonCadManualOperationKind.LinearPattern
            or PhotonCadManualOperationKind.CircularPattern))
            throw Failure("manual_operation_not_installed");
        var providerRequest = request.ProviderRequest;
        var baseState = ValidateBase(providerRequest, request.Command);
        var replay = ResolveReplayFeature(providerRequest, request.Command);
        var source = request.Command.CreatesEntity
            ? null
            : new IndustrialPreviewSource(
                request.Command.TargetEntityId,
                "source",
                baseState.TargetGeometry!.ContentDigest,
                baseState.TargetGeometry.ByteLength);
        var manualInputs = source is null
            ? Array.Empty<IndustrialInputArtifact>()
            : [new IndustrialInputArtifact(source.InputSlot, baseState.TargetGeometry!.Content.ToArray(), source.ExpectedDigest)];

        IndustrialPrimitiveResponse geometry;
        byte[] step;
        await using (var invocation = await _runner.ExecuteAsync(
            ManualProtocolV1.Serialize(request.Command, source, replay),
            manualInputs,
            cancellationToken).ConfigureAwait(false))
        {
            geometry = ManualProtocolV1.ParseResponse(invocation.Response, request.Command);
            step = await ArtifactReader.ReadSealedAsync(
                invocation.OutputDirectory,
                "model.step",
                geometry.Artifact,
                ProtocolV1.MaximumStepBytes,
                cancellationToken).ConfigureAwait(false);
        }

        var previewPlan = BuildPreview(providerRequest, request.Command, baseState, step, geometry.Artifact.ContentDigest);
        IndustrialPreviewResponse preview;
        byte[] glb;
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
        return ManualMutationMapperV1.Map(
            providerRequest,
            request.Command,
            geometry,
            step,
            preview,
            glb,
            previewPlan.Command,
            baseState.TargetGeometry?.ContentDigest,
            baseState.PreviewDigest,
            _evidence.ProviderEvidence,
            previewPlan.CreatedOccurrence,
            previewPlan.CreatedBom);
    }

    private static ManualReplayFeature? ResolveReplayFeature(
        PhotonCadSealedMutationProviderRequest request,
        PhotonCadManualCommand command)
    {
        if (command.Kind is not (PhotonCadManualOperationKind.LinearPattern or PhotonCadManualOperationKind.CircularPattern))
            return null;
        var pattern = command.Execution as ManualPatternParameters
            ?? throw Failure("manual_pattern_parameters_missing");
        var feature = request.BaseEntities.SingleOrDefault(value =>
            StringComparer.Ordinal.Equals(value.Id, pattern.SeedFeatureId));
        if (feature is null
            || feature.Kind != PhotonCadEntityKindV1.Datum
            || !StringComparer.Ordinal.Equals(feature.ParentId, command.TargetEntityId))
            throw Failure("manual_pattern_seed_feature_invalid");
        var operations = request.BaseOperations.Where(value =>
            StringComparer.Ordinal.Equals(value.CapabilityId, feature.SourceCapabilityId)
            && value.TargetEntityIds.Contains(pattern.SeedFeatureId, StringComparer.Ordinal)).Take(2).ToArray();
        if (operations.Length != 1)
            throw Failure("manual_pattern_seed_operation_count_invalid");
        if (operations[0].TargetEntityIds.Count != 2)
            throw Failure("manual_pattern_seed_target_count_invalid");
        if (!operations[0].TargetEntityIds.Contains(command.TargetEntityId, StringComparer.Ordinal))
            throw Failure("manual_pattern_seed_body_target_invalid");
        if (!operations[0].TargetEntityIds.Contains(pattern.SeedFeatureId, StringComparer.Ordinal))
            throw Failure("manual_pattern_seed_feature_target_invalid");
        if (!StringComparer.Ordinal.Equals(feature.SourceCapabilityId, operations[0].CapabilityId))
            throw Failure("manual_pattern_seed_capability_invalid");
        var operation = operations[0];
        var replay = operation.CapabilityId switch
        {
            PhotonCadManualCapabilityIds.SketchExtrudeCut => new ManualReplayFeature(
                PhotonCadManualOperationKind.SketchExtrudeCut,
                new ManualSketchParameters(
                    RequireText(operation, "profileKind", PhotonCadInputKindV1.Choice),
                    RequireText(operation, "sketchPlane", PhotonCadInputKindV1.Choice),
                    RequireNumber(operation, "profileWidthMm"),
                    RequireNumber(operation, "profileHeightMm"),
                    0,
                    RequireNumber(operation, "cutDepthMm"))),
            PhotonCadManualCapabilityIds.HoleCut => new ManualReplayFeature(
                PhotonCadManualOperationKind.HoleCut,
                new ManualHoleParameters(
                    RequireNumber(operation, "diameterMm") / 2,
                    RequireNumber(operation, "depthMm"),
                    RequireNumber(operation, "xMm"),
                    RequireNumber(operation, "yMm"),
                    RequireNumber(operation, "zMm"))),
            _ => throw Failure("manual_pattern_seed_kind_unsupported"),
        };
        if (command.Kind == PhotonCadManualOperationKind.CircularPattern)
        {
            if (replay.Kind != PhotonCadManualOperationKind.HoleCut
                || replay.Parameters is not ManualHoleParameters hole
                || (BitConverter.DoubleToInt64Bits(hole.XMm) == 0
                    && BitConverter.DoubleToInt64Bits(hole.YMm) == 0))
                throw Failure("manual_circular_pattern_seed_invalid");
        }
        return replay;
    }

    private static PhotonCadSyncOperationInput RequireInput(PhotonCadProviderBaseOperation operation, string id)
    {
        var matches = operation.Inputs.Where(value => StringComparer.Ordinal.Equals(value.Id, id)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : throw Failure("manual_pattern_seed_input_invalid");
    }

    private static double RequireNumber(PhotonCadProviderBaseOperation operation, string id)
    {
        var value = RequireInput(operation, id).Value;
        return value.Kind == PhotonCadInputKindV1.Number && value.TryGetNumber(out var number) && double.IsFinite(number)
            ? number
            : throw Failure("manual_pattern_seed_input_invalid");
    }

    private static string RequireText(
        PhotonCadProviderBaseOperation operation,
        string id,
        PhotonCadInputKindV1 kind)
    {
        var value = RequireInput(operation, id).Value;
        return value.Kind == kind && value.TryGetText(out var text)
            ? text
            : throw Failure("manual_pattern_seed_input_invalid");
    }

    public ValueTask CompensateAsync(
        PhotonCadManualAuthorityRequest request,
        PhotonCadSealedMutationDelta mutation,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(mutation);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 128 || reason.Any(char.IsControl))
            throw new ArgumentException("manual_compensation_reason_invalid", nameof(reason));
        if (!StringComparer.Ordinal.Equals(mutation.RequestId, request.ProviderRequest.Request.RequestId)
            || !StringComparer.Ordinal.Equals(mutation.SessionId, request.ProviderRequest.Request.SessionId)
            || !StringComparer.Ordinal.Equals(mutation.ProjectId, request.ProviderRequest.Request.ProjectId)
            || mutation.BaseRevision != request.ProviderRequest.Request.BaseRevision)
            throw Failure("manual_compensation_binding_mismatch");
        return ValueTask.CompletedTask;
    }

    private static ManualBaseState ValidateBase(
        PhotonCadSealedMutationProviderRequest request,
        PhotonCadManualCommand command)
    {
        if (request.Units != PhotonCadProjectUnit.Millimeter)
            throw Failure("manual_units_not_supported");
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
                throw Failure("manual_base_geometry_invalid");
        }
        geometry.TryGetValue(command.TargetEntityId, out var target);
        if (command.CreatesEntity == (target is not null))
            throw Failure("manual_target_geometry_mismatch");
        var previews = request.BaseArtifacts.Where(value => value.Role == PhotonCadArtifactRoleV1.ProjectPreview).ToArray();
        if (previews.Length > 1 || (request.Request.BaseRevision > 0 && previews.Length != 1))
            throw Failure("manual_base_preview_not_unique");
        string? previewDigest = null;
        if (previews.Length == 1)
        {
            var preview = previews[0];
            if (preview.Kind != PhotonCadArtifactKindV1.Glb
                || preview.OwnerEntityId is not null
                || preview.Bounds is null
                || preview.Revision != request.Request.BaseRevision
                || !StringComparer.Ordinal.Equals(preview.MediaType, "model/gltf-binary")
                || preview.ByteLength != preview.Content.Length
                || !ProtocolV1.FixedDigestEquals(ProtocolV1.Sha256(preview.Content.Span), preview.ContentDigest))
                throw Failure("manual_base_preview_invalid");
            previewDigest = preview.ContentDigest;
        }
        if (request.BaseOccurrences.Count > MaximumPreviewOccurrences)
            throw Failure("manual_base_occurrence_count_rejected");
        var roots = request.BaseOccurrences.Count(value => value.ParentOccurrenceId is null);
        if (request.BaseOccurrences.Count > 0 && roots != 1)
            throw Failure("manual_base_occurrence_root_invalid");
        return new ManualBaseState(geometry, target, previewDigest);
    }

    private static ManualPreviewPlan BuildPreview(
        PhotonCadSealedMutationProviderRequest request,
        PhotonCadManualCommand command,
        ManualBaseState baseState,
        byte[] step,
        string stepDigest)
    {
        var parts = new Dictionary<string, ManualPart>(StringComparer.Ordinal);
        foreach (var item in baseState.Geometry)
        {
            if (StringComparer.Ordinal.Equals(item.Key, command.TargetEntityId)) continue;
            parts.Add(item.Key, new ManualPart(item.Value.Content.ToArray(), item.Value.ContentDigest));
        }
        parts.Add(command.TargetEntityId, new ManualPart(step.ToArray(), stepDigest));
        if (parts.Count is < 1 or > MaximumPreviewSources)
            throw Failure("manual_preview_source_count_rejected");
        var occurrences = request.BaseOccurrences.Select(value => new PhotonCadOccurrenceV1(
            value.OccurrenceId,
            value.ParentOccurrenceId,
            value.PartNumber,
            value.SourceEntityId,
            value.Transform)).ToList();
        PhotonCadOccurrenceV1? createdOccurrence = null;
        PhotonCadBomRow? createdBom = null;
        if (command.CreatesEntity)
        {
            var occurrenceId = $"{command.TargetEntityId}.occ";
            if (occurrences.Any(value => StringComparer.OrdinalIgnoreCase.Equals(value.OccurrenceId, occurrenceId)))
                throw Failure("manual_occurrence_duplicate");
            var parent = occurrences.SingleOrDefault(value => value.ParentOccurrenceId is null)?.OccurrenceId;
            var partNumber = PartNumber(command.TargetEntityId);
            createdOccurrence = new PhotonCadOccurrenceV1(
                occurrenceId,
                parent,
                partNumber,
                command.TargetEntityId,
                MutationMapperV1.IdentityTransform);
            createdBom = new PhotonCadBomRow(
                partNumber,
                "Manual solid",
                1,
                PhotonCadBomUnit.Each,
                command.TargetEntityId);
            occurrences.Add(createdOccurrence);
        }
        if (occurrences.Count is < 1 or > MaximumPreviewOccurrences)
            throw Failure("manual_preview_occurrence_count_rejected");
        var sources = new List<IndustrialPreviewSource>(parts.Count);
        var inputs = new List<IndustrialInputArtifact>(parts.Count);
        var index = 0;
        foreach (var part in parts.OrderBy(value => value.Key, StringComparer.Ordinal))
        {
            var slot = $"part{index++:D4}";
            sources.Add(new IndustrialPreviewSource(part.Key, slot, part.Value.Digest, part.Value.Content.LongLength));
            inputs.Add(new IndustrialInputArtifact(slot, part.Value.Content, part.Value.Digest));
        }
        var previewOccurrences = occurrences
            .OrderBy(value => value.OccurrenceId, StringComparer.Ordinal)
            .Select(value => new IndustrialPreviewOccurrence(
                value.OccurrenceId,
                value.SourceEntityId,
                value.ParentOccurrenceId,
                value.Transform.ToArray()))
            .ToArray();
        if (previewOccurrences.Any(value => value.SourcePartId is null || !parts.ContainsKey(value.SourcePartId)))
            throw Failure("manual_preview_source_coverage_invalid");
        return new ManualPreviewPlan(
            new IndustrialPreviewCommand(sources, previewOccurrences),
            inputs,
            createdOccurrence,
            createdBom);
    }

    private static string PartNumber(string entityId)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entityId)));
        return $"MAN-{digest[..12]}";
    }

    private sealed record ManualPart(byte[] Content, string Digest);
    private sealed record ManualBaseState(
        IReadOnlyDictionary<string, PhotonCadProviderBaseArtifact> Geometry,
        PhotonCadProviderBaseArtifact? TargetGeometry,
        string? PreviewDigest);
    private sealed record ManualPreviewPlan(
        IndustrialPreviewCommand Command,
        IReadOnlyList<IndustrialInputArtifact> Inputs,
        PhotonCadOccurrenceV1? CreatedOccurrence,
        PhotonCadBomRow? CreatedBom);

    private static InvalidDataException Failure(string code) => new(code);
}
