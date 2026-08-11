using PhotonCadProjects;
using PhotonCadProjects.Codec;
using PhotonCadProjects.RuntimeSync;

namespace PhotonCadRuntime.IndustrialProvider;

public sealed class PhotonCadIndustrialBoundMutation
{
    internal PhotonCadIndustrialBoundMutation(
        PhotonCadRuntimeSyncRequest request,
        IPhotonCadSealedMutationProvider provider,
        IPhotonCadSealedMutationCompensator compensator)
    {
        Request = request;
        Provider = provider;
        Compensator = compensator;
    }

    public PhotonCadRuntimeSyncRequest Request { get; }
    public IPhotonCadSealedMutationProvider Provider { get; }
    public IPhotonCadSealedMutationCompensator Compensator { get; }
}

/// <summary>
/// Verified local-only industrial runtime. Its accepted evidence declares redistribution blocked;
/// constructing this object is not a release, licensing, or public-distribution approval.
/// </summary>
public sealed class PhotonCadIndustrialProviderRuntime
{
    private readonly IIndustrialContainerRunner _runner;
    private readonly VerifiedIndustrialEvidence _evidence;
    private readonly CatalogCache _catalogCache;
    private readonly object _catalogLock = new();
    private PhotonCadIndustrialCatalog? _catalog;

    private PhotonCadIndustrialProviderRuntime(IIndustrialContainerRunner runner, VerifiedIndustrialEvidence evidence)
    {
        _runner = runner;
        _evidence = evidence;
        _catalogCache = new CatalogCache(LoadCatalogBytesAsync, evidence.CatalogDigest);
    }

    public static async ValueTask<PhotonCadIndustrialProviderRuntime> CreateLocalEngineeringAsync(
        string dockerExecutable,
        string dockerConfigDirectory,
        string workspaceRoot,
        string evidenceSelectionPath,
        TimeSpan? operationTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var evidence = await EvidenceVerifier.VerifyAsync(evidenceSelectionPath, cancellationToken).ConfigureAwait(false);
        var runner = new ContainerRunner(
            dockerExecutable,
            dockerConfigDirectory,
            workspaceRoot,
            evidence.DerivedImageId,
            operationTimeout ?? TimeSpan.FromSeconds(75));
        return new PhotonCadIndustrialProviderRuntime(runner, evidence);
    }

    internal static PhotonCadIndustrialProviderRuntime CreateForSmoke(
        IIndustrialContainerRunner runner,
        VerifiedIndustrialEvidence evidence) => new(runner, evidence);

