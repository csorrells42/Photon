using System.Globalization;
using System.Text.Json;

namespace HermesUsageCollectors;

public sealed class AnthropicUsageCollector : IUsageCollector, IDisposable
{
    private const string UsagePath = "/v1/organizations/usage_report/messages";
    private const string CostsPath = "/v1/organizations/cost_report";
    private static readonly Uri Origin = new("https://api.anthropic.com/");
    private static readonly TimeSpan MaximumWindow = TimeSpan.FromDays(31);
    private readonly IProviderSecretSource _secrets;
    private readonly FixedEndpointHttpClient _http;

    public AnthropicUsageCollector(IProviderSecretSource secrets, HttpMessageHandler? handler = null, CollectorHttpPolicy? policy = null)
    {
        _secrets = secrets;
        _http = new FixedEndpointHttpClient(Origin, handler, policy);
    }

    public string ProviderId => UsageProviderIds.AnthropicApi;

    public async Task<ProviderCollectionResult> CollectAsync(
        ProviderCredentialProfile profile,
        UsageCollectionRequest request,
        CancellationToken cancellationToken = default)
    {
        var invalid = CollectorSupport.ValidateProfile(profile, ProviderId);
        if (invalid is not null) return CollectorSupport.InvalidProfile(profile, invalid);
        try { request.Validate(MaximumWindow); }
        catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
        {
            return CollectorSupport.InvalidProfile(profile, CollectorSupport.Configuration("invalid-window", "The Anthropic collection window or bounds are invalid."));
        }

        var resolution = await CollectorSupport.ResolveSecretAsync(_secrets, profile.CredentialReference, cancellationToken).ConfigureAwait(false);
        if (resolution.Error is not null)
            return CollectorSupport.CredentialFailure(profile, resolution.Error, "organization-usage", "organization-costs");
        using var secret = resolution.Secret;
        if (secret is null)
        {
            return CollectorSupport.SetupRequired(profile,
                new CapabilityStatus("organization-usage", CapabilityState.SetupRequired, "An Anthropic Admin API key is required."),
                new CapabilityStatus("organization-costs", CapabilityState.SetupRequired, "An Anthropic Admin API key is required."));
        }

        var token = secret.Materialize();
        var observations = new List<UsageObservation>();
        var capabilities = new List<CapabilityStatus>();
        var errors = new List<SanitizedProviderError>();
        await CaptureAsync("organization-usage", "Anthropic organization message usage is available with an Admin API key.",
            () => ReadUsageAsync(token, request, cancellationToken), observations, capabilities, errors).ConfigureAwait(false);
        if (!cancellationToken.IsCancellationRequested)
        {
            await CaptureAsync("organization-costs", "Anthropic organization costs are available with an Admin API key.",
                () => ReadCostsAsync(token, request, cancellationToken), observations, capabilities, errors).ConfigureAwait(false);
        }
        return new ProviderCollectionResult(profile.Metadata, capabilities.ToArray(), observations.ToArray(), errors.ToArray(), DateTimeOffset.UtcNow);
    }

    private static async Task CaptureAsync(
        string capability, string successReason, Func<Task<IReadOnlyList<UsageObservation>>> collect,
        List<UsageObservation> observations, List<CapabilityStatus> capabilities, List<SanitizedProviderError> errors)
    {
        try
        {
            observations.AddRange(await collect().ConfigureAwait(false));
            capabilities.Add(new CapabilityStatus(capability, CapabilityState.Supported, successReason));
        }
        catch (CollectorFailureException exception)
        {
            errors.Add(exception.Error);
            capabilities.Add(CollectorSupport.CapabilityFromError(capability, exception.Error));
        }
    }

