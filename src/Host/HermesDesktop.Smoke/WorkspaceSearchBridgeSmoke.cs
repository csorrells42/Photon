using System.Net;
using System.Net.Http;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HermesDesktop;

internal static class WorkspaceSearchBridgeSmoke
{
    internal static async Task RunAsync()
    {
        VerifyPathPolicyVocabulary();
        Console.WriteLine("Desktop Workspace Search path vocabulary smoke passed.");
        var root = Path.Combine(Path.GetTempPath(), $"photon-workspace-search-{Guid.NewGuid():N}");
        var outside = Path.Combine(Path.GetTempPath(), $"photon-workspace-search-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(root, "src", "SessionController.cs"),
            "class SessionController { }\n// Photon opens the session controller\n", new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(root, ".ENV"), "PHOTON_SECRET=must-never-cross");
        await File.WriteAllTextAsync(Path.Combine(root, "nested", "AUTH.JSON"), "must-never-cross");
        await File.WriteAllBytesAsync(Path.Combine(root, "src", "binary.dat"), new byte[] { 1, 0, 2 });
        await File.WriteAllTextAsync(Path.Combine(outside, "outside.cs"), "Photon must-never-cross");
        var junction = Path.Combine(root, "linked");
        try
        {
            await CreateJunctionAsync(junction, outside);
            await VerifyLiteralBridgeAsync(root).WaitAsync(TimeSpan.FromSeconds(5));
            Console.WriteLine("Desktop Workspace Search literal/redaction smoke passed.");
            await VerifyFixedSerenaBridgeAsync(root).WaitAsync(TimeSpan.FromSeconds(5));
            Console.WriteLine("Desktop Workspace Search fixed Serena smoke passed.");
            await VerifySerenaProtocolRejectionsAsync(root).WaitAsync(TimeSpan.FromSeconds(10));
            Console.WriteLine("Desktop Workspace Search Serena request binding and schema rejection smoke passed.");
            await VerifyCancellationAsync(root).WaitAsync(TimeSpan.FromSeconds(5));
            Console.WriteLine("Desktop Workspace Search native path, literal, Serena, redaction, and cancellation smoke passed.");
        }
        finally
        {
            if (Directory.Exists(junction)) Directory.Delete(junction);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (Directory.Exists(outside)) Directory.Delete(outside, recursive: true);
        }
    }

    private static void VerifyPathPolicyVocabulary()
    {
        var blocked = new[]
        {
            ".ENV", ".env.production", "nested/AUTH.JSON", "nested/Credentials.Json", "nested/secrets.json",
            ".git/config", ".SSH/id_ed25519", "logs/runtime.log", "data/state.db", "vault/opaque.ref",
            "keys/client.KEY", "keys/client.PEM", "keys/client.PFX", "file.txt:stream", "COM1.txt", "COM¹.txt", "LPT³",
            "trailing. ", "../escape.cs", "C:/absolute.cs", "//server/share.cs",
        };
        if (blocked.Any(WorkspaceSearchPathPolicy.IsSafeRelativePath)
            || !WorkspaceSearchPathPolicy.IsSafeRelativePath("src/SessionController.cs"))
        {
            throw new InvalidOperationException("Workspace Search path vocabulary smoke failed.");
        }
    }