    public async ValueTask<PhotonCadIndustrialCatalog> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        lock (_catalogLock)
        {
            if (_catalog is not null) return _catalog;
        }
        var bytes = await _catalogCache.GetAsync(cancellationToken).ConfigureAwait(false);
        var projected = CatalogProjection.Parse(bytes, _evidence.CatalogDigest);
        lock (_catalogLock)
        {
            _catalog ??= projected;
            return _catalog;
        }
    }

    public ValueTask<PhotonCadCommittedVerificationResult> VerifyCommittedAsync(
        PhotonCadCanonicalProject project,
        IReadOnlyList<PhotonCadCommittedVerificationCheck> checks,
        CancellationToken cancellationToken = default) =>
        CommittedVerification.VerifyAsync(project, checks, _runner, cancellationToken);

    public PhotonCadIndustrialBoundMutation BindBox(
        string requestId,
        string sessionId,
        string projectId,
        long baseRevision,
        string entityId,
        double lengthMm,
        double widthMm,
        double heightMm,
        string partNumber = "BOX") => Bind(
            requestId,
            sessionId,
            projectId,
            baseRevision,
            entityId,
            partNumber,
            new IndustrialPrimitiveCommand(
                IndustrialPrimitiveKind.Box,
                Dimension(lengthMm, nameof(lengthMm)),
                Dimension(widthMm, nameof(widthMm)),
                Dimension(heightMm, nameof(heightMm)),
                0,
                entityId,
                PartNumber(partNumber),
                "Create industrial box"));

    public PhotonCadIndustrialBoundMutation BindCylinder(
        string requestId,
        string sessionId,
        string projectId,
        long baseRevision,
        string entityId,
        double radiusMm,
        double heightMm,
        string partNumber = "CYLINDER") => Bind(
            requestId,
            sessionId,
            projectId,
            baseRevision,
            entityId,
            partNumber,
            new IndustrialPrimitiveCommand(
                IndustrialPrimitiveKind.Cylinder,
                0,
                0,
                Dimension(heightMm, nameof(heightMm)),
                Dimension(radiusMm, nameof(radiusMm)),
                entityId,
                PartNumber(partNumber),
                "Create industrial cylinder"));

    public async ValueTask<PhotonCadIndustrialBoundMutation> BindCatalogItemAsync(
        string requestId,
        string sessionId,
        string projectId,
        long baseRevision,
        string entityId,
        string capabilityId,
        IReadOnlyDictionary<string, PhotonCadIndustrialCatalogInputValue?> inputs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var catalog = await GetCatalogAsync(cancellationToken).ConfigureAwait(false);
        var definition = catalog.RequireDefinition(capabilityId);
        if (inputs.Count > definition.Parameters.Count
            || inputs.Keys.Any(key => !definition.Parameters.ContainsKey(key)))
            throw new ArgumentException("industrial_catalog_input_set_invalid", nameof(inputs));
        var values = new List<IndustrialCatalogParameterValue>();
        foreach (var parameter in definition.Parameters.Values.OrderBy(value => value.Public.Id, StringComparer.Ordinal))
        {
            inputs.TryGetValue(parameter.Public.Id, out var supplied);
            supplied ??= parameter.Public.DefaultValue;
            if (supplied is null)
            {
                if (parameter.Public.Required)
                    throw new ArgumentException("industrial_catalog_required_input_missing", nameof(inputs));
                continue;
            }
            var raw = CatalogProjection.Resolve(parameter, supplied);
            var sync = supplied.Kind switch
            {
                PhotonCadIndustrialParameterKind.Number => PhotonCadSyncInputValue.Number(supplied.NumberValue),
                PhotonCadIndustrialParameterKind.Integer => PhotonCadSyncInputValue.Integer(supplied.IntegerValue),
                PhotonCadIndustrialParameterKind.Boolean => PhotonCadSyncInputValue.Boolean(supplied.BooleanValue),
                PhotonCadIndustrialParameterKind.Choice => PhotonCadSyncInputValue.Choice(supplied.ChoiceToken),
                _ => throw new ArgumentException("industrial_catalog_input_kind_invalid", nameof(inputs)),
            };
            values.Add(new IndustrialCatalogParameterValue(parameter.Public.Id, raw, sync));
        }
        var partNumber = PartNumber($"BDW-{definition.Public.CapabilityId[4..16].ToUpperInvariant()}");
        return Bind(
            requestId,
            sessionId,
            projectId,
            baseRevision,
            entityId,
            partNumber,
            new IndustrialCatalogCommand(
                catalog.Digest,
                definition.Public.CapabilityId,
                values.ToArray(),
                entityId,
                partNumber,
                $"Create {definition.Public.Title}",
                definition.Public.Title));
    }

    /// <summary>
    /// Binds one already validated, byte-preserved generic STEP Part-21 part to the same canonical
    /// transaction used by generated parts. The imported STEP bytes remain authoritative while the
    /// exact industrial image is used only to create the complete-project GLB preview.
    /// </summary>
    public PhotonCadIndustrialBoundMutation BindImportedStepPart(
        string requestId,
        string sessionId,
        string projectId,
        long baseRevision,
        string entityId,
        string partNumber,
        string displayName,
        ReadOnlyMemory<byte> stepContent,
        string stepContentDigest,
        PhotonCadProviderEvidence importEvidence)
    {
        var command = new ImportedStepPartCommand(
            entityId,
            PartNumber(partNumber),
            DisplayName(displayName),
            stepContent,
            stepContentDigest,
            importEvidence);
        var request = new PhotonCadRuntimeSyncRequest(
            requestId,
            sessionId,
            projectId,
            baseRevision,
            ImportedStepPartCommand.Capability,
            PhotonCadOperationModeV1.Scratch,
            [
                new PhotonCadSyncOperationInput("sourceDigest", PhotonCadSyncInputValue.Text(command.StepContentDigest)),
                new PhotonCadSyncOperationInput("sourceByteLength", PhotonCadSyncInputValue.Integer(command.StepContent.Length)),
            ],
            [entityId]);
        var provider = new ImportedStepMutationProvider(request, command, _runner, _evidence);
        return new PhotonCadIndustrialBoundMutation(request, provider, new ImportedStepMutationCompensator(provider));
    }

    public PhotonCadAssemblyBoundMutation BindAssemblyPlace(
        string requestId,
        string sessionId,
        string projectId,
        long baseRevision,
        string occurrenceId,
        string sourceEntityId,
        string parentOccurrenceId,
        IReadOnlyList<double> transform) => BindAssembly(
            requestId,
            sessionId,
            projectId,
            baseRevision,
            new AssemblyMutationCommand(
                AssemblyMutationKind.Place,
                occurrenceId,
                sourceEntityId,
                parentOccurrenceId,
                transform));

    public PhotonCadAssemblyBoundMutation BindAssemblyTransform(
        string requestId,
        string sessionId,
        string projectId,
        long baseRevision,
        string occurrenceId,
        string sourceEntityId,
        IReadOnlyList<double> transform) => BindAssembly(
            requestId,
            sessionId,
            projectId,
            baseRevision,
            new AssemblyMutationCommand(
                AssemblyMutationKind.Transform,
                occurrenceId,
                sourceEntityId,
                parentOccurrenceId: null,
                transform));

    public PhotonCadAssemblyBoundMutation BindAssemblyRemove(
        string requestId,
        string sessionId,
        string projectId,
        long baseRevision,
        string occurrenceId,
        string sourceEntityId) => BindAssembly(
            requestId,
            sessionId,
            projectId,
            baseRevision,
            new AssemblyMutationCommand(
                AssemblyMutationKind.Remove,
                occurrenceId,
                sourceEntityId,
                parentOccurrenceId: null,
                MutationMapperV1.IdentityTransform));

    private PhotonCadAssemblyBoundMutation BindAssembly(
        string requestId,
        string sessionId,
        string projectId,
        long baseRevision,
        AssemblyMutationCommand command)
    {
        var request = new PhotonCadRuntimeSyncRequest(
            requestId,
            sessionId,
            projectId,
            baseRevision,
            command.CapabilityId,
            PhotonCadOperationModeV1.Scratch,
            command.Inputs(),
            [command.SourceEntityId]);
        return new PhotonCadAssemblyBoundMutation(
            request,
            new AssemblyMutationProvider(request, command, _runner, _evidence));
    }

    private PhotonCadIndustrialBoundMutation Bind(
        string requestId,
        string sessionId,
        string projectId,
        long baseRevision,
        string entityId,
        string partNumber,
        IIndustrialPartCommand command)
    {
        _ = PartNumber(partNumber);
        var request = new PhotonCadRuntimeSyncRequest(
            requestId,
            sessionId,
            projectId,
            baseRevision,
            command.CapabilityId,
            PhotonCadOperationModeV1.Scratch,
            command.SyncInputs,
            [entityId]);
        var provider = new SealedMutationProvider(request, command, _runner, _evidence);
        var compensator = new Compensator(provider);
        return new PhotonCadIndustrialBoundMutation(request, provider, compensator);
    }

    private async ValueTask<byte[]> LoadCatalogBytesAsync(CancellationToken cancellationToken)
    {
        await using var invocation = await _runner.ExecuteAsync(
            ProtocolV1.SerializeCatalog(),
            [],
            cancellationToken).ConfigureAwait(false);
        return ProtocolV1.ParseCatalogResponse(invocation.Response, _evidence.CatalogDigest);
    }

    private static double Dimension(double value, string field)
    {
        if (!double.IsFinite(value) || value <= 0 || value > 1_000_000)
            throw new ArgumentOutOfRangeException(field);
        return value;
    }

    private static string PartNumber(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 || !char.IsAsciiLetterOrDigit(value[0])
            || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
            throw new ArgumentException("part_number_invalid", nameof(value));
        return value;
    }

    private static string DisplayName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
            throw new ArgumentException("display_name_invalid", nameof(value));
        return value.Trim();
    }
}

