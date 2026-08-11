using System.Text.Json;

namespace PhotonCadArtifacts;

internal sealed class PhotonCadArtifactProtocolBridge : IAsyncDisposable
{
    private readonly PhotonCadArtifactStorage _storage;
    private readonly PhotonCadArtifactReleaseCoordinator _coordinator;
    private readonly IPhotonCadArtifactDestinationAuthority _destinations;
    private readonly Action<object> _post;
    private readonly TimeSpan _requestTimeout;
    private readonly PhotonCadArtifactProtocolLimits _limits;
    private readonly PhotonCadMonotonicClock _clock;
    private readonly Dictionary<string, ActiveRequest> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<PhotonCadArtifactContext, int> _activeByContext = [];
    private readonly Dictionary<string, ReplayEntry> _replay = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _replayLru = [];
    private readonly object _stateSync = new();
    private readonly SemaphoreSlim _pickerGate = new(1, 1);
    private TaskCompletionSource _drained = CompletedDrain();
    private int _state;

    public PhotonCadArtifactProtocolBridge(
        PhotonCadArtifactStorage storage,
        PhotonCadArtifactReleaseCoordinator coordinator,
        IPhotonCadArtifactDestinationAuthority destinations,
        Action<object> post,
        TimeSpan? requestTimeout = null,
        PhotonCadArtifactProtocolLimits? limits = null,
        TimeProvider? timeProvider = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _destinations = destinations ?? throw new ArgumentNullException(nameof(destinations));
        _post = post ?? throw new ArgumentNullException(nameof(post));
        _requestTimeout = requestTimeout ?? TimeSpan.FromMinutes(2);
        _limits = limits ?? new PhotonCadArtifactProtocolLimits();
        _clock = new PhotonCadMonotonicClock(timeProvider);
        if (_requestTimeout < TimeSpan.FromSeconds(5) || _requestTimeout > TimeSpan.FromMinutes(10))
            throw new PhotonCadArtifactException("invalid_request_timeout");
    }

    public static bool CanHandle(string type) => type is
        "photonCad.artifact.destination.pick" or
        "photonCad.artifact.review" or
        "photonCad.artifact.commit" or
        "photonCad.artifact.discard" or
        "photonCad.artifact.cancel";