    private static async Task VerifyLiteralBridgeAsync(string root)
    {
        var frames = new List<JsonElement>();
        await using var bridge = new WorkspaceSearchBridge(root, frame => frames.Add(JsonSerializer.SerializeToElement(frame)));
        await bridge.SearchLiteralAsync(1, "literal:smoke", "Photon", 20, 5, 256);
        var result = frames.Single(frame => Text(frame, "type") == "workspaceSearch.literal.result");
        var payload = result.GetProperty("results").EnumerateArray().ToArray();
        if (payload.Length != 1 || Text(payload[0], "path") != "src/SessionController.cs"
            || payload[0].GetProperty("line").GetInt32() != 2
            || !Text(payload[0], "preview")!.Contains("Photon", StringComparison.Ordinal)
            || frames.Any(frame => frame.ToString().Contains("must-never-cross", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Workspace Search literal safety smoke failed.");
        }
    }

    private static async Task VerifyFixedSerenaBridgeAsync(string root)
    {
        var handler = new SerenaSmokeHandler();
        var frames = new List<JsonElement>();
        await using var client = new SerenaWorkspaceSearchClient(handler);
        await using var bridge = new WorkspaceSearchBridge(root, frame => frames.Add(JsonSerializer.SerializeToElement(frame)), client);
        await bridge.SearchSemanticAsync(1, "semantic:smoke", "find SessionController", 20, 5, 256);
        var result = frames.Single(frame => Text(frame, "type") == "workspaceSearch.semantic.result");
        var payload = result.GetProperty("results").EnumerateArray().ToArray();
        if (payload.Length != 1 || Text(payload[0], "path") != "src/SessionController.cs"
            || payload[0].GetProperty("line").GetInt32() != 1
            || handler.ObservedEndpoint != SerenaWorkspaceSearchClient.Endpoint
            || !string.Equals(handler.ObservedHost, "localhost:9121", StringComparison.Ordinal)
            || handler.ToolName != "find_symbol"
            || !HasFixedReadOnlyArguments(handler.ToolArguments)
            || handler.RawToolRequest.Contains("find SessionController", StringComparison.Ordinal)
            || handler.ObservedRequestIds.Count != 3
            || handler.ObservedRequestIds.Distinct(StringComparer.Ordinal).Count() != 3
            || frames.Any(frame => frame.ToString().Contains("must-never-cross", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Workspace Search fixed Serena boundary smoke failed.");
        }
    }

    private static bool HasFixedReadOnlyArguments(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) return false;
        var fields = arguments.EnumerateObject().ToArray();
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            "name_path_pattern", "depth", "relative_path", "include_body", "include_info",
            "include_kinds", "exclude_kinds", "substring_matching", "max_matches", "max_answer_chars",
        };
        return fields.Length == expected.Count
            && fields.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() == fields.Length
            && fields.All(field => expected.Contains(field.Name))
            && Text(arguments, "name_path_pattern") == "SessionController"
            && arguments.GetProperty("depth").GetInt32() == 0
            && Text(arguments, "relative_path") == string.Empty
            && arguments.GetProperty("include_body").ValueKind == JsonValueKind.False
            && arguments.GetProperty("include_info").ValueKind == JsonValueKind.False
            && arguments.GetProperty("include_kinds").ValueKind == JsonValueKind.Array
            && arguments.GetProperty("include_kinds").GetArrayLength() == 0
            && arguments.GetProperty("exclude_kinds").ValueKind == JsonValueKind.Array
            && arguments.GetProperty("exclude_kinds").GetArrayLength() == 0
            && arguments.GetProperty("substring_matching").ValueKind == JsonValueKind.True
            && arguments.GetProperty("max_matches").GetInt32() == 20
            && arguments.GetProperty("max_answer_chars").GetInt32() == 64 * 1024;
    }

    private static async Task VerifySerenaProtocolRejectionsAsync(string root)
    {
        foreach (var mutation in new[]
        {
            SerenaResponseMutation.MissingInitializeId,
            SerenaResponseMutation.WrongToolsListId,
            SerenaResponseMutation.DuplicateToolCallId,
            SerenaResponseMutation.UnsafeFindSymbolSchema,
        })
        {
            var handler = new SerenaSmokeHandler(mutation);
            await using var client = new SerenaWorkspaceSearchClient(handler);
            var policy = new WorkspaceSearchPathPolicy(root);
            try
            {
                await client.SearchAsync("SessionController", 20, 5, 256, policy, CancellationToken.None);
                throw new InvalidOperationException($"Workspace Search accepted the adversarial Serena response {mutation}.");
            }
            catch (InvalidDataException) { }
            if (mutation == SerenaResponseMutation.UnsafeFindSymbolSchema && handler.ToolCallCount != 0)
            {
                throw new InvalidOperationException("Workspace Search called find_symbol before validating its fixed read-only schema.");
            }
        }
    }

    private static async Task VerifyCancellationAsync(string root)
    {
        var handler = new BlockingSerenaHandler();
        var frames = new List<JsonElement>();
        await using var client = new SerenaWorkspaceSearchClient(handler);
        await using var bridge = new WorkspaceSearchBridge(root, frame => frames.Add(JsonSerializer.SerializeToElement(frame)), client);
        var search = bridge.SearchSemanticAsync(1, "semantic:cancelled", "SessionController", 20, 5, 256);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        bridge.Cancel(1, "cancel:smoke", "semantic:cancelled");
        await search;
        if (!frames.Any(frame => Text(frame, "type") == "workspaceSearch.cancel.result" && frame.GetProperty("accepted").GetBoolean())
            || !frames.Any(frame => Text(frame, "type") == "workspaceSearch.error" && Text(frame, "code") == "cancelled"))
        {
            throw new InvalidOperationException("Workspace Search cancellation smoke failed.");
        }
    }

    private static async Task CreateJunctionAsync(string junction, string target)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/d /c mklink /J \"{junction}\" \"{target}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("Workspace Search junction fixture could not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch (TimeoutException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1));
            throw new InvalidOperationException("Workspace Search junction fixture timed out.");
        }
        if (process.ExitCode != 0) throw new InvalidOperationException("Workspace Search junction fixture could not be created.");
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private enum SerenaResponseMutation
    {
        None,
        MissingInitializeId,
        WrongToolsListId,
        DuplicateToolCallId,
        UnsafeFindSymbolSchema,
    }

