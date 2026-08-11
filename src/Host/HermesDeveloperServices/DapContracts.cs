using System.Text.Json;
using System.Text.Json.Serialization;

namespace HermesDeveloperServices;

public enum DapSessionState
{
    Created,
    Initializing,
    Initialized,
    Configuring,
    Running,
    Stopped,
    Disconnecting,
    Disconnected,
    Exited,
    Faulted,
}

public enum DapStartMode
{
    Launch,
    Attach,
}

public sealed record DapRequest(
    [property: JsonPropertyName("seq")] int Seq,
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("arguments")] JsonElement? Arguments = null)
{
    [JsonPropertyName("type")]
    public string Type => "request";
}

public sealed record DapResponse(
    [property: JsonPropertyName("seq")] int Seq,
    [property: JsonPropertyName("request_seq")] int RequestSeq,
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("message")] string? Message = null,
    [property: JsonPropertyName("body")] JsonElement? Body = null)
{
    [JsonPropertyName("type")]
    public string Type => "response";
}

public sealed record DapEvent(
    [property: JsonPropertyName("seq")] int Seq,
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("body")] JsonElement? Body = null)
{
    [JsonPropertyName("type")]
    public string Type => "event";
}

public abstract record DapIncomingMessage;

public sealed record DapIncomingResponse(DapResponse Response) : DapIncomingMessage;

public sealed record DapIncomingEvent(DapEvent Event) : DapIncomingMessage;

public sealed record DapClientCapabilities(
    string ClientId = "hermes-workbench",
    string ClientName = "Hermes Workbench",
    string AdapterId = "coreclr",
    bool LinesStartAt1 = true,
    bool ColumnsStartAt1 = true,
    string PathFormat = "path",
    bool SupportsVariableType = true,
    bool SupportsVariablePaging = true,
    bool SupportsRunInTerminalRequest = false);

public sealed record DapAdapterCapabilities(
    bool SupportsConfigurationDoneRequest = false,
    bool SupportsFunctionBreakpoints = false,
    bool SupportsConditionalBreakpoints = false,
    bool SupportsHitConditionalBreakpoints = false,
    bool SupportsEvaluateForHovers = false,
    bool SupportsStepBack = false,
    bool SupportsSetVariable = false,
    bool SupportsRestartRequest = false,
    bool SupportsTerminateRequest = false,
    bool SupportsCancelRequest = false,
    bool SupportsBreakpointLocationsRequest = false);

public sealed record DapSource(string? Name, string? Path, int SourceReference = 0);

public sealed record DapSourceBreakpoint(
    int Line,
    int? Column = null,
    string? Condition = null,
    string? HitCondition = null,
    string? LogMessage = null);

public sealed record DapBreakpoint(
    int? Id,
    bool Verified,
    string? Message,
    DapSource? Source,
    int? Line,
    int? Column);

public sealed record DapThread(int Id, string Name);

public sealed record DapStackFrame(
    int Id,
    string Name,
    DapSource? Source,
    int Line,
    int Column,
    int? EndLine = null,
    int? EndColumn = null);

public sealed record DapScope(
    string Name,
    int VariablesReference,
    bool Expensive,
    string? PresentationHint = null);

public sealed record DapVariable(
    string Name,
    string Value,
    string? Type,
    int VariablesReference,
    string? EvaluateName = null,
    string? MemoryReference = null);

public sealed record DapEvaluateResult(
    string Result,
    string? Type,
    int VariablesReference,
    string? MemoryReference = null);

public sealed record DapStoppedEvent(
    string Reason,
    int? ThreadId,
    string? Description,
    bool AllThreadsStopped);

public sealed record DapContinuedEvent(int? ThreadId, bool AllThreadsContinued);

public sealed class DapProtocolException : Exception
{
    public DapProtocolException(string message) : base(message)
    {
    }

    public DapProtocolException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

public sealed class DapSessionException : Exception
{
    public DapSessionException(string message) : base(message)
    {
    }
}
