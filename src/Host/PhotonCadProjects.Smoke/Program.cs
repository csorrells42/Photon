using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using PhotonCadProjects;

var suite = new SmokeSuite();

await suite.RunAsync("contract rejects renderer paths and malformed bounds", () =>
{
    var issuer = new CryptographicPhotonCadHandleIssuer();
    True(issuer.NewWorkspace().Value.StartsWith("cad-workspace:", StringComparison.Ordinal), "workspace prefix");
    True(issuer.NewProject().Value.StartsWith("cad-project:", StringComparison.Ordinal), "project prefix");
    ThrowsCode(() => _ = new PhotonCadWorkspaceHandle("cad-workspace:C:\\private\\gearbox"), "invalid_handle");
    ThrowsCode(() => _ = new PhotonCadProjectCreateRequest("create:bad", Workspace(1), "C:\\private\\gearbox", PhotonCadProjectUnit.Millimeter), "path_like_display_name");
    ThrowsCode(() => _ = new PhotonCadProjectSaveRequest("save:bad", Project(1), "session:1", "project:1", 0, "not-a-digest"), "invalid_digest");
    ThrowsCode(() => _ = new PhotonCadProjectCloseRequest("close:bad", Project(1), "session:1", "project:1", 1, 2, Hash("a"), Hash("a"), false), "last_saved_revision_ahead");
    var source = Encoding.UTF8.GetBytes("canonical");
    var canonical = new PhotonCadCanonicalProject(
        "session:1",
        "project:1",
        0,
        "Gearbox",
        PhotonCadProjectUnit.Millimeter,
        Hash("logical"),
        PhotonCadBomCanonicalizer.Compute(PhotonCadProjectUnit.Millimeter, []),
        false,
        source);
    source[0] = (byte)'X';
    Equal("canonical", Encoding.UTF8.GetString(canonical.CanonicalBytes.Span), "canonical bytes defensive copy");
    var exposed = canonical.CanonicalBytes;
    True(MemoryMarshal.TryGetArray(exposed, out var exposedArray), "copy remains array-backed for the probe");
    exposedArray.Array![exposedArray.Offset] = (byte)'Z';
    Equal("canonical", Encoding.UTF8.GetString(canonical.CanonicalBytes.Span), "returned memory cannot mutate canonical state");
    Type[] rendererRequestTypes =
    [
        typeof(PhotonCadProjectCreateRequest),
        typeof(PhotonCadProjectOpenRequest),
        typeof(PhotonCadProjectReopenRequest),
        typeof(PhotonCadProjectRefreshRequest),
        typeof(PhotonCadProjectSaveRequest),
        typeof(PhotonCadProjectSaveAsRequest),
        typeof(PhotonCadProjectCloseRequest),
    ];
    foreach (var requestType in rendererRequestTypes)
    {
        True(!requestType.GetProperties().Any(property =>
                property.Name.Contains("Path", StringComparison.Ordinal)
                || property.Name.Contains("Grant", StringComparison.Ordinal)
                || property.Name.Contains("Evidence", StringComparison.Ordinal)
                || property.PropertyType == typeof(PhotonCadStorageTargetHandle)
                || property.PropertyType == typeof(PhotonCadStorageVersion)
                || property.PropertyType == typeof(PhotonCadOverwriteGrantHandle)
                || property.PropertyType == typeof(PhotonCadPathEvidence)),
            $"{requestType.Name} keeps paths, OS identity, versions, and grants host-only");
    }
    return Task.CompletedTask;
});

await suite.RunAsync("BOM canonical digest is deterministic and constructor enforced", () =>
{
    Equal("sha256:469d7e49bae9d5e8d6d0a7eb424560356e466e53f6c8395150c3a90d8ccd969d",
        PhotonCadBomCanonicalizer.Compute(PhotonCadProjectUnit.Millimeter, []), "empty millimeter vector");
    var shaft = new PhotonCadBomRow("PN-100", "Output shaft", 2.0, PhotonCadBomUnit.Each, "shaft:1");
    Equal("sha256:f18f96ebd3c8526fe68e18327edd96c8ec0cdd457408b3620a4e76d57ef5a9c9",
        PhotonCadBomCanonicalizer.Compute(PhotonCadProjectUnit.Millimeter, [shaft]), "single-row vector");
    var gear = new PhotonCadBomRow("PN-200", "Gear", 1.0, PhotonCadBomUnit.Each, "gear:2");
    var longShaft = new PhotonCadBomRow("PN-100", "Output shaft", 250.5, PhotonCadBomUnit.Length, "shaft:1");
    const string twoRowDigest = "sha256:376815066b230ad9b8d0c81b4908b06346e8e4302c860b661b97d2a280410ceb";
    Equal(twoRowDigest, PhotonCadBomCanonicalizer.Compute(PhotonCadProjectUnit.Inch, [gear, longShaft]), "two-row vector");
    Equal(twoRowDigest, PhotonCadBomCanonicalizer.Compute(PhotonCadProjectUnit.Inch, [longShaft, gear]), "input order invariant");
    ThrowsCode(() => _ = new PhotonCadCanonicalProject(
        "session:1", "project:1", 0, "Bad BOM", PhotonCadProjectUnit.Millimeter,
        Hash("logical"), Hash("wrong"), false, Encoding.UTF8.GetBytes("canonical"), [shaft]), "bom_digest_mismatch");
    return Task.CompletedTask;
});

await suite.RunAsync("codec size policy reaches storage before allocation", async () =>
{
    const int policyLimit = 128;
    var backend = new SmokeAtomicBackend();
    var target = Target(901);
    backend.Seed(target, Encoding.UTF8.GetBytes("bounded"));
    var codec = new SmokeProjectCodec(policyLimit);
    var store = new PhotonCadAtomicProjectStore(backend, maximumEncodedBytes: policyLimit);
    Equal(policyLimit, store.MaximumEncodedBytes, "store policy");
    _ = await store.ReadVersionedAsync(target);
    Equal(policyLimit, backend.LastMaximumReadBytes, "backend preallocation bound");
    await ThrowsCodeAsync(
        async () => _ = await store.SaveAsync(Target(902), new byte[policyLimit + 1]),
        "invalid_content_length");
    ThrowsCode(
        () => _ = new PhotonCadProjectCoordinator(new PhotonCadAtomicProjectStore(new SmokeAtomicBackend()), codec),
        "codec_storage_size_policy_mismatch");
});