    private sealed class SerenaSmokeHandler(SerenaResponseMutation mutation = SerenaResponseMutation.None) : HttpMessageHandler
    {
        internal Uri? ObservedEndpoint { get; private set; }
        internal string? ObservedHost { get; private set; }
        internal string? ToolName { get; private set; }
        internal JsonElement ToolArguments { get; private set; }
        internal string RawToolRequest { get; private set; } = string.Empty;
        internal HashSet<string> ObservedRequestIds { get; } = new(StringComparer.Ordinal);
        internal int ToolCallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ObservedEndpoint = request.RequestUri;
            ObservedHost = request.Headers.Host;
            if (request.Method == HttpMethod.Delete) return Response(HttpStatusCode.OK, "{}");
            var raw = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            if (raw.Contains("notifications/initialized", StringComparison.Ordinal)) return Response(HttpStatusCode.Accepted, string.Empty);
            using var document = JsonDocument.Parse(raw);
            var method = Text(document.RootElement, "method");
            var requestId = Text(document.RootElement, "id")
                ?? throw new InvalidOperationException("Workspace Search sent a request without a string identifier.");
            if (!ObservedRequestIds.Add(requestId))
                throw new InvalidOperationException("Workspace Search reused a Serena JSON-RPC request identifier.");
            if (method == "initialize")
            {
                var idProperty = mutation == SerenaResponseMutation.MissingInitializeId
                    ? string.Empty
                    : $",\"id\":{JsonSerializer.Serialize(requestId)}";
                var response = Response(HttpStatusCode.OK,
                    $"{{\"jsonrpc\":\"2.0\"{idProperty},\"result\":{{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{{\"tools\":{{}}}},\"serverInfo\":{{\"name\":\"serena\",\"version\":\"1.6.1\"}}}}}}");
                response.Headers.TryAddWithoutValidation("Mcp-Session-Id", "workspace-search-smoke-session");
                return response;
            }
            if (method == "tools/list")
            {
                var responseId = mutation == SerenaResponseMutation.WrongToolsListId ? $"foreign-{requestId}" : requestId;
                var schema = mutation == SerenaResponseMutation.UnsafeFindSymbolSchema
                    ? "{\"type\":\"object\",\"properties\":{\"name_path_pattern\":{\"type\":\"string\"},\"command\":{\"type\":\"string\"}},\"required\":[\"name_path_pattern\"]}"
                    : FixedFindSymbolSchema;
                return Response(HttpStatusCode.OK,
                    $"{{\"jsonrpc\":\"2.0\",\"id\":{JsonSerializer.Serialize(responseId)},\"result\":{{\"tools\":[{{\"name\":\"find_symbol\",\"annotations\":{{\"readOnlyHint\":true,\"destructiveHint\":false}},\"inputSchema\":{schema}}}]}}}}");
            }
            if (method == "tools/call")
            {
                ToolCallCount++;
                RawToolRequest = raw;
                var parameters = document.RootElement.GetProperty("params");
                ToolName = Text(parameters, "name");
                ToolArguments = parameters.GetProperty("arguments").Clone();
                var symbols = """[{"relative_path":"src/SessionController.cs","body_location":{"start_line":0,"end_line":0}},{"relative_path":".ENV","body_location":{"start_line":0,"end_line":0}},{"relative_path":"C:/outside.cs","body_location":{"start_line":0,"end_line":0}}]""";
                var resultJson = JsonSerializer.Serialize(new { content = new[] { new { type = "text", text = symbols } }, isError = false });
                var idJson = JsonSerializer.Serialize(requestId);
                return mutation == SerenaResponseMutation.DuplicateToolCallId
                    ? Response(HttpStatusCode.OK, $"{{\"jsonrpc\":\"2.0\",\"id\":{idJson},\"id\":{idJson},\"result\":{resultJson}}}")
                    : Response(HttpStatusCode.OK, $"{{\"jsonrpc\":\"2.0\",\"id\":{idJson},\"result\":{resultJson}}}");
            }
            return Response(HttpStatusCode.BadRequest, "{}");
        }

        private static HttpResponseMessage Response(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        private const string FixedFindSymbolSchema = """
            {"type":"object","properties":{
              "name_path_pattern":{"type":"string"},
              "depth":{"type":"integer"},
              "relative_path":{"type":"string"},
              "include_body":{"type":"boolean"},
              "include_info":{"type":"boolean"},
              "include_kinds":{"type":"array","items":{"type":"integer"}},
              "exclude_kinds":{"type":"array","items":{"type":"integer"}},
              "substring_matching":{"type":"boolean"},
              "max_matches":{"type":"integer"},
              "max_answer_chars":{"type":"integer"}
            },"required":["name_path_pattern"]}
            """;
    }

    private sealed class BlockingSerenaHandler : HttpMessageHandler
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Cancellation did not stop the Serena request.");
        }
    }
}
