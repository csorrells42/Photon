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
using PhotonCadRuntime.IndustrialProvider;
using PhotonCadRuntime.ManualProvider;

internal static class PhotonCadBridgeSmoke
{
    private const string ImportedStepCapabilityId = "external.step.import.v1";
    private const string ImportedStepAssemblyCapabilityId = "external.step.assembly.import.v1";

    internal static async Task<bool> RunLiveIndustrialAsync(string installRoot)
    {
        EnsureRuntimeSyncLoadedForSmoke();
        var exactInstallRoot = Path.GetFullPath(installRoot);
        var tempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"photon-cad-bridge-smoke-live-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(tempRoot);
        PhotonCadBridge? bridge = null;
        try
        {
            var projectPath = Path.Combine(tempRoot, "live-industrial.photoncad");
            var frames = new List<JsonElement>();
            var origin = new Uri("https://127.0.0.1:4173/", UriKind.Absolute);
            bridge = new PhotonCadBridge(
                exactInstallRoot,
                message => frames.Add(JsonSerializer.SerializeToElement(message)),
                projectDialog: new SmokeProjectDialog(projectPath),
                workbenchOrigin: origin);

            var epoch = await bridge.ResetAsync();
            if (!bridge.TryOpenRendererGeneration(epoch)) return Fail("Live industrial bridge generation did not open.");
            await LivePhaseAsync("describe", () => SendAsync(bridge,
                """{"type":"photonCad.describe","version":1,"contractVersion":1,"requestId":"live-industrial-describe"}"""),
                LiveControlPhaseTimeout);
            var described = Frame(frames, "photonCad.describe.result").GetProperty("value");
            if (Text(described, "status") != "available" || Text(described, "reason") != "ready")
                return Fail("Live industrial provider did not expose its exact ready catalog.");

            var capabilities = described.GetProperty("catalog").GetProperty("capabilities").EnumerateArray().ToArray();
            var catalogFamilies = capabilities
                .GroupBy(capability => Text(capability, "category") ?? "unknown", StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => $"{group.Key}={group.Count()}");
            Console.WriteLine($"Photon CAD LIVE catalog exposed {capabilities.Length} verified capabilities: {string.Join(", ", catalogFamilies)}.");
            var bearings = capabilities
                .Where(capability => Text(capability, "category") == "Industrial bearings")
                .OrderBy(capability => Text(capability, "id"), StringComparer.Ordinal)
                .ToArray();
            var gears = capabilities.Where(capability => Text(capability, "title") == "Spur Gear").ToArray();
            var bearing = bearings.FirstOrDefault(capability =>
                LiveIndustrialCatalogCapability(capability) && TryBuildLiveBearingInputs(capability, out _));
            if (!capabilities.Any(capability => Text(capability, "id") == CadPinnedCapabilityCatalog.BoxCapabilityId)
                || !capabilities.Any(capability => Text(capability, "id") == CadPinnedCapabilityCatalog.CylinderCapabilityId)
                || bearing.ValueKind != JsonValueKind.Object
                || gears.Length != 1
                || !LiveIndustrialCatalogCapability(bearing)
                || !LiveIndustrialCatalogCapability(gears[0])
                || !TryBuildLiveBearingInputs(bearing, out var bearingInputs)
                || !TryBuildLiveSpurGearInputs(gears[0], out var gearInputs))
                return Fail("Live industrial catalog did not expose one safely bound bearing and one exact Spur Gear.");

            var bearingCapabilityId = Text(bearing, "id")!;
            var bearingTitle = Text(bearing, "title")!;
            var gearCapabilityId = Text(gears[0], "id")!;

            var identity = await LivePhaseAsync("create-project",
                () => CreateCanonicalProjectAsync(bridge, frames, "live-industrial"),
                LiveControlPhaseTimeout);
            var createdDocument = Frame(frames, "photonCad.project.create.result").GetProperty("value").GetProperty("document").Clone();
            var bearingResult = await LivePhaseAsync("bearing-execute", () => ExecuteLiveCatalogAsync(
                    bridge, frames, "live-bearing", identity.SessionId, identity.ProjectId, 0,
                    bearingCapabilityId, bearingInputs, expectedRevision: 2, expectedPreviewEntities: 1),
                LiveCadPhaseTimeout);
            var bearingOccurrences = SnapshotOccurrenceIds(bearingResult);
            var bearingPreview = await LivePhaseAsync("bearing-preview-resolve-read", () => ReadLivePreviewAsync(
                    bridge, frames, identity, bearingResult.GetProperty("preview"), bearingOccurrences, "live-bearing-preview"),
                LiveControlPhaseTimeout);
            if (bearingPreview is null) return false;

            var gearResult = await LivePhaseAsync("gear-execute", () => ExecuteLiveCatalogAsync(
                    bridge, frames, "live-spur-gear", identity.SessionId, identity.ProjectId, 2,
                    gearCapabilityId, gearInputs, expectedRevision: 4, expectedPreviewEntities: 2),
                LiveCadPhaseTimeout);
            var gearOccurrences = SnapshotOccurrenceIds(gearResult);
            var gearPreview = await LivePhaseAsync("gear-preview-resolve-read", () => ReadLivePreviewAsync(
                    bridge, frames, identity, gearResult.GetProperty("preview"), gearOccurrences, "live-spur-gear-preview"),
                LiveControlPhaseTimeout);
            if (gearPreview is null || StringComparer.Ordinal.Equals(bearingPreview.ContentDigest, gearPreview.ContentDigest))
                return Fail("Live industrial complete-project GLB did not replace the bearing-only preview.");

            var projectHandle = Text(createdDocument, "projectHandle")!;
            await LivePhaseAsync("refresh", () => SendFrameAsync(bridge, new
            {
                type = "photonCad.project.refresh",
                version = 1,
                contractVersion = 1,
                requestId = "live-industrial-refresh",
                projectHandle,
                sessionId = identity.SessionId,
                projectId = identity.ProjectId,
                knownRevision = 4,
            }), LiveControlPhaseTimeout);
            var refreshed = Frame(frames, "photonCad.project.refresh.result").GetProperty("value").GetProperty("document").Clone();
            await LivePhaseAsync("close", () => SendFrameAsync(bridge, new
            {
                type = "photonCad.project.close",
                version = 1,
                contractVersion = 1,
                requestId = "live-industrial-close",
                projectHandle,
                sessionId = identity.SessionId,
                projectId = identity.ProjectId,
                revision = 4,
                lastSavedRevision = refreshed.GetProperty("lastSavedRevision").GetInt64(),
                contentDigest = Text(refreshed, "contentDigest"),
                lastSavedContentDigest = Text(refreshed, "lastSavedContentDigest"),
                discardUnsavedChanges = false,
            }), LiveControlPhaseTimeout);
            var closed = Frame(frames, "photonCad.project.close.result").GetProperty("value");
            if (Text(closed, "status") != "closed") return Fail("Live industrial clean close was not accepted.");
            await LivePhaseAsync("reopen", () => SendFrameAsync(bridge, new
            {
                type = "photonCad.project.reopen",
                version = 1,
                contractVersion = 1,
                requestId = "live-industrial-reopen",
                reopenHandle = Text(closed.GetProperty("reopen"), "reopenHandle"),
            }), LiveControlPhaseTimeout);
            var reopenedDocument = Frame(frames, "photonCad.project.reopen.result").GetProperty("value").GetProperty("document");
            var reopenedSnapshot = reopenedDocument.GetProperty("snapshot");
            if (reopenedSnapshot.GetProperty("revision").GetInt64() != 4
                || reopenedSnapshot.GetProperty("dirty").GetBoolean()
                || reopenedDocument.GetProperty("lastSavedRevision").GetInt64() != 4
                || !StringComparer.Ordinal.Equals(Text(reopenedDocument, "contentDigest"), Text(refreshed, "contentDigest"))
                || !StringComparer.Ordinal.Equals(Text(reopenedDocument, "lastSavedContentDigest"), Text(refreshed, "lastSavedContentDigest")))
                return Fail("Live industrial project did not reopen as the exact clean saved revision.");

            await LivePhaseAsync("committed-verification", () => SendFrameAsync(bridge, new
            {
                type = "photonCad.verify",
                version = 1,
                contractVersion = 1,
                requestId = "live-industrial-verify",
                sessionId = identity.SessionId,
                projectId = identity.ProjectId,
                revision = 4,
                checks = new[] { "valid-solids", "dimensions", "assembly-structure", "export-readiness" },
            }), LiveCadPhaseTimeout);
            var verification = Frame(frames, "photonCad.verify.result", "live-industrial-verify").GetProperty("value");
            if (Text(verification, "status") != "passed"
                || verification.GetProperty("revision").GetInt64() != 4
                || verification.GetProperty("stale").GetBoolean())
                return Fail("Live reopened bearing and Spur Gear did not pass exact committed-project verification.");

            var codec = new PhotonCadCanonicalProjectCodecV1();
            var reopened = await LivePhaseAsync("canonical-decode", async () =>
                codec.Decode(await File.ReadAllBytesAsync(projectPath)), LiveControlPhaseTimeout);
            var state = codec.Inspect(reopened);
            if (reopened.Dirty || reopened.Revision != 4
                || state.Entities.Count != 2
                || state.Occurrences.Count != 2
                || state.Bom.Count != 2
                || state.Operations.Count != 4
                || state.Artifacts.Count(value => value.Role == PhotonCadArtifactRoleV1.AuthoritativeGeometry) != 2
                || state.Artifacts.Count(value => value.Role == PhotonCadArtifactRoleV1.ProjectPreview) != 1
                || !state.Entities.Select(value => value.SourceCapabilityId).ToHashSet(StringComparer.Ordinal)
                    .SetEquals([bearingCapabilityId, gearCapabilityId])
                || !state.Bom.Select(value => value.SourceEntityId).ToHashSet(StringComparer.Ordinal)
                    .SetEquals(state.Entities.Select(value => value.Id))
                || state.Bom.Any(value => value.Quantity != 1 || value.Unit != PhotonCadBomUnit.Each)
                || state.Bom.Select(value => value.PartNumber).Distinct(StringComparer.Ordinal).Count() != 2
                || state.Entities.Single(value => value.SourceCapabilityId == bearingCapabilityId).Name != bearingTitle
                || state.Entities.Single(value => value.SourceCapabilityId == gearCapabilityId).Name != "Spur Gear")
                return Fail("Live industrial project did not reopen as exact clean revision four with complete geometry and preview state.");

            var bearingEntity = state.Entities.Single(value => value.SourceCapabilityId == bearingCapabilityId);
            var gearEntity = state.Entities.Single(value => value.SourceCapabilityId == gearCapabilityId);
            if (state.Bom.Single(value => value.SourceEntityId == bearingEntity.Id).Description != $"Create {bearingTitle}"
                || state.Bom.Single(value => value.SourceEntityId == gearEntity.Id).Description != "Create Spur Gear"
                || state.Operations[0].CapabilityId != bearingCapabilityId
                || state.Operations[1].CapabilityId != "industrial.preview.glb.v1"
                || state.Operations[2].CapabilityId != gearCapabilityId
                || state.Operations[3].CapabilityId != "industrial.preview.glb.v1"
                || !state.Operations[0].TargetEntityIds.SequenceEqual([bearingEntity.Id], StringComparer.Ordinal)
                || !state.Operations[2].TargetEntityIds.SequenceEqual([gearEntity.Id], StringComparer.Ordinal))
                return Fail("Live industrial bearing, Spur Gear, BOM, and operation identities drifted.");

            var steps = state.Artifacts
                .Where(value => value.Role == PhotonCadArtifactRoleV1.AuthoritativeGeometry)
                .OrderBy(value => value.Revision)
                .ToArray();
            var entityIds = state.Entities.Select(value => value.Id).ToHashSet(StringComparer.Ordinal);
            if (!steps.Select(value => value.OwnerEntityId!).ToHashSet(StringComparer.Ordinal).SetEquals(entityIds)
                || !steps.Select(value => value.Revision).SequenceEqual([1L, 3L])
                || steps.Any(value => value.Kind != PhotonCadArtifactKindV1.Step
                    || value.Bounds is not null
                    || value.ByteLength != value.Content.Length
                    || !StringComparer.Ordinal.Equals(value.Digest, Sha256(value.Content.Span))
                    || !ValidPart21Envelope(value.Content.Span)
                    || !LiveIndustrialProvenance(value.Provenance)))
                return Fail("Live industrial STEP artifacts lost their exact owner, revision, bytes, digest, envelope, or image provenance.");

            var persistedPreview = state.Artifacts.Single(value => value.Role == PhotonCadArtifactRoleV1.ProjectPreview);
            if (persistedPreview.Kind != PhotonCadArtifactKindV1.Glb
                || persistedPreview.OwnerEntityId is not null
                || persistedPreview.Revision != 4
                || persistedPreview.Bounds is null
                || !StringComparer.Ordinal.Equals(persistedPreview.Digest, gearPreview.ContentDigest)
                || !persistedPreview.Content.Span.SequenceEqual(gearPreview.Content)
                || !LiveIndustrialProvenance(persistedPreview.Provenance))
                return Fail("Live industrial GLB replacement was not the exact committed complete-project preview.");

            var serializedFrames = JsonSerializer.Serialize(frames);
            if (serializedFrames.Contains(projectPath, StringComparison.OrdinalIgnoreCase)
                || serializedFrames.Contains(tempRoot, StringComparison.OrdinalIgnoreCase)
                || serializedFrames.Contains("C:\\", StringComparison.OrdinalIgnoreCase))
                return Fail("Live industrial renderer frames exposed a native path.");

            Console.WriteLine("Desktop Photon CAD LIVE bearing and Spur Gear exact-image 0-to-2-to-4 persistence, reopen, BOM, STEP, and GLB replacement passed.");
            if (!await RunLiveStepImportAsync(exactInstallRoot, tempRoot, steps[0].Content.ToArray())) return false;
            if (!await RunLiveManualAsync(exactInstallRoot)) return false;
            return true;
        }
        finally
        {
            try
            {
                if (bridge is not null)
                {
                    LivePhase("disposal", "start");
                    try { await bridge.DisposeAsync(); }
                    finally { LivePhase("disposal", "end"); }
                }
            }
            finally { DeleteOwnedSmokeRoot(tempRoot); }
        }
    }

