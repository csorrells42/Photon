using System.Diagnostics;
using System.Security.Cryptography;
using PhotonCadProjects;
using PhotonCadProjects.Codec;
using PhotonCadProjects.RuntimeSync;

var tests = new (string Name, Func<Task> Run)[]
{
    ("primitive provider advances 0 to 2 with sealed STEP", PrimitiveAdvancesByTwoAsync),
    ("second primitive mutation advances 2 to 4 and preserves prior state", SecondMutationPreservesStateAsync),
    ("industrial single operation advances 0 to 1 truthfully", IndustrialAdvancesByOneAsync),
    ("provider base view owns exact pathless canonical state", ProviderBaseViewIsImmutableAsync),
    ("entity-valued inputs remain bound to canonical base", EntityInputMustExistInBaseAsync),
    ("request and mutation mismatch compensates without commit", MutationMismatchCompensatesAsync),
    ("unadvertised side-effect entity is rejected", HiddenEntityFailsAsync),
    ("missing authoritative STEP fails closed", MissingStepFailsAsync),
    ("digest and path-like evidence are rejected", HostileValuesFailAsync),
    ("injectable artifact policy rejects oversize mutation", ArtifactPolicyFailsAsync),
    ("artifact replacement requires exact prior digest", ArtifactReplacementIsCasAsync),
    ("cancellation after provider return compensates", CancellationCompensatesAsync),
    ("commit failure compensates and preserves base", CommitFailureCompensatesAsync),
    ("compensation failure is surfaced without false success", CompensationFailureSurfacesAsync),
    ("provider failure does not invent compensation", ProviderFailureDoesNotCompensateAsync),
    ("malformed commit result fails closed", MalformedCommitResultFailsAsync),
    ("same project serializes and stale queued request never executes", SameProjectSerializesAsync),
    ("per-project pending queue is bounded canceled and pruned", PendingProjectQueueIsBoundedAsync),
    ("different projects execute concurrently", DifferentProjectsRunConcurrentlyAsync),
    ("global project capacity is fixed and isolated", ActiveProjectCapAsync),
};

var suite = Stopwatch.StartNew();
var passed = 0;
foreach (var test in tests)
{
    var clock = Stopwatch.StartNew();
    await test.Run();
    passed++;
    Console.WriteLine($"PASS {test.Name} ({clock.Elapsed.TotalMilliseconds:F1} ms)");
}
Console.WriteLine($"PhotonCadProjects.RuntimeSync smoke: {passed}/{tests.Length} passed in {suite.Elapsed.TotalMilliseconds:F1} ms.");

static async Task PrimitiveAdvancesByTwoAsync()
{
    var (codec, project) = await SmokeFactory.ProjectAsync("primitive");
    var authority = new InMemoryCanonicalAuthority(codec, [project]);
    var compensator = new RecordingCompensator();
    var provider = new DelegateMutationProvider((request, _) => ValueTask.FromResult(
        SmokeFactory.CreateBodyMutation(request, "body-a", includeExportOperation: true)));
    var synchronizer = SmokeFactory.Synchronizer(codec, authority, provider, compensator);
    var request = SmokeFactory.Request(project, "request-primitive", targets: ["body-a"]);

    var result = await synchronizer.SynchronizeAsync(request);
    var state = codec.Inspect(result.SavedProject);
    Check(!result.SavedProject.Dirty, "committed project must be clean");
    Check(state.Revision == 2 && state.Operations.Count == 2, "primitive suffix must truthfully advance by two");
    Check(state.Entities.Count == 1 && state.Artifacts.Count == 1, "body and sealed STEP must persist");
    Check(state.Artifacts[0].Role == PhotonCadArtifactRoleV1.AuthoritativeGeometry
        && state.Artifacts[0].Kind == PhotonCadArtifactKindV1.Step, "STEP must be authoritative geometry");
    Check(state.Artifacts[0].Provenance.BundleManifestSha256 == SmokeFactory.Digest('e'), "receipt commitment must persist");
    Check(state.Artifacts[0].Provenance.Source.Digest == SmokeFactory.Digest('d'), "derived image commitment must persist");
    Check(compensator.Count == 0 && synchronizer.ActiveProjectCount == 0, "success must not compensate or leak a gate");
}

