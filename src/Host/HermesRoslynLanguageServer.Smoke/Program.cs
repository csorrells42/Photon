using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using HermesDeveloperServices;
using HermesRoslynLanguageServer;

var tests = new (string Name, Func<Task> Run)[]
{
    ("fixed installer ownership and hash validation", FixedInstallerOwnership),
    ("fixed stdio launch and capability negotiation", FixedLaunchAndCapabilities),
    ("bounded diagnostics and complete language surface", LanguageSurface),
    ("solution ownership cancellation and explicit recovery", SolutionOwnershipAndRecovery),
    ("ambiguous solution send is never replayed", AmbiguousSolutionSendIsNeverReplayed),
    ("adapter exit explicit restart and workspace change", AdapterExitRestartAndWorkspaceChange),
    ("startup cancellation requires explicit recovery", StartupCancellationRequiresExplicitRecovery),
    ("workspace paths and server requests are bounded", WorkspacePathsAndServerRequests),
    ("diagnostic pulls are latest-wins and nonblocking", DiagnosticPullsAreLatestWins),
    ("request cancellation is correlated", CancellationIsCorrelated),
    ("stale document versions are rejected", StaleVersionsAreRejected),
    ("stderr and language output are bounded", OutputIsBounded),
    ("missing capabilities fail and shutdown is clean", CapabilityFailureAndShutdown),
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {test.Name}: {exception}");
    }
}
Console.WriteLine($"HermesRoslynLanguageServer smoke: {tests.Length - failed}/{tests.Length} passed.");
return failed == 0 ? 0 : 1;

static async Task FixedInstallerOwnership()
{
    using var fixture = new Fixture();
    var factory = new FakeRoslynServerFactory();
    await using var provider = fixture.Provider(factory);
    var discovery = await provider.DiscoverExecutablesAsync(
        new ToolchainDiscoveryContext(fixture.Workspace, ToolchainExecutionKind.LocalSidecarProcess),
        CancellationToken.None);
    Equal(ToolchainAvailabilityState.Available, discovery.Availability.State, "valid pinned executable");
    Equal("roslyn-lsp", provider.Descriptor.ProviderId, "provider id");
    True(provider.Descriptor.Lsp.Supported && !provider.Descriptor.Build.Supported && !provider.Descriptor.Dap.Supported, "provider capabilities");

    File.AppendAllText(fixture.Executable, "tampered");
    var tampered = await provider.DiscoverExecutablesAsync(
        new ToolchainDiscoveryContext(fixture.Workspace, ToolchainExecutionKind.LocalSidecarProcess),
        CancellationToken.None);
    Equal("roslyn_hash_mismatch", tampered.Availability.Code, "hash mismatch");

    Throws<ArgumentException>(() => new RoslynLanguageServerProvider(fixture.Configuration with
    {
        ExecutablePath = Path.Combine(fixture.Root, "outside", "Microsoft.CodeAnalysis.LanguageServer.exe"),
    }), "outside installer executable");
    Throws<ArgumentException>(() => new RoslynLanguageServerProvider(fixture.Configuration with
    {
        ExecutablePath = "Microsoft.CodeAnalysis.LanguageServer.exe",
    }), "relative executable");
}