await suite.RunAsync("duplicate durable identity cannot alias two open files", async () =>
{
    var backend = new SmokeAtomicBackend();
    var codec = new SmokeProjectCodec();
    var canonical = await codec.CreateAsync("Copied gearbox", PhotonCadProjectUnit.Millimeter);
    var firstTarget = Target(903);
    var copiedTarget = Target(904);
    backend.Seed(firstTarget, canonical.CanonicalBytes.ToArray());
    backend.Seed(copiedTarget, canonical.CanonicalBytes.ToArray());
    await using var coordinator = new PhotonCadProjectCoordinator(
        new PhotonCadAtomicProjectStore(backend),
        codec,
        new DeterministicHandleIssuer());
    var firstWorkspace = await coordinator.RegisterWorkspaceAsync(new PhotonCadWorkspaceBinding(firstTarget, "Copied gearbox A"));
    var copiedWorkspace = await coordinator.RegisterWorkspaceAsync(new PhotonCadWorkspaceBinding(copiedTarget, "Copied gearbox B"));
    var first = await coordinator.OpenProjectAsync(new PhotonCadProjectOpenRequest("open:identity-a", firstWorkspace.WorkspaceHandle));
    await ThrowsCodeAsync(
        async () => _ = await coordinator.OpenProjectAsync(new PhotonCadProjectOpenRequest("open:identity-b", copiedWorkspace.WorkspaceHandle)),
        "duplicate_project_identity_open");
    _ = await coordinator.CloseProjectAsync(new PhotonCadProjectCloseRequest(
        "close:identity-a",
        first.ProjectHandle,
        first.Snapshot.SessionId,
        first.Snapshot.ProjectId,
        first.Snapshot.Revision,
        first.LastSavedRevision,
        first.ContentDigest,
        first.LastSavedContentDigest,
        discardUnsavedChanges: false));
    var reopenedCopy = await coordinator.OpenProjectAsync(new PhotonCadProjectOpenRequest("open:identity-after-close", copiedWorkspace.WorkspaceHandle));
    Equal(canonical.ProjectId, reopenedCopy.Snapshot.ProjectId, "copy can open after original closes");
});

await suite.RunAsync("native picker mount and dispatcher fail honestly unavailable", async () =>
{
    IPhotonCadNativeWorkspacePicker picker = new UnavailablePhotonCadNativeWorkspacePicker();
    IPhotonCadNativeWorkspaceMount mount = new UnavailablePhotonCadNativeWorkspaceMount();
    IPhotonCadProjectDispatcher dispatcher = new UnavailablePhotonCadProjectDispatcher();
    var picked = await picker.ChooseAsync("open");
    var mounted = await mount.MountAsync("selection:opaque");
    Equal(PhotonCadNativeServiceStatus.Unavailable, picked.Status, "picker status");
    Equal(PhotonCadNativeServiceStatus.Unavailable, mounted.Status, "mount status");
    True(picked.Binding is null && mounted.Binding is null, "no fabricated binding");
    True(!dispatcher.Available, "dispatcher unavailable");
    await ThrowsCodeAsync(async () => _ = await dispatcher.DispatchAsync(new object()), "dispatcher_unavailable");
});

await suite.RunAsync("atomic save requires exact durable proof and post-write digest", async () =>
{
    var backend = new SmokeAtomicBackend();
    var store = new PhotonCadAtomicProjectStore(backend);
    var target = Target(1);
    var bytes = Encoding.UTF8.GetBytes("gearbox-v1");
    var receipt = await store.SaveAsync(target, bytes);
    True(receipt.Atomic && receipt.FlushedToDisk, "atomic durable receipt");
    Equal(HashBytes(bytes), receipt.StorageDigest, "storage digest");
    Equal("gearbox-v1", Encoding.UTF8.GetString(backend.Bytes(target).Span), "committed bytes");
    Equal("gearbox-v1", Encoding.UTF8.GetString((await store.ReadAsync(target)).Span), "readback bytes");

    await RejectSaveAsync(new SmokeAtomicBackend { ReparseOnInspect = true }, Target(2), "reparse_point_rejected");
    await RejectSeededSaveAsync(new SmokeAtomicBackend { HardLinkOnInspect = true }, Target(3), "hard_link_rejected");
    await RejectSaveAsync(new SmokeAtomicBackend { CrossVolumeOnPrepare = true }, Target(4), "cross_volume_stage", expectQuarantine: true);
    await RejectSaveAsync(new SmokeAtomicBackend { OmitFlushOnPrepare = true }, Target(5), "prepare_not_durable", expectQuarantine: true);
    var identityBackend = new SmokeAtomicBackend();
    var identityTarget = Target(6);
    identityBackend.Seed(identityTarget, Encoding.UTF8.GetBytes("old"));
    var identityStore = new PhotonCadAtomicProjectStore(identityBackend);
    var identityVersion = (await identityStore.ReadVersionedAsync(identityTarget)).Version;
    identityBackend.ChangeIdentityBeforeCommit = true;
    await ThrowsCodeAsync(async () => _ = await identityStore.SaveIfVersionAsync(
        identityTarget, Encoding.UTF8.GetBytes("new"), identityVersion), "target_identity_changed");
    Equal(1, identityBackend.QuarantineCount, "identity conflict quarantined before commit");

    var corruptBackend = new SmokeAtomicBackend { CorruptCommitDigest = true };
    await ThrowsCommittedRecoveryAsync(async () => _ = await new PhotonCadAtomicProjectStore(corruptBackend)
        .SaveAsync(Target(7), Encoding.UTF8.GetBytes("new")));
    Equal(0, corruptBackend.QuarantineCount, "entered commit is never precommit-quarantined");
});

