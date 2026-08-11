using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HermesDeveloperServices;

if (args is ["--fake-lsp-server"])
{
    return await FakeLspStdioServer.RunAsync();
}

var tests = new (string Name, Func<Task> Run)[]
{
    ("provider registry selects two uncoupled synthetic adapters", SmokeTests.ProviderRegistryAsync),
    ("build-target discovery is bounded and skips generated trees", SmokeTests.BuildTargetDiscoveryAsync),
    ("MSBuild parser preserves Windows paths and source ranges", SmokeTests.MsBuildParserAsync),
    ("build runner accepts safe targets, rejects escapes, and bounds output", SmokeTests.BuildRunnerAsync),
    ("build runner cancellation stops its disposable fixture", SmokeTests.BuildCancellationAsync),
    ("DAP decoder handles fragmented and multiple frames", SmokeTests.DapFramingAsync),
    ("DAP decoder rejects malformed frames", SmokeTests.DapMalformedFrameAsync),
    ("DAP fake adapter covers launch, inspection, stepping, and cancellation", SmokeTests.DapLaunchLifecycleAsync),
    ("DAP fake adapter covers attach negotiation and adapter exit", SmokeTests.DapAttachAndExitAsync),
    ("LSP decoder handles fragmented frames and rejects malformed payloads", SmokeTests.LspFramingAsync),
    ("LSP fake adapter covers documents, diagnostics, language requests, and shutdown", SmokeTests.LspLifecycleAsync),
    ("LSP cancellation is correlated and unsupported server requests are rejected", SmokeTests.LspCancellationAndServerRequestAsync),
    ("LSP stdio transport owns one bounded explicit child process", SmokeTests.LspProcessTransportAsync),
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}: {exception.GetType().Name}: {exception.Message}");
    }
}

Console.WriteLine($"Smoke total: {tests.Length}; passed: {tests.Length - failures}; failed: {failures}");
return failures == 0 ? 0 : 1;

