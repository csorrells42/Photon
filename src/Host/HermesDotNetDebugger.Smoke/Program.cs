using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using HermesDeveloperServices;
using HermesDotNetDebugger;
using HermesDotNetDebugger.Smoke;

if (args.Length == 1 && args[0] == NetCoreDbgProvisioning.FixedAdapterArgument)
{
    return await FakeDapAdapter.RunAsync(args).ConfigureAwait(false);
}

var tests = new (string Name, Func<Task> Run)[]
{
    ("pinned provisioning and integrity receipt", TestProvisioningAsync),
    ("explicit launch and attach authorization", TestAuthorizationAsync),
    ("full launch DAP workflow and bounded process", TestLaunchWorkflowAsync),
    ("disconnect fully retires the session before restart", TestDisconnectRetirementAsync),
    ("attach negotiation without a real attach", TestAttachWorkflowAsync),
    ("adapter exit is observed and owned", TestAdapterExitAsync),
    ("provider stop terminates only its exact child", TestExactChildOwnershipAsync),
    ("malformed frame faults and recovery works", TestMalformedFrameAndRecoveryAsync),
    ("negotiation cancellation does not poison recovery", TestNegotiationCancellationAsync),
    ("paths, arguments, expressions, and frames are bounded", TestBoundsAsync),
};

var passed = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run().ConfigureAwait(false);
        Console.WriteLine($"PASS {test.Name}");
        passed++;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"FAIL {test.Name}: {exception.GetType().Name}: {exception.Message}");
    }
}

Console.WriteLine($"{passed} passed, {tests.Length - passed} failed");
return passed == tests.Length ? 0 : 1;

static async Task TestProvisioningAsync()
{
    using var fixture = SmokeFixture.Create();
    await using var provider = new HermesDotNetDebuggerProvider(fixture.InstallRoot, new DecisionPolicy());
    var result = await provider.DiscoverExecutablesAsync(
        new ToolchainDiscoveryContext(fixture.WorkspaceRoot, ToolchainExecutionKind.LocalSidecarProcess),
        CancellationToken.None);
    Assert(result.Availability.State == ToolchainAvailabilityState.Available, "Pinned fake installation was unavailable.");
    Assert(result.Executables.Single().Version == NetCoreDbgProvisioning.Version, "Pinned version drifted.");
    Assert(result.Executables.Single().ExecutablePath == fixture.AdapterPath, "Executable path was not fixed.");

    fixture.WriteReceipt(archiveHash: new string('0', 64));
    var rejected = await provider.DiscoverExecutablesAsync(
        new ToolchainDiscoveryContext(fixture.WorkspaceRoot, ToolchainExecutionKind.LocalSidecarProcess),
        CancellationToken.None);
    Assert(rejected.Availability.Code == "receipt-mismatch", "A mismatched archive digest was accepted.");
    fixture.WriteReceipt();
}

static async Task TestAuthorizationAsync()
{
    using var fixture = SmokeFixture.Create();
    var policy = new DecisionPolicy(allowLaunch: false, allowAttach: false);
    var factory = new CountingTransportFactory();
    await using var provider = new HermesDotNetDebuggerProvider(
        new NetCoreDbgInstallation(fixture.InstallRoot),
        policy,
        factory);
    await StartProviderAsync(provider, fixture.WorkspaceRoot);

    await AssertThrowsAsync<DotNetDebuggerAuthorizationException>(
        () => provider.LaunchAsync(new DotNetDebugLaunchRequest(fixture.ProgramPath)));
    await AssertThrowsAsync<DotNetDebuggerAuthorizationException>(
        () => provider.AttachAsync(new DotNetDebugAttachRequest(424242)));
    Assert(policy.LaunchRequests == 1 && policy.AttachRequests == 1, "Authorization was not requested exactly once.");
    Assert(factory.StartCount == 0, "An adapter started before authorization.");
}