await suite.RunAsync("write preconditions and overwrite grants are exact single-use and expiring", async () =>
{
    var clock = new FixedClock(new DateTimeOffset(2026, 8, 10, 18, 0, 0, TimeSpan.Zero));
    var backend = new SmokeAtomicBackend();
    var store = new PhotonCadAtomicProjectStore(backend, clock);
    var target = Target(8);
    backend.Seed(target, Encoding.UTF8.GetBytes("existing"));
    await ThrowsCodeAsync(async () => _ = await store.SaveAsync(target, Encoding.UTF8.GetBytes("replacement")),
        "target_exists_overwrite_confirmation_required");
    Equal("existing", Encoding.UTF8.GetString(backend.Bytes(target).Span), "must-not-exist preserves target");

    var opened = (await store.ReadVersionedAsync(target)).Version;
    backend.MutateInPlace(target, Encoding.UTF8.GetBytes("external-edit"));
    await ThrowsCodeAsync(async () => _ = await store.SaveIfVersionAsync(target, Encoding.UTF8.GetBytes("ours"), opened),
        "storage_version_conflict");
    Equal("external-edit", Encoding.UTF8.GetString(backend.Bytes(target).Span), "match version preserves external edit");
    var current = (await store.ReadVersionedAsync(target)).Version;
    _ = await store.SaveIfVersionAsync(target, Encoding.UTF8.GetBytes("matched"), current);
    Equal("matched", Encoding.UTF8.GetString(backend.Bytes(target).Span), "exact match saves");

    var grantTarget = Target(9);
    backend.Seed(grantTarget, Encoding.UTF8.GetBytes("grant-old"));
    var context = PhotonCadOverwriteGrantContext.ForCreate();
    var grant = await store.IssueOverwriteGrantAsync(grantTarget, context);
    Equal(32, DecodeOpaqueToken(grant.Value, "cad-overwrite-grant:").Length, "grant has 256 random bits");
    _ = await store.SaveWithOverwriteGrantAsync(grantTarget, Encoding.UTF8.GetBytes("grant-new"), grant, context);
    await ThrowsCodeAsync(async () => _ = await store.SaveWithOverwriteGrantAsync(
        grantTarget, Encoding.UTF8.GetBytes("replay"), grant, context), "overwrite_grant_unknown_or_used");
    Equal("grant-new", Encoding.UTF8.GetString(backend.Bytes(grantTarget).Span), "replay cannot overwrite");

    var changedTarget = Target(90);
    backend.Seed(changedTarget, Encoding.UTF8.GetBytes("confirmed"));
    var changedGrant = await store.IssueOverwriteGrantAsync(changedTarget, context);
    backend.Mutate(changedTarget, Encoding.UTF8.GetBytes("changed-after-confirmation"));
    await ThrowsCodeAsync(async () => _ = await store.SaveWithOverwriteGrantAsync(
        changedTarget, Encoding.UTF8.GetBytes("replacement"), changedGrant, context), "storage_version_conflict");
    Equal("changed-after-confirmation", Encoding.UTF8.GetString(backend.Bytes(changedTarget).Span), "changed grant target preserved");

    var expiredTarget = Target(91);
    backend.Seed(expiredTarget, Encoding.UTF8.GetBytes("expiry-old"));
    var expiredGrant = await store.IssueOverwriteGrantAsync(expiredTarget, context);
    clock.UtcNow = clock.UtcNow.AddMinutes(6);
    await ThrowsCodeAsync(async () => _ = await store.SaveWithOverwriteGrantAsync(
        expiredTarget, Encoding.UTF8.GetBytes("expiry-new"), expiredGrant, context), "overwrite_grant_expired");
    Equal("expiry-old", Encoding.UTF8.GetString(backend.Bytes(expiredTarget).Span), "expired grant preserves target");
});

await suite.RunAsync("commit point ignores late cancellation and preserves explicit recovery", async () =>
{
    var cancellationBackend = new SmokeAtomicBackend();
    using var cancellation = new CancellationTokenSource();
    cancellationBackend.AfterCommitReplace = cancellation.Cancel;
    var cancellationStore = new PhotonCadAtomicProjectStore(cancellationBackend);
    var cancellationTarget = Target(92);
    var saved = await cancellationStore.SaveAsync(cancellationTarget, Encoding.UTF8.GetBytes("committed"), cancellation.Token);
    True(saved.Atomic && cancellation.IsCancellationRequested, "late cancellation returns committed receipt");
    True(!cancellationBackend.CommitReceivedCancelableToken && !cancellationBackend.CompleteReceivedCancelableToken,
        "commit and finalization are non-cancellable");
    Equal("committed", Encoding.UTF8.GetString(cancellationBackend.Bytes(cancellationTarget).Span), "late cancellation preserves commit");

    var precommitBackend = new SmokeAtomicBackend();
    using var precommitCancellation = new CancellationTokenSource();
    precommitBackend.AfterPrepare = precommitCancellation.Cancel;
    await ThrowsCancellationAsync(async () => _ = await new PhotonCadAtomicProjectStore(precommitBackend)
        .SaveAsync(Target(93), Encoding.UTF8.GetBytes("never-committed"), precommitCancellation.Token));
    Equal(1, precommitBackend.QuarantineCount, "precommit cancellation quarantines stage");
    True(!precommitBackend.Exists(Target(93)), "precommit cancellation leaves target absent");

    var recoveryBackend = new SmokeAtomicBackend { ThrowAfterCommitReplace = true };
    var recoveryStore = new PhotonCadAtomicProjectStore(recoveryBackend);
    var recoveryTarget = Target(94);
    var recoveryHandle = await CaptureCommittedRecoveryAsync(async () => _ = await recoveryStore.SaveAsync(
        recoveryTarget, Encoding.UTF8.GetBytes("recover-me")));
    True(recoveryHandle.Value.StartsWith("cad-recovery:", StringComparison.Ordinal), "opaque recovery handle returned");
    Equal(0, recoveryBackend.QuarantineCount, "uncertain commit keeps journal");
    var recovered = await recoveryStore.RecoverAsync();
    Equal(1, recovered.Completed, "recovery finalizes already committed target");
    Equal("recover-me", Encoding.UTF8.GetString(recoveryBackend.Bytes(recoveryTarget).Span), "recovered target preserved");

    var finalizeBackend = new SmokeAtomicBackend { ThrowOnCompleteOnce = true };
    var finalizeStore = new PhotonCadAtomicProjectStore(finalizeBackend);
    var finalizeTarget = Target(95);
    _ = await CaptureCommittedRecoveryAsync(async () => _ = await finalizeStore.SaveAsync(
        finalizeTarget, Encoding.UTF8.GetBytes("finalize-me")));
    var finalized = await finalizeStore.RecoverAsync();
    Equal(1, finalized.Completed, "crash after commit before finalization completes");
    Equal("finalize-me", Encoding.UTF8.GetString(finalizeBackend.Bytes(finalizeTarget).Span), "finalized bytes preserved");
});

await suite.RunAsync("apply-and-save atomically publishes only evidence-bound canonical state", async () =>
{
    var backend = new SmokeAtomicBackend();
    var codec = new SmokeProjectCodec();
    var store = new PhotonCadAtomicProjectStore(backend);
    await using var coordinator = new PhotonCadProjectCoordinator(store, codec, new DeterministicHandleIssuer());
    var target = Target(96);
    var workspace = await coordinator.RegisterWorkspaceAsync(new PhotonCadWorkspaceBinding(target, "Atomic mutation"));
    var created = await coordinator.CreateProjectAsync(new PhotonCadProjectCreateRequest(
        "create:atomic-mutation", workspace.WorkspaceHandle, "Gearbox", PhotonCadProjectUnit.Millimeter));
    var opened = await store.ReadVersionedAsync(target);
    var mapped = codec.Advance(created.Snapshot, "gearbox-v1");
    var readsBefore = backend.ReadCallCount;
    using var lateCancellation = new CancellationTokenSource();
    backend.AfterCommitReplace = lateCancellation.Cancel;

    var outcome = await coordinator.ApplyAndSaveCurrentProjectAsync(
        "apply-save:atomic",
        created.ProjectHandle,
        created.Snapshot.SessionId,
        created.Snapshot.ProjectId,
        created.Snapshot.Revision,
        created.ContentDigest,
        opened.Version,
        mapped,
        lateCancellation.Token);

    Equal(PhotonCadProjectApplyAndSaveStatus.Committed, outcome.Status, "atomic mutation status");
    Equal("committed", outcome.Reason, "atomic mutation reason");
    True(outcome.Receipt is not null && outcome.Document is not null && outcome.RecoveryHandle is null,
        "committed result shape");
    Equal(0L, outcome.Receipt!.BaseRevision, "receipt base revision");
    Equal(1L, outcome.Receipt.CommittedRevision, "receipt committed revision");
    Equal(created.ContentDigest, outcome.Receipt.BaseContentDigest, "receipt base digest");
    Equal(mapped.ContentDigest, outcome.Receipt.ContentDigest, "receipt committed digest");
    True(outcome.Receipt.Atomic && outcome.Receipt.FlushedToDisk, "receipt durable");
    True(!outcome.Document!.Snapshot.Dirty && outcome.Document.Snapshot.Revision == 1,
        "published session is clean and advanced");
    Equal(2, backend.ReadCallCount - readsBefore,
        "SaveIfVersion performs precondition plus internal post-write readback with no coordinator reread");
    True(lateCancellation.IsCancellationRequested, "late cancellation observed after commit");
    var persisted = new SmokeProjectCodec().Decode(backend.Bytes(target));
    Equal(outcome.Document.ContentDigest, persisted.ContentDigest, "published state exactly matches durable bytes");
});

