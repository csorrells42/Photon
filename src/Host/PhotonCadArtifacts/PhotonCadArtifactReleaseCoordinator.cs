using System.Security.Cryptography;
using System.Text;

namespace PhotonCadArtifacts;

public sealed record PhotonCadArtifactReviewRequest
{
    public PhotonCadArtifactReviewRequest(
        string requestId,
        PhotonCadArtifactContext context,
        PhotonCadArtifactHandle artifactHandle,
        string requestedKind,
        string contentDigest,
        PhotonCadDestinationHandle destinationHandle)
    {
        RequestId = PhotonCadArtifactGuards.Identifier(requestId, nameof(requestId));
        Context = context ?? throw new PhotonCadArtifactException("invalid_context");
        ArtifactHandle = artifactHandle ?? throw new PhotonCadArtifactException("invalid_artifact_handle");
        RequestedKind = requestedKind ?? throw new PhotonCadArtifactException("invalid_requested_kind");
        ContentDigest = PhotonCadArtifactGuards.Digest(contentDigest, nameof(contentDigest));
        DestinationHandle = destinationHandle ?? throw new PhotonCadArtifactException("invalid_destination_handle");
    }

    public string RequestId { get; }
    public PhotonCadArtifactContext Context { get; }
    public PhotonCadArtifactHandle ArtifactHandle { get; }
    public string RequestedKind { get; }
    public string ContentDigest { get; }
    public PhotonCadDestinationHandle DestinationHandle { get; }
}

public sealed record PhotonCadArtifactReviewResult(
    int ContractVersion,
    string RequestId,
    string Status,
    string Reason,
    PhotonCadArtifactReviewHandle? ReviewHandle = null,
    string? Fingerprint = null,
    Uri? ResourceUrl = null,
    PhotonCadArtifactDescriptor? Artifact = null,
    string? DestinationLabel = null,
    DateTimeOffset? ExpiresAtUtc = null);

public sealed record PhotonCadArtifactCommitRequest
{
    public PhotonCadArtifactCommitRequest(
        string requestId,
        PhotonCadArtifactContext context,
        PhotonCadArtifactReviewHandle reviewHandle,
        string fingerprint)
    {
        RequestId = PhotonCadArtifactGuards.Identifier(requestId, nameof(requestId));
        Context = context ?? throw new PhotonCadArtifactException("invalid_context");
        ReviewHandle = reviewHandle ?? throw new PhotonCadArtifactException("invalid_review_handle");
        Fingerprint = PhotonCadArtifactGuards.Digest(fingerprint, nameof(fingerprint));
    }

    public string RequestId { get; }
    public PhotonCadArtifactContext Context { get; }
    public PhotonCadArtifactReviewHandle ReviewHandle { get; }
    public string Fingerprint { get; }
}

public sealed record PhotonCadArtifactCommitResult(
    int ContractVersion,
    string RequestId,
    string Status,
    string Reason,
    PhotonCadDestinationCommitReceipt? Receipt = null);

internal sealed class PhotonCadArtifactReleaseCoordinator : IAsyncDisposable
{
    private readonly IPhotonCadArtifactReleaseStorage _storage;
    private readonly IPhotonCadArtifactDestinationAuthority _destinationAuthority;
    private readonly IPhotonCadArtifactContextAuthority _contextAuthority;
    private readonly PhotonCadArtifactReleaseOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly PhotonCadMonotonicClock _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, ReviewEntry> _reviews = new(StringComparer.Ordinal);
    private bool _closing;
    private bool _disposed;

    public PhotonCadArtifactReleaseCoordinator(
        IPhotonCadArtifactReleaseStorage storage,
        IPhotonCadArtifactDestinationAuthority destinationAuthority,
        IPhotonCadArtifactContextAuthority contextAuthority,
        PhotonCadArtifactReleaseOptions options,
        TimeProvider? timeProvider = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _destinationAuthority = destinationAuthority ?? throw new ArgumentNullException(nameof(destinationAuthority));
        _contextAuthority = contextAuthority ?? throw new ArgumentNullException(nameof(contextAuthority));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _clock = new PhotonCadMonotonicClock(_timeProvider);
    }

