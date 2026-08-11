#if HERMES_JAVA_JDT_SMOKE
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Text.Json;
using HermesDeveloperServices;

internal sealed class FakeJavaJdtTransport : ILspMessageTransport
{
    private readonly Channel<LspIncomingMessage> _incoming = Channel.CreateUnbounded<LspIncomingMessage>();
    private readonly List<LspOutgoingMessage> _sent = [];

    internal IReadOnlyList<LspOutgoingMessage> Sent => _sent;

    public ValueTask SendAsync(LspOutgoingMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _sent.Add(message);
        if (message is LspRequest request)
        {
            JsonElement? result = request.Method switch
            {
                "initialize" => JsonSerializer.SerializeToElement(new
                {
                    capabilities = new
                    {
                        textDocumentSync = 1,
                        completionProvider = new { },
                        hoverProvider = true,
                        definitionProvider = true,
                        referencesProvider = true,
                        renameProvider = true,
                    },
                    serverInfo = new { name = "fake-eclipse-jdt-ls", version = "0.0.0-fake" },
                }),
                "shutdown" => JsonSerializer.SerializeToElement<object?>(null),
                _ => JsonSerializer.SerializeToElement(new { method = request.Method, ok = true }),
            };
            _incoming.Writer.TryWrite(new LspIncomingResponse(request.Id, result, null));
        }
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<LspIncomingMessage> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var message in _incoming.Reader.ReadAllAsync(cancellationToken)) yield return message;
    }

    internal void PublishDiagnostics(string uri, int version, string message)
    {
        _incoming.Writer.TryWrite(new LspIncomingNotification(
            "textDocument/publishDiagnostics",
            JsonSerializer.SerializeToElement(new
            {
                uri,
                version,
                diagnostics = new[]
                {
                    new
                    {
                        range = new { start = new { line = 0, character = 0 }, end = new { line = 0, character = 1 } },
                        severity = 2,
                        source = "jdt",
                        message,
                    },
                },
            })));
    }

    public ValueTask DisposeAsync()
    {
        _incoming.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
#endif
