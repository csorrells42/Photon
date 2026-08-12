using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PhotonCadProjects;
using PhotonCadProjects.Codec;
using PhotonCadProjects.RuntimeSync;
using PhotonCadRuntime.IndustrialProvider;

var smoke = new Smoke();
return await smoke.RunAsync();

internal sealed class Smoke
{
    private int _passed;
    private int _failed;

    internal async Task<int> RunAsync()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("exact evidence receipt and redistribution block", EvidenceAsync),
            ("bound box emits copied STEP and GLB", BoundBoxAsync),
            ("first and second mutations round-trip preview CAS", FirstAndSecondMutationAsync),
            ("assembly place nested transform preserves STEP and derives BOM", AssemblyPlaceTransformAsync),
            ("assembly adversarial bindings fail closed", AssemblyAdversarialAsync),
            ("bound cylinder preserves normalized scalars", BoundCylinderAsync),
            ("equal foreign request identity rejected", ForeignRequestAsync),
            ("provider is one shot", OneShotAsync),
            ("compensation is bound and idempotent", CompensationAsync),
            ("protocol rejects unknown members", UnknownMemberAsync),
            ("protocol rejects duplicate members", DuplicateMemberAsync),
            ("artifact digest mismatch rejected", DigestMismatchAsync),
            ("GLB external URI rejected", ExternalUriAsync),
            ("catalog cache copies and loads once", CatalogCacheAsync),
            ("dynamic bearing and Spur Gear catalog item persists", DynamicCatalogItemAsync),
            ("verified STEP part import persists with complete GLB preview", ImportedStepPartAsync),
            ("public mutation seam leaks no path or process type", PublicSurfaceAsync),
        };
        foreach (var test in tests)
        {
            try
            {
                await test.Run();
                _passed++;
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception exception)
            {
                _failed++;
                Console.Error.WriteLine($"FAIL {test.Name}: {exception.GetType().Name}: {exception.Message}");
            }
        }
        Console.WriteLine($"PhotonCadRuntime.IndustrialProvider.Smoke: {_passed}/{_passed + _failed} passed");
        return _failed == 0 ? 0 : 1;
    }

    private static async Task EvidenceAsync()
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..",
            "runtime-assets", "photon-cad-industrial", "evidence-selection.json"));
        var evidence = await EvidenceVerifier.VerifyAsync(path, CancellationToken.None);
        Equal(EvidenceVerifier.AcceptedReceiptSha256, evidence.ReceiptSha256, "receipt digest");
        Equal(EvidenceVerifier.AcceptedDerivedImageId, evidence.DerivedImageId, "derived image");
        Equal("redistribution-blocked", evidence.ProviderEvidence.Source.License, "redistribution status");
    }

    private static async Task BoundBoxAsync()
    {
        await using var runner = new FakeRunner();
        var (bound, providerRequest) = await BoxAsync(runner);
        var mutation = await bound.Provider.ApplyAsync(providerRequest);
        Equal(2L, mutation.ResultingRevision, "result revision");
        Equal(2, mutation.Operations.Count, "operation count");
        Equal("industrial.preview.glb.v1", mutation.Operations[1].CapabilityId, "preview capability");
        Equal(1, mutation.Entities.Count, "entity count");
        Equal(1, mutation.Occurrences.Count, "occurrence count");
        Equal(1, mutation.Bom.Count, "BOM row count");
        Equal("BOX", mutation.Bom[0].PartNumber, "BOM part number");
        Equal("box-root", mutation.Bom[0].SourceEntityId, "BOM source entity");
        Equal(2, mutation.Artifacts.Count, "artifact count");
        True(mutation.Artifacts.Any(value => value.Kind == PhotonCadArtifactKindV1.Step), "STEP missing");
        True(mutation.Artifacts.Any(value => value.Kind == PhotonCadArtifactKindV1.Glb), "GLB missing");
        var stepArtifact = mutation.Artifacts.Single(value => value.Kind == PhotonCadArtifactKindV1.Step);
        var previewArtifact = mutation.Artifacts.Single(value => value.Kind == PhotonCadArtifactKindV1.Glb);
        True(stepArtifact.Bounds is null, "authoritative STEP bounds must be null");
        True(previewArtifact.ReplacesContentDigest is null, "first preview must not claim replacement");
        True(mutation.Artifacts.All(value => value.ByteLength <= PhotonCadRuntimeSyncContract.MaximumSealedArtifactBytes), "per artifact codec bound");
        True(mutation.TotalArtifactBytes <= PhotonCadRuntimeSyncContract.MaximumSealedArtifactBytesPerMutation, "aggregate codec bound");
        True(runner.Requests.Count == 2 && runner.Requests.All(value => !ContainsForbiddenTransportTruth(value)), "transport truth leaked");
        var step = mutation.Artifacts.Single(value => value.Kind == PhotonCadArtifactKindV1.Step).Content.ToArray();
        runner.OverwriteLastOutput();
        ArtifactReader.ValidateStep(step);
    }

    private static async Task FirstAndSecondMutationAsync()
    {
        await using var runner = new FakeRunner();
        var runtime = Runtime(runner);
        const string sessionId = "pcsid:two-mutations";
        const string projectId = "pcpid:two-mutations";
        var codec = new PhotonCadCanonicalProjectCodecV1(new FixedIdentityIssuer(sessionId, projectId));
        var mapper = new PhotonCadRuntimeCanonicalMapperV1(codec, IndustrialPolicy());
        var initial = await codec.CreateAsync("Two industrial mutations", PhotonCadProjectUnit.Millimeter);
        var handle = new PhotonCadProjectHandle("cad-project:11111111111111111111111111111111");

        var first = runtime.BindBox(
            "request-first-canonical",
            sessionId,
            projectId,
            initial.Revision,
            "first-root",
            10,
            20,
            30);
        var firstBinding = new PhotonCadCanonicalMutationBinding(handle, initial);
        var firstProviderRequest = mapper.PrepareProviderRequest(firstBinding, first.Request);
        var firstMutation = await first.Provider.ApplyAsync(firstProviderRequest);
        var firstStep = firstMutation.Artifacts.Single(artifact => artifact.Role == PhotonCadArtifactRoleV1.AuthoritativeGeometry);
        var firstPreviewDelta = firstMutation.Artifacts.Single(artifact => artifact.Role == PhotonCadArtifactRoleV1.ProjectPreview);
        True(firstStep.Bounds is null, "first STEP carried forbidden bounds");
        True(firstPreviewDelta.ReplacesContentDigest is null, "first preview replacement must be null");
        var firstUpdated = mapper.Apply(firstBinding, first.Request, firstMutation);
        var firstSaved = codec.MarkSaved(firstUpdated);
        var firstState = codec.Inspect(codec.Decode(firstSaved.CanonicalBytes));
        var firstPreview = firstState.Artifacts.Single(artifact => artifact.Role == PhotonCadArtifactRoleV1.ProjectPreview);
        Equal(2L, firstState.Revision, "first canonical revision");
        True(firstState.Artifacts.Single(artifact => artifact.Role == PhotonCadArtifactRoleV1.AuthoritativeGeometry).Bounds is null,
            "first canonical STEP bounds");

        var second = runtime.BindCylinder(
            "request-second-canonical",
            sessionId,
            projectId,
            firstSaved.Revision,
            "second-root",
            5,
            12);
        var secondBinding = new PhotonCadCanonicalMutationBinding(handle, firstSaved);
        var secondProviderRequest = mapper.PrepareProviderRequest(secondBinding, second.Request);
        var secondMutation = await second.Provider.ApplyAsync(secondProviderRequest);
        var secondPreviewDelta = secondMutation.Artifacts.Single(artifact => artifact.Role == PhotonCadArtifactRoleV1.ProjectPreview);
        Equal(firstPreview.Digest, secondPreviewDelta.ReplacesContentDigest, "second preview CAS digest");
        var secondUpdated = mapper.Apply(secondBinding, second.Request, secondMutation);
        var secondSaved = codec.MarkSaved(secondUpdated);
        var reopened = codec.Decode(secondSaved.CanonicalBytes);
        var secondState = codec.Inspect(reopened);
        Equal(4L, secondState.Revision, "second canonical revision");
        Equal(2, secondState.Entities.Count, "second canonical entity count");
        Equal(2, secondState.Bom.Count, "second canonical BOM count");
        True(secondState.Bom.Select(row => row.SourceEntityId).ToHashSet(StringComparer.Ordinal)
            .SetEquals(["first-root", "second-root"]), "second canonical BOM source binding");
        Equal(2, secondState.Artifacts.Count(artifact => artifact.Role == PhotonCadArtifactRoleV1.AuthoritativeGeometry),
            "second canonical geometry count");
        Equal(1, secondState.Artifacts.Count(artifact => artifact.Role == PhotonCadArtifactRoleV1.ProjectPreview),
            "second canonical preview count");
        True(secondState.Artifacts
            .Where(artifact => artifact.Role == PhotonCadArtifactRoleV1.AuthoritativeGeometry)
            .All(artifact => artifact.Bounds is null), "reopened STEP bounds must all be null");
        var secondPreview = secondState.Artifacts.Single(artifact => artifact.Role == PhotonCadArtifactRoleV1.ProjectPreview);
        Equal(secondPreviewDelta.ContentDigest, secondPreview.Digest, "reopened preview digest");
        True(!ProtocolV1.FixedDigestEquals(firstPreview.Digest, secondPreview.Digest), "second preview did not replace first bytes");
        using var secondPreviewRequest = JsonDocument.Parse(runner.Requests[3]);
        Equal(2, secondPreviewRequest.RootElement.GetProperty("sources").GetArrayLength(), "complete preview source count");
        Equal(2, secondPreviewRequest.RootElement.GetProperty("occurrences").GetArrayLength(), "complete preview occurrence count");
        var tags = ReadGlbEntityTags(secondPreview.Content.Span);
        True(tags.SetEquals(["first-root.occ", "second-root.occ"]), "complete preview entity tags");

        var verified = await runtime.VerifyCommittedAsync(secondSaved,
            [
                PhotonCadCommittedVerificationCheck.ValidSolids,
                PhotonCadCommittedVerificationCheck.Dimensions,
                PhotonCadCommittedVerificationCheck.AssemblyStructure,
                PhotonCadCommittedVerificationCheck.ExportReadiness,
            ]);
        True(verified.Available && verified.Passed && verified.Revision == 4, "committed revision-four verification");
        var callsBeforeUnavailable = runner.Requests.Count;
        var interference = await runtime.VerifyCommittedAsync(secondSaved,
            [PhotonCadCommittedVerificationCheck.Interference]);
        True(!interference.Available && !interference.Passed && interference.Reason == "interference_unavailable",
            "interference must remain explicitly unavailable");
        Equal(callsBeforeUnavailable, runner.Requests.Count, "unavailable interference invoked no container work");

        runner.ForeignNextPreview = true;
        var foreign = await runtime.VerifyCommittedAsync(secondSaved,
            [PhotonCadCommittedVerificationCheck.ValidSolids]);
        True(foreign.Available && !foreign.Passed,
            "foreign rerun GLB reported a completed failed check");
        var malformedBytes = secondSaved.CanonicalBytes.ToArray();
        malformedBytes[^1] ^= 0x5a;
        var malformedProject = new PhotonCadCanonicalProject(
            secondSaved.SessionId, secondSaved.ProjectId, secondSaved.Revision, secondSaved.DisplayName,
            secondSaved.Units, secondSaved.ContentDigest, secondSaved.BomDigest, secondSaved.Dirty,
            malformedBytes, secondSaved.Bom);
        var malformed = await runtime.VerifyCommittedAsync(malformedProject,
            [PhotonCadCommittedVerificationCheck.ValidSolids]);
        True(malformed.Available && !malformed.Passed, "malformed committed project reported a completed failed check");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => runtime.VerifyCommittedAsync(secondSaved,
            [PhotonCadCommittedVerificationCheck.ExportReadiness], cancelled.Token).AsTask(), "canceled");
    }

    private static async Task BoundCylinderAsync()
    {
        await using var runner = new FakeRunner();
        var runtime = Runtime(runner);
        var bound = runtime.BindCylinder("request-cylinder", "pcsid:cylinder", "pcpid:cylinder", 0, "cylinder-root", 5, 12);
        var providerRequest = await ProviderRequestAsync(bound.Request);
        var mutation = await bound.Provider.ApplyAsync(providerRequest);
        var operation = mutation.Operations[0];
        Equal("radiusMm", operation.Inputs[0].Id, "radius input");
        Equal("heightMm", operation.Inputs[1].Id, "height input");
        True(runner.Requests[0].Contains("\"radiusMm\":5", StringComparison.Ordinal), "normalized radius not serialized");
    }

    private static async Task AssemblyPlaceTransformAsync()
    {
        await using var runner = new FakeRunner();
        var runtime = Runtime(runner);
        const string sessionId = "pcsid:assembly-flow";
        const string projectId = "pcpid:assembly-flow";
        var codec = new PhotonCadCanonicalProjectCodecV1(new FixedIdentityIssuer(sessionId, projectId));
        var mapper = new PhotonCadRuntimeCanonicalMapperV1(codec, IndustrialPolicy());
        var handle = new PhotonCadProjectHandle("cad-project:22222222222222222222222222222222");
        var initial = await codec.CreateAsync("Assembly flow", PhotonCadProjectUnit.Millimeter);
        var root = runtime.BindBox("assembly-root-create", sessionId, projectId, 0, "assembly-root", 10, 20, 30, "ROOT");
        var rootBinding = new PhotonCadCanonicalMutationBinding(handle, initial);
        var rootMutation = await root.Provider.ApplyAsync(mapper.PrepareProviderRequest(rootBinding, root.Request));
        var rootSaved = codec.MarkSaved(mapper.Apply(rootBinding, root.Request, rootMutation));
        var rootState = codec.Inspect(rootSaved);
        var originalStep = rootState.Artifacts.Single(value => value.Role == PhotonCadArtifactRoleV1.AuthoritativeGeometry);
        var originalPreview = rootState.Artifacts.Single(value => value.Role == PhotonCadArtifactRoleV1.ProjectPreview);
        Equal(1d, rootState.Bom.Single().Quantity, "root BOM quantity");

        var place = runtime.BindAssemblyPlace(
            "assembly-place-one", sessionId, projectId, rootSaved.Revision,
            "assembly-copy-1", "assembly-root", "assembly-root.occ", Translation(100, 0, 0));
        var placeBinding = new PhotonCadCanonicalMutationBinding(handle, rootSaved);
        var placeMutation = await place.Provider.ApplyAsync(mapper.PrepareProviderRequest(placeBinding, place.Request));
        Equal(4L, placeMutation.ResultingRevision, "place resulting revision");
        Equal(2, placeMutation.Operations.Count, "place operation count");
        Equal(PhotonCadCollectionMergeMode.ReplaceAll, placeMutation.OccurrenceMergeMode, "place occurrence merge");
        Equal(PhotonCadCollectionMergeMode.ReplaceAll, placeMutation.BomMergeMode, "place BOM merge");
        Equal(0, placeMutation.Entities.Count, "place entity count");
        Equal(2, placeMutation.Occurrences.Count, "place occurrence count");
        Equal(2d, placeMutation.Bom.Single().Quantity, "place BOM 1 to 2");
        Equal(1, placeMutation.Artifacts.Count, "place artifact count");
        True(placeMutation.Artifacts.All(value => value.Kind == PhotonCadArtifactKindV1.Glb), "place emitted non-preview artifact");
        Equal(originalPreview.Digest, placeMutation.Artifacts.Single().ReplacesContentDigest, "place preview CAS");
        var placedSaved = codec.MarkSaved(mapper.Apply(placeBinding, place.Request, placeMutation));
        AssertStepUnchanged(codec.Inspect(placedSaved), originalStep);

        var transform = runtime.BindAssemblyTransform(
            "assembly-transform-one", sessionId, projectId, placedSaved.Revision,
            "assembly-copy-1", "assembly-root", Translation(100, 50, 0));
        var transformBinding = new PhotonCadCanonicalMutationBinding(handle, placedSaved);
        var transformMutation = await transform.Provider.ApplyAsync(mapper.PrepareProviderRequest(transformBinding, transform.Request));
        Equal(2, transformMutation.Operations.Count, "transform operation count");
        Equal(2d, transformMutation.Bom.Single().Quantity, "transform changed BOM quantity");
        var transformedSaved = codec.MarkSaved(mapper.Apply(transformBinding, transform.Request, transformMutation));
        var transformedState = codec.Inspect(transformedSaved);
        Equal(6L, transformedState.Revision, "transform resulting revision");
        True(transformedState.Occurrences.Single(value => value.OccurrenceId == "assembly-copy-1")
            .Transform.SequenceEqual(Translation(100, 50, 0)), "transform was not persisted exactly");
        AssertStepUnchanged(transformedState, originalStep);

        var nested = runtime.BindAssemblyPlace(
            "assembly-place-nested", sessionId, projectId, transformedSaved.Revision,
            "assembly-copy-2", "assembly-root", "assembly-copy-1", Translation(0, 25, 0));
        var nestedBinding = new PhotonCadCanonicalMutationBinding(handle, transformedSaved);
        var nestedMutation = await nested.Provider.ApplyAsync(mapper.PrepareProviderRequest(nestedBinding, nested.Request));
        Equal(2, nestedMutation.Operations.Count, "nested place operation count");
        Equal(3d, nestedMutation.Bom.Single().Quantity, "nested place BOM quantity");
        var nestedSaved = codec.MarkSaved(mapper.Apply(nestedBinding, nested.Request, nestedMutation));
        var nestedState = codec.Inspect(codec.Decode(nestedSaved.CanonicalBytes));
        Equal(8L, nestedState.Revision, "nested resulting revision");
        Equal("assembly-copy-1", nestedState.Occurrences.Single(value => value.OccurrenceId == "assembly-copy-2").ParentOccurrenceId,
            "nested parent binding");
        AssertStepUnchanged(nestedState, originalStep);
        using var nestedPreviewRequest = JsonDocument.Parse(runner.Requests[^1]);
        Equal(1, nestedPreviewRequest.RootElement.GetProperty("sources").GetArrayLength(), "assembly source reuse count");
        Equal(3, nestedPreviewRequest.RootElement.GetProperty("occurrences").GetArrayLength(), "nested preview occurrence count");

        var remove = runtime.BindAssemblyRemove(
            "assembly-remove-branch", sessionId, projectId, nestedSaved.Revision,
            "assembly-copy-1", "assembly-root");
        var removeBinding = new PhotonCadCanonicalMutationBinding(handle, nestedSaved);
        var removeMutation = await remove.Provider.ApplyAsync(mapper.PrepareProviderRequest(removeBinding, remove.Request));
        Equal(10L, removeMutation.ResultingRevision, "remove resulting revision");
        Equal(2, removeMutation.Operations.Count, "remove operation count");
        Equal(1, removeMutation.Occurrences.Count, "remove occurrence and descendants");
        Equal("assembly-root.occ", removeMutation.Occurrences[0].OccurrenceId, "remove preserved root");
        Equal(1d, removeMutation.Bom.Single().Quantity, "remove BOM quantity");
        var removedSaved = codec.MarkSaved(mapper.Apply(removeBinding, remove.Request, removeMutation));
        var removedState = codec.Inspect(codec.Decode(removedSaved.CanonicalBytes));
        Equal(1, removedState.Occurrences.Count, "remove persisted occurrence count");
        Equal(1, removedState.Bom.Count, "remove persisted BOM count");
        Equal(1, removedState.Entities.Count(value => value.Kind == PhotonCadEntityKindV1.Body), "remove retained source body");
        AssertStepUnchanged(removedState, originalStep);

        var removeLast = runtime.BindAssemblyRemove(
            "assembly-remove-last", sessionId, projectId, removedSaved.Revision,
            "assembly-root.occ", "assembly-root");
        await ThrowsAsync<InvalidOperationException>(() => removeLast.Provider.ApplyAsync(
            mapper.PrepareProviderRequest(new PhotonCadCanonicalMutationBinding(handle, removedSaved), removeLast.Request)).AsTask(),
            "last_occurrence");
    }

    private static async Task AssemblyAdversarialAsync()
    {
        await using var runner = new FakeRunner();
        var runtime = Runtime(runner);
        const string sessionId = "pcsid:assembly-hostile";
        const string projectId = "pcpid:assembly-hostile";
        var codec = new PhotonCadCanonicalProjectCodecV1(new FixedIdentityIssuer(sessionId, projectId));
        var mapper = new PhotonCadRuntimeCanonicalMapperV1(codec, IndustrialPolicy());
        var handle = new PhotonCadProjectHandle("cad-project:33333333333333333333333333333333");
        var initial = await codec.CreateAsync("Assembly hostile", PhotonCadProjectUnit.Millimeter);
        var root = runtime.BindBox("assembly-hostile-root", sessionId, projectId, 0, "hostile-root", 10, 20, 30, "ROOT");
        var initialBinding = new PhotonCadCanonicalMutationBinding(handle, initial);
        var rootMutation = await root.Provider.ApplyAsync(mapper.PrepareProviderRequest(initialBinding, root.Request));
        var saved = codec.MarkSaved(mapper.Apply(initialBinding, root.Request, rootMutation));
        var binding = new PhotonCadCanonicalMutationBinding(handle, saved);

        Throws<ArgumentException>(() => runtime.BindAssemblyTransform(
            "bad-reflection", sessionId, projectId, 2, "hostile-root.occ", "hostile-root", Reflection()), "orientation");
        Throws<ArgumentException>(() => runtime.BindAssemblyTransform(
            "bad-scale", sessionId, projectId, 2, "hostile-root.occ", "hostile-root", Scale()), "not_rigid");
        Throws<ArgumentException>(() => runtime.BindAssemblyTransform(
            "bad-shear", sessionId, projectId, 2, "hostile-root.occ", "hostile-root", Shear()), "not_rigid");
        var nan = Translation(0, 0, 0); nan[0] = double.NaN;
        Throws<ArgumentException>(() => runtime.BindAssemblyTransform(
            "bad-nan", sessionId, projectId, 2, "hostile-root.occ", "hostile-root", nan), "number");

        var stale = runtime.BindAssemblyTransform(
            "assembly-stale", sessionId, projectId, 1, "hostile-root.occ", "hostile-root", Translation(0, 0, 0));
        Throws<PhotonCadRuntimeSyncException>(() => mapper.PrepareProviderRequest(binding, stale.Request), "canonical_base_binding_mismatch");

        var missing = runtime.BindAssemblyTransform(
            "assembly-missing", sessionId, projectId, 2, "missing.occ", "hostile-root", Translation(0, 0, 0));
        await ThrowsAsync<InvalidOperationException>(() => missing.Provider.ApplyAsync(
            mapper.PrepareProviderRequest(binding, missing.Request)).AsTask(), "target_missing");
        var duplicate = runtime.BindAssemblyPlace(
            "assembly-duplicate", sessionId, projectId, 2, "hostile-root.occ", "hostile-root", "hostile-root.occ", Translation(0, 0, 0));
        await ThrowsAsync<InvalidOperationException>(() => duplicate.Provider.ApplyAsync(
            mapper.PrepareProviderRequest(binding, duplicate.Request)).AsTask(), "duplicate");
        var missingParent = runtime.BindAssemblyPlace(
            "assembly-parent-missing", sessionId, projectId, 2, "new.occ", "hostile-root", "missing-parent.occ", Translation(0, 0, 0));
        await ThrowsAsync<InvalidOperationException>(() => missingParent.Provider.ApplyAsync(
            mapper.PrepareProviderRequest(binding, missingParent.Request)).AsTask(), "parent_missing");

        Throws<InvalidOperationException>(() => AssemblyMutationProvider.ValidateOccurrenceDag(
        [
            new PhotonCadOccurrenceV1("root.occ", null, "ROOT", "hostile-root", Translation(0, 0, 0)),
            new PhotonCadOccurrenceV1("cycle-a.occ", "cycle-b.occ", "ROOT", "hostile-root", Translation(0, 0, 0)),
            new PhotonCadOccurrenceV1("cycle-b.occ", "cycle-a.occ", "ROOT", "hostile-root", Translation(0, 0, 0)),
        ]), "cycle");

        var place = runtime.BindAssemblyPlace(
            "assembly-cas", sessionId, projectId, 2, "cas-copy.occ", "hostile-root", "hostile-root.occ", Translation(10, 0, 0));
        var mutation = await place.Provider.ApplyAsync(mapper.PrepareProviderRequest(binding, place.Request));
        var preview = mutation.Artifacts.Single();
        var wrongPreview = new PhotonCadSealedArtifactDelta(
            preview.Role, preview.Kind, preview.OwnerEntityId, preview.Revision, preview.Content,
            preview.ByteLength, preview.ContentDigest, preview.MediaType, preview.Bounds,
            preview.OperationId, preview.Evidence, "sha256:" + new string('f', 64));
        var wrongCas = new PhotonCadSealedMutationDelta(
            "assembly-wrong-cas", mutation.RequestId, mutation.SessionId, mutation.ProjectId,
            mutation.BaseRevision, mutation.ResultingRevision, mutation.Operations,
            mutation.Entities, mutation.Occurrences, mutation.Issues, mutation.Bom, [wrongPreview],
            mutation.OccurrenceMergeMode, mutation.IssueMergeMode, mutation.BomMergeMode);
        Throws<PhotonCadRuntimeSyncException>(() => mapper.Apply(binding, place.Request, wrongCas), "replacement_binding");
        await place.Compensator.CompensateAsync(mutation, "commit failed");
        await place.Compensator.CompensateAsync(mutation, "commit retry failed");
    }

    private static void AssertStepUnchanged(PhotonCadProjectStateV1 state, PhotonCadArtifactV1 expected)
    {
        var actual = state.Artifacts.Single(value => value.Role == PhotonCadArtifactRoleV1.AuthoritativeGeometry);
        Equal(expected.Digest, actual.Digest, "assembly STEP digest changed");
        Equal(expected.ByteLength, actual.ByteLength, "assembly STEP length changed");
        True(expected.Content.Span.SequenceEqual(actual.Content.Span), "assembly STEP bytes changed");
    }

    private static double[] Translation(double x, double y, double z) =>
    [
        1, 0, 0, x,
        0, 1, 0, y,
        0, 0, 1, z,
        0, 0, 0, 1,
    ];

    private static double[] Reflection() =>
    [
        -1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    ];

    private static double[] Scale() =>
    [
        2, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    ];

    private static double[] Shear() =>
    [
        1, 0, 0, 0,
        .5, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    ];

    private static async Task ForeignRequestAsync()
    {
        await using var runner = new FakeRunner();
        var runtime = Runtime(runner);
        var bound = runtime.BindBox("request-foreign", "pcsid:foreign", "pcpid:foreign", 0, "foreign-root", 10, 20, 30);
        var foreign = new PhotonCadRuntimeSyncRequest(
            bound.Request.RequestId,
            bound.Request.SessionId,
            bound.Request.ProjectId,
            bound.Request.BaseRevision,
            bound.Request.CapabilityId,
            bound.Request.Mode,
            bound.Request.Inputs,
            bound.Request.TargetEntityIds);
        var providerRequest = await ProviderRequestAsync(foreign);
        await ThrowsAsync<InvalidOperationException>(() => bound.Provider.ApplyAsync(providerRequest).AsTask(), "not_bound");
        Equal(0, runner.Requests.Count, "foreign request reached runner");
    }

    private static async Task OneShotAsync()
    {
        await using var runner = new FakeRunner();
        var (bound, providerRequest) = await BoxAsync(runner, "oneshot");
        _ = await bound.Provider.ApplyAsync(providerRequest);
        await ThrowsAsync<InvalidOperationException>(() => bound.Provider.ApplyAsync(providerRequest).AsTask(), "single_use");
    }

    private static async Task CompensationAsync()
    {
        await using var runner = new FakeRunner();
        var (bound, providerRequest) = await BoxAsync(runner, "compensate");
        var mutation = await bound.Provider.ApplyAsync(providerRequest);
        await bound.Compensator.CompensateAsync(mutation, "commit_failed");
        await bound.Compensator.CompensateAsync(mutation, "commit_failed_retry");
    }

    private static Task UnknownMemberAsync()
    {
        var command = BoxCommand();
        var payload = Encoding.UTF8.GetBytes(PrimitiveResponse(command, Step(), addUnknown: true));
        Throws<InvalidDataException>(() => ProtocolV1.ParsePrimitiveResponse(payload, command), "unexpected_protocol_member");
        return Task.CompletedTask;
    }

    private static Task DuplicateMemberAsync()
    {
        var command = BoxCommand();
        var valid = PrimitiveResponse(command, Step());
        var payload = Encoding.UTF8.GetBytes(valid.Replace("\"ok\":true", "\"ok\":true,\"ok\":true", StringComparison.Ordinal));
        Throws<InvalidDataException>(() => ProtocolV1.ParsePrimitiveResponse(payload, command), "duplicate_protocol_member");
        return Task.CompletedTask;
    }

    private static async Task DigestMismatchAsync()
    {
        await using var runner = new FakeRunner { CorruptPrimitiveDigest = true };
        var (bound, providerRequest) = await BoxAsync(runner, "digest");
        await ThrowsAsync<InvalidDataException>(() => bound.Provider.ApplyAsync(providerRequest).AsTask(), "artifact_digest_mismatch");
    }

    private static Task ExternalUriAsync()
    {
        var command = new IndustrialPreviewCommand(
            [new IndustrialPreviewSource("root", "part0000", ProtocolV1.Sha256(Step()), Step().LongLength)],
            [new IndustrialPreviewOccurrence("root.occ", "root", null, MutationMapperV1.IdentityTransform)]);
        var bytes = Glb(command, externalUri: true);
        Throws<InvalidDataException>(() => GlbValidator.Validate(bytes, command, new IndustrialBounds(0, 0, 0, 1, 1, 1)), "uri");
        return Task.CompletedTask;
    }

    private static async Task CatalogCacheAsync()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"schema\":\"photon.cad.industrial.catalog/v1\",\"items\":[]}");
        var calls = 0;
        var cache = new CatalogCache(_ =>
        {
            calls++;
            return ValueTask.FromResult(bytes.ToArray());
        }, ProtocolV1.Sha256(bytes));
        var first = await cache.GetAsync(CancellationToken.None);
        var second = await cache.GetAsync(CancellationToken.None);
        Equal(1, calls, "catalog load count");
        True(first.Span.SequenceEqual(second.Span), "catalog cache bytes");
    }

    private static async Task DynamicCatalogItemAsync()
    {
        await using var runner = new FakeRunner();
        var runtime = Runtime(runner);
        var catalog = await runtime.GetCatalogAsync();
        Equal(3, catalog.Items.Count, "mounted catalog item count");
        var bearing = catalog.Items.Single(item => item.Category == "bearings");
        var gear = catalog.Items.Single(item => item.Title == "Spur Gear");
        var fastener = catalog.Items.Single(item => item.Category == "fasteners");
        Equal(1, bearing.Parameters.Count, "bearing parameter count");
        Equal(1, bearing.Parameters[0].Choices.Count, "bearing choice count");
        Equal(4, gear.Parameters.Count, "gear required parameter count");
        Equal("Socket Head Cap Screw", fastener.Title, "fastener title");

        var bound = await runtime.BindCatalogItemAsync(
            "request-catalog-gear",
            "pcsid:catalog-gear",
            "pcpid:catalog-gear",
            0,
            "catalog-gear-root",
            gear.CapabilityId,
            new Dictionary<string, PhotonCadIndustrialCatalogInputValue?>
            {
                ["module"] = PhotonCadIndustrialCatalogInputValue.Number(2),
                ["pressure_angle"] = PhotonCadIndustrialCatalogInputValue.Number(20),
                ["thickness"] = PhotonCadIndustrialCatalogInputValue.Number(10),
                ["tooth_count"] = PhotonCadIndustrialCatalogInputValue.Integer(24),
            });
        var request = await ProviderRequestAsync(bound.Request);
        var mutation = await bound.Provider.ApplyAsync(request);
        Equal(2L, mutation.ResultingRevision, "catalog result revision");
        Equal(PhotonCadEntityKindV1.Part, mutation.Entities.Single().Kind, "catalog entity kind");
        Equal("Spur Gear", mutation.Entities.Single().Name, "catalog entity name");
        Equal(1, mutation.Bom.Count, "catalog BOM count");
        True(runner.Requests.Any(value => value.Contains("\"operation\":\"createCatalogItem\"", StringComparison.Ordinal)),
            "catalog item request missing");
        True(runner.Requests.Count(value => value.Contains("\"operation\":\"catalog\"", StringComparison.Ordinal)) == 1,
            "catalog cache did not load exactly once");
    }

    private static Task PublicSurfaceAsync()
    {
        var forbidden = new[]
        {
            typeof(Process), typeof(Stream), typeof(FileInfo), typeof(DirectoryInfo),
            typeof(System.Runtime.InteropServices.SafeHandle),
        };
        foreach (var boundType in new[] { typeof(PhotonCadIndustrialBoundMutation), typeof(PhotonCadAssemblyBoundMutation) })
            foreach (var property in boundType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                True(forbidden.All(type => !type.IsAssignableFrom(property.PropertyType)), $"forbidden property type: {boundType.Name}.{property.Name}");
        var providerMethod = typeof(IPhotonCadSealedMutationProvider).GetMethod(nameof(IPhotonCadSealedMutationProvider.ApplyAsync))!;
        True(providerMethod.GetParameters().All(parameter => forbidden.All(type => !type.IsAssignableFrom(parameter.ParameterType))), "provider process/path parameter leak");
        return Task.CompletedTask;
    }

    private static async Task ImportedStepPartAsync()
    {
        await using var runner = new FakeRunner();
        var runtime = Runtime(runner);
        const string sessionId = "pcsid:step-import";
        const string projectId = "pcpid:step-import";
        var codec = new PhotonCadCanonicalProjectCodecV1(new FixedIdentityIssuer(sessionId, projectId));
        var initial = await codec.CreateAsync("Imported part", PhotonCadProjectUnit.Millimeter);
        var step = Step();
        var digest = ProtocolV1.Sha256(step);
        var importEvidence = new PhotonCadProviderEvidence(
            PhotonCadBackendV1.Geometry,
            "photon.cad.step.import.v1",
            ["iso-10303-21"],
            digest,
            "external.step.part21.v1",
            digest,
            digest,
            digest,
            digest,
            new PhotonCadSourceIdentityV1("user-supplied-step", "part21", digest, "user-supplied"));
        var bound = runtime.BindImportedStepPart(
            "request-step-import",
            sessionId,
            projectId,
            0,
            "imported-part",
            "IMPORTED-PART",
            "Imported STEP part",
            step,
            digest,
            importEvidence);
        var mapper = new PhotonCadRuntimeCanonicalMapperV1(codec, IndustrialPolicy());
        var binding = new PhotonCadCanonicalMutationBinding(
            new PhotonCadProjectHandle("cad-project:33333333333333333333333333333333"),
            initial);
        var mutation = await bound.Provider.ApplyAsync(mapper.PrepareProviderRequest(binding, bound.Request));
        Equal(2L, mutation.ResultingRevision, "import revision");
        Equal(2, mutation.Operations.Count, "import operation count");
        Equal(ImportedStepPartCommand.Capability, mutation.Operations[0].CapabilityId, "import capability");
        Equal("industrial.preview.glb.v1", mutation.Operations[1].CapabilityId, "import preview capability");
        Equal(2, mutation.Artifacts.Count, "import artifact count");
        var stepArtifact = mutation.Artifacts.Single(value => value.Kind == PhotonCadArtifactKindV1.Step);
        True(stepArtifact.Content.Span.SequenceEqual(step), "imported STEP bytes changed");
        True(stepArtifact.Bounds is null, "imported STEP bounds must remain null");
        Equal("user-supplied-step", stepArtifact.Evidence.Source.Package, "import source package");
        var saved = codec.MarkSaved(mapper.Apply(binding, bound.Request, mutation));
        var reopened = codec.Inspect(codec.Decode(saved.CanonicalBytes));
        Equal(2L, reopened.Revision, "reopened import revision");
        Equal("Imported STEP part", reopened.Entities.Single().Name, "reopened import name");
        Equal("IMPORTED-PART", reopened.Bom.Single().PartNumber, "reopened import BOM");
        True(reopened.Artifacts.Single(value => value.Kind == PhotonCadArtifactKindV1.Step).Content.Span.SequenceEqual(step),
            "reopened STEP bytes changed");
        True(ReadGlbEntityTags(reopened.Artifacts.Single(value => value.Kind == PhotonCadArtifactKindV1.Glb).Content.Span)
            .SetEquals(["imported-part.occ"]), "imported GLB entity tag");
        Equal(1, runner.Requests.Count, "import should invoke preview only");
        True(runner.Requests[0].Contains("\"operation\":\"createPreview\"", StringComparison.Ordinal),
            "import invoked non-preview container operation");
    }

    private static async Task<(PhotonCadIndustrialBoundMutation Bound, PhotonCadSealedMutationProviderRequest Request)> BoxAsync(
        FakeRunner runner,
        string suffix = "box")
    {
        var runtime = Runtime(runner);
        var bound = runtime.BindBox($"request-{suffix}", $"pcsid:{suffix}", $"pcpid:{suffix}", 0, $"{suffix}-root", 10, 20, 30);
        return (bound, await ProviderRequestAsync(bound.Request));
    }

    private static PhotonCadIndustrialProviderRuntime Runtime(FakeRunner runner)
    {
        var image = EvidenceVerifier.AcceptedDerivedImageId;
        var receipt = EvidenceVerifier.AcceptedReceiptSha256;
        var evidence = new PhotonCadProviderEvidence(
            PhotonCadBackendV1.Assembly,
            "photon.cad.industrial.docker.v1",
            ["photon.cad.industrial.protocol.v1"],
            runner.CatalogDigest,
            "photon.cad.industrial.container.v1",
            receipt,
            receipt,
            image,
            EvidenceVerifier.AcceptedBaseImageId,
            new PhotonCadSourceIdentityV1("photon-cad-industrial", "0.1.0", image, "redistribution-blocked"));
        return PhotonCadIndustrialProviderRuntime.CreateForSmoke(
            runner,
            new VerifiedIndustrialEvidence(receipt, image, EvidenceVerifier.AcceptedBaseImageId, runner.CatalogDigest, evidence));
    }

    private static async Task<PhotonCadSealedMutationProviderRequest> ProviderRequestAsync(PhotonCadRuntimeSyncRequest request)
    {
        var codec = new PhotonCadCanonicalProjectCodecV1(new FixedIdentityIssuer(request.SessionId, request.ProjectId));
        var project = await codec.CreateAsync("Industrial smoke", PhotonCadProjectUnit.Millimeter);
        var binding = new PhotonCadCanonicalMutationBinding(
            new PhotonCadProjectHandle("cad-project:00000000000000000000000000000000"),
            project);
        var mapper = new PhotonCadRuntimeCanonicalMapperV1(codec, IndustrialPolicy());
        return mapper.PrepareProviderRequest(binding, request);
    }

    private static PhotonCadRuntimeSyncPolicy IndustrialPolicy() => new(
            PhotonCadRuntimeSyncContract.MaximumSealedArtifactBytes,
            PhotonCadRuntimeSyncContract.MaximumSealedArtifactBytesPerMutation,
            PhotonCadRuntimeSyncContract.MaximumBaseArtifactBytes,
            PhotonCadRuntimeSyncContract.MaximumBaseArtifactBytesPerRequest);

    internal static IndustrialPrimitiveCommand BoxCommand() => new(
        IndustrialPrimitiveKind.Box, 10, 20, 30, 0, "root", "BOX", "Create industrial box");

    internal static byte[] Step() => Encoding.ASCII.GetBytes(
        "ISO-10303-21;\nHEADER;\nFILE_DESCRIPTION(('sealed'),'2;1');\nENDSEC;\nDATA;\nENDSEC;\nEND-ISO-10303-21;\n");

    internal static string PrimitiveResponse(IndustrialPrimitiveCommand command, byte[] step, bool addUnknown = false)
    {
        var digest = ProtocolV1.Sha256(step);
        object provenance = command.Kind == IndustrialPrimitiveKind.Box
            ? new { generator = "primitive", kind = "box", parameters = new { heightMm = command.HeightMm, lengthMm = command.LengthMm, widthMm = command.WidthMm } }
            : new { generator = "primitive", kind = "cylinder", parameters = new { heightMm = command.HeightMm, radiusMm = command.RadiusMm } };
        var payload = new Dictionary<string, object?>
        {
            ["schema"] = ProtocolV1.ResponseSchema,
            ["ok"] = true,
            ["operation"] = "createPrimitive",
            ["artifact"] = new { format = "step", contentDigest = digest, byteLength = step.Length },
            ["measurement"] = new
            {
                units = "millimeter",
                volumeMm3 = command.Kind == IndustrialPrimitiveKind.Box
                    ? command.LengthMm * command.WidthMm * command.HeightMm
                    : Math.PI * command.RadiusMm * command.RadiusMm * command.HeightMm,
                solidCount = 1,
                bounds = new { minimum = new[] { 0d, 0d, 0d }, maximum = new[] { 10d, 20d, 30d } },
            },
            ["provenance"] = provenance,
        };
        if (addUnknown) payload["outputPath"] = "C:\\forbidden.step";
        return JsonSerializer.Serialize(payload);
    }

    internal static byte[] Glb(IndustrialPreviewCommand command, bool externalUri = false)
    {
        object buffer = externalUri ? new { byteLength = 4, uri = "file:///forbidden.bin" } : new { byteLength = 4 };
        var sourceIndexes = command.Sources
            .Select((source, index) => (source.SourcePartId, index))
            .ToDictionary(value => value.SourcePartId, value => value.index, StringComparer.Ordinal);
        var occurrenceIndexes = command.Occurrences
            .Select((occurrence, index) => (occurrence.EntityId, index))
            .ToDictionary(value => value.EntityId, value => value.index, StringComparer.Ordinal);
        var nodes = command.Occurrences.Select(occurrence =>
        {
            var node = new Dictionary<string, object?>
            {
                ["mesh"] = sourceIndexes[occurrence.SourcePartId],
                ["matrix"] = ColumnMajor(occurrence.Transform),
                ["extras"] = new { photonEntityId = occurrence.EntityId },
            };
            var children = command.Occurrences
                .Where(candidate => StringComparer.Ordinal.Equals(candidate.ParentEntityId, occurrence.EntityId))
                .Select(candidate => occurrenceIndexes[candidate.EntityId])
                .ToArray();
            if (children.Length > 0) node["children"] = children;
            return node;
        }).ToArray();
        var roots = command.Occurrences
            .Where(occurrence => occurrence.ParentEntityId is null)
            .Select(occurrence => occurrenceIndexes[occurrence.EntityId])
            .ToArray();
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            asset = new { version = "2.0", generator = "Photon CAD industrial container" },
            scene = 0,
            scenes = new[] { new { nodes = roots } },
            nodes,
            meshes = command.Sources.Select(_ => new { primitives = Array.Empty<object>() }).ToArray(),
            buffers = new[] { buffer },
        }).ToList();
        while (json.Count % 4 != 0) json.Add((byte)' ');
        var total = 12 + 8 + json.Count + 8 + 4;
        var result = new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(result, 0x46546C67);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), (uint)total);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12), (uint)json.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16), 0x4E4F534A);
        json.ToArray().CopyTo(result, 20);
        var binaryHeader = 20 + json.Count;
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(binaryHeader), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(binaryHeader + 4), 0x004E4942);
        return result;
    }

    private static double[] ColumnMajor(IReadOnlyList<double> rowMajor) =>
        [.. Enumerable.Range(0, 4).SelectMany(column => Enumerable.Range(0, 4).Select(row => rowMajor[row * 4 + column]))];

    private static HashSet<string> ReadGlbEntityTags(ReadOnlySpan<byte> bytes)
    {
        var jsonLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]));
        using var document = JsonDocument.Parse(bytes.Slice(20, jsonLength).ToArray());
        return document.RootElement.GetProperty("nodes").EnumerateArray()
            .Select(node => node.GetProperty("extras").GetProperty("photonEntityId").GetString()
                ?? throw new InvalidDataException("missing GLB entity tag"))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool ContainsForbiddenTransportTruth(string json) =>
        json.Contains("path", StringComparison.OrdinalIgnoreCase)
        || json.Contains("python", StringComparison.OrdinalIgnoreCase)
        || json.Contains("docker", StringComparison.OrdinalIgnoreCase)
        || json.Contains("command", StringComparison.OrdinalIgnoreCase)
        || json.Contains("executable", StringComparison.OrdinalIgnoreCase);

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Throws<T>(Action action, string contains) where T : Exception
    {
        try { action(); }
        catch (T exception) when (exception.Message.Contains(contains, StringComparison.OrdinalIgnoreCase)) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name} containing {contains}");
    }

    private static async Task ThrowsAsync<T>(Func<Task> action, string contains) where T : Exception
    {
        try { await action(); }
        catch (T exception) when (exception.Message.Contains(contains, StringComparison.OrdinalIgnoreCase)) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name} containing {contains}");
    }
}

