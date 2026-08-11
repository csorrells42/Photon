using System.Collections.Concurrent;
using System.Text.Json;
using HermesDesktop;
using HermesDeveloperServices;

internal static class RealDeveloperIntegrationSmoke
{
    internal static async Task RunAsync()
    {
        var ownedRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("SPAT_OWNED_ROOT")
            ?? throw new InvalidOperationException("SPAT_OWNED_ROOT is required."));
        var installRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("SPAT_COMPLETE_INSTALL_ROOT")
            ?? throw new InvalidOperationException("SPAT_COMPLETE_INSTALL_ROOT is required."));
        var workspace = Path.Combine(ownedRoot, "spat-live-validation", "workspace");
        var sourcePath = Path.Combine(workspace, "Program.cs");
        var source = await File.ReadAllTextAsync(sourcePath);
        var frames = new ConcurrentQueue<JsonElement>();

        await using var bridge = new DeveloperServicesBridge(
            workspace,
            installRoot,
            value => frames.Enqueue(JsonSerializer.SerializeToElement(value)));

        await bridge.DescribeAsync(2, "real-describe");
        var description = RequireResult(frames, "real-describe", "developerServices.describe.result");
        var dotnetTooling = description.GetProperty("languageTooling").EnumerateArray().Single(item =>
            Text(item, "providerId") == "dotnet");
        var dotnetCapabilities = dotnetTooling.GetProperty("capabilities").EnumerateArray().ToArray();
        if (dotnetCapabilities.Length != 4
            || dotnetCapabilities.Any(item => Text(item, "availability") != "available"))
        {
            throw new InvalidOperationException("The integrated desktop host did not report all four .NET language-tooling capabilities as available.");
        }

        await bridge.RunLanguageToolingTestsAsync(
            1, "real-dotnet-tests", "dotnet", "Dragon.csproj", null);
        var testFrame = RequireResult(frames, "real-dotnet-tests", "developerServices.languageTooling.result");
        if (!testFrame.GetProperty("succeeded").GetBoolean()
            || !testFrame.GetProperty("result").GetProperty("succeeded").GetBoolean())
        {
            throw new InvalidOperationException("The real typed .NET test operation did not succeed.");
        }
        Console.WriteLine("PASS integrated .NET language-tooling reports 4/4 and executes typed tests");

        await RunPythonAsync(installRoot);
        await RunNativeToolchainsAsync(installRoot);

        await bridge.OpenLanguageDocumentAsync(2, "real-language-open", 1, "Program.cs", source);
        var opened = RequireResult(frames, "real-language-open", "developerServices.language.open.result");
        var sessionId = Text(opened, "sessionId") ?? throw new InvalidOperationException("Real Roslyn session ID was missing.");
        var symbol = PositionOf(source, "Double(21)");

        await RequireLanguageOperationAsync(bridge, frames, sessionId, 1, "hover", symbol, "real-hover");
        await RequireLanguageOperationAsync(bridge, frames, sessionId, 1, "definition", symbol, "real-definition");
        await RequireLanguageOperationAsync(bridge, frames, sessionId, 1, "references", symbol, "real-references");
        await RequireLanguageOperationAsync(bridge, frames, sessionId, 1, "rename", symbol, "real-rename", "Twice");
        await RequireLanguageOperationAsync(bridge, frames, sessionId, 1, "code-actions", symbol, "real-code-actions");

        var completionSource = source.Replace("Double(21)", "Dou", StringComparison.Ordinal);
        await bridge.ChangeLanguageDocumentAsync(2, "real-language-change", sessionId, 2, "Program.cs", completionSource);
        RequireResult(frames, "real-language-change", "developerServices.language.change.result");
        var completion = PositionAfter(completionSource, "DragonMath.Dou");
        var completionPassed = false;
        for (var attempt = 1; attempt <= 3 && !completionPassed; attempt++)
        {
            var requestId = $"real-completion-{attempt}";
            await bridge.RunLanguageOperationAsync(2, requestId, sessionId, 2, "Program.cs", "completion",
                completion.Line, completion.Character, completion.Line, completion.Character, null, true);
            completionPassed = TryRequireNonNullResult(frames, requestId, "completion");
        }
        if (!completionPassed) throw new InvalidOperationException("Real Roslyn completion did not succeed through the desktop bridge.");
        await bridge.CloseLanguageDocumentAsync(2, "real-language-close", sessionId, "Program.cs");
        RequireResult(frames, "real-language-close", "developerServices.language.close.result");
        Console.WriteLine("PASS real desktop Roslyn bridge open/change/completion/hover/definition/references/rename/code-actions/close");

        await bridge.DescribeDebugTargetsAsync(2, "real-debug-targets", "debug");
        var targets = RequireResult(frames, "real-debug-targets", "developerServices.debug.result").GetProperty("result");
        var program = targets.EnumerateArray().Select(item => item.GetString()).FirstOrDefault(item =>
            item is not null && item.EndsWith("Dragon.exe", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The real desktop bridge did not discover Dragon.exe.");

        await bridge.LaunchDebugAsync(2, "real-debug-launch", program, "", [], true);
        RequireResult(frames, "real-debug-launch", "developerServices.debug.result");
        await bridge.SetDebugBreakpointsAsync(2, "real-debug-breakpoints", "Program.cs", [new DapSourceBreakpoint(7)]);
        RequireResult(frames, "real-debug-breakpoints", "developerServices.debug.result");
        await bridge.ConfigurationDoneDebugAsync(2, "real-debug-config");
        RequireResult(frames, "real-debug-config", "developerServices.debug.result");
        await WaitForStoppedCountAsync(frames, 1, "initial debug stop");

        await bridge.GetDebugThreadsAsync(2, "real-debug-threads");
        var threads = RequireResult(frames, "real-debug-threads", "developerServices.debug.result").GetProperty("result");
        var threadId = threads[0].GetProperty("id").GetInt32();
        await bridge.GetDebugStackTraceAsync(2, "real-debug-stack", threadId, 0, 100);
        var stack = RequireResult(frames, "real-debug-stack", "developerServices.debug.result").GetProperty("result");
        var frameId = stack[0].GetProperty("id").GetInt32();
        await bridge.GetDebugScopesAsync(2, "real-debug-scopes", frameId);
        var scopes = RequireResult(frames, "real-debug-scopes", "developerServices.debug.result").GetProperty("result");
        var variablesReference = scopes.EnumerateArray().Select(item => item.GetProperty("variablesReference").GetInt32()).FirstOrDefault(value => value > 0);
        if (variablesReference > 0)
        {
            await bridge.GetDebugVariablesAsync(2, "real-debug-variables", variablesReference, 0, 100);
            RequireResult(frames, "real-debug-variables", "developerServices.debug.result");
        }
        await bridge.EvaluateDebugAsync(2, "real-debug-evaluate", "1 + 1", frameId, "watch");
        var evaluation = RequireResult(frames, "real-debug-evaluate", "developerServices.debug.result").GetProperty("result");
        if (Text(evaluation, "result") != "2") throw new InvalidOperationException("Real desktop debug evaluation was not projected.");

        await bridge.ContinueDebugAsync(2, "real-debug-continue", threadId);
        RequireResult(frames, "real-debug-continue", "developerServices.debug.result");
        await WaitForStoppedCountAsync(frames, 2, "breakpoint after continue");
        await bridge.StepIntoDebugAsync(2, "real-debug-into", threadId);
        RequireResult(frames, "real-debug-into", "developerServices.debug.result");
        await WaitForStoppedCountAsync(frames, 3, "stop after step-in");
        await bridge.StepOutDebugAsync(2, "real-debug-out", threadId);
        RequireResult(frames, "real-debug-out", "developerServices.debug.result");
        await WaitForStoppedCountAsync(frames, 4, "stop after step-out");
        await bridge.StepOverDebugAsync(2, "real-debug-over", threadId);
        RequireResult(frames, "real-debug-over", "developerServices.debug.result");
        await WaitForStoppedCountAsync(frames, 5, "stop after step-over");
        await bridge.DisconnectDebugAsync(2, "real-debug-disconnect");
        RequireResult(frames, "real-debug-disconnect", "developerServices.debug.result");

        await bridge.LaunchDebugAsync(2, "real-debug-relaunch", program, "", [], true);
        RequireResult(frames, "real-debug-relaunch", "developerServices.debug.result");
        await bridge.ConfigurationDoneDebugAsync(2, "real-debug-relaunch-config");
        RequireResult(frames, "real-debug-relaunch-config", "developerServices.debug.result");
        await WaitForStoppedCountAsync(frames, 6, "explicit relaunch stop");
        await bridge.DisconnectDebugAsync(2, "real-debug-relaunch-disconnect");
        RequireResult(frames, "real-debug-relaunch-disconnect", "developerServices.debug.result");
        Console.WriteLine("PASS real desktop .NET debugger bridge discovery/launch/breakpoint/inspect/evaluate/step/disconnect/relaunch");
    }

    private static async Task RunNativeToolchainsAsync(string installRoot)
    {
        var workspace = Path.GetFullPath(Environment.GetEnvironmentVariable("SPAT_PYTHON_WORKSPACE_ROOT")
            ?? throw new InvalidOperationException("SPAT_PYTHON_WORKSPACE_ROOT is required."));
        var frames = new ConcurrentQueue<JsonElement>();
        await using var bridge = new DeveloperServicesBridge(
            workspace,
            installRoot,
            value => frames.Enqueue(JsonSerializer.SerializeToElement(value)));
        var nativeRoot = Path.Combine(workspace, "native-toolchain-proof");
        var sketchRoot = Path.Combine(nativeRoot, "Blink");
        Directory.CreateDirectory(sketchRoot);
        await File.WriteAllTextAsync(Path.Combine(nativeRoot, "hello.c"),
            "#include <stdio.h>\nint main(void) { puts(\"C17 OK\"); return 0; }\n");
        await File.WriteAllTextAsync(Path.Combine(nativeRoot, "hello.cpp"),
            "#include <iostream>\nint main() { std::cout << \"C++20 OK\\n\"; return 0; }\n");
        await File.WriteAllTextAsync(Path.Combine(sketchRoot, "Blink.ino"),
            "void setup() { pinMode(LED_BUILTIN, OUTPUT); }\nvoid loop() { digitalWrite(LED_BUILTIN, HIGH); delay(100); digitalWrite(LED_BUILTIN, LOW); delay(100); }\n");

        await bridge.DescribeAsync(2, "real-native-describe");
        var description = RequireResult(frames, "real-native-describe", "developerServices.describe.result");
        RequireAvailableCapabilities(description, "gcc", "gcc.compiler");
        RequireAvailableCapabilities(description, "arduino", "arduino.project", "arduino.compiler");

        await bridge.CompileLanguageToolingAsync(1, "real-gcc-c17", "gcc", "native-toolchain-proof/hello.c", "check", null);
        RequireArtifacts(RequireSucceededResult(frames, "real-gcc-c17"), "C17");
        await bridge.CompileLanguageToolingAsync(1, "real-gcc-cpp20", "gcc", "native-toolchain-proof/hello.cpp", "check", null);
        RequireArtifacts(RequireSucceededResult(frames, "real-gcc-cpp20"), "C++20");

        await bridge.InspectLanguageToolingProjectAsync(1, "real-arduino-inspect", "arduino", "native-toolchain-proof/Blink");
        RequireSucceededResult(frames, "real-arduino-inspect");
        await bridge.CompileLanguageToolingAsync(
            1, "real-arduino-compile", "arduino", "native-toolchain-proof/Blink/Blink.ino", "check", "arduino:avr:uno");
        RequireArtifacts(RequireSucceededResult(frames, "real-arduino-compile"), "Arduino Uno");
        Console.WriteLine("PASS real desktop C17/C++20 GCC and Arduino Uno inspect/compile/artifact workflows");
    }

    private static void RequireAvailableCapabilities(JsonElement description, string providerId, params string[] capabilityIds)
    {
        var provider = description.GetProperty("languageTooling").EnumerateArray().Single(item =>
            Text(item, "providerId") == providerId);
        var capabilities = provider.GetProperty("capabilities").EnumerateArray().ToArray();
        foreach (var capabilityId in capabilityIds)
        {
            if (!capabilities.Any(item => Text(item, "capabilityId") == capabilityId
                    && Text(item, "availability") == "available"))
                throw new InvalidOperationException($"The integrated desktop host did not report {capabilityId} as available.");
        }
    }

    private static void RequireArtifacts(JsonElement frame, string label)
    {
        var result = frame.GetProperty("result");
        if (!result.TryGetProperty("artifacts", out var artifacts)
            || artifacts.ValueKind != JsonValueKind.Array
            || artifacts.GetArrayLength() == 0)
            throw new InvalidOperationException($"The real {label} operation returned no bounded artifact.");
    }

    private static async Task RunPythonAsync(string installRoot)
    {
        var workspace = Path.GetFullPath(Environment.GetEnvironmentVariable("SPAT_PYTHON_WORKSPACE_ROOT")
            ?? throw new InvalidOperationException("SPAT_PYTHON_WORKSPACE_ROOT is required."));
        var frames = new ConcurrentQueue<JsonElement>();
        await using var bridge = new DeveloperServicesBridge(
            workspace,
            installRoot,
            value => frames.Enqueue(JsonSerializer.SerializeToElement(value)));

        await bridge.DescribeAsync(2, "real-python-describe");
        var description = RequireResult(frames, "real-python-describe", "developerServices.describe.result");
        var python = description.GetProperty("languageTooling").EnumerateArray().Single(item =>
            Text(item, "providerId") == "python");
        var capabilities = python.GetProperty("capabilities").EnumerateArray().ToArray();
        if (capabilities.Length != 4 || capabilities.Any(item => Text(item, "availability") != "available"))
        {
            var summary = string.Join(", ", capabilities.Select(item =>
                $"{Text(item, "capabilityId") ?? "unknown"}={Text(item, "availability") ?? "unknown"}/{Text(item, "code") ?? "no-code"}"));
            throw new InvalidOperationException(
                $"The integrated desktop host did not report all four Python capabilities as available: {summary}");
        }

        await bridge.InspectLanguageToolingProjectAsync(
            1, "real-python-inspect", "python", "photon-capability-stage1");
        RequireSucceededResult(frames, "real-python-inspect");
        await bridge.CompileLanguageToolingAsync(
            1, "real-python-syntax", "python", "photon-capability-stage1", "check", null);
        RequireSucceededResult(frames, "real-python-syntax");
        await bridge.RunLanguageToolingTestsAsync(
            1, "real-python-tests", "python", "photon-capability-stage1/tests/test_envelope.py", "round_trip");
        RequireSucceededResult(frames, "real-python-tests");
        await bridge.StartLanguageToolingSessionAsync(
            1, "real-python-language-start", "python", "photon-capability-stage1/assistant_bus_envelope.py");
        var started = RequireSucceededResult(frames, "real-python-language-start");
        var sessionId = Text(started.GetProperty("result"), "sessionId")
            ?? throw new InvalidOperationException("The real Python language session did not return an owned session ID.");
        await bridge.StopLanguageToolingSessionAsync(
            1, "real-python-language-stop", "python", sessionId);
        RequireSucceededResult(frames, "real-python-language-stop");
        Console.WriteLine("PASS integrated Python language-tooling reports 4/4 and executes project/syntax/unittest/Serena session");
    }

    private static JsonElement RequireSucceededResult(ConcurrentQueue<JsonElement> frames, string requestId)
    {
        var frame = RequireResult(frames, requestId, "developerServices.languageTooling.result");
        if (!frame.GetProperty("succeeded").GetBoolean()
            || !frame.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Object
            || !result.GetProperty("succeeded").GetBoolean())
            throw new InvalidOperationException($"The real typed language-tooling operation failed: {requestId} ({Text(frame, "code")}).");
        return frame;
    }

    private static async Task RequireLanguageOperationAsync(
        DeveloperServicesBridge bridge,
        ConcurrentQueue<JsonElement> frames,
        string sessionId,
        int revision,
        string operation,
        LspPosition position,
        string requestId,
        string? newName = null)
    {
        await bridge.RunLanguageOperationAsync(2, requestId, sessionId, revision, "Program.cs", operation,
            position.Line, position.Character, position.Line, position.Character, newName, true);
        if (!TryRequireNonNullResult(frames, requestId, operation))
            throw new InvalidOperationException($"Real Roslyn {operation} did not succeed through the desktop bridge.");
    }

    private static bool TryRequireNonNullResult(ConcurrentQueue<JsonElement> frames, string requestId, string operation)
    {
        var frame = frames.LastOrDefault(item => Text(item, "requestId") == requestId);
        return frame.ValueKind == JsonValueKind.Object
            && Text(frame, "type") == "developerServices.language.result"
            && Text(frame, "operation") == operation
            && frame.TryGetProperty("result", out var result)
            && result.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;
    }

    private static JsonElement RequireResult(ConcurrentQueue<JsonElement> frames, string requestId, string expectedType)
    {
        var frame = frames.LastOrDefault(item => Text(item, "requestId") == requestId);
        if (frame.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"Desktop bridge emitted no frame for {requestId}.");
        if (Text(frame, "type") == "developerServices.error")
            throw new InvalidOperationException($"Desktop bridge failed {requestId}: {Text(frame, "code")} {Text(frame, "message")}");
        if (Text(frame, "type") != expectedType)
            throw new InvalidOperationException($"Desktop bridge emitted the wrong frame type for {requestId}.");
        return frame;
    }

    private static async Task WaitForStoppedCountAsync(ConcurrentQueue<JsonElement> frames, int expected, string label)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (frames.Count(item => Text(item, "type") == "developerServices.debug.event" && Text(item, "event") == "stopped") < expected)
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException($"Timed out waiting for {label}.");
            await Task.Delay(20);
        }
    }

    private static LspPosition PositionOf(string text, string token)
    {
        var index = text.IndexOf(token, StringComparison.Ordinal);
        if (index < 0) throw new InvalidOperationException($"Fixture token {token} was not found.");
        var prefix = text[..index];
        var line = prefix.Count(character => character == '\n');
        var lastBreak = prefix.LastIndexOf('\n');
        return new LspPosition(line, index - lastBreak - 1);
    }

    private static LspPosition PositionAfter(string text, string token)
    {
        var start = PositionOf(text, token);
        return new LspPosition(start.Line, start.Character + token.Length);
    }

    private static string? Text(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
}