static async Task SecondMutationPreservesStateAsync()
{
    var (codec, project) = await SmokeFactory.ProjectAsync("twice");
    var authority = new InMemoryCanonicalAuthority(codec, [project]);
    var compensator = new RecordingCompensator();
    var provider = new DelegateMutationProvider((request, _) => ValueTask.FromResult(
        SmokeFactory.CreateBodyMutation(
            request,
            request.Request.RequestId.EndsWith("one", StringComparison.Ordinal) ? "body-one" : "body-two",
            includeExportOperation: true,
            bom: [new PhotonCadBomRow(
                request.Request.RequestId.EndsWith("one", StringComparison.Ordinal) ? "P-001" : "P-002",
                "part",
                1,
                PhotonCadBomUnit.Each,
                request.Request.RequestId.EndsWith("one", StringComparison.Ordinal) ? "body-one" : "body-two")])));
    var synchronizer = SmokeFactory.Synchronizer(codec, authority, provider, compensator);

    var first = await synchronizer.SynchronizeAsync(SmokeFactory.Request(project, "request-one", targets: ["body-one"]));
    var prior = codec.Inspect(first.SavedProject);
    var priorBytes = prior.Artifacts.Single().Content.ToArray();
    var secondRequest = SmokeFactory.Request(first.SavedProject, "request-two", targets: ["body-two"]);
    var second = await synchronizer.SynchronizeAsync(secondRequest);
    var state = codec.Inspect(second.SavedProject);

    Check(state.Revision == 4 && state.Operations.Count == 4, "two primitive suffixes must advance 0->2->4");
    Check(state.Entities.Select(value => value.Id).Order().SequenceEqual(["body-one", "body-two"]), "both bodies must remain");
    Check(state.Artifacts.Count == 2 && state.Bom.Count == 2, "prior STEP and BOM must remain");
    Check(state.Artifacts.Any(value => value.Content.Span.SequenceEqual(priorBytes)), "prior exact STEP bytes must survive");
}

static async Task IndustrialAdvancesByOneAsync()
{
    var (codec, project) = await SmokeFactory.ProjectAsync("industrial");
    var authority = new InMemoryCanonicalAuthority(codec, [project]);
    var provider = new DelegateMutationProvider((request, _) => ValueTask.FromResult(
        SmokeFactory.CreateBodyMutation(request, "part-a", includeExportOperation: false)));
    var synchronizer = SmokeFactory.Synchronizer(codec, authority, provider, new RecordingCompensator());

    var result = await synchronizer.SynchronizeAsync(SmokeFactory.Request(
        project,
        "request-industrial",
        "industrial.create.catalog-item",
        targets: ["part-a"]));
    var state = codec.Inspect(result.SavedProject);
    Check(state.Revision == 1 && state.Operations.Count == 1, "industrial response must not invent an export operation");
    Check(state.Artifacts.Single().Provenance.OperationId == state.Operations.Single().Id, "one operation must own sealed STEP");
}

