using System.Text.RegularExpressions;

namespace HermesUsageCollectors;

internal static partial class CollectorSupport
{
    [GeneratedRegex("^[A-Za-z0-9._-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeProfileIdRegex();

    internal static SanitizedProviderError? ValidateProfile(ProviderCredentialProfile profile, string expectedProvider)
    {
        if (!string.Equals(profile.ProviderId, expectedProvider, StringComparison.Ordinal))
            return Configuration("provider-mismatch", "The credential profile targets a different provider.");
        if (!SafeProfileIdRegex().IsMatch(profile.ProfileId) || string.IsNullOrWhiteSpace(profile.DisplayName) || profile.DisplayName.Length > 128)
            return Configuration("invalid-profile", "The credential profile metadata is invalid.");
        if (string.IsNullOrWhiteSpace(profile.CredentialReference) || profile.CredentialReference.Length > 256)
            return Configuration("invalid-credential-reference", "The credential reference is invalid.");
        return null;
    }

    internal static ProviderCollectionResult InvalidProfile(ProviderCredentialProfile profile, SanitizedProviderError error) =>
        new(profile.Metadata,
            [new CapabilityStatus("collection", CapabilityState.Error, error.Message)],
            [], [error], DateTimeOffset.UtcNow);

    internal static ProviderCollectionResult SetupRequired(ProviderCredentialProfile profile, params CapabilityStatus[] additional) =>
        new(profile.Metadata,
            [new CapabilityStatus("authentication", CapabilityState.SetupRequired, "A host-managed credential is required."), .. additional],
            [], [], DateTimeOffset.UtcNow);

    internal static async ValueTask<(ProviderSecret? Secret, SanitizedProviderError? Error)> ResolveSecretAsync(
        IProviderSecretSource source, string credentialReference, CancellationToken cancellationToken)
    {
        try
        {
            return (await source.GetSecretAsync(credentialReference, cancellationToken).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (null, new SanitizedProviderError(ProviderErrorCategory.Cancelled, "cancelled",
                "Collection was cancelled.", false));
        }
        catch (Exception)
        {
            return (null, new SanitizedProviderError(ProviderErrorCategory.Unexpected, "credential-source-failure",
                "The trusted host could not resolve the credential safely.", false));
        }
    }

    internal static ProviderCollectionResult CredentialFailure(
        ProviderCredentialProfile profile, SanitizedProviderError error, params string[] capabilities) =>
        new(profile.Metadata, capabilities.Select(capability => CapabilityFromError(capability, error)).ToArray(),
            [], [error], DateTimeOffset.UtcNow);

    internal static CapabilityStatus CapabilityFromError(string capability, SanitizedProviderError error) =>
        new(capability, error.Category switch
        {
            ProviderErrorCategory.Unauthorized or ProviderErrorCategory.Forbidden => CapabilityState.PermissionDenied,
            ProviderErrorCategory.Configuration => CapabilityState.SetupRequired,
            _ => CapabilityState.Error,
        }, error.Message);

    internal static SanitizedProviderError Configuration(string code, string message) =>
        new(ProviderErrorCategory.Configuration, code, message, false);

    internal static ObservationFreshness ReportedFreshness(DateTimeOffset collectedAt, DateTimeOffset dataThrough, string note) =>
        new(collectedAt, dataThrough, FreshnessState.Delayed, note);

    internal static void EnsureRows(ref int rows, int added, int maximumRows)
    {
        checked { rows += added; }
        if (rows > maximumRows)
            throw FixedEndpointHttpClient.Failure(ProviderErrorCategory.PaginationLimit, "row-limit", "The provider response exceeded the configured row limit.", false);
    }

    internal static void EnsureNextPage(bool hasMore, string? nextPage, HashSet<string> seenPages)
    {
        if (!hasMore) return;
        if (string.IsNullOrWhiteSpace(nextPage) || nextPage.Length > 1024 || !seenPages.Add(nextPage))
            throw FixedEndpointHttpClient.Failure(ProviderErrorCategory.MalformedResponse, "invalid-pagination", "The provider returned an invalid pagination cursor.", false);
    }
}
