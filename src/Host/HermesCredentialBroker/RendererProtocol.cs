using System.Text.Json;

namespace HermesCredentialBroker;

public static class HermesConnectionsRendererProtocol
{
    public const int Version = HermesCredentialBrokerProtocol.Version;

    public static string SerializeMetadataResult(string requestId, IEnumerable<CredentialMetadata> entries)
    {
        var safeRequestId = ContractText.Identifier(requestId, nameof(requestId), 128);
        var result = new
        {
            type = "connections.list.result",
            version = Version,
            requestId = safeRequestId,
            entries = entries.Select(ToRendererMetadata).ToArray(),
        };
        return JsonSerializer.Serialize(result);
    }

    public static string SerializeReviewResult(string requestId, CredentialReviewTicket ticket)
    {
        var safeRequestId = ContractText.Identifier(requestId, nameof(requestId), 128);
        return JsonSerializer.Serialize(new
        {
            type = "connections.review.ready",
            version = Version,
            requestId = safeRequestId,
            review = new
            {
                reviewHandle = ticket.ReviewHandle,
                action = ticket.Action.ToString().ToLowerInvariant(),
                connectionRef = ticket.ConnectionRef?.Value,
                ticket.ProfileId,
                ticket.ProviderId,
                ticket.SlotId,
                ticket.ExpectedRevision,
                expiresAt = ticket.ExpiresAt,
            },
        });
    }

    public static object ToRendererMetadata(CredentialMetadata metadata) => new
    {
        connectionRef = metadata.ConnectionRef.Value,
        metadata.ProfileId,
        metadata.ProviderId,
        metadata.SlotId,
        authKind = AuthKind(metadata.AuthKind),
        sourceKind = SourceKind(metadata.SourceKind),
        metadata.Purposes,
        metadata.Revision,
        updatedAt = metadata.UpdatedAt,
        metadata.Configured,
    };

    private static string AuthKind(CredentialAuthKind kind) => kind switch
    {
        CredentialAuthKind.ApiKey => "api-key",
        CredentialAuthKind.OAuth => "oauth",
        CredentialAuthKind.DeviceCode => "device-code",
        _ => throw new CredentialBrokerException("invalid_auth_kind", "The credential authentication kind is invalid."),
    };

    private static string SourceKind(CredentialSourceKind kind) => kind switch
    {
        CredentialSourceKind.Native => "native",
        CredentialSourceKind.OAuth => "oauth",
        CredentialSourceKind.DeviceCode => "device-code",
        CredentialSourceKind.ExternalCli => "external-cli",
        _ => throw new CredentialBrokerException("invalid_source_kind", "The credential source kind is invalid."),
    };
}