static async Task ProviderBaseViewIsImmutableAsync()
{
    var (codec, project) = await SmokeFactory.ProjectAsync("base-view");
    var authority = new InMemoryCanonicalAuthority(codec, [project]);
    var calls = 0;
    var provider = new DelegateMutationProvider((request, _) =>
    {
        calls++;
        if (calls == 2)
        {
            Check(request.BaseEntities.Count == 1 && request.ExistingEntityIds.SequenceEqual(["body-first"]), "base entities must be exact");
            Check(request.BaseArtifacts.Count == 1 && request.BaseBom.Count == 1, "base STEP and BOM must be supplied");
            var copy = request.BaseArtifacts[0].Content.ToArray();
            copy[0] ^= 0xff;
            Check(request.BaseArtifacts[0].Content.Span[0] == (byte)'I', "provider content access must return isolated bytes");
            Check(request.BaseArtifacts.All(value => !value.MediaType.Contains('\\')), "base view must remain pathless");
        }
        var entity = calls == 1 ? "body-first" : "body-second";
        return ValueTask.FromResult(SmokeFactory.CreateBodyMutation(
            request,
            entity,
            includeExportOperation: true,
            bom: [new PhotonCadBomRow($"P-{calls}", "base row", 1, PhotonCadBomUnit.Each, entity)]));
    });
    var synchronizer = SmokeFactory.Synchronizer(codec, authority, provider, new RecordingCompensator());
    var first = await synchronizer.SynchronizeAsync(SmokeFactory.Request(project, "request-base-one", targets: ["body-first"]));
    await synchronizer.SynchronizeAsync(SmokeFactory.Request(first.SavedProject, "request-base-two", targets: ["body-second"]));
    Check(calls == 2, "both provider calls expected");
}

static async Task EntityInputMustExistInBaseAsync()
{
    var (codec, project) = await SmokeFactory.ProjectAsync("entity-input");
    var authority = new InMemoryCanonicalAuthority(codec, [project]);
    var provider = new DelegateMutationProvider((request, _) => ValueTask.FromResult(
        SmokeFactory.CreateBodyMutation(request, "body-new", false)));
    var compensator = new RecordingCompensator();
    var synchronizer = SmokeFactory.Synchronizer(codec, authority, provider, compensator);
    var request = SmokeFactory.Request(
        project,
        "request-entity-input",
        inputs: [new PhotonCadSyncOperationInput("source", PhotonCadSyncInputValue.Entity("body-new"))],
        targets: ["body-new"]);
    await ExpectCodeAsync("request_entity_not_in_canonical_base", () => synchronizer.SynchronizeAsync(request).AsTask());
    Check(provider.Calls == 0 && compensator.Count == 0, "unbound entity input must fail before provider execution");
}

static async Task MutationMismatchCompensatesAsync()
{
    var (codec, project) = await SmokeFactory.ProjectAsync("mismatch");
    var authority = new InMemoryCanonicalAuthority(codec, [project]);
    var compensator = new RecordingCompensator();
    var provider = new DelegateMutationProvider((request, _) => ValueTask.FromResult(
        SmokeFactory.CreateBodyMutation(request, "body-a", true, requestIdOverride: "foreign-request")));
    var synchronizer = SmokeFactory.Synchronizer(codec, authority, provider, compensator);

    await ExpectCodeAsync("mutation_request_binding_mismatch", () => synchronizer.SynchronizeAsync(
        SmokeFactory.Request(project, "request-bound", targets: ["body-a"])).AsTask());
    Check(authority.CommitCount == 0 && compensator.Count == 1, "mismatch must compensate before commit");
}

static async Task HiddenEntityFailsAsync()
{
    var (codec, project) = await SmokeFactory.ProjectAsync("hidden-entity");
    var authority = new InMemoryCanonicalAuthority(codec, [project]);
    var compensator = new RecordingCompensator();
    var provider = new DelegateMutationProvider((request, _) =>
    {
        var visible = SmokeFactory.CreateBodyMutation(request, "body-visible", false);
        return ValueTask.FromResult(new PhotonCadSealedMutationDelta(
            visible.MutationId,
            visible.RequestId,
            visible.SessionId,
            visible.ProjectId,
            visible.BaseRevision,
            visible.ResultingRevision,
            visible.Operations,
            visible.Entities.Concat([
                new PhotonCadEntityV1(
                    "body-hidden",
                    null,
                    PhotonCadEntityKindV1.Body,
                    "hidden",
                    true,
                    false,
                    request.Request.CapabilityId),
            ]),
            artifacts: visible.Artifacts));
    });
    var synchronizer = SmokeFactory.Synchronizer(codec, authority, provider, compensator);
    await ExpectCodeAsync("entity_not_targeted_by_source_operation", () => synchronizer.SynchronizeAsync(
        SmokeFactory.Request(project, "request-hidden", targets: ["body-visible"])).AsTask());
    Check(compensator.Count == 1 && authority.CommitCount == 0, "hidden side effect must not commit");
}

