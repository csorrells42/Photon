namespace AssistantConversationBus;

public static class AssistantMessagePolicy
{
    public static (AssistantIdentity Recipient, string Body, string? ParentMessageId, bool ExpectsReply) Validate(
        AssistantIdentity authenticatedSender,
        AssistantBusSendRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        AssistantIdentityPolicy.RequireSender(authenticatedSender);
        if (!AssistantIdentityPolicy.TryParse(request.Recipient, out var recipient))
            throw new AssistantBusValidationException("invalid_recipient", "The intended recipient must be Codex, Photon, Ali, Scarlett, or Everyone.");
        AssistantIdentityPolicy.RequireRecipient(authenticatedSender, recipient);

        var body = request.Body?.Trim() ?? string.Empty;
        if (body.Length == 0)
            throw new AssistantBusValidationException("empty_body", "The bus message body is empty.");
        if (body.Length > AssistantBusLimits.MaximumBodyCharacters)
            throw new AssistantBusValidationException("body_too_large", "The bus message exceeds the 64 KiB text limit.");
        if (LooksLikeIdentityHeader(body.Split('\n', 2)[0]))
            throw new AssistantBusValidationException(
                "embedded_identity_header_rejected",
                "Do not put a sender header in the body; the authenticated bus stamps Sender->Recipient.");

        var parent = string.IsNullOrWhiteSpace(request.ParentMessageId)
            ? null
            : RequireIdentifier(request.ParentMessageId, "parent_message_id_invalid");
        return (recipient, body, parent, request.ExpectsReply);
    }

    public static string RequireIdentifier(string value, string code)
    {
        var normalized = value.Trim();
        if (normalized.Length is < 8 or > 160
            || normalized.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is ':' or '-' or '_')))
            throw new AssistantBusValidationException(code, "The bus identifier is malformed.");
        return normalized;
    }

    private static bool LooksLikeIdentityHeader(string firstLine)
    {
        var separator = firstLine.IndexOf("->", StringComparison.Ordinal);
        if (separator <= 0) return false;
        return AssistantIdentityPolicy.TryParse(firstLine[..separator], out _)
            || AssistantIdentityPolicy.TryParse(firstLine[(separator + 2)..], out _);
    }
}
