using System.Buffers.Binary;
using System.Text.Json;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using HermesDesktop;
using PhotonCadProjects;
using PhotonCadProjects.Codec;
using PhotonCadProjects.DesktopAdapter;
using PhotonCadProjects.RuntimeSync;
using PhotonCadProjects.Windows;
using PhotonCadPreviews;
using PhotonCadRuntime;

internal static class PhotonCadBridgeSmoke
{
    internal static async Task<bool> RunAsync()
    {
        EnsureRuntimeSyncLoadedForSmoke();
        if (!await ProjectResetFailureStaysClosedAsync()) return false;
        var tempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"photon-cad-bridge-smoke-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            if (!await RunPersistedPrimitiveFlowAsync(tempRoot)) return false;
            if (!await IndustrialMissingEvidenceStaysUnavailableAsync(tempRoot)) return false;
            if (!await RunIndustrialPreviewFlowAsync(tempRoot)) return false;
            if (!await HostileRuntimeResultMatrixAsync(tempRoot)) return false;
            if (!await PersistenceFailureCompensatesAsync(tempRoot)) return false;
            if (!await CancellationDrainsAsync(tempRoot)) return false;
            return true;
        }
        finally { DeleteOwnedSmokeRoot(tempRoot); }
    }

    private static async Task<bool> IndustrialMissingEvidenceStaysUnavailableAsync(string tempRoot)
    {
        var installRoot = Path.Combine(tempRoot, "missing-industrial-assets");
        Directory.CreateDirectory(installRoot);
        var frames = new List<JsonElement>();
        await using var bridge = new PhotonCadBridge(
            installRoot,
            message => frames.Add(JsonSerializer.SerializeToElement(message)));
        var epoch = await bridge.ResetAsync();
        if (!bridge.TryOpenRendererGeneration(epoch))
            return Fail("Missing-evidence industrial bridge generation did not open.");
        await SendAsync(bridge,
            """{"type":"photonCad.describe","version":1,"contractVersion":1,"requestId":"industrial-missing-evidence"}""");
        var value = Frame(frames, "photonCad.describe.result").GetProperty("value");
        if (Text(value, "status") != "unavailable"
            || Text(value, "reason") != "industrial_runtime_unavailable"
            || Directory.Exists(Path.Combine(installRoot, "runtime-assets", "photon-cad-industrial")))
            return Fail("Industrial CAD did not remain truthfully unavailable without exact installer-owned evidence.");
        return true;
    }

    private static async Task<bool> RunIndustrialPreviewFlowAsync(string tempRoot)
    {
        var path = Path.Combine(tempRoot, "industrial-preview.photoncad");
        var frames = new List<JsonElement>();
        SmokeIndustrialProvider? provider = null;
        var origin = new Uri("https://127.0.0.1:4173/", UriKind.Absolute);
        await using var bridge = new PhotonCadBridge(
            Directory.GetCurrentDirectory(),
            message => frames.Add(JsonSerializer.SerializeToElement(message)),
            projectDialog: new SmokeProjectDialog(path),
            workbenchOrigin: origin,
            industrialBindingFactory: (request, _) =>
            {
                var syncRequest = new PhotonCadRuntimeSyncRequest(
                    request.RequestId,
                    request.SessionId,
                    request.ProjectId,
                    request.BaseRevision,
                    request.CapabilityId,
                    PhotonCadOperationModeV1.Scratch,
                    request.Inputs.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .Select(pair => new PhotonCadSyncOperationInput(pair.Key, PhotonCadSyncInputValue.Number(pair.Value))),
                    [request.EntityId]);
                provider = new SmokeIndustrialProvider(syncRequest, request.EntityId);
                return ValueTask.FromResult(new IndustrialMutationBinding(syncRequest, provider, provider));
            });

        var epoch = await bridge.ResetAsync();
        if (!bridge.TryOpenRendererGeneration(epoch)) return Fail("Industrial preview bridge generation did not open.");
        var identity = await CreateCanonicalProjectAsync(bridge, frames, "industrial-preview");
        await SendPrimitiveAsync(bridge, "industrial-box", identity.SessionId, identity.ProjectId, 0,
            CadPinnedCapabilityCatalog.BoxCapabilityId);
        var result = Frame(frames, "photonCad.execute.result").GetProperty("value");
        if (provider is null
            || provider.ApplyCount != 1
            || Text(result, "status") != "accepted"
            || result.GetProperty("resultingRevision").GetInt64() != 2
            || !result.TryGetProperty("preview", out var preview)
            || preview.GetProperty("entityCount").GetInt32() != 1)
            return Fail("Industrial CAD did not publish one exact committed preview receipt.");

        var snapshot = result.GetProperty("snapshot");
        var occurrence = snapshot.GetProperty("entities").EnumerateArray()
            .SingleOrDefault(entity => Text(entity, "kind") == "occurrence");
        if (occurrence.ValueKind != JsonValueKind.Object
            || Text(occurrence, "id") != provider.OccurrenceId
            || snapshot.GetProperty("entities").GetArrayLength() != 2)
            return Fail("Industrial CAD did not project the GLB occurrence tag into the viewer inventory.");

        var codec = new PhotonCadCanonicalProjectCodecV1();
        var persisted = codec.Decode(await File.ReadAllBytesAsync(path));
        var persistedState = codec.Inspect(persisted);
        if (persisted.Dirty || persisted.Revision != 2
            || persistedState.Occurrences.Count != 1
            || persistedState.Occurrences[0].OccurrenceId != provider.OccurrenceId)
            return Fail("Industrial CAD did not durably save the exact preview occurrence before acceptance.");

        var previewId = Text(preview, "previewId")!;
        var digest = Text(preview, "contentDigest")!;
        async Task<Uri> ResolveAsync(string requestId)
        {
            await SendAsync(bridge, $$"""
                {"type":"photonCad.preview.resolve","version":1,"contractVersion":1,"requestId":"{{requestId}}","sessionId":"{{identity.SessionId}}","projectId":"{{identity.ProjectId}}","revision":2,"previewId":"{{previewId}}","expectedDigest":"{{digest}}","maximumBytes":1048576}
                """);
            var resolved = Frame(frames, "photonCad.preview.resolve.result").GetProperty("value");
            if (Text(resolved, "status") != "available"
                || Text(resolved, "contentDigest") != digest
                || Text(resolved, "mediaType") != PhotonCadPreviewContract.MediaType)
                throw new InvalidOperationException("The committed preview did not resolve through its exact receipt.");
            return new Uri(Text(resolved, "url")!, UriKind.Absolute);
        }

        var rangeUrl = await ResolveAsync("preview-range");
        using (var range = bridge.TryRespondPreviewResource("GET", rangeUrl, true,
                   new Dictionary<string, string> { ["Range"] = "bytes=0-1" })!)
        {
            if (range.StatusCode != 404) return Fail("Industrial preview accepted a range request.");
        }
        var assetUrl = await ResolveAsync("preview-get");
        if (assetUrl.GetLeftPart(UriPartial.Authority) != origin.GetLeftPart(UriPartial.Authority)
            || !assetUrl.AbsolutePath.StartsWith(PhotonCadBridge.PreviewResourcePathPrefix, StringComparison.Ordinal)
            || assetUrl.AbsoluteUri.Contains(identity.ProjectId, StringComparison.Ordinal))
            return Fail("Industrial preview resource was not fixed-origin and opaque.");
        using (var response = bridge.TryRespondPreviewResource("GET", assetUrl, true)!)
        {
            if (response.StatusCode != 200 || response.Content is null)
                return Fail("Industrial preview resource was unavailable on its one authorized GET.");
            using var memory = new MemoryStream();
            await response.Content.CopyToAsync(memory);
            if (!memory.ToArray().SequenceEqual(provider.Glb))
                return Fail("Industrial preview responder changed the sealed GLB bytes.");
        }
        using (var replay = bridge.TryRespondPreviewResource("GET", assetUrl, true)!)
        {
            if (replay.StatusCode != 404) return Fail("Industrial preview resource replay was accepted.");
        }
        var revokedUrl = await ResolveAsync("preview-reset");
        var resetEpoch = await bridge.ResetAsync();
        if (!bridge.TryOpenRendererGeneration(resetEpoch)) return Fail("Industrial preview reset did not reopen cleanly.");
        using var revoked = bridge.TryRespondPreviewResource("GET", revokedUrl, true)!;
        if (revoked.StatusCode != 404) return Fail("Industrial preview survived renderer reset.");
        if (JsonSerializer.Serialize(frames).Contains(tempRoot, StringComparison.OrdinalIgnoreCase))
            return Fail("Industrial preview frames leaked a host path.");
        return true;
    }

    private static void EnsureRuntimeSyncLoadedForSmoke()
    {
        const string assemblyName = "PhotonCadProjects.RuntimeSync";
        if (AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name == assemblyName)) return;
        var exactPath = Path.Combine(AppContext.BaseDirectory, $"{assemblyName}.dll");
        if (!File.Exists(exactPath)) throw new FileNotFoundException("The focused CAD smoke dependency is missing.", exactPath);
        _ = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(exactPath));
    }

    private static async Task<bool> HostileRuntimeResultMatrixAsync(string tempRoot)
    {
        var faults = new[]
        {
            SmokeCadFault.Stale,
            SmokeCadFault.ForeignProject,
            SmokeCadFault.ForeignSession,
            SmokeCadFault.ForeignArtifact,
            SmokeCadFault.ExtraArtifact,
            SmokeCadFault.DigestMismatch,
            SmokeCadFault.LengthMismatch,
            SmokeCadFault.MediaMismatch,
            SmokeCadFault.OverCapacity,
        };
        foreach (var fault in faults)
        {
            var path = Path.Combine(tempRoot, $"hostile-{fault}.photoncad");
            var frames = new List<JsonElement>();
            var broker = new SmokeCadBroker { Fault = fault };
            await using var bridge = new PhotonCadBridge(
                Directory.GetCurrentDirectory(),
                message => frames.Add(JsonSerializer.SerializeToElement(message)),
                _ => ValueTask.FromResult<ICadRuntimeBroker>(broker),
                projectDialog: new SmokeProjectDialog(path));
            var epoch = await bridge.ResetAsync();
            if (!bridge.TryOpenRendererGeneration(epoch)) return Fail($"Hostile {fault} bridge generation did not open.");
            var identity = await CreateCanonicalProjectAsync(bridge, frames, $"hostile-{fault}");
            var before = frames.Count;
            await SendPrimitiveAsync(bridge, $"request-{fault}", identity.SessionId, identity.ProjectId, 0, CadPinnedCapabilityCatalog.BoxCapabilityId);
            var emitted = frames.Skip(before).ToArray();
            if (emitted.Any(frame => Text(frame, "type") == "photonCad.execute.result"
                    && Text(frame.GetProperty("value"), "status") == "accepted")
                || !emitted.Any(frame => Text(frame, "type") == "photonCad.error")
                || broker.CloseCount != 1)
                return Fail($"Hostile {fault} runtime evidence was accepted or not evicted.");
            var persisted = new PhotonCadCanonicalProjectCodecV1().Decode(await File.ReadAllBytesAsync(path));
            if (persisted.Revision != 0 || persisted.Dirty)
                return Fail($"Hostile {fault} runtime evidence changed the canonical project.");
        }
        return true;
    }

    private static async Task<bool> PersistenceFailureCompensatesAsync(string tempRoot)
    {
        var path = Path.Combine(tempRoot, "persistence-failure.photoncad");
        var frames = new List<JsonElement>();
        var broker = new SmokeCadBroker();
        await using var bridge = new PhotonCadBridge(
            Directory.GetCurrentDirectory(),
            message => frames.Add(JsonSerializer.SerializeToElement(message)),
            _ => ValueTask.FromResult<ICadRuntimeBroker>(broker),
            projectDialog: new SmokeProjectDialog(path));
        var epoch = await bridge.ResetAsync();
        if (!bridge.TryOpenRendererGeneration(epoch)) return Fail("Persistence-failure bridge generation did not open.");
        var identity = await CreateCanonicalProjectAsync(bridge, frames, "persistence-failure");
        var before = frames.Count;
        await using (var lockStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await SendPrimitiveAsync(
                bridge,
                "request-persistence-failure",
                identity.SessionId,
                identity.ProjectId,
                0,
                CadPinnedCapabilityCatalog.BoxCapabilityId);
        }
        var emitted = frames.Skip(before).ToArray();
        var persisted = new PhotonCadCanonicalProjectCodecV1().Decode(await File.ReadAllBytesAsync(path));
        if (emitted.Any(frame => Text(frame, "type") == "photonCad.execute.result"
                && Text(frame.GetProperty("value"), "status") == "accepted")
            || !emitted.Any(frame => Text(frame, "type") == "photonCad.error")
            || broker.CloseCount != 1
            || persisted.Revision != 0
            || persisted.Dirty)
            return Fail("A failed durable save published acceptance, retained the runtime binding, or changed canonical state.");
        return true;
    }

    private static async Task<bool> CancellationDrainsAsync(string tempRoot)
    {
        var path = Path.Combine(tempRoot, "cancellation.photoncad");
        var frames = new List<JsonElement>();
        var broker = new SmokeCadBroker { BlockNextCreate = true };
        await using var bridge = new PhotonCadBridge(
            Directory.GetCurrentDirectory(),
            message => frames.Add(JsonSerializer.SerializeToElement(message)),
            _ => ValueTask.FromResult<ICadRuntimeBroker>(broker),
            projectDialog: new SmokeProjectDialog(path));
        var epoch = await bridge.ResetAsync();
        if (!bridge.TryOpenRendererGeneration(epoch)) return Fail("Cancellation bridge generation did not open.");
        var identity = await CreateCanonicalProjectAsync(bridge, frames, "cancellation");
        using var document = JsonDocument.Parse($$"""
            {"type":"photonCad.execute","version":1,"contractVersion":1,"requestId":"request-cancelled-create","sessionId":"{{identity.SessionId}}","projectId":"{{identity.ProjectId}}","baseRevision":0,"mode":"scratch","capabilityId":"geometry.box.create.v1","inputs":{"length":10,"width":20,"height":30},"targetEntityIds":[]}
            """);
        var execution = bridge.HandleAsync("photonCad.execute", document.RootElement);
        await broker.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await SendAsync(bridge, """{"type":"photonCad.cancel","version":1,"requestId":"cancel-create","targetRequestId":"request-cancelled-create"}""");
        await execution.WaitAsync(TimeSpan.FromSeconds(10));
        if (frames.Any(frame => Text(frame, "type") == "photonCad.execute.result"
                && Text(frame.GetProperty("value"), "status") == "accepted")
            || broker.CloseCount != 1)
            return Fail("Cancelled CAD execution published acceptance or retained its runtime binding.");
        var resetEpoch = await bridge.ResetAsync();
        if (!bridge.TryOpenRendererGeneration(resetEpoch))
            return Fail("CAD reset did not drain and reauthorize after cancellation.");
        return true;
    }

    private static async Task<(string SessionId, string ProjectId)> CreateCanonicalProjectAsync(
        PhotonCadBridge bridge,
        List<JsonElement> frames,
        string suffix)
    {
        await SendAsync(bridge, $$"""
            {"type":"photonCad.project.picker","version":1,"contractVersion":1,"requestId":"picker-{{suffix}}","purpose":"new"}
            """);
        var picker = Frame(frames, "photonCad.project.picker.result").GetProperty("value");
        var workspaceHandle = Text(picker, "workspaceHandle")
            ?? throw new InvalidOperationException("The smoke picker did not return an opaque workspace handle.");
        await SendAsync(bridge, $$"""
            {"type":"photonCad.project.create","version":1,"contractVersion":1,"requestId":"create-{{suffix}}","workspaceHandle":"{{workspaceHandle}}","title":"Primitive","units":"millimeter"}
            """);
        var created = Frame(frames, "photonCad.project.create.result").GetProperty("value").GetProperty("document").GetProperty("snapshot");
        return (Text(created, "sessionId")!, Text(created, "projectId")!);
    }

    private static Task SendPrimitiveAsync(
        PhotonCadBridge bridge,
        string requestId,
        string sessionId,
        string projectId,
        long revision,
        string capabilityId)
    {
        var inputs = capabilityId == CadPinnedCapabilityCatalog.BoxCapabilityId
            ? "\"length\":10,\"width\":20,\"height\":30"
            : "\"radius\":5,\"height\":30";
        return SendAsync(bridge, $$"""
            {"type":"photonCad.execute","version":1,"contractVersion":1,"requestId":"{{requestId}}","sessionId":"{{sessionId}}","projectId":"{{projectId}}","baseRevision":{{revision}},"mode":"scratch","capabilityId":"{{capabilityId}}","inputs":{ {{inputs}} },"targetEntityIds":[]}
            """);
    }

    private static async Task<bool> RunPersistedPrimitiveFlowAsync(string tempRoot)
    {
        var frames = new List<JsonElement>();
        var broker = new SmokeCadBroker();
        var brokerFactoryCalls = 0;
        var projectPath = Path.Combine(tempRoot, "primitive.photoncad");
        var projectDialog = new SmokeProjectDialog(projectPath);
        await using var bridge = new PhotonCadBridge(
            Directory.GetCurrentDirectory(),
            message => frames.Add(JsonSerializer.SerializeToElement(message)),
            _ =>
            {
                brokerFactoryCalls++;
                return ValueTask.FromResult<ICadRuntimeBroker>(broker);
            },
            projectDialog: projectDialog);

        var projectCapabilities = JsonSerializer.SerializeToElement(bridge.ProjectCapabilityAdvertisement());
        if (!bridge.ProjectActionsAvailable
            || Text(projectCapabilities, "reason") != "project-host-ready"
            || !projectCapabilities.TryGetProperty("runtimeHydrationAvailable", out var hydrationAvailable)
            || hydrationAvailable.ValueKind != JsonValueKind.False
            || !projectCapabilities.TryGetProperty("runtimeProjectSyncAvailable", out var projectSyncAvailable)
            || projectSyncAvailable.ValueKind != JsonValueKind.True)
            return Fail("Photon CAD did not advertise the frozen native project lifecycle provider.");
        var initialEpoch = await bridge.ResetAsync();
        if (!bridge.TryOpenRendererGeneration(initialEpoch))
            return Fail("Photon CAD did not open a clean initial renderer generation.");
        if (!await VerifyCoreContractVersionGateAsync(bridge, frames, broker, () => brokerFactoryCalls)) return false;

        await SendAsync(bridge, """{"type":"photonCad.project.picker","version":1,"contractVersion":1,"requestId":"cad-project-picker-smoke","purpose":"open"}""");
        var picker = Frame(frames, "photonCad.project.picker.result");
        if (picker.ValueKind != JsonValueKind.Object
            || Text(picker.GetProperty("value"), "requestId") != "cad-project-picker-smoke"
            || Text(picker.GetProperty("value"), "status") != "cancelled"
            || projectDialog.Calls != 1
            || projectDialog.LastPurpose != "open"
            || JsonSerializer.Serialize(picker).Contains("\\", StringComparison.Ordinal))
            return Fail("Photon CAD did not correlate the renderer picker frame to one pathless native-dialog cancellation.");

        await SendAsync(bridge, """{"type":"photonCad.describe","version":1,"contractVersion":1,"requestId":"cad-describe-smoke"}""");
        var description = Frame(frames, "photonCad.describe.result");
        if (description.ValueKind != JsonValueKind.Object
            || Text(description.GetProperty("value"), "status") != "available"
            || description.GetProperty("value").GetProperty("catalog").GetProperty("capabilities").GetArrayLength() != 2
            || description.GetProperty("value").GetProperty("catalog").GetProperty("coverage").GetProperty("unavailable").GetInt32() != 1
            || brokerFactoryCalls != 1
            || broker.DescribeCount != 1)
            return Fail("Photon CAD describe did not expose the safe verified catalog projection.");

        await SendAsync(bridge, """{"type":"photonCad.project.picker","version":1,"contractVersion":1,"requestId":"cad-project-new-picker","purpose":"new"}""");
        var newPicker = Frame(frames, "photonCad.project.picker.result").GetProperty("value");
        if (Text(newPicker, "status") != "selected")
            return Fail("Photon CAD did not issue one opaque native workspace selection for a new project.");
        var workspaceHandle = Text(newPicker, "workspaceHandle")!;
        await SendAsync(bridge, $$"""
            {"type":"photonCad.project.create","version":1,"contractVersion":1,"requestId":"cad-project-create-smoke","workspaceHandle":"{{workspaceHandle}}","title":"Primitive","units":"millimeter"}
            """);
        var createdProject = Frame(frames, "photonCad.project.create.result").GetProperty("value");
        if (Text(createdProject, "status") != "opened")
            return Fail("Photon CAD did not create the canonical revision-zero project through the native authority.");
        var document = createdProject.GetProperty("document");
        var canonicalSnapshot = document.GetProperty("snapshot");
        var canonicalSessionId = Text(canonicalSnapshot, "sessionId")!;
        var canonicalProjectId = Text(canonicalSnapshot, "projectId")!;

        await SendAsync(bridge, $$"""
            {"type":"photonCad.execute","version":1,"contractVersion":1,"requestId":"cad-execute-smoke","sessionId":"{{canonicalSessionId}}","projectId":"{{canonicalProjectId}}","baseRevision":0,"mode":"scratch","capabilityId":"geometry.box.create.v1","inputs":{"length":10,"width":20,"height":30},"targetEntityIds":[]}
            """);
        var execute = Frame(frames, "photonCad.execute.result");
        var executeValue = execute.ValueKind == JsonValueKind.Object ? execute.GetProperty("value") : default;
        if (executeValue.ValueKind != JsonValueKind.Object
            || Text(executeValue, "status") != "accepted"
            || executeValue.GetProperty("resultingRevision").GetInt64() != 2
            || Text(executeValue, "projectId") != canonicalProjectId
            || Text(executeValue.GetProperty("snapshot"), "sessionId") != canonicalSessionId
            || Text(executeValue.GetProperty("snapshot"), "projectId") != canonicalProjectId
            || Text(executeValue.GetProperty("snapshot"), "mode") != "canonical"
            || executeValue.GetProperty("snapshot").GetProperty("dirty").GetBoolean()
            || executeValue.TryGetProperty("preview", out _))
            return Fail($"Photon CAD execute did not publish only the durably saved canonical +2 result: {JsonSerializer.Serialize(frames.TakeLast(3))}");

        await SendAsync(bridge, $$"""
            {"type":"photonCad.execute","version":1,"contractVersion":1,"requestId":"cad-export-smoke","sessionId":"{{canonicalSessionId}}","projectId":"{{canonicalProjectId}}","baseRevision":2,"mode":"scratch","capabilityId":"geometry.step.export.v1","inputs":{},"targetEntityIds":["entity-placeholder"]}
            """);
        var export = Frame(frames, "photonCad.execute.result").GetProperty("value");
        if (Text(export, "status") != "unavailable"
            || Text(export, "reason") != "runtime_persisted_operation_unavailable"
            || export.GetProperty("stale").GetBoolean()
            || broker.ExecuteCount != 2)
            return Fail("Photon CAD exposed the host-internal STEP operation to the renderer.");

        await SendAsync(bridge, $$"""
            {"type":"photonCad.project.close","version":1,"contractVersion":1,"requestId":"cad-close-unknown","projectHandle":"cad-project:{{new string('A', 32)}}","sessionId":"renderer-session","projectId":"renderer-project","revision":1,"lastSavedRevision":0,"contentDigest":"sha256:{{new string('a', 64)}}","lastSavedContentDigest":"sha256:{{new string('a', 64)}}","discardUnsavedChanges":false}
            """);
        var close = Frame(frames, "photonCad.project.close.result").GetProperty("value");
        if (Text(close, "status") != "rejected")
            return Fail("Photon CAD project close did not remain bound to the native project provider.");

        await SendAsync(bridge, $$"""
            {"type":"photonCad.execute","version":1,"contractVersion":1,"requestId":"cad-after-project-close","sessionId":"{{canonicalSessionId}}","projectId":"{{canonicalProjectId}}","baseRevision":2,"mode":"scratch","capabilityId":"geometry.cylinder.create.v1","inputs":{"radius":5,"height":30},"targetEntityIds":[]}
            """);
        var afterProjectClose = Frame(frames, "photonCad.execute.result").GetProperty("value");
        if (Text(afterProjectClose, "status") != "accepted"
            || afterProjectClose.GetProperty("resultingRevision").GetInt64() != 4
            || broker.ExecuteCount != 4
            || broker.ReceiptCount != 2
            || broker.LeaseCount != 2)
            return Fail("Photon CAD did not preserve the same-open exact 2-to-4 runtime attachment.");

        var persisted = new PhotonCadCanonicalProjectCodecV1().Decode(await File.ReadAllBytesAsync(projectPath));
        var persistedState = new PhotonCadCanonicalProjectCodecV1().Inspect(persisted);
        if (persisted.Dirty || persisted.Revision != 4
            || persistedState.Entities.Count != 2
            || persistedState.Operations.Count != 4
            || persistedState.Artifacts.Count != 2)
            return Fail("The canonical project did not contain both sealed primitive tranches after atomic persistence.");
        if (!VerifyPersistedPrimitiveEvidence(persistedState, canonicalSessionId, canonicalProjectId, broker)) return false;

        broker.Fault = SmokeCadFault.DigestMismatch;
        var frameCountBeforeFailure = frames.Count;
        await SendAsync(bridge, $$"""
            {"type":"photonCad.execute","version":1,"contractVersion":1,"requestId":"cad-hostile-digest","sessionId":"{{canonicalSessionId}}","projectId":"{{canonicalProjectId}}","baseRevision":4,"mode":"scratch","capabilityId":"geometry.box.create.v1","inputs":{"length":1,"width":2,"height":3},"targetEntityIds":[]}
            """);
        if (!frames.Skip(frameCountBeforeFailure).Any(frame => Text(frame, "type") == "photonCad.error" && Text(frame, "requestId") == "cad-hostile-digest")
            || frames.Skip(frameCountBeforeFailure).Any(frame => Text(frame, "type") == "photonCad.execute.result")
            || broker.CloseCount != 1)
            return Fail("A hostile artifact result was accepted or its runtime binding was not evicted.");

        broker.FailNextDispose = true;
        var resetFailedClosed = false;
        try { _ = await bridge.ResetAsync(); }
        catch (InvalidOperationException) { resetFailedClosed = true; }
        var frameCountAfterFailedReset = frames.Count;
        await SendAsync(bridge, """{"type":"photonCad.describe","version":1,"contractVersion":1,"requestId":"cad-blocked-after-failed-reset"}""");
        if (!resetFailedClosed || broker.DisposeAttempts != 1 || frames.Count != frameCountAfterFailedReset)
            return Fail("Photon CAD did not retain broker custody and keep renderer admission closed after reset failure.");

        var resetEpoch = await bridge.ResetAsync();
        if (!bridge.TryOpenRendererGeneration(resetEpoch))
            return Fail("Photon CAD did not open a clean post-reset renderer generation.");
        if (broker.DisposeAttempts != 2)
            return Fail("Photon CAD did not retry the exact failed broker teardown before reauthorization.");
        await SendAsync(bridge, """
            {"type":"photonCad.execute","version":1,"contractVersion":1,"requestId":"cad-after-reset-smoke","sessionId":"renderer-session","projectId":"renderer-project","baseRevision":1,"mode":"scratch","capabilityId":"primitive.box","inputs":{"length":10,"width":20,"height":30},"targetEntityIds":[]}
            """);
        var afterReset = Frame(frames, "photonCad.execute.result").GetProperty("value");
        if (Text(afterReset, "status") != "unavailable" || afterReset.GetProperty("stale").GetBoolean())
            return Fail("Photon CAD navigation reset retained a runtime binding or emitted a false stale-revision signal.");

        await SendAsync(bridge, """{"type":"photonCad.project.create","version":1,"contractVersion":1,"requestId":"cad-project-smoke","workspaceHandle":"cad-workspace:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","title":"Part","units":"millimeter"}""");
        var project = Frame(frames, "photonCad.project.create.result");
        if (project.ValueKind != JsonValueKind.Object || Text(project.GetProperty("value"), "status") != "rejected")
            return Fail("Photon CAD project provider did not reject an unissued opaque workspace handle.");

        await SendAsync(bridge, """{"type":"photonCad.release.review","version":1,"contractVersion":1,"requestId":"cad-release-smoke","sessionId":"renderer-session","projectId":"renderer-project","revision":1,"formats":["step-ap242"],"destinationHandle":"cad-destination:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}""");
        var release = Frame(frames, "photonCad.release.review.result");
        if (release.ValueKind != JsonValueKind.Object || Text(release.GetProperty("value"), "status") != "unavailable")
            return Fail("Photon CAD release provider did not fail honestly unavailable.");

        await SendAsync(bridge, """{"type":"photonCad.commercial.document.review","version":1,"contractVersion":1,"requestId":"cad-commercial-smoke"}""");
        var commercial = Frame(frames, "photonCad.commercial.document.review.result");
        if (commercial.ValueKind != JsonValueKind.Object
            || Text(commercial.GetProperty("value"), "status") != "unavailable"
            || commercial.GetProperty("value").GetProperty("pages").GetArrayLength() != 0)
            return Fail("Photon CAD commercial provider did not fail honestly unavailable.");

        var serialized = JsonSerializer.Serialize(frames);
        if (serialized.Contains(Directory.GetCurrentDirectory(), StringComparison.OrdinalIgnoreCase)
            || serialized.Contains("docker.exe", StringComparison.OrdinalIgnoreCase)
            || serialized.Contains("python", StringComparison.OrdinalIgnoreCase)
            || serialized.Contains("--network", StringComparison.OrdinalIgnoreCase))
            return Fail("Photon CAD bridge leaked a native path, executable, runtime, or launch argument.");
        Console.WriteLine("Desktop Photon CAD typed routing, identity projection, unavailable seams, and no-native-leak guards passed.");
        return true;
    }

    private static async Task<bool> VerifyCoreContractVersionGateAsync(
        PhotonCadBridge bridge,
        List<JsonElement> frames,
        SmokeCadBroker broker,
        Func<int> brokerFactoryCalls)
    {
        var cases = new (string RequestId, string Json)[]
        {
            ("missing-contract-describe", """{"type":"photonCad.describe","version":1,"requestId":"missing-contract-describe"}"""),
            ("foreign-contract-describe", """{"type":"photonCad.describe","version":1,"contractVersion":2,"requestId":"foreign-contract-describe"}"""),
            ("missing-contract-execute", """{"type":"photonCad.execute","version":1,"requestId":"missing-contract-execute","sessionId":"session","projectId":"project","baseRevision":0,"mode":"scratch","capabilityId":"geometry.box.create.v1","inputs":{"length":1,"width":2,"height":3},"targetEntityIds":[]}"""),
            ("foreign-contract-execute", """{"type":"photonCad.execute","version":1,"contractVersion":2,"requestId":"foreign-contract-execute","sessionId":"session","projectId":"project","baseRevision":0,"mode":"scratch","capabilityId":"geometry.box.create.v1","inputs":{"length":1,"width":2,"height":3},"targetEntityIds":[]}"""),
            ("missing-contract-verify", """{"type":"photonCad.verify","version":1,"requestId":"missing-contract-verify","sessionId":"session","projectId":"project","revision":0,"checks":["valid-solids"]}"""),
            ("foreign-contract-verify", """{"type":"photonCad.verify","version":1,"contractVersion":2,"requestId":"foreign-contract-verify","sessionId":"session","projectId":"project","revision":0,"checks":["valid-solids"]}"""),
        };
        var before = frames.Count;
        foreach (var item in cases) await SendAsync(bridge, item.Json);
        var emitted = frames.Skip(before).ToArray();
        if (brokerFactoryCalls() != 0
            || broker.DescribeCount != 0
            || broker.OpenCount != 0
            || broker.StartCount != 0
            || broker.ExecuteCount != 0
            || broker.VerifyCount != 0
            || emitted.Length != cases.Length
            || cases.Any(item => emitted.Count(frame =>
                Text(frame, "type") == "photonCad.error"
                && Text(frame, "requestId") == item.RequestId
                && Text(frame, "code") == "invalid_contract_version") != 1)
            || emitted.Any(frame => Text(frame, "type") is "photonCad.describe.result" or "photonCad.execute.result" or "photonCad.verify.result"))
            return Fail("Missing or foreign core contract versions reached the CAD broker or emitted a success result.");
        return true;
    }

    private static bool VerifyPersistedPrimitiveEvidence(
        PhotonCadProjectStateV1 state,
        string expectedSessionId,
        string expectedProjectId,
        SmokeCadBroker broker)
    {
        const string geometryImageSha256 = "sha256:33d9c839840115640b08dd3c4142b7f29624329408155fe1484e1d88c3891703";
        var receiptSha256 = $"sha256:{PhotonCadBridge.AcceptedReceiptSha256}";
        var expectedCapabilities = new[]
        {
            CadPinnedCapabilityCatalog.BoxCapabilityId,
            CadPinnedCapabilityCatalog.ExportStepCapabilityId,
            CadPinnedCapabilityCatalog.CylinderCapabilityId,
            CadPinnedCapabilityCatalog.ExportStepCapabilityId,
        };
        var expectedBases = new long[] { 0, 1, 2, 3 };
        if (!StringComparer.Ordinal.Equals(state.SessionId, expectedSessionId)
            || !StringComparer.Ordinal.Equals(state.ProjectId, expectedProjectId)
            || state.Operations.Select(operation => operation.CapabilityId).SequenceEqual(expectedCapabilities, StringComparer.Ordinal) is false
            || state.Operations.Select(operation => (long)operation.Ordinal).SequenceEqual(expectedBases) is false
            || state.Operations.Any(operation => operation.State != PhotonCadOperationStateV1.Applied
                || operation.Mode != PhotonCadOperationModeV1.Scratch
                || !SourceMatches(operation.Source, geometryImageSha256)))
            return Fail("Persisted primitive operation identity, order, mode, state, source, or project binding was not exact.");

        var box = state.Entities.SingleOrDefault(entity => entity.SourceCapabilityId == CadPinnedCapabilityCatalog.BoxCapabilityId);
        var cylinder = state.Entities.SingleOrDefault(entity => entity.SourceCapabilityId == CadPinnedCapabilityCatalog.CylinderCapabilityId);
        if (box is null || cylinder is null
            || state.Operations[0].TargetEntityIds.Count != 1
            || !StringComparer.Ordinal.Equals(state.Operations[0].TargetEntityIds[0], box.Id)
            || state.Operations[1].TargetEntityIds.Count != 1
            || !StringComparer.Ordinal.Equals(state.Operations[1].TargetEntityIds[0], box.Id)
            || state.Operations[2].TargetEntityIds.Count != 1
            || !StringComparer.Ordinal.Equals(state.Operations[2].TargetEntityIds[0], cylinder.Id)
            || state.Operations[3].TargetEntityIds.Count != 1
            || !StringComparer.Ordinal.Equals(state.Operations[3].TargetEntityIds[0], cylinder.Id))
            return Fail($"Persisted primitive create/export operations were not bound to the exact created entities: {JsonSerializer.Serialize(new { entities = state.Entities.Select(value => new { value.Id, value.SourceCapabilityId }), targets = state.Operations.Select(value => value.TargetEntityIds) })}");

        var orderedArtifacts = state.Artifacts.OrderBy(artifact => artifact.Revision).ToArray();
        var expectedOwners = new[] { box.Id, cylinder.Id };
        var expectedRevisions = new long[] { 2, 4 };
        var expectedOperationIds = new[] { state.Operations[1].Id, state.Operations[3].Id };
        for (var index = 0; index < orderedArtifacts.Length; index++)
        {
            var artifact = orderedArtifacts[index];
            if (artifact.Role != PhotonCadArtifactRoleV1.AuthoritativeGeometry
                || artifact.Kind != PhotonCadArtifactKindV1.Step
                || !StringComparer.Ordinal.Equals(artifact.OwnerEntityId, expectedOwners[index])
                || artifact.Revision != expectedRevisions[index]
                || artifact.ByteLength != artifact.Content.Length
                || !StringComparer.Ordinal.Equals(artifact.Digest, Sha256(artifact.Content.Span))
                || artifact.Bounds is not null
                || artifact.Provenance.Backend != PhotonCadBackendV1.Geometry
                || !StringComparer.Ordinal.Equals(artifact.Provenance.BundleId, "photon-cad-geometry")
                || !StringComparer.Ordinal.Equals(artifact.Provenance.BundleManifestSha256, receiptSha256)
                || !StringComparer.Ordinal.Equals(artifact.Provenance.CapabilityId, CadPinnedCapabilityCatalog.ExportStepCapabilityId)
                || !StringComparer.Ordinal.Equals(artifact.Provenance.OperationId, expectedOperationIds[index])
                || !SourceMatches(artifact.Provenance.Source, geometryImageSha256))
                return Fail("A sealed STEP artifact lost its exact owner, revision, bytes, digest, provenance, or runtime evidence binding.");
        }

        var executions = broker.ExecutionLog;
        if (executions.Count != 4
            || !StringComparer.Ordinal.Equals(executions[0].CapabilityId, CadPinnedCapabilityCatalog.BoxCapabilityId)
            || executions[0].BaseRevision != 0 || executions[0].TargetEntityIds.Count != 0
            || !StringComparer.Ordinal.Equals(executions[1].CapabilityId, CadPinnedCapabilityCatalog.ExportStepCapabilityId)
            || executions[1].BaseRevision != 1 || !executions[1].TargetEntityIds.SequenceEqual(["runtime-entity-1"], StringComparer.Ordinal)
            || !StringComparer.Ordinal.Equals(executions[2].CapabilityId, CadPinnedCapabilityCatalog.CylinderCapabilityId)
            || executions[2].BaseRevision != 2 || executions[2].TargetEntityIds.Count != 0
            || !StringComparer.Ordinal.Equals(executions[3].CapabilityId, CadPinnedCapabilityCatalog.ExportStepCapabilityId)
            || executions[3].BaseRevision != 3 || !executions[3].TargetEntityIds.SequenceEqual(["runtime-entity-2"], StringComparer.Ordinal)
            || executions.Any(execution => !StringComparer.Ordinal.Equals(execution.Session, broker.Session)
                || !StringComparer.Ordinal.Equals(execution.Project, broker.Project)))
            return Fail("The broker did not receive the exact ordered create/export 0-to-2 and 2-to-4 operation sequence.");

        var brokerArtifacts = broker.ArtifactEvidence;
        if (brokerArtifacts.Count != 2
            || brokerArtifacts.Any(evidence =>
                !StringComparer.Ordinal.Equals(evidence.Descriptor.Session.Value, broker.Session)
                || !StringComparer.Ordinal.Equals(evidence.Descriptor.MediaType, "model/step")
                || evidence.Descriptor.ByteLength != evidence.Content.LongLength
                || !DigestEquals(evidence.Descriptor.ContentDigest, Sha256(evidence.Content)))
            || orderedArtifacts.Any(artifact => brokerArtifacts.Count(evidence =>
                evidence.Revision == artifact.Revision
                && evidence.Descriptor.ByteLength == artifact.ByteLength
                && DigestEquals(evidence.Descriptor.ContentDigest, artifact.Digest)
                && evidence.Content.AsSpan().SequenceEqual(artifact.Content.Span)) != 1))
            return Fail($"The locked broker receipts and persisted STEP bytes were not bound by exact session, media, length, digest, and content: {JsonSerializer.Serialize(new { session = broker.Session, brokerArtifacts = brokerArtifacts.Select(value => new { descriptorSession = value.Descriptor.Session.Value, value.Descriptor.MediaType, value.Descriptor.ByteLength, value.Descriptor.ContentDigest, computed = Sha256(value.Content) }), persisted = orderedArtifacts.Select(value => new { value.Revision, value.ByteLength, value.Digest, computed = Sha256(value.Content.Span) }) })}");
        return true;
    }

    private static bool SourceMatches(PhotonCadSourceIdentityV1 source, string digest) =>
        StringComparer.Ordinal.Equals(source.Package, "build123d-mcp")
        && StringComparer.Ordinal.Equals(source.Version, "0.3.80")
        && StringComparer.Ordinal.Equals(source.Digest, digest)
        && StringComparer.Ordinal.Equals(source.License, "Apache-2.0");

    private static string Sha256(ReadOnlySpan<byte> value) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(value))}";

    private static bool DigestEquals(string left, string right) =>
        StringComparer.Ordinal.Equals(
            left.StartsWith("sha256:", StringComparison.Ordinal) ? left[7..] : left,
            right.StartsWith("sha256:", StringComparison.Ordinal) ? right[7..] : right);

    private sealed class SmokeProjectDialog : IPhotonCadWindowsFileDialog
    {
        private readonly string _projectPath;

        internal SmokeProjectDialog(string projectPath) => _projectPath = projectPath;

        internal int Calls { get; private set; }
        internal string? LastPurpose { get; private set; }

        public PhotonCadWindowsDialogResult Show(string purpose)
        {
            Calls++;
            LastPurpose = purpose;
            return purpose == "new"
                ? new PhotonCadWindowsDialogResult(true, _projectPath)
                : new PhotonCadWindowsDialogResult(false, null);
        }
    }

    private static void DeleteOwnedSmokeRoot(string tempRoot)
    {
        var full = Path.GetFullPath(tempRoot);
        var expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(full).StartsWith("photon-cad-bridge-smoke-", StringComparison.Ordinal)
            || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The bridge smoke cleanup root is not owned.");
        var entries = Directory.EnumerateFileSystemEntries(full, "*", SearchOption.AllDirectories).ToArray();
        if (entries.Any(entry => (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0))
            throw new InvalidOperationException("The bridge smoke cleanup root contains a linked path.");
        Directory.Delete(full, recursive: true);
    }

    private static async Task<bool> ProjectResetFailureStaysClosedAsync()
    {
        var host = new FailingResetProjectHost();
        var codec = new PhotonCadCanonicalProjectCodecV1();
        var dispatcher = new PhotonCadProjectDesktopDispatcher(host, new PhotonCadProjectWireProjection(codec), _ => { });
        await using var bridge = new PhotonCadBridge(
            Directory.GetCurrentDirectory(),
            _ => { },
            _ => ValueTask.FromResult<ICadRuntimeBroker>(new SmokeCadBroker()),
            dispatcher);
        var failed = false;
        try { _ = await bridge.ResetAsync(); }
        catch (InvalidOperationException) { failed = true; }
        if (!failed || bridge.TryOpenRendererGeneration(1))
            return Fail("Photon CAD reopened renderer admission after native project reset failure.");
        var retryEpoch = await bridge.ResetAsync();
        if (!bridge.TryOpenRendererGeneration(retryEpoch) || host.ResetCalls != 2)
            return Fail("Photon CAD did not require a successful native project reset retry.");
        return true;
    }

    private static async Task SendAsync(PhotonCadBridge bridge, string json)
    {
        using var document = JsonDocument.Parse(json);
        await bridge.HandleAsync(Text(document.RootElement, "type")!, document.RootElement);
    }

    private static JsonElement Frame(IEnumerable<JsonElement> frames, string type) =>
        frames.LastOrDefault(frame => Text(frame, "type") == type);

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool Fail(string message)
    {
        Console.Error.WriteLine(message);
        return false;
    }

    private sealed class SmokeIndustrialProvider : IPhotonCadSealedMutationProvider, IPhotonCadSealedMutationCompensator
    {
        private static readonly string ReceiptDigest = "sha256:" + new string('a', 64);
        private static readonly string ImageDigest = "sha256:" + new string('b', 64);
        private static readonly string BaseImageDigest = "sha256:" + new string('c', 64);
        private readonly PhotonCadRuntimeSyncRequest _request;
        private readonly string _entityId;

        internal SmokeIndustrialProvider(PhotonCadRuntimeSyncRequest request, string entityId)
        {
            _request = request;
            _entityId = entityId;
            OccurrenceId = entityId + ".occ";
            Glb = BuildGlb(OccurrenceId);
        }

        internal int ApplyCount { get; private set; }
        internal string OccurrenceId { get; }
        internal byte[] Glb { get; }

        public ValueTask<PhotonCadSealedMutationDelta> ApplyAsync(
            PhotonCadSealedMutationProviderRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(request.Request, _request)
                || request.Units != PhotonCadProjectUnit.Millimeter
                || request.BaseEntities.Count != 0
                || request.BaseArtifacts.Count != 0
                || request.BaseOccurrences.Count != 0)
                throw new InvalidOperationException("The industrial smoke provider received a foreign canonical base.");
            ApplyCount++;
            var source = new PhotonCadSourceIdentityV1(
                "photon-cad-industrial", "0.1.0", ImageDigest, "redistribution-blocked");
            var evidence = new PhotonCadProviderEvidence(
                PhotonCadBackendV1.Assembly,
                "photon.cad.industrial.smoke.v1",
                ["photon.cad.industrial.protocol.v1"],
                "catalog-v1",
                "industrial-bundle",
                ReceiptDigest,
                ReceiptDigest,
                ImageDigest,
                BaseImageDigest,
                source);
            var createOperationId = $"operation-{Guid.NewGuid():N}";
            var previewOperationId = $"operation-{Guid.NewGuid():N}";
            var createdAt = DateTimeOffset.UtcNow;
            var step = Encoding.ASCII.GetBytes(
                "ISO-10303-21;\nHEADER;\nFILE_DESCRIPTION(('sealed'),'2;1');\nENDSEC;\nDATA;\nENDSEC;\nEND-ISO-10303-21;\n");
            var bounds = new PhotonCadBoundsV1(new(0, 0, 0), new(10, 20, 30));
            var mutation = new PhotonCadSealedMutationDelta(
                $"mutation-{Guid.NewGuid():N}",
                _request.RequestId,
                _request.SessionId,
                _request.ProjectId,
                _request.BaseRevision,
                checked(_request.BaseRevision + 2),
                [
                    new PhotonCadAppliedOperationDelta(
                        checked(_request.BaseRevision + 1), createOperationId, _request.CapabilityId,
                        "Create industrial primitive", createdAt, _request.Mode, _request.Inputs, [_entityId], evidence),
                    new PhotonCadAppliedOperationDelta(
                        checked(_request.BaseRevision + 2), previewOperationId, "industrial.preview.glb.v1",
                        "Seal complete-project preview", createdAt.AddMilliseconds(1), _request.Mode, [], [_entityId], evidence),
                ],
                [new PhotonCadEntityV1(_entityId, null, PhotonCadEntityKindV1.Body, "Box", true, false, _request.CapabilityId)],
                [new PhotonCadOccurrenceV1(OccurrenceId, null, "BOX", _entityId,
                    new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 })],
                [],
                [],
                [
                    new PhotonCadSealedArtifactDelta(
                        PhotonCadArtifactRoleV1.AuthoritativeGeometry, PhotonCadArtifactKindV1.Step, _entityId,
                        checked(_request.BaseRevision + 1), step, step.LongLength, Digest(step), "model/step", null,
                        createOperationId, evidence),
                    new PhotonCadSealedArtifactDelta(
                        PhotonCadArtifactRoleV1.ProjectPreview, PhotonCadArtifactKindV1.Glb, null,
                        checked(_request.BaseRevision + 2), Glb, Glb.LongLength, Digest(Glb), PhotonCadPreviewContract.MediaType,
                        bounds, previewOperationId, evidence),
                ],
                occurrenceMergeMode: PhotonCadCollectionMergeMode.ReplaceAll);
            return ValueTask.FromResult(mutation);
        }

        public ValueTask CompensateAsync(
            PhotonCadSealedMutationDelta mutation,
            string reason,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        private static string Digest(ReadOnlySpan<byte> bytes) =>
            "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

        private static byte[] BuildGlb(string occurrenceId)
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(new
            {
                asset = new { version = "2.0" },
                scene = 0,
                scenes = new[] { new { nodes = new[] { 0 } } },
                nodes = new[] { new { mesh = 0, extras = new { photonEntityId = occurrenceId } } },
                meshes = new[] { new { primitives = new[] { new { attributes = new { POSITION = 0 } } } } },
                accessors = new[] { new { bufferView = 0, componentType = 5126, count = 1, type = "VEC3" } },
                bufferViews = new[] { new { buffer = 0, byteOffset = 0, byteLength = 12 } },
                buffers = new[] { new { byteLength = 12 } },
            });
            var jsonLength = (json.Length + 3) & ~3;
            const int binaryLength = 12;
            var output = new byte[12 + 8 + jsonLength + 8 + binaryLength];
            BinaryPrimitives.WriteUInt32LittleEndian(output, 0x46546C67);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4), 2);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(8), checked((uint)output.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(12), checked((uint)jsonLength));
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(16), 0x4E4F534A);
            json.CopyTo(output.AsSpan(20));
            output.AsSpan(20 + json.Length, jsonLength - json.Length).Fill(0x20);
            var binaryHeader = 20 + jsonLength;
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(binaryHeader), binaryLength);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(binaryHeader + 4), 0x004E4942);
            return output;
        }
    }

    private sealed class SmokeCadBroker : ICadRuntimeBroker, IAsyncDisposable
    {
        private readonly CadCapabilityCatalog _catalog;
        private readonly List<CadProjectEntity> _entities = [];
        private readonly List<CadProjectOperationRecord> _operations = [];
        private readonly Dictionary<string, (CadArtifactDescriptor Descriptor, byte[] Content)> _artifacts = new(StringComparer.Ordinal);
        private readonly List<SmokeArtifactEvidence> _artifactEvidence = [];
        private CadProjectHandle? _project;
        private CadSessionHandle? _session;
        private long _revision;
        private readonly List<SmokeExecutionRecord> _executionLog = [];
        internal int DescribeCount { get; private set; }
        internal int OpenCount { get; private set; }
        internal int StartCount { get; private set; }
        internal int ExecuteCount { get; private set; }
        internal int VerifyCount { get; private set; }
        internal int ReceiptCount { get; private set; }
        internal int LeaseCount { get; private set; }
        internal int CloseCount { get; private set; }
        internal int DisposeAttempts { get; private set; }
        internal bool FailNextDispose { get; set; }
        internal SmokeCadFault Fault { get; set; }
        internal bool BlockNextCreate { get; set; }
        internal TaskCompletionSource CreateStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal IReadOnlyList<SmokeExecutionRecord> ExecutionLog => _executionLog;
        internal IReadOnlyList<SmokeArtifactEvidence> ArtifactEvidence => _artifactEvidence;
        internal string? Project => _project?.Value;
        internal string? Session => _session?.Value;

        internal SmokeCadBroker()
        {
            var source = new CadSourceIdentity(
                "build123d-mcp",
                "0.3.80",
                CadDockerRuntimeIdentity.GeometrySourceArchiveSha256,
                "Apache-2.0");
            var boxParameters = new[]
            {
                new CadParameterDefinition("length", "Length", "Box length", CadParameterKind.Number, true, CadParameterUnit.Length, 0.001, 1_000_000),
                new CadParameterDefinition("width", "Width", "Box width", CadParameterKind.Number, true, CadParameterUnit.Length, 0.001, 1_000_000),
                new CadParameterDefinition("height", "Height", "Box height", CadParameterKind.Number, true, CadParameterUnit.Length, 0.001, 1_000_000),
            };
            var cylinderParameters = new[]
            {
                new CadParameterDefinition("radius", "Radius", "Cylinder radius", CadParameterKind.Number, true, CadParameterUnit.Length, 0.001, 1_000_000),
                new CadParameterDefinition("height", "Height", "Cylinder height", CadParameterKind.Number, true, CadParameterUnit.Length, 0.001, 1_000_000),
            };
            _catalog = new CadCapabilityCatalog(
                CadPinnedCapabilityCatalog.Revision,
                DateTimeOffset.UtcNow,
                [
                    new CadCapability(CadPinnedCapabilityCatalog.BoxCapabilityId, CadBackend.Geometry, "Primitive", "Box", "Create a box", CadCapabilityOperationKind.Create,
                        boxParameters, source, previewSupported: false, experimental: false),
                    new CadCapability(CadPinnedCapabilityCatalog.CylinderCapabilityId, CadBackend.Geometry, "Primitive", "Cylinder", "Create a cylinder", CadCapabilityOperationKind.Create,
                        cylinderParameters, source, previewSupported: false, experimental: false),
                    new CadCapability(CadPinnedCapabilityCatalog.ExportStepCapabilityId, CadBackend.Geometry, "Interchange", "STEP", "Export STEP", CadCapabilityOperationKind.Modify,
                        [], source, previewSupported: false, experimental: false),
                ],
                new CadCatalogCoverage(3, 3, 0));
        }

        public CadRuntimeDescription Describe()
        {
            DescribeCount++;
            return new CadRuntimeDescription(
                CadRuntimeAvailability.Ready,
                "verified_runtime_ready",
                "Smoke runtime ready.",
                new CadRuntimeBundleSetIdentity(
                    new CadBundleIdentity(
                        CadRuntimeRole.Geometry,
                        "photon-cad-geometry",
                        CadDockerRuntimeIdentity.GeometryBundleVersion,
                        "docker",
                        CadDockerRuntimeIdentity.GeometryRevision,
                        "linux-amd64",
                        PhotonCadBridge.AcceptedReceiptSha256,
                        DateTimeOffset.UtcNow),
                    new CadBundleIdentity(
                        CadRuntimeRole.Assembly,
                        "photon-cad-assembly",
                        CadDockerRuntimeIdentity.AssemblyBundleVersion,
                        "docker",
                        CadDockerRuntimeIdentity.AssemblyRevision,
                        "linux-amd64",
                        PhotonCadBridge.AcceptedReceiptSha256,
                        DateTimeOffset.UtcNow),
                    new CadRevision(1)),
                _catalog);
        }

        public ValueTask<CadResult<CadProjectDescriptor>> OpenProjectAsync(CadOpenProjectRequest request, CancellationToken cancellationToken = default)
        {
            OpenCount++;
            _project = CadProjectHandle.New();
            return ValueTask.FromResult(CadResult<CadProjectDescriptor>.Success(
                new CadProjectDescriptor(_project, request.DisplayName, new CadRevision(0), CadProjectState.Available)));
        }

        public ValueTask<CadResult<CadSessionDescriptor>> StartSessionAsync(CadStartSessionRequest request, CancellationToken cancellationToken = default)
        {
            StartCount++;
            _session = CadSessionHandle.New();
            return ValueTask.FromResult(CadResult<CadSessionDescriptor>.Success(
                new CadSessionDescriptor(_session, request.Project, CadSessionState.Ready, request.ExpectedRevision)));
        }

        public ValueTask<CadResult<CadCapabilityCatalog>> GetCatalogAsync(CadRequestId requestId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CadResult<CadCapabilityCatalog>.Success(_catalog));

        public async ValueTask<CadResult<CadOperationResult>> ExecuteAsync(CadOperationRequest request, CancellationToken cancellationToken = default)
        {
            ExecuteCount += 1;
            if (_project is null || _session is null || request.Project != _project || request.Session != _session)
                throw new InvalidOperationException("Bridge failed to use broker-issued opaque handles.");
            _executionLog.Add(new SmokeExecutionRecord(
                request.CapabilityId,
                request.BaseRevision.Value,
                request.Session.Value,
                request.Project.Value,
                request.TargetEntityIds.ToArray()));
            if (Fault == SmokeCadFault.Stale || request.BaseRevision.Value != _revision)
                return CadResult<CadOperationResult>.Success(new CadOperationResult(
                    request.RequestId,
                    request.Project,
                    request.BaseRevision,
                    request.BaseRevision,
                    CadOperationStatus.Rejected,
                    stale: true,
                    "revision_conflict"));

            var nextRevision = checked(_revision + 1);
            var artifacts = new List<CadArtifactHandle>();
            string label;
            if (request.CapabilityId is CadPinnedCapabilityCatalog.BoxCapabilityId or CadPinnedCapabilityCatalog.CylinderCapabilityId)
            {
                if (BlockNextCreate)
                {
                    BlockNextCreate = false;
                    CreateStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                if (request.TargetEntityIds.Count != 0)
                    throw new InvalidOperationException("Primitive create unexpectedly received a renderer target.");
                var id = $"runtime-entity-{_entities.Count + 1}";
                label = request.CapabilityId == CadPinnedCapabilityCatalog.BoxCapabilityId ? "Box" : "Cylinder";
                _entities.Add(new CadProjectEntity(id, null, CadEntityKind.Body, label, true, false, request.CapabilityId));
            }
            else if (request.CapabilityId == CadPinnedCapabilityCatalog.ExportStepCapabilityId)
            {
                if (request.Inputs.Count != 0 || request.TargetEntityIds.Count != 1
                    || _entities.All(entity => !StringComparer.Ordinal.Equals(entity.Id, request.TargetEntityIds[0])))
                    throw new InvalidOperationException("STEP export was not bound to one known runtime entity.");
                label = "Export STEP copy";
                var count = Fault == SmokeCadFault.ExtraArtifact ? 2 : 1;
                for (var index = 0; index < count; index++)
                {
                    var artifact = CadArtifactHandle.New();
                    var bytes = Encoding.ASCII.GetBytes("ISO-10303-21;\nHEADER;\nENDSEC;\nDATA;\n#1=PRODUCT('P','P','',());\nENDSEC;\nEND-ISO-10303-21;\n");
                    var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                    var descriptorSession = Fault == SmokeCadFault.ForeignSession ? CadSessionHandle.New() : _session;
                    var descriptorArtifact = Fault == SmokeCadFault.ForeignArtifact ? CadArtifactHandle.New() : artifact;
                    var descriptorLength = Fault switch
                    {
                        SmokeCadFault.OverCapacity => (64L * 1024 * 1024) + 1,
                        SmokeCadFault.LengthMismatch => bytes.LongLength + 1,
                        _ => bytes.LongLength,
                    };
                    var mediaType = Fault == SmokeCadFault.MediaMismatch ? "application/octet-stream" : "model/step";
                    if (Fault == SmokeCadFault.DigestMismatch) digest = new string('d', 64);
                    var descriptor = new CadArtifactDescriptor(descriptorSession, descriptorArtifact, digest, descriptorLength, mediaType);
                    _artifacts.Add(artifact.Value, (descriptor, bytes));
                    _artifactEvidence.Add(new SmokeArtifactEvidence(nextRevision, descriptor, bytes.ToArray()));
                    artifacts.Add(artifact);
                }
            }
            else
            {
                throw new InvalidOperationException("Bridge called an unapproved smoke capability.");
            }

            _operations.Add(new CadProjectOperationRecord(
                $"runtime-operation-{nextRevision}",
                request.CapabilityId,
                label,
                DateTimeOffset.UtcNow,
                CadOperationRecordState.Applied));
            _revision = nextRevision;
            var revision = new CadRevision(_revision);
            var resultProject = Fault == SmokeCadFault.ForeignProject ? CadProjectHandle.New() : _project;
            var snapshot = new CadProjectSnapshot(
                _session,
                resultProject,
                revision,
                "Photon CAD Project",
                CadLengthUnit.Millimeter,
                CadProjectMode.Scratch,
                _entities,
                _operations,
                [],
                dirty: true);
            return CadResult<CadOperationResult>.Success(new CadOperationResult(
                request.RequestId, resultProject, request.BaseRevision, revision, CadOperationStatus.Accepted, false, "accepted", artifacts: artifacts, snapshot: snapshot));
        }

        public ValueTask<CadResult<CadArtifactDescriptor>> GetArtifactReceiptAsync(
            CadArtifactReadRequest request,
            CancellationToken cancellationToken = default)
        {
            ReceiptCount++;
            if (_session is null || request.Session != _session || !_artifacts.TryGetValue(request.Artifact.Value, out var stored))
                return ValueTask.FromResult(CadResult<CadArtifactDescriptor>.Failure(
                    new CadError("artifact_not_available", "The artifact is foreign.", retryable: false)));
            return ValueTask.FromResult(CadResult<CadArtifactDescriptor>.Success(stored.Descriptor));
        }

        public ValueTask<CadResult<CadArtifactReadLease>> OpenArtifactReadAsync(
            CadArtifactReadRequest request,
            CancellationToken cancellationToken = default)
        {
            LeaseCount++;
            if (_session is null || request.Session != _session || !_artifacts.TryGetValue(request.Artifact.Value, out var stored)
                || stored.Descriptor.ByteLength != stored.Content.LongLength)
                return ValueTask.FromResult(CadResult<CadArtifactReadLease>.Failure(
                    new CadError("artifact_not_available", "The artifact lease is unavailable.", retryable: false)));
            var constructor = typeof(CadArtifactReadLease).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
            var lease = (CadArtifactReadLease)constructor.Invoke(
                [stored.Descriptor, new MemoryStream(stored.Content, writable: false), null]);
            return ValueTask.FromResult(CadResult<CadArtifactReadLease>.Success(lease));
        }

        public ValueTask<CadResult<CadVerificationResult>> VerifyAsync(CadVerificationRequest request, CancellationToken cancellationToken = default)
        {
            VerifyCount++;
            return ValueTask.FromResult(CadResult<CadVerificationResult>.Success(new CadVerificationResult(
                request.RequestId, request.Project, request.Revision, CadVerificationStatus.Passed, false, [], DateTimeOffset.UtcNow)));
        }

        public ValueTask<CadResult<CadSessionDescriptor>> CloseSessionAsync(CadCloseSessionRequest request, CancellationToken cancellationToken = default)
        {
            CloseCount++;
            if (_project is null || _session is null || request.Session != _session)
                return ValueTask.FromResult(CadResult<CadSessionDescriptor>.Failure(new CadError("foreign_session", "Foreign session.", false)));
            return ValueTask.FromResult(CadResult<CadSessionDescriptor>.Success(
                new CadSessionDescriptor(_session, _project, CadSessionState.Closed, new CadRevision(_revision))));
        }

        public ValueTask DisposeAsync()
        {
            DisposeAttempts++;
            if (FailNextDispose)
            {
                FailNextDispose = false;
                throw new IOException("Synthetic broker disposal failure.");
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed record SmokeExecutionRecord(
        string CapabilityId,
        long BaseRevision,
        string Session,
        string Project,
        IReadOnlyList<string> TargetEntityIds);

    private sealed record SmokeArtifactEvidence(long Revision, CadArtifactDescriptor Descriptor, byte[] Content);

    private enum SmokeCadFault
    {
        None,
        Stale,
        ForeignProject,
        ForeignSession,
        ForeignArtifact,
        ExtraArtifact,
        DigestMismatch,
        LengthMismatch,
        MediaMismatch,
        OverCapacity,
    }

    private sealed class FailingResetProjectHost : IPhotonCadDesktopProjectHost
    {
        internal int ResetCalls { get; private set; }
        public PhotonCadWindowsDesktopProjectReadiness Readiness { get; } = new(true, "project-host-ready");

        public ValueTask ResetAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResetCalls++;
            if (ResetCalls == 1) throw new InvalidOperationException("Synthetic project reset failure.");
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public ValueTask<PhotonCadDesktopProjectPickerOutcome> ChooseWorkspaceAsync(string requestId, string purpose, CancellationToken cancellationToken = default) => NotUsed<PhotonCadDesktopProjectPickerOutcome>();
        public ValueTask<PhotonCadProjectDocument> CreateProjectAsync(PhotonCadProjectCreateRequest request, CancellationToken cancellationToken = default) => NotUsed<PhotonCadProjectDocument>();
        public ValueTask<PhotonCadProjectDocument> OpenProjectAsync(PhotonCadProjectOpenRequest request, CancellationToken cancellationToken = default) => NotUsed<PhotonCadProjectDocument>();
        public ValueTask<PhotonCadProjectDocument> ReopenProjectAsync(PhotonCadProjectReopenRequest request, CancellationToken cancellationToken = default) => NotUsed<PhotonCadProjectDocument>();
        public ValueTask<PhotonCadProjectDocument> RefreshProjectAsync(PhotonCadProjectRefreshRequest request, CancellationToken cancellationToken = default) => NotUsed<PhotonCadProjectDocument>();
        public ValueTask<PhotonCadProjectSaveOutcome> SaveProjectAsync(PhotonCadProjectSaveRequest request, CancellationToken cancellationToken = default) => NotUsed<PhotonCadProjectSaveOutcome>();
        public ValueTask<PhotonCadProjectSaveOutcome> SaveProjectAsAsync(PhotonCadProjectSaveAsRequest request, CancellationToken cancellationToken = default) => NotUsed<PhotonCadProjectSaveOutcome>();
        public ValueTask<PhotonCadProjectCloseOutcome> CloseProjectAsync(PhotonCadProjectCloseRequest request, CancellationToken cancellationToken = default) => NotUsed<PhotonCadProjectCloseOutcome>();

        private static ValueTask<T> NotUsed<T>() => ValueTask.FromException<T>(new InvalidOperationException("Not used by reset smoke."));
    }
}
