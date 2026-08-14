using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Security;
using System.Security.Cryptography;
using System.Net;
using System.Net.Http;
using System.Diagnostics;
using HermesDesktop;
using HermesDeveloperServices;
using HermesRoslynLanguageServer;

if (args.Contains("--photon-cad-only", StringComparer.Ordinal))
    return await PhotonCadBridgeSmoke.RunAsync() ? 0 : 95;

if (args.Contains("--photon-cad-live-industrial", StringComparer.Ordinal))
    return await PhotonCadBridgeSmoke.RunLiveIndustrialAsync(Directory.GetCurrentDirectory()) ? 0 : 96;

const string marker = "__HERMES_CONPTY_OK__";

await WorkspaceSearchBridgeSmoke.RunAsync();

var trustedWorkbench = new Uri("http://127.0.0.1:4173/");
if (!DesktopOptions.IsSameWorkbenchOrigin(trustedWorkbench, new Uri("http://127.0.0.1:4173/editor?file=test"))
    || DesktopOptions.IsSameWorkbenchOrigin(trustedWorkbench, new Uri("http://127.0.0.1:8872/"))
    || DesktopOptions.IsSameWorkbenchOrigin(trustedWorkbench, new Uri("https://127.0.0.1:4173/"))
    || DesktopOptions.IsSameWorkbenchOrigin(trustedWorkbench, new Uri("http://localhost:4173/"))
    || DesktopOptions.IsSupportedWorkbenchOrigin(new Uri("http://127.0.0.1:8872/"))
    || DesktopOptions.IsTrustedWorkbenchUri(new Uri("http://user:password@127.0.0.1:4173/")))
{
    Console.Error.WriteLine("Desktop Workbench origin pinning validation failed.");
    return 9;
}
Console.WriteLine("Desktop Workbench navigation and native-message origin pinning passed.");
if (DesktopOptions.CanAuthorizeWorkbenchNavigation(
        41, 42, trustedWorkbench, trustedWorkbench, trustedWorkbench)
    || !DesktopOptions.CanAuthorizeWorkbenchNavigation(
        42, 42, trustedWorkbench, trustedWorkbench, new Uri("http://127.0.0.1:4173/editor")))
{
    Console.Error.WriteLine("Desktop stale-navigation authorization validation failed.");
    return 7;
}
Console.WriteLine("Desktop stale-navigation authorization guard passed.");
var identityNonce = new string('1', 64);
var identityChallenge = new string('2', 64);
var identityProof = DesktopOptions.CreateWorkbenchIdentityProof(identityNonce, identityChallenge);
if (!DesktopOptions.IsValidWorkbenchNonce(identityProof)
    || identityProof == DesktopOptions.CreateWorkbenchIdentityProof(new string('3', 64), identityChallenge)
    || identityProof == DesktopOptions.CreateWorkbenchIdentityProof(identityNonce, new string('4', 64)))
{
    Console.Error.WriteLine("Desktop Workbench challenge-response identity validation failed.");
    return 8;
}
Console.WriteLine("Desktop Workbench challenge-response identity binding passed.");

var protectedDocumentPaths = new[]
{
    "AUTH.JSON", "nested/Credentials.Json", "keys/client.KEY", "keys/client.PEM",
    "keys/client.PFX", "keys/client.P12", ".NPMRC", ".PyPiRc", "NuGet.Config",
};
if (protectedDocumentPaths.Any(path => !DocumentBridge.IsBlockedDocumentPath(path))
    || DocumentBridge.IsBlockedDocumentPath("src/authentication/TokenModel.cs"))
{
    Console.Error.WriteLine("Desktop document secret-file policy validation failed.");
    return 90;
}

var junctionTestRoot = Path.Combine(Path.GetTempPath(), $"photon-document-smoke-{Guid.NewGuid():N}");
var junctionTarget = Path.Combine(Path.GetTempPath(), $"photon-document-target-{Guid.NewGuid():N}");
Directory.CreateDirectory(Path.Combine(junctionTestRoot, "nested"));
Directory.CreateDirectory(junctionTarget);
var junctionPath = Path.Combine(junctionTestRoot, "nested", "linked");
try
{
    using var junctionProcess = Process.Start(new ProcessStartInfo
    {
        FileName = "cmd.exe",
        Arguments = $"/d /c mklink /J \"{junctionPath}\" \"{junctionTarget}\"",
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    });
    junctionProcess?.WaitForExit();
    if (junctionProcess is null || junctionProcess.ExitCode != 0)
    {
        Console.Error.WriteLine("Desktop document junction fixture creation failed.");
        return 91;
    }
    var documentBridge = new DocumentBridge(junctionTestRoot, _ => { });
    if (!documentBridge.TraversesReparsePoint(junctionPath)
        || !documentBridge.TraversesReparsePoint(Path.Combine(junctionPath, "outside.txt")))
    {
        Console.Error.WriteLine("Desktop document nested-junction rejection failed.");
        return 92;
    }
}
finally
{
    if (Directory.Exists(junctionPath)) Directory.Delete(junctionPath);
    if (Directory.Exists(junctionTestRoot)) Directory.Delete(junctionTestRoot, recursive: true);
    if (Directory.Exists(junctionTarget)) Directory.Delete(junctionTarget, recursive: true);
}
Console.WriteLine("Desktop document secret-file and nested-junction guards passed.");

