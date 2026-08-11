using System.Text.RegularExpressions;

namespace HermesUsageCollectors;

public sealed record GoogleBillingExportCostRow(
    decimal Cost,
    decimal Credits,
    string Currency,
    DateTimeOffset UsageStart,
    DateTimeOffset UsageEnd,
    DateTimeOffset? ExportedAt);

public sealed record GoogleBillingExportPage(
    IReadOnlyList<GoogleBillingExportCostRow> Rows,
    string? NextPageToken);

/// <summary>
/// Host boundary for an authenticated, parameterized BigQuery billing-export query.
/// Implementations own Google authentication and must never return query text, project IDs,
/// billing-account IDs, labels, or raw export rows to the collector.
/// </summary>
public interface IGoogleBillingExportSource
{
    ValueTask<GoogleBillingExportPage> QueryCostsAsync(
        string targetReference,
        ObservationWindow window,
        string? pageToken,
        int maximumRows,
        CancellationToken cancellationToken);
}

public sealed partial class GoogleBillingExportCollector : IUsageCollector
{
    public const string TargetReferenceSetting = "targetReference";
    private const string ActualCostCapability = "actual-cost-from-billing-export";
    private static readonly TimeSpan MaximumWindow = TimeSpan.FromDays(366);
    private readonly IGoogleBillingExportSource? _source;

    public GoogleBillingExportCollector(IGoogleBillingExportSource? source = null) => _source = source;

    public string ProviderId => UsageProviderIds.GoogleCloudBillingExport;

    public async Task<ProviderCollectionResult> CollectAsync(
        ProviderCredentialProfile profile,
        UsageCollectionRequest request,
        CancellationToken cancellationToken = default)
    {
        var invalid = CollectorSupport.ValidateProfile(profile, ProviderId);
        if (invalid is not null) return CollectorSupport.InvalidProfile(profile, invalid);

        try { request.Validate(MaximumWindow); }
        catch (ArgumentException)
        {
            return CollectorSupport.InvalidProfile(profile,
                CollectorSupport.Configuration("invalid-request", "The collection request is outside supported bounds."));
        }

        var targetReference = profile.Setting(TargetReferenceSetting);
        if (_source is null || string.IsNullOrWhiteSpace(targetReference))
        {
            return new ProviderCollectionResult(profile.Metadata,
                [new CapabilityStatus(ActualCostCapability, CapabilityState.SetupRequired,
                    "A host-managed BigQuery billing-export source and opaque target reference are required.")],
                [], [], DateTimeOffset.UtcNow);
        }

        if (!SafeReferenceRegex().IsMatch(targetReference))
        {
            return CollectorSupport.InvalidProfile(profile,
                CollectorSupport.Configuration("invalid-target-reference", "The billing-export target reference is invalid."));
        }

        var collectedAt = DateTimeOffset.UtcNow;
        try
        {
            var rows = 0;
            var pages = 0;
            string? pageToken = null;
            var seenPages = new HashSet<string>(StringComparer.Ordinal);
            var totals = new Dictionary<string, decimal>(StringComparer.Ordinal);
            DateTimeOffset? dataThrough = null;

            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++pages > request.MaximumPages)
                    throw FixedEndpointHttpClient.Failure(ProviderErrorCategory.PaginationLimit, "page-limit",
                        "The billing export exceeded the configured page limit.", false);

                var page = await _source.QueryCostsAsync(targetReference, request.Window, pageToken,
                    request.MaximumRows - rows, cancellationToken).ConfigureAwait(false);
                if (page.Rows is null)
                    throw FixedEndpointHttpClient.Failure(ProviderErrorCategory.MalformedResponse, "missing-rows",
                        "The billing export returned an invalid page.", false);

                CollectorSupport.EnsureRows(ref rows, page.Rows.Count, request.MaximumRows);
                foreach (var row in page.Rows)
                {
                    if (!CurrencyRegex().IsMatch(row.Currency) || row.UsageEnd <= row.UsageStart ||
                        row.UsageStart < request.Window.Start || row.UsageEnd > request.Window.End)
                        throw FixedEndpointHttpClient.Failure(ProviderErrorCategory.MalformedResponse, "invalid-row",
                            "The billing export returned an invalid normalized row.", false);

                    var currency = row.Currency.ToUpperInvariant();
                    totals[currency] = totals.GetValueOrDefault(currency) + row.Cost + row.Credits;
                    if (dataThrough is null || row.UsageEnd > dataThrough) dataThrough = row.UsageEnd;
                }

                pageToken = page.NextPageToken;
                if (pageToken is not null && (pageToken.Length is 0 or > 1024 || !seenPages.Add(pageToken)))
                    throw FixedEndpointHttpClient.Failure(ProviderErrorCategory.MalformedResponse, "invalid-pagination",
                        "The billing export returned an invalid pagination cursor.", false);
            } while (pageToken is not null);

            var observations = totals.Select(pair => new UsageObservation(
                ObservationKind.Cost, "actual-cost", pair.Value, "currency", pair.Key,
                request.Window, "google-cloud-bigquery-billing-export", ValueProvenance.ProviderReported,
                new ObservationFreshness(collectedAt, dataThrough, FreshnessState.Delayed,
                    "Billing export data can arrive late or be revised; the provider does not offer a latency SLA."),
                "Cost plus credits from normalized export rows.")).ToArray();

            return new ProviderCollectionResult(profile.Metadata,
                [new CapabilityStatus(ActualCostCapability, CapabilityState.Supported,
                    "Actual cloud spend is sourced from a host-managed BigQuery billing export query.")],
                observations, [], collectedAt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var error = new SanitizedProviderError(ProviderErrorCategory.Cancelled, "cancelled",
                "Collection was cancelled.", false);
            return Failure(profile, collectedAt, error);
        }
        catch (CollectorFailureException exception)
        {
            return Failure(profile, collectedAt, exception.Error);
        }
        catch (Exception)
        {
            var error = new SanitizedProviderError(ProviderErrorCategory.Unexpected, "adapter-failure",
                "The billing-export adapter failed safely.", false);
            return Failure(profile, collectedAt, error);
        }
    }

    private static ProviderCollectionResult Failure(
        ProviderCredentialProfile profile, DateTimeOffset collectedAt, SanitizedProviderError error) =>
        new(profile.Metadata, [CollectorSupport.CapabilityFromError(ActualCostCapability, error)], [], [error], collectedAt);

    [GeneratedRegex("^[A-Za-z0-9._-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeReferenceRegex();

    [GeneratedRegex("^[A-Za-z]{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex CurrencyRegex();
}
