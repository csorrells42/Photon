using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PhotonCadProjects;
using PhotonCadProjects.Codec;
using PhotonCadProjects.RuntimeSync;
using PhotonCadProjects.Windows;
using System.Windows;
using System.Windows.Interop;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("PhotonCadProjects.Windows.Smoke is unavailable: Windows is required.");
    return 2;
}

var root = Path.Combine(Path.GetTempPath(), $"photon-cad-windows-smoke-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);
var suite = new SmokeSuite(root);

await suite.RunAsync("opaque target and native mount capabilities never expose paths", async () =>
{
    var registry = new PhotonCadWindowsTargetRegistry();
    var path = suite.PathFor("opaque.photoncad");
    var binding = registry.RegisterExactPath(path);
    True(registry.Available, "registry available");
    True(binding.Target.Value.StartsWith("cad-storage-target:", StringComparison.Ordinal), "opaque target prefix");
    True(!binding.Target.Value.Contains(root, StringComparison.OrdinalIgnoreCase), "handle excludes path");
    Equal("opaque.photoncad", binding.Label, "safe display label");

    var token = registry.IssueMountToken(path);
    var mount = new PhotonCadWindowsWorkspaceMount(registry);
    var selected = await mount.MountAsync(token);
    Equal(PhotonCadNativeServiceStatus.Selected, selected.Status, "mount selected");
    Equal(binding.Target, selected.Binding!.Target, "mount binding");
    var replay = await mount.MountAsync(token);
    Equal(PhotonCadNativeServiceStatus.Rejected, replay.Status, "mount token single use");
    True(replay.Binding is null, "replay has no binding");

    var picker = new PhotonCadWindowsWorkspacePicker(registry, new FixedDialog(true, suite.PathFor("picked.photoncad")));
    var picked = await picker.ChooseAsync("new");
    Equal(PhotonCadNativeServiceStatus.Selected, picked.Status, "picker selected");
    var cancelled = await new PhotonCadWindowsWorkspacePicker(registry, new FixedDialog(false, null)).ChooseAsync("open");
    Equal(PhotonCadNativeServiceStatus.Cancelled, cancelled.Status, "picker cancellation is honest");
});

await suite.RunAsync("native project dialog is invoked once with the exact live owner", async () =>
{
    var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var thread = new Thread(() =>
    {
        Window? owner = null;
        try
        {
            owner = new Window();
            _ = new WindowInteropHelper(owner).EnsureHandle();
            var calls = 0;
            var dialog = new PhotonCadWindowsFileDialog(
                () => owner,
                (candidate, actualOwner) =>
                {
                    calls++;
                    True(ReferenceEquals(owner, actualOwner), "dialog owner identity");
                    if (calls == 1)
                    {
                        Equal("Open Photon CAD project", candidate.Title, "open dialog title");
                        Equal(string.Empty, candidate.FileName, "open dialog has no suggested file");
                    }
                    else
                    {
                        Equal("Create Photon CAD project", candidate.Title, "new dialog title");
                        Equal("Gearbox Input", candidate.FileName, "new dialog receives the modal project name");
                    }
                    return false;
                });
            var result = dialog.Show("open");
            True(!result.Accepted && result.ExactPath is null, "owned dialog cancellation");
            var newResult = dialog.Show("new", "Gearbox Input");
            True(!newResult.Accepted && newResult.ExactPath is null, "suggested-name dialog cancellation");
            Equal(2, calls, "one invocation per owned dialog request");
            completed.SetResult();
        }
        catch (Exception exception) { completed.SetException(exception); }
        finally { owner?.Close(); }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    await completed.Task;
    thread.Join();
});

await suite.RunAsync("local NTFS policy rejects traversal ADS network device and relative paths", async () =>
{
    var registry = new PhotonCadWindowsTargetRegistry();
    ThrowsCode(() => registry.RegisterExactPath(Path.Combine(root, "nested", "..", "escape.photoncad")), "path_traversal_rejected");
    ThrowsCode(() => registry.RegisterExactPath(suite.PathFor("ads.photoncad") + ":secret"), "alternate_data_stream_rejected");
    ThrowsCode(() => registry.RegisterExactPath(@"\\server\share\project.photoncad"), "absolute_local_path_required");
    ThrowsCode(() => registry.RegisterExactPath(@"\\?\C:\project.photoncad"), "absolute_local_path_required");
    ThrowsCode(() => registry.RegisterExactPath("relative.photoncad"), "absolute_local_path_required");
    await Task.CompletedTask;
});

await suite.RunAsync("reparse points and hard links are rejected", async () =>
{
    var registry = new PhotonCadWindowsTargetRegistry();
    var source = suite.PathFor("link-source.photoncad");
    await File.WriteAllBytesAsync(source, Bytes("link-source"));
    var hardLink = suite.PathFor("hard-link.photoncad");
    NativeSmoke.CreateHardLink(hardLink, source);
    ThrowsCode(() => registry.RegisterExactPath(source), "hard_link_rejected");
    ThrowsCode(() => registry.RegisterExactPath(hardLink), "hard_link_rejected");

    var junctionTarget = suite.PathFor("junction-target");
    var junction = suite.PathFor("junction");
    Directory.CreateDirectory(junctionTarget);
    NativeSmoke.CreateJunction(junction, junctionTarget);
    ThrowsCode(() => registry.RegisterExactPath(Path.Combine(junction, "through-junction.photoncad")), "reparse_point_rejected");
    Directory.Delete(junction);
});

await suite.RunAsync("case-sensitive directory semantics are rejected", async () =>
{
    var directory = suite.PathFor("case-sensitive");
    Directory.CreateDirectory(directory);
    NativeSmoke.SetCaseSensitiveDirectory(directory, enabled: true);
    try
    {
        ThrowsCode(
            () => new PhotonCadWindowsTargetRegistry().RegisterExactPath(Path.Combine(directory, "Project.photoncad")),
            "case_sensitive_directory_rejected");
    }
    finally
    {
        NativeSmoke.SetCaseSensitiveDirectory(directory, enabled: false);
    }
    await Task.CompletedTask;
});

await suite.RunAsync("create read match-version and external edit are exact", async () =>
{
    var registry = new PhotonCadWindowsTargetRegistry();
    var binding = registry.RegisterExactPath(suite.PathFor("lifecycle.photoncad"));
    var backend = new PhotonCadWindowsAtomicStorageBackend(registry);
    var store = new PhotonCadAtomicProjectStore(backend);
    var first = await store.SaveAsync(binding.Target, Bytes("revision-0"));
    True(first.Atomic && first.FlushedToDisk, "durable create receipt");
    Equal("revision-0", Text((await store.ReadAsync(binding.Target)).Span), "read created content");
    await ThrowsCodeAsync(() => store.SaveAsync(binding.Target, Bytes("implicit overwrite")).AsTask(),
        "target_exists_overwrite_confirmation_required");

    var opened = await store.ReadVersionedAsync(binding.Target);
    var second = await store.SaveIfVersionAsync(binding.Target, Bytes("revision-1"), opened.Version);
    Equal("revision-1", Text((await store.ReadAsync(binding.Target)).Span), "match-version save");
    await ThrowsCodeAsync(() => store.SaveIfVersionAsync(binding.Target, Bytes("stale"), opened.Version).AsTask(),
        "storage_version_conflict");

    await File.WriteAllBytesAsync(suite.PathFor("lifecycle.photoncad"), Bytes("external-in-place-edit"));
    await ThrowsCodeAsync(() => store.SaveIfVersionAsync(binding.Target, Bytes("must-not-clobber"), second.Version).AsTask(),
        "storage_version_conflict");
    Equal("external-in-place-edit", await File.ReadAllTextAsync(suite.PathFor("lifecycle.photoncad")), "external edit preserved");

    var externalOpened = await store.ReadVersionedAsync(binding.Target);
    var replacement = suite.PathFor("replacement.tmp");
    await File.WriteAllBytesAsync(replacement, Bytes("external-in-place-edit"));
    File.Move(replacement, suite.PathFor("lifecycle.photoncad"), overwrite: true);
    await ThrowsCodeAsync(() => store.SaveIfVersionAsync(binding.Target, Bytes("stale-id"), externalOpened.Version).AsTask(),
        "storage_version_conflict");
});

await suite.RunAsync("host-confirmed overwrite grant is exact and single-use", async () =>
{
    var path = suite.PathFor("overwrite.photoncad");
    await File.WriteAllBytesAsync(path, Bytes("old"));
    var registry = new PhotonCadWindowsTargetRegistry();
    var target = registry.RegisterExactPath(path).Target;
    var store = new PhotonCadAtomicProjectStore(new PhotonCadWindowsAtomicStorageBackend(registry));
    var context = PhotonCadOverwriteGrantContext.ForCreate();
    var grant = await store.IssueOverwriteGrantAsync(target, context);
    await store.SaveWithOverwriteGrantAsync(target, Bytes("new"), grant, context);
    Equal("new", await File.ReadAllTextAsync(path), "confirmed overwrite committed");
    await ThrowsCodeAsync(() => store.SaveWithOverwriteGrantAsync(target, Bytes("replay"), grant, context).AsTask(),
        "overwrite_grant_unknown_or_used");
    Equal("new", await File.ReadAllTextAsync(path), "grant replay preserved target");
});

await suite.RunAsync("same version concurrent writers never both commit", async () =>
{
    var path = suite.PathFor("concurrency.photoncad");
    await File.WriteAllBytesAsync(path, Bytes("base"));
    var registry = new PhotonCadWindowsTargetRegistry();
    var target = registry.RegisterExactPath(path).Target;
    var store = new PhotonCadAtomicProjectStore(new PhotonCadWindowsAtomicStorageBackend(registry));
    var version = (await store.ReadVersionedAsync(target)).Version;
    using var start = new ManualResetEventSlim(false);
    var left = Task.Run(async () => { start.Wait(); return await CaptureAsync(() => store.SaveIfVersionAsync(target, Bytes("left"), version).AsTask()); });
    var right = Task.Run(async () => { start.Wait(); return await CaptureAsync(() => store.SaveIfVersionAsync(target, Bytes("right"), version).AsTask()); });
    start.Set();
    var outcomes = await Task.WhenAll(left, right);
    Equal(1, outcomes.Count(outcome => outcome is null), "one writer committed");
    Equal(1, outcomes.Count(outcome => outcome is PhotonCadProjectException), "one writer rejected");
    True((await File.ReadAllTextAsync(path)) is "left" or "right", "winner content exact");
});

await suite.RunAsync("prepared transaction resumes after backend restart", async () =>
{
    var registry = new PhotonCadWindowsTargetRegistry();
    var binding = registry.RegisterExactPath(suite.PathFor("recover-prepared.photoncad"));
    var firstBackend = new PhotonCadWindowsAtomicStorageBackend(registry);
    var before = await firstBackend.InspectExactAsync(binding.Target);
    _ = await firstBackend.PrepareSameVolumeAsync(binding.Target, before, Bytes("prepared-content"));

    var restartedRegistry = new PhotonCadWindowsTargetRegistry();
    _ = restartedRegistry.RegisterExactPath(suite.PathFor("recover-prepared.photoncad"));
    var recoveryStore = new PhotonCadAtomicProjectStore(new PhotonCadWindowsAtomicStorageBackend(restartedRegistry));
    var report = await recoveryStore.RecoverAsync();
    Equal(1, report.Discovered, "prepared discovered");
    Equal(1, report.Resumed, "prepared resumed");
    Equal("prepared-content", await File.ReadAllTextAsync(suite.PathFor("recover-prepared.photoncad")), "resumed content");
    Equal(0, (await recoveryStore.RecoverAsync()).Discovered, "recovery idempotent");
});

await suite.RunAsync("postcommit journal completes after backend restart", async () =>
{
    var registry = new PhotonCadWindowsTargetRegistry();
    var binding = registry.RegisterExactPath(suite.PathFor("recover-committed.photoncad"));
    var firstBackend = new PhotonCadWindowsAtomicStorageBackend(registry);
    var before = await firstBackend.InspectExactAsync(binding.Target);
    var prepared = await firstBackend.PrepareSameVolumeAsync(binding.Target, before, Bytes("committed-content"));
    _ = await firstBackend.CommitAtomicAsync(prepared, before, new CancellationToken(true));

    var restartedRegistry = new PhotonCadWindowsTargetRegistry();
    _ = restartedRegistry.RegisterExactPath(suite.PathFor("recover-committed.photoncad"));
    var recoveryStore = new PhotonCadAtomicProjectStore(new PhotonCadWindowsAtomicStorageBackend(restartedRegistry));
    var report = await recoveryStore.RecoverAsync();
    Equal(1, report.Discovered, "committed discovered");
    Equal(1, report.Completed, "committed completion");
    Equal("committed-content", await File.ReadAllTextAsync(suite.PathFor("recover-committed.photoncad")), "committed content");
});

await suite.RunAsync("recovery quarantines an externally modified target", async () =>
{
    var path = suite.PathFor("recover-conflict.photoncad");
    await File.WriteAllBytesAsync(path, Bytes("original"));
    var registry = new PhotonCadWindowsTargetRegistry();
    var binding = registry.RegisterExactPath(path);
    var firstBackend = new PhotonCadWindowsAtomicStorageBackend(registry);
    var before = await firstBackend.InspectExactAsync(binding.Target);
    _ = await firstBackend.PrepareSameVolumeAsync(binding.Target, before, Bytes("staged"));
    await File.WriteAllBytesAsync(path, Bytes("external"));

    var restartedRegistry = new PhotonCadWindowsTargetRegistry();
    _ = restartedRegistry.RegisterExactPath(path);
    var recoveryStore = new PhotonCadAtomicProjectStore(new PhotonCadWindowsAtomicStorageBackend(restartedRegistry));
    var report = await recoveryStore.RecoverAsync();
    Equal(1, report.Quarantined, "conflict quarantined");
    Equal("external", await File.ReadAllTextAsync(path), "external target preserved");
    True(Directory.EnumerateFiles(Path.Combine(root, ".photon-cad-quarantine")).Any(), "artifacts isolated");
});

await suite.RunAsync("host provider hydrates nonzero revision and accepts only exact-base updates", async () =>
{
    var path = suite.PathFor("provider.photoncad");
    var codec = new SmokeProjectCodec();
    await File.WriteAllBytesAsync(path, codec.Project(7, dirty: false).CanonicalBytes.ToArray());
    var interruptedRegistry = new PhotonCadWindowsTargetRegistry();
    var interruptedTarget = interruptedRegistry.RegisterExactPath(path).Target;
    var interruptedBackend = new PhotonCadWindowsAtomicStorageBackend(interruptedRegistry);
    var interruptedBefore = await interruptedBackend.InspectExactAsync(interruptedTarget);
    _ = await interruptedBackend.PrepareSameVolumeAsync(
        interruptedTarget,
        interruptedBefore,
        codec.Project(8, dirty: false).CanonicalBytes);
    await using var provider = new PhotonCadWindowsProjectProvider(codec, new FixedDialog(false, null));
    var binding = provider.RegisterHostPath(path, "Gearbox");
    var hydrated = await provider.HydrateAsync(binding);
    Equal(8L, hydrated.Document.Snapshot.Revision, "nonzero revision hydrated after recovery");
    Equal(hydrated.Document.BomDigest, hydrated.Document.Snapshot.BomDigest, "BOM digest carried");
    True(!hydrated.RevisionBinding.GetType().GetProperties().Any(property =>
            property.Name.Contains("Path", StringComparison.Ordinal)
            || property.Name.Contains("Version", StringComparison.Ordinal)
            || property.Name.Contains("Evidence", StringComparison.Ordinal)
            || property.Name.Contains("Grant", StringComparison.Ordinal)),
        "revision binding excludes native authority");

    var applied = await provider.ApplyRuntimeUpdateAsync(hydrated.RevisionBinding, codec.Project(9, dirty: true));
    Equal(9L, applied.Document.Snapshot.Revision, "runtime update advanced");
    await ThrowsCodeAsync(
        () => provider.ApplyRuntimeUpdateAsync(hydrated.RevisionBinding, codec.Project(10, dirty: true)).AsTask(),
        "runtime_update_base_conflict");
    var saved = await provider.SaveCurrentAsync(applied.RevisionBinding);
    Equal(9L, saved.Outcome.Document.Snapshot.Revision, "revision-bound save");
    True(!saved.Outcome.Document.Snapshot.Dirty, "saved document clean");
    Equal(9L, codec.Decode(await File.ReadAllBytesAsync(path)).Revision, "persisted nonzero update");
    await provider.DisposeAsync();
    True(!provider.Available, "provider unavailable after disposal");
    Equal(PhotonCadNativeServiceStatus.Unavailable, (await provider.Picker.ChooseAsync("open")).Status, "picker revoked");
    Equal(PhotonCadNativeServiceStatus.Unavailable, (await provider.Mount.MountAsync("cad-windows-selection:revoked")).Status, "mount revoked");
    await provider.DisposeAsync();
});

await suite.RunAsync("desktop runtime authority persists one exact revision-zero container mutation", async () =>
{
    var path = suite.PathFor("runtime-authority.photoncad");
    var codec = RuntimeSmoke.Codec("authority");
    await using var host = new PhotonCadWindowsDesktopProjectHost(codec, new FixedDialog(true, path));
    var selected = await host.ChooseWorkspaceAsync("runtime-select", "new");
    var created = await host.CreateProjectAsync(new PhotonCadProjectCreateRequest(
        "runtime-create",
        selected.Workspace!.WorkspaceHandle,
        "Runtime gearbox",
        PhotonCadProjectUnit.Millimeter));
    Equal(0L, created.Snapshot.Revision, "clean revision-zero base");

    var provider = new RuntimeMutationProvider(request => ValueTask.FromResult(RuntimeSmoke.BoxMutation(request)));
    var compensator = new RuntimeMutationCompensator();
    var synchronizer = host.CreateRuntimeProjectSynchronizer(
        provider,
        compensator,
        new PhotonCadRuntimeCanonicalMapperV1(codec));
    var request = RuntimeSmoke.Request(created, "runtime-mutate", "body-one");
    var result = await host.ApplyRuntimeMutationAsync(synchronizer, request);
    Equal(2L, result.SavedProject.Revision, "provider suffix revision persisted");
    True(!result.SavedProject.Dirty, "committed project is clean");
    Equal(1, provider.Calls, "provider called once");
    Equal(0, compensator.Calls, "successful commit not compensated");
    var disk = codec.Decode(await File.ReadAllBytesAsync(path));
    Equal(result.SavedProject.Revision, disk.Revision, "disk revision matches result");
    Equal(result.SavedProject.ContentDigest, disk.ContentDigest, "disk digest matches result");

    var second = RuntimeSmoke.Request(result.SavedProject, "runtime-second", "body-two");
    var secondResult = await host.ApplyRuntimeMutationAsync(synchronizer, second);
    Equal(4L, secondResult.SavedProject.Revision, "same-open attachment advanced to second suffix");
    Equal(2, provider.Calls, "provider called exactly twice");
    Equal(4L, codec.Decode(await File.ReadAllBytesAsync(path)).Revision, "second mutation persisted");
    var resolved = await host.ResolveCommittedProjectAsync(
        "runtime-verify-readback",
        result.SavedProject.SessionId,
        result.SavedProject.ProjectId,
        secondResult.SavedProject.Revision);
    Equal(secondResult.SavedProject.ContentDigest, resolved.ContentDigest, "committed resolver exact digest");
    Equal(4L, resolved.Revision, "committed resolver exact revision");
    await ThrowsCodeAsync(
        () => host.ResolveCommittedProjectAsync(
            "runtime-verify-stale",
            resolved.SessionId,
            resolved.ProjectId,
            2).AsTask(),
        "committed_project_binding_mismatch");
    await ThrowsCodeAsync(
        () => host.ApplyRuntimeMutationAsync(synchronizer, second).AsTask(),
        "runtime_project_revision_mismatch");
    Equal(2, provider.Calls, "stale revision rejected before provider");
});

await suite.RunAsync("desktop runtime authority rejects changed storage before provider", async () =>
{
    var path = suite.PathFor("runtime-tamper.photoncad");
    var codec = RuntimeSmoke.Codec("tamper");
    await using var host = new PhotonCadWindowsDesktopProjectHost(codec, new FixedDialog(true, path));
    var selected = await host.ChooseWorkspaceAsync("tamper-select", "new");
    var created = await host.CreateProjectAsync(new PhotonCadProjectCreateRequest(
        "tamper-create",
        selected.Workspace!.WorkspaceHandle,
        "Tamper check",
        PhotonCadProjectUnit.Millimeter));
    var provider = new RuntimeMutationProvider(request => ValueTask.FromResult(RuntimeSmoke.BoxMutation(request)));
    var synchronizer = host.CreateRuntimeProjectSynchronizer(
        provider,
        new RuntimeMutationCompensator(),
        new PhotonCadRuntimeCanonicalMapperV1(codec));
    await File.WriteAllBytesAsync(path, Bytes("foreign-content"));
    await ThrowsCodeAsync(
        () => host.ApplyRuntimeMutationAsync(synchronizer, RuntimeSmoke.Request(created, "tamper-mutate")).AsTask(),
        "runtime_storage_content_mismatch");
    Equal(0, provider.Calls, "storage mismatch rejected before provider");
    Equal("foreign-content", await File.ReadAllTextAsync(path), "foreign bytes untouched");
    var resolverFailure = await CaptureAsync(() => host.ResolveCommittedProjectAsync(
        "tamper-verify",
        created.Snapshot.SessionId,
        created.Snapshot.ProjectId,
        created.Snapshot.Revision).AsTask());
    True(resolverFailure is PhotonCadProjectException, "committed resolver rejected changed storage");
});

await suite.RunAsync("desktop reset cancels drains and revokes an active runtime operation", async () =>
{
    var path = suite.PathFor("runtime-reset.photoncad");
    var codec = RuntimeSmoke.Codec("reset");
    await using var host = new PhotonCadWindowsDesktopProjectHost(codec, new FixedDialog(true, path));
    var selected = await host.ChooseWorkspaceAsync("reset-select", "new");
    var created = await host.CreateProjectAsync(new PhotonCadProjectCreateRequest(
        "reset-create",
        selected.Workspace!.WorkspaceHandle,
        "Reset check",
        PhotonCadProjectUnit.Millimeter));
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var provider = new RuntimeMutationProvider(async (_, token) =>
    {
        entered.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        throw new InvalidOperationException("unreachable");
    });
    var synchronizer = host.CreateRuntimeProjectSynchronizer(
        provider,
        new RuntimeMutationCompensator(),
        new PhotonCadRuntimeCanonicalMapperV1(codec));
    var request = RuntimeSmoke.Request(created, "reset-mutate");
    var mutation = host.ApplyRuntimeMutationAsync(synchronizer, request).AsTask();
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var reset = host.ResetAsync().AsTask();
    await ThrowsCanceledAsync(() => mutation);
    await reset.WaitAsync(TimeSpan.FromSeconds(5));
    await ThrowsCodeAsync(
        () => host.ApplyRuntimeMutationAsync(synchronizer, request).AsTask(),
        "runtime_synchronizer_foreign_or_stale");
    True(!File.Exists(path) || codec.Decode(await File.ReadAllBytesAsync(path)).Revision == 0,
        "canceled provider did not alter canonical project");
});

await suite.RunAsync("only New establishes runtime eligibility and Open actively revokes it", async () =>
{
    var openedPath = suite.PathFor("runtime-opened-rev0.photoncad");
    var openedCodec = RuntimeSmoke.Codec("opened-rev0");
    var persisted = await openedCodec.CreateAsync("Opened revision zero", PhotonCadProjectUnit.Millimeter);
    await File.WriteAllBytesAsync(openedPath, persisted.CanonicalBytes.ToArray());
    await using (var openedHost = new PhotonCadWindowsDesktopProjectHost(openedCodec, new FixedDialog(true, openedPath)))
    {
        var selected = await openedHost.ChooseWorkspaceAsync("opened-select", "open");
        var opened = await openedHost.OpenProjectAsync(new PhotonCadProjectOpenRequest(
            "opened-project",
            selected.Workspace!.WorkspaceHandle));
        var provider = new RuntimeMutationProvider(request => ValueTask.FromResult(RuntimeSmoke.BoxMutation(request)));
        var registration = openedHost.CreateRuntimeProjectSynchronizer(
            provider,
            new RuntimeMutationCompensator(),
            new PhotonCadRuntimeCanonicalMapperV1(openedCodec));
        await ThrowsCodeAsync(
            () => openedHost.ApplyRuntimeMutationAsync(
                registration,
                RuntimeSmoke.Request(opened, "opened-mutate")).AsTask(),
            "runtime_project_attachment_unavailable");
        Equal(0, provider.Calls, "opened persisted revision zero rejected before provider");
    }

    var newPath = suite.PathFor("runtime-new-then-open.photoncad");
    var newCodec = RuntimeSmoke.Codec("new-then-open");
    await using var host = new PhotonCadWindowsDesktopProjectHost(newCodec, new FixedDialog(true, newPath));
    var newSelection = await host.ChooseWorkspaceAsync("new-open-select-new", "new");
    var created = await host.CreateProjectAsync(new PhotonCadProjectCreateRequest(
        "new-open-create",
        newSelection.Workspace!.WorkspaceHandle,
        "New then open",
        PhotonCadProjectUnit.Millimeter));
    var providerAfterNew = new RuntimeMutationProvider(request => ValueTask.FromResult(RuntimeSmoke.BoxMutation(request)));
    var newRegistration = host.CreateRuntimeProjectSynchronizer(
        providerAfterNew,
        new RuntimeMutationCompensator(),
        new PhotonCadRuntimeCanonicalMapperV1(newCodec));
    var openSelection = await host.ChooseWorkspaceAsync("new-open-select-open", "open");
    var existing = await host.OpenProjectAsync(new PhotonCadProjectOpenRequest(
        "new-open-existing",
        openSelection.Workspace!.WorkspaceHandle));
    Equal(created.ProjectHandle, existing.ProjectHandle, "same open document returned");
    await ThrowsCodeAsync(
        () => host.ApplyRuntimeMutationAsync(
            newRegistration,
            RuntimeSmoke.Request(existing, "new-open-mutate")).AsTask(),
        "runtime_project_attachment_unavailable");
    Equal(0, providerAfterNew.Calls, "Open revoked New eligibility before provider");
});

await suite.RunAsync("exact refresh preserves attachment while close and reopen revoke it", async () =>
{
    var path = suite.PathFor("runtime-lifecycle.photoncad");
    var codec = RuntimeSmoke.Codec("lifecycle");
    await using var host = new PhotonCadWindowsDesktopProjectHost(codec, new FixedDialog(true, path));
    var selected = await host.ChooseWorkspaceAsync("lifecycle-select", "new");
    var created = await host.CreateProjectAsync(new PhotonCadProjectCreateRequest(
        "lifecycle-create",
        selected.Workspace!.WorkspaceHandle,
        "Lifecycle",
        PhotonCadProjectUnit.Millimeter));
    var provider = new RuntimeMutationProvider(request => ValueTask.FromResult(RuntimeSmoke.BoxMutation(request)));
    var registration = host.CreateRuntimeProjectSynchronizer(
        provider,
        new RuntimeMutationCompensator(),
        new PhotonCadRuntimeCanonicalMapperV1(codec));

    var refreshed = await host.RefreshProjectAsync(new PhotonCadProjectRefreshRequest(
        "lifecycle-refresh",
        created.ProjectHandle,
        created.Snapshot.SessionId,
        created.Snapshot.ProjectId,
        created.Snapshot.Revision));
    var mutated = await host.ApplyRuntimeMutationAsync(
        registration,
        RuntimeSmoke.Request(refreshed, "lifecycle-after-refresh"));
    Equal(2L, mutated.SavedProject.Revision, "exact refresh retained runtime attachment");
    var committed = await host.RefreshProjectAsync(new PhotonCadProjectRefreshRequest(
        "lifecycle-refresh-committed",
        refreshed.ProjectHandle,
        refreshed.Snapshot.SessionId,
        refreshed.Snapshot.ProjectId,
        mutated.SavedProject.Revision));

    var closed = await host.CloseProjectAsync(new PhotonCadProjectCloseRequest(
        "lifecycle-close",
        committed.ProjectHandle,
        committed.Snapshot.SessionId,
        committed.Snapshot.ProjectId,
        committed.Snapshot.Revision,
        committed.LastSavedRevision,
        committed.ContentDigest,
        committed.LastSavedContentDigest,
        false));
    await ThrowsCodeAsync(
        () => host.ApplyRuntimeMutationAsync(
            registration,
            RuntimeSmoke.Request(committed, "lifecycle-after-close")).AsTask(),
        "runtime_project_binding_unknown");
    var reopened = await host.ReopenProjectAsync(new PhotonCadProjectReopenRequest(
        "lifecycle-reopen",
        closed.Reopen.ReopenHandle));
    var reopenedResolved = await host.ResolveCommittedProjectAsync(
        "lifecycle-verify-reopened",
        reopened.Snapshot.SessionId,
        reopened.Snapshot.ProjectId,
        reopened.Snapshot.Revision);
    Equal(reopened.Snapshot.ContentDigest, reopenedResolved.ContentDigest, "reopened committed readback");
    await ThrowsCodeAsync(
        () => host.ApplyRuntimeMutationAsync(
            registration,
            RuntimeSmoke.Request(reopened, "lifecycle-after-reopen")).AsTask(),
        "runtime_project_attachment_unavailable");
    Equal(1, provider.Calls, "close and reopen revocations precede any later provider call");
});

await suite.RunAsync("two New projects retain independent exact runtime attachments", async () =>
{
    var pathA = suite.PathFor("runtime-multi-a.photoncad");
    var pathB = suite.PathFor("runtime-multi-b.photoncad");
    var codec = new PhotonCadCanonicalProjectCodecV1(new SequentialProjectIdentityIssuer("multi-attach"));
    await using var host = new PhotonCadWindowsDesktopProjectHost(codec, new QueueDialog(pathA, pathB));

    async Task<PhotonCadProjectDocument> CreateAsync(string suffix)
    {
        var selected = await host.ChooseWorkspaceAsync($"multi-select-{suffix}", "new");
        return await host.CreateProjectAsync(new PhotonCadProjectCreateRequest(
            $"multi-create-{suffix}", selected.Workspace!.WorkspaceHandle, $"Project {suffix}", PhotonCadProjectUnit.Millimeter));
    }

    var projectA = await CreateAsync("a");
    var projectB = await CreateAsync("b");
    True(projectA.ProjectHandle != projectB.ProjectHandle
        && !StringComparer.Ordinal.Equals(projectA.Snapshot.ProjectId, projectB.Snapshot.ProjectId),
        "two New projects have independent durable identities");
    var provider = new RuntimeMutationProvider(request => ValueTask.FromResult(RuntimeSmoke.BoxMutation(request)));
    var registration = host.CreateRuntimeProjectSynchronizer(
        provider,
        new RuntimeMutationCompensator(),
        new PhotonCadRuntimeCanonicalMapperV1(codec));

    var a2 = await host.ApplyRuntimeMutationAsync(registration, RuntimeSmoke.Request(projectA, "multi-a-0-to-2", "part-a"));
    var b2 = await host.ApplyRuntimeMutationAsync(registration, RuntimeSmoke.Request(projectB, "multi-b-0-to-2", "part-b"));
    Equal(2L, a2.SavedProject.Revision, "project A exact first revision");
    Equal(2L, b2.SavedProject.Revision, "project B exact first revision");
    var refreshedA = await host.RefreshProjectAsync(new PhotonCadProjectRefreshRequest(
        "multi-refresh-a", projectA.ProjectHandle, projectA.Snapshot.SessionId, projectA.Snapshot.ProjectId, 2));
    var refreshedB = await host.RefreshProjectAsync(new PhotonCadProjectRefreshRequest(
        "multi-refresh-b", projectB.ProjectHandle, projectB.Snapshot.SessionId, projectB.Snapshot.ProjectId, 2));
    var a4 = await host.ApplyRuntimeMutationAsync(registration, RuntimeSmoke.Request(refreshedA, "multi-a-return", "part-a-2"));
    Equal(4L, a4.SavedProject.Revision, "returning to A retained only A attachment");
    Equal(2L, codec.Decode(await File.ReadAllBytesAsync(pathB)).Revision, "project B remained exact revision two");
    Equal(refreshedB.Snapshot.ContentDigest, codec.Decode(await File.ReadAllBytesAsync(pathB)).ContentDigest,
        "project B bytes remained unchanged while mutating A");
    Equal(3, provider.Calls, "no cross-project retry or mutation");
});

await suite.RunAsync("foreign generation revision and inch requests fail before provider", async () =>
{
    var path = suite.PathFor("runtime-foreign.photoncad");
    var codec = RuntimeSmoke.Codec("foreign");
    await using var host = new PhotonCadWindowsDesktopProjectHost(codec, new FixedDialog(true, path));
    var selected = await host.ChooseWorkspaceAsync("foreign-select", "new");
    var created = await host.CreateProjectAsync(new PhotonCadProjectCreateRequest(
        "foreign-create",
        selected.Workspace!.WorkspaceHandle,
        "Foreign",
        PhotonCadProjectUnit.Millimeter));
    var foreignProvider = new RuntimeMutationProvider(request => ValueTask.FromResult(RuntimeSmoke.BoxMutation(request)));
    await using var foreignHost = new PhotonCadWindowsDesktopProjectHost(
        RuntimeSmoke.Codec("foreign-host"),
        new FixedDialog(false, null));
    var foreignRegistration = foreignHost.CreateRuntimeProjectSynchronizer(
        foreignProvider,
        new RuntimeMutationCompensator(),
        new PhotonCadRuntimeCanonicalMapperV1(codec));
    await ThrowsCodeAsync(
        () => host.ApplyRuntimeMutationAsync(
            foreignRegistration,
            RuntimeSmoke.Request(created, "foreign-registration")).AsTask(),
        "runtime_synchronizer_foreign_or_stale");
    Equal(0, foreignProvider.Calls, "foreign synchronizer rejected before provider");

    var ownProvider = new RuntimeMutationProvider(request => ValueTask.FromResult(RuntimeSmoke.BoxMutation(request)));
    var ownRegistration = host.CreateRuntimeProjectSynchronizer(
        ownProvider,
        new RuntimeMutationCompensator(),
        new PhotonCadRuntimeCanonicalMapperV1(codec));
    var wrongRevision = new PhotonCadRuntimeSyncRequest(
        "wrong-revision",
        created.Snapshot.SessionId,
        created.Snapshot.ProjectId,
        1,
        "geometry.create.box",
        PhotonCadOperationModeV1.Suggest,
        targetEntityIds: ["body-one"]);
    await ThrowsCodeAsync(
        () => host.ApplyRuntimeMutationAsync(ownRegistration, wrongRevision).AsTask(),
        "runtime_project_revision_mismatch");
    Equal(0, ownProvider.Calls, "revision mismatch rejected before provider");

    var inchPath = suite.PathFor("runtime-inch.photoncad");
    var inchCodec = RuntimeSmoke.Codec("inch");
    await using var inchHost = new PhotonCadWindowsDesktopProjectHost(inchCodec, new FixedDialog(true, inchPath));
    var inchSelection = await inchHost.ChooseWorkspaceAsync("inch-select", "new");
    var inch = await inchHost.CreateProjectAsync(new PhotonCadProjectCreateRequest(
        "inch-create",
        inchSelection.Workspace!.WorkspaceHandle,
        "Inch project",
        PhotonCadProjectUnit.Inch));
    var inchProvider = new RuntimeMutationProvider(request => ValueTask.FromResult(RuntimeSmoke.BoxMutation(request)));
    var inchRegistration = inchHost.CreateRuntimeProjectSynchronizer(
        inchProvider,
        new RuntimeMutationCompensator(),
        new PhotonCadRuntimeCanonicalMapperV1(inchCodec));
    await ThrowsCodeAsync(
        () => inchHost.ApplyRuntimeMutationAsync(
            inchRegistration,
            RuntimeSmoke.Request(inch, "inch-mutate")).AsTask(),
        "runtime_project_attachment_unavailable");
    Equal(0, inchProvider.Calls, "inch project rejected before provider");
});

await suite.RunAsync("reset ignores caller cancellation after swap and poisons on revoker failure", async () =>
{
    var cancelRevoker = new ControlledReferenceRevoker();
    await using (var host = new PhotonCadWindowsDesktopProjectHost(
        RuntimeSmoke.Codec("reset-caller-cancel"),
        new FixedDialog(false, null),
        references: cancelRevoker))
    {
        using var caller = new CancellationTokenSource();
        var reset = host.ResetAsync(caller.Token).AsTask();
        await cancelRevoker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();
        cancelRevoker.Release.TrySetResult();
        await reset.WaitAsync(TimeSpan.FromSeconds(5));
        Equal(1, cancelRevoker.Calls, "post-swap revoke called exactly once");
        True(host.Readiness.Available, "caller cancellation after swap does not poison successful reset");
    }

    var failingRevoker = new ControlledReferenceRevoker { Failure = new InvalidOperationException("revoker failed") };
    var failedHost = new PhotonCadWindowsDesktopProjectHost(
        RuntimeSmoke.Codec("reset-revoker-fail"),
        new FixedDialog(false, null),
        references: failingRevoker);
    failingRevoker.Release.TrySetResult();
    await ThrowsAsync<InvalidOperationException>(() => failedHost.ResetAsync().AsTask());
    Equal(1, failingRevoker.Calls, "failed revoker called exactly once");
    True(!failedHost.Readiness.Available, "revoker failure leaves host unavailable");
    await ThrowsAsync<InvalidOperationException>(() => failedHost.DisposeAsync().AsTask());
    True(!failedHost.Readiness.Available, "failed final revocation still leaves host closed");

    var timeoutRevoker = new ControlledReferenceRevoker();
    var timeoutHost = new PhotonCadWindowsDesktopProjectHost(
        RuntimeSmoke.Codec("reset-revoker-timeout"),
        new FixedDialog(false, null),
        references: timeoutRevoker,
        cleanupTimeout: TimeSpan.FromMilliseconds(100));
    var timeoutReset = CaptureAsync(() => timeoutHost.ResetAsync().AsTask());
    await timeoutRevoker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var timeoutFailure = await timeoutReset;
    True(timeoutFailure is TimeoutException or OperationCanceledException,
        "bounded reset exposes timeout/cancellation failure");
    True(!timeoutHost.Readiness.Available, "revoker timeout leaves host unavailable");
    _ = await CaptureAsync(() => timeoutHost.DisposeAsync().AsTask());
});

await suite.RunAsync("active dispose cancels drains and closes runtime authority", async () =>
{
    var path = suite.PathFor("runtime-dispose.photoncad");
    var codec = RuntimeSmoke.Codec("dispose");
    var host = new PhotonCadWindowsDesktopProjectHost(codec, new FixedDialog(true, path));
    var selected = await host.ChooseWorkspaceAsync("dispose-select", "new");
    var created = await host.CreateProjectAsync(new PhotonCadProjectCreateRequest(
        "dispose-create",
        selected.Workspace!.WorkspaceHandle,
        "Dispose",
        PhotonCadProjectUnit.Millimeter));
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var provider = new RuntimeMutationProvider(async (_, token) =>
    {
        entered.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        throw new InvalidOperationException("unreachable");
    });
    var registration = host.CreateRuntimeProjectSynchronizer(
        provider,
        new RuntimeMutationCompensator(),
        new PhotonCadRuntimeCanonicalMapperV1(codec));
    var request = RuntimeSmoke.Request(created, "dispose-mutate");
    var mutation = host.ApplyRuntimeMutationAsync(registration, request).AsTask();
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var disposal = host.DisposeAsync().AsTask();
    await ThrowsCanceledAsync(() => mutation);
    await disposal.WaitAsync(TimeSpan.FromSeconds(5));
    True(!host.Readiness.Available, "disposed host unavailable");
    await ThrowsAsync<ObjectDisposedException>(
        () => host.ApplyRuntimeMutationAsync(registration, request).AsTask());
});

await suite.RunAsync("runtime public shape excludes native authority and storage identity", async () =>
{
    var registrationType = typeof(PhotonCadWindowsRuntimeSynchronizerRegistration);
    True(!registrationType.GetProperties().Any(), "opaque registration has no public properties");
    var forbidden = new[] { "Path", "Grant", "StorageVersion", "Target", "Evidence", "Process", "Docker", "Python" };
    var exposed = typeof(PhotonCadWindowsDesktopProjectHost).GetMethods()
        .Where(method => method.Name.Contains("Runtime", StringComparison.Ordinal))
        .SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType.Name)
            .Append(method.ReturnType.Name))
        .Any(name => forbidden.Any(term => name.Contains(term, StringComparison.OrdinalIgnoreCase)));
    True(!exposed, "runtime host methods expose no native authority types");
    await Task.CompletedTask;
});

await suite.RunAsync("dirty coordinator state fails before persistence and compensates provider", async () =>
{
    var path = suite.PathFor("runtime-dirty.photoncad");
    var codec = RuntimeSmoke.Codec("dirty");
    var factory = new CapturingCompositionFactory(codec, new FixedDialog(true, path));
    await using var host = new PhotonCadWindowsDesktopProjectHost(factory);
    var selected = await host.ChooseWorkspaceAsync("dirty-select", "new");
    var created = await host.CreateProjectAsync(new PhotonCadProjectCreateRequest(
        "dirty-create",
        selected.Workspace!.WorkspaceHandle,
        "Dirty",
        PhotonCadProjectUnit.Millimeter));
    var mapper = new PhotonCadRuntimeCanonicalMapperV1(codec);
    var request = RuntimeSmoke.Request(created, "dirty-mutate");
    var dirty = RuntimeSmoke.DirtyMutation(mapper, created, request);
    _ = await factory.Coordinator.ApplyCurrentProjectAsync(created.ProjectHandle, dirty);

    var provider = new RuntimeMutationProvider(value => ValueTask.FromResult(RuntimeSmoke.BoxMutation(value)));
    var compensator = new RuntimeMutationCompensator();
    var registration = host.CreateRuntimeProjectSynchronizer(provider, compensator, mapper);
    await ThrowsCodeAsync(
        () => host.ApplyRuntimeMutationAsync(registration, request).AsTask(),
        "save_binding_mismatch");
    Equal(1, provider.Calls, "dirty coordinator conflict occurs at authoritative commit");
    Equal(1, compensator.Calls, "dirty commit failure compensates provider");
    Equal(0L, codec.Decode(await File.ReadAllBytesAsync(path)).Revision, "dirty conflict does not persist");
});

await suite.RunAsync("committed readback and recovery ambiguity never report accepted", async () =>
{
    var readbackPath = suite.PathFor("runtime-readback-ambiguity.photoncad");
    var readbackCodec = RuntimeSmoke.Codec("readback-ambiguity");
    var failingCodec = new SwitchableDecodeFailCodec(readbackCodec);
    var readbackFactory = new CapturingCompositionFactory(failingCodec, new FixedDialog(true, readbackPath));
    await using (var host = new PhotonCadWindowsDesktopProjectHost(readbackFactory))
    {
        var selected = await host.ChooseWorkspaceAsync("readback-select", "new");
        var created = await host.CreateProjectAsync(new PhotonCadProjectCreateRequest(
            "readback-create",
            selected.Workspace!.WorkspaceHandle,
            "Readback ambiguity",
            PhotonCadProjectUnit.Millimeter));
        var provider = new RuntimeMutationProvider(value => ValueTask.FromResult(RuntimeSmoke.BoxMutation(value)));
        var compensator = new RuntimeMutationCompensator();
        var registration = host.CreateRuntimeProjectSynchronizer(
            provider,
            compensator,
            new PhotonCadRuntimeCanonicalMapperV1(readbackCodec));
        failingCodec.FailDecode = true;
        await ThrowsCodeAsync(
            () => host.ApplyRuntimeMutationAsync(
                registration,
                RuntimeSmoke.Request(created, "readback-mutate")).AsTask(),
            "runtime_commit_readback_required");
        Equal(1, provider.Calls, "readback ambiguity provider call");
        Equal(1, compensator.Calls, "readback ambiguity compensated");
        Equal(2L, readbackCodec.Decode(await File.ReadAllBytesAsync(readbackPath)).Revision,
            "readback ambiguity remains truthfully committed on disk");
    }

    var recoveryDirectory = suite.PathFor("runtime-recovery-ambiguity");
    Directory.CreateDirectory(recoveryDirectory);
    var recoveryPath = Path.Combine(recoveryDirectory, "project.photoncad");
    var recoveryCodec = RuntimeSmoke.Codec("recovery-ambiguity");
    var recoveryFactory = new CapturingCompositionFactory(recoveryCodec, new FixedDialog(true, recoveryPath));
    await using (var host = new PhotonCadWindowsDesktopProjectHost(recoveryFactory))
    {
        var selected = await host.ChooseWorkspaceAsync("recovery-select", "new");
        var created = await host.CreateProjectAsync(new PhotonCadProjectCreateRequest(
            "recovery-create",
            selected.Workspace!.WorkspaceHandle,
            "Recovery ambiguity",
            PhotonCadProjectUnit.Millimeter));
        var provider = new RuntimeMutationProvider(value => ValueTask.FromResult(RuntimeSmoke.BoxMutation(value)));
        var compensator = new RuntimeMutationCompensator();
        var registration = host.CreateRuntimeProjectSynchronizer(
            provider,
            compensator,
            new PhotonCadRuntimeCanonicalMapperV1(recoveryCodec));
        recoveryFactory.Backend.FailNextPostCommitRead = true;
        await ThrowsCodeAsync(
            () => host.ApplyRuntimeMutationAsync(
                registration,
                RuntimeSmoke.Request(created, "recovery-mutate")).AsTask(),
            "runtime_commit_recovery_required");
        Equal(1, provider.Calls, "recovery ambiguity provider call");
        Equal(1, compensator.Calls, "recovery ambiguity compensated");
        Equal(2L, recoveryCodec.Decode(await File.ReadAllBytesAsync(recoveryPath)).Revision,
            "recovery ambiguity crossed durable commit without false acceptance");
    }
});

await suite.RunAsync("declined SaveAs and failed Close preserve exact runtime eligibility", async () =>
{
    var sourcePath = suite.PathFor("runtime-preserve-source.photoncad");
    var destinationPath = suite.PathFor("runtime-preserve-destination.photoncad");
    await File.WriteAllBytesAsync(destinationPath, Bytes("existing-customer-file"));
    var codec = RuntimeSmoke.Codec("preserve-noop");
    var closeRevoker = new ProjectFailingReferenceRevoker();
    await using var host = new PhotonCadWindowsDesktopProjectHost(
        codec,
        new QueueDialog(sourcePath, destinationPath),
        overwrite: new DecliningOverwriteAuthority(),
        references: closeRevoker);
    var selected = await host.ChooseWorkspaceAsync("preserve-select-new", "new");
    var created = await host.CreateProjectAsync(new PhotonCadProjectCreateRequest(
        "preserve-create",
        selected.Workspace!.WorkspaceHandle,
        "Preserve no-op",
        PhotonCadProjectUnit.Millimeter));
    var provider = new RuntimeMutationProvider(value => ValueTask.FromResult(RuntimeSmoke.BoxMutation(value)));
    var registration = host.CreateRuntimeProjectSynchronizer(
        provider,
        new RuntimeMutationCompensator(),
        new PhotonCadRuntimeCanonicalMapperV1(codec));

    var saveAsSelection = await host.ChooseWorkspaceAsync("preserve-select-save-as", "save-as");
    await ThrowsCodeAsync(
        () => host.SaveProjectAsAsync(new PhotonCadProjectSaveAsRequest(
            "preserve-save-as",
            created.ProjectHandle,
            saveAsSelection.Workspace!.WorkspaceHandle,
            created.Snapshot.SessionId,
            created.Snapshot.ProjectId,
            created.Snapshot.Revision,
            created.ContentDigest)).AsTask(),
        "overwrite_confirmation_declined");
    Equal("existing-customer-file", await File.ReadAllTextAsync(destinationPath), "declined destination untouched");

    await ThrowsAsync<InvalidOperationException>(() => host.CloseProjectAsync(new PhotonCadProjectCloseRequest(
        "preserve-close",
        created.ProjectHandle,
        created.Snapshot.SessionId,
        created.Snapshot.ProjectId,
        created.Snapshot.Revision,
        created.LastSavedRevision,
        created.ContentDigest,
        created.LastSavedContentDigest,
        false)).AsTask());
    var result = await host.ApplyRuntimeMutationAsync(
        registration,
        RuntimeSmoke.Request(created, "preserve-mutate"));
    Equal(2L, result.SavedProject.Revision, "precommit no-ops preserve runtime eligibility");
    Equal(1, provider.Calls, "provider invoked only for final mutation");
});

await suite.RunAsync("repeated versioned saves remain bounded and exact", async () =>
{
    var registry = new PhotonCadWindowsTargetRegistry();
    var target = registry.RegisterExactPath(suite.PathFor("repeat.photoncad")).Target;
    var store = new PhotonCadAtomicProjectStore(new PhotonCadWindowsAtomicStorageBackend(registry));
    var saved = await store.SaveAsync(target, Bytes("v0"));
    for (var index = 1; index <= 20; index++)
        saved = await store.SaveIfVersionAsync(target, Bytes($"v{index}"), saved.Version);
    Equal("v20", Text((await store.ReadAsync(target)).Span), "final repeated content");
    True(!Directory.EnumerateFiles(root, ".photon-cad-txn-*", SearchOption.TopDirectoryOnly).Any(), "no transaction debris");
});

var exit = await suite.CompleteAsync();
return exit;

static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
static string Text(ReadOnlySpan<byte> value) => Encoding.UTF8.GetString(value);

static async Task<Exception?> CaptureAsync(Func<Task> action)
{
    try { await action(); return null; }
    catch (Exception exception) { return exception; }
}

static void ThrowsCode(Action action, string code)
{
    try { action(); }
    catch (PhotonCadProjectException exception) when (exception.Code == code) { return; }
    throw new InvalidOperationException($"Expected PhotonCadProjectException '{code}'.");
}

static async Task ThrowsCodeAsync(Func<Task> action, string code)
{
    try { await action(); }
    catch (PhotonCadProjectException exception) when (exception.Code == code) { return; }
    throw new InvalidOperationException($"Expected PhotonCadProjectException '{code}'.");
}

static async Task ThrowsCanceledAsync(Func<Task> action)
{
    try { await action(); }
    catch (OperationCanceledException) { return; }
    throw new InvalidOperationException("Expected cancellation.");
}

static async Task ThrowsAsync<TException>(Func<Task> action) where TException : Exception
{
    try { await action(); }
    catch (TException) { return; }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static void True(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

static void Equal<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{message}: expected '{expected}', got '{actual}'.");
}

sealed class FixedDialog(bool accepted, string? path) : IPhotonCadWindowsFileDialog
{
    public PhotonCadWindowsDialogResult Show(string purpose) => new(accepted, path);
}

sealed class QueueDialog(params string[] paths) : IPhotonCadWindowsFileDialog
{
    private readonly Queue<string> _paths = new(paths);
    public PhotonCadWindowsDialogResult Show(string purpose) =>
        _paths.TryDequeue(out var path) ? new PhotonCadWindowsDialogResult(true, path) : new PhotonCadWindowsDialogResult(false, null);
}

sealed class DecliningOverwriteAuthority : IPhotonCadWindowsOverwriteAuthority
{
    public ValueTask<bool> ConfirmOverwriteAsync(
        PhotonCadWindowsOverwriteContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(false);
    }
}

sealed class ProjectFailingReferenceRevoker : IPhotonCadDesktopProjectReferenceRevoker
{
    public ValueTask RevokeProjectAsync(
        PhotonCadProjectHandle projectHandle,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException(new InvalidOperationException("Injected project revocation failure."));

    public ValueTask RevokeAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}

sealed class SmokeProjectCodec : IPhotonCadProjectCodec
{
    private const string SessionId = "session:windows-smoke";
    private const string ProjectId = "project:windows-smoke";

    public ValueTask<PhotonCadCanonicalProject> CreateAsync(string title, PhotonCadProjectUnit units, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Project(0, dirty: false, title, units));
    }

    public PhotonCadCanonicalProject Decode(ReadOnlyMemory<byte> canonicalBytes)
    {
        var fields = Encoding.UTF8.GetString(canonicalBytes.Span).Split('|');
        if (fields.Length != 5
            || !long.TryParse(fields[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var revision)
            || !Enum.TryParse<PhotonCadProjectUnit>(fields[4], ignoreCase: false, out var units))
            throw new PhotonCadProjectException("smoke_codec_invalid", nameof(canonicalBytes));
        return Create(fields[0], fields[1], revision, fields[3], units, dirty: false, canonicalBytes);
    }

    public PhotonCadCanonicalProject MarkSaved(PhotonCadCanonicalProject current) => Create(
        current.SessionId,
        current.ProjectId,
        current.Revision,
        current.DisplayName,
        current.Units,
        dirty: false,
        current.CanonicalBytes);

    public string ComputeLogicalContentDigest(ReadOnlyMemory<byte> canonicalBytes) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(canonicalBytes.Span))}";

    public PhotonCadCanonicalProject Project(
        long revision,
        bool dirty,
        string title = "Gearbox",
        PhotonCadProjectUnit units = PhotonCadProjectUnit.Millimeter)
    {
        var bytes = Encoding.UTF8.GetBytes($"{SessionId}|{ProjectId}|{revision.ToString(System.Globalization.CultureInfo.InvariantCulture)}|{title}|{units}");
        return Create(SessionId, ProjectId, revision, title, units, dirty, bytes);
    }

    private PhotonCadCanonicalProject Create(
        string sessionId,
        string projectId,
        long revision,
        string title,
        PhotonCadProjectUnit units,
        bool dirty,
        ReadOnlyMemory<byte> bytes) => new(
            sessionId,
            projectId,
            revision,
            title,
            units,
            ComputeLogicalContentDigest(bytes),
            PhotonCadBomCanonicalizer.Compute(units, []),
            dirty,
            bytes);
}

sealed class RuntimeMutationProvider : IPhotonCadSealedMutationProvider
{
    private readonly Func<PhotonCadSealedMutationProviderRequest, CancellationToken, ValueTask<PhotonCadSealedMutationDelta>> _apply;
    private int _calls;

    public RuntimeMutationProvider(Func<PhotonCadSealedMutationProviderRequest, ValueTask<PhotonCadSealedMutationDelta>> apply)
        : this((request, _) => apply(request))
    {
    }

    public RuntimeMutationProvider(
        Func<PhotonCadSealedMutationProviderRequest, CancellationToken, ValueTask<PhotonCadSealedMutationDelta>> apply) =>
        _apply = apply;

    public int Calls => Volatile.Read(ref _calls);

    public ValueTask<PhotonCadSealedMutationDelta> ApplyAsync(
        PhotonCadSealedMutationProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        return _apply(request, cancellationToken);
    }
}

sealed class RuntimeMutationCompensator : IPhotonCadSealedMutationCompensator
{
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);

    public ValueTask CompensateAsync(
        PhotonCadSealedMutationDelta mutation,
        string reason,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _calls);
        return ValueTask.CompletedTask;
    }
}