internal sealed class FixedIdentityIssuer(string sessionId, string projectId) : IPhotonCadProjectIdentityIssuerV1
{
    public (string SessionId, string ProjectId) NewIdentity() => (sessionId, projectId);
}

internal sealed class FakeRunner : IIndustrialContainerRunner, IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotonCadIndustrialProviderSmoke", Guid.NewGuid().ToString("N"));
    private string? _lastOutput;
    internal List<string> Requests { get; } = [];
    internal bool CorruptPrimitiveDigest { get; set; }
    internal bool ForeignNextPreview { get; set; }
    internal string CatalogDigest => ProtocolV1.Sha256(CatalogBytes);

    private static byte[] CatalogBytes => JsonSerializer.SerializeToUtf8Bytes(new
    {
        schema = "photon.cad.industrial.catalog/v1",
        runtime = new { identity = "fake" },
        categories = Array.Empty<object>(),
        items = new object[]
        {
            new
            {
                availability = "supported",
                category = "bearings",
                id = "bdw_111111111111111111111111111111111111111111111111",
                parameters = new object[]
                {
                    new { choices = new[] { "M10-30-9" }, id = "size", kind = "choice", label = "Size", nullable = false, required = true },
                },
                title = "Smoke Bearing",
            },
            new
            {
                availability = "supported",
                category = "gears",
                id = "bdw_222222222222222222222222222222222222222222222222",
                parameters = new object[]
                {
                    new { id = "module", kind = "number", label = "Module", maximum = 1000000d, minimum = 0.000001d, nullable = false, required = true },
                    new { id = "pressure_angle", kind = "number", label = "Pressure angle", maximum = 89d, minimum = 0.1d, nullable = false, required = true },
                    new { id = "thickness", kind = "number", label = "Thickness", maximum = 1000000d, minimum = 0.000001d, nullable = false, required = true },
                    new { id = "tooth_count", kind = "integer", label = "Tooth count", maximum = 1000, minimum = 3, nullable = false, required = true },
                },
                title = "Spur Gear",
            },
            new
            {
                availability = "supported",
                category = "fasteners",
                id = "bdw_333333333333333333333333333333333333333333333333",
                parameters = new object[]
                {
                    new { choices = new[] { "M6-1x20" }, id = "size", kind = "choice", label = "Size", nullable = false, required = true },
                },
                title = "Socket Head Cap Screw",
            },
        },
    });

    public ValueTask<IndustrialContainerInvocation> ExecuteAsync(
        ReadOnlyMemory<byte> request,
        IReadOnlyList<IndustrialInputArtifact> inputs,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var json = Encoding.UTF8.GetString(request.Span);
        Requests.Add(json);
        using var document = JsonDocument.Parse(request);
        var operation = document.RootElement.GetProperty("operation").GetString();
        var job = Path.Combine(_root, $"job-{Requests.Count}");
        var output = Path.Combine(job, "output");
        Directory.CreateDirectory(output);
        _lastOutput = output;
        if (operation == "catalog")
        {
            using var catalog = JsonDocument.Parse(CatalogBytes);
            var response = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = ProtocolV1.ResponseSchema,
                ok = true,
                operation = "catalog",
                catalogDigest = CatalogDigest,
                catalog = catalog.RootElement,
            });
            return ValueTask.FromResult(new IndustrialContainerInvocation(
                response, output, () => ValueTask.CompletedTask));
        }
        if (operation == "createPrimitive")
        {
            var primitive = document.RootElement.GetProperty("primitive");
            var kind = primitive.GetProperty("kind").GetString();
            var dimensions = primitive.GetProperty("dimensions");
            var command = kind == "box"
                ? new IndustrialPrimitiveCommand(
                    IndustrialPrimitiveKind.Box,
                    dimensions.GetProperty("lengthMm").GetDouble(),
                    dimensions.GetProperty("widthMm").GetDouble(),
                    dimensions.GetProperty("heightMm").GetDouble(),
                    0, "unused", "BOX", "Create industrial box")
                : new IndustrialPrimitiveCommand(
                    IndustrialPrimitiveKind.Cylinder,
                    0, 0,
                    dimensions.GetProperty("heightMm").GetDouble(),
                    dimensions.GetProperty("radiusMm").GetDouble(),
                    "unused", "CYLINDER", "Create industrial cylinder");
            var step = Smoke.Step();
            File.WriteAllBytes(Path.Combine(output, "model.step"), step);
            var response = Smoke.PrimitiveResponse(command, step);
            if (CorruptPrimitiveDigest)
                response = response.Replace(ProtocolV1.Sha256(step), "sha256:" + new string('0', 64), StringComparison.Ordinal);
            return ValueTask.FromResult(new IndustrialContainerInvocation(
                Encoding.UTF8.GetBytes(response), output, () => ValueTask.CompletedTask));
        }
        if (operation == "createCatalogItem")
        {
            var root = document.RootElement;
            var step = Smoke.Step();
            File.WriteAllBytes(Path.Combine(output, "model.step"), step);
            var parameters = root.GetProperty("parameters").Clone();
            var itemId = root.GetProperty("itemId").GetString()!;
            var digest = root.GetProperty("catalogDigest").GetString()!;
            var response = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = ProtocolV1.ResponseSchema,
                ok = true,
                operation = "createCatalogItem",
                catalogDigest = digest,
                itemId,
                artifact = new { format = "step", contentDigest = ProtocolV1.Sha256(step), byteLength = step.Length },
                measurement = new
                {
                    units = "millimeter",
                    volumeMm3 = 100d,
                    solidCount = 4,
                    bounds = new { minimum = new[] { 0d, 0d, 0d }, maximum = new[] { 10d, 20d, 30d } },
                },
                provenance = new { generator = "catalog", catalogDigest = digest, itemId, parameters },
            });
            return ValueTask.FromResult(new IndustrialContainerInvocation(
                response, output, () => ValueTask.CompletedTask));
        }
        if (operation == "createPreview")
        {
            var sources = document.RootElement.GetProperty("sources").EnumerateArray()
                .Select(source =>
                {
                    var slot = source.GetProperty("inputSlot").GetString()!;
                    var input = inputs.Single(candidate => StringComparer.Ordinal.Equals(candidate.Slot, slot));
                    return new IndustrialPreviewSource(
                        source.GetProperty("sourcePartId").GetString()!,
                        slot,
                        source.GetProperty("expectedDigest").GetString()!,
                        input.Content.Length);
                })
                .ToArray();
            var occurrences = document.RootElement.GetProperty("occurrences").EnumerateArray()
                .Select(occurrence => new IndustrialPreviewOccurrence(
                    occurrence.GetProperty("entityId").GetString()!,
                    occurrence.GetProperty("sourcePartId").GetString()!,
                    occurrence.GetProperty("parentEntityId").ValueKind == JsonValueKind.Null
                        ? null
                        : occurrence.GetProperty("parentEntityId").GetString(),
                    occurrence.GetProperty("transform").EnumerateArray().Select(value => value.GetDouble()).ToArray()))
                .ToArray();
            var command = new IndustrialPreviewCommand(sources, occurrences);
            EqualInputs(inputs, command);
            var glb = Smoke.Glb(command);
            if (ForeignNextPreview)
            {
                ForeignNextPreview = false;
                glb = Smoke.Glb(new IndustrialPreviewCommand(command.Sources, command.Occurrences.Reverse().ToArray()));
            }
            File.WriteAllBytes(Path.Combine(output, "preview.glb"), glb);
            var response = JsonSerializer.Serialize(new
            {
                schema = ProtocolV1.ResponseSchema,
                ok = true,
                operation = "createPreview",
                artifact = new { format = "glb", contentDigest = ProtocolV1.Sha256(glb), byteLength = glb.Length },
                entityCount = command.Occurrences.Count,
                sourceCount = command.Sources.Count,
                bounds = new { minimum = new[] { 0d, 0d, 0d }, maximum = new[] { 10d, 20d, 30d } },
                units = "millimeter",
                provenance = new
                {
                    sources = command.Sources.Select(source => new
                    {
                        sourcePartId = source.SourcePartId,
                        contentDigest = source.ExpectedDigest,
                        byteLength = source.ExpectedByteLength,
                    }).ToArray(),
                    occurrences = command.Occurrences.Select(occurrence => new
                    {
                        entityId = occurrence.EntityId,
                        sourcePartId = occurrence.SourcePartId,
                        parentEntityId = occurrence.ParentEntityId,
                        transform = occurrence.Transform,
                    }).ToArray(),
                    tessellation = new { linearToleranceMm = 0.1, angularToleranceRad = 0.1 },
                },
            });
            return ValueTask.FromResult(new IndustrialContainerInvocation(
                Encoding.UTF8.GetBytes(response), output, () => ValueTask.CompletedTask));
        }
        throw new InvalidOperationException("unexpected fake operation");
    }

    internal void OverwriteLastOutput()
    {
        if (_lastOutput is not null) File.WriteAllText(Path.Combine(_lastOutput, "model.step"), "overwritten");
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    private static void EqualInputs(IReadOnlyList<IndustrialInputArtifact> inputs, IndustrialPreviewCommand command)
    {
        if (inputs.Count != command.Sources.Count
            || inputs.Any(input => !ProtocolV1.FixedDigestEquals(ProtocolV1.Sha256(input.Content.Span), input.Digest))
            || command.Sources.Any(source => inputs.All(input => !StringComparer.Ordinal.Equals(input.Slot, source.InputSlot)
                || !ProtocolV1.FixedDigestEquals(input.Digest, source.ExpectedDigest)
                || input.Content.Length != source.ExpectedByteLength)))
            throw new InvalidOperationException("fake input mismatch");
    }
}