static async Task MissingStepFailsAsync()
{
    var (codec, project) = await SmokeFactory.ProjectAsync("missing-step");
    var authority = new InMemoryCanonicalAuthority(codec, [project]);
    var compensator = new RecordingCompensator();
    var provider = new DelegateMutationProvider((providerRequest, _) =>
    {
        var request = providerRequest.Request;
        var operation = new PhotonCadAppliedOperationDelta(
            1,
            "op-missing-step",
            request.CapabilityId,
            "Create without STEP",
            DateTimeOffset.Parse("2026-08-10T12:00:00Z"),
            request.Mode,
            request.Inputs,
            request.TargetEntityIds,
            SmokeFactory.Evidence());
        return ValueTask.FromResult(new PhotonCadSealedMutationDelta(
            "mutation-missing-step",
            request.RequestId,
            request.SessionId,
            request.ProjectId,
            0,
            1,
            [operation],
            [new PhotonCadEntityV1("body-a", null, PhotonCadEntityKindV1.Body, "body-a", true, false, request.CapabilityId)]));
    });
    var synchronizer = SmokeFactory.Synchronizer(codec, authority, provider, compensator);
    await ExpectCodeAsync("geometry_artifact_missing", () => synchronizer.SynchronizeAsync(
        SmokeFactory.Request(project, "request-missing-step", targets: ["body-a"])).AsTask());
    Check(compensator.Count == 1 && authority.CommitCount == 0, "unsealed geometry must not commit");
}

static Task HostileValuesFailAsync()
{
    var content = SmokeFactory.Step();
    ExpectCode("sealed_artifact_digest_mismatch", () => new PhotonCadSealedArtifactDelta(
        PhotonCadArtifactRoleV1.AuthoritativeGeometry,
        PhotonCadArtifactKindV1.Step,
        "body-a",
        1,
        content,
        content.Length,
        SmokeFactory.Digest('0'),
        "model/step",
        null,
        "op-a",
        SmokeFactory.Evidence()));
    ExpectCode("path_like_value_rejected", () => new PhotonCadProviderEvidence(
        PhotonCadBackendV1.Geometry,
        "C:/evil",
        ["mcp.initialize"],
        "catalog.1",
        "bundle-a",
        SmokeFactory.Digest('e'),
        SmokeFactory.Digest('e'),
        SmokeFactory.Digest('d'),
        SmokeFactory.Digest('b'),
        new PhotonCadSourceIdentityV1("build123d-mcp", "0.3.80", SmokeFactory.Digest('d'), "Apache-2.0")));
    ExpectCode("receipt_commitment_not_persistable", () => new PhotonCadProviderEvidence(
        PhotonCadBackendV1.Geometry,
        "provider",
        ["mcp.initialize"],
        "catalog.1",
        "bundle-a",
        SmokeFactory.Digest('a'),
        SmokeFactory.Digest('e'),
        SmokeFactory.Digest('d'),
        SmokeFactory.Digest('b'),
        new PhotonCadSourceIdentityV1("build123d-mcp", "0.3.80", SmokeFactory.Digest('d'), "Apache-2.0")));
    ExpectCode("runtime_handle_rejected", () => new PhotonCadRuntimeSyncRequest(
        $"cad-project:{new string('a', 32)}",
        "pcsid:safe",
        "pcpid:safe",
        0,
        "geometry.create.box",
        PhotonCadOperationModeV1.Suggest));
    return Task.CompletedTask;
}