static async Task TestLaunchWorkflowAsync()
{
    using var fixture = SmokeFixture.Create();
    var policy = new DecisionPolicy();
    await using var provider = new HermesDotNetDebuggerProvider(fixture.InstallRoot, policy);
    await StartProviderAsync(provider, fixture.WorkspaceRoot);
    var session = await provider.LaunchAsync(new DotNetDebugLaunchRequest(
        fixture.ProgramPath,
        Arguments: ["alpha", "two words"],
        StopAtEntry: true));
    var processId = session.AdapterProcessId;
    Assert(processId is > 0, "The owned adapter process was not observable to the smoke seam.");
    Assert(session.StartMode == DapStartMode.Launch, "Launch negotiation mode was not retained.");
    Assert(session.AdapterCapabilities.SupportsConfigurationDoneRequest, "Capabilities were not negotiated.");
    Assert(session.AdapterCapabilities.SupportsCancelRequest, "Cancel capability was not negotiated.");

    var events = new ConcurrentQueue<string>();
    session.EventReceived += dapEvent => events.Enqueue(dapEvent.Event);
    var breakpoints = await session.SetBreakpointsAsync(
        fixture.SourcePath,
        [new DapSourceBreakpoint(3)]);
    Assert(breakpoints.Count == 1 && breakpoints[0].Verified, "Breakpoint negotiation failed.");
    await session.ConfigurationDoneAsync();
    await WaitUntilAsync(() => session.State == DapSessionState.Stopped, "stopped event");

    var threads = await session.GetThreadsAsync();
    Assert(threads.Count == 1, "Threads response was not mapped.");
    Assert(threads[0].Name.Contains("env=0", StringComparison.Ordinal), "Adapter environment was not empty.");
    Assert(threads[0].Name.Contains("args=--interpreter=vscode", StringComparison.Ordinal), "Adapter arguments were not fixed.");
    await WaitUntilAsync(() => session.DroppedAdapterStandardErrorCharacters > 0, "bounded stderr drop count");
    Assert(session.BoundedAdapterStandardError.Length <= NetCoreDbgProcessTransport.MaximumStandardErrorCharacters,
        "Adapter stderr retention exceeded its bound.");

    var stack = await session.GetStackTraceAsync(1, 0, 20);
    Assert(stack.Single().Name == "Program.Main", "Stack trace response was not mapped.");
    var scopes = await session.GetScopesAsync(stack[0].Id);
    Assert(scopes.Single().VariablesReference == 20, "Scope response was not mapped.");
    var variables = await session.GetVariablesAsync(scopes[0].VariablesReference, 0, 50);
    Assert(variables.Single().Value == "42", "Variable response was not mapped.");
    var evaluation = await session.EvaluateAsync("1 + 1", stack[0].Id);
    Assert(evaluation.Result == "42", "Evaluation response was not mapped.");

    using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
    {
        await AssertThrowsAsync<OperationCanceledException>(
            () => session.EvaluateAsync("wait", stack[0].Id, cancellationToken: cancellation.Token));
    }
    Assert((await session.EvaluateAsync("1 + 1", stack[0].Id)).Result == "42",
        "The session did not recover after request cancellation.");

    await session.ContinueAsync(1);
    await WaitUntilAsync(() => session.State == DapSessionState.Stopped, "stop after continue");
    await session.StepOverAsync(1);
    await WaitUntilAsync(() => session.State == DapSessionState.Stopped, "stop after step over");
    await session.StepIntoAsync(1);
    await WaitUntilAsync(() => session.State == DapSessionState.Stopped, "stop after step into");
    await session.StepOutAsync(1);
    await WaitUntilAsync(() => session.State == DapSessionState.Stopped, "stop after step out");
    Assert(events.Contains("continued") && events.Contains("stopped"), "Stopped/continued events were not observed.");

    await session.DisconnectAsync();
    await AssertProcessExitedAsync(processId!.Value);
    Assert(policy.LaunchRequests == 1, "Launch authorization count was incorrect.");
}

static async Task TestAttachWorkflowAsync()
{
    using var fixture = SmokeFixture.Create();
    var policy = new DecisionPolicy();
    await using var provider = new HermesDotNetDebuggerProvider(fixture.InstallRoot, policy);
    await StartProviderAsync(provider, fixture.WorkspaceRoot);
    var session = await provider.AttachAsync(new DotNetDebugAttachRequest(424242));
    Assert(session.StartMode == DapStartMode.Attach, "Attach negotiation mode was not retained.");
    await session.ConfigurationDoneAsync();
    await WaitUntilAsync(() => session.State == DapSessionState.Stopped, "attach stopped event");
    var processId = session.AdapterProcessId!.Value;
    await session.DisconnectAsync();
    await AssertProcessExitedAsync(processId);
    Assert(policy.AttachRequests == 1, "Attach was not explicitly authorized.");
}

