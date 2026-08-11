using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using HermesDeveloperServices;

internal sealed class FakeLspAdapter : ILspMessageTransport
{
    private readonly Channel<LspIncomingMessage> _messages = Channel.CreateUnbounded<LspIncomingMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private int _serverRequestId = 10_000;

    public ConcurrentQueue<string> Methods { get; } = new();

    public ConcurrentQueue<LspOutgoingMessage> Outgoing { get; } = new();

    public bool HoldHoverResponses { get; set; }

    public ValueTask SendAsync(LspOutgoingMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Outgoing.Enqueue(message);
        switch (message)
        {
            case LspRequest request:
                Methods.Enqueue(request.Method);
                HandleRequest(request);
                break;
            case LspNotification notification:
                Methods.Enqueue(notification.Method);
                HandleNotification(notification);
                break;
            case LspClientResponse:
                break;
        }
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<LspIncomingMessage> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var message in _messages.Reader.ReadAllAsync(cancellationToken)) yield return message;
    }

    public void EmitServerRequest(string method) =>
        _messages.Writer.TryWrite(new LspIncomingServerRequest(
            Interlocked.Increment(ref _serverRequestId),
            method,
            JsonSerializer.SerializeToElement(new { registrations = Array.Empty<object>() })));

    public void Complete() => _messages.Writer.TryComplete();

    public ValueTask DisposeAsync()
    {
        _messages.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private void HandleRequest(LspRequest request)
    {
        switch (request.Method)
        {
            case "initialize":
                Respond(request, new
                {
                    capabilities = new
                    {
                        textDocumentSync = 1,
                        completionProvider = new { triggerCharacters = new[] { "." } },
                        hoverProvider = true,
                        definitionProvider = true,
                        referencesProvider = true,
                        renameProvider = true,
                    },
                    serverInfo = new { name = "Hermes fake LSP", version = "1.0" },
                });
                break;
            case "textDocument/hover" when HoldHoverResponses:
                break;
            case "textDocument/hover":
                Respond(request, new { contents = new { kind = "markdown", value = "`answer`: int" } });
                break;
            case "textDocument/completion":
                Respond(request, new { isIncomplete = false, items = new[] { new { label = "Answer", kind = 6 } } });
                break;
            case "textDocument/definition":
            case "textDocument/references":
                Respond(request, new[]
                {
                    new { uri = "file:///C:/fixture/Program.cs", range = new { start = new { line = 0, character = 6 }, end = new { line = 0, character = 12 } } },
                });
                break;
            case "textDocument/rename":
                Respond(request, new { changes = new Dictionary<string, object[]> { ["file:///C:/fixture/Program.cs"] = Array.Empty<object>() } });
                break;
            case "shutdown":
                Respond(request, result: null);
                break;
            default:
                _messages.Writer.TryWrite(new LspIncomingResponse(
                    request.Id,
                    null,
                    new LspError(-32601, "unsupported")));
                break;
        }
    }

    private void HandleNotification(LspNotification notification)
    {
        if (notification.Method == "textDocument/didOpen")
        {
            _messages.Writer.TryWrite(new LspIncomingNotification(
                "textDocument/publishDiagnostics",
                JsonSerializer.SerializeToElement(new
                {
                    uri = "file:///C:/fixture/Program.cs",
                    version = 1,
                    diagnostics = new[]
                    {
                        new
                        {
                            range = new { start = new { line = 0, character = 0 }, end = new { line = 0, character = 5 } },
                            severity = 2,
                            code = "CS0001",
                            source = "fake-lsp",
                            message = "Synthetic warning",
                        },
                    },
                })));
        }
    }

    private void Respond(LspRequest request, object? result)
    {
        var element = result is null ? JsonSerializer.SerializeToElement<object?>(null) : JsonSerializer.SerializeToElement(result);
        _messages.Writer.TryWrite(new LspIncomingResponse(request.Id, element, null));
    }
}
