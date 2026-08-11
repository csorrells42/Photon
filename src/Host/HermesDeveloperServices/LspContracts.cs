using System.Text.Json;
using System.Text.Json.Serialization;

namespace HermesDeveloperServices;

public enum LspSessionState
{
    Created,
    Initializing,
    Ready,
    ShuttingDown,
    Exited,
    Faulted,
}

public sealed record LspPosition(int Line, int Character);

public sealed record LspRange(LspPosition Start, LspPosition End);

public sealed record LspTextDocumentIdentifier(string Uri);

public sealed record LspVersionedTextDocumentIdentifier(string Uri, int Version);

public sealed record LspTextDocumentItem(string Uri, string LanguageId, int Version, string Text);

public sealed record LspTextDocumentPositionParams(
    LspTextDocumentIdentifier TextDocument,
    LspPosition Position);

public sealed record LspDiagnostic(
    LspRange Range,
    int? Severity,
    JsonElement? Code,
    string? Source,
    string Message);

public sealed record LspPublishDiagnosticsParams(
    string Uri,
    IReadOnlyList<LspDiagnostic> Diagnostics,
    int? Version = null);

public sealed record LspServerInfo(string Name, string? Version = null);

public sealed record LspInitializeResult(JsonElement Capabilities, LspServerInfo? ServerInfo = null);

public sealed record LspError(int Code, string Message, JsonElement? Data = null);

public abstract record LspOutgoingMessage;

public sealed record LspRequest(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("params")] JsonElement? Params = null,
    [property: JsonPropertyName("jsonrpc")] string JsonRpc = "2.0") : LspOutgoingMessage;

public sealed record LspNotification(
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("params")] JsonElement? Params = null,
    [property: JsonPropertyName("jsonrpc")] string JsonRpc = "2.0") : LspOutgoingMessage;

public sealed record LspClientResponse(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("result")] JsonElement? Result = null,
    [property: JsonPropertyName("error")] LspError? Error = null,
    [property: JsonPropertyName("jsonrpc")] string JsonRpc = "2.0") : LspOutgoingMessage;

public abstract record LspIncomingMessage;

public sealed record LspIncomingResponse(
    int Id,
    JsonElement? Result,
    LspError? Error) : LspIncomingMessage;

public sealed record LspIncomingNotification(
    string Method,
    JsonElement? Params) : LspIncomingMessage;

public sealed record LspIncomingServerRequest(
    int Id,
    string Method,
    JsonElement? Params) : LspIncomingMessage;

public sealed class LspProtocolException : Exception
{
    public LspProtocolException(string message) : base(message) { }

    public LspProtocolException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class LspSessionException : Exception
{
    public LspSessionException(string message) : base(message) { }
}
