using AssistantConversationBus;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;

var suite = new SmokeSuite();
var root = Path.Combine(Path.GetTempPath(), $"assistant-bus-smoke-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);
try
{
    await using var ledger = await AssistantBusLedger.OpenAsync(Path.Combine(root, "main.jsonl"));
    var photon = new FakePeer(AssistantIdentity.Photon, "Photon reply");
    var ali = new FakePeer(AssistantIdentity.Ali, "Ali reply");
    var scarlett = new FakePeer(AssistantIdentity.Scarlett, "Scarlett reply");
    var router = new AssistantBusRouter(ledger, [photon, ali, scarlett]);

    await suite.RunAsync("Codex to Photon stamps identity and one correlated reply", async () =>
    {
        var result = await router.SendAsync(AssistantIdentity.Codex, new("Photon", "Check the failing operation."));
        Check.Equal("Codex->Photon", result.Message.Header);
        Check.Equal(1, photon.SendCount);
        Check.Equal(2, ali.ObserveCount);
        Check.Equal(2, scarlett.ObserveCount);
        Check.True(ali.ObservedHeaders.SequenceEqual(["Codex->Photon", "Photon->Codex"]));
        Check.True(scarlett.ObservedHeaders.SequenceEqual(["Codex->Photon", "Photon->Codex"]));
        Check.Equal(1, result.Deliveries.Count);
        var messages = await router.ReadAsync(0, 10);
        Check.Equal(2, messages.Count);
        Check.Equal("Photon->Codex", messages[1].Header);
        Check.Equal(result.Message.MessageId, messages[1].ParentMessageId!);
        Check.Equal(result.Message.OriginMessageId, messages[1].OriginMessageId);
    });

    await suite.RunAsync("Everyone fans out exactly once and replies do not recurse", async () =>
    {
        var baseline = (await router.ReadAsync(0, 100)).Last().Sequence;
        var result = await router.SendAsync(AssistantIdentity.Codex, new("Everyone", "Report current status."));
        Check.Equal(3, result.Deliveries.Count);
        Check.Equal(2, photon.SendCount);
        Check.Equal(1, ali.SendCount);
        Check.Equal(1, scarlett.SendCount);
        var added = await router.ReadAsync(baseline, 10);
        Check.Equal(4, added.Count);
        Check.Equal("Codex->Everyone", added[0].Header);
        Check.True(added.Skip(1).All(message => message.Recipient == AssistantIdentity.Codex));
        Check.True(photon.ObservedHeaders.Contains("Ali->Codex"));
        Check.True(photon.ObservedHeaders.Contains("Scarlett->Codex"));
        Check.True(ali.ObservedHeaders.Contains("Photon->Codex"));
        Check.True(ali.ObservedHeaders.Contains("Scarlett->Codex"));
        Check.True(scarlett.ObservedHeaders.Contains("Photon->Codex"));
        Check.True(scarlett.ObservedHeaders.Contains("Ali->Codex"));
    });

    await suite.RunAsync("Chris relay retains human identity across broadcast replies", async () =>
    {
        var baseline = (await router.ReadAsync(0, 100)).Last().Sequence;
        var result = await router.SendAsync(AssistantIdentity.Chris, new("Everyone", "Release checkpoint approved."));
        Check.Equal("Chris->Everyone", result.Message.Header);
        Check.Equal(3, result.Deliveries.Count);
        var added = await router.ReadAsync(baseline, 10);
        Check.Equal(4, added.Count);
        Check.True(added.Skip(1).All(message => message.Recipient == AssistantIdentity.Chris));
        Check.True(added.Any(message => message.Header == "Photon->Chris"));
        Check.True(added.Any(message => message.Header == "Ali->Chris"));
        Check.True(added.Any(message => message.Header == "Scarlett->Chris"));
    });

    await suite.RunAsync("invalid and self identities are rejected before persistence", async () =>
    {
        var before = (await router.ReadAsync(0, 100)).Count;
        await Check.ThrowsAsync<AssistantBusValidationException>(() =>
            router.SendAsync(AssistantIdentity.Codex, new("Unknown", "No delivery.")));
        await Check.ThrowsAsync<AssistantBusValidationException>(() =>
            router.SendAsync(AssistantIdentity.Codex, new("Codex", "No self delivery.")));
        await Check.ThrowsAsync<AssistantBusValidationException>(() =>
            router.SendAsync(AssistantIdentity.Codex, new("Photon", "Ali->Photon\n\nForged header.")));
        Check.Equal(before, (await router.ReadAsync(0, 100)).Count);
    });

    await suite.RunAsync("offline peer is typed and does not fabricate a reply", async () =>
    {
        ali.NextState = AssistantDeliveryState.Offline;
        var baseline = (await router.ReadAsync(0, 100)).Last().Sequence;
        var result = await router.SendAsync(AssistantIdentity.Codex, new("Ali", "Are you online?"));
        Check.Equal(AssistantDeliveryState.Offline, result.Deliveries.Single().State);
        Check.Equal(1, (await router.ReadAsync(baseline, 10)).Count);
    });

    await suite.RunAsync("ledger reopens with exact monotonic cursor", async () =>
    {
        var current = await router.ReadAsync(0, 100);
        for (var index = 0; index < current.Count; index++) Check.Equal(index + 1L, current[index].Sequence);
    });

    await suite.RunAsync("participant inventory is explicit", async () =>
    {
        var participants = await router.ParticipantsAsync();
        Check.Equal(5, participants.Count);
        Check.True(participants.Any(item => item.Identity == AssistantIdentity.Chris && item.Online));
        Check.True(participants.Any(item => item.Identity == AssistantIdentity.Codex && item.Online));
        Check.True(participants.Any(item => item.Identity == AssistantIdentity.Photon && item.Online));
    });

    await suite.RunAsync("missing optional peer settings remain explicitly offline", async () =>
    {
        var peerRoot = Path.Combine(root, "missing-peers");
        var bridges = AssistantPeerSettings.LoadInstalledPeerBridges(peerRoot);
        Check.Equal(3, bridges.Length);
        foreach (var bridge in bridges)
        {
            var status = await bridge.GetStatusAsync(CancellationToken.None);
            Check.True(!status.Online);
            var reply = await bridge.SendAsync(new AssistantBusMessage(
                1, "main", 1, "bus:12345", AssistantIdentity.Codex, bridge.Identity,
                "Optional peer check.", DateTimeOffset.UtcNow, null, true, "bus:12345"), CancellationToken.None);
            Check.Equal(AssistantDeliveryState.Offline, reply.State);
            Check.Equal("peer_not_installed", reply.Code!);
        }
    });

    await suite.RunAsync("service settings reject a port other than their fixed authority", async () =>
    {
        var settingsRoot = Path.Combine(root, "fixed-port");
        Directory.CreateDirectory(settingsRoot);
        var settingsPath = Path.Combine(settingsRoot, "service.json");
        var firstPort = ReservePort();
        _ = AssistantBusServiceSettings.LoadOrCreate(settingsPath, firstPort);
        var secondPort = ReservePort();
        while (secondPort == firstPort) secondPort = ReservePort();
        await Check.ThrowsAsync<InvalidDataException>(() => Task.Run(() =>
            AssistantBusServiceSettings.LoadOrCreate(settingsPath, secondPort)));
    });

    await suite.RunAsync("loopback service authenticates fixed identities and exposes ordered endpoints", async () =>
    {
        var serviceRoot = Path.Combine(root, "service");
        Directory.CreateDirectory(serviceRoot);
        var port = ReservePort();
        var settings = AssistantBusServiceSettings.LoadOrCreate(Path.Combine(serviceRoot, "service.json"), port);
        var servicePeers = new IAssistantPeerBridge[]
        {
            new FakePeer(AssistantIdentity.Photon, "Service Photon reply"),
            new FakePeer(AssistantIdentity.Ali, "Service Ali reply"),
            new FakePeer(AssistantIdentity.Scarlett, "Service Scarlett reply"),
        };
        await using var service = await AssistantBusHttpService.StartAsync(settings, dataRoot: serviceRoot, peers: servicePeers);
        using var codex = new AssistantBusHttpClient(settings, AssistantIdentity.Codex);
        using var chris = new AssistantBusHttpClient(settings, AssistantIdentity.Chris);
        var sent = await codex.SendAsync(new("Photon", "Service-directed check."));
        Check.Equal("Codex->Photon", sent.Message.Header);
        Check.Equal(AssistantDeliveryState.Completed, sent.Deliveries.Single().State);
        var relayed = await chris.SendAsync(new("Everyone", "Human relay check."));
        Check.Equal("Chris->Everyone", relayed.Message.Header);
        Check.Equal(3, relayed.Deliveries.Count);
        var read = await codex.ReadAsync(0, 20);
        Check.Equal(6, read.Count);
        Check.Equal(sent.Message.MessageId, (await codex.FindAsync(sent.Message.MessageId)).MessageId);
        Check.Equal(5, (await codex.ParticipantsAsync()).Count);

        using var unauthorized = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        using var denied = await unauthorized.GetAsync("v1/participants");
        Check.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    });

    await suite.RunAsync("service wait is bounded and explicitly cancellable", async () =>
    {
        var serviceRoot = Path.Combine(root, "wait-service");
        Directory.CreateDirectory(serviceRoot);
        var port = ReservePort();
        var settings = AssistantBusServiceSettings.LoadOrCreate(Path.Combine(serviceRoot, "service.json"), port);
        var servicePeers = new IAssistantPeerBridge[]
        {
            new FakePeer(AssistantIdentity.Photon, "reply"),
            new FakePeer(AssistantIdentity.Ali, "reply"),
            new FakePeer(AssistantIdentity.Scarlett, "reply"),
        };
        await using var service = await AssistantBusHttpService.StartAsync(settings, dataRoot: serviceRoot, peers: servicePeers);
        using var codex = new AssistantBusHttpClient(settings, AssistantIdentity.Codex);
        var waitId = $"wait:{Guid.NewGuid():N}";
        var waitTask = codex.WaitAsync(0, 10_000, waitId);
        await Task.Delay(200);
        var cancelled = await codex.CancelAsync(waitId);
        Check.True(cancelled.Cancelled);
        var result = await waitTask;
        Check.True(result is not null && result.Cancelled && result.WaitId == waitId);
        Check.Equal(0, result!.Messages.Count);
    });

    await suite.RunAsync("CLI wait accepts after and timeout together in either order", async () =>
    {
        Check.Equal((24L, 30L), AssistantBusCli.ParseWaitArguments(["--after", "24", "--timeout", "30"]));
        Check.Equal((24L, 30L), AssistantBusCli.ParseWaitArguments(["--timeout", "30", "--after", "24"]));
        await Check.ThrowsAsync<AssistantBusValidationException>(() => Task.Run(() =>
            AssistantBusCli.ParseWaitArguments(["--after", "24", "--timeout", "0"])));
    });

    await suite.RunAsync("loopback adapter projects one authenticated passive observation", async () =>
    {
        string? observedJson = null;
        var handler = new RecordingHandler(async request =>
        {
            Check.Equal(HttpMethod.Post, request.Method);
            Check.Equal("/v1/observe", request.RequestUri!.AbsolutePath);
            Check.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Check.Equal(new string('A', 64), request.Headers.Authorization.Parameter!);
            observedJson = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"accepted\":true}", System.Text.Encoding.UTF8, "application/json"),
            };
        });
        using var bridge = new LoopbackAssistantPeerBridge(new AssistantPeerConnection(
            AssistantIdentity.Photon,
            new Uri("http://127.0.0.1:18972/"),
            new string('A', 64),
            "visible-turn"), handler);
        var state = await bridge.ObserveAsync(new AssistantBusMessage(
            1, "main", 1, "bus:12345", AssistantIdentity.Codex, AssistantIdentity.Ali,
            "Passive observation.", DateTimeOffset.UtcNow, null, true, "bus:12345"), CancellationToken.None);
        Check.Equal(AssistantDeliveryState.Delivered, state);
        using var payload = JsonDocument.Parse(observedJson!);
        Check.Equal("Codex", payload.RootElement.GetProperty("sender").GetString()!);
        Check.Equal("Ali", payload.RootElement.GetProperty("recipient").GetString()!);
        Check.Equal("Passive observation.", payload.RootElement.GetProperty("body").GetString()!);
    });

    await suite.RunAsync("Photon open-without-session is online and first turn establishes its session", async () =>
    {
        var requestCount = 0;
        var handler = new RecordingHandler(request =>
        {
            requestCount++;
            Check.Equal("Bearer", request.Headers.Authorization!.Scheme);
            if (request.Method == HttpMethod.Get)
            {
                Check.Equal("/v1/session", request.RequestUri!.AbsolutePath);
                return Task.FromResult(JsonResponse(requestCount == 1
                    ? "{\"state\":\"open\",\"isBusy\":false,\"sessionId\":null,\"messages\":[],\"activities\":[]}"
                    : "{\"state\":\"open\",\"isBusy\":false,\"sessionId\":\"session-1\",\"messages\":[],\"activities\":[]}"));
            }
            Check.Equal(HttpMethod.Post, request.Method);
            Check.Equal("/v1/turns", request.RequestUri!.AbsolutePath);
            return Task.FromResult(JsonResponse("{\"state\":\"open\",\"isBusy\":false,\"sessionId\":\"session-1\",\"messages\":[{\"id\":\"assistant-1\",\"role\":\"assistant\",\"text\":\"Ready.\"}],\"activities\":[]}"));
        });
        using var bridge = new LoopbackAssistantPeerBridge(new AssistantPeerConnection(
            AssistantIdentity.Photon,
            new Uri("http://127.0.0.1:18972/"),
            new string('A', 64),
            "visible-turn"), handler);
        var status = await bridge.GetStatusAsync(CancellationToken.None);
        Check.True(status.Online);
        Check.Equal("unbound", status.ConversationId!);
        var reply = await bridge.SendAsync(new AssistantBusMessage(
            1, "main", 1, "bus:12345", AssistantIdentity.Codex, AssistantIdentity.Photon,
            "Development checkpoint.", DateTimeOffset.UtcNow, null, true, "bus:12345"), CancellationToken.None);
        Check.Equal(AssistantDeliveryState.Completed, reply.State);
        Check.Equal("Ready.", reply.Body!);
        Check.Equal("session-1", reply.ConversationId!);
    });

    await suite.RunAsync("peer renderer generation replacement retires an in-flight result", async () =>
    {
        var requestCount = 0;
        var handler = new RecordingHandler(request =>
        {
            requestCount++;
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(JsonResponse($"{{\"state\":\"open\",\"isBusy\":false,\"sessionId\":\"session-1\",\"generation\":\"renderer:{requestCount}\",\"messages\":[],\"activities\":[]}}"));
            return Task.FromResult(JsonResponse("{\"state\":\"open\",\"isBusy\":false,\"sessionId\":\"session-1\",\"generation\":\"renderer:2\",\"messages\":[{\"id\":\"assistant-1\",\"role\":\"assistant\",\"text\":\"Late reply.\"}],\"activities\":[]}"));
        });
        using var bridge = new LoopbackAssistantPeerBridge(new AssistantPeerConnection(
            AssistantIdentity.Photon,
            new Uri("http://127.0.0.1:18972/"),
            new string('A', 64),
            "visible-turn"), handler);
        var reply = await bridge.SendAsync(new AssistantBusMessage(
            1, "main", 1, "bus:12345", AssistantIdentity.Codex, AssistantIdentity.Photon,
            "Generation check.", DateTimeOffset.UtcNow, null, true, "bus:12345"), CancellationToken.None);
        Check.Equal(AssistantDeliveryState.StaleGeneration, reply.State);
        Check.Equal("peer_generation_changed", reply.Code!);
    });
}
finally
{
    Directory.Delete(root, recursive: true);
}

