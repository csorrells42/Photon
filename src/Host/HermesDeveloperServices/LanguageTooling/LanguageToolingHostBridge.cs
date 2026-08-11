namespace HermesDeveloperServices.LanguageTooling;

public sealed record LanguageToolingHostResponse(
    int Version,
    string RequestId,
    string ProviderId,
    string Operation,
    bool Succeeded,
    string Code,
    string SafeMessage,
    LanguageToolingProviderEvidence? Evidence = null,
    LanguageToolingOperationResult? Result = null);

/// <summary>
/// Thin host-facing coordinator. The desktop message switch only needs to deserialize one of the
/// typed request records and post this bounded response; it never accepts process configuration.
/// </summary>
public sealed class LanguageToolingHostBridge(LanguageToolingTrustedRegistry registry)
{
    private readonly LanguageToolingTrustedRegistry _registry = registry
        ?? throw new ArgumentNullException(nameof(registry));

    public async ValueTask<LanguageToolingHostResponse> HandleAsync(
        LanguageToolingHostRequest request,
        CancellationToken cancellationToken = default)
    {
        var requestId = request?.RequestId ?? string.Empty;
        var providerId = request?.ProviderId ?? string.Empty;
        var operation = request?.Operation ?? "invalid";
        try
        {
            var validated = LanguageToolingRequestPolicy.Validate(request!);
            if (validated is InspectLanguageToolingProviderRequest)
            {
                var evidence = await _registry.DescribeProviderAsync(validated.ProviderId, cancellationToken).ConfigureAwait(false);
                return new(
                    LanguageToolingProtocol.Version,
                    validated.RequestId,
                    validated.ProviderId,
                    validated.Operation,
                    true,
                    "ok",
                    "The trusted host inspected this provider.",
                    Evidence: evidence);
            }

            var result = await _registry.ExecuteAsync(validated, cancellationToken).ConfigureAwait(false);
            return new(
                LanguageToolingProtocol.Version,
                validated.RequestId,
                validated.ProviderId,
                validated.Operation,
                result.Succeeded,
                result.Code,
                result.SafeMessage,
                Result: result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(requestId, providerId, operation, "cancelled", "The language-tooling request was cancelled.");
        }
        catch (LanguageToolingRequestException exception)
        {
            return Failure(requestId, providerId, operation, exception.Code, exception.Message);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ArgumentException
            or System.Security.SecurityException)
        {
            return Failure(requestId, providerId, operation, "trusted-host-failure", "The trusted host could not complete the language-tooling request.");
        }
    }

    private static LanguageToolingHostResponse Failure(
        string requestId,
        string providerId,
        string operation,
        string code,
        string message) => new(
            LanguageToolingProtocol.Version,
            SafeIdentifier(requestId),
            SafeIdentifier(providerId),
            KnownPinnedToolchainEvidenceSource.SafeCode(operation, "invalid"),
            false,
            KnownPinnedToolchainEvidenceSource.SafeCode(code, "request-failed"),
            KnownPinnedToolchainEvidenceSource.SafeMessage(message, "The language-tooling request failed."));

    private static string SafeIdentifier(string value)
    {
        var cleaned = new string((value ?? string.Empty).Where(character => char.IsAsciiLetterOrDigit(character)
            || character is '.' or '_' or ':' or '-').ToArray());
        return cleaned[..Math.Min(cleaned.Length, 128)];
    }
}
