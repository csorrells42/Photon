using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HermesDesktop;

internal sealed record WorkspaceSearchMatchRange(int Start, int End);
internal sealed record WorkspaceSearchNativeResult(
    string Path,
    int Line,
    int Column,
    string Preview,
    IReadOnlyList<WorkspaceSearchMatchRange> Matches);

/// <summary>
/// Fixed, read-only Serena MCP adapter. Renderer messages can provide intent and
/// result bounds only; endpoint, server, tool, transport and tool arguments are
/// compiled native policy.
/// </summary>
internal sealed partial class SerenaWorkspaceSearchClient : IAsyncDisposable
{
    internal static readonly Uri Endpoint = new("http://127.0.0.1:9121/mcp");
    private const int MaximumResponseBytes = 256 * 1024;
    private const int MaximumIntentCharacters = 512;
    private const int MaximumTokens = 3;
    private const string ProtocolVersion = "2025-06-18";
    private static readonly IReadOnlyDictionary<string, string> FixedFindSymbolFields =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["name_path_pattern"] = "string",
            ["depth"] = "integer",
            ["relative_path"] = "string",
            ["include_body"] = "boolean",
            ["include_info"] = "boolean",
            ["include_kinds"] = "array:integer",
            ["exclude_kinds"] = "array:integer",
            ["substring_matching"] = "boolean",
            ["max_matches"] = "integer",
            ["max_answer_chars"] = "integer",
        };
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "as", "at", "be", "behavior", "by", "code", "does", "find", "for",
        "from", "how", "in", "is", "it", "of", "on", "or", "search", "show", "that", "the", "this",
        "to", "where", "which", "with",
    };

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    internal SerenaWorkspaceSearchClient(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _ownsHttp = true;
    }

    internal async Task<IReadOnlyList<WorkspaceSearchNativeResult>> SearchAsync(
        string intent,
        int maximumResults,
        int maximumResultsPerFile,
        int maximumPreviewCharacters,
        WorkspaceSearchPathPolicy pathPolicy,
        CancellationToken cancellationToken)
    {
        var tokens = IntentTokens(intent);
        if (tokens.Count == 0) return Array.Empty<WorkspaceSearchNativeResult>();
        var resultLimit = Math.Clamp(maximumResults, 1, 200);
        var perFileLimit = Math.Clamp(maximumResultsPerFile, 1, 25);
        var previewLimit = Math.Clamp(maximumPreviewCharacters, 1, 1_024);
        var requestNonce = Guid.NewGuid().ToString("N");
        string? sessionId = null;
        try
        {
            var initializeRequestId = $"{requestNonce}:initialize";
            var initialized = await SendRequestAsync(new
            {
                jsonrpc = "2.0",
                id = initializeRequestId,
                method = "initialize",
                @params = new
                {
                    protocolVersion = ProtocolVersion,
                    capabilities = new { },
                    clientInfo = new { name = "photos-agape-aphthartos-workspace-search", version = "1" },
                },
            }, initializeRequestId, null, cancellationToken).ConfigureAwait(false);
            sessionId = initialized.SessionId;
            if (string.IsNullOrEmpty(sessionId) || !IsSerenaInitializeResponse(initialized.Document.RootElement))
            {
                throw new InvalidDataException("The trusted Serena search service did not complete its fixed handshake.");
            }
            initialized.Document.Dispose();

            await SendNotificationAsync(new
            {
                jsonrpc = "2.0",
                method = "notifications/initialized",
                @params = new { },
            }, sessionId, cancellationToken).ConfigureAwait(false);

            var toolsRequestId = $"{requestNonce}:tools-list";
            using var tools = (await SendRequestAsync(new
            {
                jsonrpc = "2.0",
                id = toolsRequestId,
                method = "tools/list",
                @params = new { },
            }, toolsRequestId, sessionId, cancellationToken).ConfigureAwait(false)).Document;
            if (!HasFixedFindSymbolTool(tools.RootElement))
            {
                throw new InvalidDataException("The trusted Serena search service does not expose the required read-only symbol tool.");
            }

            var locations = new List<(string Path, int ZeroBasedLine, string Token)>();
            var locationKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var tokenIndex = 0; tokenIndex < tokens.Count; tokenIndex++)
            {
                var token = tokens[tokenIndex];
                cancellationToken.ThrowIfCancellationRequested();
                var callRequestId = $"{requestNonce}:find-symbol:{tokenIndex}";
                using var call = (await SendRequestAsync(new
                {
                    jsonrpc = "2.0",
                    id = callRequestId,
                    method = "tools/call",
                    @params = new
                    {
                        name = "find_symbol",
                        arguments = new
                        {
                            name_path_pattern = token,
                            depth = 0,
                            relative_path = "",
                            include_body = false,
                            include_info = false,
                            include_kinds = Array.Empty<int>(),
                            exclude_kinds = Array.Empty<int>(),
                            substring_matching = true,
                            max_matches = resultLimit,
                            max_answer_chars = 64 * 1024,
                        },
                    },
                }, callRequestId, sessionId, cancellationToken).ConfigureAwait(false)).Document;
                foreach (var location in ParseSymbolLocations(call.RootElement, token))
                {
                    if (!WorkspaceSearchPathPolicy.IsSafeRelativePath(location.Path)) continue;
                    var key = $"{location.Path}\0{location.ZeroBasedLine}";
                    if (locationKeys.Add(key)) locations.Add(location);
                    if (locations.Count >= resultLimit) break;
                }
                if (locations.Count >= resultLimit) break;
            }

            var perFile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var results = new List<WorkspaceSearchNativeResult>();
            foreach (var location in locations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = perFile.GetValueOrDefault(location.Path);
                if (count >= perFileLimit) continue;
                var preview = await pathPolicy.ReadVerifiedLineAsync(location.Path, location.ZeroBasedLine, previewLimit, cancellationToken).ConfigureAwait(false);
                if (preview is null) continue;
                var match = preview.IndexOf(location.Token, StringComparison.OrdinalIgnoreCase);
                var ranges = match >= 0
                    ? new[] { new WorkspaceSearchMatchRange(match, Math.Min(preview.Length, match + location.Token.Length)) }
                    : Array.Empty<WorkspaceSearchMatchRange>();
                var column = match >= 0 ? match + 1 : 1;
                results.Add(new WorkspaceSearchNativeResult(location.Path.Replace('\\', '/'), location.ZeroBasedLine + 1, column, preview, ranges));
                perFile[location.Path] = count + 1;
                if (results.Count >= resultLimit) break;
            }
            return results;
        }
        finally
        {
            if (!string.IsNullOrEmpty(sessionId)) await TryCloseSessionAsync(sessionId).ConfigureAwait(false);
        }
    }

    internal static IReadOnlyList<string> IntentTokens(string? intent)
    {
        var bounded = (intent ?? string.Empty).Replace('\0', ' ').Trim();
        if (bounded.Length > MaximumIntentCharacters) bounded = bounded[..MaximumIntentCharacters];
        return IntentTokenPattern().Matches(bounded)
            .Select(match => match.Value)
            .Where(value => !StopWords.Contains(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(value => value.Length)
            .ThenBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumTokens)
            .ToArray();
    }

    internal static IReadOnlyList<(string Path, int ZeroBasedLine, string Token)> ParseSymbolLocations(JsonElement root, string token)
    {
        var locations = new List<(string Path, int ZeroBasedLine, string Token)>();
        if (!TryResult(root, out var result)
            || (result.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True)
            || !result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return locations;
        foreach (var part in content.EnumerateArray())
        {
            if (!part.TryGetProperty("type", out var type) || type.GetString() != "text"
                || !part.TryGetProperty("text", out var textValue) || textValue.ValueKind != JsonValueKind.String) continue;
            var text = textValue.GetString() ?? string.Empty;
            if (Encoding.UTF8.GetByteCount(text) > MaximumResponseBytes) continue;
            try
            {
                using var payload = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
                if (payload.RootElement.ValueKind != JsonValueKind.Array) continue;
                foreach (var symbol in payload.RootElement.EnumerateArray())
                {
                    if (symbol.ValueKind != JsonValueKind.Object
                        || !symbol.TryGetProperty("relative_path", out var pathValue) || pathValue.ValueKind != JsonValueKind.String
                        || !symbol.TryGetProperty("body_location", out var location) || location.ValueKind != JsonValueKind.Object
                        || !location.TryGetProperty("start_line", out var lineValue) || !lineValue.TryGetInt32(out var line)
                        || line < 0 || line > 10_000_000) continue;
                    var path = pathValue.GetString() ?? string.Empty;
                    if (path.Length is > 0 and <= WorkspaceSearchPathPolicy.MaximumRelativePathCharacters)
                    {
                        locations.Add((path, line, token));
                    }
                }
            }
            catch (JsonException) { /* Provider prose and shortened summaries are not trusted location data. */ }
        }
        return locations;
    }

    private async Task<(JsonDocument Document, string? SessionId)> SendRequestAsync(
        object payload,
        string expectedRequestId,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, payload, sessionId);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("The trusted Serena search request failed.", null, response.StatusCode);
        var body = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
        var document = ParseMcpResponse(body);
        if (!HasExactResponseId(document.RootElement, expectedRequestId))
        {
            document.Dispose();
            throw new InvalidDataException("The trusted Serena search response did not match the exact request.");
        }
        var returnedSession = SessionHeader(response) ?? sessionId;
        return (document, returnedSession);
    }

    private async Task SendNotificationAsync(object payload, string sessionId, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, payload, sessionId);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("The trusted Serena search handshake failed.", null, response.StatusCode);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, object? payload, string? sessionId)
    {
        var request = new HttpRequestMessage(method, Endpoint);
        request.Headers.Host = "localhost:9121";
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (!string.IsNullOrEmpty(sessionId)) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        if (payload is not null)
        {
            request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(payload));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        return request;
    }

    private async Task TryCloseSessionAsync(string sessionId)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Delete, null, sessionId);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException) { }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > MaximumResponseBytes) throw new InvalidDataException("The trusted Serena search response exceeded its bound.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static JsonDocument ParseMcpResponse(byte[] body)
    {
        var text = Encoding.UTF8.GetString(body);
        if (text.TrimStart().StartsWith("data:", StringComparison.Ordinal) || text.Contains("\ndata:", StringComparison.Ordinal))
        {
            var dataFrames = text.Split('\n')
                .Select(line => line.TrimEnd('\r'))
                .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
                .Select(line => line[5..].TrimStart())
                .Where(line => line.Length > 0 && line != "[DONE]")
                .ToArray();
            if (dataFrames.Length != 1) throw new InvalidDataException("The trusted Serena search response did not contain exactly one result frame.");
            return JsonDocument.Parse(dataFrames[0], new JsonDocumentOptions { MaxDepth = 64 });
        }
        return JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 64 });
    }

    private static bool IsSerenaInitializeResponse(JsonElement root)
    {
        if (!TryResult(root, out var result) || !result.TryGetProperty("serverInfo", out var serverInfo)
            || serverInfo.ValueKind != JsonValueKind.Object || !serverInfo.TryGetProperty("name", out var name)) return false;
        return string.Equals(name.GetString(), "serena", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasFixedFindSymbolTool(JsonElement root)
    {
        if (!TryResult(root, out var result) || !result.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array) return false;
        var matches = tools.EnumerateArray().Where(tool => tool.ValueKind == JsonValueKind.Object
            && tool.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
            && name.GetString() == "find_symbol").ToArray();
        if (matches.Length != 1) return false;
        var tool = matches[0];
        return tool.TryGetProperty("annotations", out var annotations)
            && annotations.ValueKind == JsonValueKind.Object
            && annotations.TryGetProperty("readOnlyHint", out var readOnlyHint)
            && readOnlyHint.ValueKind == JsonValueKind.True
            && annotations.TryGetProperty("destructiveHint", out var destructiveHint)
            && destructiveHint.ValueKind == JsonValueKind.False
            && tool.TryGetProperty("inputSchema", out var schema)
            && IsFixedFindSymbolSchema(schema);
    }

    private static bool IsFixedFindSymbolSchema(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("type", out var type) || type.GetString() != "object"
            || !schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("required", out var required) || required.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var propertyList = properties.EnumerateObject().ToArray();
        if (propertyList.Length != FixedFindSymbolFields.Count
            || propertyList.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != propertyList.Length)
        {
            return false;
        }
        foreach (var property in propertyList)
        {
            if (!FixedFindSymbolFields.TryGetValue(property.Name, out var expectedType)
                || property.Value.ValueKind != JsonValueKind.Object
                || !HasExactSchemaType(property.Value, expectedType)) return false;
        }

        var requiredFields = required.EnumerateArray().ToArray();
        return requiredFields.Length == 1
            && requiredFields[0].ValueKind == JsonValueKind.String
            && requiredFields[0].GetString() == "name_path_pattern";
    }

    private static bool HasExactSchemaType(JsonElement schema, string expectedType)
    {
        if (expectedType.StartsWith("array:", StringComparison.Ordinal))
        {
            return schema.TryGetProperty("type", out var arrayType) && arrayType.GetString() == "array"
                && schema.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object
                && items.TryGetProperty("type", out var itemType)
                && itemType.GetString() == expectedType["array:".Length..];
        }
        return schema.TryGetProperty("type", out var type) && type.GetString() == expectedType;
    }

    private static bool HasExactResponseId(JsonElement root, string expectedRequestId)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        var identifiers = root.EnumerateObject().Where(property => property.NameEquals("id")).ToArray();
        return identifiers.Length == 1
            && identifiers[0].Value.ValueKind == JsonValueKind.String
            && string.Equals(identifiers[0].Value.GetString(), expectedRequestId, StringComparison.Ordinal);
    }

    private static bool TryResult(JsonElement root, out JsonElement result)
    {
        result = default;
        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("jsonrpc", out var jsonrpc) && jsonrpc.GetString() == "2.0"
            && !root.TryGetProperty("error", out _)
            && root.TryGetProperty("result", out result) && result.ValueKind == JsonValueKind.Object;
    }

    private static string? SessionHeader(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Mcp-Session-Id", out var values)) return null;
        var value = values.SingleOrDefault()?.Trim();
        return value is { Length: > 0 and <= 256 } && value.All(character => character is >= '!' and <= '~') ? value : null;
    }

    public ValueTask DisposeAsync()
    {
        if (_ownsHttp) _http.Dispose();
        return ValueTask.CompletedTask;
    }

    [GeneratedRegex(@"[A-Za-z_$][A-Za-z0-9_$]{1,127}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex IntentTokenPattern();
}