sealed class ControlledReferenceRevoker : IPhotonCadDesktopProjectReferenceRevoker
{
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);
    public Exception? Failure { get; init; }
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask RevokeProjectAsync(
        PhotonCadProjectHandle projectHandle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectHandle);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public async ValueTask RevokeAllAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        Entered.TrySetResult();
        await Release.Task.WaitAsync(cancellationToken);
        if (Failure is not null) throw Failure;
    }
}

sealed class FixedProjectIdentityIssuer(string suffix) : IPhotonCadProjectIdentityIssuerV1
{
    public (string SessionId, string ProjectId) NewIdentity() => ($"pcsid:{suffix}", $"pcpid:{suffix}");
}

sealed class SequentialProjectIdentityIssuer(string suffix) : IPhotonCadProjectIdentityIssuerV1
{
    private int _sequence;

    public (string SessionId, string ProjectId) NewIdentity()
    {
        var sequence = Interlocked.Increment(ref _sequence);
        return ($"pcsid:{suffix}-{sequence}", $"pcpid:{suffix}-{sequence}");
    }
}

sealed class SwitchableDecodeFailCodec(PhotonCadCanonicalProjectCodecV1 inner)
    : IPhotonCadProjectCodec, IPhotonCadProjectCodecPolicy
{
    public bool FailDecode { get; set; }
    public int MaximumEncodedBytes => inner.MaximumEncodedBytes;
    public ValueTask<PhotonCadCanonicalProject> CreateAsync(
        string title,
        PhotonCadProjectUnit units,
        CancellationToken cancellationToken = default) => inner.CreateAsync(title, units, cancellationToken);
    public PhotonCadCanonicalProject Decode(ReadOnlyMemory<byte> canonicalBytes) =>
        FailDecode ? throw new PhotonCadProjectException("injected_decode_failure", nameof(canonicalBytes)) : inner.Decode(canonicalBytes);
    public PhotonCadCanonicalProject MarkSaved(PhotonCadCanonicalProject current) => inner.MarkSaved(current);
    public string ComputeLogicalContentDigest(ReadOnlyMemory<byte> canonicalBytes) => inner.ComputeLogicalContentDigest(canonicalBytes);
}