await suite.RunAsync("apply-and-save precommit failures preserve prior session and foreign bytes", async () =>
{
    var backend = new SmokeAtomicBackend();
    var codec = new SmokeProjectCodec();
    var store = new PhotonCadAtomicProjectStore(backend);
    await using var coordinator = new PhotonCadProjectCoordinator(store, codec, new DeterministicHandleIssuer());
    var target = Target(97);
    var workspace = await coordinator.RegisterWorkspaceAsync(new PhotonCadWorkspaceBinding(target, "Precommit mutation"));
    var created = await coordinator.CreateProjectAsync(new PhotonCadProjectCreateRequest(
        "create:precommit-mutation", workspace.WorkspaceHandle, "Auger", PhotonCadProjectUnit.Inch));
    var opened = await store.ReadVersionedAsync(target);
    var mapped = codec.Advance(created.Snapshot, "auger-v1");

    await ThrowsCodeAsync(async () => _ = await coordinator.ApplyAndSaveCurrentProjectAsync(
        "apply-save:bad-base",
        created.ProjectHandle,
        created.Snapshot.SessionId,
        created.Snapshot.ProjectId,
        created.Snapshot.Revision,
        Hash("wrong-base"),
        opened.Version,
        mapped), "save_binding_mismatch");
    await ThrowsCodeAsync(async () => _ = await coordinator.ApplyAndSaveCurrentProjectAsync(
        "apply-save:clean-candidate",
        created.ProjectHandle,
        created.Snapshot.SessionId,
        created.Snapshot.ProjectId,
        created.Snapshot.Revision,
        created.ContentDigest,
        opened.Version,
        created.Snapshot), "non_advancing_dirty_update");

    var foreign = Encoding.UTF8.GetBytes("foreign-edit");
    backend.MutateInPlace(target, foreign);
    await ThrowsCodeAsync(async () => _ = await coordinator.ApplyAndSaveCurrentProjectAsync(
        "apply-save:storage-conflict",
        created.ProjectHandle,
        created.Snapshot.SessionId,
        created.Snapshot.ProjectId,
        created.Snapshot.Revision,
        created.ContentDigest,
        opened.Version,
        mapped), "storage_version_conflict");
    True(backend.Bytes(target).Span.SequenceEqual(foreign), "precommit conflict preserves foreign bytes");
    _ = await coordinator.CloseProjectAsync(Close("close:precommit-prior", created, discard: false));
});

await suite.RunAsync("apply-and-save postcommit ambiguity returns recovery without publishing memory", async () =>
{
    var recoveryBackend = new SmokeAtomicBackend();
    var recoveryCodec = new SmokeProjectCodec();
    var recoveryStore = new PhotonCadAtomicProjectStore(recoveryBackend);
    await using var recoveryCoordinator = new PhotonCadProjectCoordinator(
        recoveryStore, recoveryCodec, new DeterministicHandleIssuer());
    var recoveryTarget = Target(98);
    var recoveryWorkspace = await recoveryCoordinator.RegisterWorkspaceAsync(
        new PhotonCadWorkspaceBinding(recoveryTarget, "Recovery mutation"));
    var recoveryBase = await recoveryCoordinator.CreateProjectAsync(new PhotonCadProjectCreateRequest(
        "create:recovery-mutation", recoveryWorkspace.WorkspaceHandle, "Sprocket", PhotonCadProjectUnit.Millimeter));
    var recoveryVersion = await recoveryStore.ReadVersionedAsync(recoveryTarget);
    recoveryBackend.ThrowAfterCommitReplace = true;
    var recoveryOutcome = await recoveryCoordinator.ApplyAndSaveCurrentProjectAsync(
        "apply-save:recovery",
        recoveryBase.ProjectHandle,
        recoveryBase.Snapshot.SessionId,
        recoveryBase.Snapshot.ProjectId,
        recoveryBase.Snapshot.Revision,
        recoveryBase.ContentDigest,
        recoveryVersion.Version,
        recoveryCodec.Advance(recoveryBase.Snapshot, "sprocket-v1"));
    Equal(PhotonCadProjectApplyAndSaveStatus.CommittedRecoveryRequired, recoveryOutcome.Status, "recovery status");
    True(recoveryOutcome.RecoveryHandle is not null && recoveryOutcome.Receipt is null && recoveryOutcome.Document is null,
        "recovery result is opaque and pathless");
    _ = await recoveryCoordinator.CloseProjectAsync(Close("close:recovery-prior", recoveryBase, discard: false));
    var recovered = await recoveryStore.RecoverAsync();
    Equal(1, recovered.Completed, "storage recovery recognizes already committed mutation");

    var rebindBackend = new SmokeAtomicBackend();
    var rebindCodec = new SmokeProjectCodec();
    var rebindStore = new PhotonCadAtomicProjectStore(rebindBackend);
    await using var rebindCoordinator = new PhotonCadProjectCoordinator(
        rebindStore, rebindCodec, new DeterministicHandleIssuer());
    var rebindTarget = Target(99);
    var rebindWorkspace = await rebindCoordinator.RegisterWorkspaceAsync(
        new PhotonCadWorkspaceBinding(rebindTarget, "Rebind mutation"));
    var rebindBase = await rebindCoordinator.CreateProjectAsync(new PhotonCadProjectCreateRequest(
        "create:rebind-mutation", rebindWorkspace.WorkspaceHandle, "Shaft", PhotonCadProjectUnit.Inch));
    var rebindVersion = await rebindStore.ReadVersionedAsync(rebindTarget);
    var rebindMapped = rebindCodec.Advance(rebindBase.Snapshot, "shaft-v1");
    var readsBefore = rebindBackend.ReadCallCount;
    rebindCodec.FailNextDecode = true;
    var rebindOutcome = await rebindCoordinator.ApplyAndSaveCurrentProjectAsync(
        "apply-save:rebind",
        rebindBase.ProjectHandle,
        rebindBase.Snapshot.SessionId,
        rebindBase.Snapshot.ProjectId,
        rebindBase.Snapshot.Revision,
        rebindBase.ContentDigest,
        rebindVersion.Version,
        rebindMapped);
    Equal(PhotonCadProjectApplyAndSaveStatus.CommittedReadbackRequired, rebindOutcome.Status, "rebind status");
    True(rebindOutcome.Receipt is not null && rebindOutcome.Document is null && rebindOutcome.RecoveryHandle is null,
        "rebind failure returns committed receipt only");
    Equal(2, rebindBackend.ReadCallCount - readsBefore, "failed codec rebind does not trigger a second filesystem read");
    _ = await rebindCoordinator.CloseProjectAsync(Close("close:rebind-prior", rebindBase, discard: false));
    var durable = new SmokeProjectCodec().Decode(rebindBackend.Bytes(rebindTarget));
    Equal(rebindMapped.ContentDigest, durable.ContentDigest, "committed bytes remain durable despite unpublished memory");
});