var saveTestRoot = Path.Combine(Path.GetTempPath(), $"photon-document-save-{Guid.NewGuid():N}");
Directory.CreateDirectory(saveTestRoot);
var saveTestPath = Path.Combine(saveTestRoot, "document.txt");
await File.WriteAllTextAsync(saveTestPath, "one");
var saveMessages = new List<string>();
var saveBridge = new DocumentBridge(saveTestRoot, message => saveMessages.Add(JsonSerializer.Serialize(message)));
try
{
    var firstDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("one"))).ToLowerInvariant();
    await saveBridge.SaveAsync(1, "save:one", "document.txt", "two", firstDigest, saveAs: false);
    var savedDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("two"))).ToLowerInvariant();
    if (await File.ReadAllTextAsync(saveTestPath) != "two" || !saveMessages.Last().Contains(savedDigest, StringComparison.Ordinal))
    {
        Console.Error.WriteLine("Desktop document identity-bound save failed.");
        return 932;
    }
    await File.WriteAllTextAsync(saveTestPath, "external-change");
    await saveBridge.SaveAsync(1, "save:stale", "document.txt", "three", savedDigest, saveAs: false);
    if (await File.ReadAllTextAsync(saveTestPath) != "external-change" || !saveMessages.Last().Contains("external_change", StringComparison.Ordinal))
    {
        Console.Error.WriteLine("Desktop document external-change rejection failed.");
        return 933;
    }
}
finally
{
    Directory.Delete(saveTestRoot, recursive: true);
}
Console.WriteLine("Desktop document identity-bound save and conflict rejection passed.");

