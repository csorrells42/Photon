using System.Text.Json;
using HermesDesktop;
using HermesDeveloperServices;
using HermesDotNetDebugger;
using HermesRoslynLanguageServer;

internal static class DeveloperDebugBridgeModuleSmoke
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"HermesDeveloperDebugBridgeSmoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        Directory.CreateDirectory(Path.Combine(root, "bin", "Debug", "net10.0"));
        var source = Path.Combine(root, "src", "Program.cs");
        var program = Path.Combine(root, "bin", "Debug", "net10.0", "Smoke.dll");
        await File.WriteAllTextAsync(source, "class Program { static void Main() { } }");
        await File.WriteAllBytesAsync(program, [0]);
        await File.WriteAllTextAsync(Path.ChangeExtension(program, ".runtimeconfig.json"), "{}");
        var frames = new List<JsonElement>();
        var debug = new SmokeDebugHost(root);
        try
        {
            await using var bridge = new DeveloperServicesBridge(
                root,
                root,
                message => frames.Add(JsonSerializer.SerializeToElement(message)),
                new ModuleSmokeRoslynHost(),
                debug);

            await bridge.DescribeDebugTargetsAsync(2, "debug-targets-smoke", "debug");
            var targets = Result(frames, "debug-targets-smoke").GetProperty("result");
            Require(targets.EnumerateArray().Any(item => item.GetString() == "bin/Debug/net10.0/Smoke.dll"), "Debug target discovery did not return the bounded runnable artifact.");

            await bridge.LaunchDebugAsync(2, "debug-launch-smoke", "bin/Debug/net10.0/Smoke.dll", "", ["one"], true);
            Require(debug.LaunchRequest is { StopAtEntry: true } && Path.IsPathFullyQualified(debug.LaunchRequest.ProgramPath), "Debug launch did not resolve one explicit workspace program.");

            await bridge.SetDebugBreakpointsAsync(2, "debug-breakpoints-smoke", "src/Program.cs", [new DapSourceBreakpoint(1)]);
            await bridge.ConfigurationDoneDebugAsync(2, "debug-config-smoke");
            debug.PublishStopped(7);
            Require(frames.Any(frame => String(frame, "type") == "developerServices.debug.event" && String(frame, "event") == "stopped"), "Stopped event was not projected.");

            await bridge.GetDebugThreadsAsync(2, "debug-threads-smoke");
            await bridge.GetDebugStackTraceAsync(2, "debug-stack-smoke", 7, 0, 100);
            await bridge.GetDebugScopesAsync(2, "debug-scopes-smoke", 0);
            await bridge.GetDebugVariablesAsync(2, "debug-variables-smoke", 9, 0, 100);
            await bridge.EvaluateDebugAsync(2, "debug-evaluate-smoke", "answer", 0, "repl");
            await bridge.ContinueDebugAsync(2, "debug-continue-smoke", 7);
            await bridge.StepOverDebugAsync(2, "debug-over-smoke", 7);
            await bridge.StepIntoDebugAsync(2, "debug-into-smoke", 7);
            await bridge.StepOutDebugAsync(2, "debug-out-smoke", 7);

            var stack = Result(frames, "debug-stack-smoke").GetProperty("result")[0];
            var variable = Result(frames, "debug-variables-smoke").GetProperty("result")[0];
            Require(String(stack, "path") == "src/Program.cs" && stack.GetProperty("id").GetInt32() == 0, "Debug stack projection lost frame zero or leaked an absolute path.");
            Require(String(variable, "name") == "answer" && String(variable, "value") == "42", "Debug variable inspection was not projected.");
            Require(String(Result(frames, "debug-evaluate-smoke").GetProperty("result"), "result") == "42", "Debug evaluation was not projected.");

            debug.BlockThreads = true;
            var cancelled = bridge.GetDebugThreadsAsync(2, "debug-cancel-target");
            bridge.CancelDebugOperation(2, "debug-cancel-request", "debug-cancel-target");
            await cancelled;
            Require(frames.Any(frame => String(frame, "requestId") == "debug-cancel-target" && String(frame, "code") == "debug_cancelled"), "Debug cancellation did not stop the exact active request.");

            await bridge.DisconnectDebugAsync(2, "debug-disconnect-smoke");
            Require(debug.Disconnected, "Debug disconnect was not forwarded.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }

        var authorization = new DeveloperDebugAuthorizationPolicy();
        var authorizationRequest = new DotNetDebugLaunchRequest(program, root, ["one"], true);
        authorization.AuthorizeNextLaunch(authorizationRequest);
        Require(await authorization.AuthorizeLaunchAsync(root, authorizationRequest, CancellationToken.None), "Explicit launch authorization was not consumed.");
        Require(!await authorization.AuthorizeLaunchAsync(root, authorizationRequest, CancellationToken.None), "Explicit launch authorization was replayable.");
        Console.WriteLine("Desktop typed .NET debugger bridge workflow, cancellation, projection, and one-use authorization passed.");
    }

    private static JsonElement Result(IEnumerable<JsonElement> frames, string requestId) => frames.Last(frame =>
        String(frame, "type") == "developerServices.debug.result" && String(frame, "requestId") == requestId);

    private static string? String(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed class SmokeDebugHost(string root) : IDeveloperDebugHost
{
    public event Action<DapEvent>? EventReceived;
    public DapSessionState? State { get; private set; }
    public DapAdapterCapabilities? Capabilities { get; } = new(true, SupportsConditionalBreakpoints: true, SupportsEvaluateForHovers: true);
    internal DotNetDebugLaunchRequest? LaunchRequest { get; private set; }
    internal bool BlockThreads { get; set; }
    internal bool Disconnected { get; private set; }

    public Task LaunchAsync(DotNetDebugLaunchRequest request, CancellationToken cancellationToken) { LaunchRequest = request; State = DapSessionState.Configuring; return Task.CompletedTask; }
    public Task AttachAsync(DotNetDebugAttachRequest request, CancellationToken cancellationToken) { State = DapSessionState.Configuring; return Task.CompletedTask; }
    public Task<IReadOnlyList<DapBreakpoint>> SetBreakpointsAsync(string sourcePath, IReadOnlyList<DapSourceBreakpoint> breakpoints, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DapBreakpoint>>([new(1, true, null, new DapSource("Program.cs", sourcePath), 1, 1)]);
    public Task ConfigurationDoneAsync(CancellationToken cancellationToken) { State = DapSessionState.Running; return Task.CompletedTask; }
    public async Task<IReadOnlyList<DapThread>> GetThreadsAsync(CancellationToken cancellationToken) { if (BlockThreads) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return [new DapThread(7, "main")]; }
    public Task<IReadOnlyList<DapStackFrame>> GetStackTraceAsync(int threadId, int? startFrame, int? levels, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DapStackFrame>>([new(0, "Program.Main", new DapSource("Program.cs", Path.Combine(root, "src", "Program.cs")), 1, 1)]);
    public Task<IReadOnlyList<DapScope>> GetScopesAsync(int frameId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DapScope>>([new("Locals", 9, false)]);
    public Task<IReadOnlyList<DapVariable>> GetVariablesAsync(int variablesReference, int? start, int? count, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DapVariable>>([new("answer", "42", "int", 0, "answer")]);
    public Task<DapEvaluateResult> EvaluateAsync(string expression, int? frameId, string context, CancellationToken cancellationToken) => Task.FromResult(new DapEvaluateResult("42", "int", 0));
    public Task ContinueAsync(int threadId, CancellationToken cancellationToken) { State = DapSessionState.Running; return Task.CompletedTask; }
    public Task StepOverAsync(int threadId, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StepIntoAsync(int threadId, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StepOutAsync(int threadId, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task DisconnectAsync(CancellationToken cancellationToken) { Disconnected = true; State = DapSessionState.Disconnected; return Task.CompletedTask; }
    internal void PublishStopped(int threadId) { State = DapSessionState.Stopped; EventReceived?.Invoke(new DapEvent(1, "stopped", JsonSerializer.SerializeToElement(new { reason = "breakpoint", threadId, allThreadsStopped = true }))); }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class ModuleSmokeRoslynHost : IDeveloperRoslynHost
{
    public event Action<LspPublishDiagnosticsParams>? DiagnosticsPublished { add { } remove { } }
    public Task OpenDocumentAsync(string workspaceRoot, string uri, int revision, string text, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ChangeDocumentAsync(string uri, int revision, string text, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task CloseDocumentAsync(string uri, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<RoslynLanguageResult?> CompletionAsync(string uri, int revision, LspPosition position, CancellationToken cancellationToken) => Task.FromResult<RoslynLanguageResult?>(null);
    public Task<RoslynLanguageResult?> HoverAsync(string uri, int revision, LspPosition position, CancellationToken cancellationToken) => Task.FromResult<RoslynLanguageResult?>(null);
    public Task<RoslynLanguageResult?> DefinitionAsync(string uri, int revision, LspPosition position, CancellationToken cancellationToken) => Task.FromResult<RoslynLanguageResult?>(null);
    public Task<RoslynLanguageResult?> ReferencesAsync(string uri, int revision, LspPosition position, bool includeDeclaration, CancellationToken cancellationToken) => Task.FromResult<RoslynLanguageResult?>(null);
    public Task<RoslynLanguageResult?> RenameAsync(string uri, int revision, LspPosition position, string newName, CancellationToken cancellationToken) => Task.FromResult<RoslynLanguageResult?>(null);
    public Task<RoslynLanguageResult?> CodeActionsAsync(string uri, int revision, LspRange range, CancellationToken cancellationToken) => Task.FromResult<RoslynLanguageResult?>(null);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