await suite.RunAsync("session save receipt close and reopen stay revision and digest bound", async () =>
{
    var backend = new SmokeAtomicBackend { PrepareDelayMilliseconds = 20 };
    var codec = new SmokeProjectCodec();
    var clock = new FixedClock(new DateTimeOffset(2026, 8, 10, 18, 0, 0, TimeSpan.Zero));
    await using var coordinator = new PhotonCadProjectCoordinator(
        new PhotonCadAtomicProjectStore(backend), codec, new DeterministicHandleIssuer(), clock);
    var workspace = await coordinator.RegisterWorkspaceAsync(new PhotonCadWorkspaceBinding(Target(10), "Gearbox project"));
    var parkedWorkspace = await coordinator.RegisterWorkspaceAsync(new PhotonCadWorkspaceBinding(Target(11), "Registered for later"));
    var created = await coordinator.CreateProjectAsync(new PhotonCadProjectCreateRequest("create:1", workspace.WorkspaceHandle, "Gearbox", PhotonCadProjectUnit.Millimeter));
    True(!created.Snapshot.Dirty, "created clean");
    var dirty = await coordinator.ApplyCurrentProjectAsync(created.ProjectHandle, codec.Advance(created.Snapshot, "gear-train-v1"));
    True(dirty.Snapshot.Dirty && dirty.Snapshot.Revision == 1, "dirty revision advanced");
    await ThrowsCodeAsync(async () => _ = await coordinator.SaveProjectAsync(new PhotonCadProjectSaveRequest(
        "save:stale", dirty.ProjectHandle, dirty.Snapshot.SessionId, dirty.Snapshot.ProjectId, 0, dirty.ContentDigest)), "save_binding_mismatch");
    var saved = await coordinator.SaveProjectAsync(new PhotonCadProjectSaveRequest(
        "save:1", dirty.ProjectHandle, dirty.Snapshot.SessionId, dirty.Snapshot.ProjectId, dirty.Snapshot.Revision, dirty.ContentDigest));
    Equal(dirty.ProjectHandle, saved.Receipt.SourceProjectHandle, "receipt source handle");
    Equal(dirty.ProjectHandle, saved.Receipt.ProjectHandle, "receipt project handle");
    Equal(1L, saved.Receipt.BaseRevision, "receipt base revision");
    Equal(1L, saved.Receipt.SavedRevision, "receipt saved revision");
    Equal(dirty.ContentDigest, saved.Receipt.ContentDigest, "receipt logical digest");
    True(saved.Receipt.Atomic && !saved.Document.Snapshot.Dirty, "saved receipt and clean snapshot");

    var saveTwo = coordinator.SaveProjectAsync(new PhotonCadProjectSaveRequest(
        "save:2", saved.Document.ProjectHandle, saved.Document.Snapshot.SessionId, saved.Document.Snapshot.ProjectId, 1, saved.Document.ContentDigest)).AsTask();
    var saveThree = coordinator.SaveProjectAsync(new PhotonCadProjectSaveRequest(
        "save:3", saved.Document.ProjectHandle, saved.Document.Snapshot.SessionId, saved.Document.Snapshot.ProjectId, 1, saved.Document.ContentDigest)).AsTask();
    await Task.WhenAll(saveTwo, saveThree);
    Equal(1, backend.MaximumConcurrentPrepare, "mutating saves serialized");

    var dirtyAgain = await coordinator.ApplyCurrentProjectAsync(saved.Document.ProjectHandle, codec.Advance(saved.Document.Snapshot, "unsaved-change"));
    var closeWithoutDiscard = Close("close:no", dirtyAgain, discard: false);
    await ThrowsCodeAsync(async () => _ = await coordinator.CloseProjectAsync(closeWithoutDiscard), "dirty_close_confirmation_required");
    var closed = await coordinator.CloseProjectAsync(Close("close:yes", dirtyAgain, discard: true));
    var parked = await coordinator.CreateProjectAsync(new PhotonCadProjectCreateRequest(
        "create:parked", parkedWorkspace.WorkspaceHandle, "Parked", PhotonCadProjectUnit.Millimeter));
    Equal("Parked", parked.DisplayName, "closing another project keeps registered workspace usable");
    Equal(1L, closed.Reopen.LastSavedRevision, "reopen saved revision");
    Equal(saved.Document.ContentDigest, closed.Reopen.ContentDigest, "reopen saved digest");
    var reopened = await coordinator.ReopenProjectAsync(new PhotonCadProjectReopenRequest("reopen:1", closed.Reopen.ReopenHandle));
    Equal(1L, reopened.Snapshot.Revision, "discarded edit not reopened");
    True(!reopened.Snapshot.Dirty, "reopened clean");
    await coordinator.CloseProjectAsync(Close("close:reopened", reopened, discard: false));
    await ThrowsCodeAsync(async () => _ = await coordinator.ReopenProjectAsync(new PhotonCadProjectReopenRequest("reopen:reuse", closed.Reopen.ReopenHandle)), "reopen_handle_unknown");
});