internal static class SmokeTests
{
    public static Task BuildTargetDiscoveryAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"HermesTargetDiscoverySmoke-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src", "App"));
            Directory.CreateDirectory(Path.Combine(root, "src", "Library"));
            Directory.CreateDirectory(Path.Combine(root, "src", "App", "obj"));
            File.WriteAllText(Path.Combine(root, "Hermes.slnx"), "<Solution />");
            File.WriteAllText(Path.Combine(root, "src", "App", "App.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(root, "src", "Library", "Library.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(root, "src", "App", "obj", "Generated.csproj"), "<Project />");

            var targets = BuildTargetDiscovery.Discover(root, maximumTargets: 2);
            Require(targets.Count == 2, "The target bound was not enforced.");
            Require(targets[0] == "Hermes.slnx", "The solution target was not prioritized.");
            Require(targets.Contains("src/App/App.csproj"), "The application project was not discovered.");
            Require(targets.All(path => !path.Contains("obj", StringComparison.OrdinalIgnoreCase)), "A generated target escaped discovery filtering.");
            return Task.CompletedTask;
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    public static async Task ProviderRegistryAsync()
    {
        await using var registry = new ToolchainProviderRegistry();
        registry.Register(new SyntheticToolchainProvider("alpha", "language-a", "alpha-project", build: true));
        registry.Register(new SyntheticToolchainProvider("beta", "language-b", "beta-project", build: false));

        var alpha = registry.Select(new ToolchainSelectionRequest(
            "language-a",
            "alpha-project",
            RequiresBuild: true));
        Require(alpha.State == ToolchainSelectionState.Selected, "The alpha provider was not selected.");
        Require(alpha.Provider?.Descriptor.ProviderId == "alpha", "The alpha provider selection was coupled incorrectly.");

        var beta = registry.Select(new ToolchainSelectionRequest("language-b", "beta-project"));
        Require(beta.State == ToolchainSelectionState.Selected, "The beta provider was not selected.");
        Require(beta.Provider?.Descriptor.ProviderId == "beta", "The beta provider selection was coupled incorrectly.");

        var unsupported = registry.Select(new ToolchainSelectionRequest("language-b", RequiresBuild: true));
        Require(unsupported.State == ToolchainSelectionState.NotFound, "Capability filtering did not exclude beta.");
        Require(registry.GetDescriptors().All(
            descriptor => descriptor.DeploymentScope == ToolchainDeploymentScope.HermesWorkbenchInternalModule),
            "A provider escaped the single-product deployment scope.");
    }

    public static Task MsBuildParserAsync()
    {
        const string ranged = @"C:\Ångström Workspace\Demo File.cs(12,4,12,9): warning CS8602: Dereference of a possibly null reference. [C:\Ångström Workspace\Demo.csproj]";
        Require(MsBuildDiagnosticParser.TryParse(ranged, out var diagnostic), "The ranged diagnostic did not parse.");
        Require(diagnostic is not null, "The diagnostic was null.");
        Require(diagnostic!.FilePath == @"C:\Ångström Workspace\Demo File.cs", "The Unicode Windows path changed.");
        Require(diagnostic.Range.Start == new SourcePosition(12, 4), "The start range changed.");
        Require(diagnostic.Range.End == new SourcePosition(12, 9), "The end range changed.");
        Require(diagnostic.Project == @"C:\Ångström Workspace\Demo.csproj", "The project path changed.");
        Require(diagnostic.Severity == DiagnosticSeverity.Warning, "The severity changed.");

        const string point = @"C:\work\Program.cs(2,3): error CS1002: ; expected [C:\work\App.csproj]";
        Require(MsBuildDiagnosticParser.TryParse(point, out var pointDiagnostic), "The point diagnostic did not parse.");
        Require(pointDiagnostic!.Range.Start == pointDiagnostic.Range.End, "A point diagnostic gained a range.");
        Require(!MsBuildDiagnosticParser.TryParse("  Determining projects to restore...", out _), "Progress text parsed as a diagnostic.");
        return Task.CompletedTask;
    }

    public static async Task BuildRunnerAsync()
    {
        using var fixture = BuildFixture.Create();
        var runner = new DotnetBuildRunner(new DotnetBuildRunnerOptions
        {
            MaximumRetainedCharacters = 1_024,
            MaximumDiagnostics = 100,
        });

        var result = await runner.BuildAsync(new BuildRequest(
            fixture.WorkspaceRoot,
            Path.GetFileName(fixture.OutputProject),
            BuildConfiguration.Release));
        Require(result.Succeeded, $"The safe fixture failed: {result.FailureCode}");
        Require(result.Output.Truncated, "The fixture output was not bounded.");
        Require(result.Output.DroppedCharacters > 0, "The dropped output count was not recorded.");
        Require(
            result.Output.StandardOutput.Length + result.Output.StandardError.Length <= 1_024,
            "Retained output exceeded the configured bound.");

        var broken = await runner.BuildAsync(new BuildRequest(
            fixture.WorkspaceRoot,
            Path.GetRelativePath(fixture.WorkspaceRoot, fixture.BrokenProject),
            BuildConfiguration.Debug));
        Require(!broken.Succeeded, "The invalid project unexpectedly built successfully.");
        Require(broken.Diagnostics.Count == 1, "Repeated MSBuild summary diagnostics were not deduplicated.");
        Require(broken.Diagnostics[0].Code == "CS1525", "The compiler diagnostic changed unexpectedly.");

        var escaped = await runner.BuildAsync(new BuildRequest(
            fixture.WorkspaceRoot,
            fixture.OutsideProject,
            BuildConfiguration.Release));
        Require(!escaped.Succeeded, "An outside build target was accepted.");
        Require(escaped.FailureCode == "target_outside_workspace", "The outside target failure was not classified.");

        var unsupportedPath = Path.Combine(fixture.WorkspaceRoot, "notes.txt");
        await File.WriteAllTextAsync(unsupportedPath, "not a build target");
        var unsupported = await runner.BuildAsync(new BuildRequest(fixture.WorkspaceRoot, unsupportedPath));
        Require(unsupported.FailureCode == "unsupported_target", "An unsupported target was not rejected.");
    }

    public static async Task BuildCancellationAsync()
    {
        using var fixture = BuildFixture.Create();
        var runner = new DotnetBuildRunner(new DotnetBuildRunnerOptions
        {
            MaximumRetainedCharacters = 4_096,
            MaximumDiagnostics = 100,
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
        var stopwatch = Stopwatch.StartNew();
        var result = await runner.BuildAsync(
            new BuildRequest(fixture.WorkspaceRoot, Path.GetFileName(fixture.SlowProject)),
            cancellation.Token);
        stopwatch.Stop();
        Require(result.WasCancelled, "The cancelled build was not classified as cancelled.");
        Require(!result.Succeeded, "The cancelled build reported success.");
        Require(stopwatch.Elapsed < TimeSpan.FromSeconds(15), "Owned-process cancellation did not complete promptly.");
    }

    public static Task DapFramingAsync()
    {
        var response = new DapResponse(1, 7, true, "initialize", Body: JsonSerializer.SerializeToElement(new { supportsConfigurationDoneRequest = true }));
        var dapEvent = new DapEvent(2, "initialized");
        var bytes = DapMessageCodec.EncodeFrame(response).Concat(DapMessageCodec.EncodeFrame(dapEvent)).ToArray();
        var decoder = new DapFrameDecoder();
        var frames = new List<byte[]>();
        var chunkSizes = new[] { 1, 2, 7, 3, 19, 5, 31 };
        var offset = 0;
        var chunkIndex = 0;
        while (offset < bytes.Length)
        {
            var count = Math.Min(chunkSizes[chunkIndex++ % chunkSizes.Length], bytes.Length - offset);
            frames.AddRange(decoder.Append(bytes.AsSpan(offset, count)));
            offset += count;
        }

        Require(frames.Count == 2, "Fragmented multiple frames were not decoded exactly.");
        Require(DapMessageCodec.DecodeIncoming(frames[0]) is DapIncomingResponse, "The first frame was not a response.");
        Require(DapMessageCodec.DecodeIncoming(frames[1]) is DapIncomingEvent, "The second frame was not an event.");
        return Task.CompletedTask;
    }

    public static Task DapMalformedFrameAsync()
    {
        var decoder = new DapFrameDecoder();
        RequireThrows<DapProtocolException>(
            () => decoder.Append("Content-Length: nope\r\n\r\n{}"u8),
            "An invalid Content-Length was accepted.");

        var smallDecoder = new DapFrameDecoder(maximumHeaderBytes: 64, maximumPayloadBytes: 8);
        RequireThrows<DapProtocolException>(
            () => smallDecoder.Append("Content-Length: 9\r\n\r\n123456789"u8),
            "An oversized DAP payload was accepted.");
        return Task.CompletedTask;
    }

    public static async Task DapLaunchLifecycleAsync()
    {
        await using var adapter = new FakeDapAdapter();
        await using var session = new DapSession(adapter);
        var capabilities = await session.InitializeAsync();
        Require(capabilities.SupportsConfigurationDoneRequest, "Configuration capability negotiation failed.");
        Require(capabilities.SupportsCancelRequest, "Cancellation capability negotiation failed.");

        await session.StartAsync(DapStartMode.Launch, JsonSerializer.SerializeToElement(new { noDebug = false }));
        Require(session.State == DapSessionState.Configuring, "The launch session did not enter configuration.");
        var breakpoints = await session.SetBreakpointsAsync(
            new DapSource("Program.cs", @"C:\fixture\Program.cs"),
            new[] { new DapSourceBreakpoint(10) });
        Require(breakpoints.Count == 1 && breakpoints[0].Verified, "Breakpoint verification failed.");
        await session.ConfigurationDoneAsync();

        await adapter.EmitStoppedAsync();
        await EventuallyAsync(() => session.State == DapSessionState.Stopped);
        var threads = await session.GetThreadsAsync();
        var frames = await session.GetStackTraceAsync(threads.Single().Id);
        var scopes = await session.GetScopesAsync(frames.Single().Id);
        var variables = await session.GetVariablesAsync(scopes.Single().VariablesReference);
        var evaluation = await session.EvaluateAsync("answer", frames.Single().Id);
        Require(variables.Single().Value == "42", "Variable inspection failed.");
        Require(evaluation.Result == "42", "Expression evaluation failed.");

        adapter.HoldEvaluateResponses = true;
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            await RequireThrowsAsync<OperationCanceledException>(
                () => session.EvaluateAsync("cancel-me", frames.Single().Id, cancellationToken: cancellation.Token),
                "A held evaluate request did not cancel.");
        }
        await EventuallyAsync(() => adapter.Commands.Contains("cancel"));
        adapter.HoldEvaluateResponses = false;

        await session.ContinueAsync(threads.Single().Id);
        Require(session.State == DapSessionState.Running, "Continue did not resume the session.");
        await adapter.EmitStoppedAsync();
        await EventuallyAsync(() => session.State == DapSessionState.Stopped);
        await session.StepAsync(DapStepKind.Over, threads.Single().Id);
        Require(session.State == DapSessionState.Running, "Step-over did not resume the session.");
        await adapter.EmitStoppedAsync();
        await EventuallyAsync(() => session.State == DapSessionState.Stopped);
        await session.StepAsync(DapStepKind.Into, threads.Single().Id);
        await adapter.EmitStoppedAsync();
        await EventuallyAsync(() => session.State == DapSessionState.Stopped);
        await session.StepAsync(DapStepKind.Out, threads.Single().Id);
        await session.DisconnectAsync();
        Require(session.State == DapSessionState.Disconnected, "Disconnect did not complete.");
    }

    public static async Task DapAttachAndExitAsync()
    {
        await using var adapter = new FakeDapAdapter();
        await using var session = new DapSession(adapter);
        await session.InitializeAsync();
        await session.StartAsync(DapStartMode.Attach, JsonSerializer.SerializeToElement(new { processId = 1234 }));
        Require(session.StartMode == DapStartMode.Attach, "Attach mode was not retained.");
        await session.ConfigurationDoneAsync();
        adapter.Complete();
        await EventuallyAsync(() => session.State == DapSessionState.Exited);
    }

    public static Task LspFramingAsync()
    {
        var notification = new LspNotification("window/logMessage", JsonSerializer.SerializeToElement(new { type = 3, message = "ready" }));
        var response = new { jsonrpc = "2.0", id = 7, result = new { hoverProvider = true } };
        var bytes = LspMessageCodec.EncodeFrame(notification).Concat(LspMessageCodec.EncodeFrame(response)).ToArray();
        using var decoder = new LspFrameDecoder();
        var frames = new List<byte[]>();
        var offset = 0;
        foreach (var chunkSize in new[] { 1, 3, 7, 2, 19, 5, 31, int.MaxValue })
        {
            if (offset >= bytes.Length) break;
            var count = Math.Min(chunkSize, bytes.Length - offset);
            frames.AddRange(decoder.Append(bytes.AsSpan(offset, count)));
            offset += count;
        }

        Require(frames.Count == 2, "Fragmented LSP frames were not decoded exactly.");
        Require(LspMessageCodec.DecodeIncoming(frames[0]) is LspIncomingNotification, "The first LSP frame was not a notification.");
        Require(LspMessageCodec.DecodeIncoming(frames[1]) is LspIncomingResponse, "The second LSP frame was not a response.");
        using var smallDecoder = new LspFrameDecoder(maximumHeaderBytes: 64, maximumPayloadBytes: 8);
        RequireThrows<LspProtocolException>(
            () => smallDecoder.Append("Content-Length: 9\r\n\r\n123456789"u8),
            "An oversized LSP payload was accepted.");
        RequireThrows<LspProtocolException>(
            () => LspMessageCodec.DecodeIncoming("{\"id\":1,\"result\":null}"u8),
            "A payload without JSON-RPC 2.0 was accepted.");
        return Task.CompletedTask;
    }

    public static async Task LspLifecycleAsync()
    {
        await using var adapter = new FakeLspAdapter();
        await using var session = new LspSession(adapter);
        LspPublishDiagnosticsParams? published = null;
        session.DiagnosticsPublished += value => published = value;
        var result = await session.InitializeAsync("file:///C:/fixture/");
        Require(result.ServerInfo?.Name == "Hermes fake LSP", "LSP server information was not retained.");
        Require(session.State == LspSessionState.Ready, "The LSP session did not become ready.");

        const string uri = "file:///C:/fixture/Program.cs";
        await session.OpenDocumentAsync(uri, "csharp", 1, "class Program { }");
        await EventuallyAsync(() => published is not null);
        Require(published!.Diagnostics.Count == 1, "Published LSP diagnostics were not normalized.");
        Require(published.Diagnostics[0].Message == "Synthetic warning", "The diagnostic message changed.");
        var position = new LspPosition(0, 6);
        Require((await session.HoverAsync(uri, position)) is not null, "Hover returned no result.");
        Require((await session.CompletionAsync(uri, position)) is not null, "Completion returned no result.");
        Require((await session.DefinitionAsync(uri, position)) is not null, "Definition returned no result.");
        Require((await session.ReferencesAsync(uri, position)) is not null, "References returned no result.");
        Require((await session.RenameAsync(uri, position, "RenamedProgram")) is not null, "Rename returned no result.");
        await session.ChangeDocumentAsync(uri, 2, "class RenamedProgram { }");
        await session.CloseDocumentAsync(uri);
        await session.ShutdownAsync();
        Require(session.State == LspSessionState.Exited, "The LSP session did not shut down cleanly.");
        Require(adapter.Methods.Contains("initialized"), "The initialized notification was not sent.");
        Require(adapter.Methods.Contains("textDocument/didChange"), "The document change was not sent.");
        Require(adapter.Methods.Contains("exit"), "The LSP exit notification was not sent.");
    }

    public static async Task LspCancellationAndServerRequestAsync()
    {
        await using var adapter = new FakeLspAdapter { HoldHoverResponses = true };
        await using var session = new LspSession(adapter);
        await session.InitializeAsync("file:///C:/fixture/");
        const string uri = "file:///C:/fixture/Program.cs";
        await session.OpenDocumentAsync(uri, "csharp", 1, "class Program { }");
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            await RequireThrowsAsync<OperationCanceledException>(
                () => session.HoverAsync(uri, new LspPosition(0, 2), cancellation.Token),
                "A held LSP hover request did not cancel.");
        }
        await EventuallyAsync(() => adapter.Methods.Contains("$/cancelRequest"));

        adapter.EmitServerRequest("client/registerCapability");
        await EventuallyAsync(() => adapter.Outgoing.OfType<LspClientResponse>().Any());
        var rejected = adapter.Outgoing.OfType<LspClientResponse>().Last();
        Require(rejected.Error?.Code == -32601, "An unsupported LSP server request was not rejected safely.");
        adapter.HoldHoverResponses = false;
        await session.CloseDocumentAsync(uri);
        await session.ShutdownAsync();
    }

    public static async Task LspProcessTransportAsync()
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The smoke executable path is unavailable.");
        RequireThrows<ArgumentException>(
            () => LspProcessTransport.Start(new LspProcessLaunchOptions(
                "relative-language-server.exe", Array.Empty<string>(), Path.GetTempPath())),
            "A relative language-server executable was accepted.");

        const string sentinelName = "HERMES_LSP_SECRET_SENTINEL";
        const string sentinelValue = "must-not-reach-language-server";
        var previousSentinel = Environment.GetEnvironmentVariable(sentinelName);
        LspProcessTransport transport;
        try
        {
            Environment.SetEnvironmentVariable(sentinelName, sentinelValue);
            transport = LspProcessTransport.Start(new LspProcessLaunchOptions(
                executable,
                new[] { "--fake-lsp-server" },
                AppContext.BaseDirectory,
                MaximumStandardErrorCharacters: 128));
        }
        finally
        {
            Environment.SetEnvironmentVariable(sentinelName, previousSentinel);
        }
        await using var ownedTransport = transport;
        await using var session = new LspSession(ownedTransport);
        LspPublishDiagnosticsParams? published = null;
        session.DiagnosticsPublished += value => published = value;
        var initialized = await session.InitializeAsync(new Uri(AppContext.BaseDirectory).AbsoluteUri);
        Require(initialized.ServerInfo?.Name == "Hermes stdio fixture", "The stdio fixture did not initialize.");
        var document = new Uri(Path.Combine(AppContext.BaseDirectory, "Fixture.cs")).AbsoluteUri;
        await session.OpenDocumentAsync(document, "csharp", 1, "class Fixture { }");
        await EventuallyAsync(() => published is not null);
        Require(published!.Diagnostics.Single().Message == "Stdio warning", "The stdio diagnostic changed.");
        await session.CloseDocumentAsync(document);
        await session.ShutdownAsync();
        await EventuallyAsync(() => ownedTransport.HasExited);
        await EventuallyAsync(() => ownedTransport.DroppedStandardErrorCharacters > 0);
        Require(ownedTransport.StandardError.Length == 128, "The stdio error capture was not bounded.");
        Require(!ownedTransport.StandardError.Contains(sentinelValue, StringComparison.Ordinal),
            "The parent environment leaked into the language-server child.");
    }

    private static async Task EventuallyAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (!predicate() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Require(predicate(), "The expected asynchronous state was not observed.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void RequireThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static async Task RequireThrowsAsync<TException>(Func<Task> action, string message)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }
}

internal sealed class BuildFixture : IDisposable
{
    private BuildFixture(string parent, string workspaceRoot, string outputProject, string slowProject, string brokenProject, string outsideProject)
    {
        Parent = parent;
        WorkspaceRoot = workspaceRoot;
        OutputProject = outputProject;
        SlowProject = slowProject;
        BrokenProject = brokenProject;
        OutsideProject = outsideProject;
    }

    public string Parent { get; }
    public string WorkspaceRoot { get; }
    public string OutputProject { get; }
    public string SlowProject { get; }
    public string BrokenProject { get; }
    public string OutsideProject { get; }

    public static BuildFixture Create()
    {
        var parent = Path.Combine(Path.GetTempPath(), $"HermesDeveloperServicesSmoke-{Guid.NewGuid():N}");
        var workspace = Path.Combine(parent, "workspace");
        Directory.CreateDirectory(workspace);
        var outputProject = Path.Combine(workspace, "Output.csproj");
        var slowProject = Path.Combine(workspace, "Slow.csproj");
        var brokenDirectory = Path.Combine(workspace, "Broken");
        Directory.CreateDirectory(brokenDirectory);
        var brokenProject = Path.Combine(brokenDirectory, "Broken.csproj");
        var outsideProject = Path.Combine(parent, "Outside.csproj");

        var messages = new StringBuilder();
        for (var index = 0; index < 300; index++)
        {
            messages.AppendLine($"    <Message Importance=\"high\" Text=\"HERMES-SMOKE-{index:D4}-XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX\" />");
        }

        File.WriteAllText(outputProject, $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <Target Name="EmitBoundedOutput" BeforeTargets="Build">
            {{messages}}  </Target>
            </Project>
            """);
        File.WriteAllText(slowProject, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <Target Name="DelayBuild" BeforeTargets="Build">
                <Sleep Delay="30000" />
              </Target>
            </Project>
            """);
        File.WriteAllText(brokenProject, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <ItemGroup><Compile Include="Broken.cs" /></ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(brokenDirectory, "Broken.cs"), "class Broken { void Fail() { int value = ; } }");
        File.WriteAllText(outsideProject, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        return new BuildFixture(parent, workspace, outputProject, slowProject, brokenProject, outsideProject);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Parent, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
