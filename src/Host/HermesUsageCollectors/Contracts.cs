using System.Text.Json.Serialization;

namespace HermesUsageCollectors;

public static class UsageProviderIds
{
    public const string OpenAiApi = "openai-api";
    public const string AnthropicApi = "anthropic-api";
    public const string GoogleCloudBudgets = "google-cloud-budgets";
    public const string GoogleCloudBillingExport = "google-cloud-billing-export";
    public const string GoogleAiStudio = "google-ai-studio";
    public const string ChatGptSubscription = "chatgpt-subscription";
    public const string ClaudeConsumer = "claude-consumer";
}

[JsonConverter(typeof(JsonStringEnumConverter<CapabilityState>))]
public enum CapabilityState { Supported, SetupRequired, Unavailable, PermissionDenied, Stale, Partial, Error }

[JsonConverter(typeof(JsonStringEnumConverter<ObservationKind>))]
public enum ObservationKind { Usage, Cost, Budget, Quota }

[JsonConverter(typeof(JsonStringEnumConverter<ValueProvenance>))]
public enum ValueProvenance { ProviderReported, ConfiguredThreshold, Estimated, Projected }

[JsonConverter(typeof(JsonStringEnumConverter<FreshnessState>))]
public enum FreshnessState { Current, Delayed, Stale, Unknown }

[JsonConverter(typeof(JsonStringEnumConverter<ProviderErrorCategory>))]
public enum ProviderErrorCategory
{
    Configuration, Unauthorized, Forbidden, RateLimited, Redirected, Timeout, Cancelled,
    Network, MalformedResponse, OversizedResponse, RemoteFailure, PaginationLimit, Unexpected,
}

public sealed record ProviderCredentialProfile(
    string ProviderId,
    string ProfileId,
    string DisplayName,
    string CredentialReference,
    IReadOnlyDictionary<string, string>? Settings = null)
{
    public ProviderProfileMetadata Metadata => new(ProviderId, ProfileId, DisplayName);
    public string? Setting(string name) => Settings is not null && Settings.TryGetValue(name, out var value) ? value : null;
}

public sealed record ProviderProfileMetadata(string ProviderId, string ProfileId, string DisplayName);

public sealed record ObservationWindow(DateTimeOffset Start, DateTimeOffset End)
{
    public TimeSpan Duration => End - Start;
}

public sealed record ObservationFreshness(
    DateTimeOffset CollectedAt,
    DateTimeOffset? DataThrough,
    FreshnessState State,
    string Note);

public sealed record UsageObservation(
    ObservationKind Kind,
    string Metric,
    decimal Value,
    string Unit,
    string? Currency,
    ObservationWindow Window,
    string Source,
    ValueProvenance Provenance,
    ObservationFreshness Freshness,
    string? Qualifier = null);

public sealed record CapabilityStatus(string Capability, CapabilityState State, string Reason);

public sealed record SanitizedProviderError(
    ProviderErrorCategory Category,
    string Code,
    string Message,
    bool Retryable,
    int? HttpStatus = null,
    string? ProviderCode = null);

public sealed record ProviderCollectionResult(
    ProviderProfileMetadata Profile,
    IReadOnlyList<CapabilityStatus> Capabilities,
    IReadOnlyList<UsageObservation> Observations,
    IReadOnlyList<SanitizedProviderError> Errors,
    DateTimeOffset CollectedAt)
{
    public bool HasData => Observations.Count > 0;
    public bool IsPartial => HasData && Errors.Count > 0;
}

public sealed record AggregateObservation(
    ObservationKind Kind,
    string Metric,
    decimal Value,
    string Unit,
    string? Currency,
    ValueProvenance Provenance,
    string? Qualifier,
    ObservationWindow Window,
    int ContributingProfiles,
    IReadOnlyList<string> ContributingProviders,
    bool IsPartial);

public sealed record AggregateCollectionResult(
    IReadOnlyList<ProviderCollectionResult> Profiles,
    IReadOnlyList<AggregateObservation> Aggregates,
    DateTimeOffset CollectedAt,
    bool HasPartialFailures);

public sealed record UsageCollectionRequest(ObservationWindow Window, int MaximumPages = 8, int MaximumRows = 2048)
{
    public const int AbsoluteMaximumPages = 16;
    public const int AbsoluteMaximumRows = 10_000;

    public void Validate(TimeSpan maximumWindow)
    {
        if (Window.End <= Window.Start) throw new ArgumentException("The collection window end must be after its start.");
        if (Window.Duration > maximumWindow) throw new ArgumentException($"The collection window cannot exceed {maximumWindow.TotalDays:0} days.");
        if (MaximumPages is < 1 or > AbsoluteMaximumPages) throw new ArgumentOutOfRangeException(nameof(MaximumPages));
        if (MaximumRows is < 1 or > AbsoluteMaximumRows) throw new ArgumentOutOfRangeException(nameof(MaximumRows));
    }
}

public interface IUsageCollector
{
    string ProviderId { get; }
    Task<ProviderCollectionResult> CollectAsync(
        ProviderCredentialProfile profile,
        UsageCollectionRequest request,
        CancellationToken cancellationToken = default);
}