var callbackUri = new Uri("https://user:password@example.test/oauth/callback/provider-token?code=secret-code#access_token=secret-fragment");
var displayUrl = BrowserSurfaceBridge.RendererSafeDisplayUrl(callbackUri);
var displayTitle = BrowserSurfaceBridge.RendererSafeTitle(callbackUri);
if (displayUrl != "https://example.test/"
    || displayTitle != "example.test"
    || displayUrl.Contains("secret", StringComparison.OrdinalIgnoreCase)
    || displayTitle.Contains("secret", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Desktop browser renderer-safe callback projection failed.");
    return 93;
}
Console.WriteLine("Desktop browser callback URL and title projection passed.");

var exactMapsUri = new Uri("https://www.google.com/maps/dir/?api=1&origin=Plant+A&destination=Plant+B&travelmode=driving");
if (BrowserSurfaceBridge.RendererSafeTitle(exactMapsUri) != "Google Maps"
    || BrowserSurfaceBridge.RendererSafeDisplayUrl(exactMapsUri) != "https://www.google.com/"
    || BrowserSurfaceBridge.RendererSafeDisplayUrl(exactMapsUri).Contains("Plant", StringComparison.Ordinal)
    || BrowserSurfaceBridge.TryNormalizeAddress("https://user:password@example.test/", out _)
    || BrowserSurfaceBridge.TryNormalizeAddress($"https://example.test/{new string('x', 4_096)}", out _)
    || !BrowserSurfaceBridge.TryNormalizeAddress(exactMapsUri.AbsoluteUri, out var acceptedMapsUri)
    || acceptedMapsUri.AbsoluteUri != exactMapsUri.AbsoluteUri)
{
    Console.Error.WriteLine("Desktop browser address validation or Google Maps projection failed.");
    return 939;
}
Console.WriteLine("Desktop browser strict address validation and Google Maps projection passed.");

var browserTabState = new BrowserTabState(new Uri("https://www.google.com/maps/dir/?api=1&destination=Drug+Store"));
browserTabState.Capture(new Uri("https://www.google.com/maps/dir/route-a?api=1"));
browserTabState.Capture(new Uri("https://www.google.com/maps/dir/route-b?api=1"));
if (!browserTabState.CanGoBack
    || browserTabState.CanGoForward
    || !browserTabState.TryBack(out var priorRoute)
    || priorRoute.AbsolutePath != "/maps/dir/route-a"
    || !browserTabState.TryForward(out var restoredRoute)
    || restoredRoute.AbsolutePath != "/maps/dir/route-b")
{
    Console.Error.WriteLine("Desktop browser per-tab route history failed.");
    return 931;
}
Console.WriteLine("Desktop browser per-tab route history passed.");

var raceTabs = new Dictionary<string, BrowserTabState>(StringComparer.Ordinal)
{
    ["tab-a"] = new(new Uri("https://maps.example/a/start?route=1")),
    ["tab-b"] = new(new Uri("https://maps.example/b/start?route=2")),
};
BrowserSurfaceBridge.ApplyNavigationCompletion(
    raceTabs,
    new BrowserNavigationContext("tab-b", new Uri("https://maps.example/b/complete?route=2"), ReplaceCurrent: true),
    succeeded: true);
BrowserSurfaceBridge.ApplyNavigationCompletion(
    raceTabs,
    new BrowserNavigationContext("tab-a", new Uri("https://maps.example/a/late?route=1"), ReplaceCurrent: true),
    succeeded: true);
if (raceTabs["tab-a"].CurrentUri.AbsolutePath != "/a/late"
    || raceTabs["tab-b"].CurrentUri.AbsolutePath != "/b/complete")
{
    Console.Error.WriteLine("Desktop browser late navigation crossed tab histories.");
    return 934;
}
Console.WriteLine("Desktop browser navigation-id tab binding passed.");

var placedRoute = new Uri("https://maps.example/route?destination=store");
var pendingPlacement = new BrowserNavigationContext("tab-map", placedRoute, ReplaceCurrent: true);
if (!BrowserSurfaceBridge.IsEquivalentNavigationPendingOrDisplayed("tab-map", placedRoute, pendingPlacement, [], [], null, null)
    || !BrowserSurfaceBridge.IsEquivalentNavigationPendingOrDisplayed("tab-map", placedRoute, null, [pendingPlacement], [], null, null)
    || !BrowserSurfaceBridge.IsEquivalentNavigationPendingOrDisplayed("tab-map", placedRoute, null, [], [], "tab-map", placedRoute)
    || BrowserSurfaceBridge.IsEquivalentNavigationPendingOrDisplayed("tab-other", placedRoute, null, [], [], "tab-map", placedRoute)
    || BrowserSurfaceBridge.IsEquivalentNavigationPendingOrDisplayed("tab-other", placedRoute, pendingPlacement, [], [], null, null))
{
    Console.Error.WriteLine("Desktop browser placement-only navigation deduplication failed.");
    return 935;
}
for (var resize = 0; resize < 10; resize++)
{
    if (!BrowserSurfaceBridge.IsEquivalentNavigationPendingOrDisplayed("tab-map", placedRoute, null, [], [], "tab-map", placedRoute))
    {
        Console.Error.WriteLine("Desktop browser repeated placement attempted to navigate.");
        return 940;
    }
}
Console.WriteLine("Desktop browser placement-only navigation deduplication passed.");

Task<bool>? browserInitialization = null;
var browserInitializationCalls = 0;
Task<bool> InitializeBrowserOnce()
{
    browserInitializationCalls++;
    return Task.FromResult(true);
}
var browserInitializationA = BrowserSurfaceBridge.GetOrCreateInitializationTask(ref browserInitialization, InitializeBrowserOnce);
var browserInitializationB = BrowserSurfaceBridge.GetOrCreateInitializationTask(ref browserInitialization, InitializeBrowserOnce);
if (!ReferenceEquals(browserInitializationA, browserInitializationB)
    || !await browserInitializationA
    || browserInitializationCalls != 1)
{
    Console.Error.WriteLine("Desktop browser initialization was not single-flight.");
    return 936;
}
Console.WriteLine("Desktop browser single-flight initialization passed.");

var browserSurfaceRequests = new BrowserSurfaceRequestGate();
var deferredShowRequest = browserSurfaceRequests.Begin();
var deferredBrowserInitialization = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var deferredShow = Task.Run(async () =>
{
    await deferredBrowserInitialization.Task;
    return browserSurfaceRequests.IsCurrent(deferredShowRequest);
});
browserSurfaceRequests.Invalidate();
deferredBrowserInitialization.SetResult();
if (await deferredShow)
{
    Console.Error.WriteLine("Desktop browser stale show survived a newer hide request.");
    return 937;
}
var currentShowRequest = browserSurfaceRequests.Begin();
if (!browserSurfaceRequests.IsCurrent(currentShowRequest))
{
    Console.Error.WriteLine("Desktop browser current show request was rejected.");
    return 938;
}
var showA = browserSurfaceRequests.Begin();
browserSurfaceRequests.Invalidate();
var showB = browserSurfaceRequests.Begin();
if (browserSurfaceRequests.IsCurrent(showA) || !browserSurfaceRequests.IsCurrent(showB))
{
    Console.Error.WriteLine("Desktop browser out-of-order show requests were not generation-bound.");
    return 941;
}
browserSurfaceRequests.Invalidate();
if (browserSurfaceRequests.IsCurrent(showB))
{
    Console.Error.WriteLine("Desktop browser disposal/reset invalidation retained a stale show.");
    return 942;
}
Console.WriteLine("Desktop browser deferred show/hide ordering passed.");

var chromiumBookmarks = ChromiumBookmarksBridge.ParseSnapshot(Encoding.UTF8.GetBytes("""
{
  "roots": {
    "bookmark_bar": {
      "type": "folder",
      "name": "Bookmarks bar",
      "children": [
        { "type": "url", "name": "Existing", "url": "https://example.test/" },
        { "type": "folder", "name": "Work", "children": [
          { "type": "url", "name": "Reference", "url": "https://docs.example.test/reference" }
        ] },
        { "type": "url", "name": "Duplicate", "url": "https://example.test" },
        { "type": "url", "name": "Script", "url": "javascript:alert(1)" },
        { "type": "url", "name": "Credential", "url": "https://user:password@example.test/" }
      ]
    },
    "other": {
      "type": "folder",
      "name": "Other bookmarks",
      "children": [
        { "type": "url", "name": "Bounded out", "url": "https://third.example.test/" }
      ]
    },
    "synced": { "type": "folder", "name": "Mobile bookmarks", "children": [] }
  }
}
"""), maximumBookmarks: 2);
if (chromiumBookmarks.Bookmarks.Count != 2
    || chromiumBookmarks.DiscoveredCount != 6
    || chromiumBookmarks.RejectedCount != 3
    || !chromiumBookmarks.Truncated
    || chromiumBookmarks.Bookmarks[0] != new ChromiumBookmark("Existing", "https://example.test/", "Bookmarks bar")
    || chromiumBookmarks.Bookmarks[1] != new ChromiumBookmark("Reference", "https://docs.example.test/reference", "Bookmarks bar / Work")
    || chromiumBookmarks.Bookmarks.Any(bookmark => bookmark.Url.Contains("user:", StringComparison.Ordinal)
        || bookmark.Url.Contains("javascript", StringComparison.OrdinalIgnoreCase)
        || bookmark.Folder.Contains(@"C:\", StringComparison.OrdinalIgnoreCase)))
{
    Console.Error.WriteLine("Chrome bookmark projection, deduplication, or bounds failed.");
    return 943;
}
Console.WriteLine("Chrome bookmark projection, deduplication, and bounds passed.");

if (args.Contains("--document-browser-only", StringComparer.Ordinal)) return 0;

if (!await DockerControlBridgeSmoke.RunAsync()) return 94;

var authUri = NativeAuthProtocol.BuildLoginUri(new Uri("http://127.0.0.1:4173/"), "local_password");
if (authUri.AbsolutePath != "/auth/login"
    || !authUri.Query.Contains("provider=local_password", StringComparison.Ordinal)
    || !authUri.Query.Contains("next=%2Fworkbench-auth-complete", StringComparison.OrdinalIgnoreCase)
    || NativeAuthProtocol.IsValidProvider("../../unsafe"))
{
    Console.Error.WriteLine("Native authentication URI validation failed.");
    return 10;
}

var credentialTarget = NativeCredentialProtocol.BuildTargetName("openrouter", "primary");
if (!NativeCredentialProtocol.TryParseTargetName(credentialTarget, out var credentialProvider, out var credentialId)
    || credentialProvider != "openrouter"
    || credentialId != "primary"
    || NativeCredentialProtocol.IsValidProvider("../../unsafe")
    || NativeCredentialProtocol.IsValidCredentialId("secret profile"))
{
    Console.Error.WriteLine("Native credential protocol validation failed.");
    return 11;
}

if (!args.Contains("--developer-services-only", StringComparer.Ordinal))
{
    var vault = new WindowsCredentialVault();
    var smokeCredentialIds = new[]
    {
    $"roundtrip-ali-{Guid.NewGuid():N}",
    $"roundtrip-scarlett-{Guid.NewGuid():N}",
};
    var smokeSecrets = smokeCredentialIds.ToDictionary(
        credentialId => credentialId,
        _ => $"hermes-smoke-{Guid.NewGuid():N}",
        StringComparer.Ordinal);
    var savedSmokeCredentials = new HashSet<string>(StringComparer.Ordinal);
    try
    {
        foreach (var smokeCredentialId in smokeCredentialIds)
        {
            using var secureSecret = new SecureString();
            foreach (var character in smokeSecrets[smokeCredentialId]) secureSecret.AppendChar(character);
            secureSecret.MakeReadOnly();

            var saved = vault.Save("smoke-test", smokeCredentialId, secureSecret);
            savedSmokeCredentials.Add(smokeCredentialId);
            if (saved.Provider != "smoke-test" || saved.CredentialId != smokeCredentialId)
            {
                Console.Error.WriteLine("Windows Credential Manager returned unexpected credential metadata.");
                return 12;
            }
        }

        var listedIds = vault.List()
            .Where(item => item.Provider == "smoke-test" && smokeCredentialIds.Contains(item.CredentialId, StringComparer.Ordinal))
            .Select(item => item.CredentialId)
            .ToHashSet(StringComparer.Ordinal);
        if (!smokeCredentialIds.All(listedIds.Contains))
        {
            Console.Error.WriteLine("Windows Credential Manager did not list both synthetic named credentials.");
            return 13;
        }

        if (smokeCredentialIds.Any(credentialId => vault.ReadSecret("smoke-test", credentialId) != smokeSecrets[credentialId]))
        {
            Console.Error.WriteLine("Windows Credential Manager did not round-trip both synthetic named credentials.");
            return 14;
        }
    }
    finally
    {
        foreach (var smokeCredentialId in savedSmokeCredentials)
        {
            if (!vault.Delete("smoke-test", smokeCredentialId))
                throw new InvalidOperationException($"Windows Credential Manager did not delete synthetic credential {smokeCredentialId}.");
        }
    }
    if (smokeCredentialIds.Any(credentialId => vault.ReadSecret("smoke-test", credentialId) is not null))
    {
        Console.Error.WriteLine("Windows Credential Manager retained a deleted synthetic named credential.");
        return 15;
    }
    Console.WriteLine("Windows Credential Manager two-profile synthetic save/list/read/delete round trip passed.");
}

const string openRouterSmokeKey = "sk-or-v1-native-only-smoke";
var openRouterHandler = new OpenRouterSmokeHandler(HttpStatusCode.OK, """
    {
      "data": {
        "usage": 25.5,
        "usage_daily": 1.25,
        "usage_weekly": 4.5,
        "usage_monthly": 12.75,
        "limit": 100,
        "limit_remaining": 74.5,
        "limit_reset": "monthly",
        "is_free_tier": false,
        "label": "sk-or-v1-must-not-leave-host",
        "creator_user_id": "user_must_not_leave_host"
      }
    }
    """);
using (var openRouterCollector = new OpenRouterUsageCollector(openRouterHandler))
{
    var usage = await openRouterCollector.CollectAsync(openRouterSmokeKey);
    if (openRouterHandler.RequestUri != new Uri("https://openrouter.ai/api/v1/key")
        || openRouterHandler.AuthorizationScheme != "Bearer"
        || openRouterHandler.AuthorizationParameter != openRouterSmokeKey
        || usage.Usage != 25.5
        || usage.UsageWeekly != 4.5
        || usage.LimitRemaining != 74.5
        || usage.LimitReset != "monthly")
    {
        Console.Error.WriteLine("Native OpenRouter usage collector contract validation failed.");
        return 16;
    }
    var sanitized = JsonSerializer.Serialize(usage);
    if (sanitized.Contains("sk-or", StringComparison.OrdinalIgnoreCase)
        || sanitized.Contains("creator", StringComparison.OrdinalIgnoreCase)
        || sanitized.Contains("user_", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("Native OpenRouter usage collector leaked excluded identity fields.");
        return 17;
    }
}

using (var unauthorizedCollector = new OpenRouterUsageCollector(new OpenRouterSmokeHandler(HttpStatusCode.Unauthorized, """{"error":{"code":401,"message":"invalid key"}}""")))
{
    try
    {
        await unauthorizedCollector.CollectAsync(openRouterSmokeKey);
        Console.Error.WriteLine("Native OpenRouter collector accepted an unauthorized response.");
        return 18;
    }
    catch (UsageCollectionException exception) when (exception.Code == "permission-denied" && !exception.Retryable) { }
}

using (var redirectCollector = new OpenRouterUsageCollector(new OpenRouterSmokeHandler(HttpStatusCode.Redirect, string.Empty)))
{
    try
    {
        await redirectCollector.CollectAsync(openRouterSmokeKey);
        Console.Error.WriteLine("Native OpenRouter collector followed or accepted a redirect.");
        return 19;
    }
    catch (UsageCollectionException exception) when (exception.Code == "unexpected") { }
}
Console.WriteLine("Native OpenRouter collector request, sanitization, authorization-error, and no-redirect checks passed.");

var developerWorkspace = Path.Combine(Path.GetTempPath(), $"HermesDesktopDeveloperSmoke-{Guid.NewGuid():N}");
Directory.CreateDirectory(developerWorkspace);
try
{
    File.WriteAllText(Path.Combine(developerWorkspace, "Smoke.csproj"), """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
          </PropertyGroup>
        </Project>
        """);
    File.WriteAllText(Path.Combine(developerWorkspace, "Program.cs"), "Console.WriteLine(\"Hermes desktop developer-services smoke\");");
    var assetBundleRoot = Path.Combine(developerWorkspace, "bundle-root");
    var assetBinaryRoot = Path.Combine(developerWorkspace, "binary-root");
    var binaryRoslynRoot = Path.Combine(assetBinaryRoot, "developer-services", "roslyn");
    Directory.CreateDirectory(binaryRoslynRoot);
    File.WriteAllText(Path.Combine(binaryRoslynRoot, "provider.json"), "{}");
    if (!DeveloperServicesBridge.ResolveRoslynInstallerRoot(assetBundleRoot, assetBinaryRoot)
        .Equals(binaryRoslynRoot, StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("Developer-services did not resolve executable-adjacent Roslyn assets for the source-tree launcher layout.");
        return 19;
    }
    var configuredRoslynRoot = Path.Combine(assetBundleRoot, "developer-services", "roslyn");
    Directory.CreateDirectory(configuredRoslynRoot);
    File.WriteAllText(Path.Combine(configuredRoslynRoot, "provider.json"), "{}");
    if (!DeveloperServicesBridge.ResolveRoslynInstallerRoot(assetBundleRoot, assetBinaryRoot)
        .Equals(configuredRoslynRoot, StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("Developer-services did not preserve a provisioned bundle-root Roslyn layout.");
        return 19;
    }
    var binaryArduinoRoot = Path.Combine(assetBinaryRoot, "toolchains", "arduino");
    Directory.CreateDirectory(binaryArduinoRoot);
    File.WriteAllText(Path.Combine(binaryArduinoRoot, "hermes-toolchain-receipt.json"), "{}");
    if (!DeveloperServicesBridge.ResolveArduinoWorkbenchRoot(assetBundleRoot, assetBinaryRoot)
        .Equals(assetBinaryRoot, StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("Developer-services did not resolve executable-adjacent Arduino assets for the source-tree launcher layout.");
        return 19;
    }
    var configuredArduinoRoot = Path.Combine(assetBundleRoot, "toolchains", "arduino");
    Directory.CreateDirectory(configuredArduinoRoot);
    File.WriteAllText(Path.Combine(configuredArduinoRoot, "hermes-toolchain-receipt.json"), "{}");
    if (!DeveloperServicesBridge.ResolveArduinoWorkbenchRoot(assetBundleRoot, assetBinaryRoot)
        .Equals(assetBundleRoot, StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("Developer-services did not preserve a provisioned bundle-root Arduino layout.");
        return 19;
    }

    var debugDiscoveryRoot = Path.Combine(developerWorkspace, "debug-discovery");
    var debugRuntimeRoot = Path.Combine(debugDiscoveryRoot, "src", "Probe", "bin", "Debug", "net10.0");
    var inaccessibleRoot = Path.Combine(debugDiscoveryRoot, "locked-zone");
    var skippedDataRoot = Path.Combine(debugDiscoveryRoot, "data");
    Directory.CreateDirectory(debugRuntimeRoot);
    Directory.CreateDirectory(inaccessibleRoot);
    Directory.CreateDirectory(skippedDataRoot);
    File.WriteAllText(Path.Combine(debugRuntimeRoot, "Probe.runtimeconfig.json"), "{}");
    File.WriteAllBytes(Path.Combine(debugRuntimeRoot, "Probe.exe"), [0x4d, 0x5a]);
    var inaccessibleVisited = false;
    var skippedDataVisited = false;
    string[] EnumerateDebugFiles(string directory)
    {
        if (directory.Equals(skippedDataRoot, StringComparison.OrdinalIgnoreCase))
        {
            skippedDataVisited = true;
            throw new IOException("The skipped data root must not be enumerated.");
        }
        if (directory.Equals(inaccessibleRoot, StringComparison.OrdinalIgnoreCase))
        {
            inaccessibleVisited = true;
            throw new IOException("Simulated inaccessible workspace directory.");
        }
        return Directory.GetFiles(directory, "*.runtimeconfig.json", SearchOption.TopDirectoryOnly);
    }
    string[] EnumerateDebugDirectories(string directory)
    {
        if (directory.Equals(inaccessibleRoot, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Simulated inaccessible workspace directory.");
        return Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly);
    }
    var debugTargets = DeveloperServicesBridge.DiscoverDebugTargetsCore(
        debugDiscoveryRoot,
        BuildConfiguration.Debug,
        CancellationToken.None,
        EnumerateDebugFiles,
        EnumerateDebugDirectories);
    if (!inaccessibleVisited
        || skippedDataVisited
        || !debugTargets.SequenceEqual(["src/Probe/bin/Debug/net10.0/Probe.exe"], StringComparer.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("Developer-services debugger discovery did not skip metadata roots, tolerate an inaccessible directory, and preserve valid targets.");
        return 19;
    }
    var developerFrames = new List<JsonElement>();
    await using (var developerBridge = new DeveloperServicesBridge(
                     developerWorkspace,
                     developerWorkspace,
                     message => developerFrames.Add(JsonSerializer.SerializeToElement(message))))
    {
        await developerBridge.DescribeAsync(DeveloperServicesBridge.ProtocolVersion, "describe-smoke");
        var description = developerFrames.FirstOrDefault(frame => GetString(frame, "type") == "developerServices.describe.result");
        if (description.ValueKind != JsonValueKind.Object
            || description.GetProperty("version").GetInt32() != 2
            || !description.GetProperty("targets").EnumerateArray().Any(item => item.GetString() == "Smoke.csproj")
            || !description.GetProperty("providers").EnumerateArray().Any(item =>
                GetString(item, "providerId") == "dotnet"
                && GetString(item.GetProperty("availability"), "state") == "available")
            || !description.GetProperty("providers").EnumerateArray().Any(item =>
                GetString(item, "providerId") == "roslyn-lsp"
                && GetString(item.GetProperty("availability"), "state") == "unavailable")
            || !description.GetProperty("providers").EnumerateArray().Any(item =>
                GetString(item, "providerId") == "hermes-dotnet-dap"
                && GetString(item.GetProperty("availability"), "state") == "unavailable")
            || !description.GetProperty("languageTooling").EnumerateArray().Any(item =>
                GetString(item, "providerId") == "dotnet"
                && item.GetProperty("capabilities").EnumerateArray().Count(capability =>
                    GetString(capability, "availability") == "available") == 2
                && item.GetProperty("capabilities").EnumerateArray().Any(capability =>
                    GetString(capability, "capabilityId") == "dotnet.compiler"
                    && GetString(capability, "availability") == "available")
                && item.GetProperty("capabilities").EnumerateArray().Any(capability =>
                    GetString(capability, "capabilityId") == "dotnet.tests"
                    && GetString(capability, "availability") == "available"))
            || !description.GetProperty("languageTooling").EnumerateArray().Any(item =>
                GetString(item, "providerId") == "raspberry-pi"
                && item.GetProperty("capabilities").EnumerateArray().All(capability =>
                    GetString(capability, "availability") == "unavailable"
                    && GetString(capability, "code") == "trusted-target-not-configured"))
            || GetString(description.GetProperty("availability"), "state") != "available")
        {
            Console.Error.WriteLine("Developer-services description did not advertise the guarded .NET target.");
            return 20;
        }

        await developerBridge.BuildAsync(DeveloperServicesBridge.ProtocolVersion, "build-smoke", 41, "Smoke.csproj", "Debug");
        var build = developerFrames.FirstOrDefault(frame => GetString(frame, "type") == "developerServices.build.result");
        if (build.ValueKind != JsonValueKind.Object
            || build.GetProperty("revision").GetInt32() != 41
            || GetString(build, "operation") != "build"
            || build.GetProperty("stale").GetBoolean()
            || !build.GetProperty("succeeded").GetBoolean()
            || build.GetProperty("diagnostics").GetArrayLength() != 0)
        {
            Console.Error.WriteLine("Developer-services bridge did not return a successful bounded build result.");
            if (build.ValueKind == JsonValueKind.Object) Console.Error.WriteLine(build.GetRawText());
            return 21;
        }

        await developerBridge.AnalyzeAsync(DeveloperServicesBridge.ProtocolVersion, "analyze-smoke", 42, "Smoke.csproj", "Release");
        var analyze = developerFrames.FirstOrDefault(frame => GetString(frame, "type") == "developerServices.analyze.result");
        if (analyze.ValueKind != JsonValueKind.Object
            || analyze.GetProperty("revision").GetInt32() != 42
            || GetString(analyze, "operation") != "analyze"
            || analyze.GetProperty("stale").GetBoolean()
            || !analyze.GetProperty("succeeded").GetBoolean())
        {
            Console.Error.WriteLine("Developer-services bridge did not return a successful typed analysis result.");
            if (analyze.ValueKind == JsonValueKind.Object) Console.Error.WriteLine(analyze.GetRawText());
            return 22;
        }

        await developerBridge.BuildAsync(DeveloperServicesBridge.ProtocolVersion, "absolute-target-smoke", 43, Path.Combine(developerWorkspace, "Smoke.csproj"), "Debug");
        if (!developerFrames.Any(frame =>
                GetString(frame, "type") == "developerServices.error"
                && GetString(frame, "requestId") == "absolute-target-smoke"
                && GetString(frame, "code") == "invalid_target"))
        {
            Console.Error.WriteLine("Developer-services bridge accepted an absolute renderer target.");
            return 23;
        }

        await developerBridge.InspectLanguageToolingProviderAsync(1, "language-tooling-inspect-smoke", "gcc");
        var toolingInspect = developerFrames.FirstOrDefault(frame =>
            GetString(frame, "type") == "developerServices.languageTooling.result"
            && GetString(frame, "requestId") == "language-tooling-inspect-smoke");
        if (toolingInspect.ValueKind != JsonValueKind.Object
            || toolingInspect.GetProperty("version").GetInt32() != 1
            || GetString(toolingInspect, "providerId") != "gcc"
            || GetString(toolingInspect, "operation") != "inspect-provider"
            || toolingInspect.GetProperty("evidence").ValueKind != JsonValueKind.Object
            || toolingInspect.GetRawText().Contains("executable", StringComparison.OrdinalIgnoreCase)
            || toolingInspect.GetRawText().Contains("argv", StringComparison.OrdinalIgnoreCase)
            || toolingInspect.GetRawText().Contains("environment", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Developer-services language-tooling inspection leaked native authority or returned an invalid envelope.");
            return 24;
        }

        await developerBridge.CompileLanguageToolingAsync(1, "language-tooling-path-smoke", "gcc", developerWorkspace, "debug", null);
        var toolingRejected = developerFrames.FirstOrDefault(frame =>
            GetString(frame, "type") == "developerServices.languageTooling.result"
            && GetString(frame, "requestId") == "language-tooling-path-smoke");
        if (toolingRejected.ValueKind != JsonValueKind.Object
            || toolingRejected.GetProperty("succeeded").GetBoolean()
            || GetString(toolingRejected, "code") != "invalid-path")
        {
            Console.Error.WriteLine("Developer-services language-tooling bridge accepted an absolute renderer target.");
            return 25;
        }

        await developerBridge.RunLanguageToolingTestsAsync(
            1, "language-tooling-tests-smoke", "dotnet", "Smoke.csproj", null);
        var toolingTests = developerFrames.FirstOrDefault(frame =>
            GetString(frame, "type") == "developerServices.languageTooling.result"
            && GetString(frame, "requestId") == "language-tooling-tests-smoke");
        if (toolingTests.ValueKind != JsonValueKind.Object
            || !toolingTests.GetProperty("succeeded").GetBoolean()
            || GetString(toolingTests, "operation") != "run-tests"
            || !toolingTests.TryGetProperty("result", out var toolingTestResult)
            || !toolingTestResult.GetProperty("succeeded").GetBoolean())
        {
            Console.Error.WriteLine("Developer-services language-tooling bridge did not execute the fixed .NET test operation.");
            return 26;
        }
    }
    Console.WriteLine("Desktop developer-services v2 build/analyze/test and typed language-tooling renderer contract passed.");

    var roslynFrames = new List<JsonElement>();
    var fakeRoslyn = new SmokeRoslynHost();
    await using (var languageBridge = new DeveloperServicesBridge(
                     developerWorkspace,
                     developerWorkspace,
                     message => roslynFrames.Add(JsonSerializer.SerializeToElement(message)),
                     fakeRoslyn))
    {
        await languageBridge.OpenLanguageDocumentAsync(2, "language-open-smoke", 1, "Program.cs", "class Broken { void M() { int x = ; } }");
        var opened = roslynFrames.FirstOrDefault(frame => GetString(frame, "type") == "developerServices.language.open.result");
        var languageSessionId = GetString(opened, "sessionId");
        if (languageSessionId is null || languageSessionId.Length != 48 || fakeRoslyn.OpenedRevision != 1)
        {
            Console.Error.WriteLine("Roslyn document open did not return a bounded opaque session.");
            return 24;
        }

        fakeRoslyn.Publish(1, new LspDiagnostic(
            new LspRange(new LspPosition(0, 6), new LspPosition(0, 12)),
            1,
            JsonSerializer.SerializeToElement("CS1525"),
            "Microsoft.CodeAnalysis",
            "Invalid expression term ';'"));
        var diagnostics = roslynFrames.LastOrDefault(frame => GetString(frame, "type") == "developerServices.language.diagnostics");
        var firstDiagnostic = diagnostics.GetProperty("diagnostics")[0];
        if (GetString(diagnostics, "sessionId") != languageSessionId
            || diagnostics.GetProperty("revision").GetInt32() != 1
            || GetString(firstDiagnostic, "source") != "roslyn"
            || firstDiagnostic.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32() != 1
            || firstDiagnostic.GetProperty("range").GetProperty("start").GetProperty("column").GetInt32() != 7)
        {
            Console.Error.WriteLine("Roslyn diagnostics were not session-, revision-, and coordinate-bound.");
            return 25;
        }

        await languageBridge.RunLanguageOperationAsync(
            2,
            "language-completion-smoke",
            languageSessionId,
            1,
            "Program.cs",
            "completion",
            0,
            5,
            -1,
            -1,
            null,
            true);
        var completion = roslynFrames.LastOrDefault(frame =>
            GetString(frame, "type") == "developerServices.language.result"
            && GetString(frame, "requestId") == "language-completion-smoke");
        if (completion.ValueKind != JsonValueKind.Object
            || GetString(completion, "sessionId") != languageSessionId
            || GetString(completion, "operation") != "completion"
            || GetString(completion.GetProperty("result").GetProperty("items")[0], "label") != "Console"
            || fakeRoslyn.LastOperation != "completion")
        {
            Console.Error.WriteLine("Roslyn completion was not session/revision-bound through the desktop bridge.");
            return 251;
        }

        await languageBridge.ChangeLanguageDocumentAsync(2, "language-change-smoke", languageSessionId, 2, "Program.cs", "class Fixed { }");
        await languageBridge.ChangeLanguageDocumentAsync(2, "language-stale-smoke", languageSessionId, 1, "Program.cs", "class Stale { }");
        if (fakeRoslyn.ChangedRevision != 2 || !roslynFrames.Any(frame =>
                GetString(frame, "type") == "developerServices.error"
                && GetString(frame, "requestId") == "language-stale-smoke"
                && GetString(frame, "code") == "stale_revision"))
        {
            Console.Error.WriteLine("Roslyn revision binding did not reject a stale document update.");
            return 26;
        }

        await languageBridge.CloseLanguageDocumentAsync(2, "language-close-smoke", languageSessionId, "Program.cs");
        if (!fakeRoslyn.Closed || !roslynFrames.Any(frame => GetString(frame, "type") == "developerServices.language.close.result"))
        {
            Console.Error.WriteLine("Roslyn language-session close was not acknowledged.");
            return 27;
        }
    }
    Console.WriteLine("Desktop Roslyn live diagnostics session/revision/workspace contract passed.");
}
finally
{
    if (Directory.Exists(developerWorkspace)) Directory.Delete(developerWorkspace, recursive: true);
}

if (args.Contains("--developer-services-only", StringComparer.Ordinal)) return 0;

var output = new StringBuilder();
var outputGate = new object();
var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

await using var session = await ConPtySession.StartAsync(
    Directory.GetCurrentDirectory(),
    100,
    24,
    data => { lock (outputGate) output.Append(data); },
    code => exit.TrySetResult(code));

var promptDeadline = DateTime.UtcNow.AddSeconds(10);
while (DateTime.UtcNow < promptDeadline)
{
    lock (outputGate)
    {
        if (output.ToString().Contains("PS ", StringComparison.Ordinal)) break;
    }
    await Task.Delay(50);
}

await session.WriteAsync($"Write-Output '{marker}'\r");
var deadline = DateTime.UtcNow.AddSeconds(10);
while (DateTime.UtcNow < deadline)
{
    lock (outputGate)
    {
        if (output.ToString().Contains(marker, StringComparison.Ordinal)) break;
    }
    await Task.Delay(50);
}

string captured;
lock (outputGate) captured = output.ToString();
if (!captured.Contains(marker, StringComparison.Ordinal))
{
    Console.Error.WriteLine("ConPTY did not return the PowerShell smoke-test marker.");
    Console.Error.WriteLine($"Captured UTF-8 hex: {Convert.ToHexString(Encoding.UTF8.GetBytes(captured))}");
    return 1;
}

await session.WriteAsync("exit\r");
var exitCode = await exit.Task.WaitAsync(TimeSpan.FromSeconds(10));
Console.WriteLine($"ConPTY round trip passed; PowerShell PID {session.ProcessId}, exit code {exitCode}.");
if (exitCode != 0) return exitCode;

var frames = Channel.CreateUnbounded<JsonElement>();
await using var codex = new CodexAppServerBridge(
    Directory.GetCurrentDirectory(),
    message => frames.Writer.TryWrite(JsonSerializer.SerializeToElement(message)));
await codex.StartAsync();
var ready = await ReadFrameAsync(frames.Reader, frame => GetString(frame, "type") == "codex.ready", TimeSpan.FromSeconds(10));
if (ready is null)
{
    Console.Error.WriteLine("Codex app-server did not report ready.");
    return 2;
}

await SendJsonAsync(codex, """
    {"method":"initialize","id":1,"params":{"clientInfo":{"name":"hermes_workbench_smoke","title":"Hermes Workbench Smoke","version":"0.1.0"},"capabilities":null}}
    """);
var initialized = await ReadFrameAsync(frames.Reader, frame => IsProtocolResponse(frame, 1), TimeSpan.FromSeconds(10));
if (initialized is null || !initialized.Value.GetProperty("payload").TryGetProperty("result", out var initializeResult))
{
    Console.Error.WriteLine("Codex app-server initialization handshake failed.");
    return 3;
}
if (GetString(initializeResult, "platformOs") != "windows")
{
    Console.Error.WriteLine("Codex app-server returned an unexpected platform.");
    return 4;
}

await SendJsonAsync(codex, """{"method":"initialized","params":{}}""");
await SendJsonAsync(codex, """{"method":"account/read","id":2,"params":{"refreshToken":false}}""");
var account = await ReadFrameAsync(frames.Reader, frame => IsProtocolResponse(frame, 2), TimeSpan.FromSeconds(10));
if (account is null || !account.Value.GetProperty("payload").TryGetProperty("result", out _))
{
    Console.Error.WriteLine("Codex app-server account-state read failed.");
    return 5;
}

Console.WriteLine($"Codex app-server handshake passed; PID {ready.Value.GetProperty("processId").GetInt32()}, no model turn sent.");
return 0;

static async Task SendJsonAsync(CodexAppServerBridge bridge, string json)
{
    using var document = JsonDocument.Parse(json);
    await bridge.SendAsync(document.RootElement.Clone());
}

static async Task<JsonElement?> ReadFrameAsync(ChannelReader<JsonElement> reader, Func<JsonElement, bool> predicate, TimeSpan timeout)
{
    using var cancellation = new CancellationTokenSource(timeout);
    try
    {
        while (await reader.WaitToReadAsync(cancellation.Token))
        {
            while (reader.TryRead(out var frame))
            {
                if (predicate(frame)) return frame;
                if (GetString(frame, "type") is "codex.error" or "codex.unavailable")
                {
                    Console.Error.WriteLine(GetString(frame, "message"));
                }
            }
        }
    }
    catch (OperationCanceledException) { }
    return null;
}

static bool IsProtocolResponse(JsonElement frame, int requestId)
{
    if (GetString(frame, "type") != "codex.protocol" || !frame.TryGetProperty("payload", out var payload)) return false;
    return payload.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.GetInt32() == requestId;
}

static string? GetString(JsonElement element, string name) =>
    element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

sealed class OpenRouterSmokeHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
{
    internal Uri? RequestUri { get; private set; }
    internal string? AuthorizationScheme { get; private set; }
    internal string? AuthorizationParameter { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestUri = request.RequestUri;
        AuthorizationScheme = request.Headers.Authorization?.Scheme;
        AuthorizationParameter = request.Headers.Authorization?.Parameter;
        return Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
            RequestMessage = request,
        });
    }
}

sealed class SmokeRoslynHost : IDeveloperRoslynHost
{
    private string? _uri;

    public event Action<LspPublishDiagnosticsParams>? DiagnosticsPublished;

    public int OpenedRevision { get; private set; }

    public int ChangedRevision { get; private set; }

    public bool Closed { get; private set; }

    public string? LastOperation { get; private set; }

    public Task OpenDocumentAsync(string workspaceRoot, string uri, int revision, string text, CancellationToken cancellationToken)
    {
        _uri = uri;
        OpenedRevision = revision;
        return Task.CompletedTask;
    }

    public Task ChangeDocumentAsync(string uri, int revision, string text, CancellationToken cancellationToken)
    {
        if (uri != _uri) throw new InvalidOperationException();
        ChangedRevision = revision;
        return Task.CompletedTask;
    }

    public Task CloseDocumentAsync(string uri, CancellationToken cancellationToken)
    {
        if (uri != _uri) throw new InvalidOperationException();
        Closed = true;
        return Task.CompletedTask;
    }

    public Task<RoslynLanguageResult?> CompletionAsync(string uri, int revision, LspPosition position, CancellationToken cancellationToken) =>
        LanguageResult("completion", new { items = new[] { new { label = "Console", kind = 7 } } });

    public Task<RoslynLanguageResult?> HoverAsync(string uri, int revision, LspPosition position, CancellationToken cancellationToken) =>
        LanguageResult("hover", new { contents = "hover" });

    public Task<RoslynLanguageResult?> DefinitionAsync(string uri, int revision, LspPosition position, CancellationToken cancellationToken) =>
        LanguageResult("definition", Array.Empty<object>());

    public Task<RoslynLanguageResult?> ReferencesAsync(string uri, int revision, LspPosition position, bool includeDeclaration, CancellationToken cancellationToken) =>
        LanguageResult("references", Array.Empty<object>());

    public Task<RoslynLanguageResult?> RenameAsync(string uri, int revision, LspPosition position, string newName, CancellationToken cancellationToken) =>
        LanguageResult("rename", new { changes = new { } });

    public Task<RoslynLanguageResult?> CodeActionsAsync(string uri, int revision, LspRange range, CancellationToken cancellationToken) =>
        LanguageResult("code-actions", Array.Empty<object>());

    private Task<RoslynLanguageResult?> LanguageResult(string operation, object value)
    {
        LastOperation = operation;
        return Task.FromResult<RoslynLanguageResult?>(new RoslynLanguageResult(JsonSerializer.SerializeToElement(value)));
    }

    public void Publish(int revision, params LspDiagnostic[] diagnostics) =>
        DiagnosticsPublished?.Invoke(new LspPublishDiagnosticsParams(_uri!, diagnostics, revision));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
