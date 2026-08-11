using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HermesUsageCollectors;

/// <summary>
/// Reads project-level Gemini Developer API telemetry from Google Cloud Monitoring.
/// Authentication and project selection remain trusted-host concerns. The collector
/// never accepts a caller-selected endpoint or metric filter and never returns Google
/// project identifiers, API-key labels, or raw provider labels to the renderer.
/// </summary>
public sealed partial class GoogleGeminiMonitoringCollector : IUsageCollector, IDisposable
{
    public const string ProjectSetting = "monitoringProject";

    private const string Source = "https://monitoring.googleapis.com/v3/projects/*/timeSeries";
    private const string AggregateQualifier =
        "Google Cloud project aggregate; not API-key attributed; public BETA meters only.";
    private static readonly Uri Origin = new("https://monitoring.googleapis.com/");
    private static readonly TimeSpan MaximumWindow = TimeSpan.FromDays(31);
    private static readonly MeterDefinition[] Meters =
    [
        new("generativelanguage.googleapis.com/generate_content_usage_output_token_count", "output-tokens", "tokens"),
        new("generativelanguage.googleapis.com/quota/generate_content_free_tier_input_token_count/usage", "input-tokens", "tokens"),
        new("generativelanguage.googleapis.com/quota/generate_content_paid_tier_input_token_count/usage", "input-tokens", "tokens"),
        new("generativelanguage.googleapis.com/quota/generate_content_free_tier_requests/usage", "requests", "requests"),
        new("generativelanguage.googleapis.com/quota/generate_requests_per_model/usage", "requests", "requests"),
    ];

    private readonly IProviderSecretSource _secrets;
    private readonly FixedEndpointHttpClient _http;

    [GeneratedRegex("^(?:[a-z][a-z0-9-]{4,28}[a-z0-9]|[0-9]{6,20})$", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectRegex();

    public GoogleGeminiMonitoringCollector(
        IProviderSecretSource secrets,
        HttpMessageHandler? handler = null,
        CollectorHttpPolicy? policy = null)
    {
        _secrets = secrets;
        _http = new FixedEndpointHttpClient(Origin, handler, policy);
    }

    public string ProviderId => UsageProviderIds.GoogleAiStudio;

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
            return CollectorSupport.InvalidProfile(profile,
                CollectorSupport.Configuration("invalid-window", "The Google Monitoring collection window or bounds are invalid."));
        }

        var project = profile.Setting(ProjectSetting)?.Trim();
        if (project is null || !ProjectRegex().IsMatch(project))
        {
            return CollectorSupport.InvalidProfile(profile,
                CollectorSupport.Configuration("monitoring-project-required",
                    "A valid Google Cloud project reference is required by the trusted host."));
        }

        var resolution = await CollectorSupport.ResolveSecretAsync(
            _secrets, profile.CredentialReference, cancellationToken).ConfigureAwait(false);
        if (resolution.Error is not null)
            return CollectorSupport.CredentialFailure(profile, resolution.Error, "project-usage");

        using var secret = resolution.Secret;
        if (secret is null)
        {
            return CollectorSupport.SetupRequired(profile,
                new CapabilityStatus("project-usage", CapabilityState.SetupRequired,
                    "A host-managed Google OAuth or ADC access token with monitoring.read permission is required."),
                PerKeyUnavailable(),
                SpendRequiresBillingExport());
        }

        var observations = new List<UsageObservation>();
        var errors = new List<SanitizedProviderError>();
        try
        {
            await ReadMetersAsync(secret.Materialize(), project, request, observations, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CollectorFailureException exception)
        {
            errors.Add(exception.Error);
        }

        var capabilities = new List<CapabilityStatus>
        {
            errors.Count == 0
                ? new CapabilityStatus("project-usage", CapabilityState.Partial,
                    "Project-level Gemini usage is available from fixed public BETA Cloud Monitoring meters; alpha tier-specific meters are intentionally excluded.")
                : CollectorSupport.CapabilityFromError("project-usage", errors[0]),
            PerKeyUnavailable(),
            SpendRequiresBillingExport(),
        };
        return new ProviderCollectionResult(
            profile.Metadata, capabilities, observations, errors, DateTimeOffset.UtcNow);
    }

    private async Task ReadMetersAsync(
        string token,
        string project,
        UsageCollectionRequest request,
        List<UsageObservation> observations,
        CancellationToken cancellationToken)
    {
        var totals = new Dictionary<string, MeterTotal>(StringComparer.Ordinal);
        var totalRows = 0;

        foreach (var meter in Meters)
        {
            var result = await ReadMeterAsync(token, project, meter, request, cancellationToken, totalRows)
                .ConfigureAwait(false);
            totalRows = result.TotalRows;
            if (!result.SawPoint) continue;

            if (!totals.TryGetValue(meter.ResultMetric, out var total))
                total = new MeterTotal(0, null, meter.Unit);
            totals[meter.ResultMetric] = total with
            {
                Value = total.Value + result.Value,
                DataThrough = Latest(total.DataThrough, result.DataThrough),
            };
        }

        var collectedAt = DateTimeOffset.UtcNow;
        foreach (var (metric, total) in totals)
        {
            observations.Add(new UsageObservation(
                ObservationKind.Usage,
                metric,
                total.Value,
                total.Unit,
                null,
                request.Window,
                Source,
                ValueProvenance.ProviderReported,
                new ObservationFreshness(
                    collectedAt,
                    total.DataThrough,
                    FreshnessState.Delayed,
                    "Cloud Monitoring telemetry can arrive after the underlying Gemini request."),
                AggregateQualifier));
        }
    }

    private async Task<MeterReadResult> ReadMeterAsync(
        string token,
        string project,
        MeterDefinition meter,
        UsageCollectionRequest request,
        CancellationToken cancellationToken,
        int initialRows)
    {
        string? page = null;
        var seenPages = new HashSet<string>(StringComparer.Ordinal);
        var rows = initialRows;
        var value = 0m;
        DateTimeOffset? dataThrough = null;
        var sawPoint = false;

        for (var pageIndex = 0; pageIndex < request.MaximumPages; pageIndex++)
        {
            var endpoint = CollectorUris.Build(
                Origin,
                $"/v3/projects/{Uri.EscapeDataString(project)}/timeSeries",
                [
                    new("filter", $"metric.type = \"{meter.ProviderMetric}\""),
                    new("interval.startTime", Rfc3339(request.Window.Start)),
                    new("interval.endTime", Rfc3339(request.Window.End)),
                    new("view", "FULL"),
                    new("pageSize", Math.Min(request.MaximumRows, 1000).ToString(CultureInfo.InvariantCulture)),
                    new("pageToken", page),
                ]);

            using var json = CollectorJson.Parse(await _http.GetJsonAsync(endpoint, message =>
            {
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }, cancellationToken).ConfigureAwait(false));

            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw CollectorJson.Invalid("root");
            if (root.TryGetProperty("timeSeries", out var series))
            {
                if (series.ValueKind != JsonValueKind.Array) throw CollectorJson.Invalid("timeSeries");
                foreach (var item in series.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) throw CollectorJson.Invalid("timeSeries item");
                    if (!item.TryGetProperty("points", out var points) || points.ValueKind != JsonValueKind.Array)
                        throw CollectorJson.Invalid("points");
                    CollectorSupport.EnsureRows(ref rows, points.GetArrayLength(), request.MaximumRows);
                    foreach (var point in points.EnumerateArray())
                    {
                        value += PointValue(point);
                        dataThrough = Latest(dataThrough, PointEnd(point));
                        sawPoint = true;
                    }
                }
            }

            page = OptionalCursor(root, "nextPageToken");
            var hasMore = !string.IsNullOrEmpty(page);
            CollectorSupport.EnsureNextPage(hasMore, page, seenPages);
            if (!hasMore) return new MeterReadResult(value, dataThrough, sawPoint, rows);
        }

        throw FixedEndpointHttpClient.Failure(
            ProviderErrorCategory.PaginationLimit,
            "page-limit",
            "Google Monitoring pagination exceeded the configured page limit.",
            false);
    }