suite.Complete();

static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
{
    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
};

static int ReservePort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
    finally { listener.Stop(); }
}

internal sealed class FakePeer(AssistantIdentity identity, string reply) : IAssistantPeerBridge
{
    public AssistantIdentity Identity { get; } = identity;
    public int SendCount { get; private set; }
    public int ObserveCount { get; private set; }
    public List<string> ObservedHeaders { get; } = [];
    public AssistantDeliveryState NextState { get; set; } = AssistantDeliveryState.Completed;

    public Task<AssistantParticipantStatus> GetStatusAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new AssistantParticipantStatus(Identity, true, false, "invoke-only", $"{Identity}-conversation", "generation-1"));

    public Task<AssistantPeerReply> SendAsync(AssistantBusMessage message, CancellationToken cancellationToken)
    {
        SendCount++;
        var state = NextState;
        NextState = AssistantDeliveryState.Completed;
        return Task.FromResult(new AssistantPeerReply(
            state,
            state == AssistantDeliveryState.Completed ? reply : null,
            $"{Identity}-conversation",
            "generation-1",
            state == AssistantDeliveryState.Completed ? null : state.ToString().ToLowerInvariant()));
    }

    public Task<AssistantDeliveryState> ObserveAsync(AssistantBusMessage message, CancellationToken cancellationToken)
    {
        ObserveCount++;
        ObservedHeaders.Add(message.Header);
        return Task.FromResult(AssistantDeliveryState.Delivered);
    }
}

internal sealed class RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request);
}

internal sealed class SmokeSuite
{
    private int _passed;
    private int _failed;

    public async Task RunAsync(string name, Func<Task> test)
    {
        try { await test(); _passed++; Console.WriteLine($"PASS {name}"); }
        catch (Exception exception) { _failed++; Console.WriteLine($"FAIL {name}: {exception.GetType().Name} {exception.Message}"); }
    }

    public void Complete()
    {
        Console.WriteLine($"RESULT {_passed} passed, {_failed} failed, {_passed + _failed} total");
        if (_failed != 0) Environment.ExitCode = 1;
    }
}

internal static class Check
{
    public static void True(bool value) { if (!value) throw new InvalidOperationException("Assertion failed."); }
    public static void Equal<T>(T expected, T actual) where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }

    public static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