    private async Task<IReadOnlyList<UsageObservation>> ReadUsageAsync(string token, UsageCollectionRequest request, CancellationToken cancellationToken)
    {
        decimal uncached = 0;
        decimal cacheWrite = 0;
        decimal cacheRead = 0;
        decimal output = 0;
        decimal webSearches = 0;
        string? page = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = 0;

        for (var pageIndex = 0; pageIndex < request.MaximumPages; pageIndex++)
        {
            using var json = CollectorJson.Parse(await _http.GetJsonAsync(AnthropicUri(UsagePath, request.Window, page), message => Authorize(message, token), cancellationToken).ConfigureAwait(false));
            var root = RequireRoot(json.RootElement);
            var data = CollectorJson.RequiredArray(root, "data");
            CollectorSupport.EnsureRows(ref rows, data.GetArrayLength(), request.MaximumRows);
            foreach (var bucket in data.EnumerateArray())
            {
                ValidateBucket(bucket);
                var results = CollectorJson.RequiredArray(bucket, "results");
                CollectorSupport.EnsureRows(ref rows, results.GetArrayLength(), request.MaximumRows);
                foreach (var result in results.EnumerateArray())
                {
                    uncached += CollectorJson.RequiredNonNegativeDecimal(result, "uncached_input_tokens");
                    cacheRead += CollectorJson.RequiredNonNegativeDecimal(result, "cache_read_input_tokens");
                    output += CollectorJson.RequiredNonNegativeDecimal(result, "output_tokens");
                    cacheWrite += OptionalCacheCreation(result);
                    webSearches += OptionalNestedNonNegative(result, "server_tool_use", "web_search_requests");
                }
            }
            var hasMore = RequiredBoolean(root, "has_more");
            page = OptionalCursor(root, "next_page");
            CollectorSupport.EnsureNextPage(hasMore, page, seen);
            if (!hasMore) return UsageObservations(request.Window, uncached, cacheWrite, cacheRead, output, webSearches);
        }
        throw FixedEndpointHttpClient.Failure(ProviderErrorCategory.PaginationLimit, "page-limit", "Anthropic usage pagination exceeded the configured page limit.", false);
    }

    private async Task<IReadOnlyList<UsageObservation>> ReadCostsAsync(string token, UsageCollectionRequest request, CancellationToken cancellationToken)
    {
        var totals = new Dictionary<string, decimal>(StringComparer.Ordinal);
        string? page = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = 0;

        for (var pageIndex = 0; pageIndex < request.MaximumPages; pageIndex++)
        {
            using var json = CollectorJson.Parse(await _http.GetJsonAsync(AnthropicUri(CostsPath, request.Window, page), message => Authorize(message, token), cancellationToken).ConfigureAwait(false));
            var root = RequireRoot(json.RootElement);
            var data = CollectorJson.RequiredArray(root, "data");
            CollectorSupport.EnsureRows(ref rows, data.GetArrayLength(), request.MaximumRows);
            foreach (var bucket in data.EnumerateArray())
            {
                ValidateBucket(bucket);
                var results = CollectorJson.RequiredArray(bucket, "results");
                CollectorSupport.EnsureRows(ref rows, results.GetArrayLength(), request.MaximumRows);
                foreach (var result in results.EnumerateArray())
                {
                    var amountText = CollectorJson.RequiredString(result, "amount", 64);
                    if (!decimal.TryParse(amountText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var lowestUnits) || lowestUnits < 0)
                        throw CollectorJson.Invalid("amount");
                    var currency = CollectorJson.RequiredString(result, "currency", 3).ToUpperInvariant();
                    if (currency.Length != 3 || currency.Any(character => character is < 'A' or > 'Z')) throw CollectorJson.Invalid("currency");
                    totals[currency] = totals.GetValueOrDefault(currency) + (lowestUnits / 100m);
                }
            }
            var hasMore = RequiredBoolean(root, "has_more");
            page = OptionalCursor(root, "next_page");
            CollectorSupport.EnsureNextPage(hasMore, page, seen);
            if (!hasMore) return CostObservations(request.Window, totals);
        }
        throw FixedEndpointHttpClient.Failure(ProviderErrorCategory.PaginationLimit, "page-limit", "Anthropic cost pagination exceeded the configured page limit.", false);
    }

    private static void Authorize(HttpRequestMessage message, string token)
    {
        message.Headers.Add("x-api-key", token);
        message.Headers.Add("anthropic-version", "2023-06-01");
    }