await suite.RunAsync("save as changes only the opaque project and workspace authority", async () =>
{
    var backend = new SmokeAtomicBackend();
    var codec = new SmokeProjectCodec();
    await using var coordinator = new PhotonCadProjectCoordinator(new PhotonCadAtomicProjectStore(backend), codec, new DeterministicHandleIssuer());
    var sourceWorkspace = await coordinator.RegisterWorkspaceAsync(new PhotonCadWorkspaceBinding(Target(20), "Source"));
    var destinationWorkspace = await coordinator.RegisterWorkspaceAsync(new PhotonCadWorkspaceBinding(Target(21), "Destination"));
    var source = await coordinator.CreateProjectAsync(new PhotonCadProjectCreateRequest("create:save-as", sourceWorkspace.WorkspaceHandle, "Auger", PhotonCadProjectUnit.Inch));
    var dirty = await coordinator.ApplyCurrentProjectAsync(source.ProjectHandle, codec.Advance(source.Snapshot, "auger-flight"));
    var savedAs = await coordinator.SaveProjectAsAsync(new PhotonCadProjectSaveAsRequest(
        "save-as:1", source.ProjectHandle, destinationWorkspace.WorkspaceHandle, dirty.Snapshot.SessionId, dirty.Snapshot.ProjectId, dirty.Snapshot.Revision, dirty.ContentDigest));
    Equal(source.ProjectHandle, savedAs.Receipt.SourceProjectHandle, "save-as source");
    True(!source.ProjectHandle.Equals(savedAs.Receipt.ProjectHandle), "save-as new project handle");
    Equal(destinationWorkspace.WorkspaceHandle, savedAs.Document.WorkspaceHandle, "destination workspace");
    await ThrowsCodeAsync(async () => _ = await coordinator.RefreshProjectAsync(new PhotonCadProjectRefreshRequest(
        "refresh:old", source.ProjectHandle, dirty.Snapshot.SessionId, dirty.Snapshot.ProjectId, dirty.Snapshot.Revision)), "project_handle_unknown");
});

await suite.RunAsync("coordinator consumes host-confirmed overwrite and binds save-as source", async () =>
{
    var backend = new SmokeAtomicBackend();
    var codec = new SmokeProjectCodec();
    var clock = new FixedClock(new DateTimeOffset(2026, 8, 10, 18, 0, 0, TimeSpan.Zero));
    var store = new PhotonCadAtomicProjectStore(backend, clock);
    await using var coordinator = new PhotonCadProjectCoordinator(store, codec, new DeterministicHandleIssuer(), clock);
    var sourceWorkspace = await coordinator.RegisterWorkspaceAsync(new PhotonCadWorkspaceBinding(Target(22), "Overwrite source"));
    var destinationTarget = Target(23);
    backend.Seed(destinationTarget, Encoding.UTF8.GetBytes("existing-destination"));
    var destinationWorkspace = await coordinator.RegisterWorkspaceAsync(new PhotonCadWorkspaceBinding(destinationTarget, "Overwrite destination"));
    var source = await coordinator.CreateProjectAsync(new PhotonCadProjectCreateRequest(
        "create:overwrite-source", sourceWorkspace.WorkspaceHandle, "Gear train", PhotonCadProjectUnit.Millimeter));
    var dirty = await coordinator.ApplyCurrentProjectAsync(source.ProjectHandle, codec.Advance(source.Snapshot, "ready-for-save-as"));
    _ = await coordinator.ConfirmOverwriteAsync(
        destinationWorkspace.WorkspaceHandle, PhotonCadOverwritePurpose.SaveAs, dirty.ProjectHandle);
    var savedAs = await coordinator.SaveProjectAsAsync(new PhotonCadProjectSaveAsRequest(
        "save-as:overwrite", dirty.ProjectHandle, destinationWorkspace.WorkspaceHandle,
        dirty.Snapshot.SessionId, dirty.Snapshot.ProjectId, dirty.Snapshot.Revision, dirty.ContentDigest));
    True(!Encoding.UTF8.GetString(backend.Bytes(destinationTarget).Span).Contains("existing-destination", StringComparison.Ordinal),
        "confirmed save-as replaces exact destination");
    Equal(destinationWorkspace.WorkspaceHandle, savedAs.Document.WorkspaceHandle, "confirmed save-as destination");

    var createTarget = Target(24);
    backend.Seed(createTarget, Encoding.UTF8.GetBytes("existing-create-target"));
    var createWorkspace = await coordinator.RegisterWorkspaceAsync(new PhotonCadWorkspaceBinding(createTarget, "Create replacement"));
    _ = await coordinator.ConfirmOverwriteAsync(createWorkspace.WorkspaceHandle, PhotonCadOverwritePurpose.Create);
    var created = await coordinator.CreateProjectAsync(new PhotonCadProjectCreateRequest(
        "create:confirmed-overwrite", createWorkspace.WorkspaceHandle, "Replacement", PhotonCadProjectUnit.Inch));
    Equal("Replacement", created.DisplayName, "confirmed create succeeds");

    var staleSourceWorkspace = await coordinator.RegisterWorkspaceAsync(new PhotonCadWorkspaceBinding(Target(25), "Stale source"));
    var staleDestinationTarget = Target(26);
    backend.Seed(staleDestinationTarget, Encoding.UTF8.GetBytes("stale-destination-original"));
    var staleDestinationWorkspace = await coordinator.RegisterWorkspaceAsync(new PhotonCadWorkspaceBinding(staleDestinationTarget, "Stale destination"));
    var staleSource = await coordinator.CreateProjectAsync(new PhotonCadProjectCreateRequest(
        "create:stale-source", staleSourceWorkspace.WorkspaceHandle, "Stale source", PhotonCadProjectUnit.Millimeter));
    _ = await coordinator.ConfirmOverwriteAsync(
        staleDestinationWorkspace.WorkspaceHandle, PhotonCadOverwritePurpose.SaveAs, staleSource.ProjectHandle);
    var changedSource = await coordinator.ApplyCurrentProjectAsync(
        staleSource.ProjectHandle, codec.Advance(staleSource.Snapshot, "changed-after-confirmation"));
    await ThrowsCodeAsync(async () => _ = await coordinator.SaveProjectAsAsync(new PhotonCadProjectSaveAsRequest(
        "save-as:stale-grant", changedSource.ProjectHandle, staleDestinationWorkspace.WorkspaceHandle,
        changedSource.Snapshot.SessionId, changedSource.Snapshot.ProjectId, changedSource.Snapshot.Revision, changedSource.ContentDigest)),
        "overwrite_grant_binding_mismatch");
    Equal("stale-destination-original", Encoding.UTF8.GetString(backend.Bytes(staleDestinationTarget).Span),
        "source-bound grant cannot overwrite after source changes");
});