static async Task FixedLaunchAndCapabilities()
{
    using var fixture = new Fixture();
    var factory = new FakeRoslynServerFactory();
    await using var provider = fixture.Provider(factory);
    await provider.StartAsync(fixture.StartContext(), CancellationToken.None);
    var spec = factory.LastLaunchSpec ?? throw new InvalidOperationException("No launch spec was captured.");
    Equal(Path.GetFullPath(fixture.Executable), spec.ExecutablePath, "fixed executable");
    EqualSequence(new[]
    {
        "--stdio",
        "--logLevel", "Error",
        "--telemetryLevel", "off",
        "--extensionLogDirectory", fixture.ExtensionLogDirectory,
    }, spec.Arguments, "fixed arguments");
    Equal(Path.GetFullPath(fixture.Workspace), spec.WorkingDirectory, "working directory");
    Equal(8, spec.Environment.Count, "fixed environment count");
    Equal(fixture.DotnetRoot, spec.Environment["DOTNET_ROOT"], "fixed DOTNET_ROOT");
    Equal("1", spec.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"], "telemetry opt out");
    Equal(fixture.TemporaryDirectory, spec.Environment["DOTNET_CLI_HOME"], "fixed CLI home");
    Equal(fixture.DotnetRoot, spec.Environment["PATH"], "fixed PATH");
    Equal(fixture.TemporaryDirectory, spec.Environment["TEMP"], "fixed TEMP");
    Equal(fixture.TemporaryDirectory, spec.Environment["TMP"], "fixed TMP");
    True(provider.Session.Capabilities?.SupportsRequiredSurface == true, "required capabilities negotiated");
    Equal(ToolchainLifecycleState.Ready, provider.LifecycleState, "ready lifecycle");
}

static async Task LanguageSurface()
{
    using var fixture = new Fixture();
    var factory = new FakeRoslynServerFactory();
    await using var provider = fixture.Provider(factory);
    await provider.StartAsync(fixture.StartContext(), CancellationToken.None);
    var session = provider.Session;
    var uri = fixture.DocumentUri;
    await session.OpenSolutionAsync(fixture.SolutionPath, CancellationToken.None);
    await session.OpenDocumentAsync(uri, 1, "class C { int Value; }", CancellationToken.None);

    var position = new LspPosition(0, 8);
    True(await session.CompletionAsync(uri, 1, position) is not null, "completion");
    True(await session.HoverAsync(uri, 1, position) is not null, "hover");
    True(await session.DefinitionAsync(uri, 1, position) is not null, "definition");
    True(await session.ReferencesAsync(uri, 1, position) is not null, "references");
    True(await session.RenameAsync(uri, 1, position, "Renamed") is not null, "rename");
    True(await session.CodeActionsAsync(uri, 1, new LspRange(position, position)) is not null, "code actions");

    LspPublishDiagnosticsParams? published = null;
    session.DiagnosticsPublished += value => published = value;
    factory.Server!.PublishDiagnostics(uri, 1, new[]
    {
        new LspDiagnostic(new LspRange(position, position), 1, null, new string('s', 200), new string('m', 20_000)),
    });
    await WaitUntil(() => published is not null);
    Equal(128, published!.Diagnostics[0].Source!.Length, "diagnostic source bound");
    Equal(16_384, published.Diagnostics[0].Message.Length, "diagnostic message bound");
    await session.CloseDocumentAsync(uri, CancellationToken.None);

    var methods = factory.Server.Messages.OfType<LspRequest>().Select(message => message.Method).ToArray();
    foreach (var required in new[]
    {
        "textDocument/completion", "textDocument/hover", "textDocument/definition",
        "textDocument/references", "textDocument/rename", "textDocument/codeAction",
    }) True(methods.Contains(required), $"method {required}");
}

static async Task SolutionOwnershipAndRecovery()
{
    using var fixture = new Fixture();
    var factory = new FakeRoslynServerFactory { HoldSolutionInitialization = true };
    await using var provider = fixture.Provider(factory);
    await provider.StartAsync(fixture.StartContext(), CancellationToken.None);
    var server = factory.Server!;

    var initialize = server.Messages.OfType<LspRequest>().Single(message => message.Method == "initialize");
    var parameters = initialize.Params!.Value;
    True(parameters.GetProperty("capabilities").GetProperty("workspace").GetProperty("workspaceFolders").GetBoolean(),
        "workspace-folder capability advertised");
    var folders = parameters.GetProperty("workspaceFolders");
    Equal(1, folders.GetArrayLength(), "one bounded workspace folder");
    var workspaceUri = new Uri(Path.GetFullPath(fixture.Workspace).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).AbsoluteUri;
    Equal(workspaceUri, folders[0].GetProperty("uri").GetString(), "workspace folder URI");

    using var cancellation = new CancellationTokenSource();
    var first = provider.Session.OpenSolutionAsync(fixture.SolutionPath, cancellation.Token);
    await WaitUntil(() => server.Messages.OfType<LspNotification>().Count(message => message.Method == "solution/open") == 1);
    cancellation.Cancel();
    await ThrowsAsync<OperationCanceledException>(async () => await first, "cancelled solution initialization");

    var open = server.Messages.OfType<LspNotification>().Single(message => message.Method == "solution/open");
    Equal(new Uri(fixture.SolutionPath).AbsoluteUri,
        open.Params!.Value.GetProperty("solution").GetString(), "exact selected solution URI");

    var recovery = provider.Session.OpenSolutionAsync(fixture.SolutionPath, CancellationToken.None);
    await Task.Delay(30);
    Equal(1, server.Messages.OfType<LspNotification>().Count(message => message.Method == "solution/open"),
        "explicit retry does not replay solution/open");
    True(!recovery.IsCompleted, "retry awaits original initialization");
    server.PublishProjectInitializationComplete();
    await recovery;

    var otherSolution = Path.Combine(fixture.Workspace, "Other.sln");
    File.WriteAllText(otherSolution, "synthetic other solution");
    await ThrowsAsync<InvalidOperationException>(
        () => provider.Session.OpenSolutionAsync(otherSolution, CancellationToken.None),
        "different solution rejected");
    await ThrowsAsync<ArgumentException>(
        () => provider.Session.OpenSolutionAsync("relative.sln", CancellationToken.None),
        "relative solution rejected");
}

static async Task AmbiguousSolutionSendIsNeverReplayed()
{
    using var fixture = new Fixture();
    var factory = new FakeRoslynServerFactory { FailFirstSolutionOpen = true };
    await using var provider = fixture.Provider(factory);
    await provider.StartAsync(fixture.StartContext(), CancellationToken.None);
    var firstServer = factory.Server!;

    await ThrowsAsync<LspSessionException>(
        () => provider.Session.OpenSolutionAsync(fixture.SolutionPath, CancellationToken.None),
        "ambiguous solution send fails");
    Equal(1, firstServer.Messages.OfType<LspNotification>().Count(message => message.Method == "solution/open"),
        "first solution notification recorded once");
    await ThrowsAsync<LspSessionException>(
        () => provider.Session.OpenSolutionAsync(fixture.SolutionPath, CancellationToken.None),
        "same-session retry retains the original failure");
    Equal(1, firstServer.Messages.OfType<LspNotification>().Count(message => message.Method == "solution/open"),
        "ambiguous solution notification is not replayed");

    await provider.StopAsync(CancellationToken.None);
    await provider.StartAsync(fixture.StartContext(), CancellationToken.None);
    await provider.Session.OpenSolutionAsync(fixture.SolutionPath, CancellationToken.None);
    Equal(2, factory.Servers.Count, "explicit restart creates a clean solution owner");
}

static async Task AdapterExitRestartAndWorkspaceChange()
{
    using var fixture = new Fixture();
    var factory = new FakeRoslynServerFactory { HoldSolutionInitialization = true };
    await using var provider = fixture.Provider(factory);
    await provider.StartAsync(fixture.StartContext(), CancellationToken.None);
    var firstSession = provider.Session;
    var firstServer = factory.Server!;
    var pendingInitialization = firstSession.OpenSolutionAsync(fixture.SolutionPath, CancellationToken.None);
    await WaitUntil(() => firstServer.Messages.OfType<LspNotification>().Any(message => message.Method == "solution/open"));

    firstServer.ExitUnexpectedly();
    await WaitUntil(() => firstSession.State == LspSessionState.Exited);
    await ThrowsAsync<LspSessionException>(
        async () => await pendingInitialization.WaitAsync(TimeSpan.FromSeconds(2)),
        "adapter exit fails pending solution initialization");
    Equal(1, factory.Servers.Count, "adapter exit does not automatically restart");
    Throws<InvalidOperationException>(() => _ = provider.Session, "exited session is unavailable");

    await provider.StartAsync(fixture.StartContext(), CancellationToken.None);
    Equal(2, factory.Servers.Count, "explicit same-workspace restart");
    Equal(LspSessionState.Ready, provider.Session.State, "restarted session ready");

    var otherWorkspace = Path.Combine(fixture.Root, "other-workspace");
    Directory.CreateDirectory(otherWorkspace);
    await ThrowsAsync<InvalidOperationException>(
        () => provider.StartAsync(fixture.StartContext(otherWorkspace), CancellationToken.None).AsTask(),
        "live workspace change rejected");
    Equal(2, factory.Servers.Count, "workspace rejection does not restart");

    await provider.StopAsync(CancellationToken.None);
    await provider.StartAsync(fixture.StartContext(otherWorkspace), CancellationToken.None);
    Equal(3, factory.Servers.Count, "workspace change after explicit stop");
    Equal(LspSessionState.Ready, provider.Session.State, "changed workspace ready");
}

static async Task StartupCancellationRequiresExplicitRecovery()
{
    using var fixture = new Fixture();
    var factory = new FakeRoslynServerFactory { HoldFirstInitialize = true };
    await using var provider = fixture.Provider(factory);
    using var cancellation = new CancellationTokenSource();
    var pending = provider.StartAsync(fixture.StartContext(), cancellation.Token).AsTask();
    await WaitUntil(() => factory.Server?.HeldRequestIds.Count == 1);
    cancellation.Cancel();
    await ThrowsAsync<OperationCanceledException>(async () => await pending, "startup cancellation");
    Equal(ToolchainLifecycleState.Faulted, provider.LifecycleState, "cancelled startup faulted");
    Equal(1, factory.Servers.Count, "cancelled startup does not automatically restart");
    True(factory.Servers[0].Disposed, "cancelled startup transport disposed");

    await provider.StartAsync(fixture.StartContext(), CancellationToken.None);
    Equal(2, factory.Servers.Count, "explicit startup recovery");
    Equal(ToolchainLifecycleState.Ready, provider.LifecycleState, "recovered startup ready");
}

static async Task WorkspacePathsAndServerRequests()
{
    using var fixture = new Fixture();
    var factory = new FakeRoslynServerFactory();
    await using var provider = fixture.Provider(factory);
    await provider.StartAsync(fixture.StartContext(), CancellationToken.None);

    var outsideDocument = Path.Combine(fixture.Root, "Outside.cs");
    File.WriteAllText(outsideDocument, "class Outside { }");
    await ThrowsAsync<UnauthorizedAccessException>(
        () => provider.Session.OpenDocumentAsync(new Uri(outsideDocument).AbsoluteUri, 1, "class Outside { }", CancellationToken.None),
        "outside document rejected");

    var outsideSolution = Path.Combine(fixture.Root, "Outside.sln");
    File.WriteAllText(outsideSolution, "synthetic outside solution");
    await ThrowsAsync<UnauthorizedAccessException>(
        () => provider.Session.OpenSolutionAsync(outsideSolution, CancellationToken.None),
        "outside solution rejected");

    factory.Server!.SendServerRequest(781, "workspace/configuration");
    await WaitUntil(() => factory.Server.Messages.OfType<LspClientResponse>().Any(response => response.Id == 781));
    var rejection = factory.Server.Messages.OfType<LspClientResponse>().Single(response => response.Id == 781);
    Equal(-32601, rejection.Error?.Code, "unadvertised server request rejection");
    True(rejection.Result is null, "unadvertised server request has no result");
}

static async Task DiagnosticPullsAreLatestWins()
{
    using var fixture = new Fixture();
    var factory = new FakeRoslynServerFactory { HeldMethod = "textDocument/diagnostic" };
    await using var provider = fixture.Provider(factory);
    await provider.StartAsync(fixture.StartContext(), CancellationToken.None);
    var session = provider.Session;
    var server = factory.Server!;

    await session.OpenDocumentAsync(fixture.DocumentUri, 1, "class C { }", CancellationToken.None)
        .WaitAsync(TimeSpan.FromSeconds(1));
    await WaitUntil(() => server.HeldRequestIds.Count == 1);
    var firstPull = server.HeldRequestIds.First();

    await session.ChangeDocumentAsync(fixture.DocumentUri, 2, "class C { int X; }", CancellationToken.None)
        .WaitAsync(TimeSpan.FromSeconds(1));
    await WaitUntil(() => server.HeldRequestIds.Count == 2 && server.CancelledRequestIds.Contains(firstPull));
    var secondPull = server.HeldRequestIds.Last();

    await session.CloseDocumentAsync(fixture.DocumentUri, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
    await WaitUntil(() => server.CancelledRequestIds.Contains(secondPull));
    Throws<InvalidOperationException>(
        () => _ = session.HoverAsync(fixture.DocumentUri, 2, new LspPosition(0, 1)),
        "closed document no longer participates in language requests");
}

static async Task CancellationIsCorrelated()
{
    using var fixture = new Fixture();
    var factory = new FakeRoslynServerFactory { HeldMethod = "textDocument/completion" };
    await using var provider = fixture.Provider(factory);
    await provider.StartAsync(fixture.StartContext(), CancellationToken.None);
    await provider.Session.OpenDocumentAsync(fixture.DocumentUri, 1, "class C {}", CancellationToken.None);
    using var cancellation = new CancellationTokenSource();
    var pending = provider.Session.CompletionAsync(fixture.DocumentUri, 1, new LspPosition(0, 1), cancellation.Token);
    await WaitUntil(() => factory.Server!.HeldRequestId is not null);
    var server = factory.Server!;
    cancellation.Cancel();
    await ThrowsAsync<OperationCanceledException>(async () => await pending, "cancelled completion");
    await WaitUntil(() => server.CancelledRequestIds.Count == 1);
    Equal(server.HeldRequestId, server.CancelledRequestIds.Single(), "exact cancellation id");
}

static async Task StaleVersionsAreRejected()
{
    using var fixture = new Fixture();
    var factory = new FakeRoslynServerFactory();
    await using var provider = fixture.Provider(factory);
    await provider.StartAsync(fixture.StartContext(), CancellationToken.None);
    var session = provider.Session;
    await session.OpenDocumentAsync(fixture.DocumentUri, 1, "class C {}", CancellationToken.None);
    await session.ChangeDocumentAsync(fixture.DocumentUri, 2, "class C { int X; }", CancellationToken.None);
    var change = factory.Server!.Messages.OfType<LspNotification>()
        .Single(message => message.Method == "textDocument/didChange");
    var contentChange = change.Params!.Value.GetProperty("contentChanges")[0];
    True(contentChange.TryGetProperty("range", out _), "incremental replacement range");
    Equal(10, contentChange.GetProperty("rangeLength").GetInt32(), "previous document range length");
    await ThrowsAsync<RoslynStaleDocumentException>(
        () => session.ChangeDocumentAsync(fixture.DocumentUri, 2, "stale", CancellationToken.None),
        "duplicate document version");
    Throws<RoslynStaleDocumentException>(
        () => _ = session.HoverAsync(fixture.DocumentUri, 1, new LspPosition(0, 1)),
        "stale language request");

    var diagnostics = 0;
    session.DiagnosticsPublished += _ => diagnostics++;
    factory.Server!.PublishDiagnostics(fixture.DocumentUri, 1, Array.Empty<LspDiagnostic>());
    await Task.Delay(30);
    Equal(0, diagnostics, "stale diagnostics dropped");
    factory.Server.PublishDiagnostics(fixture.DocumentUri, 2, Array.Empty<LspDiagnostic>());
    await WaitUntil(() => diagnostics == 1);
}

static async Task OutputIsBounded()
{
    using var fixture = new Fixture();
    var factory = new FakeRoslynServerFactory
    {
        StandardErrorSeed = new string('e', RoslynLanguageServerProvider.MaximumStandardErrorCharacters + 500),
        OversizedMethod = "textDocument/completion",
    };
    await using var provider = fixture.Provider(factory);
    await provider.StartAsync(fixture.StartContext(), CancellationToken.None);
    await provider.Session.OpenDocumentAsync(fixture.DocumentUri, 1, "class C {}", CancellationToken.None);
    Equal(RoslynLanguageServerProvider.MaximumStandardErrorCharacters, provider.Session.BoundedStandardError.Length, "stderr retained bound");
    Equal(500L, provider.Session.DroppedStandardErrorCharacters, "stderr dropped count");
    await ThrowsAsync<LspProtocolException>(
        async () => _ = await provider.Session.CompletionAsync(fixture.DocumentUri, 1, new LspPosition(0, 1)),
        "oversized language result");
}

static async Task CapabilityFailureAndShutdown()
{
    using var fixture = new Fixture();
    var missingFactory = new FakeRoslynServerFactory { OmitCodeActions = true };
    await using (var missing = fixture.Provider(missingFactory))
    {
        await ThrowsAsync<LspSessionException>(
            () => missing.StartAsync(fixture.StartContext(), CancellationToken.None).AsTask(),
            "missing capability");
        Equal(ToolchainLifecycleState.Faulted, missing.LifecycleState, "faulted lifecycle");
        True(missingFactory.Server?.Disposed == true, "failed server disposed");
    }

    var factory = new FakeRoslynServerFactory();
    await using var provider = fixture.Provider(factory);
    await provider.StartAsync(fixture.StartContext(), CancellationToken.None);
    await provider.StopAsync(CancellationToken.None);
    Equal(ToolchainLifecycleState.Stopped, provider.LifecycleState, "stopped lifecycle");
    True(factory.Server!.Messages.OfType<LspRequest>().Any(message => message.Method == "shutdown"), "shutdown request");
    True(factory.Server.Messages.OfType<LspNotification>().Any(message => message.Method == "exit"), "exit notification");
    True(factory.Server.Disposed, "transport disposed");
}

static async Task WaitUntil(Func<bool> condition)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    while (!condition()) await Task.Delay(5, timeout.Token);
}

static void True(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Equal<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{message}: expected {expected}, actual {actual}");
}

static void EqualSequence<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual, string message)
{
    if (!expected.SequenceEqual(actual)) throw new InvalidOperationException($"{message}: sequences differ");
}