    private static Uri AnthropicUri(string path, ObservationWindow window, string? page) => CollectorUris.Build(Origin, path,
    [
        new("starting_at", window.Start.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
        new("ending_at", window.End.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
        new("bucket_width", "1d"),
        new("limit", "31"),
        new("page", page),
    ]);

    private static JsonElement RequireRoot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw CollectorJson.Invalid("root");
        return root;
    }

    private static void ValidateBucket(JsonElement bucket)
    {
        if (bucket.ValueKind != JsonValueKind.Object) throw CollectorJson.Invalid("bucket");
        _ = RequiredTimestamp(bucket, "starting_at");
        _ = RequiredTimestamp(bucket, "ending_at");
    }

    private static DateTimeOffset RequiredTimestamp(JsonElement parent, string name)
    {
        var value = CollectorJson.RequiredString(parent, name, 64);
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)) throw CollectorJson.Invalid(name);
        return parsed;
    }

    private static decimal OptionalCacheCreation(JsonElement result)
    {
        if (!result.TryGetProperty("cache_creation", out var cache) || cache.ValueKind == JsonValueKind.Null) return 0;
        if (cache.ValueKind != JsonValueKind.Object) throw CollectorJson.Invalid("cache_creation");
        return OptionalNonNegative(cache, "ephemeral_1h_input_tokens") + OptionalNonNegative(cache, "ephemeral_5m_input_tokens");
    }

    private static decimal OptionalNestedNonNegative(JsonElement result, string objectName, string valueName)
    {
        if (!result.TryGetProperty(objectName, out var nested) || nested.ValueKind == JsonValueKind.Null) return 0;
        if (nested.ValueKind != JsonValueKind.Object) throw CollectorJson.Invalid(objectName);
        return OptionalNonNegative(nested, valueName);
    }

    private static decimal OptionalNonNegative(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null) return 0;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDecimal(out var value) || value < 0) throw CollectorJson.Invalid(name);
        return value;
    }

    private static bool RequiredBoolean(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw CollectorJson.Invalid(name);
        return element.GetBoolean();
    }

    private static string? OptionalCursor(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null) return null;
        if (element.ValueKind != JsonValueKind.String) throw CollectorJson.Invalid(name);
        return element.GetString();
    }

    private static IReadOnlyList<UsageObservation> UsageObservations(
        ObservationWindow window, decimal uncached, decimal cacheWrite, decimal cacheRead, decimal output, decimal webSearches)
    {
        var collected = DateTimeOffset.UtcNow;
        var freshness = CollectorSupport.ReportedFreshness(collected, window.End, "Anthropic usage reporting is provider-aggregated and can lag recent requests.");
        const string source = "https://api.anthropic.com/v1/organizations/usage_report/messages";
        return
        [
            new(ObservationKind.Usage, "uncached-input-tokens", uncached, "tokens", null, window, source, ValueProvenance.ProviderReported, freshness),
            new(ObservationKind.Usage, "cache-write-input-tokens", cacheWrite, "tokens", null, window, source, ValueProvenance.ProviderReported, freshness),
            new(ObservationKind.Usage, "cache-read-input-tokens", cacheRead, "tokens", null, window, source, ValueProvenance.ProviderReported, freshness),
            new(ObservationKind.Usage, "output-tokens", output, "tokens", null, window, source, ValueProvenance.ProviderReported, freshness),
            new(ObservationKind.Usage, "web-search-requests", webSearches, "requests", null, window, source, ValueProvenance.ProviderReported, freshness),
        ];
    }

    private static IReadOnlyList<UsageObservation> CostObservations(ObservationWindow window, Dictionary<string, decimal> totals)
    {
        var collected = DateTimeOffset.UtcNow;
        var freshness = CollectorSupport.ReportedFreshness(collected, window.End,
            "Anthropic cost values are post-discount and pre-credit; Priority Tier costs are excluded, and reports may be revised.");
        return totals.OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => new UsageObservation(ObservationKind.Cost, "actual-cost", item.Value, "currency", item.Key, window,
                "https://api.anthropic.com/v1/organizations/cost_report", ValueProvenance.ProviderReported, freshness))
            .ToArray();
    }

    public void Dispose() => _http.Dispose();
}