internal sealed class SealedMutationProvider : IPhotonCadSealedMutationProvider
{
    private readonly PhotonCadRuntimeSyncRequest _boundRequest;
    private readonly IIndustrialPartCommand _command;
    private readonly IIndustrialContainerRunner _runner;
    private readonly VerifiedIndustrialEvidence _evidence;
    private int _started;
    private int _revoked;
    private string? _mutationId;

    internal SealedMutationProvider(
        PhotonCadRuntimeSyncRequest boundRequest,
        IIndustrialPartCommand command,
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
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) throw Failure("industrial_provider_single_use");
        if (Volatile.Read(ref _revoked) != 0) throw Failure("industrial_provider_revoked");
        RequireBoundRequest(request);
        var priorPreviewDigest = RequirePriorPreviewDigest(request);
        var primitivePayload = _command.SerializeRequest();
        byte[] step;
        IndustrialPrimitiveResponse primitive;
        await using (var invocation = await _runner.ExecuteAsync(primitivePayload, [], cancellationToken).ConfigureAwait(false))
        {
            primitive = _command.ParseResponse(invocation.Response);
            step = await ArtifactReader.ReadSealedAsync(
                invocation.OutputDirectory,
                "model.step",
                primitive.Artifact,
                ProtocolV1.MaximumStepBytes,
                cancellationToken).ConfigureAwait(false);
        }
        var previewPlan = BuildCompletePreview(request, step, primitive.Artifact);
        var previewPayload = ProtocolV1.SerializePreview(previewPlan.Command);
        byte[] glb;
        IndustrialPreviewResponse preview;
        await using (var invocation = await _runner.ExecuteAsync(
            previewPayload,
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
        var mutation = MutationMapperV1.Map(
            _boundRequest,
            _command,
            primitive,
            step,
            preview,
            glb,
            previewPlan.Command,
            priorPreviewDigest,
            _evidence.ProviderEvidence);
        Volatile.Write(ref _mutationId, mutation.MutationId);
        return mutation;
    }

    internal ValueTask RevokeAsync(PhotonCadSealedMutationDelta mutation, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 128 || reason.Any(char.IsControl))
            throw new ArgumentException("industrial_compensation_reason_invalid", nameof(reason));
        var expected = Volatile.Read(ref _mutationId);
        if (expected is null || !StringComparer.Ordinal.Equals(expected, mutation.MutationId)
            || !StringComparer.Ordinal.Equals(mutation.RequestId, _boundRequest.RequestId))
            throw Failure("industrial_compensation_binding_mismatch");
        Interlocked.Exchange(ref _revoked, 1);
        return ValueTask.CompletedTask;
    }

