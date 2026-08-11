using System.Text.Json;
using HermesDeveloperServices;
using HermesDeveloperServices.LanguageTooling;
using HermesDeveloperServices.LanguageTooling.RaspberryPi;

var workspace = Path.Combine(Path.GetTempPath(), $"HermesRaspberryPiTooling-{Guid.NewGuid():N}");
Directory.CreateDirectory(workspace);
try
{
    var trusted = new RaspberryPiTrustedHost(
        "pi.internal",
        "builder",
        22,
        new RaspberryPiHostKeyTrust("ssh-ed25519", "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"));
    var authority = new FakeAuthority();
    var targets = RaspberryPiLanguageToolingProvider.RequireTargets(
        [new RaspberryPiConfiguredTarget("shop-pi", trusted)]);
    var evidence = new RaspberryPiInspectionEvidenceSource(authority);
    var handler = new RaspberryPiLanguageToolingOperationHandler(authority, targets);

    await using var registry = LanguageToolingRegistryFactory.Create(workspace, [evidence], [handler]);
    var description = await registry.DescribeProviderAsync(LanguageToolingCatalog.RaspberryPi);
    Require(description.Capabilities.Single(item => item.CapabilityId == "raspberry-pi.inspect") is
    { Availability: LanguageToolingCapabilityState.Available },
        "Receipt-bound target inspection was not reported available.");
    Require(description.Capabilities.Single(item => item.CapabilityId == "raspberry-pi.deploy") is
    { Availability: LanguageToolingCapabilityState.Unavailable },
        "Deployment became available without a review/commit authority.");

    var inspected = await registry.ExecuteAsync(new InspectLanguageToolingRemoteTargetRequest(
        1, "rpi:inspect:1", "workspace:1", "raspberry-pi", "shop-pi"));
    Require(inspected is { Succeeded: true, Code: "ok" } && authority.Probed is not null,
        "The configured target did not route through the receipt-bound authority.");
    Require(authority.Probed == trusted, "The opaque target ID changed its host-owned binding.");
    var serialized = JsonSerializer.Serialize(inspected);
    Require(!serialized.Contains(trusted.Host, StringComparison.Ordinal)
        && !serialized.Contains(trusted.User, StringComparison.Ordinal)
        && !serialized.Contains(trusted.HostKey.Sha256Fingerprint, StringComparison.Ordinal),
        "A native Raspberry Pi authority detail crossed the renderer result boundary.");

    var unknown = await registry.ExecuteAsync(new InspectLanguageToolingRemoteTargetRequest(
        1, "rpi:inspect:2", "workspace:1", "raspberry-pi", "unknown-pi"));
    Require(!unknown.Succeeded && unknown.Code == "target-not-configured" && authority.ProbeCount == 1,
        "An unknown renderer target reached the SSH authority.");

    try
    {
        _ = await registry.ExecuteAsync(new DeployLanguageToolingFileRequest(
            1, "rpi:deploy:1", "workspace:1", "raspberry-pi", "shop-pi", "build/app", "production"));
        throw new InvalidOperationException("Deployment was accepted without a reviewed destination authority.");
    }
    catch (LanguageToolingRequestException exception) when (exception.Code == "capability-unavailable")
    {
    }

    authority.Result = EmbeddedHostOperationResult.Failure(
        "probe", "ssh_host_key_mismatch", "The configured Raspberry Pi target could not be verified.");
    var failed = await registry.ExecuteAsync(new InspectLanguageToolingRemoteTargetRequest(
        1, "rpi:inspect:3", "workspace:1", "raspberry-pi", "shop-pi"));
    Require(!failed.Succeeded && failed.Code == "ssh_host_key_mismatch",
        "A failed trusted-host probe became a capability claim.");

    Require(handler.Operations.SequenceEqual(["inspect-remote-target"]),
        "The Raspberry Pi handler exposed an unreviewed mutation operation.");
    Console.WriteLine("PASS Raspberry Pi receipt-bound inspection and fail-closed deployment smoke");
    return 0;
}
finally
{
    if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

file sealed class FakeAuthority : IRaspberryPiInspectionAuthority
{
    public int ProbeCount { get; private set; }
    public RaspberryPiTrustedHost? Probed { get; private set; }
    public EmbeddedHostOperationResult Result { get; set; } = new(
        true, "probe", "The configured Raspberry Pi target was verified.", null, 0,
        string.Empty, string.Empty, false, 0, false, false, [], []);

    public Task EnsureAvailableAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<EmbeddedHostOperationResult> ProbeAsync(
        RaspberryPiTrustedHost target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ProbeCount += 1;
        Probed = target;
        return Task.FromResult(Result);
    }
}