static async Task ArtifactPolicyFailsAsync()
{
    var (codec, project) = await SmokeFactory.ProjectAsync("policy");
    var authority = new InMemoryCanonicalAuthority(codec, [project]);
    var compensator = new RecordingCompensator();
    var step = SmokeFactory.Step().Concat(Enumerable.Repeat((byte)' ', 256)).ToArray();
    var provider = new DelegateMutationProvider((request, _) => ValueTask.FromResult(
        SmokeFactory.CreateBodyMutation(request, "body-a", false, step: step)));
    var policy = new PhotonCadRuntimeSyncPolicy(128, 128, 128, 128);
    var synchronizer = SmokeFactory.Synchronizer(codec, authority, provider, compensator, policy);
    await ExpectCodeAsync("sealed_artifact_policy_rejected", () => synchronizer.SynchronizeAsync(
        SmokeFactory.Request(project, "request-policy", targets: ["body-a"])).AsTask());
    Check(compensator.Count == 1 && authority.CommitCount == 0, "policy rejection must compensate");
}

static async Task ArtifactReplacementIsCasAsync()
{
    var (codec, project) = await SmokeFactory.ProjectAsync("replace");
    var authority = new InMemoryCanonicalAuthority(codec, [project]);
    var firstProvider = new DelegateMutationProvider((request, _) => ValueTask.FromResult(
        SmokeFactory.CreateBodyMutation(
            request,
            "body-a",
            false,
            bom: [new PhotonCadBomRow("P-1", "old", 1, PhotonCadBomUnit.Each, "body-a")])));
    var firstSync = SmokeFactory.Synchronizer(codec, authority, firstProvider, new RecordingCompensator());
    var first = await firstSync.SynchronizeAsync(SmokeFactory.Request(project, "request-replace-create", targets: ["body-a"]));
    var prior = codec.Inspect(first.SavedProject).Artifacts.Single();

    var badCompensator = new RecordingCompensator();
    var badProvider = new DelegateMutationProvider((request, _) => ValueTask.FromResult(
        SmokeFactory.CreateBodyMutation(request, "body-a", false, step: SmokeFactory.Step("new"), replacesDigest: SmokeFactory.Digest('f'), includeEntity: false)));
    var badSync = SmokeFactory.Synchronizer(codec, authority, badProvider, badCompensator);
    var rebuild = SmokeFactory.Request(first.SavedProject, "request-replace-bad", "geometry.rebuild", targets: ["body-a"]);
    await ExpectCodeAsync("artifact_replacement_binding_mismatch", () => badSync.SynchronizeAsync(rebuild).AsTask());
    Check(badCompensator.Count == 1 && authority.Get(project.SessionId, project.ProjectId).Revision == 1, "bad CAS must preserve base");

    var goodProvider = new DelegateMutationProvider((request, _) => ValueTask.FromResult(
        SmokeFactory.CreateBodyMutation(
            request,
            "body-a",
            false,
            step: SmokeFactory.Step("new"),
            replacesDigest: prior.Digest,
            includeEntity: false,
            bom: [new PhotonCadBomRow("P-1", "new", 2, PhotonCadBomUnit.Each, "body-a")],
            bomMode: PhotonCadCollectionMergeMode.ReplaceAll)));
    var goodSync = SmokeFactory.Synchronizer(codec, authority, goodProvider, new RecordingCompensator());
    var saved = await goodSync.SynchronizeAsync(SmokeFactory.Request(
        first.SavedProject,
        "request-replace-good",
        "geometry.rebuild",
        targets: ["body-a"]));
    var state = codec.Inspect(saved.SavedProject);
    Check(state.Revision == 2 && state.Artifacts.Count == 1, "replacement must advance once without duplicate geometry");
    Check(state.Bom.Count == 1 && state.Bom[0].Description == "new", "explicit BOM ReplaceAll must be honored");
}