static void Throws<T>(Action action, string message) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"{message}: no {typeof(T).Name}");
}

static async Task ThrowsAsync<T>(Func<Task> action, string message) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"{message}: no {typeof(T).Name}");
}

internal sealed class Fixture : IDisposable
{
    internal Fixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "HermesRoslynSmoke", Guid.NewGuid().ToString("N"));
        InstallerRoot = Path.Combine(Root, "installer", "roslyn");
        DotnetRoot = Path.Combine(InstallerRoot, "dotnet");
        Workspace = Path.Combine(Root, "workspace");
        ExtensionLogDirectory = Path.Combine(Root, "logs");
        TemporaryDirectory = Path.Combine(Root, "temp");
        Directory.CreateDirectory(DotnetRoot);
        Directory.CreateDirectory(Workspace);
        Executable = Path.Combine(InstallerRoot, "Microsoft.CodeAnalysis.LanguageServer.exe");
        File.WriteAllText(Executable, "synthetic fake executable; never launched");
        File.WriteAllText(Path.Combine(Workspace, "Smoke.cs"), "class C {}");
        SolutionPath = Path.Combine(Workspace, "Smoke.sln");
        File.WriteAllText(SolutionPath, "synthetic solution");
        Configuration = new RoslynLanguageServerConfiguration(
            InstallerRoot,
            Executable,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Executable))).ToLowerInvariant(),
            "5.0.0-1.25277.114",
            DotnetRoot,
            ExtensionLogDirectory,
            TemporaryDirectory);
        DocumentUri = new Uri(Path.Combine(Workspace, "Smoke.cs")).AbsoluteUri;
    }

    internal string Root { get; }
    internal string InstallerRoot { get; }
    internal string DotnetRoot { get; }
    internal string Workspace { get; }
    internal string Executable { get; }
    internal string ExtensionLogDirectory { get; }
    internal string TemporaryDirectory { get; }
    internal string DocumentUri { get; }
    internal string SolutionPath { get; }
    internal RoslynLanguageServerConfiguration Configuration { get; }

    internal RoslynLanguageServerProvider Provider(FakeRoslynServerFactory factory) => new(Configuration, factory);

    internal ToolchainStartContext StartContext(string? workspace = null) => new(
        workspace ?? Workspace,
        new[] { new WorkspacePathMapping(workspace ?? Workspace, workspace ?? Workspace) },
        ToolchainExecutionKind.LocalSidecarProcess);

    public void Dispose()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}

