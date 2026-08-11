namespace AssistantConversationBus;

public interface IAssistantPeerBridge
{
    AssistantIdentity Identity { get; }
    Task<AssistantParticipantStatus> GetStatusAsync(CancellationToken cancellationToken);
    Task<AssistantPeerReply> SendAsync(AssistantBusMessage message, CancellationToken cancellationToken);
    Task<AssistantDeliveryState> ObserveAsync(AssistantBusMessage message, CancellationToken cancellationToken);
}

public sealed class AssistantBusRouter
{
    private readonly IAssistantBusLedger _ledger;
    private readonly IReadOnlyDictionary<AssistantIdentity, IAssistantPeerBridge> _peers;
    private readonly SemaphoreSlim _deliveryGate = new(AssistantBusLimits.MaximumConcurrentDeliveries);

    public AssistantBusRouter(IAssistantBusLedger ledger, IEnumerable<IAssistantPeerBridge> peers)
    {
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        ArgumentNullException.ThrowIfNull(peers);
        var mapped = peers.ToDictionary(peer => peer.Identity);
        if (mapped.Keys.Any(identity => identity is AssistantIdentity.Codex or AssistantIdentity.Everyone))
            throw new ArgumentException("Only application assistant peers can be registered.", nameof(peers));
        _peers = mapped;
    }

    public async Task<AssistantBusSendResult> SendAsync(
        AssistantIdentity authenticatedSender,
        AssistantBusSendRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalized = AssistantMessagePolicy.Validate(authenticatedSender, request);
        var message = await _ledger.AppendAsync(
            authenticatedSender,
            normalized.Recipient,
            normalized.Body,
            normalized.ParentMessageId,
            normalized.ExpectsReply,
            originMessageId: null,
            cancellationToken).ConfigureAwait(false);

        var targets = ResolveTargets(authenticatedSender, normalized.Recipient);
        var observers = normalized.Recipient == AssistantIdentity.Everyone
            ? []
            : _peers.Keys
                .Where(identity => identity != authenticatedSender && identity != normalized.Recipient)
                .OrderBy(identity => identity)
                .ToArray();
        var observationTask = Task.WhenAll(observers.Select(observer => ObserveAsync(message, observer, cancellationToken)));
        var deliveries = await Task.WhenAll(targets.Select(target =>
            DeliverAsync(message, target, cancellationToken))).ConfigureAwait(false);
        await observationTask.ConfigureAwait(false);
        return new AssistantBusSendResult(message, deliveries);
    }

    public Task<IReadOnlyList<AssistantBusMessage>> ReadAsync(
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default) =>
        _ledger.ReadAsync(afterSequence, limit, cancellationToken);

    public Task<AssistantBusMessage?> FindAsync(
        string messageId,
        CancellationToken cancellationToken = default) =>
        _ledger.FindAsync(messageId, cancellationToken);

    public async Task<IReadOnlyList<AssistantParticipantStatus>> ParticipantsAsync(
        CancellationToken cancellationToken = default)
    {
        var statuses = new List<AssistantParticipantStatus>
        {
            new(AssistantIdentity.Chris, true, false, "user-relay", null, null),
            new(AssistantIdentity.Codex, true, false, "bus-client", null, null),
        };
        var peerStatuses = await Task.WhenAll(_peers.Values.Select(async peer =>
        {
            try { return await peer.GetStatusAsync(cancellationToken).ConfigureAwait(false); }
            catch { return new AssistantParticipantStatus(peer.Identity, false, false, "invoke-only", null, null); }
        })).ConfigureAwait(false);
        statuses.AddRange(peerStatuses.OrderBy(status => status.Identity));
        return statuses;
    }

    private IReadOnlyList<AssistantIdentity> ResolveTargets(AssistantIdentity sender, AssistantIdentity recipient)
    {
        if (recipient is AssistantIdentity.Chris or AssistantIdentity.Codex) return [];
        if (recipient != AssistantIdentity.Everyone) return [recipient];
        return _peers.Keys.Where(identity => identity != sender).OrderBy(identity => identity).ToArray();
    }

    private async Task<AssistantDeliveryResult> DeliverAsync(
        AssistantBusMessage message,
        AssistantIdentity recipient,
        CancellationToken cancellationToken)
    {
        var deliveryId = $"delivery:{Guid.NewGuid():N}";
        if (!_peers.TryGetValue(recipient, out var peer))
            return new AssistantDeliveryResult(deliveryId, recipient, AssistantDeliveryState.Offline, null, "peer_not_registered");
        AssistantBusMessage? replyMessage = null;
        AssistantDeliveryResult result;
        await _deliveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AssistantPeerReply reply;
            try { reply = await peer.SendAsync(message, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { return new AssistantDeliveryResult(deliveryId, recipient, AssistantDeliveryState.Failed, null, "peer_delivery_failed"); }

            string? replyMessageId = null;
            if (reply.State == AssistantDeliveryState.Completed && message.ExpectsReply && !string.IsNullOrWhiteSpace(reply.Body))
            {
                replyMessage = await _ledger.AppendAsync(
                    recipient,
                    message.Sender,
                    reply.Body.Trim(),
                    message.MessageId,
                    expectsReply: false,
                    message.OriginMessageId,
                    cancellationToken).ConfigureAwait(false);
                replyMessageId = replyMessage.MessageId;
            }
            result = new AssistantDeliveryResult(deliveryId, recipient, reply.State, replyMessageId, reply.Code);
        }
        finally
        {
            _deliveryGate.Release();
        }
        if (replyMessage is not null)
        {
            var observers = _peers.Keys
                .Where(identity => identity != replyMessage.Sender)
                .OrderBy(identity => identity)
                .ToArray();
            await Task.WhenAll(observers.Select(observer =>
                ObserveAsync(replyMessage, observer, cancellationToken))).ConfigureAwait(false);
        }
        return result;
    }

    private async Task ObserveAsync(
        AssistantBusMessage message,
        AssistantIdentity observer,
        CancellationToken cancellationToken)
    {
        if (!_peers.TryGetValue(observer, out var peer)) return;
        await _deliveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try { _ = await peer.ObserveAsync(message, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { }
        }
        finally
        {
            _deliveryGate.Release();
        }
    }
}