static async Task CancellationCompensatesAsync()
{
    var (codec, project) = await SmokeFactory.ProjectAsync("cancel");
    var authority = new InMemoryCanonicalAuthority(codec, [project]);
    var compensator = new RecordingCompensator();
    using var cancellation = new CancellationTokenSource();
    var provider = new DelegateMutationProvider((request, _) =>
    {
        cancellation.Cancel();
        return ValueTask.FromResult(SmokeFactory.CreateBodyMutation(request, "body-a", false));
    });
    var synchronizer = SmokeFactory.Synchronizer(codec, authority, provider, compensator);
    await ExpectCanceledAsync(() => synchronizer.SynchronizeAsync(
        SmokeFactory.Request(project, "request-cancel", targets: ["body-a"]), cancellation.Token).AsTask());
    Check(compensator.Count == 1 && compensator.Calls[0].Reason == "canonical_commit_canceled", "canceled returned mutation must compensate");
    Check(authority.CommitCount == 0, "canceled mutation must not commit");
}

static async Task CommitFailureCompensatesAsync()
{
    var (codec, project) = await SmokeFactory.ProjectAsync("commit-fail");
    var authority = new InMemoryCanonicalAuthority(codec, [project]) { FailNextCommit = true };
    var compensator = new RecordingCompensator();
    var provider = new DelegateMutationProvider((request, _) => ValueTask.FromResult(
        SmokeFactory.CreateBodyMutation(request, "body-a", false)));
    var synchronizer = SmokeFactory.Synchronizer(codec, authority, provider, compensator);
    await ExpectCodeAsync("injected_commit_failure", () => synchronizer.SynchronizeAsync(
        SmokeFactory.Request(project, "request-commit-fail", targets: ["body-a"])).AsTask());
    Check(compensator.Count == 1 && authority.Get(project.SessionId, project.ProjectId).Revision == 0, "commit failure must retain base");
}

static async Task CompensationFailureSurfacesAsync()
{
    var (codec, project) = await SmokeFactory.ProjectAsync("compensation-fail");
    var authority = new InMemoryCanonicalAuthority(codec, [project]) { FailNextCommit = true };
    var compensator = new RecordingCompensator { Fail = true };
    var provider = new DelegateMutationProvider((request, _) => ValueTask.FromResult(
        SmokeFactory.CreateBodyMutation(request, "body-a", false)));
    var synchronizer = SmokeFactory.Synchronizer(codec, authority, provider, compensator);
    await ExpectCodeAsync("compensation_failed", () => synchronizer.SynchronizeAsync(
        SmokeFactory.Request(project, "request-compensation-fail", targets: ["body-a"])).AsTask());
    Check(compensator.Count == 1 && authority.CommitCount == 0, "compensation failure must never report canonical success");
}

static async Task ProviderFailureDoesNotCompensateAsync()
{
    var (codec, project) = await SmokeFactory.ProjectAsync("provider-fail");
    var authority = new InMemoryCanonicalAuthority(codec, [project]);
    var compensator = new RecordingCompensator();
    var provider = new DelegateMutationProvider((_, _) => ValueTask.FromException<PhotonCadSealedMutationDelta>(
        new PhotonCadRuntimeSyncException("injected_provider_failure", "provider")));
    var synchronizer = SmokeFactory.Synchronizer(codec, authority, provider, compensator);
    await ExpectCodeAsync("injected_provider_failure", () => synchronizer.SynchronizeAsync(
        SmokeFactory.Request(project, "request-provider-fail")).AsTask());
    Check(compensator.Count == 0 && authority.CommitCount == 0, "provider owns cleanup until it returns a sealed mutation");
}