await suite.RunAsync("external edits conflict and refresh reloads only advancing clean state", async () =>
{
    var backend = new SmokeAtomicBackend();
    var codec = new SmokeProjectCodec();
    await using var coordinator = new PhotonCadProjectCoordinator(
        new PhotonCadAtomicProjectStore(backend), codec, new DeterministicHandleIssuer());

    var conflictTarget = Target(27);
    var conflictWorkspace = await coordinator.RegisterWorkspaceAsync(new PhotonCadWorkspaceBinding(conflictTarget, "Conflict"));
    var conflictBase = await coordinator.CreateProjectAsync(new PhotonCadProjectCreateRequest(
        "create:conflict", conflictWorkspace.WorkspaceHandle, "Conflict", PhotonCadProjectUnit.Millimeter));
    var external = codec.PersistedAdvance(conflictBase.Snapshot, "external-v1");
    backend.MutateInPlace(conflictTarget, external.CanonicalBytes);
    var local = await coordinator.ApplyCurrentProjectAsync(
        conflictBase.ProjectHandle, codec.Advance(conflictBase.Snapshot, "local-v1"));
    await ThrowsCodeAsync(async () => _ = await coordinator.SaveProjectAsync(new PhotonCadProjectSaveRequest(
        "save:external-conflict", local.ProjectHandle, local.Snapshot.SessionId, local.Snapshot.ProjectId,
        local.Snapshot.Revision, local.ContentDigest)), "storage_version_conflict");
    Equal(external.ContentDigest, new SmokeProjectCodec().Decode(backend.Bytes(conflictTarget)).ContentDigest,
        "save conflict preserves external bytes");
    await ThrowsCodeAsync(async () => _ = await coordinator.RefreshProjectAsync(new PhotonCadProjectRefreshRequest(
        "refresh:dirty-conflict", local.ProjectHandle, local.Snapshot.SessionId, local.Snapshot.ProjectId, local.Snapshot.Revision)),
        "refresh_conflict_unsaved_changes");

    var refreshTarget = Target(28);
    var refreshWorkspace = await coordinator.RegisterWorkspaceAsync(new PhotonCadWorkspaceBinding(refreshTarget, "Refresh"));
    var refreshBase = await coordinator.CreateProjectAsync(new PhotonCadProjectCreateRequest(
        "create:refresh", refreshWorkspace.WorkspaceHandle, "Refresh", PhotonCadProjectUnit.Inch));
    var newer = codec.PersistedAdvance(refreshBase.Snapshot, "external-newer");
    backend.Mutate(refreshTarget, newer.CanonicalBytes);
    var refreshed = await coordinator.RefreshProjectAsync(new PhotonCadProjectRefreshRequest(
        "refresh:newer", refreshBase.ProjectHandle, refreshBase.Snapshot.SessionId, refreshBase.Snapshot.ProjectId, refreshBase.Snapshot.Revision));
    Equal(newer.Revision, refreshed.Snapshot.Revision, "refresh advances revision");
    Equal(newer.ContentDigest, refreshed.ContentDigest, "refresh adopts newer content");

    var sameRevisionChange = codec.PersistedSameRevisionChange(refreshed.Snapshot, "same-revision-change");
    backend.Mutate(refreshTarget, sameRevisionChange.CanonicalBytes);
    await ThrowsCodeAsync(async () => _ = await coordinator.RefreshProjectAsync(new PhotonCadProjectRefreshRequest(
        "refresh:non-advancing", refreshed.ProjectHandle, refreshed.Snapshot.SessionId, refreshed.Snapshot.ProjectId, refreshed.Snapshot.Revision)),
        "refresh_non_advancing_change");
});

await suite.RunAsync("open reads are latest wins and stale work cannot register", async () =>
{
    var backend = new SmokeAtomicBackend { ReadDelayMilliseconds = 100 };
    var codec = new SmokeProjectCodec();
    var seeded = await codec.CreateAsync("Seeded", PhotonCadProjectUnit.Millimeter);
    var target = Target(30);
    backend.Seed(target, seeded.CanonicalBytes);
    await using var coordinator = new PhotonCadProjectCoordinator(new PhotonCadAtomicProjectStore(backend), codec, new DeterministicHandleIssuer());
    var workspace = await coordinator.RegisterWorkspaceAsync(new PhotonCadWorkspaceBinding(target, "Seeded"));
    var first = coordinator.OpenProjectAsync(new PhotonCadProjectOpenRequest("open:first", workspace.WorkspaceHandle)).AsTask();
    await Task.Delay(10);
    var second = coordinator.OpenProjectAsync(new PhotonCadProjectOpenRequest("open:second", workspace.WorkspaceHandle)).AsTask();
    await ThrowsCodeAsync(async () => _ = await first, "read_superseded");
    var opened = await second;
    Equal("Seeded", opened.DisplayName, "latest open won");
});

await suite.RunAsync("independent targets load concurrently without cross-tab cancellation", async () =>
{
    var backend = new SmokeAtomicBackend { ReadDelayMilliseconds = 100 };
    var codec = new SmokeProjectCodec();
    var firstProject = await codec.CreateAsync("First tab", PhotonCadProjectUnit.Millimeter);
    var secondProject = await codec.CreateAsync("Second tab", PhotonCadProjectUnit.Inch);
    var firstTarget = Target(31);
    var secondTarget = Target(32);
    backend.Seed(firstTarget, firstProject.CanonicalBytes);
    backend.Seed(secondTarget, secondProject.CanonicalBytes);
    await using var coordinator = new PhotonCadProjectCoordinator(
        new PhotonCadAtomicProjectStore(backend), codec, new DeterministicHandleIssuer());
    var firstWorkspace = await coordinator.RegisterWorkspaceAsync(new PhotonCadWorkspaceBinding(firstTarget, "First tab"));
    var secondWorkspace = await coordinator.RegisterWorkspaceAsync(new PhotonCadWorkspaceBinding(secondTarget, "Second tab"));
    var first = coordinator.OpenProjectAsync(new PhotonCadProjectOpenRequest("open:tab-one", firstWorkspace.WorkspaceHandle)).AsTask();
    var second = coordinator.OpenProjectAsync(new PhotonCadProjectOpenRequest("open:tab-two", secondWorkspace.WorkspaceHandle)).AsTask();
    var opened = await Task.WhenAll(first, second);
    True(opened.Any(document => document.DisplayName == "First tab")
        && opened.Any(document => document.DisplayName == "Second tab"), "both tabs open");
    True(backend.MaximumConcurrentRead >= 2, "different target reads overlap");
});

await suite.RunAsync("workspace and request registries prune without hard-bricking the session", async () =>
{
    var coordinator = new PhotonCadProjectCoordinator(
        new PhotonCadAtomicProjectStore(new SmokeAtomicBackend()), new SmokeProjectCodec(), new DeterministicHandleIssuer());
    var first = await coordinator.RegisterWorkspaceAsync(new PhotonCadWorkspaceBinding(Target(1000), "First registry target"));
    var duplicate = await coordinator.RegisterWorkspaceAsync(new PhotonCadWorkspaceBinding(Target(1000), "Same target again"));
    Equal(first.WorkspaceHandle, duplicate.WorkspaceHandle, "duplicate target reuses registration");
    for (var index = 1; index < PhotonCadProjectContract.MaximumKnownWorkspaces; index++)
        _ = await coordinator.RegisterWorkspaceAsync(new PhotonCadWorkspaceBinding(Target(1000 + index), $"Target {index}"));
    var replacement = await coordinator.RegisterWorkspaceAsync(new PhotonCadWorkspaceBinding(Target(5000), "After prune"));
    True(replacement.WorkspaceHandle is not null, "unused workspace capacity pruned");
    await ThrowsCodeAsync(async () => _ = await coordinator.OpenProjectAsync(
        new PhotonCadProjectOpenRequest("open:pruned", first.WorkspaceHandle)), "workspace_handle_unknown");

    var unknownWorkspace = Workspace(999_999);
    for (var index = 0; index <= PhotonCadProjectContract.MaximumRequestHistory; index++)
    {
        try
        {
            _ = await coordinator.OpenProjectAsync(new PhotonCadProjectOpenRequest($"history:{index}", unknownWorkspace));
            throw new InvalidOperationException("unknown workspace unexpectedly opened");
        }
        catch (PhotonCadProjectException exception) when (exception.Code == "workspace_handle_unknown") { }
    }
    await ThrowsCodeAsync(async () => _ = await coordinator.OpenProjectAsync(
        new PhotonCadProjectOpenRequest("history:0", unknownWorkspace)), "workspace_handle_unknown");
    await coordinator.DisposeAsync();

    var disposeCoordinator = new PhotonCadProjectCoordinator(
        new PhotonCadAtomicProjectStore(new SmokeAtomicBackend()), new SmokeProjectCodec(), new DeterministicHandleIssuer());
    await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => disposeCoordinator.DisposeAsync().AsTask()));
    await ThrowsAsync<ObjectDisposedException>(async () => _ = await disposeCoordinator.RegisterWorkspaceAsync(
        new PhotonCadWorkspaceBinding(Target(6000), "Disposed")));
});