    private void RequireBoundRequest(PhotonCadSealedMutationProviderRequest request)
    {
        if (!ReferenceEquals(request.Request, _boundRequest)
            || request.Units != PhotonCadProjectUnit.Millimeter
            || request.Request.Mode != PhotonCadOperationModeV1.Scratch
            || request.Request.TargetEntityIds.Count != 1
            || !StringComparer.Ordinal.Equals(request.Request.TargetEntityIds[0], _command.EntityId)
            || request.ExistingEntityIds.Contains(_command.EntityId, StringComparer.OrdinalIgnoreCase))
            throw Failure("industrial_provider_request_not_bound");
    }

    private static string? RequirePriorPreviewDigest(PhotonCadSealedMutationProviderRequest request)
    {
        var previews = request.BaseArtifacts
            .Where(artifact => artifact.Role == PhotonCadArtifactRoleV1.ProjectPreview)
            .ToArray();
        if (previews.Length > 1) throw Failure("industrial_base_preview_not_unique");
        if (previews.Length == 0) return null;
        var preview = previews[0];
        if (preview.Kind != PhotonCadArtifactKindV1.Glb
            || preview.OwnerEntityId is not null
            || preview.Bounds is null
            || preview.Revision != request.Request.BaseRevision
            || !StringComparer.Ordinal.Equals(preview.MediaType, "model/gltf-binary"))
            throw Failure("industrial_base_preview_invalid");
        return preview.ContentDigest;
    }

