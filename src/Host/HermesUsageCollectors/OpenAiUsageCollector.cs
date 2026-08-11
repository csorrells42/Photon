using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace HermesUsageCollectors;

public sealed class OpenAiUsageCollector : IUsageCollector, IDisposable
{
    private const string UsagePath = "/v1/organization/usage/completions";
    private const string CostsPath = "/v1/organization/costs";
    private static readonly Uri Origin = new("https://api.openai.com/");
    private static readonly TimeSpan MaximumWindow = TimeSpan.FromDays(31);
    private readonly IProviderSecretSource _secrets;
    private readonly FixedEndpointHttpClient _http;

    public OpenAiUsageCollector(IProviderSecretSource secrets, HttpMessageHandler? handler = null, CollectorHttpPolicy? policy = null)
    {
        _secrets = secrets;
        _http = new FixedEndpointHttpClient(Origin, handler, policy);
    }

    public string ProviderId => UsageProviderIds.OpenAiApi;

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
            return CollectorSupport.InvalidProfile(profile, CollectorSupport.Configuration("invalid-window", "The OpenAI collection window or bounds are invalid."));
        }

        var resolution = await CollectorSupport.ResolveSecretAsync(_secrets, profile.CredentialReference, cancellationToken).ConfigureAwait(false);
        if (resolution.Error is not null)
            return CollectorSupport.CredentialFailure(profile, resolution.Error, "organization-usage", "organization-costs");
        using var secret = resolution.Secret;
        if (secret is null)
        {
            return CollectorSupport.SetupRequired(profile,
                new CapabilityStatus("organization-usage", CapabilityState.SetupRequired, "An OpenAI organization Admin API key is required."),
                new CapabilityStatus("organization-costs", CapabilityState.SetupRequired, "An OpenAI organization Admin API key is required."));
        }

        var token = secret.Materialize();
        var observations = new List<UsageObservation>();
        var capabilities = new List<CapabilityStatus>();
        var errors = new List<SanitizedProviderError>();

        await CaptureAsync("organization-usage", "OpenAI organization completion usage is available with an Admin API key.",
            () => ReadUsageAsync(token, request, cancellationToken), observations, capabilities, errors).ConfigureAwait(false);

        if (!cancellationToken.IsCancellationRequested)
        {
            await CaptureAsync("organization-costs", "OpenAI organization costs are reported by the Costs endpoint with an Admin API key.",
                () => ReadCostsAsync(token, request, cancellationToken), observations, capabilities, errors).ConfigureAwait(false);
        }

        return new ProviderCollectionResult(profile.Metadata, capabilities.ToArray(), observations.ToArray(), errors.ToArray(), DateTimeOffset.UtcNow);
    }

    private static async Task CaptureAsync(
        string capability,
        string successReason,
        Func<Task<IReadOnlyList<UsageObservation>>> collect,
        List<UsageObservation> observations,
        List<CapabilityStatus> capabilities,
        List<SanitizedProviderError> errors)
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

    private async Task<IReadOnlyList<UsageObservation>> ReadUsageAsync(
        string token,
        UsageCollectionRequest request,
        CancellationToken cancellationToken)
    {
        decimal inputTokens = 0;
        decimal outputTokens = 0;
        decimal requests = 0;
        var page = (string?)null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = 0;

        for (var pageIndex = 0; pageIndex < request.MaximumPages; pageIndex++)
        {
            var endpoint = OpenAiUri(UsagePath, request.Window, page);
            using var json = CollectorJson.Parse(await _http.GetJsonAsync(endpoint, message =>
            {
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }, cancellationToken).ConfigureAwait(false));

            var root = json.RootElement;
            RequirePage(root);
            var data = CollectorJson.RequiredArray(root, "data");
            CollectorSupport.EnsureRows(ref rows, data.GetArrayLength(), request.MaximumRows);
            foreach (var bucket in data.EnumerateArray())
            {
                ValidateBucket(bucket);
                var results = CollectorJson.RequiredArray(bucket, "results");
                CollectorSupport.EnsureRows(ref rows, results.GetArrayLength(), request.MaximumRows);
                foreach (var result in results.EnumerateArray())
                {
                    if (CollectorJson.RequiredString(result, "object") != "organization.usage.completions.result")
                        throw CollectorJson.Invalid("object");
                    inputTokens += CollectorJson.RequiredNonNegativeDecimal(result, "input_tokens");
                    outputTokens += CollectorJson.RequiredNonNegativeDecimal(result, "output_tokens");
                    requests += CollectorJson.RequiredNonNegativeDecimal(result, "num_model_requests");
                }
            }

            var hasMore = RequiredBoolean(root, "has_more");
            page = OptionalCursor(root, "next_page");
            CollectorSupport.EnsureNextPage(hasMore, page, seen);
            if (!hasMore) return UsageObservations(request.Window, inputTokens, outputTokens, requests);
        }

        throw FixedEndpointHttpClient.Failure(ProviderErrorCategory.PaginationLimit, "page-limit", "OpenAI usage pagination exceeded the configured page limit.", false);
    }

    private async Task<IReadOnlyList<UsageObservation>> ReadCostsAsync(
        string token,
        UsageCollectionRequest request,
        CancellationToken cancellationToken)
    {
        var totals = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var page = (string?)null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = 0;

        for (var pageIndex = 0; pageIndex < request.MaximumPages; pageIndex++)
        {
            var endpoint = OpenAiUri(CostsPath, request.Window, page);
            using var json = CollectorJson.Parse(await _http.GetJsonAsync(endpoint, message =>
            {
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }, cancellationToken).ConfigureAwait(false));

            var root = json.RootElement;
            RequirePage(root);
            var data = CollectorJson.RequiredArray(root, "data");
            CollectorSupport.EnsureRows(ref rows, data.GetArrayLength(), request.MaximumRows);
            foreach (var bucket in data.EnumerateArray())
            {
                ValidateBucket(bucket);
                var results = CollectorJson.RequiredArray(bucket, "results");
                CollectorSupport.EnsureRows(ref rows, results.GetArrayLength(), request.MaximumRows);
                foreach (var result in results.EnumerateArray())
                {
                    if (CollectorJson.RequiredString(result, "object") != "organization.costs.result"
                        || !result.TryGetProperty("amount", out var amount) || amount.ValueKind != JsonValueKind.Object)
                        throw CollectorJson.Invalid("amount");
                    var value = CollectorJson.RequiredNonNegativeDecimal(amount, "value");
                    var currency = CollectorJson.RequiredString(amount, "currency", 3).ToUpperInvariant();
                    if (currency.Length != 3 || currency.Any(character => character is < 'A' or > 'Z')) throw CollectorJson.Invalid("currency");
                    totals[currency] = totals.GetValueOrDefault(currency) + value;
                }
            }

            var hasMore = RequiredBoolean(root, "has_more");
            page = OptionalCursor(root, "next_page");
            CollectorSupport.EnsureNextPage(hasMore, page, seen);
            if (!hasMore) return CostObservations(request.Window, totals);
        }

        throw FixedEndpointHttpClient.Failure(ProviderErrorCategory.PaginationLimit, "page-limit", "OpenAI cost pagination exceeded the configured page limit.", false);
    }

    private static Uri OpenAiUri(string path, ObservationWindow window, string? page) => CollectorUris.Build(Origin, path,
    [
        new("start_time", window.Start.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
        new("end_time", window.End.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
        new("bucket_width", "1d"),
        new("limit", "31"),
        new("page", page),
    ]);

    private static void RequirePage(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || CollectorJson.RequiredString(root, "object") != "page") throw CollectorJson.Invalid("object");
    }

    private static void ValidateBucket(JsonElement bucket)
    {
        if (bucket.ValueKind != JsonValueKind.Object || CollectorJson.RequiredString(bucket, "object") != "bucket") throw CollectorJson.Invalid("bucket");
        _ = CollectorJson.RequiredUnixSeconds(bucket, "start_time");
        _ = CollectorJson.RequiredUnixSeconds(bucket, "end_time");
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

    private static IReadOnlyList<UsageObservation> UsageObservations(ObservationWindow window, decimal input, decimal output, decimal requests)
    {
        var collected = DateTimeOffset.UtcNow;
        var freshness = CollectorSupport.ReportedFreshness(collected, window.End, "OpenAI organization usage can arrive after the underlying API activity.");
        const string source = "https://api.openai.com/v1/organization/usage/completions";
        return
        [
            new(ObservationKind.Usage, "input-tokens", input, "tokens", null, window, source, ValueProvenance.ProviderReported, freshness),
            new(ObservationKind.Usage, "output-tokens", output, "tokens", null, window, source, ValueProvenance.ProviderReported, freshness),
            new(ObservationKind.Usage, "model-requests", requests, "requests", null, window, source, ValueProvenance.ProviderReported, freshness),
        ];
    }

    private static IReadOnlyList<UsageObservation> CostObservations(ObservationWindow window, Dictionary<string, decimal> totals)
    {
        var collected = DateTimeOffset.UtcNow;
        var freshness = CollectorSupport.ReportedFreshness(collected, window.End, "The OpenAI Costs endpoint reports organization costs; recent values may still settle.");
        return totals.OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => new UsageObservation(ObservationKind.Cost, "actual-cost", item.Value, "currency", item.Key, window,
                "https://api.openai.com/v1/organization/costs", ValueProvenance.ProviderReported, freshness))
            .ToArray();
    }

    public void Dispose() => _http.Dispose();
}
