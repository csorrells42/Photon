using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HermesUsageCollectors;

public sealed partial class GoogleCloudBudgetCollector : IUsageCollector, IDisposable
{
    public const string BillingAccountSetting = "billingAccountId";
    private static readonly Uri Origin = new("https://billingbudgets.googleapis.com/");
    private static readonly TimeSpan MaximumWindow = TimeSpan.FromDays(366);
    private readonly IProviderSecretSource _secrets;
    private readonly FixedEndpointHttpClient _http;

    [GeneratedRegex("^[A-Za-z0-9]{6}-[A-Za-z0-9]{6}-[A-Za-z0-9]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex BillingAccountRegex();

    public GoogleCloudBudgetCollector(IProviderSecretSource secrets, HttpMessageHandler? handler = null, CollectorHttpPolicy? policy = null)
    {
        _secrets = secrets;
        _http = new FixedEndpointHttpClient(Origin, handler, policy);
    }

    public string ProviderId => UsageProviderIds.GoogleCloudBudgets;

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
            return CollectorSupport.InvalidProfile(profile, CollectorSupport.Configuration("invalid-window", "The Google Cloud collection window or bounds are invalid."));
        }

        var billingAccount = profile.Setting(BillingAccountSetting)?.Trim();
        if (billingAccount is null || !BillingAccountRegex().IsMatch(billingAccount))
            return CollectorSupport.InvalidProfile(profile, CollectorSupport.Configuration("billing-account-required", "A valid Google Cloud billing account reference is required by the trusted host."));

        var resolution = await CollectorSupport.ResolveSecretAsync(_secrets, profile.CredentialReference, cancellationToken).ConfigureAwait(false);
        if (resolution.Error is not null)
            return CollectorSupport.CredentialFailure(profile, resolution.Error, "budget-configuration");
        using var secret = resolution.Secret;
        if (secret is null)
        {
            return CollectorSupport.SetupRequired(profile,
                new CapabilityStatus("budget-configuration", CapabilityState.SetupRequired, "A Google OAuth access token with billing.budgets.list permission is required."),
                new CapabilityStatus("actual-spend", CapabilityState.SetupRequired, "Actual spend requires Cloud Billing export to BigQuery; the Budget API does not return it."));
        }

        var observations = new List<UsageObservation>();
        var capabilities = new List<CapabilityStatus>();
        var errors = new List<SanitizedProviderError>();
        var sawLastPeriod = false;
        try
        {
            sawLastPeriod = await ReadBudgetsAsync(secret.Materialize(), billingAccount, request, observations, cancellationToken).ConfigureAwait(false);
            capabilities.Add(new CapabilityStatus("budget-configuration", sawLastPeriod ? CapabilityState.Partial : CapabilityState.Supported,
                sawLastPeriod
                    ? "Budget configuration was returned; last-period-relative budgets have no numeric configured amount in this response."
                    : "Configured budget amounts and threshold rules were returned by the official Budget API."));
        }
        catch (CollectorFailureException exception)
        {
            errors.Add(exception.Error);
            capabilities.Add(CollectorSupport.CapabilityFromError("budget-configuration", exception.Error));
        }

        capabilities.Add(new CapabilityStatus("actual-spend", CapabilityState.SetupRequired,
            "The Budget API returns budget configuration, not actual spend. Use an explicitly configured Cloud Billing BigQuery export adapter."));
        capabilities.Add(new CapabilityStatus("gemini-per-key-usage", CapabilityState.Unavailable,
            "No supported official Google AI Studio/Gemini per-key usage feed is used by this collector."));
        return new ProviderCollectionResult(profile.Metadata, capabilities.ToArray(), observations.ToArray(), errors.ToArray(), DateTimeOffset.UtcNow);
    }

    private async Task<bool> ReadBudgetsAsync(
        string token, string billingAccount, UsageCollectionRequest request,
        List<UsageObservation> observations, CancellationToken cancellationToken)
    {
        string? page = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = 0;
        var sawLastPeriod = false;
        var budgetIndex = 0;

        for (var pageIndex = 0; pageIndex < request.MaximumPages; pageIndex++)
        {
            var path = $"/v1/billingAccounts/{Uri.EscapeDataString(billingAccount)}/budgets";
            var endpoint = CollectorUris.Build(Origin, path, [new("pageSize", "100"), new("pageToken", page)]);
            using var json = CollectorJson.Parse(await _http.GetJsonAsync(endpoint, message =>
            {
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }, cancellationToken).ConfigureAwait(false));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw CollectorJson.Invalid("root");
            var budgets = CollectorJson.RequiredArray(root, "budgets");
            CollectorSupport.EnsureRows(ref rows, budgets.GetArrayLength(), request.MaximumRows);
            foreach (var budget in budgets.EnumerateArray())
            {
                budgetIndex++;
                sawLastPeriod |= ParseBudget(budget, budgetIndex, request.Window, observations);
            }
            page = OptionalCursor(root, "nextPageToken");
            var hasMore = !string.IsNullOrEmpty(page);
            CollectorSupport.EnsureNextPage(hasMore, page, seen);
            if (!hasMore) return sawLastPeriod;
        }
        throw FixedEndpointHttpClient.Failure(ProviderErrorCategory.PaginationLimit, "page-limit", "Google Cloud budget pagination exceeded the configured page limit.", false);
    }