internal sealed class FakeRoslynServerFactory : IRoslynServerTransportFactory
{
    internal RoslynServerLaunchSpec? LastLaunchSpec { get; private set; }
    internal FakeRoslynServer? Server { get; private set; }
    internal List<FakeRoslynServer> Servers { get; } = new();
    internal string? HeldMethod { get; init; }
    internal string? OversizedMethod { get; init; }
    internal string StandardErrorSeed { get; init; } = string.Empty;
    internal bool OmitCodeActions { get; init; }
    internal bool HoldSolutionInitialization { get; init; }
    internal bool HoldFirstInitialize { get; init; }
    internal bool FailFirstSolutionOpen { get; init; }

    public ValueTask<IRoslynServerTransport> StartAsync(RoslynServerLaunchSpec launchSpec, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastLaunchSpec = launchSpec;
        var firstServer = Servers.Count == 0;
        Server = new FakeRoslynServer(
            launchSpec,
            HeldMethod,
            OversizedMethod,
            StandardErrorSeed,
            OmitCodeActions,
            HoldSolutionInitialization,
            HoldFirstInitialize && firstServer,
            FailFirstSolutionOpen && firstServer);
        Servers.Add(Server);
        return ValueTask.FromResult<IRoslynServerTransport>(Server);
    }
}