static async Task TestDisconnectRetirementAsync()
{
    using var fixture = SmokeFixture.Create();
    await using var provider = new HermesDotNetDebuggerProvider(fixture.InstallRoot, new DecisionPolicy());
    await StartProviderAsync(provider, fixture.WorkspaceRoot);

    for (var attempt = 0; attempt < 8; attempt++)
    {
        var session = await provider.LaunchAsync(new DotNetDebugLaunchRequest(fixture.ProgramPath));
        await session.ConfigurationDoneAsync();
        await WaitUntilAsync(() => session.State == DapSessionState.Stopped, "restartable session stop");
        await session.DisconnectAsync();
        Assert(!provider.HasActiveSession, "Disconnect returned before the exact active-session slot was released.");
    }
}

static async Task TestAdapterExitAsync()
{
    using var fixture = SmokeFixture.Create();
    await using var provider = new HermesDotNetDebuggerProvider(fixture.InstallRoot, new DecisionPolicy());
    await StartProviderAsync(provider, fixture.WorkspaceRoot);
    var exitedSession = await provider.LaunchAsync(new DotNetDebugLaunchRequest(fixture.ExitProgramPath));
    var exitedProcessId = exitedSession.AdapterProcessId!.Value;
    await exitedSession.ConfigurationDoneAsync();
    await WaitUntilAsync(() => exitedSession.State == DapSessionState.Exited, "adapter exit state");
    await AssertProcessExitedAsync(exitedProcessId);
    await WaitUntilAsync(() => !provider.HasActiveSession, "automatic terminal session retirement");

    // No caller disposal is permitted between the unexpected exit and this explicit recovery.
    var recovered = await provider.LaunchAsync(new DotNetDebugLaunchRequest(fixture.ProgramPath));
    var recoveredProcessId = recovered.AdapterProcessId!.Value;
    await recovered.ConfigurationDoneAsync();
    await WaitUntilAsync(() => recovered.State == DapSessionState.Stopped, "explicit launch after adapter exit");
    await recovered.DisconnectAsync();
    await AssertProcessExitedAsync(recoveredProcessId);
}

static async Task TestExactChildOwnershipAsync()
{
    using var firstFixture = SmokeFixture.Create();
    using var secondFixture = SmokeFixture.Create();
    await using var firstProvider = new HermesDotNetDebuggerProvider(firstFixture.InstallRoot, new DecisionPolicy());
    await using var secondProvider = new HermesDotNetDebuggerProvider(secondFixture.InstallRoot, new DecisionPolicy());
    await StartProviderAsync(firstProvider, firstFixture.WorkspaceRoot);
    await StartProviderAsync(secondProvider, secondFixture.WorkspaceRoot);
    var firstSession = await firstProvider.LaunchAsync(new DotNetDebugLaunchRequest(firstFixture.ProgramPath));
    var secondSession = await secondProvider.LaunchAsync(new DotNetDebugLaunchRequest(secondFixture.ProgramPath));
    var firstProcessId = firstSession.AdapterProcessId!.Value;
    var secondProcessId = secondSession.AdapterProcessId!.Value;

    await firstProvider.StopAsync(CancellationToken.None);
    await AssertProcessExitedAsync(firstProcessId);
    using (var unrelated = Process.GetProcessById(secondProcessId))
    {
        Assert(!unrelated.HasExited, "Stopping one provider terminated another provider's adapter.");
    }

    await secondSession.DisconnectAsync();
    await AssertProcessExitedAsync(secondProcessId);
}