static async Task MalformedCommitResultFailsAsync()
{
    var (codec, project) = await SmokeFactory.ProjectAsync("dirty-return");
    var authority = new InMemoryCanonicalAuthority(codec, [project]) { ReturnDirtyAfterCommit = true };
    var compensator = new RecordingCompensator();
    var provider = new DelegateMutationProvider((request, _) => ValueTask.FromResult(
        SmokeFactory.CreateBodyMutation(request, "body-a", false)));
    var synchronizer = SmokeFactory.Synchronizer(codec, authority, provider, compensator);
    await ExpectCodeAsync("saved_project_binding_mismatch", () => synchronizer.SynchronizeAsync(
        SmokeFactory.Request(project, "request-dirty-return", targets: ["body-a"])).AsTask());
    Check(compensator.Count == 1, "malformed authority result must fail closed and compensate provider state");
}

static async Task SameProjectSerializesAsync()
{
    var (codec, project) = await SmokeFactory.ProjectAsync("serialized");
    var authority = new InMemoryCanonicalAuthority(codec, [project]);
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var provider = new DelegateMutationProvider(async (request, _) =>
    {
        entered.TrySetResult();
        await release.Task;
        return SmokeFactory.CreateBodyMutation(request, "body-first", false);
    });
    var synchronizer = SmokeFactory.Synchronizer(codec, authority, provider, new RecordingCompensator());
    var first = synchronizer.SynchronizeAsync(SmokeFactory.Request(project, "request-serialized-one", targets: ["body-first"])).AsTask();
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var second = synchronizer.SynchronizeAsync(SmokeFactory.Request(project, "request-serialized-two", targets: ["body-second"])).AsTask();
    await Task.Delay(25);
    Check(provider.Calls == 1, "same project second request must wait outside provider");
    release.TrySetResult();
    await first;
    await ExpectCodeAsync("stale_revision", () => second);
    Check(provider.Calls == 1 && synchronizer.ActiveProjectCount == 0, "stale queued request must never execute provider");
}

static async Task PendingProjectQueueIsBoundedAsync()
{
    var (codec, project) = await SmokeFactory.ProjectAsync("pending-cap");
    var authority = new InMemoryCanonicalAuthority(codec, [project]);
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var provider = new DelegateMutationProvider(async (request, _) =>
    {
        entered.TrySetResult();
        await release.Task;
        return SmokeFactory.CreateBodyMutation(request, "body-first", false);
    });
    var synchronizer = SmokeFactory.Synchronizer(codec, authority, provider, new RecordingCompensator());
    var first = synchronizer.SynchronizeAsync(SmokeFactory.Request(
        project,
        "request-pending-first",
        targets: ["body-first"])).AsTask();
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

    using var cancellation = new CancellationTokenSource();
    var waiters = Enumerable.Range(1, PhotonCadRuntimeSyncContract.MaximumPendingMutationsPerProject - 1)
        .Select(index => synchronizer.SynchronizeAsync(
            SmokeFactory.Request(project, $"request-pending-{index}", targets: [$"body-{index}"]),
            cancellation.Token).AsTask())
        .ToArray();
    await ExpectCodeAsync("project_pending_capacity_reached", () => synchronizer.SynchronizeAsync(
        SmokeFactory.Request(project, "request-pending-excess", targets: ["body-excess"])).AsTask());
    Check(provider.Calls == 1, "pending overflow must not enter provider");

    cancellation.Cancel();
    foreach (var waiter in waiters) await ExpectCanceledAsync(() => waiter);
    release.TrySetResult();
    await first;
    Check(synchronizer.ActiveProjectCount == 0, "canceled waiters and completed owner must prune the gate");
}