await suite.RunAsync("crash recovery resumes unchanged targets and quarantines conflicts", async () =>
{
    var backend = new SmokeAtomicBackend();
    var store = new PhotonCadAtomicProjectStore(backend);
    var resumable = Target(40);
    var resumeBytes = Encoding.UTF8.GetBytes("prepared-save");
    _ = await backend.LeavePreparedAsync(resumable, resumeBytes);
    var resumed = await store.RecoverAsync();
    Equal(1, resumed.Resumed, "recovery resumed");
    Equal("prepared-save", Encoding.UTF8.GetString(backend.Bytes(resumable).Span), "recovered bytes");

    var conflicted = Target(41);
    backend.Seed(conflicted, Encoding.UTF8.GetBytes("before"));
    _ = await backend.LeavePreparedAsync(conflicted, Encoding.UTF8.GetBytes("pending"));
    backend.Mutate(conflicted, Encoding.UTF8.GetBytes("foreign-change"));
    var quarantined = await store.RecoverAsync();
    Equal(1, quarantined.Quarantined, "conflict quarantined");
    Equal("foreign-change", Encoding.UTF8.GetString(backend.Bytes(conflicted).Span), "foreign target preserved");
    True(backend.QuarantineCount > 0, "quarantine recorded");
});

return suite.Complete();

static PhotonCadProjectCloseRequest Close(string requestId, PhotonCadProjectDocument document, bool discard) => new(
    requestId,
    document.ProjectHandle,
    document.Snapshot.SessionId,
    document.Snapshot.ProjectId,
    document.Snapshot.Revision,
    document.LastSavedRevision,
    document.ContentDigest,
    document.LastSavedContentDigest,
    discard);

static async Task RejectSaveAsync(SmokeAtomicBackend backend, PhotonCadStorageTargetHandle target, string code, bool expectQuarantine = false)
{
    var store = new PhotonCadAtomicProjectStore(backend);
    await ThrowsCodeAsync(async () => _ = await store.SaveAsync(target, Encoding.UTF8.GetBytes("new")), code);
    if (expectQuarantine) True(backend.QuarantineCount == 1, $"{code} quarantined");
}

static async Task RejectSeededSaveAsync(SmokeAtomicBackend backend, PhotonCadStorageTargetHandle target, string code, bool expectQuarantine = false)
{
    backend.Seed(target, Encoding.UTF8.GetBytes("old"));
    await RejectSaveAsync(backend, target, code, expectQuarantine);
}

static PhotonCadWorkspaceHandle Workspace(int value) => new($"cad-workspace:{value.ToString("x8").PadLeft(32, '0')}");
static PhotonCadProjectHandle Project(int value) => new($"cad-project:{value.ToString("x8").PadLeft(32, '0')}");
static PhotonCadStorageTargetHandle Target(int value) => new($"cad-storage-target:{value.ToString("x8").PadLeft(32, '0')}");
static string Hash(string value) => HashBytes(Encoding.UTF8.GetBytes(value));
static string HashBytes(ReadOnlySpan<byte> value) => $"sha256:{Convert.ToHexStringLower(SHA256.HashData(value))}";

static byte[] DecodeOpaqueToken(string value, string prefix)
{
    True(value.StartsWith(prefix, StringComparison.Ordinal), "opaque token prefix");
    var encoded = value[prefix.Length..].Replace('-', '+').Replace('_', '/');
    encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
    return Convert.FromBase64String(encoded);
}

static void True(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Equal<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{message}: expected {expected}, actual {actual}");
}

static void ThrowsCode(Action action, string code)
{
    try
    {
        action();
        throw new InvalidOperationException($"{code}: no exception");
    }
    catch (PhotonCadProjectException exception)
    {
        Equal(code, exception.Code, "exception code");
    }
}

static async Task ThrowsCodeAsync(Func<Task> action, string code)
{
    try
    {
        await action();
        throw new InvalidOperationException($"{code}: no exception");
    }
    catch (PhotonCadProjectException exception)
    {
        Equal(code, exception.Code, "exception code");
    }
}

static async Task ThrowsCommittedRecoveryAsync(Func<Task> action) => _ = await CaptureCommittedRecoveryAsync(action);

static async Task<PhotonCadRecoveryHandle> CaptureCommittedRecoveryAsync(Func<Task> action)
{
    try
    {
        await action();
        throw new InvalidOperationException("committed_recovery_required: no exception");
    }
    catch (PhotonCadCommittedRecoveryRequiredException exception)
    {
        Equal("committed_recovery_required", exception.Code, "committed recovery code");
        return exception.RecoveryHandle;
    }
}

static async Task ThrowsCancellationAsync(Func<Task> action)
{
    try
    {
        await action();
        throw new InvalidOperationException("cancellation: no exception");
    }
    catch (OperationCanceledException) { }
}

static async Task ThrowsAsync<TException>(Func<Task> action) where TException : Exception
{
    try
    {
        await action();
        throw new InvalidOperationException($"{typeof(TException).Name}: no exception");
    }
    catch (TException) { }
}

internal sealed class SmokeSuite
{
    private readonly Stopwatch _total = Stopwatch.StartNew();
    private int _failed;
    private int _passed;

    internal async Task RunAsync(string name, Func<Task> test)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await test();
            _passed++;
            Console.WriteLine($"PASS {name} ({stopwatch.Elapsed.TotalMilliseconds:F1} ms)");
        }
        catch (Exception exception)
        {
            _failed++;
            Console.Error.WriteLine($"FAIL {name}: {exception.GetType().Name}: {exception.Message}");
        }
    }

    internal int Complete()
    {
        _total.Stop();
        Console.WriteLine($"PhotonCadProjects smoke: {_passed}/{_passed + _failed} passed in {_total.Elapsed.TotalMilliseconds:F1} ms.");
        return _failed == 0 ? 0 : 1;
    }
}
