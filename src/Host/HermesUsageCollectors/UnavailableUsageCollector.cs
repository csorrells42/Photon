namespace HermesUsageCollectors;

public sealed class UnavailableUsageCollector : IUsageCollector
{
    private readonly string _capability;
    private readonly string _reason;

    public UnavailableUsageCollector(string providerId, string capability, string reason)
    {
        ProviderId = providerId ?? throw new ArgumentNullException(nameof(providerId));
        _capability = capability ?? throw new ArgumentNullException(nameof(capability));
        _reason = reason ?? throw new ArgumentNullException(nameof(reason));
    }

    public string ProviderId { get; }

    public Task<ProviderCollectionResult> CollectAsync(
        ProviderCredentialProfile profile,
        UsageCollectionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var invalid = CollectorSupport.ValidateProfile(profile, ProviderId);
        if (invalid is not null) return Task.FromResult(CollectorSupport.InvalidProfile(profile, invalid));

        return Task.FromResult(new ProviderCollectionResult(profile.Metadata,
            [new CapabilityStatus(_capability, CapabilityState.Unavailable, _reason)],
            [], [], DateTimeOffset.UtcNow));
    }

    public static UnavailableUsageCollector ChatGptSubscription() => new(
        UsageProviderIds.ChatGptSubscription, "consumer-subscription-usage", 
        "No supported official API exposes ChatGPT consumer subscription usage or remaining limits.");

    public static UnavailableUsageCollector ClaudeConsumer() => new(
        UsageProviderIds.ClaudeConsumer, "consumer-subscription-usage",
        "No supported official API exposes Claude consumer subscription usage or remaining limits.");

    public static UnavailableUsageCollector GoogleAiStudioPerKey() => new(
        UsageProviderIds.GoogleAiStudio, "per-key-usage",
        "No supported official API exposes Gemini API or AI Studio usage by individual API key.");
}
