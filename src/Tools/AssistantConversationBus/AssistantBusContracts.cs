using System.Text.Json.Serialization;

namespace AssistantConversationBus;

[JsonConverter(typeof(JsonStringEnumConverter<AssistantIdentity>))]
public enum AssistantIdentity
{
    Chris,
    Codex,
    Photon,
    Ali,
    Scarlett,
    Everyone,
}

public static class AssistantIdentityPolicy
{
    public static readonly IReadOnlyList<AssistantIdentity> Peers =
        [AssistantIdentity.Chris, AssistantIdentity.Codex, AssistantIdentity.Photon, AssistantIdentity.Ali, AssistantIdentity.Scarlett];

    public static bool TryParse(string? value, out AssistantIdentity identity)
    {
        identity = default;
        var text = value?.Trim();
        return text is not null
            && Enum.TryParse(text, ignoreCase: true, out identity)
            && Enum.IsDefined(identity);
    }

    public static void RequireSender(AssistantIdentity sender)
    {
        if (sender == AssistantIdentity.Everyone || !Enum.IsDefined(sender))
            throw new AssistantBusValidationException("invalid_sender", "The bus sender is not a known assistant identity.");
    }

    public static void RequireRecipient(AssistantIdentity sender, AssistantIdentity recipient)
    {
        if (!Enum.IsDefined(recipient))
            throw new AssistantBusValidationException("invalid_recipient", "The bus recipient is not known.");
        if (sender == recipient)
            throw new AssistantBusValidationException("self_address_rejected", "An assistant cannot address itself through the bus.");
    }
}

public sealed record AssistantBusSendRequest(
    string Recipient,
    string Body,
    string? ParentMessageId = null,
    bool ExpectsReply = true);

public sealed record AssistantBusMessage(
    int ProtocolVersion,
    string RoomId,
    long Sequence,
    string MessageId,
    AssistantIdentity Sender,
    AssistantIdentity Recipient,
    string Body,
    DateTimeOffset CreatedAtUtc,
    string? ParentMessageId,
    bool ExpectsReply,
    string OriginMessageId)
{
    public string Header => $"{Sender}->{Recipient}";
    public string VisibleText => $"{Header}\n\n{Body}";
}

public enum AssistantDeliveryState
{
    Completed,
    Delivered,
    Offline,
    Busy,
    StaleGeneration,
    Rejected,
    TimedOut,
    Failed,
}

public sealed record AssistantPeerReply(
    AssistantDeliveryState State,
    string? Body = null,
    string? ConversationId = null,
    string? Generation = null,
    string? Code = null);

public sealed record AssistantDeliveryResult(
    string DeliveryId,
    AssistantIdentity Recipient,
    AssistantDeliveryState State,
    string? ReplyMessageId,
    string? Code);

public sealed record AssistantBusSendResult(
    AssistantBusMessage Message,
    IReadOnlyList<AssistantDeliveryResult> Deliveries);

public sealed record AssistantParticipantStatus(
    AssistantIdentity Identity,
    bool Online,
    bool Busy,
    string Capability,
    string? ConversationId,
    string? Generation);

public sealed class AssistantBusValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class AssistantBusLimits
{
    public const int ProtocolVersion = 1;
    public const int MaximumBodyCharacters = 64 * 1024;
    public const int MaximumRoomMessages = 10_000;
    public const int MaximumReadMessages = 500;
    public const int MaximumConcurrentDeliveries = 3;
    public const int MaximumConcurrentWaits = 64;
    public const int MaximumServiceRequestBytes = 128 * 1024;
    public const int DefaultServicePort = 9072;
    public const int MaximumPeerResponseBytes = 4 * 1024 * 1024;
    public static readonly TimeSpan PeerStatusTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan PeerTurnTimeout = TimeSpan.FromMinutes(31);
}
