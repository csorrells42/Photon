namespace HermesDeveloperServices.LanguageTooling.RaspberryPi;

/// <summary>
/// One opaque renderer-visible target ID bound to a host-owned SSH identity and host-key trust.
/// The network authority never crosses the renderer protocol.
/// </summary>
public sealed record RaspberryPiConfiguredTarget(
    string TargetId,
    RaspberryPiTrustedHost TrustedHost);

public sealed record RaspberryPiLanguageToolingRegistration(
    ILanguageToolingEvidenceSource EvidenceSource,
    ILanguageToolingOperationHandler OperationHandler);

public static class RaspberryPiLanguageToolingProvider
{
    public static RaspberryPiLanguageToolingRegistration Create(
        RaspberryPiProviderOptions options,
        IReadOnlyCollection<RaspberryPiConfiguredTarget> configuredTargets)
    {
        ArgumentNullException.ThrowIfNull(options);
        var targets = RequireTargets(configuredTargets);
        var authority = new ReceiptBoundRaspberryPiInspectionAuthority(options, targets.Values);
        return new(
            new RaspberryPiInspectionEvidenceSource(authority),
            new RaspberryPiLanguageToolingOperationHandler(authority, targets));
    }

    internal static IReadOnlyDictionary<string, RaspberryPiTrustedHost> RequireTargets(
        IReadOnlyCollection<RaspberryPiConfiguredTarget> configuredTargets)
    {
        ArgumentNullException.ThrowIfNull(configuredTargets);
        if (configuredTargets.Count is 0 or > 64)
            throw new ArgumentException("Configure between one and 64 trusted Raspberry Pi targets.", nameof(configuredTargets));

        var targets = new Dictionary<string, RaspberryPiTrustedHost>(StringComparer.Ordinal);
        foreach (var configured in configuredTargets)
        {
            ArgumentNullException.ThrowIfNull(configured);
            var targetId = RequireTargetId(configured.TargetId);
            RaspberryPiTrustedHostProvider.ValidateTarget(configured.TrustedHost);
            if (!targets.TryAdd(targetId, configured.TrustedHost))
                throw new ArgumentException("Trusted Raspberry Pi target identifiers must be unique.", nameof(configuredTargets));
        }
        return targets;
    }

    private static string RequireTargetId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 128 || !value.All(character => char.IsAsciiLetterOrDigit(character)
            || character is '.' or '_' or ':' or '-'))
            throw new ArgumentException("A trusted Raspberry Pi target identifier is invalid.", nameof(value));
        return value;
    }
}

public sealed class RaspberryPiInspectionEvidenceSource : ILanguageToolingEvidenceSource
{
    private readonly IRaspberryPiInspectionAuthority _authority;

    internal RaspberryPiInspectionEvidenceSource(IRaspberryPiInspectionAuthority authority) =>
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));

    public string ProviderId => LanguageToolingCatalog.RaspberryPi;

    public IReadOnlyCollection<string> CapabilityIds { get; } = ["raspberry-pi.inspect"];

    public async ValueTask<IReadOnlyList<LanguageToolingCapabilityStatus>> InspectAsync(
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        _ = workspaceRoot;
        try
        {
            await _authority.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
            return ArduinoPinnedEvidenceSource.Available(
                CapabilityIds,
                "The trusted host verified the receipt-bound SSH runtime and configured Raspberry Pi target authorities.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TrustedToolchainValidationException exception)
        {
            return ArduinoPinnedEvidenceSource.Unavailable(CapabilityIds, exception.Code, exception.Message);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or System.Security.Cryptography.CryptographicException)
        {
            return ArduinoPinnedEvidenceSource.Error(
                CapabilityIds,
                "raspberry-pi-verification-failed",
                "The receipt-bound Raspberry Pi inspection authority could not be verified.");
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class RaspberryPiLanguageToolingOperationHandler : ILanguageToolingOperationHandler
{
    private readonly IRaspberryPiInspectionAuthority _authority;
    private readonly IReadOnlyDictionary<string, RaspberryPiTrustedHost> _targets;

    internal RaspberryPiLanguageToolingOperationHandler(
        IRaspberryPiInspectionAuthority authority,
        IReadOnlyDictionary<string, RaspberryPiTrustedHost> targets)
    {
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
    }

    public string ProviderId => LanguageToolingCatalog.RaspberryPi;

    public IReadOnlyCollection<string> Operations { get; } = ["inspect-remote-target"];

    public async ValueTask<LanguageToolingOperationResult> ExecuteAsync(
        LanguageToolingHostRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ProviderId != ProviderId || request is not InspectLanguageToolingRemoteTargetRequest inspect)
            throw new LanguageToolingRequestException(
                "operation-mismatch",
                "The Raspberry Pi handler accepts only typed target inspection requests.");
        if (!_targets.TryGetValue(inspect.TargetId, out var target))
            return new(false, "target-not-configured", "The requested Raspberry Pi target is not configured by this host.");

        try
        {
            var result = await _authority.ProbeAsync(target, cancellationToken).ConfigureAwait(false);
            return new(
                result.Succeeded,
                result.FailureCode ?? (result.Succeeded ? "ok" : "raspberry-pi-inspection-failed"),
                result.Summary);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or TrustedToolchainValidationException
            or ArgumentException)
        {
            return new(false, "raspberry-pi-authority-failed", "The receipt-bound Raspberry Pi authority could not be established safely.");
        }
    }
}

internal interface IRaspberryPiInspectionAuthority
{
    Task EnsureAvailableAsync(CancellationToken cancellationToken);

    Task<EmbeddedHostOperationResult> ProbeAsync(
        RaspberryPiTrustedHost target,
        CancellationToken cancellationToken);
}

internal sealed class ReceiptBoundRaspberryPiInspectionAuthority(
    RaspberryPiProviderOptions options,
    IEnumerable<RaspberryPiTrustedHost> targets) : IRaspberryPiInspectionAuthority
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly RaspberryPiTrustedHost[] _targets = targets.ToArray();
    private RaspberryPiTrustedHostProvider? _provider;

    public async Task EnsureAvailableAsync(CancellationToken cancellationToken) =>
        _ = await GetProviderAsync(cancellationToken).ConfigureAwait(false);

    public async Task<EmbeddedHostOperationResult> ProbeAsync(
        RaspberryPiTrustedHost target,
        CancellationToken cancellationToken)
    {
        var provider = await GetProviderAsync(cancellationToken).ConfigureAwait(false);
        return await provider.ProbeAsync(target, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RaspberryPiTrustedHostProvider> GetProviderAsync(CancellationToken cancellationToken)
    {
        var provider = _provider;
        if (provider is not null) return provider;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            provider = _provider;
            if (provider is null)
            {
                provider = await RaspberryPiTrustedHostProvider.CreateAsync(options, cancellationToken).ConfigureAwait(false);
                foreach (var target in _targets) provider.VerifyTargetAuthority(target);
                _provider = provider;
            }
            return provider;
        }
        finally
        {
            _gate.Release();
        }
    }
}