sealed class CapturingCompositionFactory(
    IPhotonCadProjectCodec codec,
    IPhotonCadWindowsFileDialog dialog) : IPhotonCadWindowsDesktopProjectCompositionFactory
{
    public PhotonCadProjectCoordinator Coordinator { get; private set; } = null!;
    public FaultInjectingStorageBackend Backend { get; private set; } = null!;

    public PhotonCadWindowsDesktopProjectComposition Create()
    {
        var registry = new PhotonCadWindowsTargetRegistry();
        try
        {
            Backend = new FaultInjectingStorageBackend(new PhotonCadWindowsAtomicStorageBackend(registry));
            var maximum = codec is IPhotonCadProjectCodecPolicy policy
                ? policy.MaximumEncodedBytes
                : PhotonCadProjectContract.MaximumCanonicalProjectBytes;
            var store = new PhotonCadAtomicProjectStore(Backend, maximumEncodedBytes: maximum);
            Coordinator = new PhotonCadProjectCoordinator(store, codec);
            return new PhotonCadWindowsDesktopProjectComposition(
                Coordinator,
                store,
                Backend,
                new PhotonCadWindowsWorkspacePicker(registry, dialog),
                registry.Available,
                registry);
        }
        catch
        {
            registry.Dispose();
            throw;
        }
    }
}

