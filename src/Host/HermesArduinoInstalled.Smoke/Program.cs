using System.Security.Cryptography;
using HermesDeveloperServices;
using HermesDeveloperServices.LanguageTooling;
using HermesDeveloperServices.LanguageTooling.Operations;

if (args.Length != 2)
    throw new InvalidOperationException("Pass the exact installed desktop root and owned Arduino smoke workspace.");

var installRoot = Path.GetFullPath(args[0]);
var workspaceRoot = Path.GetFullPath(args[1]);
var options = new ArduinoProviderOptions(
    installRoot,
    "toolchains/arduino",
    "hermes-toolchain-receipt.json",
    "arduino-config.json",
    "developer-services/arduino-state");

var evidence = new ArduinoPinnedEvidenceSource(options);
var statuses = await evidence.InspectAsync(workspaceRoot, CancellationToken.None);
Require(statuses.Count == 2 && statuses.All(status => status.Availability == LanguageToolingCapabilityState.Available),
    "The installed Arduino receipt/core authority was not available: "
    + string.Join(", ", statuses.Select(status => $"{status.CapabilityId}={status.Availability}/{status.Code}")));

var handler = new ArduinoLanguageToolingOperationHandler(options, workspaceRoot);
var inspection = await handler.ExecuteAsync(new InspectLanguageToolingProjectRequest(
    1, "arduino:real:inspect", "workspace:arduino-real", "arduino", "Blink"), CancellationToken.None);
Require(inspection.Succeeded, $"Arduino project inspection failed safely: {inspection.Code}.");

var compile = await handler.ExecuteAsync(new CompileLanguageToolingRequest(
    1,
    "arduino:real:compile",
    "workspace:arduino-real",
    "arduino",
    "Blink/Blink.ino",
    "check",
    ArduinoProviderOptions.SupportedFqbn), CancellationToken.None);
var artifacts = compile.ArtifactPaths ?? [];
Require(compile.Succeeded && artifacts.Count > 0,
    $"Arduino compile failed safely: {compile.Code}.");

var outputRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var digests = new List<string>();
foreach (var relative in artifacts)
{
    var normalized = relative.Replace('\\', '/');
    var first = normalized.Split('/', 2)[0];
    Require(first.StartsWith(".hermes-arduino-release-", StringComparison.Ordinal),
        "Arduino returned an artifact outside its host-owned output root.");
    var artifact = Path.GetFullPath(Path.Combine(workspaceRoot, relative));
    Require(artifact.StartsWith(workspaceRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase)
        && File.Exists(artifact)
        && (File.GetAttributes(artifact) & FileAttributes.ReparsePoint) == 0
        && new FileInfo(artifact).Length > 0,
        "Arduino returned an invalid compiled artifact.");
    outputRoots.Add(Path.Combine(workspaceRoot, first));
    digests.Add(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(artifact))));
}

foreach (var outputRoot in outputRoots)
{
    if (!Directory.Exists(outputRoot)
        || (File.GetAttributes(outputRoot) & FileAttributes.ReparsePoint) != 0
        || !Path.GetFileName(outputRoot).StartsWith(".hermes-arduino-release-", StringComparison.Ordinal))
        throw new InvalidOperationException("Refusing to clean an unexpected Arduino smoke output root.");
    Directory.Delete(outputRoot, recursive: true);
}

Console.WriteLine($"PASS installed Arduino inspect/compile: {digests.Count} receipt-bound artifacts verified");
return 0;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
