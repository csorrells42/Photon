#if HERMES_LANGUAGE_TOOLING_TESTS
using System.Text.Json;
using HermesDeveloperServices;
using HermesDeveloperServices.LanguageTooling;

internal static class Program
{
    public static async Task<int> Main()
    {
        CatalogMatchesRenderer();
        await DefaultRegistryFailsClosedAsync();
        await CorruptReceiptInspectionFailsClosedAsync();
        await KnownPinnedProvidersMapWithoutPathDisclosureAsync();
        await DotnetSdkEvidenceMapsCompilerAndTestsAsync();
        RegistrationRejectsAmbiguityAndPathBasedDotnet();
        await StructuredBridgeGatesAndNormalizesAsync();
        await ConcreteOperationHandlersSmoke.RunAsync();
        await ContainerGccAuthoritySmoke.RunAsync();
        Console.WriteLine("PASS language-tooling trusted registry smoke");
        return 0;
    }

    private static void CatalogMatchesRenderer()
    {
        Require(LanguageToolingCatalog.Definitions.Select(item => item.Id).SequenceEqual(
            ["dotnet", "java-jdt", "arduino", "python", "gcc", "raspberry-pi"]),
            "The host provider order/IDs diverged from the renderer catalog.");
        Require(LanguageToolingCatalog.Definitions.SelectMany(item => item.Capabilities).Select(item => item.Id).SequenceEqual(
            [
                "dotnet.roslyn-lsp", "dotnet.compiler", "dotnet.tests", "dotnet.dap",
                "java-jdt.lsp", "arduino.project", "arduino.compiler",
                "python.project", "python.lsp", "python.compiler", "python.tests", "gcc.compiler",
                "raspberry-pi.inspect", "raspberry-pi.deploy",
            ]), "The host capability IDs diverged from the renderer catalog.");

        var banned = new[] { "Executable", "Command", "Arguments", "Environment", "Download", "Url", "Host", "Port", "Credential" };
        foreach (var type in typeof(LanguageToolingHostRequest).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && type.IsSubclassOf(typeof(LanguageToolingHostRequest))))
        {
            var properties = type.GetProperties().Select(property => property.Name).ToArray();
            Require(!properties.Any(property => banned.Any(word => property.Contains(word, StringComparison.OrdinalIgnoreCase))),
                $"Structured request {type.Name} exposes process/network authority.");
        }
    }

    private static async Task DefaultRegistryFailsClosedAsync()
    {
        using var workspace = TemporaryDirectory.Create();
        await using var registry = LanguageToolingRegistryFactory.Create(workspace.Path);
        var reports = await registry.DescribeAllAsync();
        Require(reports.Count == 6, "The default registry omitted a provider.");
        Require(reports.SelectMany(item => item.Capabilities)
            .All(item => item.Availability == LanguageToolingCapabilityState.Unavailable),
            "A static catalog declaration became an availability claim.");
        var jdt = reports.Single(item => item.ProviderId == "java-jdt").Capabilities.Single();
        Require(jdt.Code == "java-jdt-runtime-not-provisioned", "JDT did not report explicit unprovisioned status.");
        Require(jdt.SafeMessage.Contains("legal receipt", StringComparison.Ordinal), "JDT omitted the legal/provenance gate.");
        Require(reports.All(item => item.Contract == LanguageToolingProtocol.Contract && item.Source == "trusted-host"),
            "Evidence envelopes were not host-authored.");
    }

    private static async Task KnownPinnedProvidersMapWithoutPathDisclosureAsync()
    {
        using var workspace = TemporaryDirectory.Create();
        var provider = FakeToolchainProvider.Available("roslyn-lsp", supportsLsp: true);
        await using var registry = LanguageToolingRegistryFactory.Create(
            workspace.Path,
            [KnownPinnedToolchainEvidenceSource.Create(provider)]);
        var report = await registry.DescribeProviderAsync("dotnet");
        Require(report.Capabilities.Single(item => item.CapabilityId == "dotnet.roslyn-lsp").Availability
            == LanguageToolingCapabilityState.Available, "Verified Roslyn evidence was not mapped.");
        Require(report.Capabilities.Single(item => item.CapabilityId == "dotnet.compiler").Availability
            == LanguageToolingCapabilityState.Unavailable, "Roslyn evidence bled into the compiler capability.");
        var serialized = JsonSerializer.Serialize(report);
        Require(!serialized.Contains(provider.ExecutablePath, StringComparison.OrdinalIgnoreCase),
            "A trusted executable path crossed the evidence boundary.");
    }

    private static async Task CorruptReceiptInspectionFailsClosedAsync()
    {
        using var workspace = TemporaryDirectory.Create();
        await using var registry = LanguageToolingRegistryFactory.Create(
            workspace.Path,
            [new CorruptJavaReceiptEvidenceSource()]);

        var reports = await registry.DescribeAllAsync();
        var capability = reports.Single(item => item.ProviderId == "java-jdt").Capabilities.Single();
        Require(capability.Availability == LanguageToolingCapabilityState.Error,
            "Corrupt Java receipt evidence did not fail closed.");
        Require(capability.Code == "trusted-inspection-failed",
            "Corrupt Java receipt evidence leaked its internal validation code.");
    }

    private static async Task DotnetSdkEvidenceMapsCompilerAndTestsAsync()
    {
        using var workspace = TemporaryDirectory.Create();
        var provider = new DotnetToolchainProvider();
        await using var registry = LanguageToolingRegistryFactory.Create(
            workspace.Path,
            [new DotnetSdkEvidenceSource(provider)]);
        var report = await registry.DescribeProviderAsync("dotnet");
        Require(report.Capabilities.Single(item => item.CapabilityId == "dotnet.compiler").Availability
            == LanguageToolingCapabilityState.Available, "The host .NET build operation was not advertised.");
        Require(report.Capabilities.Single(item => item.CapabilityId == "dotnet.tests").Availability
            == LanguageToolingCapabilityState.Available, "The host .NET test operation was not advertised.");
        var serialized = JsonSerializer.Serialize(report);
        Require(!serialized.Contains("dotnet.exe", StringComparison.OrdinalIgnoreCase),
            "The host .NET executable path crossed the evidence boundary.");
        await provider.DisposeAsync();
    }

    private static void RegistrationRejectsAmbiguityAndPathBasedDotnet()
    {
        var rejected = false;
        try { _ = KnownPinnedToolchainEvidenceSource.Create(new DotnetToolchainProvider()); }
        catch (ArgumentException) { rejected = true; }
        Require(rejected, "The PATH-discovered dotnet provider was accepted as pinned evidence.");

        using var workspace = TemporaryDirectory.Create();
        var registry = new LanguageToolingTrustedRegistry(workspace.Path);
        var first = new UnavailableLanguageToolingEvidenceSource(
            "java-jdt", ["java-jdt.lsp"], "missing", "Missing.");
        registry.RegisterEvidenceSource(first);
        rejected = false;
        try
        {
            registry.RegisterEvidenceSource(new UnavailableLanguageToolingEvidenceSource(
                "java-jdt", ["java-jdt.lsp"], "duplicate", "Duplicate."));
        }
        catch (InvalidOperationException) { rejected = true; }
        Require(rejected, "Duplicate evidence authority was accepted.");
        registry.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static async Task StructuredBridgeGatesAndNormalizesAsync()
    {
        using var workspace = TemporaryDirectory.Create();
        var gcc = FakeToolchainProvider.Available("gcc", supportsBuild: true);
        await using var registry = LanguageToolingRegistryFactory.Create(
            workspace.Path,
            [KnownPinnedToolchainEvidenceSource.Create(gcc)],
            [new FakeCompileHandler()]);
        var bridge = new LanguageToolingHostBridge(registry);

        var inspect = await bridge.HandleAsync(new InspectLanguageToolingProviderRequest(
            1, "inspect:1", "workspace:1", "gcc"));
        Require(inspect.Succeeded && inspect.Evidence is not null, "Provider inspection failed.");

        var traversal = await bridge.HandleAsync(new CompileLanguageToolingRequest(
            1, "compile:bad", "workspace:1", "gcc", "../outside.c", "debug"));
        Require(!traversal.Succeeded && traversal.Code == "invalid-path", "Workspace traversal was accepted.");

        var remote = await bridge.HandleAsync(new DeployLanguageToolingFileRequest(
            1, "deploy:bad", "workspace:1", "raspberry-pi", "shop-pi", "build/app", "/tmp/raw"));
        Require(!remote.Succeeded && remote.Code == "invalid-identifier", "A raw remote destination crossed the opaque-ID seam.");

        var compile = await bridge.HandleAsync(new CompileLanguageToolingRequest(
            1, "compile:1", "workspace:1", "gcc", "native/main.c", "release"));
        Require(compile.Succeeded && compile.Result is not null, "Verified structured GCC operation did not run.");
        var compileResult = compile.Result!;
        Require(compileResult.SafeMessage == "compiled  safely", "Operation display text was not sanitized.");
        Require(compileResult.ArtifactPaths!.SequenceEqual(["build/app.exe"]), "Artifact paths were not normalized/deduplicated.");
        Require(compileResult.Diagnostics!.Single().Severity == "warning", "Diagnostic severity was not normalized.");

        await using var noHandler = LanguageToolingRegistryFactory.Create(
            workspace.Path,
            [KnownPinnedToolchainEvidenceSource.Create(FakeToolchainProvider.Available("gcc", supportsBuild: true))]);
        var missing = await new LanguageToolingHostBridge(noHandler).HandleAsync(new CompileLanguageToolingRequest(
            1, "compile:2", "workspace:1", "gcc", "native/main.c", "check"));
        Require(!missing.Succeeded && missing.Code == "operation-not-wired", "An unregistered operation did not fail closed.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FakeCompileHandler : ILanguageToolingOperationHandler
    {
        public string ProviderId => "gcc";
        public IReadOnlyCollection<string> Operations { get; } = ["compile"];

        public ValueTask<LanguageToolingOperationResult> ExecuteAsync(
            LanguageToolingHostRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Require(request is CompileLanguageToolingRequest, "The handler received an untyped request.");
            return ValueTask.FromResult(new LanguageToolingOperationResult(
                true,
                "ok<script>",
                "compiled\r\nsafely",
                [new("native/main.c", "WARNING", "W1", "warning\r\ntext", 2, 3, 2, 5)],
                ["build/app.exe", "build\\app.exe"]));
        }
    }

    private sealed class CorruptJavaReceiptEvidenceSource : ILanguageToolingEvidenceSource
    {
        public string ProviderId => "java-jdt";
        public IReadOnlyCollection<string> CapabilityIds { get; } = ["java-jdt.lsp"];

        public ValueTask<IReadOnlyList<LanguageToolingCapabilityStatus>> InspectAsync(
            string workspaceRoot,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidDataException("java-jdt-payload-set-mismatch");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeToolchainProvider : IToolchainProvider
    {
        private readonly ToolchainExecutableDiscoveryResult _discovery;

        private FakeToolchainProvider(ToolchainProviderDescriptor descriptor)
        {
            Descriptor = descriptor;
            ExecutablePath = Environment.ProcessPath ?? throw new InvalidOperationException("The smoke process path is unavailable.");
            Availability = new(ToolchainAvailabilityState.Available, "available", "Available.", DateTimeOffset.UtcNow);
            _discovery = new(Availability, [new("fake", ExecutablePath, "1.2.3", Availability)]);
        }

        public string ExecutablePath { get; }
        public ToolchainProviderDescriptor Descriptor { get; }
        public ToolchainLifecycleState LifecycleState => ToolchainLifecycleState.Ready;
        public ToolchainAvailability Availability { get; }

        public static FakeToolchainProvider Available(
            string providerId,
            bool supportsBuild = false,
            bool supportsLsp = false) => new(new(
                DeveloperServicesProtocol.ToolchainProviderVersion,
                providerId,
                "Fake pinned provider",
                "1.2.3",
                ["csharp"],
                ["workspace"],
                new(supportsBuild, supportsBuild, true, supportsBuild ? ["workspace"] : []),
                new(supportsLsp, supportsLsp, supportsLsp, supportsLsp, supportsLsp, supportsLsp, supportsLsp),
                new(false, false, false, false, false),
                [ToolchainExecutionKind.LocalSidecarProcess]));

        public ValueTask<ToolchainExecutableDiscoveryResult> DiscoverExecutablesAsync(
            ToolchainDiscoveryContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_discovery);
        }

        public ValueTask StartAsync(ToolchainStartContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;
        public string Path { get; }
        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hermes-language-tooling-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new(path);
        }
        public void Dispose()
        {
            if (!Directory.Exists(Path)) return;
            Directory.Delete(Path, true);
        }
    }
}
#endif