sealed class FaultInjectingStorageBackend(IPhotonCadAtomicStorageBackend inner) : IPhotonCadAtomicStorageBackend
{
    private int _failPostCommitRead;
    private int _commitObserved;

    public bool FailNextPostCommitRead
    {
        set
        {
            Interlocked.Exchange(ref _commitObserved, 0);
            Interlocked.Exchange(ref _failPostCommitRead, value ? 1 : 0);
        }
    }

    public ValueTask<PhotonCadPathEvidence> InspectExactAsync(
        PhotonCadStorageTargetHandle target,
        CancellationToken cancellationToken = default) => inner.InspectExactAsync(target, cancellationToken);

    public async ValueTask<PhotonCadDurableRead> ReadExactAsync(
        PhotonCadStorageTargetHandle target,
        PhotonCadPathEvidence expected,
        int maximumBytes,
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _commitObserved) != 0
            && Interlocked.Exchange(ref _failPostCommitRead, 0) != 0)
            throw new IOException("Injected post-commit read failure.");
        return await inner.ReadExactAsync(target, expected, maximumBytes, cancellationToken);
    }

    public ValueTask<PhotonCadPreparedWrite> PrepareSameVolumeAsync(
        PhotonCadStorageTargetHandle target,
        PhotonCadPathEvidence expectedTarget,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default) =>
        inner.PrepareSameVolumeAsync(target, expectedTarget, bytes, cancellationToken);

    public async ValueTask<PhotonCadCommitProof> CommitAtomicAsync(
        PhotonCadPreparedWrite prepared,
        PhotonCadPathEvidence expectedTarget,
        CancellationToken cancellationToken = default)
    {
        var committed = await inner.CommitAtomicAsync(prepared, expectedTarget, cancellationToken);
        if (Volatile.Read(ref _failPostCommitRead) != 0) Interlocked.Exchange(ref _commitObserved, 1);
        return committed;
    }

    public ValueTask CompleteTransactionAsync(
        PhotonCadPreparedWrite prepared,
        CancellationToken cancellationToken = default) => inner.CompleteTransactionAsync(prepared, cancellationToken);

    public ValueTask<IReadOnlyList<PhotonCadRecoveryCandidate>> ListRecoveryCandidatesAsync(
        CancellationToken cancellationToken = default) => inner.ListRecoveryCandidatesAsync(cancellationToken);

    public ValueTask<PhotonCadQuarantineOutcome> QuarantineAsync(
        PhotonCadPreparedWrite prepared,
        string reason,
        CancellationToken cancellationToken = default) => inner.QuarantineAsync(prepared, reason, cancellationToken);
}