static async Task TestMalformedFrameAndRecoveryAsync()
{
    using var fixture = SmokeFixture.Create();
    await using var provider = new HermesDotNetDebuggerProvider(fixture.InstallRoot, new DecisionPolicy());
    await StartProviderAsync(provider, fixture.WorkspaceRoot);
    await AssertThrowsAnyAsync(
        () => provider.LaunchAsync(new DotNetDebugLaunchRequest(fixture.MalformedProgramPath)));

    var recovered = await provider.LaunchAsync(new DotNetDebugLaunchRequest(fixture.ProgramPath));
    await recovered.ConfigurationDoneAsync();
    await WaitUntilAsync(() => recovered.State == DapSessionState.Stopped, "recovery after malformed frame");
    await recovered.DisconnectAsync();
}

static async Task TestNegotiationCancellationAsync()
{
    using var fixture = SmokeFixture.Create();
    await using var provider = new HermesDotNetDebuggerProvider(fixture.InstallRoot, new DecisionPolicy());
    await StartProviderAsync(provider, fixture.WorkspaceRoot);
    using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
    {
        await AssertThrowsAsync<OperationCanceledException>(
            () => provider.LaunchAsync(
                new DotNetDebugLaunchRequest(fixture.CancelProgramPath),
                cancellation.Token));
    }

    var recovered = await provider.LaunchAsync(new DotNetDebugLaunchRequest(fixture.ProgramPath));
    await recovered.ConfigurationDoneAsync();
    await WaitUntilAsync(() => recovered.State == DapSessionState.Stopped, "recovery after negotiation cancellation");
    await recovered.DisconnectAsync();
}

static async Task TestBoundsAsync()
{
    using var fixture = SmokeFixture.Create();
    var factory = new CountingTransportFactory();
    await using var provider = new HermesDotNetDebuggerProvider(
        new NetCoreDbgInstallation(fixture.InstallRoot),
        new DecisionPolicy(),
        factory);
    await StartProviderAsync(provider, fixture.WorkspaceRoot);

    await AssertThrowsAsync<UnauthorizedAccessException>(
        () => provider.LaunchAsync(new DotNetDebugLaunchRequest(fixture.OutsideProgramPath)));
    await AssertThrowsAsync<ArgumentOutOfRangeException>(
        () => provider.LaunchAsync(new DotNetDebugLaunchRequest(
            fixture.ProgramPath,
            Arguments: Enumerable.Repeat("x", DebugPathPolicy.MaximumArguments + 1).ToArray())));
    await AssertThrowsAsync<ArgumentOutOfRangeException>(
        () => provider.AttachAsync(new DotNetDebugAttachRequest(0)));
    Assert(factory.StartCount == 0, "Invalid inputs reached process creation.");

    var decoder = new DapFrameDecoder(maximumHeaderBytes: 64, maximumPayloadBytes: 16);
    AssertThrows<DapProtocolException>(() => decoder.Append("Content-Length: 17\r\n\r\n"u8));
}

static ValueTask StartProviderAsync(HermesDotNetDebuggerProvider provider, string workspaceRoot) =>
    provider.StartAsync(
        new ToolchainStartContext(
            workspaceRoot,
            Array.Empty<WorkspacePathMapping>(),
            ToolchainExecutionKind.LocalSidecarProcess),
        CancellationToken.None);

static async Task WaitUntilAsync(Func<bool> condition, string description)
{
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
    while (!condition())
    {
        if (DateTime.UtcNow >= deadline) throw new TimeoutException($"Timed out waiting for {description}.");
        await Task.Delay(10).ConfigureAwait(false);
    }
}

static async Task AssertProcessExitedAsync(int processId)
{
    await WaitUntilAsync(() =>
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }, "owned adapter process exit");
}