internal sealed class FakeRoslynServer : IRoslynServerTransport
{
    private readonly Channel<LspIncomingMessage> _incoming = Channel.CreateUnbounded<LspIncomingMessage>();
    private readonly string? _heldMethod;
    private readonly string? _oversizedMethod;
    private readonly bool _omitCodeActions;
    private readonly bool _holdSolutionInitialization;
    private readonly bool _holdInitialize;
    private readonly bool _failSolutionOpen;
    private bool _hasExited;

    internal FakeRoslynServer(
        RoslynServerLaunchSpec spec,
        string? heldMethod,
        string? oversizedMethod,
        string standardError,
        bool omitCodeActions,
        bool holdSolutionInitialization,
        bool holdInitialize,
        bool failSolutionOpen)
    {
        _heldMethod = heldMethod;
        _oversizedMethod = oversizedMethod;
        _omitCodeActions = omitCodeActions;
        _holdSolutionInitialization = holdSolutionInitialization;
        _holdInitialize = holdInitialize;
        _failSolutionOpen = failSolutionOpen;
        StandardError = standardError[..Math.Min(standardError.Length, spec.MaximumStandardErrorCharacters)];
        DroppedStandardErrorCharacters = Math.Max(0, standardError.Length - StandardError.Length);
    }

    internal ConcurrentQueue<LspOutgoingMessage> Messages { get; } = new();
    internal ConcurrentQueue<int> CancelledRequestIds { get; } = new();
    internal ConcurrentQueue<int> HeldRequestIds { get; } = new();
    internal int? HeldRequestId { get; private set; }
    internal bool Disposed { get; private set; }