static class RuntimeSmoke
{
    internal static PhotonCadCanonicalProjectCodecV1 Codec(string suffix) =>
        new(new FixedProjectIdentityIssuer(suffix));

    internal static PhotonCadRuntimeSyncRequest Request(
        PhotonCadProjectDocument document,
        string requestId,
        string targetEntityId = "body-one") => Request(document.Snapshot, requestId, targetEntityId);

    internal static PhotonCadRuntimeSyncRequest Request(
        PhotonCadCanonicalProject project,
        string requestId,
        string targetEntityId = "body-one") => new(
        requestId,
        project.SessionId,
        project.ProjectId,
        project.Revision,
        "geometry.create.box",
        PhotonCadOperationModeV1.Suggest,
        targetEntityIds: [targetEntityId]);

    internal static PhotonCadSealedMutationDelta BoxMutation(PhotonCadSealedMutationProviderRequest providerRequest)
    {
        var request = providerRequest.Request;
        var entityId = request.TargetEntityIds.Single();
        var evidence = Evidence();
        var create = new PhotonCadAppliedOperationDelta(
            request.BaseRevision + 1,
            $"op-{request.RequestId}-create",
            request.CapabilityId,
            "Create box",
            DateTimeOffset.Parse("2026-08-10T12:00:00Z"),
            request.Mode,
            request.Inputs,
            request.TargetEntityIds,
            evidence);
        var export = new PhotonCadAppliedOperationDelta(
            request.BaseRevision + 2,
            $"op-{request.RequestId}-export",
            "geometry.export.step",
            "Seal STEP",
            DateTimeOffset.Parse("2026-08-10T12:00:01Z"),
            request.Mode,
            [],
            [entityId],
            evidence);
        var step = Encoding.ASCII.GetBytes(
            $"ISO-10303-21;\nHEADER;\nFILE_DESCRIPTION(('{entityId}'),'2;1');\nENDSEC;\nDATA;\nENDSEC;\nEND-ISO-10303-21;\n");
        var artifact = new PhotonCadSealedArtifactDelta(
            PhotonCadArtifactRoleV1.AuthoritativeGeometry,
            PhotonCadArtifactKindV1.Step,
            entityId,
            export.AppliedRevision,
            step,
            step.LongLength,
            $"sha256:{Convert.ToHexStringLower(SHA256.HashData(step))}",
            "model/step",
            null,
            export.Id,
            evidence);
        return new PhotonCadSealedMutationDelta(
            $"mutation-{request.RequestId}",
            request.RequestId,
            request.SessionId,
            request.ProjectId,
            request.BaseRevision,
            export.AppliedRevision,
            [create, export],
            [new PhotonCadEntityV1(
                entityId,
                null,
                PhotonCadEntityKindV1.Body,
                entityId,
                providerRequest.BaseEntities.Count == 0,
                false,
                request.CapabilityId)],
            artifacts: [artifact]);
    }