    private static readonly TimeSpan LiveControlPhaseTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan LiveCadPhaseTimeout = TimeSpan.FromMinutes(8);

    private static async Task LivePhaseAsync(string phase, Func<Task> action, TimeSpan timeout)
    {
        LivePhase(phase, "start");
        try { await action().WaitAsync(timeout); }
        finally { LivePhase(phase, "end"); }
    }

    private static async Task<T> LivePhaseAsync<T>(string phase, Func<Task<T>> action, TimeSpan timeout)
    {
        LivePhase(phase, "start");
        try { return await action().WaitAsync(timeout); }
        finally { LivePhase(phase, "end"); }
    }

    private static void LivePhase(string phase, string boundary)
    {
        Console.WriteLine($"{DateTimeOffset.UtcNow:O} Photon CAD LIVE phase {phase} {boundary}.");
        Console.Out.Flush();
    }

    private static async Task<JsonElement> ExecuteLiveCatalogAsync(
        PhotonCadBridge bridge,
        List<JsonElement> frames,
        string requestId,
        string sessionId,
        string projectId,
        long baseRevision,
        string capabilityId,
        IReadOnlyDictionary<string, object?> inputs,
        long expectedRevision,
        int expectedPreviewEntities,
        IReadOnlyList<string>? targetEntityIds = null)
    {
        var frameCountBeforeExecute = frames.Count;
        await SendFrameAsync(bridge, new
        {
            type = "photonCad.execute",
            version = 1,
            contractVersion = 1,
            requestId,
            sessionId,
            projectId,
            baseRevision,
            mode = "scratch",
            capabilityId,
            inputs,
            targetEntityIds = targetEntityIds ?? Array.Empty<string>(),
        });
        var emitted = frames.Skip(frameCountBeforeExecute).ToArray();
        var result = emitted.LastOrDefault(frame =>
            Text(frame, "type") == "photonCad.execute.result"
            && frame.TryGetProperty("value", out var candidate)
            && Text(candidate, "requestId") == requestId);
        if (result.ValueKind != JsonValueKind.Object)
        {
            var errorCode = emitted
                .Where(frame => Text(frame, "type") == "photonCad.error" && Text(frame, "requestId") == requestId)
                .Select(frame => Text(frame, "code"))
                .FirstOrDefault(code => code is not null);
            var unavailableCode = emitted
                .Where(frame => Text(frame, "type") == "photonCad.execute.result"
                    && frame.TryGetProperty("value", out var candidate)
                    && Text(candidate, "requestId") == requestId
                    && Text(candidate, "status") == "unavailable")
                .Select(frame => Text(frame.GetProperty("value"), "reason"))
                .FirstOrDefault(code => code is not null);
            var diagnostic = BoundedSafeCode(errorCode ?? unavailableCode);
            throw new InvalidOperationException($"Live industrial {capabilityId} emitted no correlated execute result ({diagnostic}).");
        }
        var value = result.GetProperty("value");
        if (Text(value, "status") == "unavailable")
            throw new InvalidOperationException($"Live industrial {capabilityId} was unavailable ({BoundedSafeCode(Text(value, "reason"))}).");
        if (Text(value, "status") != "accepted"
            || value.GetProperty("stale").GetBoolean()
            || value.GetProperty("baseRevision").GetInt64() != baseRevision
            || value.GetProperty("resultingRevision").GetInt64() != expectedRevision
            || !value.TryGetProperty("preview", out var preview)
            || preview.GetProperty("entityCount").GetInt32() != expectedPreviewEntities)
            throw new InvalidOperationException($"Live industrial {capabilityId} did not return the exact committed preview result.");
        return value.Clone();

        static string BoundedSafeCode(string? value) =>
            value is { Length: > 0 and <= 128 }
            && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-')
                ? value
                : "missing-safe-code";
    }

    private static async Task<bool> RunLiveManualAsync(string exactInstallRoot)
    {
        var tempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"photon-cad-bridge-smoke-manual-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(tempRoot);
        var projectPath = Path.Combine(tempRoot, "live-manual.photoncad");
        var frames = new List<JsonElement>();
        PhotonCadBridge? bridge = null;
        try
        {
            bridge = new PhotonCadBridge(
                exactInstallRoot,
                message => frames.Add(JsonSerializer.SerializeToElement(message)),
                projectDialog: new SmokeProjectDialog(projectPath),
                workbenchOrigin: new Uri("https://127.0.0.1:4173/", UriKind.Absolute));
            var epoch = await bridge.ResetAsync();
            if (!bridge.TryOpenRendererGeneration(epoch)) return Fail("Live manual bridge generation did not open.");

            await LivePhaseAsync("manual-describe", () => SendAsync(bridge,
                """{"type":"photonCad.describe","version":1,"contractVersion":1,"requestId":"live-manual-describe"}"""),
                LiveControlPhaseTimeout);
            var describedFrame = Frame(frames, "photonCad.describe.result");
            if (Text(describedFrame, "requestId") != "live-manual-describe")
                return Fail("Live manual describe was not correlated to the exact request.");
            var described = describedFrame.GetProperty("value");
            var capabilityIds = described.GetProperty("catalog").GetProperty("capabilities").EnumerateArray()
                .Select(value => Text(value, "id"))
                .Where(value => value is not null)
                .ToHashSet(StringComparer.Ordinal);
            if (!capabilityIds.Contains(PhotonCadManualCapabilityIds.SketchExtrudeAdd)
                || !capabilityIds.Contains(PhotonCadManualCapabilityIds.SketchExtrudeCut)
                || !capabilityIds.Contains(PhotonCadManualCapabilityIds.HoleCut))
                return Fail("Live manual runtime did not advertise its three installed operations.");

            var identity = await LivePhaseAsync("manual-create-project",
                () => CreateCanonicalProjectAsync(bridge, frames, "live-manual"), LiveControlPhaseTimeout);
            var createdDocument = Frame(frames, "photonCad.project.create.result", "create-live-manual")
                .GetProperty("value").GetProperty("document").Clone();

            var add = await LivePhaseAsync("manual-extrude-add", () => ExecuteLiveCatalogAsync(
                    bridge, frames, "live-manual-add", identity.SessionId, identity.ProjectId, 0,
                    PhotonCadManualCapabilityIds.SketchExtrudeAdd,
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["profileKind"] = "rectangle",
                        ["sketchPlane"] = "xy",
                        ["profileWidthMm"] = 40d,
                        ["profileHeightMm"] = 30d,
                        ["profileRadiusMm"] = null,
                        ["extrusionDepthMm"] = 12d,
                    }, expectedRevision: 2, expectedPreviewEntities: 1), LiveCadPhaseTimeout);
            var bodyId = add.GetProperty("snapshot").GetProperty("entities").EnumerateArray()
                .Single(value => Text(value, "kind") == "body" && Text(value, "sourceCapabilityId") == PhotonCadManualCapabilityIds.SketchExtrudeAdd)
                .GetProperty("id").GetString()!;

            await LivePhaseAsync("manual-sketch-cut", () => ExecuteLiveCatalogAsync(
                    bridge, frames, "live-manual-cut", identity.SessionId, identity.ProjectId, 2,
                    PhotonCadManualCapabilityIds.SketchExtrudeCut,
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["sketchPlane"] = "xy",
                        ["profileWidthMm"] = 10d,
                        ["profileHeightMm"] = 8d,
                        ["cutDepthMm"] = 6d,
                    }, expectedRevision: 4, expectedPreviewEntities: 1, [bodyId]), LiveCadPhaseTimeout);