    private static decimal PointValue(JsonElement point)
    {
        if (point.ValueKind != JsonValueKind.Object || !point.TryGetProperty("value", out var value)
            || value.ValueKind != JsonValueKind.Object)
            throw CollectorJson.Invalid("point.value");

        if (value.TryGetProperty("int64Value", out var integer) && integer.ValueKind == JsonValueKind.String
            && decimal.TryParse(integer.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInteger)
            && parsedInteger >= 0)
            return parsedInteger;

        if (value.TryGetProperty("doubleValue", out var number) && number.ValueKind == JsonValueKind.Number
            && number.TryGetDecimal(out var parsedNumber) && parsedNumber >= 0)
            return parsedNumber;

        throw CollectorJson.Invalid("point.value");
    }

    private static DateTimeOffset PointEnd(JsonElement point)
    {
        if (!point.TryGetProperty("interval", out var interval) || interval.ValueKind != JsonValueKind.Object
            || !interval.TryGetProperty("endTime", out var end) || end.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(end.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            throw CollectorJson.Invalid("point.interval.endTime");
        return parsed;
    }

    private static DateTimeOffset? Latest(DateTimeOffset? first, DateTimeOffset? second) =>
        first is null ? second : second is null || first >= second ? first : second;

    private static string Rfc3339(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static string? OptionalCursor(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null) return null;
        if (element.ValueKind != JsonValueKind.String) throw CollectorJson.Invalid(name);
        return element.GetString();
    }

    private static CapabilityStatus PerKeyUnavailable() =>
        new("gemini-per-key-usage", CapabilityState.Unavailable,
            "Cloud Monitoring reports project aggregates and does not provide an API-key attribution label.");

    private static CapabilityStatus SpendRequiresBillingExport() =>
        new("actual-spend", CapabilityState.SetupRequired,
            "Actual Google spend requires a separately configured Cloud Billing export to BigQuery.");

    public void Dispose() => _http.Dispose();

    private sealed record MeterDefinition(string ProviderMetric, string ResultMetric, string Unit);
    private sealed record MeterTotal(decimal Value, DateTimeOffset? DataThrough, string Unit);
    private sealed record MeterReadResult(decimal Value, DateTimeOffset? DataThrough, bool SawPoint, int TotalRows);
}