static async Task DifferentProjectsRunConcurrentlyAsync()
{
    var identities = new[] { ("pcsid:parallel-a", "pcpid:parallel-a"), ("pcsid:parallel-b", "pcpid:parallel-b") };
    var codec = new PhotonCadCanonicalProjectCodecV1(new SequenceIdentityIssuer(identities));
    var firstProject = await codec.CreateAsync("A", PhotonCadProjectUnit.Millimeter);
    var secondProject = await codec.CreateAsync("B", PhotonCadProjectUnit.Millimeter);
    var authority = new InMemoryCanonicalAuthority(codec, [firstProject, secondProject]);
    var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var active = 0;
    var provider = new DelegateMutationProvider(async (request, _) =>
    {
        if (Interlocked.Increment(ref active) == 2) both.TrySetResult();
        await both.Task.WaitAsync(TimeSpan.FromSeconds(5));
        return SmokeFactory.CreateBodyMutation(request, $"body-{request.Request.ProjectId[^1]}", false);
    });
    var synchronizer = SmokeFactory.Synchronizer(codec, authority, provider, new RecordingCompensator());
    var first = synchronizer.SynchronizeAsync(SmokeFactory.Request(firstProject, "request-parallel-a", targets: ["body-a"])).AsTask();
    var second = synchronizer.SynchronizeAsync(SmokeFactory.Request(secondProject, "request-parallel-b", targets: ["body-b"])).AsTask();
    await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
    Check(provider.Calls == 2 && synchronizer.ActiveProjectCount == 0, "distinct projects must run concurrently without gate leaks");
}

static async Task ActiveProjectCapAsync()
{
    var identities = Enumerable.Range(0, PhotonCadRuntimeSyncContract.MaximumActiveProjects + 1)
        .Select(index => ($"pcsid:cap-{index}", $"pcpid:cap-{index}"))
        .ToArray();
    var codec = new PhotonCadCanonicalProjectCodecV1(new SequenceIdentityIssuer(identities));
    var projects = new List<PhotonCadCanonicalProject>();
    foreach (var index in Enumerable.Range(0, identities.Length))
        projects.Add(await codec.CreateAsync($"P{index}", PhotonCadProjectUnit.Millimeter));
    var authority = new InMemoryCanonicalAuthority(codec, projects);
    var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var entered = 0;
    var provider = new DelegateMutationProvider(async (request, _) =>
    {
        if (Interlocked.Increment(ref entered) == PhotonCadRuntimeSyncContract.MaximumActiveProjects) allEntered.TrySetResult();
        await release.Task;
        return SmokeFactory.CreateBodyMutation(request, $"body-{request.Request.RequestId}", false);
    });
    var synchronizer = SmokeFactory.Synchronizer(codec, authority, provider, new RecordingCompensator());
    var activeTasks = projects.Take(PhotonCadRuntimeSyncContract.MaximumActiveProjects)
        .Select((project, index) => synchronizer.SynchronizeAsync(SmokeFactory.Request(
            project,
            $"request-cap-{index}",
            targets: [$"body-request-cap-{index}"])).AsTask())
        .ToArray();
    await allEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var excess = projects[^1];
    await ExpectCodeAsync("active_project_capacity_reached", () => synchronizer.SynchronizeAsync(
        SmokeFactory.Request(excess, "request-cap-excess", targets: ["body-excess"])).AsTask());
    Check(provider.Calls == PhotonCadRuntimeSyncContract.MaximumActiveProjects, "excess project must not affect active providers");
    release.TrySetResult();
    await Task.WhenAll(activeTasks).WaitAsync(TimeSpan.FromSeconds(10));
    Check(synchronizer.ActiveProjectCount == 0, "capacity test must release every project gate");
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void ExpectCode(string code, Action action)
{
    try { action(); }
    catch (PhotonCadProjectException exception) when (exception.Code == code) { return; }
    throw new InvalidOperationException($"Expected Photon CAD error '{code}'.");
}

static async Task ExpectCodeAsync(string code, Func<Task> action)
{
    try { await action(); }
    catch (PhotonCadProjectException exception) when (exception.Code == code) { return; }
    throw new InvalidOperationException($"Expected Photon CAD error '{code}'.");
}

static async Task ExpectCanceledAsync(Func<Task> action)
{
    try { await action(); }
    catch (OperationCanceledException) { return; }
    throw new InvalidOperationException("Expected cancellation.");
}