    public async ValueTask HandleAsync(string type, JsonElement message)
    {
        if (Volatile.Read(ref _state) != 0 || !CanHandle(type)) return;
        if (!TryEnvelope(message, out var requestId)) return;
        var replayReservation = ReserveReplay(requestId);
        if (replayReservation != ReplayReservation.Accepted)
        {
            PostError(
                requestId,
                replayReservation == ReplayReservation.Duplicate ? "duplicate_request" : "request_replay_capacity",
                retryable: replayReservation == ReplayReservation.Capacity);
            return;
        }
        PhotonCadArtifactContext context;
        try
        {
            context = RequiredContext(message);
        }
        catch (PhotonCadArtifactException exception)
        {
            PostError(requestId, exception.Code, retryable: false);
            return;
        }
        if (type == "photonCad.artifact.cancel")
        {
            Cancel(message, requestId, context);
            return;
        }

        var cancellation = new CancellationTokenSource(_requestTimeout);
        if (!TryActivate(requestId, context, cancellation))
        {
            cancellation.Dispose();
            PostError(requestId, "artifact_request_capacity", retryable: true);
            return;
        }

        try
        {
            switch (type)
            {
                case "photonCad.artifact.destination.pick":
                    await PickDestinationAsync(message, requestId, cancellation.Token).ConfigureAwait(false);
                    break;
                case "photonCad.artifact.review":
                    await ReviewAsync(message, requestId, cancellation.Token).ConfigureAwait(false);
                    break;
                case "photonCad.artifact.commit":
                    await CommitAsync(message, requestId, cancellation.Token).ConfigureAwait(false);
                    break;
                case "photonCad.artifact.discard":
                    await DiscardAsync(message, requestId).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            PostError(requestId, "request_cancelled", retryable: true);
        }
        catch (PhotonCadArtifactException exception)
        {
            PostError(requestId, exception.Code, retryable: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            PostError(requestId, "artifact_request_failed", retryable: false);
        }
        finally
        {
            CompleteActive(requestId);
        }
    }

    public void PublishAvailable(PhotonCadArtifactDescriptor artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (Volatile.Read(ref _state) != 0) return;
        _post(new
        {
            type = "photonCad.artifact.available",
            version = PhotonCadArtifactContract.Version,
            value = ExternalArtifact(artifact),
        });
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(_limits.ShutdownTimeout);
        await ShutdownAsync(timeout.Token).ConfigureAwait(false);
    }

    internal async ValueTask ShutdownAsync(CancellationToken cancellationToken)
    {
        Task drained;
        CancellationTokenSource[] active;
        lock (_stateSync)
        {
            if (_state == 2) return;
            _state = 1;
            drained = _drained.Task;
            active = _active.Values.Select(value => value.Cancellation).ToArray();
        }
        foreach (var cancellation in active) cancellation.Cancel();
        try
        {
            await drained.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new PhotonCadArtifactException("artifact_shutdown_incomplete");
        }
        await _coordinator.DisposeAsync().ConfigureAwait(false);
        lock (_stateSync) _state = 2;
    }

    private async ValueTask PickDestinationAsync(
        JsonElement message,
        string requestId,
        CancellationToken cancellationToken)
    {
        var context = RequiredContext(message);
        var artifactHandle = new PhotonCadArtifactHandle(RequiredString(message, "artifactHandle"));
        var artifact = await _storage.DescribeAsync(artifactHandle, context, cancellationToken).ConfigureAwait(false);
        PhotonCadDestinationDescriptor? destination;
        await _pickerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            destination = await _destinations.PickAsync(artifact, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pickerGate.Release();
        }
        _post(new
        {
            type = "photonCad.artifact.destination.pick.result",
            version = PhotonCadArtifactContract.Version,
            value = destination is null
                ? new
                {
                    contractVersion = PhotonCadArtifactContract.Version,
                    requestId,
                    status = "cancelled",
                    reason = "destination_cancelled",
                    destinationHandle = (string?)null,
                    displayLabel = (string?)null,
                    expiresAtUtc = (string?)null,
                }
                : new
                {
                    contractVersion = PhotonCadArtifactContract.Version,
                    requestId,
                    status = "selected",
                    reason = "destination_selected",
                    destinationHandle = (string?)destination.DestinationHandle.Value,
                    displayLabel = (string?)destination.DisplayLabel,
                    expiresAtUtc = (string?)Utc(destination.ExpiresAtUtc),
                },
        });
    }

    private async ValueTask ReviewAsync(
        JsonElement message,
        string requestId,
        CancellationToken cancellationToken)
    {
        var request = new PhotonCadArtifactReviewRequest(
            requestId,
            RequiredContext(message),
            new PhotonCadArtifactHandle(RequiredString(message, "artifactHandle")),
            RequiredString(message, "kind"),
            RequiredString(message, "contentDigest"),
            new PhotonCadDestinationHandle(RequiredString(message, "destinationHandle")));
        var result = await _coordinator.ReviewAsync(request, cancellationToken).ConfigureAwait(false);
        _post(new
        {
            type = "photonCad.artifact.review.result",
            version = PhotonCadArtifactContract.Version,
            value = new
            {
                contractVersion = result.ContractVersion,
                requestId = result.RequestId,
                status = result.Status,
                reason = result.Reason,
                reviewHandle = result.ReviewHandle?.Value,
                fingerprint = result.Fingerprint,
                resourceUrl = result.ResourceUrl?.AbsoluteUri,
                artifact = result.Artifact is null ? null : ExternalArtifact(result.Artifact),
                destinationLabel = result.DestinationLabel,
                expiresAtUtc = result.ExpiresAtUtc is { } expires ? Utc(expires) : null,
            },
        });
    }

    private async ValueTask CommitAsync(
        JsonElement message,
        string requestId,
        CancellationToken cancellationToken)
    {
        var result = await _coordinator.CommitAsync(
            new PhotonCadArtifactCommitRequest(
                requestId,
                RequiredContext(message),
                new PhotonCadArtifactReviewHandle(RequiredString(message, "reviewHandle")),
                RequiredString(message, "fingerprint")),
            cancellationToken).ConfigureAwait(false);
        _post(new
        {
            type = "photonCad.artifact.commit.result",
            version = PhotonCadArtifactContract.Version,
            value = new
            {
                contractVersion = result.ContractVersion,
                requestId = result.RequestId,
                status = result.Status,
                reason = result.Reason,
                receipt = result.Receipt is null ? null : new
                {
                    receiptHandle = result.Receipt.ReceiptHandle.Value,
                    artifactHandle = result.Receipt.ArtifactHandle.Value,
                    contentDigest = result.Receipt.ContentDigest,
                    byteLength = result.Receipt.ByteLength,
                    destinationLabel = result.Receipt.DestinationLabel,
                    committedAtUtc = Utc(result.Receipt.CommittedAtUtc),
                },
            },
        });
    }

    private async ValueTask DiscardAsync(JsonElement message, string requestId)
    {
        var discarded = await _coordinator.DiscardAsync(
            new PhotonCadArtifactReviewHandle(RequiredString(message, "reviewHandle")),
            RequiredContext(message)).ConfigureAwait(false);
        _post(new
        {
            type = "photonCad.artifact.discard.result",
            version = PhotonCadArtifactContract.Version,
            value = new
            {
                contractVersion = PhotonCadArtifactContract.Version,
                requestId,
                status = discarded ? "discarded" : "rejected",
                reason = discarded ? "review_discarded" : "review_unavailable",
            },
        });
    }

    private void Cancel(JsonElement message, string requestId, PhotonCadArtifactContext context)
    {
        ActiveRequest? active;
        if (!TryString(message, "targetRequestId", out var target))
        {
            PostError(requestId, "request_not_active", retryable: false);
            return;
        }
        lock (_stateSync)
            _active.TryGetValue(target, out active);
        if (active is null || active.Context != context)
        {
            PostError(requestId, "request_not_active", retryable: false);
            return;
        }
        active.Cancellation.Cancel();
        _post(new
        {
            type = "photonCad.artifact.cancel.result",
            version = PhotonCadArtifactContract.Version,
            value = new
            {
                contractVersion = PhotonCadArtifactContract.Version,
                requestId,
                status = "cancelled",
                reason = "cancellation_requested",
            },
        });
    }

    private ReplayReservation ReserveReplay(string requestId)
    {
        lock (_stateSync)
        {
            if (_state != 0) return ReplayReservation.Capacity;
            PruneReplay();
            if (_replay.TryGetValue(requestId, out var duplicate))
            {
                _replayLru.Remove(duplicate.Node);
                _replayLru.AddLast(duplicate.Node);
                return ReplayReservation.Duplicate;
            }
            while (_replay.Count >= _limits.MaximumReplayEntries)
            {
                var candidate = _replayLru.First;
                while (candidate is not null && _active.ContainsKey(candidate.Value)) candidate = candidate.Next;
                if (candidate is null) return ReplayReservation.Capacity;
                RemoveReplay(candidate.Value);
            }
            var node = _replayLru.AddLast(requestId);
            _replay.Add(requestId, new ReplayEntry(_clock.Start(_limits.ReplayTimeToLive), node));
            return ReplayReservation.Accepted;
        }
    }

    private bool TryActivate(
        string requestId,
        PhotonCadArtifactContext context,
        CancellationTokenSource cancellation)
    {
        lock (_stateSync)
        {
            if (_state != 0 || _active.Count >= _limits.MaximumActiveRequests ||
                _activeByContext.GetValueOrDefault(context) >= _limits.MaximumActiveRequestsPerContext)
                return false;
            if (_active.Count == 0)
                _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _active.Add(requestId, new ActiveRequest(context, cancellation));
            _activeByContext[context] = _activeByContext.GetValueOrDefault(context) + 1;
            return true;
        }
    }

    private void CompleteActive(string requestId)
    {
        CancellationTokenSource? cancellation = null;
        TaskCompletionSource? drained = null;
        lock (_stateSync)
        {
            if (_active.Remove(requestId, out var active))
            {
                cancellation = active.Cancellation;
                var count = _activeByContext[active.Context] - 1;
                if (count == 0) _activeByContext.Remove(active.Context);
                else _activeByContext[active.Context] = count;
                if (_active.Count == 0) drained = _drained;
            }
        }
        cancellation?.Dispose();
        drained?.TrySetResult();
    }

    private void PruneReplay()
    {
        var node = _replayLru.First;
        while (node is not null)
        {
            var next = node.Next;
            if (_replay.TryGetValue(node.Value, out var entry) && _clock.IsExpired(entry.Deadline) &&
                !_active.ContainsKey(node.Value))
                RemoveReplay(node.Value);
            node = next;
        }
    }

    private void RemoveReplay(string requestId)
    {
        if (!_replay.Remove(requestId, out var entry)) return;
        _replayLru.Remove(entry.Node);
    }

    private static TaskCompletionSource CompletedDrain()
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        result.SetResult();
        return result;
    }

    private static bool TryEnvelope(JsonElement message, out string requestId)
    {
        requestId = string.Empty;
        return message.ValueKind == JsonValueKind.Object &&
            message.TryGetProperty("version", out var version) && version.TryGetInt32(out var parsedVersion) &&
            parsedVersion == PhotonCadArtifactContract.Version &&
            TryString(message, "requestId", out requestId);
    }

    private static PhotonCadArtifactContext RequiredContext(JsonElement message) => new(
        RequiredString(message, "rendererSessionId"),
        RequiredString(message, "controllerId"),
        RequiredString(message, "sessionId"),
        RequiredString(message, "projectId"),
        message.TryGetProperty("revision", out var revision) && revision.TryGetInt64(out var value) && value >= 0
            ? value
            : throw new PhotonCadArtifactException("invalid_revision"));

    private static string RequiredString(JsonElement message, string name) =>
        TryString(message, name, out var value)
            ? value
            : throw new PhotonCadArtifactException($"invalid_{name}");

    private static bool TryString(JsonElement message, string name, out string value)
    {
        value = string.Empty;
        if (!message.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String) return false;
        var candidate = element.GetString();
        if (string.IsNullOrEmpty(candidate) || candidate.Length > 256) return false;
        value = candidate;
        return true;
    }

    private static object ExternalArtifact(PhotonCadArtifactDescriptor artifact) => new
    {
        contractVersion = PhotonCadArtifactContract.Version,
        artifactHandle = artifact.ArtifactHandle.Value,
        rendererSessionId = artifact.Context.RendererSessionId,
        controllerId = artifact.Context.ControllerId,
        sessionId = artifact.Context.CadSessionId,
        projectId = artifact.Context.ProjectId,
        revision = artifact.Context.Revision,
        kind = artifact.Kind,
        mediaType = artifact.MediaType,
        profile = artifact.Profile,
        contentDigest = artifact.ContentDigest,
        byteLength = artifact.ByteLength,
        displayName = artifact.DisplayName,
        expiresAtUtc = Utc(artifact.ExpiresAtUtc),
    };

    private void PostError(string requestId, string code, bool retryable) => _post(new
    {
        type = "photonCad.artifact.error",
        version = PhotonCadArtifactContract.Version,
        requestId,
        code,
        retryable,
    });

    private static string Utc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private sealed record ActiveRequest(
        PhotonCadArtifactContext Context,
        CancellationTokenSource Cancellation);

    private sealed record ReplayEntry(PhotonCadDeadline Deadline, LinkedListNode<string> Node);

    private enum ReplayReservation
    {
        Accepted,
        Duplicate,
        Capacity,
    }

}

internal sealed record PhotonCadArtifactProtocolLimits
{
    internal PhotonCadArtifactProtocolLimits(
        int maximumActiveRequests = 32,
        int maximumActiveRequestsPerContext = 4,
        int maximumReplayEntries = 4_096,
        TimeSpan? replayTimeToLive = null,
        TimeSpan? shutdownTimeout = null)
    {
        var replayTtl = replayTimeToLive ?? TimeSpan.FromMinutes(15);
        var shutdown = shutdownTimeout ?? TimeSpan.FromSeconds(30);
        if (maximumActiveRequests is < 1 or > 256 ||
            maximumActiveRequestsPerContext is < 1 or > 64 ||
            maximumActiveRequestsPerContext > maximumActiveRequests ||
            maximumReplayEntries is < 32 or > 65_536 ||
            maximumReplayEntries <= maximumActiveRequests ||
            replayTtl < TimeSpan.FromSeconds(30) || replayTtl > TimeSpan.FromHours(24) ||
            shutdown < TimeSpan.FromSeconds(1) || shutdown > TimeSpan.FromMinutes(2))
            throw new PhotonCadArtifactException("invalid_protocol_limits");
        MaximumActiveRequests = maximumActiveRequests;
        MaximumActiveRequestsPerContext = maximumActiveRequestsPerContext;
        MaximumReplayEntries = maximumReplayEntries;
        ReplayTimeToLive = replayTtl;
        ShutdownTimeout = shutdown;
    }

    internal int MaximumActiveRequests { get; }
    internal int MaximumActiveRequestsPerContext { get; }
    internal int MaximumReplayEntries { get; }
    internal TimeSpan ReplayTimeToLive { get; }
    internal TimeSpan ShutdownTimeout { get; }
}
