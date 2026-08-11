namespace AssistantConversationBus;

public sealed class AssistantBusRuntime : IAsyncDisposable
{
    private readonly AssistantBusLedger _ledger;
    private readonly IReadOnlyList<IDisposable> _peerDisposables;

    private AssistantBusRuntime(
        AssistantBusLedger ledger,
        IReadOnlyList<IAssistantPeerBridge> peers,
        IReadOnlyList<IDisposable> peerDisposables)
    {
        _ledger = ledger;
        _peerDisposables = peerDisposables;
        Router = new AssistantBusRouter(ledger, peers);
    }

    public AssistantBusRouter Router { get; }

    public static string DefaultDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "hermes",
        "assistant-conversation-bus");

    public static async Task<AssistantBusRuntime> OpenAsync(
        CancellationToken cancellationToken = default,
        string? dataRoot = null,
        IEnumerable<IAssistantPeerBridge>? peers = null)
    {
        var ownedPeers = peers is null
            ? AssistantPeerSettings.LoadInstalledPeerBridges()
            : peers.ToArray();
        var disposables = ownedPeers.OfType<IDisposable>().ToArray();
        try
        {
            var ledger = await AssistantBusLedger.OpenAsync(
                Path.Combine(dataRoot ?? DefaultDataRoot, "main.jsonl"),
                cancellationToken).ConfigureAwait(false);
            return new AssistantBusRuntime(ledger, ownedPeers, disposables);
        }
        catch
        {
            foreach (var peer in disposables) peer.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var peer in _peerDisposables) peer.Dispose();
        await _ledger.DisposeAsync().ConfigureAwait(false);
    }
}
