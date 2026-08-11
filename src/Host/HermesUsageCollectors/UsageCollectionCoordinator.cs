namespace HermesUsageCollectors;

public sealed class UsageCollectionCoordinator
{
    private readonly IReadOnlyDictionary<string, IUsageCollector> _collectors;

    public UsageCollectionCoordinator(IEnumerable<IUsageCollector> collectors)
    {
        ArgumentNullException.ThrowIfNull(collectors);
        _collectors = collectors.ToDictionary(item => item.ProviderId, StringComparer.Ordinal);
    }

    public async Task<AggregateCollectionResult> CollectAsync(
        IReadOnlyList<ProviderCredentialProfile> profiles,
        UsageCollectionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var tasks = profiles.Select(profile => CollectProfileSafelyAsync(profile, request, cancellationToken)).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        var failureExists = results.Any(result => result.Errors.Count > 0 ||
            result.Capabilities.Any(capability => capability.State is CapabilityState.Error or CapabilityState.PermissionDenied));

        var aggregates = results.SelectMany(result => result.Observations.Select(observation => (result, observation)))
            .GroupBy(item => new AggregateKey(item.observation.Kind, item.observation.Metric, item.observation.Unit,
                item.observation.Currency, item.observation.Window.Start, item.observation.Window.End,
                item.observation.Provenance, item.observation.Qualifier))
            .Select(group => new AggregateObservation(
                group.Key.Kind, group.Key.Metric, group.Sum(item => item.observation.Value), group.Key.Unit,
                group.Key.Currency, group.Key.Provenance, group.Key.Qualifier,
                new ObservationWindow(group.Key.Start, group.Key.End),
                group.Select(item => item.result.Profile.ProfileId).Distinct(StringComparer.Ordinal).Count(),
                group.Select(item => item.result.Profile.ProviderId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                failureExists))
            .OrderBy(item => item.Kind).ThenBy(item => item.Metric, StringComparer.Ordinal)
            .ThenBy(item => item.Currency, StringComparer.Ordinal).ToArray();

        return new AggregateCollectionResult(results, aggregates, DateTimeOffset.UtcNow, failureExists);
    }

    private async Task<ProviderCollectionResult> CollectProfileSafelyAsync(
        ProviderCredentialProfile profile, UsageCollectionRequest request, CancellationToken cancellationToken)
    {
        if (!_collectors.TryGetValue(profile.ProviderId, out var collector))
        {
            var error = CollectorSupport.Configuration("collector-unavailable", "No collector is registered for this provider.");
            return CollectorSupport.InvalidProfile(profile, error);
        }

        try
        {
            return await collector.CollectAsync(profile, request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            var error = new SanitizedProviderError(ProviderErrorCategory.Unexpected, "collector-failure",
                "The provider collector failed safely.", false);
            return new ProviderCollectionResult(profile.Metadata,
                [CollectorSupport.CapabilityFromError("collection", error)], [], [error], DateTimeOffset.UtcNow);
        }
    }

    private sealed record AggregateKey(
        ObservationKind Kind,
        string Metric,
        string Unit,
        string? Currency,
        DateTimeOffset Start,
        DateTimeOffset End,
        ValueProvenance Provenance,
        string? Qualifier);
}