    private CompletePreviewPlan BuildCompletePreview(
        PhotonCadSealedMutationProviderRequest request,
        byte[] newStep,
        IndustrialArtifactClaim newClaim)
    {
        var parts = new List<PreviewPart>();
        foreach (var artifact in request.BaseArtifacts.Where(artifact => artifact.Role == PhotonCadArtifactRoleV1.AuthoritativeGeometry))
        {
            if (artifact.Kind != PhotonCadArtifactKindV1.Step
                || artifact.OwnerEntityId is null
                || artifact.Bounds is not null
                || !StringComparer.Ordinal.Equals(artifact.MediaType, "model/step")
                || artifact.ByteLength != artifact.Content.Length
                || !ProtocolV1.FixedDigestEquals(ProtocolV1.Sha256(artifact.Content.Span), artifact.ContentDigest))
                throw Failure("industrial_base_geometry_invalid");
            parts.Add(new PreviewPart(artifact.OwnerEntityId, artifact.Content.ToArray(), artifact.ContentDigest));
        }
        parts.Add(new PreviewPart(_command.EntityId, newStep.ToArray(), newClaim.ContentDigest));
        parts = parts.OrderBy(part => part.SourcePartId, StringComparer.Ordinal).ToList();
        if (parts.Count > 256 || parts.Select(part => part.SourcePartId).Distinct(StringComparer.Ordinal).Count() != parts.Count)
            throw Failure("industrial_preview_source_set_rejected");
        var sources = new List<IndustrialPreviewSource>(parts.Count);
        var inputs = new List<IndustrialInputArtifact>(parts.Count);
        for (var index = 0; index < parts.Count; index++)
        {
            var part = parts[index];
            var slot = $"part{index:D4}";
            sources.Add(new IndustrialPreviewSource(part.SourcePartId, slot, part.Digest, part.Content.LongLength));
            inputs.Add(new IndustrialInputArtifact(slot, part.Content, part.Digest));
        }

        var baseOccurrences = request.BaseOccurrences
            .Select(occurrence => new IndustrialPreviewOccurrence(
                occurrence.OccurrenceId,
                occurrence.SourceEntityId,
                occurrence.ParentOccurrenceId,
                occurrence.Transform.ToArray()))
            .ToArray();
        var priorRoots = baseOccurrences.Where(occurrence => occurrence.ParentEntityId is null).ToArray();
        if (baseOccurrences.Length > 0 && priorRoots.Length != 1)
            throw Failure("industrial_base_occurrence_root_invalid");
        var occurrences = baseOccurrences
            .Append(new IndustrialPreviewOccurrence(
                $"{_command.EntityId}.occ",
                _command.EntityId,
                ParentEntityId: priorRoots.SingleOrDefault()?.EntityId,
                MutationMapperV1.IdentityTransform.ToArray()))
            .OrderBy(occurrence => occurrence.EntityId, StringComparer.Ordinal)
            .ToArray();
        if (occurrences.Length > 1024
            || occurrences.Select(occurrence => occurrence.EntityId).Distinct(StringComparer.Ordinal).Count() != occurrences.Length)
            throw Failure("industrial_preview_occurrence_set_rejected");
        var usedSources = occurrences.Select(occurrence => occurrence.SourcePartId).ToHashSet(StringComparer.Ordinal);
        if (!usedSources.SetEquals(parts.Select(part => part.SourcePartId)))
            throw Failure("industrial_preview_source_coverage_rejected");
        return new CompletePreviewPlan(new IndustrialPreviewCommand(sources, occurrences), inputs);
    }

    private static InvalidOperationException Failure(string code) => new(code);

    private sealed record PreviewPart(string SourcePartId, byte[] Content, string Digest);
    private sealed record CompletePreviewPlan(IndustrialPreviewCommand Command, IReadOnlyList<IndustrialInputArtifact> Inputs);
}