    private static bool ParseBudget(JsonElement budget, int index, ObservationWindow window, List<UsageObservation> observations)
    {
        if (budget.ValueKind != JsonValueKind.Object || !budget.TryGetProperty("amount", out var amount) || amount.ValueKind != JsonValueKind.Object)
            throw CollectorJson.Invalid("budget.amount");
        var collected = DateTimeOffset.UtcNow;
        var freshness = new ObservationFreshness(collected, null, FreshnessState.Current,
            "This is current budget configuration, not a measurement of actual spend.");
        var qualifier = BudgetPeriod(budget, index);
        var sawLastPeriod = false;

        if (amount.TryGetProperty("specifiedAmount", out var specified) && specified.ValueKind == JsonValueKind.Object)
        {
            var currency = CollectorJson.RequiredString(specified, "currencyCode", 3).ToUpperInvariant();
            if (currency.Length != 3 || currency.Any(character => character is < 'A' or > 'Z')) throw CollectorJson.Invalid("currencyCode");
            var units = OptionalMoneyUnits(specified);
            var nanos = OptionalNanos(specified);
            observations.Add(new UsageObservation(ObservationKind.Budget, "configured-budget-amount", units + nanos / 1_000_000_000m,
                "currency", currency, window, "https://billingbudgets.googleapis.com/v1/billingAccounts/*/budgets",
                ValueProvenance.ConfiguredThreshold, freshness, qualifier));
        }
        else if (amount.TryGetProperty("lastPeriodAmount", out var lastPeriod) && lastPeriod.ValueKind == JsonValueKind.Object)
        {
            sawLastPeriod = true;
        }
        else throw CollectorJson.Invalid("budget.amount");

        if (budget.TryGetProperty("thresholdRules", out var thresholds))
        {
            if (thresholds.ValueKind != JsonValueKind.Array) throw CollectorJson.Invalid("thresholdRules");
            foreach (var threshold in thresholds.EnumerateArray())
            {
                var percent = CollectorJson.RequiredNonNegativeDecimal(threshold, "thresholdPercent");
                var basis = threshold.TryGetProperty("spendBasis", out var basisValue) && basisValue.ValueKind == JsonValueKind.String
                    ? basisValue.GetString() : "CURRENT_SPEND";
                if (basis is not ("CURRENT_SPEND" or "FORECASTED_SPEND" or "BASIS_UNSPECIFIED")) throw CollectorJson.Invalid("spendBasis");
                observations.Add(new UsageObservation(ObservationKind.Budget, "configured-budget-threshold", percent, "ratio", null, window,
                    "https://billingbudgets.googleapis.com/v1/billingAccounts/*/budgets", ValueProvenance.ConfiguredThreshold,
                    freshness, $"{qualifier}; basis={basis}"));
            }
        }
        return sawLastPeriod;
    }

    private static string BudgetPeriod(JsonElement budget, int index)
    {
        var period = "MONTH";
        if (budget.TryGetProperty("budgetFilter", out var filter) && filter.ValueKind == JsonValueKind.Object)
        {
            if (filter.TryGetProperty("calendarPeriod", out var calendar) && calendar.ValueKind == JsonValueKind.String)
            {
                period = calendar.GetString() ?? "MONTH";
                if (period is not ("MONTH" or "QUARTER" or "YEAR" or "CALENDAR_PERIOD_UNSPECIFIED")) throw CollectorJson.Invalid("calendarPeriod");
            }
            else if (filter.TryGetProperty("customPeriod", out var custom) && custom.ValueKind == JsonValueKind.Object) period = "CUSTOM";
        }
        return $"configured-budget-{index.ToString(CultureInfo.InvariantCulture)}; period={period}";
    }

    private static decimal OptionalMoneyUnits(JsonElement specified)
    {
        if (!specified.TryGetProperty("units", out var units) || units.ValueKind == JsonValueKind.Null) return 0;
        if (units.ValueKind != JsonValueKind.String || !decimal.TryParse(units.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < 0)
            throw CollectorJson.Invalid("units");
        return value;
    }

    private static decimal OptionalNanos(JsonElement specified)
    {
        if (!specified.TryGetProperty("nanos", out var nanos) || nanos.ValueKind == JsonValueKind.Null) return 0;
        if (nanos.ValueKind != JsonValueKind.Number || !nanos.TryGetDecimal(out var value) || value is < 0 or > 999_999_999)
            throw CollectorJson.Invalid("nanos");
        return value;
    }

    private static string? OptionalCursor(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null) return null;
        if (element.ValueKind != JsonValueKind.String) throw CollectorJson.Invalid(name);
        return element.GetString();
    }

    public void Dispose() => _http.Dispose();
}