    public async ValueTask<PhotonCadArtifactReviewResult> ReviewAsync(
        PhotonCadArtifactReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestedKind != PhotonCadArtifactContract.StepKind)
            return UnavailableReview(request.RequestId, "artifact_format_unavailable");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await RemoveExpiredReviewsAsync().ConfigureAwait(false);
            PhotonCadDestinationDescriptor? resolvedDestination = null;
            PhotonCadArtifactResourceLease? issuedResource = null;
            var installed = false;
            try
            {
                PhotonCadArtifactGuards.CurrentContext(_contextAuthority, request.Context);
                var artifact = await _storage.DescribeAsync(request.ArtifactHandle, request.Context, cancellationToken)
                    .ConfigureAwait(false);
                if (artifact.Kind != request.RequestedKind ||
                    artifact.MediaType != PhotonCadArtifactContract.StepMediaType ||
                    artifact.Profile != PhotonCadArtifactContract.UnspecifiedProfile ||
                    !artifact.ContentDigest.Equals(request.ContentDigest, StringComparison.Ordinal))
                    throw new PhotonCadArtifactException("artifact_review_binding_mismatch");
                var destination = await _destinationAuthority.ResolveAsync(
                    request.DestinationHandle,
                    artifact,
                    request.Context,
                    cancellationToken).ConfigureAwait(false);
                resolvedDestination = destination;

                foreach (var prior in _reviews.Values.Where(entry => entry.Context == request.Context).ToArray())
                {
                    _reviews.Remove(prior.Handle.Value);
                    await _storage.RevokeResourceAsync(prior.ResourceHandle).ConfigureAwait(false);
                    if (prior.Destination.DestinationHandle != destination.DestinationHandle)
                        await _destinationAuthority.RevokeAsync(prior.Destination.DestinationHandle).ConfigureAwait(false);
                }

                var resource = await _storage.IssueResourceAsync(
                    artifact.ArtifactHandle,
                    request.Context,
                    _options.ResourceTimeToLive,
                    cancellationToken).ConfigureAwait(false);
                issuedResource = resource;
                var handle = PhotonCadArtifactReviewHandle.New();
                var deadline = _clock.Start(_options.ResourceTimeToLive < _options.ReviewTimeToLive
                    ? _options.ResourceTimeToLive
                    : _options.ReviewTimeToLive);
                var expires = Minimum(
                    deadline.DisplayExpiresAtUtc,
                    artifact.ExpiresAtUtc,
                    destination.ExpiresAtUtc,
                    resource.ExpiresAtUtc);
                var fingerprint = Fingerprint(
                    request.Context,
                    artifact,
                    destination,
                    handle,
                    expires);
                var entry = new ReviewEntry(
                    handle,
                    fingerprint,
                    request.Context,
                    artifact,
                    destination,
                    resource.ResourceHandle,
                    expires,
                    deadline);
                _reviews.Add(handle.Value, entry);
                installed = true;
                return new PhotonCadArtifactReviewResult(
                    PhotonCadArtifactContract.Version,
                    request.RequestId,
                    "ready",
                    "review_ready",
                    handle,
                    fingerprint,
                    ResourceUri(resource.ResourceHandle),
                    artifact,
                    destination.DisplayLabel,
                    expires);
            }
            catch (PhotonCadArtifactException exception)
            {
                if (!installed)
                    await CleanupUnacceptedReviewAsync(issuedResource, resolvedDestination).ConfigureAwait(false);
                return RejectedReview(request.RequestId, exception.Code);
            }
            catch (OperationCanceledException)
            {
                if (!installed)
                    await CleanupUnacceptedReviewAsync(issuedResource, resolvedDestination).ConfigureAwait(false);
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                ObjectDisposedException or CryptographicException or InvalidDataException or
                NotSupportedException or ArgumentException)
            {
                if (!installed)
                    await CleanupUnacceptedReviewAsync(issuedResource, resolvedDestination).ConfigureAwait(false);
                return RejectedReview(request.RequestId, "artifact_review_failed");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<PhotonCadArtifactCommitResult> CommitAsync(
        PhotonCadArtifactCommitRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!_reviews.TryGetValue(request.ReviewHandle.Value, out var review) || review.Context != request.Context)
                return RejectedCommit(request.RequestId, "review_unavailable");
            _reviews.Remove(request.ReviewHandle.Value);
            try
            {
                if (_clock.IsExpired(review.Deadline))
                    throw new PhotonCadArtifactException("review_expired");
                if (!FixedDigestEquals(request.Fingerprint, review.Fingerprint))
                    throw new PhotonCadArtifactException("review_fingerprint_mismatch");
                PhotonCadArtifactGuards.CurrentContext(_contextAuthority, review.Context);
                var currentArtifact = await _storage.DescribeAsync(
                    review.Artifact.ArtifactHandle,
                    review.Context,
                    cancellationToken).ConfigureAwait(false);
                if (currentArtifact != review.Artifact)
                    throw new PhotonCadArtifactException("artifact_review_binding_mismatch");
                _ = await _destinationAuthority.ResolveAsync(
                    review.Destination.DestinationHandle,
                    currentArtifact,
                    review.Context,
                    cancellationToken).ConfigureAwait(false);
                await using var content = await _storage.OpenVerifiedAsync(
                    currentArtifact.ArtifactHandle,
                    review.Context,
                    cancellationToken).ConfigureAwait(false);
                var receipt = await _destinationAuthority.CommitAsync(
                    review.Destination.DestinationHandle,
                    currentArtifact,
                    review.Context,
                    content.Content,
                    cancellationToken).ConfigureAwait(false);
                return new PhotonCadArtifactCommitResult(
                    PhotonCadArtifactContract.Version,
                    request.RequestId,
                    "committed",
                    "artifact_committed",
                    receipt);
            }
            catch (PhotonCadArtifactException exception)
            {
                return RejectedCommit(request.RequestId, exception.Code);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                ObjectDisposedException or CryptographicException or InvalidDataException or
                NotSupportedException or ArgumentException)
            {
                return RejectedCommit(request.RequestId, "artifact_commit_failed");
            }
            finally
            {
                await RevokeReviewResourcesBestEffortAsync(review).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<bool> DiscardAsync(
        PhotonCadArtifactReviewHandle handle,
        PhotonCadArtifactContext context)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(context);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_disposed && _reviews.TryGetValue(handle.Value, out var review) && review.Context == context)
            {
                _reviews.Remove(handle.Value);
                await RevokeReviewResourcesAsync(review).ConfigureAwait(false);
                return true;
            }
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask InvalidateContextAsync(PhotonCadArtifactContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            foreach (var review in _reviews.Values.Where(entry => entry.Context == context).ToArray())
                await RemoveReviewAsync(review).ConfigureAwait(false);
            await _destinationAuthority.RevokeContextAsync(context).ConfigureAwait(false);
            await _storage.RevokeContextAsync(context).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _closing = true;
            foreach (var review in _reviews.Values.ToArray())
                await RevokeReviewResourcesAsync(review).ConfigureAwait(false);
            _reviews.Clear();
            await _destinationAuthority.DisposeAsync().ConfigureAwait(false);
            await _storage.DisposeAsync().ConfigureAwait(false);
            _disposed = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask RemoveExpiredReviewsAsync()
    {
        foreach (var review in _reviews.Values.Where(entry => _clock.IsExpired(entry.Deadline)).ToArray())
            await RemoveReviewAsync(review).ConfigureAwait(false);
    }

    private async ValueTask RemoveReviewAsync(ReviewEntry review)
    {
        _reviews.Remove(review.Handle.Value);
        await RevokeReviewResourcesAsync(review).ConfigureAwait(false);
    }

    private async ValueTask RevokeReviewResourcesAsync(ReviewEntry review)
    {
        await _storage.RevokeResourceAsync(review.ResourceHandle).ConfigureAwait(false);
        await _destinationAuthority.RevokeAsync(review.Destination.DestinationHandle).ConfigureAwait(false);
    }

    private async ValueTask RevokeReviewResourcesBestEffortAsync(ReviewEntry review)
    {
        try
        {
            await _storage.RevokeResourceAsync(review.ResourceHandle).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsBoundedCleanupFailure(exception))
        {
        }
        try
        {
            await _destinationAuthority.RevokeAsync(review.Destination.DestinationHandle).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsBoundedCleanupFailure(exception))
        {
        }
    }

    private async ValueTask CleanupUnacceptedReviewAsync(
        PhotonCadArtifactResourceLease? resource,
        PhotonCadDestinationDescriptor? destination)
    {
        if (resource is not null)
        {
            try
            {
                await _storage.RevokeResourceAsync(resource.ResourceHandle).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsBoundedCleanupFailure(exception))
            {
            }
        }
        if (destination is not null)
        {
            try
            {
                await _destinationAuthority.RevokeAsync(destination.DestinationHandle).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsBoundedCleanupFailure(exception))
            {
            }
        }
    }

    private static bool IsBoundedCleanupFailure(Exception exception) => exception is
        PhotonCadArtifactException or
        OperationCanceledException or
        IOException or
        UnauthorizedAccessException or
        ObjectDisposedException or
        CryptographicException or
        InvalidDataException or
        NotSupportedException or
        ArgumentException;

    private Uri ResourceUri(PhotonCadArtifactResourceHandle resource) =>
        new(_options.WorkbenchOrigin, _options.ResourcePathPrefix + resource.Token);

    private static string Fingerprint(
        PhotonCadArtifactContext context,
        PhotonCadArtifactDescriptor artifact,
        PhotonCadDestinationDescriptor destination,
        PhotonCadArtifactReviewHandle review,
        DateTimeOffset expires)
    {
        var canonical = string.Join('\n',
            "photon-cad-artifact-release/v1",
            context.RendererSessionId,
            context.ControllerId,
            context.CadSessionId,
            context.ProjectId,
            context.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            artifact.ArtifactHandle.Value,
            artifact.Kind,
            artifact.MediaType,
            artifact.Profile,
            artifact.ContentDigest,
            artifact.ByteLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
            destination.DestinationHandle.Value,
            review.Value,
            expires.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            "action=export");
        return $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant()}";
    }

    private static bool FixedDigestEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left[7..]),
            Convert.FromHexString(right[7..]));

    private static DateTimeOffset Minimum(params DateTimeOffset[] values) => values.Min();

    private static PhotonCadArtifactReviewResult UnavailableReview(string requestId, string reason) => new(
        PhotonCadArtifactContract.Version,
        requestId,
        "unavailable",
        reason);

    private static PhotonCadArtifactReviewResult RejectedReview(string requestId, string reason) => new(
        PhotonCadArtifactContract.Version,
        requestId,
        "rejected",
        reason);

    private static PhotonCadArtifactCommitResult RejectedCommit(string requestId, string reason) => new(
        PhotonCadArtifactContract.Version,
        requestId,
        "rejected",
        reason);

    private void ThrowIfDisposed()
    {
        if (_disposed || _closing) throw new ObjectDisposedException(nameof(PhotonCadArtifactReleaseCoordinator));
    }

    private sealed record ReviewEntry(
        PhotonCadArtifactReviewHandle Handle,
        string Fingerprint,
        PhotonCadArtifactContext Context,
        PhotonCadArtifactDescriptor Artifact,
        PhotonCadDestinationDescriptor Destination,
        PhotonCadArtifactResourceHandle ResourceHandle,
        DateTimeOffset ExpiresAtUtc,
        PhotonCadDeadline Deadline);
}