static async Task AssertThrowsAsync<TException>(Func<Task> action) where TException : Exception
{
    try
    {
        await action().ConfigureAwait(false);
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static async Task AssertThrowsAnyAsync(Func<Task> action)
{
    try
    {
        await action().ConfigureAwait(false);
    }
    catch
    {
        return;
    }

    throw new InvalidOperationException("Expected an exception.");
}

static void AssertThrows<TException>(Action action) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

internal sealed class DecisionPolicy(bool allowLaunch = true, bool allowAttach = true)
    : IDotNetDebugAuthorizationPolicy
{
    public int LaunchRequests { get; private set; }
    public int AttachRequests { get; private set; }

    public ValueTask<bool> AuthorizeLaunchAsync(
        string workspaceRoot,
        DotNetDebugLaunchRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LaunchRequests++;
        return ValueTask.FromResult(allowLaunch);
    }

    public ValueTask<bool> AuthorizeAttachAsync(
        string workspaceRoot,
        DotNetDebugAttachRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AttachRequests++;
        return ValueTask.FromResult(allowAttach);
    }
}

internal sealed class CountingTransportFactory : INetCoreDbgTransportFactory
{
    public int StartCount { get; private set; }

    public ValueTask<IOwnedDapMessageTransport> StartAsync(
        NetCoreDbgInstallation installation,
        CancellationToken cancellationToken)
    {
        StartCount++;
        throw new InvalidOperationException("The counting transport must never start.");
    }
}

internal sealed class SmokeFixture : IDisposable
{
    private readonly string _root;

    private SmokeFixture(string root)
    {
        _root = root;
        InstallRoot = Path.Combine(root, "install");
        WorkspaceRoot = Path.Combine(root, "workspace");
        var adapterDirectory = Path.Combine(InstallRoot, NetCoreDbgProvisioning.RelativeDirectory);
        Directory.CreateDirectory(adapterDirectory);
        Directory.CreateDirectory(WorkspaceRoot);

        foreach (var source in Directory.EnumerateFiles(AppContext.BaseDirectory))
        {
            File.Copy(source, Path.Combine(adapterDirectory, Path.GetFileName(source)), overwrite: true);
        }

        var smokeExecutable = Path.Combine(AppContext.BaseDirectory, "HermesDotNetDebugger.Smoke.exe");
        if (!File.Exists(smokeExecutable)) throw new FileNotFoundException("The smoke apphost is missing.", smokeExecutable);
        AdapterPath = Path.Combine(InstallRoot, NetCoreDbgProvisioning.RelativeExecutablePath);
        File.Copy(smokeExecutable, AdapterPath, overwrite: true);

        ProgramPath = CreateFile("Program.dll");
        ExitProgramPath = CreateFile("Exit.dll");
        MalformedProgramPath = CreateFile("Malformed.dll");
        CancelProgramPath = CreateFile("Cancel.dll");
        SourcePath = CreateFile("Program.cs", "internal static class Program { static void Main() { } }");
        OutsideProgramPath = Path.Combine(root, "Outside.dll");
        File.WriteAllBytes(OutsideProgramPath, [0]);
        WriteReceipt();
    }

    public string InstallRoot { get; }
    public string WorkspaceRoot { get; }
    public string AdapterPath { get; }
    public string ProgramPath { get; }
    public string ExitProgramPath { get; }
    public string MalformedProgramPath { get; }
    public string CancelProgramPath { get; }
    public string SourcePath { get; }
    public string OutsideProgramPath { get; }

    public static SmokeFixture Create()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hermes-dotnet-debugger-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return new SmokeFixture(root);
    }

    public void WriteReceipt(string? archiveHash = null)
    {
        var executableHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(AdapterPath))).ToLowerInvariant();
        var receipt = new NetCoreDbgProvisioningReceipt(
            NetCoreDbgProvisioning.ReceiptSchemaVersion,
            NetCoreDbgProvisioning.Component,
            NetCoreDbgProvisioning.Version,
            NetCoreDbgProvisioning.SourceCommit,
            NetCoreDbgProvisioning.ArchiveName,
            archiveHash ?? NetCoreDbgProvisioning.ArchiveSha256,
            executableHash);
        var path = Path.Combine(InstallRoot, NetCoreDbgProvisioning.RelativeReceiptPath);
        File.WriteAllText(path, JsonSerializer.Serialize(receipt));
    }

    public void Dispose()
    {
        if (!_root.StartsWith(Path.Combine(Path.GetTempPath(), "hermes-dotnet-debugger-smoke-"), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to remove a non-smoke directory.");
        }

        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string CreateFile(string name, string? content = null)
    {
        var path = Path.Combine(WorkspaceRoot, name);
        if (content is null) File.WriteAllBytes(path, [0]);
        else File.WriteAllText(path, content);
        return path;
    }
}
