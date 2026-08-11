using System.Text.Json;
using PhotonCadProjects.Codec;
using PhotonCadProjects.DesktopAdapter;

namespace PhotonCadProjects.DesktopAdapter.Smoke;

internal static class Program
{
    private static async Task<int> Main()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("wire-v1-full-lifecycle", FullLifecycleAsync),
            ("dirty-close-binding", DirtyCloseAsync),
            ("latest-wins-and-exact-cancel", LatestWinsAndCancelAsync),
            ("serialized-writes-and-replay", SerializedWritesAndReplayAsync),
            ("reset-reuse-and-unavailable", ResetReuseAndUnavailableAsync),
            ("malformed-path-frame-fails-closed", MalformedPathFrameAsync),
            ("existing-target-requires-explicit-overwrite", ExistingTargetRequiresExplicitOverwriteAsync),
            ("occurrence-transform-wire-parity", OccurrenceTransformWireParityAsync),
        };
        foreach (var test in tests)
        {
            await test.Run().ConfigureAwait(false);
            Console.WriteLine($"PASS {test.Name}");
        }
        Console.WriteLine($"PhotonCadProjects.DesktopAdapter.Smoke: {tests.Length}/{tests.Length} passed");
        return 0;
    }

    private static Task ExistingTargetRequiresExplicitOverwriteAsync()
    {
        var mapped = PhotonCadProjectErrorMapper.Map(
            new PhotonCadProjects.PhotonCadProjectException(
                "target_exists_overwrite_confirmation_required",
                "target"));
        Assert(mapped.Code == "target-exists-overwrite-confirmation-required", "exact existing-target reason");
        Assert(!mapped.Retryable && !mapped.Unavailable, "existing target stays a rejected user decision");
        return Task.CompletedTask;
    }

    private static Task OccurrenceTransformWireParityAsync()
    {
        double[] transform =
        [
            0, -1, 0, 125,
            1, 0, 0, -30,
            0, 0, 1, 8,
            0, 0, 0, 1,
        ];
        var state = new PhotonCadProjectStateV1(
            "session:projection",
            "project:projection",
            0,
            "Projection",
            PhotonCadProjectUnit.Millimeter,
            [new PhotonCadEntityV1("datum:shaft", null, PhotonCadEntityKindV1.Datum, "Shaft datum", true, false, "fixture:source")],
            [],
            [new PhotonCadOccurrenceV1("occurrence:shaft", null, "SHAFT-001", "datum:shaft", transform)],
            [],
            [],
            [],
            dirty: false);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(PhotonCadSnapshotWireProjection.Snapshot(state, 1)));
        var root = document.RootElement;
        var entities = root.GetProperty("entities");
        var occurrences = root.GetProperty("occurrences");
        Assert(entities.GetArrayLength() == 2, "canonical entity and occurrence pseudo-entity projected");
        Assert(occurrences.GetArrayLength() == 1, "explicit occurrence projected exactly once");

        var occurrence = occurrences[0];
        Assert(occurrence.GetProperty("occurrenceId").GetString() == "occurrence:shaft", "occurrence identity projected");
        Assert(occurrence.GetProperty("parentOccurrenceId").ValueKind == JsonValueKind.Null, "root occurrence parent remains explicit null");
        Assert(occurrence.GetProperty("transform").EnumerateArray().Select(value => value.GetDouble()).SequenceEqual(transform), "ordered rigid transform preserved exactly");

        var pseudo = entities[1];
        Assert(pseudo.GetProperty("id").GetString() == "occurrence:shaft", "pseudo-entity identity matches occurrence");
        Assert(pseudo.GetProperty("kind").GetString() == "occurrence", "pseudo-entity kind is occurrence");
        Assert(pseudo.GetProperty("name").GetString() == "SHAFT-001", "pseudo-entity name matches part number");
        Assert(pseudo.GetProperty("sourceCapabilityId").GetString() == "fixture:source", "pseudo-entity capability follows trusted source entity");
        return Task.CompletedTask;
    }

    private static async Task FullLifecycleAsync()
    {
        await using var context = new SmokeContext();
        var workspace = await PickAsync(context, "picker-new-1", "new", "Gearbox").ConfigureAwait(false);
        Assert(context.Host.LastSuggestedName == "Gearbox", "pathless suggested project name reached native host");
        var created = await CreateAsync(context, "create-1", workspace, "Gearbox").ConfigureAwait(false);
        Assert(created.GetProperty("snapshot").GetProperty("revision").GetInt64() == 0, "created revision");

        var saved = await SendDocumentActionAsync(context, "photonCad.project.save", "save-1", created).ConfigureAwait(false);
        Assert(saved.GetProperty("projectHandle").GetString() == created.GetProperty("projectHandle").GetString(), "save preserves handle");

        var destination = await PickAsync(context, "picker-save-as-1", "save-as").ConfigureAwait(false);
        var saveAsFrame = Frame(new
        {
            type = "photonCad.project.saveAs",
            version = 1,
            contractVersion = 1,
            requestId = "save-as-1",
            sourceProjectHandle = saved.GetProperty("projectHandle").GetString(),
            destinationWorkspaceHandle = destination,
            sessionId = saved.GetProperty("snapshot").GetProperty("sessionId").GetString(),
            projectId = saved.GetProperty("snapshot").GetProperty("projectId").GetString(),
            baseRevision = saved.GetProperty("snapshot").GetProperty("revision").GetInt64(),
            contentDigest = saved.GetProperty("contentDigest").GetString(),
        });
        await context.Dispatcher.HandleAsync("photonCad.project.saveAs", saveAsFrame).ConfigureAwait(false);
        var moved = context.SuccessDocument("photonCad.project.saveAs.result", "save-as-1", "saved");
        Assert(moved.GetProperty("projectHandle").GetString() != saved.GetProperty("projectHandle").GetString(), "save-as rotates project handle");

        var refreshFrame = Frame(new
        {
            type = "photonCad.project.refresh",
            version = 1,
            contractVersion = 1,
            requestId = "refresh-1",
            projectHandle = moved.GetProperty("projectHandle").GetString(),
            sessionId = moved.GetProperty("snapshot").GetProperty("sessionId").GetString(),
            projectId = moved.GetProperty("snapshot").GetProperty("projectId").GetString(),
            knownRevision = moved.GetProperty("snapshot").GetProperty("revision").GetInt64(),
        });
        await context.Dispatcher.HandleAsync("photonCad.project.refresh", refreshFrame).ConfigureAwait(false);
        _ = context.SuccessDocument("photonCad.project.refresh.result", "refresh-1", "opened");

        var reopen = await CloseAsync(context, "close-1", moved, discard: false).ConfigureAwait(false);
        var reopenFrame = Frame(new
        {
            type = "photonCad.project.reopen",
            version = 1,
            contractVersion = 1,
            requestId = "reopen-1",
            reopenHandle = reopen,
        });
        await context.Dispatcher.HandleAsync("photonCad.project.reopen", reopenFrame).ConfigureAwait(false);
        _ = context.SuccessDocument("photonCad.project.reopen.result", "reopen-1", "opened");

        var openWorkspace = await PickAsync(context, "picker-open-1", "open").ConfigureAwait(false);
        var openFrame = Frame(new
        {
            type = "photonCad.project.open",
            version = 1,
            contractVersion = 1,
            requestId = "open-1",
            workspaceHandle = openWorkspace,
        });
        await context.Dispatcher.HandleAsync("photonCad.project.open", openFrame).ConfigureAwait(false);
        _ = context.SuccessDocument("photonCad.project.open.result", "open-1", "opened");

        var wire = context.SerializedOutput();
        Assert(!wire.Contains("cad-storage-target:", StringComparison.Ordinal), "storage target did not cross wire");
        Assert(!wire.Contains("C:\\", StringComparison.OrdinalIgnoreCase), "native path did not cross wire");
        Assert(!wire.Contains("overwrite-grant", StringComparison.OrdinalIgnoreCase), "overwrite grant did not cross wire");
    }

    private static async Task DirtyCloseAsync()
    {
        await using var context = new SmokeContext();
        var workspace = await PickAsync(context, "dirty-picker", "new").ConfigureAwait(false);
        var clean = await CreateAsync(context, "dirty-create", workspace, "Dirty project").ConfigureAwait(false);
        var handle = new PhotonCadProjectHandle(clean.GetProperty("projectHandle").GetString()!);
        var dirty = JsonSerializer.SerializeToElement(context.Projection.Document(context.Host.MakeDirty(handle)));

        _ = await CloseAsync(context, "dirty-close-denied", dirty, discard: false, expectClosed: false).ConfigureAwait(false);
        var denied = context.Result("photonCad.project.close.result", "dirty-close-denied");
        Assert(denied.GetProperty("status").GetString() == "rejected", "dirty close rejected without confirmation");

        _ = await CloseAsync(context, "dirty-close-confirmed", dirty, discard: true).ConfigureAwait(false);
        Assert(context.Host.CloseCalls == 2, "dirty close reached host authority for both exact requests");
    }

    private static async Task LatestWinsAndCancelAsync()
    {
        await using var context = new SmokeContext();
        var firstWorkspace = await PickAsync(context, "latest-picker-1", "open").ConfigureAwait(false);
        var secondWorkspace = await PickAsync(context, "latest-picker-2", "open").ConfigureAwait(false);
        context.Host.BlockOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = context.Dispatcher.HandleAsync("photonCad.project.open", Frame(new
        {
            type = "photonCad.project.open",
            version = 1,
            contractVersion = 1,
            requestId = "latest-open-1",
            workspaceHandle = firstWorkspace,
        }));
        await Task.Delay(20).ConfigureAwait(false);
        var second = context.Dispatcher.HandleAsync("photonCad.project.open", Frame(new
        {
            type = "photonCad.project.open",
            version = 1,
            contractVersion = 1,
            requestId = "latest-open-2",
            workspaceHandle = secondWorkspace,
        }));
        await Task.WhenAll(first, second).ConfigureAwait(false);
        Assert(!context.HasResult("photonCad.project.open.result", "latest-open-1"), "superseded read produced no result");
        Assert(context.HasResult("photonCad.project.open.result", "latest-open-2"), "latest read won");

        var thirdWorkspace = await PickAsync(context, "cancel-picker", "open").ConfigureAwait(false);
        context.Host.BlockOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = context.Dispatcher.HandleAsync("photonCad.project.open", Frame(new
        {
            type = "photonCad.project.open",
            version = 1,
            contractVersion = 1,
            requestId = "cancel-open",
            workspaceHandle = thirdWorkspace,
        }));
        await Task.Delay(20).ConfigureAwait(false);
        await context.Dispatcher.HandleAsync("photonCad.project.cancel", Frame(new
        {
            type = "photonCad.project.cancel",
            version = 1,
            requestId = "cancel-wrong",
            targetRequestId = "cancel-open",
            operation = "refresh",
        })).ConfigureAwait(false);
        Assert(!blocked.IsCompleted, "cross-operation cancel was ignored");
        await context.Dispatcher.HandleAsync("photonCad.project.cancel", Frame(new
        {
            type = "photonCad.project.cancel",
            version = 1,
            requestId = "cancel-exact",
            targetRequestId = "cancel-open",
            operation = "open",
        })).ConfigureAwait(false);
        await blocked.ConfigureAwait(false);
        Assert(!context.HasResult("photonCad.project.open.result", "cancel-open"), "cancelled request produced no stale result");
    }

    private static async Task SerializedWritesAndReplayAsync()
    {
        await using var context = new SmokeContext();
        var workspace = await PickAsync(context, "write-picker", "new").ConfigureAwait(false);
        var document = await CreateAsync(context, "write-create", workspace, "Serialized").ConfigureAwait(false);
        context.Host.BlockSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = context.Dispatcher.HandleAsync("photonCad.project.save", SaveFrame("write-save-1", document));
        await Task.Delay(20).ConfigureAwait(false);
        await context.Dispatcher.HandleAsync("photonCad.project.save", SaveFrame("write-save-2", document)).ConfigureAwait(false);
        var busy = context.Result("photonCad.project.save.result", "write-save-2");
        Assert(busy.GetProperty("status").GetString() == "rejected", "second write rejected while first active");
        context.Host.BlockSave!.SetResult();
        await first.ConfigureAwait(false);
        Assert(context.HasResult("photonCad.project.save.result", "write-save-1"), "first serialized write completed");

        await context.Dispatcher.HandleAsync("photonCad.project.save", SaveFrame("write-save-2", document)).ConfigureAwait(false);
        var replay = context.Results("photonCad.project.save.result", "write-save-2").Last();
        Assert(replay.GetProperty("reason").GetString() == "duplicate-request-id", "replay identity rejected");
    }

    private static async Task ResetReuseAndUnavailableAsync()
    {
        await using var context = new SmokeContext();
        var workspace = await PickAsync(context, "reset-picker", "open").ConfigureAwait(false);
        context.Host.BlockOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var open = context.Dispatcher.HandleAsync("photonCad.project.open", Frame(new
        {
            type = "photonCad.project.open",
            version = 1,
            contractVersion = 1,
            requestId = "reset-open",
            workspaceHandle = workspace,
        }));
        await Task.Delay(20).ConfigureAwait(false);
        await context.Dispatcher.ResetAsync().ConfigureAwait(false);
        await open.ConfigureAwait(false);
        Assert(context.Host.ResetCount == 1, "reset reached reusable host");
        Assert(!context.HasResult("photonCad.project.open.result", "reset-open"), "pre-reset result revoked");

        var after = await PickAsync(context, "reset-picker", "new").ConfigureAwait(false);
        _ = await CreateAsync(context, "reset-create", after, "After reset").ConfigureAwait(false);
        context.Host.Available = false;
        await context.Dispatcher.HandleAsync("photonCad.project.picker", Frame(new
        {
            type = "photonCad.project.picker",
            version = 1,
            contractVersion = 1,
            requestId = "unavailable-picker",
            purpose = "new",
        })).ConfigureAwait(false);
        var unavailable = context.Result("photonCad.project.picker.result", "unavailable-picker");
        Assert(unavailable.GetProperty("status").GetString() == "unavailable", "readiness advertises unavailable honestly");
    }

    private static async Task MalformedPathFrameAsync()
    {
        await using var context = new SmokeContext();
        await context.Dispatcher.HandleAsync("photonCad.project.open", Frame(new
        {
            type = "photonCad.project.open",
            version = 1,
            contractVersion = 1,
            requestId = "path-injection",
            workspaceHandle = "cad-workspace:xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx",
            path = "C:\\private\\design.photoncad",
        })).ConfigureAwait(false);
        var error = context.Frame("photonCad.project.error", "path-injection");
        Assert(error.GetProperty("code").GetString() == "invalid-project-request", "unknown path field failed closed");
        Assert(!context.SerializedOutput().Contains("private", StringComparison.OrdinalIgnoreCase), "path was not reflected");

        using var duplicate = JsonDocument.Parse("""
            {"type":"photonCad.project.picker","version":1,"contractVersion":1,"requestId":"duplicate-field","requestId":"duplicate-field-2","purpose":"new"}
            """);
        await context.Dispatcher.HandleAsync("photonCad.project.picker", duplicate.RootElement).ConfigureAwait(false);
        Assert(context.Frame("photonCad.project.error", "duplicate-field-2").GetProperty("code").GetString() == "invalid-project-request", "duplicate field rejected");
    }

    private static async Task<string> PickAsync(SmokeContext context, string requestId, string purpose, string? suggestedName = null)
    {
        var frame = suggestedName is null
            ? Frame(new { type = "photonCad.project.picker", version = 1, contractVersion = 1, requestId, purpose })
            : Frame(new { type = "photonCad.project.picker", version = 1, contractVersion = 1, requestId, purpose, suggestedName });
        await context.Dispatcher.HandleAsync("photonCad.project.picker", frame).ConfigureAwait(false);
        var result = context.Result("photonCad.project.picker.result", requestId);
        Assert(result.GetProperty("status").GetString() == "selected", $"picker {requestId}");
        return result.GetProperty("workspaceHandle").GetString()!;
    }

    private static async Task<JsonElement> CreateAsync(SmokeContext context, string requestId, string workspaceHandle, string title)
    {
        await context.Dispatcher.HandleAsync("photonCad.project.create", Frame(new
        {
            type = "photonCad.project.create",
            version = 1,
            contractVersion = 1,
            requestId,
            workspaceHandle,
            title,
            units = "millimeter",
        })).ConfigureAwait(false);
        return context.SuccessDocument("photonCad.project.create.result", requestId, "opened");
    }

    private static async Task<JsonElement> SendDocumentActionAsync(
        SmokeContext context,
        string type,
        string requestId,
        JsonElement document)
    {
        await context.Dispatcher.HandleAsync(type, SaveFrame(requestId, document)).ConfigureAwait(false);
        return context.SuccessDocument("photonCad.project.save.result", requestId, "saved");
    }

    private static JsonElement SaveFrame(string requestId, JsonElement document) => Frame(new
    {
        type = "photonCad.project.save",
        version = 1,
        contractVersion = 1,
        requestId,
        projectHandle = document.GetProperty("projectHandle").GetString(),
        sessionId = document.GetProperty("snapshot").GetProperty("sessionId").GetString(),
        projectId = document.GetProperty("snapshot").GetProperty("projectId").GetString(),
        baseRevision = document.GetProperty("snapshot").GetProperty("revision").GetInt64(),
        contentDigest = document.GetProperty("contentDigest").GetString(),
    });

    private static async Task<string> CloseAsync(
        SmokeContext context,
        string requestId,
        JsonElement document,
        bool discard,
        bool expectClosed = true)
    {
        await context.Dispatcher.HandleAsync("photonCad.project.close", Frame(new
        {
            type = "photonCad.project.close",
            version = 1,
            contractVersion = 1,
            requestId,
            projectHandle = document.GetProperty("projectHandle").GetString(),
            sessionId = document.GetProperty("snapshot").GetProperty("sessionId").GetString(),
            projectId = document.GetProperty("snapshot").GetProperty("projectId").GetString(),
            revision = document.GetProperty("snapshot").GetProperty("revision").GetInt64(),
            lastSavedRevision = document.GetProperty("lastSavedRevision").GetInt64(),
            contentDigest = document.GetProperty("contentDigest").GetString(),
            lastSavedContentDigest = document.GetProperty("lastSavedContentDigest").GetString(),
            discardUnsavedChanges = discard,
        })).ConfigureAwait(false);
        var result = context.Result("photonCad.project.close.result", requestId);
        if (!expectClosed) return string.Empty;
        Assert(result.GetProperty("status").GetString() == "closed", $"close {requestId}");
        return result.GetProperty("reopen").GetProperty("reopenHandle").GetString()!;
    }

    private static JsonElement Frame(object value) => JsonSerializer.SerializeToElement(value);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"Smoke assertion failed: {message}");
    }

    private sealed class SmokeContext : IAsyncDisposable
    {
        private readonly object _sync = new();
        private readonly List<JsonElement> _frames = [];

        internal SmokeContext()
        {
            var codec = new PhotonCadCanonicalProjectCodecV1();
            Host = new FakePhotonCadDesktopProjectHost(codec);
            Projection = new PhotonCadProjectWireProjection(codec);
            Dispatcher = new PhotonCadProjectDesktopDispatcher(Host, Projection, Post);
        }

        internal FakePhotonCadDesktopProjectHost Host { get; }
        internal PhotonCadProjectWireProjection Projection { get; }
        internal PhotonCadProjectDesktopDispatcher Dispatcher { get; }

        internal JsonElement Frame(string type, string requestId) => Frames()
            .Last(value => value.GetProperty("type").GetString() == type
                && value.TryGetProperty("requestId", out var direct) && direct.GetString() == requestId);

        internal JsonElement Result(string type, string requestId) => Results(type, requestId).Last();

        internal IEnumerable<JsonElement> Results(string type, string requestId) => Frames()
            .Where(value => value.GetProperty("type").GetString() == type
                && value.TryGetProperty("value", out var result)
                && result.GetProperty("requestId").GetString() == requestId)
            .Select(value => value.GetProperty("value"));

        internal bool HasResult(string type, string requestId) => Results(type, requestId).Any();

        internal JsonElement SuccessDocument(string type, string requestId, string status)
        {
            var result = Result(type, requestId);
            Assert(result.GetProperty("status").GetString() == status, $"{type} {requestId} status");
            return result.GetProperty("document");
        }

        internal string SerializedOutput()
        {
            lock (_sync) return JsonSerializer.Serialize(_frames);
        }

        public ValueTask DisposeAsync() => Dispatcher.DisposeAsync();

        private void Post(object frame)
        {
            var value = JsonSerializer.SerializeToElement(frame);
            lock (_sync) _frames.Add(value);
        }

        private JsonElement[] Frames()
        {
            lock (_sync) return _frames.ToArray();
        }
    }
}
