using System.Text.Json;
using HermesDeveloperServices;

internal static class FakeLspStdioServer
{
    public static async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        Console.Error.Write(new string('e', 1024));
        Console.Error.Write(Environment.GetEnvironmentVariable("HERMES_LSP_SECRET_SENTINEL"));
        var input = Console.OpenStandardInput();
        var output = Console.OpenStandardOutput();
        var buffer = new byte[4096];
        using var decoder = new LspFrameDecoder();
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken);
            if (count == 0) return 0;
            foreach (var payload in decoder.Append(buffer.AsSpan(0, count)))
            {
                var message = LspMessageCodec.DecodeIncoming(payload);
                switch (message)
                {
                    case LspIncomingServerRequest { Method: "initialize" } initialize:
                        await WriteAsync(output, new
                        {
                            jsonrpc = "2.0",
                            id = initialize.Id,
                            result = new
                            {
                                capabilities = new { hoverProvider = true, textDocumentSync = 1 },
                                serverInfo = new { name = "Hermes stdio fixture", version = "1" },
                            },
                        }, cancellationToken);
                        break;
                    case LspIncomingNotification { Method: "textDocument/didOpen", Params: { } parameters }:
                        var uri = parameters.GetProperty("textDocument").GetProperty("uri").GetString();
                        await WriteAsync(output, new
                        {
                            jsonrpc = "2.0",
                            method = "textDocument/publishDiagnostics",
                            @params = new
                            {
                                uri,
                                diagnostics = new[]
                                {
                                    new
                                    {
                                        range = new
                                        {
                                            start = new { line = 0, character = 0 },
                                            end = new { line = 0, character = 1 },
                                        },
                                        severity = 2,
                                        source = "fixture",
                                        message = "Stdio warning",
                                    },
                                },
                            },
                        }, cancellationToken);
                        break;
                    case LspIncomingServerRequest { Method: "shutdown" } shutdown:
                        await WriteAsync(output, new
                        {
                            jsonrpc = "2.0",
                            id = shutdown.Id,
                            result = JsonSerializer.SerializeToElement<object?>(null),
                        }, cancellationToken);
                        break;
                    case LspIncomingNotification { Method: "exit" }:
                        return 0;
                    case LspIncomingServerRequest request:
                        await WriteAsync(output, new
                        {
                            jsonrpc = "2.0",
                            id = request.Id,
                            error = new { code = -32601, message = "Unsupported fixture method." },
                        }, cancellationToken);
                        break;
                }
            }
        }
    }

    private static async Task WriteAsync(Stream output, object message, CancellationToken cancellationToken)
    {
        var frame = LspMessageCodec.EncodeFrame(message);
        await output.WriteAsync(frame, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }
}