    public bool HasExited => _hasExited;
    public string StandardError { get; }
    public long DroppedStandardErrorCharacters { get; }

    public ValueTask SendAsync(LspOutgoingMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Messages.Enqueue(message);
        switch (message)
        {
            case LspRequest request when request.Method == "initialize" && _holdInitialize:
                HeldRequestId = request.Id;
                HeldRequestIds.Enqueue(request.Id);
                break;
            case LspRequest request when request.Method == "initialize":
                Respond(request.Id, Element(new
                {
                    capabilities = new
                    {
                        textDocumentSync = 1,
                        diagnosticProvider = new { interFileDependencies = true, workspaceDiagnostics = false },
                        completionProvider = new { },
                        hoverProvider = true,
                        definitionProvider = true,
                        referencesProvider = true,
                        renameProvider = true,
                        codeActionProvider = _omitCodeActions ? (object?)null : true,
                    },
                    serverInfo = new { name = "Disposable Fake Roslyn", version = "1" },
                }));
                break;
            case LspRequest request when request.Method == "shutdown":
                Respond(request.Id, null);
                break;
            case LspRequest request when request.Method == _heldMethod:
                HeldRequestId = request.Id;
                HeldRequestIds.Enqueue(request.Id);
                break;
            case LspRequest request when request.Method == "textDocument/diagnostic":
                Respond(request.Id, Element(new { kind = "full", items = Array.Empty<LspDiagnostic>() }));
                break;
            case LspRequest request:
                var result = request.Method == _oversizedMethod
                    ? Element(new { value = new string('x', RoslynLanguageSession.MaximumResultCharacters + 1) })
                    : Element(new { method = request.Method, items = Array.Empty<object>() });
                Respond(request.Id, result);
                break;
            case LspNotification notification when notification.Method == "$/cancelRequest":
                if (notification.Params is { } parameters && parameters.TryGetProperty("id", out var id))
                    CancelledRequestIds.Enqueue(id.GetInt32());
                break;
            case LspNotification notification when notification.Method == "solution/open":
                if (_failSolutionOpen)
                    throw new LspSessionException("Synthetic ambiguous solution/open failure.");
                if (!_holdSolutionInitialization) PublishProjectInitializationComplete();
                break;
            case LspNotification notification when notification.Method == "exit":
                _hasExited = true;
                _incoming.Writer.TryComplete();
                break;
        }
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<LspIncomingMessage> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var message in _incoming.Reader.ReadAllAsync(cancellationToken)) yield return message;
    }

    internal void PublishDiagnostics(string uri, int version, IReadOnlyList<LspDiagnostic> diagnostics)
    {
        _incoming.Writer.TryWrite(new LspIncomingNotification(
            "textDocument/publishDiagnostics",
            Element(new LspPublishDiagnosticsParams(uri, diagnostics, version))));
    }

    internal void PublishProjectInitializationComplete()
    {
        _incoming.Writer.TryWrite(new LspIncomingNotification(
            "workspace/projectInitializationComplete",
            Element(Array.Empty<object>())));
    }

    internal void ExitUnexpectedly()
    {
        _hasExited = true;
        _incoming.Writer.TryComplete();
    }

    internal void SendServerRequest(int id, string method)
    {
        _incoming.Writer.TryWrite(new LspIncomingServerRequest(id, method, Element(new { })));
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _hasExited = true;
        _incoming.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private void Respond(int id, JsonElement? result) =>
        _incoming.Writer.TryWrite(new LspIncomingResponse(id, result, null));

    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
}
