#if HERMES_JAVA_JDT_SMOKE
using HermesDeveloperServices;
using HermesDeveloperServices.LanguageTooling;
using HermesDeveloperServices.LanguageTooling.Java;

internal static class Program
{
    public static async Task<int> Main()
    {
        Console.WriteLine("RUN unprovisioned fail-closed");
        await UnprovisionedProviderFailsClosedAsync();
        Console.WriteLine("RUN typed session freshness");
        await TypedSessionRejectsStaleStateAsync();
        Console.WriteLine("RUN structured lifecycle");
        await StructuredProviderLifecycleAsync();
        var provisionedRoot = Environment.GetEnvironmentVariable("HERMES_JAVA_JDT_INSTALL_ROOT");
        if (!string.IsNullOrWhiteSpace(provisionedRoot))
        {
            Console.WriteLine("RUN receipt-bound real JDT lifecycle");
            await ReceiptBoundProviderLifecycleAsync(provisionedRoot);
            Console.WriteLine("PASS Java/JDT focused smoke (4/4 including real JDT)");
        }
        else
        {
            Console.WriteLine("PASS Java/JDT focused smoke (3/3; real JDT not requested)");
        }
        return 0;
    }

    private static async Task UnprovisionedProviderFailsClosedAsync()
    {
        await using var fixture = new WorkspaceFixture();
        await using var provider = JavaJdtLanguageToolingProvider.CreateUnprovisioned(fixture.Root);
        var evidence = await provider.InspectAsync(fixture.Root, CancellationToken.None);
        Equal(1, evidence.Count, "unprovisioned evidence count");
        Equal(LanguageToolingCapabilityState.Unavailable, evidence[0].Availability, "unprovisioned availability");
        Equal("java-jdt-provenance-not-pinned", evidence[0].Code, "unprovisioned code");
        Require(JavaJdtProvisioning.CurrentBlocker.MissingAuthorities.Count == 4, "provisioning blocker is incomplete");
    }

    private static async Task TypedSessionRejectsStaleStateAsync()
    {
        var transport = new FakeJavaJdtTransport();
        await using var session = new JavaJdtLanguageSession(transport);
        var root = new Uri(Path.GetFullPath(Path.GetTempPath()) + Path.DirectorySeparatorChar).AbsoluteUri;
        var uri = new Uri(Path.Combine(Path.GetTempPath(), "Main.java")).AbsoluteUri;
        var capabilities = await session.InitializeAsync(root);
        Require(capabilities.SupportsRequiredSurface, "required JDT surface was not negotiated");
        await session.OpenDocumentAsync(uri, 1, "class Main {}\n");
        await session.ChangeDocumentAsync(uri, 2, "class Main { int value; }\n");
        await ThrowsAsync<JavaJdtStaleDocumentException>(() =>
            session.CompletionAsync(uri, 1, new LspPosition(0, 1)));
        await ThrowsAsync<ArgumentException>(() =>
            session.RenameAsync(uri, 2, new LspPosition(0, 1), "not-valid!"));

        var diagnostics = new List<LspPublishDiagnosticsParams>();
        session.DiagnosticsPublished += diagnostics.Add;
        transport.PublishDiagnostics(uri, 1, "stale");
        transport.PublishDiagnostics(uri, 2, "current");
        await Task.Delay(25);
        Equal(1, diagnostics.Count, "stale diagnostics were not rejected");
        Equal("current", diagnostics[0].Diagnostics[0].Message, "current diagnostic mismatch");
        var completion = await session.CompletionAsync(uri, 2, new LspPosition(0, 1));
        Require(completion is not null, "bounded completion result missing");
        await session.CloseDocumentAsync(uri);
    }

