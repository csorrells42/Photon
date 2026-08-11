using HermesUsageCollectors;

namespace HermesDesktop;

internal sealed class ProviderUsageCollectorBridge : IDisposable
{
    private readonly WindowsCredentialVault _vault;
    private readonly OpenAiUsageCollector _openAi;
    private readonly AnthropicUsageCollector _anthropic;
    private readonly GoogleCloudCliAccount _googleCloud;
    private readonly GoogleGeminiMonitoringCollector _googleGemini;
    private readonly UsageCollectionCoordinator _coordinator;

    internal ProviderUsageCollectorBridge(WindowsCredentialVault vault)
    {
        _vault = vault;
        var secrets = new DelegateSecretSource(ResolveSecretAsync);
        _openAi = new OpenAiUsageCollector(secrets);
        _anthropic = new AnthropicUsageCollector(secrets);
        _googleCloud = new GoogleCloudCliAccount();
        _googleGemini = new GoogleGeminiMonitoringCollector(_googleCloud);
        _coordinator = new UsageCollectionCoordinator([_openAi, _anthropic, _googleGemini]);
    }

    internal async Task<ProviderCollectionResult> CollectAsync(
        string provider,
        string credentialId,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken = default)
    {
        if (!NativeUsageProtocol.IsOrganizationProvider(provider)
            || !NativeCredentialProtocol.IsValidCredentialId(credentialId))
            throw new ArgumentException("The provider usage profile was invalid.");

        IReadOnlyDictionary<string, string>? settings = null;
        var credentialReference = $"{provider}:{credentialId}";
        if (provider == NativeUsageProtocol.GoogleAiStudioProvider)
        {
            var project = await _googleCloud.GetProjectAsync(cancellationToken).ConfigureAwait(false);
            settings = project is null
                ? null
                : new Dictionary<string, string> { [GoogleGeminiMonitoringCollector.ProjectSetting] = project };
            credentialReference = GoogleCloudCliAccount.CredentialReference;
        }

        var profile = new ProviderCredentialProfile(
            provider,
            credentialId,
            credentialId,
            credentialReference,
            settings);
        var aggregate = await _coordinator.CollectAsync(
            [profile],
            new UsageCollectionRequest(new ObservationWindow(start, end)),
            cancellationToken).ConfigureAwait(false);
        return aggregate.Profiles.Single();
    }

    private ValueTask<ProviderSecret?> ResolveSecretAsync(string reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var separator = reference.IndexOf(':');
        if (separator <= 0 || separator == reference.Length - 1 || reference.IndexOf(':', separator + 1) >= 0)
            return ValueTask.FromResult<ProviderSecret?>(null);

        var provider = reference[..separator];
        var credentialId = reference[(separator + 1)..];
        if (!NativeUsageProtocol.IsOrganizationProvider(provider)
            || !NativeCredentialProtocol.IsValidCredentialId(credentialId))
            return ValueTask.FromResult<ProviderSecret?>(null);

        var secret = _vault.ReadSecret(provider, credentialId);
        return ValueTask.FromResult(string.IsNullOrWhiteSpace(secret) ? null : ProviderSecret.FromString(secret));
    }

    internal static object ToMessage(ProviderCollectionResult result) => new
    {
        profile = new
        {
            providerId = result.Profile.ProviderId,
            profileId = result.Profile.ProfileId,
            displayName = result.Profile.DisplayName,
        },
        capabilities = result.Capabilities.Select(item => new
        {
            capability = item.Capability,
            state = item.State.ToString(),
            reason = item.Reason,
        }),
        observations = result.Observations.Select(item => new
        {
            kind = item.Kind.ToString(),
            metric = item.Metric,
            value = item.Value,
            unit = item.Unit,
            currency = item.Currency,
            window = new { start = item.Window.Start, end = item.Window.End },
            source = item.Source,
            provenance = item.Provenance.ToString(),
            freshness = new
            {
                collectedAt = item.Freshness.CollectedAt,
                dataThrough = item.Freshness.DataThrough,
                state = item.Freshness.State.ToString(),
                note = item.Freshness.Note,
            },
            qualifier = item.Qualifier,
        }),
        errors = result.Errors.Select(item => new
        {
            category = item.Category.ToString(),
            code = item.Code,
            message = item.Message,
            retryable = item.Retryable,
            httpStatus = item.HttpStatus,
            providerCode = item.ProviderCode,
        }),
        collectedAt = result.CollectedAt,
    };

    public void Dispose()
    {
        _openAi.Dispose();
        _anthropic.Dispose();
        _googleGemini.Dispose();
    }
}