            var hole = await LivePhaseAsync("manual-hole-cut", () => ExecuteLiveCatalogAsync(
                    bridge, frames, "live-manual-hole", identity.SessionId, identity.ProjectId, 4,
                    PhotonCadManualCapabilityIds.HoleCut,
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["diameterMm"] = 4d,
                        ["depthMm"] = 12d,
                        ["xMm"] = 12d,
                        ["yMm"] = 8d,
                        ["zMm"] = 0d,
                    }, expectedRevision: 6, expectedPreviewEntities: 1, [bodyId]), LiveCadPhaseTimeout);
            var manualOccurrences = SnapshotOccurrenceIds(hole);
            var manualPreview = await LivePhaseAsync("manual-preview-resolve-read", () => ReadLivePreviewAsync(
                    bridge, frames, identity, hole.GetProperty("preview"), manualOccurrences, "live-manual-preview"),
                LiveControlPhaseTimeout);
            if (manualPreview is null) return false;

            var projectHandle = Text(createdDocument, "projectHandle")!;
            await SendFrameAsync(bridge, new
            {
                type = "photonCad.project.refresh", version = 1, contractVersion = 1,
                requestId = "live-manual-refresh", projectHandle,
                sessionId = identity.SessionId, projectId = identity.ProjectId, knownRevision = 6,
            });
            var refreshed = Frame(frames, "photonCad.project.refresh.result", "live-manual-refresh")
                .GetProperty("value").GetProperty("document").Clone();
            await SendFrameAsync(bridge, new
            {
                type = "photonCad.project.close", version = 1, contractVersion = 1,
                requestId = "live-manual-close", projectHandle,
                sessionId = identity.SessionId, projectId = identity.ProjectId, revision = 6,
                lastSavedRevision = refreshed.GetProperty("lastSavedRevision").GetInt64(),
                contentDigest = Text(refreshed, "contentDigest"),
                lastSavedContentDigest = Text(refreshed, "lastSavedContentDigest"),
                discardUnsavedChanges = false,
            });
            var closed = Frame(frames, "photonCad.project.close.result", "live-manual-close").GetProperty("value");
            await SendFrameAsync(bridge, new
            {
                type = "photonCad.project.reopen", version = 1, contractVersion = 1,
                requestId = "live-manual-reopen", reopenHandle = Text(closed.GetProperty("reopen"), "reopenHandle"),
            });
            var reopened = Frame(frames, "photonCad.project.reopen.result", "live-manual-reopen")
                .GetProperty("value").GetProperty("document").GetProperty("snapshot");
            if (reopened.GetProperty("revision").GetInt64() != 6 || reopened.GetProperty("dirty").GetBoolean())
                return Fail("Live manual project did not reopen at exact clean revision six.");

            var codec = new PhotonCadCanonicalProjectCodecV1();
            var canonical = codec.Decode(await File.ReadAllBytesAsync(projectPath));
            var state = codec.Inspect(canonical);
            var geometry = state.Artifacts.Single(value => value.Role == PhotonCadArtifactRoleV1.AuthoritativeGeometry);
            var preview = state.Artifacts.Single(value => value.Role == PhotonCadArtifactRoleV1.ProjectPreview);
            if (canonical.Revision != 6 || canonical.Dirty
                || state.Entities.Count != 1 || state.Occurrences.Count != 1 || state.Bom.Count != 1
                || state.Operations.Count != 6
                || !state.Operations.Select(value => value.CapabilityId).SequenceEqual([
                    PhotonCadManualCapabilityIds.SketchExtrudeAdd, "industrial.preview.glb.v1",
                    PhotonCadManualCapabilityIds.SketchExtrudeCut, "industrial.preview.glb.v1",
                    PhotonCadManualCapabilityIds.HoleCut, "industrial.preview.glb.v1"], StringComparer.Ordinal)
                || geometry.OwnerEntityId != bodyId || geometry.Revision != 5 || geometry.Bounds is not null
                || !ValidPart21Envelope(geometry.Content.Span)
                || preview.Revision != 6 || !preview.Content.Span.SequenceEqual(manualPreview.Content))
                return Fail("Live manual extrude/cut/hole project lost its canonical geometry, preview, or operation chain.");

            Console.WriteLine("Desktop Photon CAD LIVE manual sketch/extrude, sketch cut, and hole 0-to-2-to-4-to-6 persistence, preview, and reopen passed.");
            return true;
        }
        finally
        {
            if (bridge is not null) await bridge.DisposeAsync();
            DeleteOwnedSmokeRoot(tempRoot);
        }
    }

    private static async Task<bool> RunLiveStepImportAsync(
        string exactInstallRoot,
        string tempRoot,
        byte[] sourceStep)
    {
        var sourcePath = Path.Combine(tempRoot, "live-import-source.step");
        var projectPath = Path.Combine(tempRoot, "live-imported.photoncad");
        await File.WriteAllBytesAsync(sourcePath, sourceStep);
        var frames = new List<JsonElement>();
        await using var bridge = new PhotonCadBridge(
            exactInstallRoot,
            message => frames.Add(JsonSerializer.SerializeToElement(message)),
            projectDialog: new SmokeProjectDialog(projectPath, sourcePath),
            workbenchOrigin: new Uri("https://127.0.0.1:4173/", UriKind.Absolute));
        var epoch = await bridge.ResetAsync();
        if (!bridge.TryOpenRendererGeneration(epoch)) return Fail("Live STEP import bridge generation did not open.");

        await LivePhaseAsync("step-import-commit", () => SendFrameAsync(bridge, new
        {
            type = "photonCad.step.import",
            version = 1,
            contractVersion = 1,
            requestId = "live-step-import",
        }), LiveCadPhaseTimeout);
        var imported = Frame(frames, "photonCad.step.import.result", "live-step-import").GetProperty("value");
        if (Text(imported, "status") != "opened" || Text(imported, "reason") != "step-import-committed"
            || !imported.TryGetProperty("document", out var document))
            return Fail("Live STEP import did not return an exact committed project document.");
        var snapshot = document.GetProperty("snapshot");
        var sessionId = Text(snapshot, "sessionId")!;
        var projectId = Text(snapshot, "projectId")!;
        var projectedEntities = snapshot.GetProperty("entities").EnumerateArray().ToArray();
        var importedDefinitions = projectedEntities
            .Where(value => Text(value, "kind") is "part" or "assembly")
            .ToArray();
        var assemblyImport = importedDefinitions.Any(value => Text(value, "kind") == "assembly");
        var expectedCapability = assemblyImport ? ImportedStepAssemblyCapabilityId : ImportedStepCapabilityId;
        var occurrenceIds = projectedEntities
            .Where(value => Text(value, "kind") == "occurrence")
            .Select(value => Text(value, "id")!)
            .ToHashSet(StringComparer.Ordinal);
        if (snapshot.GetProperty("revision").GetInt64() != 2 || snapshot.GetProperty("dirty").GetBoolean()
            || importedDefinitions.Length < 1
            || importedDefinitions.Any(value => Text(value, "sourceCapabilityId") != expectedCapability)
            || occurrenceIds.Count != importedDefinitions.Length
            || assemblyImport && importedDefinitions.Count(value => Text(value, "kind") == "assembly") < 1
            || !assemblyImport && (importedDefinitions.Length != 1 || occurrenceIds.Count != 1)
            || document.GetProperty("lastSavedRevision").GetInt64() != 2)
            return Fail("Live STEP import did not publish its complete clean canonical definition and occurrence hierarchy at revision two.");

        await LivePhaseAsync("step-import-preview-hydrate", () => SendFrameAsync(bridge, new
        {
            type = "photonCad.preview.hydrate",
            version = 1,
            contractVersion = 1,
            requestId = "live-step-import-hydrate",
            sessionId,
            projectId,
            revision = 2,
        }), LiveControlPhaseTimeout);
        var hydration = Frame(frames, "photonCad.preview.hydrate.result", "live-step-import-hydrate").GetProperty("value");
        if (Text(hydration, "status") != "available" || !hydration.TryGetProperty("preview", out var preview))
            return Fail("Live STEP import did not hydrate its committed preview.");
        var previewEvidence = await LivePhaseAsync("step-import-preview-resolve-read", () => ReadLivePreviewAsync(
            bridge, frames, (sessionId, projectId), preview, occurrenceIds, "live-step-import-preview"), LiveControlPhaseTimeout);
        if (previewEvidence is null) return false;

        var projectHandle = Text(document, "projectHandle")!;
        await SendFrameAsync(bridge, new
        {
            type = "photonCad.project.close", version = 1, contractVersion = 1,
            requestId = "live-step-import-close", projectHandle, sessionId, projectId, revision = 2,
            lastSavedRevision = 2,
            contentDigest = Text(document, "contentDigest"),
            lastSavedContentDigest = Text(document, "lastSavedContentDigest"),
            discardUnsavedChanges = false,
        });
        var closed = Frame(frames, "photonCad.project.close.result", "live-step-import-close").GetProperty("value");
        await SendFrameAsync(bridge, new
        {
            type = "photonCad.project.reopen", version = 1, contractVersion = 1,
            requestId = "live-step-import-reopen", reopenHandle = Text(closed.GetProperty("reopen"), "reopenHandle"),
        });
        var reopened = Frame(frames, "photonCad.project.reopen.result", "live-step-import-reopen")
            .GetProperty("value").GetProperty("document").GetProperty("snapshot");
        if (reopened.GetProperty("revision").GetInt64() != 2 || reopened.GetProperty("dirty").GetBoolean())
            return Fail("Live STEP import did not reopen as the exact clean saved revision.");

        var codec = new PhotonCadCanonicalProjectCodecV1();
        var canonical = codec.Decode(await File.ReadAllBytesAsync(projectPath));
        var state = codec.Inspect(canonical);
        var geometries = state.Artifacts.Where(value => value.Role == PhotonCadArtifactRoleV1.AuthoritativeGeometry).ToArray();
        var originalGeometry = geometries.SingleOrDefault(value => value.Content.Span.SequenceEqual(sourceStep));
        var persistedPreview = state.Artifacts.Single(value => value.Role == PhotonCadArtifactRoleV1.ProjectPreview);
        if (canonical.Revision != 2 || canonical.Dirty
            || state.Entities.Count != importedDefinitions.Length || state.Occurrences.Count != occurrenceIds.Count
            || state.Bom.Count != state.Entities.Count(value => value.Kind == PhotonCadEntityKindV1.Part)
            || state.Operations.Count != 2 || state.Operations[0].CapabilityId != expectedCapability
            || state.Operations[1].CapabilityId != "industrial.preview.glb.v1"
            || originalGeometry is null || originalGeometry.Revision != 1 || originalGeometry.Bounds is not null
            || originalGeometry.Provenance.Source.Package != "user-supplied-step"
            || geometries.Any(value => value.Bounds is not null || !ValidPart21Envelope(value.Content.Span))
            || persistedPreview.Revision != 2 || !persistedPreview.Content.Span.SequenceEqual(previewEvidence.Content)
            || !LiveIndustrialProvenance(persistedPreview.Provenance))
            return Fail("Live STEP import lost its byte-exact STEP, canonical identities, or complete preview during save/reopen.");
        var serializedFrames = JsonSerializer.Serialize(frames);
        if (serializedFrames.Contains(sourcePath, StringComparison.OrdinalIgnoreCase)
            || serializedFrames.Contains(projectPath, StringComparison.OrdinalIgnoreCase)
            || serializedFrames.Contains(tempRoot, StringComparison.OrdinalIgnoreCase))
            return Fail("Live STEP import exposed a native path to the renderer.");

        Console.WriteLine($"Desktop Photon CAD LIVE byte-exact STEP {(assemblyImport ? "assembly hierarchy" : "part")} import, canonical revision-two save, preview, and reopen passed.");
        return true;
    }

    private static async Task<LivePreviewEvidence?> ReadLivePreviewAsync(
        PhotonCadBridge bridge,
        List<JsonElement> frames,
        (string SessionId, string ProjectId) identity,
        JsonElement preview,
        IReadOnlySet<string> expectedEntityTags,
        string requestId)
    {
        var revision = preview.GetProperty("revision").GetInt64();
        var previewId = Text(preview, "previewId")!;
        var digest = Text(preview, "contentDigest")!;
        await SendAsync(bridge, $$"""
            {"type":"photonCad.preview.resolve","version":1,"contractVersion":1,"requestId":"{{requestId}}","sessionId":"{{identity.SessionId}}","projectId":"{{identity.ProjectId}}","revision":{{revision}},"previewId":"{{previewId}}","expectedDigest":"{{digest}}","maximumBytes":134217728}
            """);
        var resolved = Frame(frames, "photonCad.preview.resolve.result").GetProperty("value");
        if (Text(resolved, "status") != "available"
            || Text(resolved, "contentDigest") != digest
            || Text(resolved, "mediaType") != PhotonCadPreviewContract.MediaType)
        {
            Fail("Live industrial committed preview did not resolve through its exact receipt.");
            return null;
        }
        var url = new Uri(Text(resolved, "url")!, UriKind.Absolute);
        using var response = bridge.TryRespondPreviewResource("GET", url, true)!;
        if (response.StatusCode != 200 || response.Content is null
            || response.Headers["Content-Type"] != PhotonCadPreviewContract.MediaType
            || response.Headers["Cache-Control"] != "no-store")
        {
            Fail("Live industrial preview responder did not return the authorized GLB.");
            return null;
        }
        using var memory = new MemoryStream();
        await response.Content.CopyToAsync(memory);
        var bytes = memory.ToArray();
        var tags = GlbEntityTags(bytes);
        using var replay = bridge.TryRespondPreviewResource("GET", url, true)!;
        if (bytes.Length < 20
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0x46546C67
            || bytes.LongLength != resolved.GetProperty("byteLength").GetInt64()
            || !StringComparer.Ordinal.Equals(Sha256(bytes), digest)
            || !tags.SetEquals(expectedEntityTags)
            || replay.StatusCode != 404)
        {
            Fail("Live industrial preview bytes, digest, entity tags, or one-use resource policy drifted.");
            return null;
        }
        return new LivePreviewEvidence(digest, bytes, tags);
    }

    private static bool LiveIndustrialCatalogCapability(JsonElement capability)
    {
        if (Text(capability, "backend") != "assembly"
            || Text(capability, "operation") != "create"
            || !capability.TryGetProperty("previewSupported", out var previewSupported)
            || previewSupported.ValueKind != JsonValueKind.True
            || !capability.TryGetProperty("experimental", out var experimental)
            || experimental.ValueKind != JsonValueKind.False
            || !capability.TryGetProperty("source", out var source))
            return false;
        return Text(source, "package") == "bd-warehouse"
            && Text(source, "version") == "0.2.0"
            && Text(source, "digest") == PhotonCadBridge.IndustrialImageSha256[7..]
            && Text(source, "license") == "redistribution-blocked";
    }

    private static bool TryBuildLiveBearingInputs(
        JsonElement capability,
        out Dictionary<string, object?> inputs)
    {
        inputs = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (!capability.TryGetProperty("parameters", out var parameters)
            || parameters.ValueKind != JsonValueKind.Array
            || parameters.GetArrayLength() is < 1 or > 32)
            return false;
        foreach (var parameter in parameters.EnumerateArray())
        {
            var id = Text(parameter, "id");
            var kind = Text(parameter, "kind");
            if (id is null || kind is null || inputs.ContainsKey(id)) return false;
            if (kind == "choice"
                && parameter.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0
                && Text(choices[0], "value") is { } choice)
            {
                inputs.Add(id, choice);
                continue;
            }
            if (parameter.TryGetProperty("defaultValue", out var defaultValue)
                && defaultValue.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                inputs.Add(id, defaultValue.Clone());
                continue;
            }
            if (parameter.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.False)
            {
                inputs.Add(id, null);
                continue;
            }
            inputs.Clear();
            return false;
        }
        return true;
    }

    private static bool TryBuildLiveSpurGearInputs(
        JsonElement capability,
        out Dictionary<string, object?> inputs)
    {
        inputs = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (!capability.TryGetProperty("parameters", out var parameters) || parameters.ValueKind != JsonValueKind.Array)
            return false;
        var expected = new Dictionary<string, (string Kind, string Unit, object Value, double Number)>(StringComparer.Ordinal)
        {
            ["module"] = ("number", "length", 2d, 2d),
            ["pressure_angle"] = ("number", "angle", 20d, 20d),
            ["thickness"] = ("number", "length", 10d, 10d),
            ["tooth_count"] = ("integer", "count", 24L, 24d),
        };
        var optionalLengths = new HashSet<string>(["addendum", "dedendum", "root_fillet"], StringComparer.Ordinal);
        if (parameters.GetArrayLength() != expected.Count + optionalLengths.Count) return false;
        foreach (var parameter in parameters.EnumerateArray())
        {
            var id = Text(parameter, "id");
            if (id is null || inputs.ContainsKey(id)) return false;
            if (expected.TryGetValue(id, out var value))
            {
                if (Text(parameter, "kind") != value.Kind
                    || Text(parameter, "unit") != value.Unit
                    || !parameter.TryGetProperty("required", out var required)
                    || required.ValueKind != JsonValueKind.True
                    || !CatalogNumberAccepts(parameter, value.Number))
                    return false;
                inputs.Add(id, value.Value);
                continue;
            }
            if (!optionalLengths.Contains(id)
                || Text(parameter, "kind") != "number"
                || Text(parameter, "unit") != "length"
                || !parameter.TryGetProperty("required", out var optionalRequired)
                || optionalRequired.ValueKind != JsonValueKind.False
                || !parameter.TryGetProperty("defaultValue", out var defaultValue)
                || defaultValue.ValueKind != JsonValueKind.Null)
                return false;
            inputs.Add(id, null);
        }
        return inputs.Count == expected.Count + optionalLengths.Count
            && expected.Keys.All(inputs.ContainsKey)
            && optionalLengths.All(inputs.ContainsKey);
    }

    private static bool CatalogNumberAccepts(JsonElement parameter, double value)
    {
        if (parameter.TryGetProperty("minimum", out var minimum)
            && minimum.ValueKind != JsonValueKind.Null
            && (minimum.ValueKind != JsonValueKind.Number || minimum.GetDouble() > value))
            return false;
        if (parameter.TryGetProperty("maximum", out var maximum)
            && maximum.ValueKind != JsonValueKind.Null
            && (maximum.ValueKind != JsonValueKind.Number || maximum.GetDouble() < value))
            return false;
        return true;
    }

    private static HashSet<string> SnapshotOccurrenceIds(JsonElement operationResult)
    {
        var ids = operationResult.GetProperty("snapshot").GetProperty("entities").EnumerateArray()
            .Where(entity => Text(entity, "kind") == "occurrence")
            .Select(entity => Text(entity, "id") ?? throw new InvalidOperationException("Live industrial occurrence lacked an identifier."))
            .ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0) throw new InvalidOperationException("Live industrial snapshot lacked occurrences.");
        return ids;
    }

    private static HashSet<string> GlbEntityTags(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 20
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0x46546C67
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != 2
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) != bytes.Length
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]) != 0x4E4F534A)
            throw new InvalidDataException("Live industrial GLB envelope was invalid.");
        var jsonLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]));
        if (jsonLength <= 0 || jsonLength > bytes.Length - 20)
            throw new InvalidDataException("Live industrial GLB JSON chunk was invalid.");
        using var document = JsonDocument.Parse(bytes.Slice(20, jsonLength).ToArray());
        var nodes = document.RootElement.GetProperty("nodes");
        var tags = nodes.EnumerateArray()
            .Select(node => Text(node.GetProperty("extras"), "photonEntityId")
                ?? throw new InvalidDataException("Live industrial GLB node lacked an entity tag."))
            .ToHashSet(StringComparer.Ordinal);
        if (tags.Count != nodes.GetArrayLength())
            throw new InvalidDataException("Live industrial GLB contained duplicate entity tags.");
        return tags;
    }

    private static bool ValidPart21Envelope(ReadOnlySpan<byte> bytes)
    {
        var text = Encoding.ASCII.GetString(bytes);
        return text.StartsWith("ISO-10303-21;", StringComparison.Ordinal)
            && text.Contains("HEADER;", StringComparison.Ordinal)
            && text.Contains("DATA;", StringComparison.Ordinal)
            && text.TrimEnd(' ', '\t', '\r', '\n').EndsWith("END-ISO-10303-21;", StringComparison.Ordinal);
    }

    private static bool LiveIndustrialProvenance(PhotonCadArtifactProvenanceV1 provenance) =>
        provenance.Backend == PhotonCadBackendV1.Assembly
        && StringComparer.Ordinal.Equals(provenance.BundleId, "photon.cad.industrial.container.v1")
        && StringComparer.Ordinal.Equals(
            provenance.BundleManifestSha256,
            "sha256:12dcd086d95759f47a892def58e2e7a85e4107ad5c0c1a83cdccd1b2f415e4da")
        && StringComparer.Ordinal.Equals(provenance.Source.Package, "photon-cad-industrial")
        && StringComparer.Ordinal.Equals(provenance.Source.Version, "0.1.0")
        && StringComparer.Ordinal.Equals(provenance.Source.Digest, PhotonCadBridge.IndustrialImageSha256)
        && StringComparer.Ordinal.Equals(provenance.Source.License, "redistribution-blocked");

    internal static async Task<bool> RunAsync()
    {
        EnsureRuntimeSyncLoadedForSmoke();
        if (!await ProjectResetFailureStaysClosedAsync()) return false;
        var tempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"photon-cad-bridge-smoke-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            if (!await RunPersistedPrimitiveFlowAsync(tempRoot)) return false;
            if (!await RunStepImportBoundaryAsync(tempRoot)) return false;
            if (!await IndustrialMissingEvidenceStaysUnavailableAsync(tempRoot)) return false;
            if (!await RunIndustrialPreviewFlowAsync(tempRoot)) return false;
            if (!await RunAssemblyPlacementFlowAsync(tempRoot)) return false;
            if (!await HostileRuntimeResultMatrixAsync(tempRoot)) return false;
            if (!await PersistenceFailureCompensatesAsync(tempRoot)) return false;
            if (!await CancellationDrainsAsync(tempRoot)) return false;
            return true;
        }
        finally { DeleteOwnedSmokeRoot(tempRoot); }
    }

    private static async Task<bool> RunStepImportBoundaryAsync(string tempRoot)
    {
        var invalidSource = Path.Combine(tempRoot, "invalid-import.step");
        await File.WriteAllTextAsync(invalidSource, "not a STEP Part 21 document");
        var frames = new List<JsonElement>();
        var dialog = new SmokeProjectDialog(
            Path.Combine(tempRoot, "unused-import.photoncad"),
            invalidSource);
        await using var bridge = new PhotonCadBridge(
            Directory.GetCurrentDirectory(),
            message => frames.Add(JsonSerializer.SerializeToElement(message)),
            projectDialog: dialog);
        var epoch = await bridge.ResetAsync();
        if (!bridge.TryOpenRendererGeneration(epoch)) return Fail("STEP import bridge generation did not open.");

        await SendFrameAsync(bridge, new
        {
            type = "photonCad.step.import",
            version = 1,
            contractVersion = 1,
            requestId = "step-import-invalid-source",
        });
        var invalid = Frame(frames, "photonCad.step.import.result", "step-import-invalid-source");
        if (Text(invalid.GetProperty("value"), "status") != "rejected"
            || Text(invalid.GetProperty("value"), "reason") != "step-source-invalid"
            || dialog.Calls != 1
            || dialog.LastPurpose != "import-step"
            || JsonSerializer.Serialize(invalid).Contains(invalidSource, StringComparison.OrdinalIgnoreCase))
            return Fail("STEP import did not reject invalid source bytes without leaking the native path.");

        var inventorSource = Path.Combine(tempRoot, "unsupported-import.ipt");
        await File.WriteAllBytesAsync(inventorSource,
            [0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1, 0, 0, 0, 0]);
        dialog.ImportPath = inventorSource;
        await SendFrameAsync(bridge, new
        {
            type = "photonCad.step.import",
            version = 1,
            contractVersion = 1,
            requestId = "inventor-import-unavailable",
        });
        var inventor = Frame(frames, "photonCad.step.import.result", "inventor-import-unavailable");
        if (Text(inventor.GetProperty("value"), "status") != "unavailable"
            || Text(inventor.GetProperty("value"), "reason") != "autodesk-inventor-authority-unavailable"
            || dialog.Calls != 2
            || JsonSerializer.Serialize(inventor).Contains(inventorSource, StringComparison.OrdinalIgnoreCase))
            return Fail("Inventor intake did not truthfully report its missing conversion authority without leaking the native path.");

        dialog.CancelImport = true;
        await SendFrameAsync(bridge, new
        {
            type = "photonCad.step.import",
            version = 1,
            contractVersion = 1,
            requestId = "step-import-cancelled",
        });
        var cancelled = Frame(frames, "photonCad.step.import.result", "step-import-cancelled");
        if (Text(cancelled.GetProperty("value"), "status") != "cancelled"
            || Text(cancelled.GetProperty("value"), "reason") != "native-picker-cancelled"
            || dialog.Calls != 3)
            return Fail("STEP import cancellation was not returned exactly once before project creation.");
        return true;
    }

    private static async Task<bool> RunAssemblyPlacementFlowAsync(string tempRoot)
    {
        var capability = PhotonCadBridge.AssemblyRendererCapability();
        var transformCapability = PhotonCadBridge.AssemblyTransformRendererCapability();
        var removeCapability = PhotonCadBridge.AssemblyRemoveRendererCapability();
        if (capability.Id != PhotonCadAssemblyContract.PlaceCapabilityId
            || capability.Operation != CadCapabilityOperationKind.Assemble
            || capability.Backend != CadBackend.Assembly
            || !capability.PreviewSupported
            || capability.Experimental
            || !capability.Parameters.Select(value => value.Id).ToHashSet(StringComparer.Ordinal)
                .SetEquals(["sourceEntityId", "parentOccurrenceId", "translation", "rotationDegrees"])
            || capability.Parameters.Single(value => value.Id == "sourceEntityId").Kind != CadParameterKind.Entity
            || capability.Parameters.Single(value => value.Id == "parentOccurrenceId").Required
            || capability.Parameters.Single(value => value.Id == "translation").Unit != CadParameterUnit.Length
            || capability.Parameters.Single(value => value.Id == "rotationDegrees").Unit != CadParameterUnit.Angle
            || transformCapability.Id != PhotonCadAssemblyContract.TransformCapabilityId
            || transformCapability.Operation != CadCapabilityOperationKind.Assemble
            || !transformCapability.Parameters.Select(value => value.Id).ToHashSet(StringComparer.Ordinal)
                .SetEquals(["translation", "rotationDegrees"])
            || !transformCapability.PreviewSupported || transformCapability.Experimental
            || removeCapability.Id != PhotonCadAssemblyContract.RemoveCapabilityId
            || removeCapability.Operation != CadCapabilityOperationKind.Assemble
            || removeCapability.Parameters.Count != 0
            || !removeCapability.PreviewSupported || removeCapability.Experimental)
            return Fail("Assembly placement, transform, or removal capability was not described exactly.");

        var path = Path.Combine(tempRoot, "assembly-place.photoncad");
        var frames = new List<JsonElement>();
        var stepExportPath = Path.Combine(tempRoot, "selected-gear.step");
        var stepPicker = new SmokeStepDestinationPicker(stepExportPath);
        var stepExportHost = new PhotonCadStepExportHost(Path.Combine(tempRoot, "step-journal"), stepPicker);
        var createEntityIds = new List<string>();
        var assemblyProviders = new List<SmokeAssemblyPlacementProvider>();
        var transformProviders = new List<SmokeAssemblyTransformProvider>();
        var removalProviders = new List<SmokeAssemblyRemovalProvider>();
        var verifiedProjects = new List<PhotonCadCanonicalProject>();
        await using var bridge = new PhotonCadBridge(
            Directory.GetCurrentDirectory(),
            message => frames.Add(JsonSerializer.SerializeToElement(message)),
            projectDialog: new SmokeProjectDialog(path),
            workbenchOrigin: new Uri("https://127.0.0.1:4173/"),
            industrialBindingFactory: (request, _) =>
            {
                if (request.AssemblyTransform is { } transform)
                {
                    var inputs = new List<PhotonCadSyncOperationInput>
                    {
                        new("occurrenceId", PhotonCadSyncInputValue.Text(transform.OccurrenceId)),
                        new("sourceEntityId", PhotonCadSyncInputValue.Entity(transform.SourceEntityId)),
                    };
                    for (var index = 0; index < transform.Transform.Count; index++)
                        inputs.Add(new PhotonCadSyncOperationInput(
                            $"matrix{index:D2}",
                            PhotonCadSyncInputValue.Number(transform.Transform[index])));
                    var sync = new PhotonCadRuntimeSyncRequest(
                        request.RequestId, request.SessionId, request.ProjectId, request.BaseRevision,
                        PhotonCadAssemblyContract.TransformCapabilityId, PhotonCadOperationModeV1.Scratch,
                        inputs, [transform.SourceEntityId]);
                    var provider = new SmokeAssemblyTransformProvider(
                        sync, transform.OccurrenceId, transform.SourceEntityId, transform.Transform);
                    transformProviders.Add(provider);
                    return ValueTask.FromResult(new IndustrialMutationBinding(sync, provider, provider));
                }
                if (request.AssemblyRemoval is { } removal)
                {
                    var sync = new PhotonCadRuntimeSyncRequest(
                        request.RequestId, request.SessionId, request.ProjectId, request.BaseRevision,
                        PhotonCadAssemblyContract.RemoveCapabilityId, PhotonCadOperationModeV1.Scratch,
                        [
                            new("occurrenceId", PhotonCadSyncInputValue.Text(removal.OccurrenceId)),
                            new("sourceEntityId", PhotonCadSyncInputValue.Entity(removal.SourceEntityId)),
                        ],
                        [removal.SourceEntityId]);
                    var provider = new SmokeAssemblyRemovalProvider(sync, removal.OccurrenceId, removal.SourceEntityId);
                    removalProviders.Add(provider);
                    return ValueTask.FromResult(new IndustrialMutationBinding(sync, provider, provider));
                }
                if (request.AssemblyInputs is { } assembly)
                {
                    var parent = assembly.ParentOccurrenceId ?? $"{assembly.SourceEntityId}.occ";
                    var inputs = new List<PhotonCadSyncOperationInput>
                    {
                        new("occurrenceId", PhotonCadSyncInputValue.Text(request.EntityId)),
                        new("sourceEntityId", PhotonCadSyncInputValue.Entity(assembly.SourceEntityId)),
                        new("parentOccurrenceId", PhotonCadSyncInputValue.Text(parent)),
                    };
                    for (var index = 0; index < assembly.Transform.Count; index++)
                        inputs.Add(new PhotonCadSyncOperationInput($"matrix{index:D2}", PhotonCadSyncInputValue.Number(assembly.Transform[index])));
                    var sync = new PhotonCadRuntimeSyncRequest(
                        request.RequestId, request.SessionId, request.ProjectId, request.BaseRevision,
                        PhotonCadAssemblyContract.PlaceCapabilityId, PhotonCadOperationModeV1.Scratch,
                        inputs, [assembly.SourceEntityId]);
                    var assemblyProvider = new SmokeAssemblyPlacementProvider(sync, request.EntityId, assembly.SourceEntityId, parent, assembly.Transform);
                    assemblyProviders.Add(assemblyProvider);
                    return ValueTask.FromResult(new IndustrialMutationBinding(sync, assemblyProvider, assemblyProvider));
                }
                var create = new PhotonCadRuntimeSyncRequest(
                    request.RequestId, request.SessionId, request.ProjectId, request.BaseRevision, request.CapabilityId,
                    PhotonCadOperationModeV1.Scratch,
                    request.NumericInputs.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .Select(pair => new PhotonCadSyncOperationInput(pair.Key, PhotonCadSyncInputValue.Number(pair.Value))),
                    [request.EntityId]);
                createEntityIds.Add(request.EntityId);
                var createProvider = new SmokeIndustrialProvider(create, request.EntityId);
                return ValueTask.FromResult(new IndustrialMutationBinding(create, createProvider, createProvider));
            },
            industrialVerification: (project, checks, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                verifiedProjects.Add(project);
                var unavailable = checks.Contains(PhotonCadCommittedVerificationCheck.Interference);
                var failed = checks.Count == 1 && checks[0] == PhotonCadCommittedVerificationCheck.Dimensions;
                return ValueTask.FromResult(new PhotonCadCommittedVerificationResult(
                    !unavailable,
                    !unavailable && !failed,
                    unavailable ? "interference_unavailable" : failed ? "committed_dimensions_invalid" : "passed",
                    project.Revision,
                    DateTimeOffset.UtcNow));
            },
            stepExportHost: stepExportHost);
        var epoch = await bridge.ResetAsync();
        if (!bridge.TryOpenRendererGeneration(epoch)) return Fail("Assembly bridge generation did not open.");
        var identity = await CreateCanonicalProjectAsync(bridge, frames, "assembly-place");
        var createdDocument = Frame(frames, "photonCad.project.create.result").GetProperty("value").GetProperty("document").Clone();
        await SendPrimitiveAsync(bridge, "assembly-part-a", identity.SessionId, identity.ProjectId, 0, CadPinnedCapabilityCatalog.BoxCapabilityId);
        await SendPrimitiveAsync(bridge, "assembly-part-b", identity.SessionId, identity.ProjectId, 2, CadPinnedCapabilityCatalog.CylinderCapabilityId);
        var baseResult = Frame(frames, "photonCad.execute.result").GetProperty("value");
        if (baseResult.GetProperty("resultingRevision").GetInt64() != 4 || createEntityIds.Count != 2)
            return Fail("Assembly fixture did not persist two exact existing parts at revision four.");
        var basePreviewDigest = Text(baseResult.GetProperty("preview"), "contentDigest")!;
        await SendAssemblyAsync(bridge, "assembly-place-root", identity, 4, createEntityIds[1], null,
            new { x = 10, y = 0, z = 0 }, new { x = 0, y = 0, z = 0 });
        var first = Frame(frames, "photonCad.execute.result").GetProperty("value");
        if (first.GetProperty("resultingRevision").GetInt64() != 6
            || Text(first.GetProperty("preview"), "contentDigest") == basePreviewDigest)
            return Fail($"First assembly placement did not commit exact +2 with replacement preview: {JsonSerializer.Serialize(new { status = Text(first, "status"), revision = first.TryGetProperty("resultingRevision", out var revision) ? revision.GetInt64() : -1, preview = first.TryGetProperty("preview", out var preview) ? Text(preview, "contentDigest") : null, expectedPrevious = basePreviewDigest, compensation = assemblyProviders.LastOrDefault()?.LastCompensationReason, recent = frames.TakeLast(4).Select(value => new { type = Text(value, "type"), code = Text(value, "code"), requestId = Text(value, "requestId") }) })}");
        var baseOccurrenceIds = baseResult.GetProperty("snapshot").GetProperty("occurrences").EnumerateArray()
            .Select(value => Text(value, "occurrenceId")!).ToHashSet(StringComparer.Ordinal);
        var firstOccurrence = first.GetProperty("snapshot").GetProperty("occurrences").EnumerateArray()
            .Single(value => !baseOccurrenceIds.Contains(Text(value, "occurrenceId")!));
        var firstOccurrenceId = Text(firstOccurrence, "occurrenceId")!;
        await SendAssemblyAsync(bridge, "assembly-place-nested", identity, 6, createEntityIds[1], firstOccurrenceId,
            new { x = 0, y = 5, z = 0 }, new { x = 0, y = 0, z = 90 });
        var second = Frame(frames, "photonCad.execute.result").GetProperty("value");
        var secondOccurrences = second.GetProperty("snapshot").GetProperty("occurrences").EnumerateArray().ToArray();
        var nested = secondOccurrences.Single(value => Text(value, "parentOccurrenceId") == firstOccurrenceId);
        var transform = nested.GetProperty("transform").EnumerateArray().Select(value => value.GetDouble()).ToArray();
        if (second.GetProperty("resultingRevision").GetInt64() != 8
            || secondOccurrences.Length != 4
            || Math.Abs(transform[0]) > 1e-12 || Math.Abs(transform[1] + 1) > 1e-12
            || Math.Abs(transform[4] - 1) > 1e-12 || transform[7] != 5
            || assemblyProviders.Count != 2 || assemblyProviders.Any(value => value.CompensationCount != 0))
            return Fail("Nested rotated assembly occurrence did not persist its exact rigid transform.");

        var nestedOccurrenceId = Text(nested, "occurrenceId")!;
        await SendAssemblyTransformAsync(
            bridge, "assembly-transform-nested", identity, 8, nestedOccurrenceId,
            new { x = 2, y = 3, z = 4 }, new { x = 0, y = 180, z = 0 });
        var transformed = Frame(frames, "photonCad.execute.result", "assembly-transform-nested").GetProperty("value");
        var transformedOccurrences = transformed.GetProperty("snapshot").GetProperty("occurrences").EnumerateArray().ToArray();
        var transformedNested = transformedOccurrences.Single(value => Text(value, "occurrenceId") == nestedOccurrenceId);
        var transformedMatrix = transformedNested.GetProperty("transform").EnumerateArray().Select(value => value.GetDouble()).ToArray();
        if (transformed.GetProperty("resultingRevision").GetInt64() != 10
            || transformedOccurrences.Length != secondOccurrences.Length
            || transformedMatrix[3] != 2 || transformedMatrix[7] != 3 || transformedMatrix[11] != 4
            || Math.Abs(transformedMatrix[0] + 1) > 1e-12 || Math.Abs(transformedMatrix[10] + 1) > 1e-12
            || transformProviders.Count != 1 || transformProviders[0].CompensationCount != 0)
            return Fail("Assembly occurrence transform did not persist as an exact +2 edit-and-preview transaction.");

        var frameCount = frames.Count;
        await SendAssemblyAsync(bridge, "assembly-foreign-source", identity, 10, "foreign-source", null,
            new { x = 0, y = 0, z = 0 }, new { x = 0, y = 0, z = 0 });
        await SendAssemblyAsync(bridge, "assembly-foreign-parent", identity, 10, createEntityIds[1], "foreign-parent.occ",
            new { x = 0, y = 0, z = 0 }, new { x = 0, y = 0, z = 0 });
        await SendAssemblyAsync(bridge, "assembly-range", identity, 10, createEntityIds[1], null,
            new { x = 1_000_001, y = 0, z = 0 }, new { x = 0, y = 0, z = 0 });
        await SendAsync(bridge, $$$"""
            {"type":"photonCad.execute","version":1,"contractVersion":1,"requestId":"assembly-nonfinite","sessionId":"{{{identity.SessionId}}}","projectId":"{{{identity.ProjectId}}}","baseRevision":10,"mode":"scratch","capabilityId":"{{{PhotonCadAssemblyContract.PlaceCapabilityId}}}","inputs":{"sourceEntityId":"{{{createEntityIds[1]}}}","parentOccurrenceId":null,"translation":{"x":1e400,"y":0,"z":0},"rotationDegrees":{"x":0,"y":0,"z":0}},"targetEntityIds":[]}
            """);
        await SendAssemblyAsync(bridge, "assembly-stale", identity, 9, createEntityIds[1], null,
            new { x = 0, y = 0, z = 0 }, new { x = 0, y = 0, z = 0 });
        await SendAssemblyAsync(bridge, "assembly-bad-result", identity, 10, createEntityIds[1], null,
            new { x = 1, y = 0, z = 0 }, new { x = 0, y = 0, z = 0 });
        await SendFrameAsync(bridge, new
        {
            type = "photonCad.execute",
            version = 1,
            contractVersion = 1,
            requestId = "assembly-target-rejected",
            sessionId = identity.SessionId,
            projectId = identity.ProjectId,
            baseRevision = 10,
            mode = "scratch",
            capabilityId = PhotonCadAssemblyContract.PlaceCapabilityId,
            inputs = new
            {
                sourceEntityId = createEntityIds[1],
                parentOccurrenceId = (string?)null,
                translation = new { x = 0, y = 0, z = 0 },
                rotationDegrees = new { x = 0, y = 0, z = 0 }
            },
            targetEntityIds = new[] { createEntityIds[0] },
        });
        if (frames.Skip(frameCount).Any(value => Text(value, "type") == "photonCad.execute.result"
            && value.GetProperty("value").GetProperty("status").GetString() == "accepted"))
            return Fail("Hostile assembly requests published accepted results.");
        var rejectedProvider = assemblyProviders.SingleOrDefault(value => value.RequestId == "assembly-bad-result");
        if (rejectedProvider is null || rejectedProvider.CompensationCount != 1
            || rejectedProvider.LastCompensationReason != "assembly_host_new_occurrence_transform_rejected")
            return Fail("Rejected assembly provider output was not compensated and evicted exactly once.");

        await SendFrameAsync(bridge, new
        {
            type = "photonCad.execute",
            version = 1,
            contractVersion = 1,
            requestId = "assembly-remove-branch",
            sessionId = identity.SessionId,
            projectId = identity.ProjectId,
            baseRevision = 10,
            mode = "scratch",
            capabilityId = PhotonCadAssemblyContract.RemoveCapabilityId,
            inputs = new { },
            targetEntityIds = new[] { firstOccurrenceId },
        });
        var removed = Frame(frames, "photonCad.execute.result", "assembly-remove-branch").GetProperty("value");
        var removedOccurrences = removed.GetProperty("snapshot").GetProperty("occurrences").EnumerateArray().ToArray();
        if (Text(removed, "status") != "accepted" || removed.GetProperty("resultingRevision").GetInt64() != 12
            || removedOccurrences.Length != 2
            || removedOccurrences.Any(value => Text(value, "occurrenceId") == firstOccurrenceId
                || Text(value, "parentOccurrenceId") == firstOccurrenceId)
            || removalProviders.Count != 1 || removalProviders[0].CompensationCount != 0)
            return Fail("Assembly occurrence subtree removal did not persist exact +2 edit-and-preview state.");

        var projectHandle = Text(createdDocument, "projectHandle")!;
        var committedPreview = removed.GetProperty("preview");
        await SendFrameAsync(bridge, new
        {
            type = "photonCad.project.refresh",
            version = 1,
            contractVersion = 1,
            requestId = Text(committedPreview, "previewId"),
            projectHandle,
            sessionId = identity.SessionId,
            projectId = identity.ProjectId,
            knownRevision = 12
        });
        var refreshFrame = Frame(frames, "photonCad.project.refresh.result", Text(committedPreview, "previewId")!);
        if (refreshFrame.ValueKind != JsonValueKind.Object)
        {
            var recentFrames = frames.TakeLast(6).Select(value => new
            {
                type = Text(value, "type"),
                requestId = value.TryGetProperty("value", out var frameValue)
                    ? Text(frameValue, "requestId")
                    : Text(value, "requestId"),
                code = Text(value, "code"),
            });
            return Fail($"Exact post-mutation refresh emitted no correlated result: {JsonSerializer.Serialize(recentFrames)}");
        }
        var refreshValue = refreshFrame.GetProperty("value");
        if (!refreshValue.TryGetProperty("document", out var refreshed))
            return Fail($"Exact post-mutation refresh was not opened: {refreshValue}");
        await SendFrameAsync(bridge, new
        {
            type = "photonCad.preview.resolve",
            version = 1,
            contractVersion = 1,
            requestId = "assembly-preview-after-internal-refresh",
            sessionId = identity.SessionId,
            projectId = identity.ProjectId,
            revision = 12,
            previewId = Text(committedPreview, "previewId"),
            expectedDigest = Text(committedPreview, "contentDigest"),
            maximumBytes = 128 * 1024 * 1024
        });
        var preservedPreview = Frame(frames, "photonCad.preview.resolve.result", "assembly-preview-after-internal-refresh").GetProperty("value");
        if (Text(preservedPreview, "status") != "available")
            return Fail("Exact post-mutation refresh revoked its committed preview.");
        var preservedPreviewUrl = new Uri(Text(preservedPreview, "url")!, UriKind.Absolute);

        await SendFrameAsync(bridge, new
        {
            type = "photonCad.project.refresh",
            version = 1,
            contractVersion = 1,
            requestId = "assembly-manual-refresh",
            projectHandle,
            sessionId = identity.SessionId,
            projectId = identity.ProjectId,
            knownRevision = 12
        });
        _ = Frame(frames, "photonCad.project.refresh.result", "assembly-manual-refresh");
        using (var revokedAfterManualRefresh = bridge.TryRespondPreviewResource("GET", preservedPreviewUrl, true)!)
        {
            if (revokedAfterManualRefresh.StatusCode != 404)
                return Fail("Manual refresh retained a committed preview resource.");
        }
        await SendFrameAsync(bridge, new
        {
            type = "photonCad.preview.hydrate",
            version = 1,
            contractVersion = 1,
            requestId = "assembly-preview-hydrate",
            sessionId = identity.SessionId,
            projectId = identity.ProjectId,
            revision = 12,
        });
        var hydration = Frame(frames, "photonCad.preview.hydrate.result", "assembly-preview-hydrate").GetProperty("value");
        if (Text(hydration, "status") != "available" || !hydration.TryGetProperty("preview", out var hydratedPreview)
            || Text(hydratedPreview, "projectId") != identity.ProjectId || hydratedPreview.GetProperty("revision").GetInt64() != 12)
            return Fail("Committed project preview hydration did not return an exact fresh receipt.");
        await SendFrameAsync(bridge, new
        {
            type = "photonCad.preview.resolve",
            version = 1,
            contractVersion = 1,
            requestId = "assembly-hydrated-preview-resolve",
            sessionId = identity.SessionId,
            projectId = identity.ProjectId,
            revision = 12,
            previewId = Text(hydratedPreview, "previewId"),
            expectedDigest = Text(hydratedPreview, "contentDigest"),
            maximumBytes = 128 * 1024 * 1024,
        });
        var hydratedAsset = Frame(frames, "photonCad.preview.resolve.result", "assembly-hydrated-preview-resolve").GetProperty("value");
        if (Text(hydratedAsset, "status") != "available") return Fail("Hydrated committed preview did not resolve.");
        using (var hydratedResponse = bridge.TryRespondPreviewResource("GET", new Uri(Text(hydratedAsset, "url")!, UriKind.Absolute), true)!)
        {
            if (hydratedResponse.StatusCode != 200 || hydratedResponse.Content is null || hydratedResponse.Content.Length < 20)
                return Fail("Hydrated committed preview did not produce a sealed one-use GLB response.");
        }
        await SendFrameAsync(bridge, new
        {
            type = "photonCad.step.export",
            version = 1,
            contractVersion = 1,
            requestId = "assembly-step-export",
            sessionId = identity.SessionId,
            projectId = identity.ProjectId,
            revision = 12,
            contentDigest = Text(refreshed, "contentDigest"),
            entityId = createEntityIds[1]
        });
        var stepExport = Frame(frames, "photonCad.step.export.result", "assembly-step-export").GetProperty("value");
        if (Text(stepExport, "status") != "committed"
            || Text(stepExport, "entityId") != createEntityIds[1]
            || Text(stepExport, "destinationLabel") != Path.GetFileName(stepExportPath)
            || !File.Exists(stepExportPath)
            || JsonSerializer.Serialize(stepExport).Contains(tempRoot, StringComparison.OrdinalIgnoreCase)
            || !ValidPart21Envelope(await File.ReadAllBytesAsync(stepExportPath)))
            return Fail("Committed selected-entity STEP export did not preserve its pathless create-only receipt.");

        await SendFrameAsync(bridge, new
        {
            type = "photonCad.step.export",
            version = 1,
            contractVersion = 1,
            requestId = "assembly-step-export-occurrence",
            sessionId = identity.SessionId,
            projectId = identity.ProjectId,
            revision = 12,
            contentDigest = Text(refreshed, "contentDigest"),
            entityId = firstOccurrenceId
        });
        if (Text(Frame(frames, "photonCad.step.export.result", "assembly-step-export-occurrence").GetProperty("value"), "status") != "rejected")
            return Fail("Occurrence STEP export did not fail closed.");

        stepPicker.CancelNext = true;
        await SendFrameAsync(bridge, new
        {
            type = "photonCad.step.export",
            version = 1,
            contractVersion = 1,
            requestId = "assembly-step-export-cancelled",
            sessionId = identity.SessionId,
            projectId = identity.ProjectId,
            revision = 12,
            contentDigest = Text(refreshed, "contentDigest"),
            entityId = createEntityIds[0]
        });
        if (Text(Frame(frames, "photonCad.step.export.result", "assembly-step-export-cancelled").GetProperty("value"), "status") != "cancelled")
            return Fail("Native STEP picker cancellation reported false success.");
        await SendFrameAsync(bridge, new
        {
            type = "photonCad.project.close",
            version = 1,
            contractVersion = 1,
            requestId = "assembly-close",
            projectHandle,
            sessionId = identity.SessionId,
            projectId = identity.ProjectId,
            revision = 12,
            lastSavedRevision = 12,
            contentDigest = Text(refreshed, "contentDigest"),
            lastSavedContentDigest = Text(refreshed, "lastSavedContentDigest"),
            discardUnsavedChanges = false
        });
        var closed = Frame(frames, "photonCad.project.close.result").GetProperty("value");
        await SendFrameAsync(bridge, new
        {
            type = "photonCad.project.reopen",
            version = 1,
            contractVersion = 1,
            requestId = "assembly-reopen",
            reopenHandle = Text(closed.GetProperty("reopen"), "reopenHandle")
        });
        await SendFrameAsync(bridge, new
        {
            type = "photonCad.verify",
            version = 1,
            contractVersion = 1,
            requestId = "assembly-verify-reopened",
            sessionId = identity.SessionId,
            projectId = identity.ProjectId,
            revision = 12,
            checks = new[] { "valid-solids", "dimensions", "assembly-structure", "export-readiness" },
        });
        var verifiedFrame = Frame(frames, "photonCad.verify.result", "assembly-verify-reopened");
        if (verifiedFrame.ValueKind != JsonValueKind.Object)
        {
            var error = frames.LastOrDefault(frame => Text(frame, "type") == "photonCad.error"
                && Text(frame, "requestId") == "assembly-verify-reopened");
            return Fail($"Reopened committed assembly verification emitted no result. Error: {(error.ValueKind == JsonValueKind.Object ? Text(error, "code") : "none")}");
        }
        var verified = verifiedFrame.GetProperty("value");
        var verifiedIssues = verified.GetProperty("issues");
        var verifiedIssueCode = verifiedIssues.GetArrayLength() > 0 ? Text(verifiedIssues[0], "code") : "none";
        if (Text(verified, "status") != "passed" || verified.GetProperty("revision").GetInt64() != 12
            || verifiedProjects.Count != 1 || verifiedProjects[0].Dirty || verifiedProjects[0].Revision != 12)
            return Fail($"Reopened committed assembly verification was not bound to exact clean storage readback. Status={Text(verified, "status")}, Revision={verified.GetProperty("revision").GetInt64()}, ProviderCalls={verifiedProjects.Count}, Issue={verifiedIssueCode}");
        await SendFrameAsync(bridge, new
        {
            type = "photonCad.verify",
            version = 1,
            contractVersion = 1,
            requestId = "assembly-verify-interference",
            sessionId = identity.SessionId,
            projectId = identity.ProjectId,
            revision = 12,
            checks = new[] { "interference" },
        });
        var interference = Frame(frames, "photonCad.verify.result", "assembly-verify-interference").GetProperty("value");
        if (Text(interference, "status") != "unavailable"
            || Text(interference.GetProperty("issues")[0], "code") != "interference_unavailable")
            return Fail("Unsupported interference verification did not remain explicitly unavailable.");
        await SendFrameAsync(bridge, new
        {
            type = "photonCad.verify",
            version = 1,
            contractVersion = 1,
            requestId = "assembly-verify-failed",
            sessionId = identity.SessionId,
            projectId = identity.ProjectId,
            revision = 12,
            checks = new[] { "dimensions" },
        });
        var failed = Frame(frames, "photonCad.verify.result", "assembly-verify-failed").GetProperty("value");
        if (Text(failed, "status") != "failed"
            || Text(failed.GetProperty("issues")[0], "code") != "committed_dimensions_invalid")
            return Fail("A completed committed-project verification failure was reported as unavailable or passed.");
        var verifiedCountBeforeStale = verifiedProjects.Count;
        await SendFrameAsync(bridge, new
        {
            type = "photonCad.verify",
            version = 1,
            contractVersion = 1,
            requestId = "assembly-verify-stale",
            sessionId = identity.SessionId,
            projectId = identity.ProjectId,
            revision = 5,
            checks = new[] { "valid-solids" },
        });
        var stale = Frame(frames, "photonCad.verify.result", "assembly-verify-stale").GetProperty("value");
        if (Text(stale, "status") != "unavailable" || verifiedProjects.Count != verifiedCountBeforeStale)
            return Fail("Stale committed verification reached the provider or reported success.");
        await SendFrameAsync(bridge, new
        {
            type = "photonCad.verify",
            version = 1,
            contractVersion = 1,
            requestId = "assembly-verify-foreign",
            sessionId = identity.SessionId,
            projectId = "pcpid:foreign-project",
            revision = 12,
            checks = new[] { "valid-solids" },
        });
        var foreign = Frame(frames, "photonCad.verify.result", "assembly-verify-foreign").GetProperty("value");
        if (Text(foreign, "status") != "unavailable" || verifiedProjects.Count != verifiedCountBeforeStale)
            return Fail("Foreign committed project identity reached the verifier or reported success.");
        var codec = new PhotonCadCanonicalProjectCodecV1();
        var state = codec.Inspect(codec.Decode(await File.ReadAllBytesAsync(path)));
        if (state.Revision != 12 || state.Occurrences.Count != 2
            || state.Bom.Single(value => value.SourceEntityId == createEntityIds[1]).Quantity != 1
            || state.Artifacts.Count(value => value.Role == PhotonCadArtifactRoleV1.AuthoritativeGeometry) != 2
            || state.Artifacts.Count(value => value.Role == PhotonCadArtifactRoleV1.ProjectPreview) != 1)
            return Fail("Assembly placement BOM, STEP, preview, or reopen state drifted.");
        return true;
    }

    private static Task SendAssemblyAsync(
        PhotonCadBridge bridge,
        string requestId,
        (string SessionId, string ProjectId) identity,
        long revision,
        string sourceEntityId,
        string? parentOccurrenceId,
        object translation,
        object rotationDegrees) => SendFrameAsync(bridge, new
        {
            type = "photonCad.execute",
            version = 1,
            contractVersion = 1,
            requestId,
            sessionId = identity.SessionId,
            projectId = identity.ProjectId,
            baseRevision = revision,
            mode = "scratch",
            capabilityId = PhotonCadAssemblyContract.PlaceCapabilityId,
            inputs = new { sourceEntityId, parentOccurrenceId, translation, rotationDegrees },
            targetEntityIds = Array.Empty<string>(),
        });

    private static Task SendAssemblyTransformAsync(
        PhotonCadBridge bridge,
        string requestId,
        (string SessionId, string ProjectId) identity,
        long revision,
        string occurrenceId,
        object translation,
        object rotationDegrees) => SendFrameAsync(bridge, new
        {
            type = "photonCad.execute",
            version = 1,
            contractVersion = 1,
            requestId,
            sessionId = identity.SessionId,
            projectId = identity.ProjectId,
            baseRevision = revision,
            mode = "scratch",
            capabilityId = PhotonCadAssemblyContract.TransformCapabilityId,
            inputs = new { translation, rotationDegrees },
            targetEntityIds = new[] { occurrenceId },
        });

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
                    request.NumericInputs.OrderBy(pair => pair.Key, StringComparer.Ordinal)
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
        foreach (var assemblyName in new[] { "PhotonCadProjects.RuntimeSync", "PhotonCadArtifacts" })
        {
            if (AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name == assemblyName)) continue;
            var exactPath = Path.Combine(AppContext.BaseDirectory, $"{assemblyName}.dll");
            if (!File.Exists(exactPath)) throw new FileNotFoundException("The focused CAD smoke dependency is missing.", exactPath);
            _ = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(exactPath));
        }
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
            return Fail($"Photon CAD did not issue one opaque native workspace selection for a new project: {newPicker}");
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

        internal SmokeProjectDialog(string projectPath, string? importPath = null)
        {
            _projectPath = projectPath;
            ImportPath = importPath;
        }

        internal int Calls { get; private set; }
        internal string? LastPurpose { get; private set; }
        internal bool CancelImport { get; set; }
        internal string? ImportPath { get; set; }

        public PhotonCadWindowsDialogResult Show(string purpose)
        {
            Calls++;
            LastPurpose = purpose;
            if (purpose == "import-step")
                return CancelImport || ImportPath is null
                    ? new PhotonCadWindowsDialogResult(false, null)
                    : new PhotonCadWindowsDialogResult(true, ImportPath);
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

    private static Task SendFrameAsync(PhotonCadBridge bridge, object value)
    {
        var frame = JsonSerializer.SerializeToElement(value);
        return bridge.HandleAsync(Text(frame, "type")!, frame);
    }

    private static JsonElement Frame(IEnumerable<JsonElement> frames, string type) =>
        frames.LastOrDefault(frame => Text(frame, "type") == type);

    private static JsonElement Frame(IEnumerable<JsonElement> frames, string type, string requestId) =>
        frames.LastOrDefault(frame => Text(frame, "type") == type
            && frame.TryGetProperty("value", out var value) && Text(value, "requestId") == requestId);

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool Fail(string message)
    {
        Console.Error.WriteLine(message);
        return false;
    }

    private sealed record LivePreviewEvidence(
        string ContentDigest,
        byte[] Content,
        IReadOnlySet<string> EntityTags);

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
            Glb = BuildGlb([OccurrenceId]);
        }

        internal int ApplyCount { get; private set; }
        internal string OccurrenceId { get; }
        internal byte[] Glb { get; private set; }

        public ValueTask<PhotonCadSealedMutationDelta> ApplyAsync(
            PhotonCadSealedMutationProviderRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(request.Request, _request)
                || request.Units != PhotonCadProjectUnit.Millimeter
                || request.BaseArtifacts.Count(value => value.Role == PhotonCadArtifactRoleV1.ProjectPreview) > 1)
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
            var rootOccurrence = request.BaseOccurrences.SingleOrDefault(value => value.ParentOccurrenceId is null);
            var occurrences = request.BaseOccurrences.Select(value => new PhotonCadOccurrenceV1(
                    value.OccurrenceId, value.ParentOccurrenceId, value.PartNumber, value.SourceEntityId, value.Transform))
                .Append(new PhotonCadOccurrenceV1(OccurrenceId, rootOccurrence?.OccurrenceId, "BOX", _entityId,
                    new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 }))
                .OrderBy(value => value.OccurrenceId, StringComparer.Ordinal)
                .ToArray();
            var bom = request.BaseBom.Select(value => new PhotonCadBomRow(
                    value.PartNumber, value.Description, value.Quantity, value.Unit, value.SourceEntityId))
                .Append(new PhotonCadBomRow("BOX", "Create industrial primitive", 1, PhotonCadBomUnit.Each, _entityId))
                .ToArray();
            Glb = BuildGlb(occurrences.Select(value => value.OccurrenceId));
            var priorPreview = request.BaseArtifacts.SingleOrDefault(value => value.Role == PhotonCadArtifactRoleV1.ProjectPreview);
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
                occurrences,
                [],
                bom,
                [
                    new PhotonCadSealedArtifactDelta(
                        PhotonCadArtifactRoleV1.AuthoritativeGeometry, PhotonCadArtifactKindV1.Step, _entityId,
                        checked(_request.BaseRevision + 1), step, step.LongLength, Digest(step), "model/step", null,
                        createOperationId, evidence),
                    new PhotonCadSealedArtifactDelta(
                        PhotonCadArtifactRoleV1.ProjectPreview, PhotonCadArtifactKindV1.Glb, null,
                        checked(_request.BaseRevision + 2), Glb, Glb.LongLength, Digest(Glb), PhotonCadPreviewContract.MediaType,
                        bounds, previewOperationId, evidence, priorPreview?.ContentDigest),
                ],
                occurrenceMergeMode: PhotonCadCollectionMergeMode.ReplaceAll,
                bomMergeMode: PhotonCadCollectionMergeMode.ReplaceAll);
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

        internal static byte[] BuildGlb(IEnumerable<string> occurrenceIds)
        {
            var occurrences = occurrenceIds
                .Select(value => (Id: value, Matrix: new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 }))
                .ToArray();
            return BuildGlb(occurrences);
        }

        internal static byte[] BuildGlb(IEnumerable<PhotonCadOccurrenceV1> occurrences) => BuildGlb(
            occurrences.Select(value => (value.OccurrenceId, ToGlbMatrix(value.Transform))));

        private static byte[] BuildGlb(IEnumerable<(string Id, double[] Matrix)> source)
        {
            var occurrences = source.OrderBy(value => value.Id, StringComparer.Ordinal).ToArray();
            var json = JsonSerializer.SerializeToUtf8Bytes(new
            {
                asset = new { version = "2.0" },
                scene = 0,
                scenes = new[] { new { nodes = Enumerable.Range(0, occurrences.Length).ToArray() } },
                nodes = occurrences.Select(value => new { mesh = 0, matrix = value.Matrix, extras = new { photonEntityId = value.Id } }).ToArray(),
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

        private static double[] ToGlbMatrix(IReadOnlyList<double> rowMajor)
        {
            var columnMajor = new double[16];
            for (var column = 0; column < 4; column++)
                for (var row = 0; row < 4; row++)
                    columnMajor[(column * 4) + row] = rowMajor[(row * 4) + column];
            return columnMajor;
        }
    }

    private sealed class SmokeAssemblyPlacementProvider : IPhotonCadSealedMutationProvider, IPhotonCadSealedMutationCompensator
    {
        private static readonly string DigestA = "sha256:" + new string('a', 64);
        private static readonly string DigestB = "sha256:" + new string('b', 64);
        private static readonly string DigestC = "sha256:" + new string('c', 64);
        private readonly PhotonCadRuntimeSyncRequest _request;
        private readonly string _occurrenceId;
        private readonly string _sourceEntityId;
        private readonly string _parentOccurrenceId;
        private readonly IReadOnlyList<double> _transform;
        private readonly bool _corruptTransform;

        internal SmokeAssemblyPlacementProvider(
            PhotonCadRuntimeSyncRequest request,
            string occurrenceId,
            string sourceEntityId,
            string parentOccurrenceId,
            IReadOnlyList<double> transform)
        {
            _request = request;
            _occurrenceId = occurrenceId;
            _sourceEntityId = sourceEntityId;
            _parentOccurrenceId = parentOccurrenceId;
            _transform = transform;
            _corruptTransform = request.RequestId == "assembly-bad-result";
        }

        internal string RequestId => _request.RequestId;
        internal int CompensationCount { get; private set; }
        internal string? LastCompensationReason { get; private set; }

        public ValueTask<PhotonCadSealedMutationDelta> ApplyAsync(
            PhotonCadSealedMutationProviderRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(request.Request, _request)
                || !request.BaseEntities.Any(value => value.Id == _sourceEntityId)
                || !request.BaseOccurrences.Any(value => value.OccurrenceId == _parentOccurrenceId)
                || request.BaseOccurrences.Any(value => value.OccurrenceId == _occurrenceId))
                throw new InvalidOperationException("assembly_smoke_base_rejected");
            var template = request.BaseBom.Single(value => value.SourceEntityId == _sourceEntityId);
            var emittedTransform = _corruptTransform
                ? new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 }
                : _transform;
            var occurrences = request.BaseOccurrences.Select(value => new PhotonCadOccurrenceV1(
                    value.OccurrenceId, value.ParentOccurrenceId, value.PartNumber, value.SourceEntityId, value.Transform))
                .Append(new PhotonCadOccurrenceV1(
                    _occurrenceId, _parentOccurrenceId, template.PartNumber, _sourceEntityId, emittedTransform))
                .OrderBy(value => value.OccurrenceId, StringComparer.Ordinal)
                .ToArray();
            var bom = request.BaseBom.Select(value => new PhotonCadBomRow(
                value.PartNumber,
                value.Description,
                occurrences.Count(occurrence => occurrence.SourceEntityId == value.SourceEntityId),
                value.Unit,
                value.SourceEntityId)).ToArray();
            var prior = request.BaseArtifacts.Single(value => value.Role == PhotonCadArtifactRoleV1.ProjectPreview);
            var glb = SmokeIndustrialProvider.BuildGlb(occurrences);
            var source = new PhotonCadSourceIdentityV1("photon-cad-industrial", "0.1.0", DigestB, "redistribution-blocked");
            var evidence = new PhotonCadProviderEvidence(
                PhotonCadBackendV1.Assembly, "photon.cad.industrial.smoke.v1",
                ["photon.cad.industrial.protocol.v1"], "catalog-v1", "industrial-bundle",
                DigestA, DigestA, DigestB, DigestC, source);
            var operationId = $"assembly-operation-{Guid.NewGuid():N}";
            var previewOperationId = $"assembly-preview-operation-{Guid.NewGuid():N}";
            var operationRevision = checked(_request.BaseRevision + 1);
            var revision = checked(_request.BaseRevision + 2);
            return ValueTask.FromResult(new PhotonCadSealedMutationDelta(
                $"assembly-mutation-{Guid.NewGuid():N}",
                _request.RequestId, _request.SessionId, _request.ProjectId, _request.BaseRevision, revision,
                [
                    new PhotonCadAppliedOperationDelta(
                        operationRevision, operationId, PhotonCadAssemblyContract.PlaceCapabilityId, "Place assembly occurrence",
                        DateTimeOffset.UtcNow, PhotonCadOperationModeV1.Scratch, _request.Inputs, [_sourceEntityId], evidence),
                    new PhotonCadAppliedOperationDelta(
                        revision, previewOperationId, PhotonCadAssemblyContract.PreviewCapabilityId, "Seal complete assembly preview",
                        DateTimeOffset.UtcNow.AddMilliseconds(1), PhotonCadOperationModeV1.Scratch, [], [_sourceEntityId], evidence),
                ],
                occurrences: occurrences,
                bom: bom,
                artifacts:
                [
                    new PhotonCadSealedArtifactDelta(
                        PhotonCadArtifactRoleV1.ProjectPreview, PhotonCadArtifactKindV1.Glb, null,
                        revision, glb, glb.LongLength, Sha256(glb), PhotonCadPreviewContract.MediaType,
                        new PhotonCadBoundsV1(new(0, 0, 0), new(10, 20, 30)), previewOperationId, evidence, prior.ContentDigest),
                ],
                occurrenceMergeMode: PhotonCadCollectionMergeMode.ReplaceAll,
                bomMergeMode: PhotonCadCollectionMergeMode.ReplaceAll));
        }

        public ValueTask CompensateAsync(
            PhotonCadSealedMutationDelta mutation,
            string reason,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CompensationCount++;
            LastCompensationReason = reason;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SmokeAssemblyTransformProvider : IPhotonCadSealedMutationProvider, IPhotonCadSealedMutationCompensator
    {
        private static readonly string DigestA = "sha256:" + new string('a', 64);
        private static readonly string DigestB = "sha256:" + new string('b', 64);
        private static readonly string DigestC = "sha256:" + new string('c', 64);
        private readonly PhotonCadRuntimeSyncRequest _request;
        private readonly string _occurrenceId;
        private readonly string _sourceEntityId;
        private readonly IReadOnlyList<double> _transform;

        internal SmokeAssemblyTransformProvider(
            PhotonCadRuntimeSyncRequest request,
            string occurrenceId,
            string sourceEntityId,
            IReadOnlyList<double> transform)
        {
            _request = request;
            _occurrenceId = occurrenceId;
            _sourceEntityId = sourceEntityId;
            _transform = transform;
        }

        internal int CompensationCount { get; private set; }

        public ValueTask<PhotonCadSealedMutationDelta> ApplyAsync(
            PhotonCadSealedMutationProviderRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(request.Request, _request))
                throw new InvalidOperationException("assembly_transform_smoke_request_rejected");
            var target = request.BaseOccurrences.SingleOrDefault(value => value.OccurrenceId == _occurrenceId);
            if (target is null || target.SourceEntityId != _sourceEntityId)
                throw new InvalidOperationException("assembly_transform_smoke_target_rejected");
            var occurrences = request.BaseOccurrences.Select(value => value.OccurrenceId == _occurrenceId
                    ? new PhotonCadOccurrenceV1(
                        value.OccurrenceId, value.ParentOccurrenceId, value.PartNumber, value.SourceEntityId, _transform)
                    : new PhotonCadOccurrenceV1(
                        value.OccurrenceId, value.ParentOccurrenceId, value.PartNumber, value.SourceEntityId, value.Transform))
                .OrderBy(value => value.OccurrenceId, StringComparer.Ordinal)
                .ToArray();
            var bom = request.BaseBom.Select(value => new PhotonCadBomRow(
                value.PartNumber, value.Description, value.Quantity, value.Unit, value.SourceEntityId)).ToArray();
            var prior = request.BaseArtifacts.Single(value => value.Role == PhotonCadArtifactRoleV1.ProjectPreview);
            var glb = SmokeIndustrialProvider.BuildGlb(occurrences);
            var source = new PhotonCadSourceIdentityV1(
                "photon-cad-industrial", "0.1.0", DigestB, "redistribution-blocked");
            var evidence = new PhotonCadProviderEvidence(
                PhotonCadBackendV1.Assembly, "photon.cad.industrial.smoke.v1",
                ["photon.cad.industrial.protocol.v1"], "catalog-v1", "industrial-bundle",
                DigestA, DigestA, DigestB, DigestC, source);
            var operationId = $"assembly-transform-operation-{Guid.NewGuid():N}";
            var previewOperationId = $"assembly-transform-preview-operation-{Guid.NewGuid():N}";
            var operationRevision = checked(_request.BaseRevision + 1);
            var revision = checked(_request.BaseRevision + 2);
            return ValueTask.FromResult(new PhotonCadSealedMutationDelta(
                $"assembly-transform-mutation-{Guid.NewGuid():N}",
                _request.RequestId, _request.SessionId, _request.ProjectId, _request.BaseRevision, revision,
                [
                    new PhotonCadAppliedOperationDelta(
                        operationRevision, operationId, PhotonCadAssemblyContract.TransformCapabilityId,
                        "Transform assembly occurrence", DateTimeOffset.UtcNow,
                        PhotonCadOperationModeV1.Scratch, _request.Inputs, [_sourceEntityId], evidence),
                    new PhotonCadAppliedOperationDelta(
                        revision, previewOperationId, PhotonCadAssemblyContract.PreviewCapabilityId,
                        "Seal complete assembly preview", DateTimeOffset.UtcNow.AddMilliseconds(1),
                        PhotonCadOperationModeV1.Scratch, [], [_sourceEntityId], evidence),
                ],
                occurrences: occurrences,
                bom: bom,
                artifacts:
                [
                    new PhotonCadSealedArtifactDelta(
                        PhotonCadArtifactRoleV1.ProjectPreview, PhotonCadArtifactKindV1.Glb, null,
                        revision, glb, glb.LongLength, Sha256(glb), PhotonCadPreviewContract.MediaType,
                        new PhotonCadBoundsV1(new(0, 0, 0), new(10, 20, 30)), previewOperationId, evidence,
                        prior.ContentDigest),
                ],
                occurrenceMergeMode: PhotonCadCollectionMergeMode.ReplaceAll,
                bomMergeMode: PhotonCadCollectionMergeMode.ReplaceAll));
        }

        public ValueTask CompensateAsync(
            PhotonCadSealedMutationDelta mutation,
            string reason,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CompensationCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SmokeAssemblyRemovalProvider : IPhotonCadSealedMutationProvider, IPhotonCadSealedMutationCompensator
    {
        private static readonly string DigestA = "sha256:" + new string('a', 64);
        private static readonly string DigestB = "sha256:" + new string('b', 64);
        private static readonly string DigestC = "sha256:" + new string('c', 64);
        private readonly PhotonCadRuntimeSyncRequest _request;
        private readonly string _occurrenceId;
        private readonly string _sourceEntityId;

        internal SmokeAssemblyRemovalProvider(
            PhotonCadRuntimeSyncRequest request,
            string occurrenceId,
            string sourceEntityId)
        {
            _request = request;
            _occurrenceId = occurrenceId;
            _sourceEntityId = sourceEntityId;
        }

        internal int CompensationCount { get; private set; }

        public ValueTask<PhotonCadSealedMutationDelta> ApplyAsync(
            PhotonCadSealedMutationProviderRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(request.Request, _request)) throw new InvalidOperationException("assembly_remove_smoke_request_rejected");
            var target = request.BaseOccurrences.SingleOrDefault(value => value.OccurrenceId == _occurrenceId);
            if (target is null || target.SourceEntityId != _sourceEntityId)
                throw new InvalidOperationException("assembly_remove_smoke_target_rejected");
            var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _occurrenceId };
            bool changed;
            do
            {
                changed = false;
                foreach (var occurrence in request.BaseOccurrences)
                {
                    if (occurrence.ParentOccurrenceId is not null && removed.Contains(occurrence.ParentOccurrenceId)
                        && removed.Add(occurrence.OccurrenceId)) changed = true;
                }
            } while (changed);
            var occurrences = request.BaseOccurrences.Where(value => !removed.Contains(value.OccurrenceId))
                .Select(value => new PhotonCadOccurrenceV1(
                    value.OccurrenceId, value.ParentOccurrenceId, value.PartNumber, value.SourceEntityId, value.Transform))
                .OrderBy(value => value.OccurrenceId, StringComparer.Ordinal).ToArray();
            if (occurrences.Length == 0) throw new InvalidOperationException("assembly_remove_smoke_last_occurrence_rejected");
            var bom = request.BaseBom.Where(value => occurrences.Any(occurrence => occurrence.SourceEntityId == value.SourceEntityId))
                .Select(value => new PhotonCadBomRow(
                    value.PartNumber, value.Description,
                    occurrences.Count(occurrence => occurrence.SourceEntityId == value.SourceEntityId),
                    value.Unit, value.SourceEntityId)).ToArray();
            var prior = request.BaseArtifacts.Single(value => value.Role == PhotonCadArtifactRoleV1.ProjectPreview);
            var glb = SmokeIndustrialProvider.BuildGlb(occurrences);
            var source = new PhotonCadSourceIdentityV1("photon-cad-industrial", "0.1.0", DigestB, "redistribution-blocked");
            var evidence = new PhotonCadProviderEvidence(
                PhotonCadBackendV1.Assembly, "photon.cad.industrial.smoke.v1",
                ["photon.cad.industrial.protocol.v1"], "catalog-v1", "industrial-bundle",
                DigestA, DigestA, DigestB, DigestC, source);
            var operationId = $"assembly-remove-operation-{Guid.NewGuid():N}";
            var previewOperationId = $"assembly-remove-preview-operation-{Guid.NewGuid():N}";
            var operationRevision = checked(_request.BaseRevision + 1);
            var revision = checked(_request.BaseRevision + 2);
            return ValueTask.FromResult(new PhotonCadSealedMutationDelta(
                $"assembly-remove-mutation-{Guid.NewGuid():N}",
                _request.RequestId, _request.SessionId, _request.ProjectId, _request.BaseRevision, revision,
                [
                    new PhotonCadAppliedOperationDelta(
                        operationRevision, operationId, PhotonCadAssemblyContract.RemoveCapabilityId, "Remove assembly occurrence",
                        DateTimeOffset.UtcNow, PhotonCadOperationModeV1.Scratch, _request.Inputs, [_sourceEntityId], evidence),
                    new PhotonCadAppliedOperationDelta(
                        revision, previewOperationId, PhotonCadAssemblyContract.PreviewCapabilityId, "Seal complete assembly preview",
                        DateTimeOffset.UtcNow.AddMilliseconds(1), PhotonCadOperationModeV1.Scratch, [], [_sourceEntityId], evidence),
                ],
                occurrences: occurrences,
                bom: bom,
                artifacts:
                [
                    new PhotonCadSealedArtifactDelta(
                        PhotonCadArtifactRoleV1.ProjectPreview, PhotonCadArtifactKindV1.Glb, null,
                        revision, glb, glb.LongLength, Sha256(glb), PhotonCadPreviewContract.MediaType,
                        new PhotonCadBoundsV1(new(0, 0, 0), new(10, 20, 30)), previewOperationId, evidence, prior.ContentDigest),
                ],
                occurrenceMergeMode: PhotonCadCollectionMergeMode.ReplaceAll,
                bomMergeMode: PhotonCadCollectionMergeMode.ReplaceAll));
        }

        public ValueTask CompensateAsync(
            PhotonCadSealedMutationDelta mutation,
            string reason,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CompensationCount++;
            return ValueTask.CompletedTask;
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

    private sealed class SmokeStepDestinationPicker(string path) : IPhotonCadStepDestinationPicker
    {
        internal bool CancelNext { get; set; }
        public ValueTask<string?> PickNewStepPathAsync(string suggestedFileName, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CancelNext)
            {
                CancelNext = false;
                return ValueTask.FromResult<string?>(null);
            }
            return ValueTask.FromResult<string?>(path);
        }
    }

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