    private static async Task StructuredProviderLifecycleAsync()
    {
        await using var fixture = new WorkspaceFixture();
        var authority = new FakeRuntimeAuthority();
        var provider = JavaJdtLanguageToolingProvider.CreateForTrustedAuthority(fixture.Root, authority);
        await using var registry = LanguageToolingRegistryFactory.Create(fixture.Root, [provider], [provider]);
        await ThrowsCodeAsync("invalid-path", () => registry.ExecuteAsync(new StartLanguageToolingSessionRequest(
            LanguageToolingProtocol.Version, "request-escape", "workspace-1", LanguageToolingCatalog.JavaJdt, "../Main.java")).AsTask());
        await ThrowsCodeAsync("java-document-required", () => registry.ExecuteAsync(new StartLanguageToolingSessionRequest(
            LanguageToolingProtocol.Version, "request-text", "workspace-1", LanguageToolingCatalog.JavaJdt, "notes.txt")).AsTask());
        var start = await registry.ExecuteAsync(new StartLanguageToolingSessionRequest(
            LanguageToolingProtocol.Version, "request-1", "workspace-1", LanguageToolingCatalog.JavaJdt, "Main.java"));
        Require(start.Succeeded && start.SessionId?.StartsWith("java:", StringComparison.Ordinal) == true, "typed start failed");
        Equal(1, provider.ActiveSessionCount, "active session count after start");
        await ThrowsCodeAsync("java-session-not-found", () => registry.ExecuteAsync(new StopLanguageToolingSessionRequest(
            LanguageToolingProtocol.Version, "request-unknown", "workspace-1", LanguageToolingCatalog.JavaJdt, "java:00000000000000000000000000000000")).AsTask());
        Equal(1, provider.ActiveSessionCount, "unknown stop removed an owned session");
        var stop = await registry.ExecuteAsync(new StopLanguageToolingSessionRequest(
            LanguageToolingProtocol.Version, "request-2", "workspace-1", LanguageToolingCatalog.JavaJdt, start.SessionId!));
        Require(stop.Succeeded, "typed stop failed");
        Equal(0, provider.ActiveSessionCount, "active session count after stop");
        Equal(1, authority.Starts, "unexpected authority start count");
    }

    private static async Task ReceiptBoundProviderLifecycleAsync(string installRoot)
    {
        await using var fixture = new WorkspaceFixture();
        await using var provider = JavaJdtLanguageToolingProvider.CreateProvisioned(fixture.Root, installRoot);
        var evidence = await provider.InspectAsync(fixture.Root, CancellationToken.None);
        Equal(1, evidence.Count, "receipt-bound evidence count");
        Equal(LanguageToolingCapabilityState.Available, evidence[0].Availability, "receipt-bound availability");
        var start = await provider.ExecuteAsync(new StartLanguageToolingSessionRequest(
            LanguageToolingProtocol.Version,
            "real-jdt-start",
            "workspace-1",
            LanguageToolingCatalog.JavaJdt,
            "Main.java"), CancellationToken.None);
        Require(start.Succeeded && start.SessionId is not null, "receipt-bound JDT start failed");
        var stop = await provider.ExecuteAsync(new StopLanguageToolingSessionRequest(
            LanguageToolingProtocol.Version,
            "real-jdt-stop",
            "workspace-1",
            LanguageToolingCatalog.JavaJdt,
            start.SessionId!), CancellationToken.None);
        Require(stop.Succeeded, "receipt-bound JDT stop failed");
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static async Task ThrowsCodeAsync(string expectedCode, Func<Task> action)
    {
        try { await action(); }
        catch (LanguageToolingRequestException exception)
        {
            Equal(expectedCode, exception.Code, "request rejection code");
            return;
        }
        throw new InvalidOperationException($"Expected LanguageToolingRequestException '{expectedCode}'.");
    }

    private static void Equal<T>(T expected, T actual, string label) where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{label}: expected {expected}, got {actual}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FakeRuntimeAuthority : IJavaJdtRuntimeAuthority
    {
        internal int Starts { get; private set; }

        public ValueTask<JavaJdtRuntimeInspection> InspectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new JavaJdtRuntimeInspection(
                true, "verified-pinned-runtime", "The fake trusted runtime is available.", "0.0.0-fake"));
        }

        public ValueTask<JavaJdtLanguageSession> StartSessionAsync(string workspaceRoot, CancellationToken cancellationToken)
        {
            _ = workspaceRoot;
            cancellationToken.ThrowIfCancellationRequested();
            Starts++;
            return ValueTask.FromResult(new JavaJdtLanguageSession(new FakeJavaJdtTransport()));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class WorkspaceFixture : IAsyncDisposable
    {
        internal WorkspaceFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"HermesJavaJdtSmoke-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "Main.java"), "class Main {}\n");
            File.WriteAllText(Path.Combine(Root, "notes.txt"), "not java\n");
        }

        internal string Root { get; }

        public ValueTask DisposeAsync()
        {
            if (!Directory.Exists(Root)) return ValueTask.CompletedTask;
            var full = Path.GetFullPath(Root);
            var expected = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar + "HermesJavaJdtSmoke-";
            if (!full.StartsWith(expected, StringComparison.OrdinalIgnoreCase)
                || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing to remove an unexpected smoke workspace.");
            Directory.Delete(full, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
#endif
