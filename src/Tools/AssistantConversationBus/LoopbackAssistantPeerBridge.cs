using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AssistantConversationBus;

public sealed class LoopbackAssistantPeerBridge : IAssistantPeerBridge, IDisposable
{
    private const string UnboundConversationId = "unbound";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 64,
    };
    private readonly AssistantPeerConnection _connection;
    private readonly HttpClient _http;
    private char[]? _token;

    public LoopbackAssistantPeerBridge(AssistantPeerConnection connection, HttpMessageHandler? handler = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        AssistantIdentityPolicy.RequireSender(connection.Identity);
        if (connection.Identity is AssistantIdentity.Chris or AssistantIdentity.Codex)
            throw new ArgumentException("Bus client identities cannot be registered as application peers.", nameof(connection));
        AssistantPeerSettings.RequireLoopback(connection.Endpoint);
        _token = connection.AuthenticationToken.ToCharArray();
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
        };
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public AssistantIdentity Identity => _connection.Identity;

    public async Task<AssistantParticipantStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var snapshot = await RequestSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return snapshot is null
            ? new AssistantParticipantStatus(Identity, false, false, _connection.Capability, null, null)
            : new AssistantParticipantStatus(Identity, true, snapshot.IsBusy, _connection.Capability, snapshot.ConversationId, snapshot.Generation);
    }

    public async Task<AssistantPeerReply> SendAsync(
        AssistantBusMessage message,
        CancellationToken cancellationToken)
    {
        if (message.Recipient != Identity && message.Recipient != AssistantIdentity.Everyone)
            return new AssistantPeerReply(AssistantDeliveryState.Rejected, Code: "recipient_mismatch");
        var before = await RequestSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (before is null) return new AssistantPeerReply(AssistantDeliveryState.Offline, Code: "peer_offline");
        if (before.IsBusy) return new AssistantPeerReply(AssistantDeliveryState.Busy, ConversationId: before.ConversationId, Generation: before.Generation, Code: "peer_busy");

        using var request = CreateRequest(HttpMethod.Post, "v1/turns");
        request.Content = JsonContent.Create(new { text = message.VisibleText }, options: JsonOptions);
        using var response = await SendAsync(request, AssistantBusLimits.PeerTurnTimeout, cancellationToken).ConfigureAwait(false);
        if (response is null) return new AssistantPeerReply(AssistantDeliveryState.Offline, Code: "peer_offline");
        if (response.StatusCode == HttpStatusCode.Conflict)
            return new AssistantPeerReply(AssistantDeliveryState.Busy, ConversationId: before.ConversationId, Generation: before.Generation, Code: "peer_busy");
        if (response.StatusCode == HttpStatusCode.GatewayTimeout)
            return new AssistantPeerReply(AssistantDeliveryState.TimedOut, ConversationId: before.ConversationId, Generation: before.Generation, Code: "peer_timeout");
        if (!response.IsSuccessStatusCode)
            return new AssistantPeerReply(AssistantDeliveryState.Rejected, ConversationId: before.ConversationId, Generation: before.Generation, Code: "peer_rejected");

        var after = await ReadSnapshotAsync(response, cancellationToken).ConfigureAwait(false);
        if (after is null)
            return new AssistantPeerReply(AssistantDeliveryState.Failed, ConversationId: before.ConversationId, Generation: before.Generation, Code: "peer_protocol_invalid");
        if (!string.Equals(before.ConversationId, UnboundConversationId, StringComparison.Ordinal)
            && !string.Equals(before.ConversationId, after.ConversationId, StringComparison.Ordinal))
            return new AssistantPeerReply(AssistantDeliveryState.StaleGeneration, ConversationId: after.ConversationId, Generation: after.Generation, Code: "peer_conversation_changed");
        if (!string.Equals(before.Generation, after.Generation, StringComparison.Ordinal))
            return new AssistantPeerReply(AssistantDeliveryState.StaleGeneration, ConversationId: after.ConversationId, Generation: after.Generation, Code: "peer_generation_changed");
        if (!message.ExpectsReply)
            return new AssistantPeerReply(AssistantDeliveryState.Delivered, ConversationId: after.ConversationId, Generation: after.Generation);

        var reply = after.Messages.LastOrDefault(candidate =>
            candidate.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase)
            && !before.MessageIds.Contains(candidate.Id)
            && !string.IsNullOrWhiteSpace(candidate.Text));
        return reply is null
            ? new AssistantPeerReply(AssistantDeliveryState.Failed, ConversationId: after.ConversationId, Generation: after.Generation, Code: "peer_reply_missing")
            : new AssistantPeerReply(AssistantDeliveryState.Completed, reply.Text, after.ConversationId, after.Generation);
    }

    public async Task<AssistantDeliveryState> ObserveAsync(
        AssistantBusMessage message,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, "v1/observe");
        request.Content = JsonContent.Create(new
        {
            messageId = message.MessageId,
            sender = message.Sender.ToString(),
            recipient = message.Recipient.ToString(),
            body = message.Body,
        }, options: JsonOptions);
        using var response = await SendAsync(request, AssistantBusLimits.PeerStatusTimeout, cancellationToken).ConfigureAwait(false);
        if (response is null) return AssistantDeliveryState.Offline;
        return response.IsSuccessStatusCode ? AssistantDeliveryState.Delivered : AssistantDeliveryState.Rejected;
    }

    private async Task<PeerSnapshot?> RequestSnapshotAsync(CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, "v1/session");
        using var response = await SendAsync(request, AssistantBusLimits.PeerStatusTimeout, cancellationToken).ConfigureAwait(false);
        return response is not null && response.IsSuccessStatusCode
            ? await ReadSnapshotAsync(response, cancellationToken).ConfigureAwait(false)
            : null;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        ObjectDisposedException.ThrowIf(_token is null, this);
        var request = new HttpRequestMessage(method, new Uri(_connection.Endpoint, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new string(_token));
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("AssistantConversationBus/1.0");
        return request;
    }

    private async Task<HttpResponseMessage?> SendAsync(
        HttpRequestMessage request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException) { return null; }
    }

    private static async Task<PeerSnapshot?> ReadSnapshotAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is null || !(mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase))) return null;
        var bytes = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryReadConversationId(root, out var conversationId)
                || !root.TryGetProperty("isBusy", out var busyElement)
                || busyElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !root.TryGetProperty("messages", out var messagesElement)
                || messagesElement.ValueKind != JsonValueKind.Array)
                return null;
            var messages = new List<PeerMessage>();
            foreach (var item in messagesElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !TryReadString(item, "id", out var id)
                    || !TryReadString(item, "role", out var role)
                    || !TryReadString(item, "text", out var text)) continue;
                messages.Add(new PeerMessage(id, role, text));
            }
            var generation = TryReadString(root, "generation", out var explicitGeneration)
                ? explicitGeneration
                : conversationId;
            return new PeerSnapshot(
                conversationId,
                busyElement.GetBoolean(),
                generation,
                messages,
                messages.Select(message => message.Id).ToHashSet(StringComparer.Ordinal));
        }
        catch (JsonException) { return null; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static bool TryReadString(JsonElement root, string property, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    private static bool TryReadConversationId(JsonElement root, out string value)
    {
        if (TryReadString(root, "conversationId", out value)
            || TryReadString(root, "sessionId", out value)) return true;
        if (root.TryGetProperty("state", out var state)
            && state.ValueKind == JsonValueKind.String
            && state.GetString() is "open"
            && root.TryGetProperty("sessionId", out var session)
            && session.ValueKind == JsonValueKind.Null)
        {
            value = UnboundConversationId;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long length && length > AssistantBusLimits.MaximumPeerResponseBytes)
            throw new InvalidDataException("Assistant peer response exceeded the bounded size.");
        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (destination.Length + read > AssistantBusLimits.MaximumPeerResponseBytes)
                throw new InvalidDataException("Assistant peer response exceeded the bounded size.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return destination.ToArray();
    }

    public void Dispose()
    {
        _http.Dispose();
        if (_token is null) return;
        CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_token.AsSpan()));
        _token = null;
    }

    private sealed record PeerMessage(string Id, string Role, string Text);
    private sealed record PeerSnapshot(
        string ConversationId,
        bool IsBusy,
        string Generation,
        IReadOnlyList<PeerMessage> Messages,
        IReadOnlySet<string> MessageIds);
}