    internal static PhotonCadCanonicalProject DirtyMutation(
        PhotonCadRuntimeCanonicalMapperV1 mapper,
        PhotonCadProjectDocument document,
        PhotonCadRuntimeSyncRequest request)
    {
        var binding = new PhotonCadCanonicalMutationBinding(document.ProjectHandle, document.Snapshot);
        var providerRequest = mapper.PrepareProviderRequest(binding, request);
        return mapper.Apply(binding, request, BoxMutation(providerRequest));
    }

    private static PhotonCadProviderEvidence Evidence()
    {
        var image = Digest('d');
        var receipt = Digest('e');
        return new PhotonCadProviderEvidence(
            PhotonCadBackendV1.Geometry,
            "build123d.mcp",
            ["mcp.initialize", "mcp.tools.call"],
            "catalog.1",
            "photon.cad.geometry.container.v1",
            receipt,
            receipt,
            image,
            Digest('b'),
            new PhotonCadSourceIdentityV1("build123d-mcp", "0.3.80", image, "Apache-2.0"));
    }

    private static string Digest(char value) => $"sha256:{new string(value, 64)}";
}

static class NativeSmoke
{
    private const uint GenericWrite = 0x40000000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FsctlSetReparsePoint = 0x000900a4;
    private const uint IoReparseTagMountPoint = 0xa0000003;
    private const int FileCaseSensitiveInfo = 23;

    public static void CreateHardLink(string linkPath, string existingPath)
    {
        if (!CreateHardLinkW(linkPath, existingPath, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public static void CreateJunction(string junctionPath, string targetPath)
    {
        var exactTarget = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetPath));
        Directory.CreateDirectory(junctionPath);
        var substitute = Encoding.Unicode.GetBytes($@"\??\{exactTarget}");
        var print = Encoding.Unicode.GetBytes(exactTarget);
        var pathBytes = checked(substitute.Length + sizeof(char) + print.Length + sizeof(char));
        var reparseDataLength = checked(8 + pathBytes);
        var buffer = new byte[checked(8 + reparseDataLength)];
        BitConverter.GetBytes(IoReparseTagMountPoint).CopyTo(buffer, 0);
        BitConverter.GetBytes(checked((ushort)reparseDataLength)).CopyTo(buffer, 4);
        BitConverter.GetBytes((ushort)0).CopyTo(buffer, 6);
        BitConverter.GetBytes((ushort)0).CopyTo(buffer, 8);
        BitConverter.GetBytes(checked((ushort)substitute.Length)).CopyTo(buffer, 10);
        BitConverter.GetBytes(checked((ushort)(substitute.Length + sizeof(char)))).CopyTo(buffer, 12);
        BitConverter.GetBytes(checked((ushort)print.Length)).CopyTo(buffer, 14);
        substitute.CopyTo(buffer, 16);
        print.CopyTo(buffer, checked(16 + substitute.Length + sizeof(char)));

        using var handle = CreateFileW(
            junctionPath,
            GenericWrite,
            FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!DeviceIoControl(handle, FsctlSetReparsePoint, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public static void SetCaseSensitiveDirectory(string directoryPath, bool enabled)
    {
        using var handle = CreateFileW(
            directoryPath,
            GenericWrite,
            FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var value = BitConverter.GetBytes(enabled ? 1u : 0u);
        if (!SetFileInformationByHandle(handle, FileCaseSensitiveInfo, value, value.Length))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string fileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, FileShare shareMode, IntPtr securityAttributes, FileMode creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode, byte[] input, int inputSize, IntPtr output, int outputSize, out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int fileInformationClass, byte[] fileInformation, int bufferSize);
}

sealed class SmokeSuite(string root)
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _passed;
    private int _failed;

    public string PathFor(string name) => Path.Combine(root, name);

    public async Task RunAsync(string name, Func<Task> test)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            await test();
            _passed++;
            Console.WriteLine($"PASS {name} ({timer.Elapsed.TotalMilliseconds:F1} ms)");
        }
        catch (Exception exception)
        {
            _failed++;
            Console.Error.WriteLine($"FAIL {name}: {exception.GetType().Name}: {exception.Message}");
        }
    }

    public Task<int> CompleteAsync()
    {
        Console.WriteLine($"PhotonCadProjects.Windows.Smoke: {_passed} passed, {_failed} failed in {_clock.Elapsed.TotalMilliseconds:F1} ms");
        if (_failed == 0)
        {
            var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            var exactRoot = Path.GetFullPath(root);
            if (!exactRoot.StartsWith(temp, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(exactRoot).StartsWith("photon-cad-windows-smoke-", StringComparison.Ordinal)
                || (File.GetAttributes(exactRoot) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Smoke cleanup root validation failed.");
            Directory.Delete(exactRoot, recursive: true);
        }
        else
        {
            Console.Error.WriteLine($"Evidence retained at {root}");
        }
        return Task.FromResult(_failed == 0 ? 0 : 1);
    }
}
