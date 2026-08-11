using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace PhotonCadArtifacts;

internal sealed class PhotonCadArtifactReadLease : IAsyncDisposable
{
    private readonly Stream _content;
    private readonly Action _release;
    private int _disposed;

    internal PhotonCadArtifactReadLease(
        PhotonCadArtifactDescriptor descriptor,
        Stream content,
        Action release)
    {
        Descriptor = descriptor;
        _content = content;
        _release = release;
    }

    public PhotonCadArtifactDescriptor Descriptor { get; }
    public Stream Content => _content;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            await _content.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _release();
        }
    }
}

internal interface IPhotonCadArtifactResourceStore
{
    ValueTask<PhotonCadArtifactReadLease> ConsumeResourceAsync(
        PhotonCadArtifactResourceHandle resource,
        PhotonCadArtifactContext context,
        CancellationToken cancellationToken = default);
}

internal interface IPhotonCadArtifactReleaseStorage : IPhotonCadArtifactResourceStore, IAsyncDisposable
{
    ValueTask<PhotonCadArtifactDescriptor> DescribeAsync(
        PhotonCadArtifactHandle handle,
        PhotonCadArtifactContext context,
        CancellationToken cancellationToken = default);

    ValueTask<PhotonCadArtifactReadLease> OpenVerifiedAsync(
        PhotonCadArtifactHandle handle,
        PhotonCadArtifactContext context,
        CancellationToken cancellationToken = default);

    ValueTask<PhotonCadArtifactResourceLease> IssueResourceAsync(
        PhotonCadArtifactHandle handle,
        PhotonCadArtifactContext context,
        TimeSpan timeToLive,
        CancellationToken cancellationToken = default);

    ValueTask RevokeResourceAsync(PhotonCadArtifactResourceHandle resource);
    ValueTask RevokeContextAsync(PhotonCadArtifactContext context);
}

internal sealed record PhotonCadArtifactResourceLease(
    PhotonCadArtifactResourceHandle ResourceHandle,
    PhotonCadArtifactHandle ArtifactHandle,
    PhotonCadArtifactContext Context,
    DateTimeOffset ExpiresAtUtc);

internal sealed record PhotonCadArtifactCleanupStatus(
    int PendingEntries,
    IReadOnlyList<string> ReasonCodes);

internal interface IPhotonCadArtifactCleanupCustody
{
    ValueTask<PhotonCadExactDeleteResult> DeleteIfExactAsync(
        string path,
        string expectedRoot,
        PhotonCadWindowsFileIdentity identity,
        long byteLength,
        string digest,
        CancellationToken cancellationToken = default);
}

internal sealed class PhotonCadWindowsArtifactCleanupCustody : IPhotonCadArtifactCleanupCustody
{
    public ValueTask<PhotonCadExactDeleteResult> DeleteIfExactAsync(
        string path,
        string expectedRoot,
        PhotonCadWindowsFileIdentity identity,
        long byteLength,
        string digest,
        CancellationToken cancellationToken = default) =>
        PhotonCadWindowsFilePolicy.DeleteIfExactBindingAsync(
            path,
            expectedRoot,
            identity,
            byteLength,
            digest,
            cancellationToken);
}

internal sealed class PhotonCadArtifactStorage : IPhotonCadArtifactReleaseStorage
{
    private const int BufferSize = 1_048_576;

    private readonly PhotonCadArtifactStorageOptions _options;
    private readonly IPhotonCadArtifactSource _source;
    private readonly IPhotonCadArtifactContextAuthority _contextAuthority;
    private readonly PhotonCadMonotonicClock _clock;
    private readonly IPhotonCadArtifactCleanupCustody _cleanupCustody;
    private readonly string _sealedRoot;
    private readonly PhotonCadArtifactCleanupJournal _cleanupJournal;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, StoredArtifact> _artifacts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StoredResource> _resources = new(StringComparer.Ordinal);
    private readonly List<PhotonCadArtifactCleanupIntent> _pendingCleanup = [];
    private readonly ConcurrentDictionary<Guid, OpenLeaseRegistration> _openLeases = new();
    private bool _closing;
    private bool _disposed;

    public PhotonCadArtifactStorage(
        PhotonCadArtifactStorageOptions options,
        IPhotonCadArtifactSource source,
        IPhotonCadArtifactContextAuthority contextAuthority,
        TimeProvider? timeProvider = null,
        IPhotonCadArtifactCleanupCustody? cleanupCustody = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _contextAuthority = contextAuthority ?? throw new ArgumentNullException(nameof(contextAuthority));
        _clock = new PhotonCadMonotonicClock(timeProvider);
        _cleanupCustody = cleanupCustody ?? new PhotonCadWindowsArtifactCleanupCustody();
        _sealedRoot = Path.Combine(options.RootDirectory, "sealed");
        PhotonCadWindowsFilePolicy.EnsureOwnedDirectory(options.RootDirectory);
        PhotonCadWindowsFilePolicy.EnsureOwnedDirectory(_sealedRoot);
        _cleanupJournal = new PhotonCadArtifactCleanupJournal(
            options.RootDirectory,
            _sealedRoot,
            options.MaximumQuarantineEntries);
        _pendingCleanup.AddRange(_cleanupJournal.Load());
    }

    public async ValueTask<PhotonCadArtifactDescriptor> SealAsync(
        PhotonCadArtifactSourceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        PhotonCadArtifactGuards.CurrentContext(_contextAuthority, request.Context);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await CleanupExpiredCoreAsync().ConfigureAwait(false);
            PhotonCadArtifactGuards.CurrentContext(_contextAuthority, request.Context);

            var contextArtifacts = _artifacts.Values.Where(entry => entry.Descriptor.Context == request.Context).ToArray();
            if (contextArtifacts.Length >= _options.MaximumArtifactsPerContext ||
                contextArtifacts.Sum(entry => entry.Descriptor.ByteLength) >= _options.MaximumSessionBytes)
                throw new PhotonCadArtifactException("artifact_quota_exceeded");

            await using var source = await _source.AcquireAsync(request, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(source.Descriptor.SourceArtifactId, request.SourceArtifactId, StringComparison.Ordinal))
                throw new PhotonCadArtifactException("source_artifact_mismatch");
            if (source.Descriptor.ByteLength > _options.MaximumArtifactBytes ||
                source.Descriptor.ByteLength + contextArtifacts.Sum(entry => entry.Descriptor.ByteLength) > _options.MaximumSessionBytes)
                throw new PhotonCadArtifactException("artifact_quota_exceeded");
            if (source.Content.CanSeek && source.Content.Position != 0)
                throw new PhotonCadArtifactException("source_stream_not_rewound");

            var handle = PhotonCadArtifactHandle.New();
            var token = handle.Value["cad-artifact:".Length..];
            var partialPath = Path.Combine(_sealedRoot, $".{token}.partial");
            var sealedPath = Path.Combine(_sealedRoot, $"{token}.step");
            PhotonCadWindowsFilePolicy.RequireMissingRegularTarget(partialPath, _sealedRoot);
            PhotonCadWindowsFilePolicy.RequireMissingRegularTarget(sealedPath, _sealedRoot);
            PhotonCadArtifactCleanupIntent? cleanupIntent = null;
            try
            {
                await using var partial = PhotonCadWindowsFilePolicy.CreateNewOwnedFile(
                    partialPath,
                    _sealedRoot,
                    BufferSize);
                var createdIdentity = PhotonCadWindowsFilePolicy
                    .InspectRegularFileMetadata(partial.SafeFileHandle)
                    .Identity;
                PhotonCadWindowsFilePolicy.SetDeleteOnClose(partial.SafeFileHandle, delete: true);
                var (digest, bytes) = await CopyAndHashAsync(
                    source.Content,
                    partial,
                    source.Descriptor.ByteLength,
                    _options.MaximumArtifactBytes,
                    cancellationToken).ConfigureAwait(false);
                if (bytes != source.Descriptor.ByteLength ||
                    !string.Equals(digest, source.Descriptor.ContentDigest, StringComparison.Ordinal))
                    throw new PhotonCadArtifactException("source_digest_mismatch");
                partial.Position = 0;
                await PhotonCadStepPart21Validator.ValidateAsync(partial, bytes, cancellationToken).ConfigureAwait(false);
                PhotonCadArtifactGuards.CurrentContext(_contextAuthority, request.Context);
                var partialMetadata = new PhotonCadWindowsFileMetadata(createdIdentity, partial.Length);
                if (partialMetadata.ByteLength != bytes)
                    throw new PhotonCadArtifactException("sealed_identity_changed");
                cleanupIntent = _cleanupJournal.Reserve(
                    handle,
                    partialPath,
                    sealedPath,
                    partialMetadata.Identity,
                    bytes,
                    digest,
                    "artifact_cleanup_pending");
                _pendingCleanup.Add(cleanupIntent);
                PhotonCadWindowsFilePolicy.SetDeleteOnClose(partial.SafeFileHandle, delete: false);
                PhotonCadWindowsFilePolicy.RenameOwnedHandle(partial.SafeFileHandle, sealedPath, _sealedRoot);
                PhotonCadWindowsFilePolicy.FlushHandle(partial.SafeFileHandle);
                PhotonCadWindowsFilePolicy.FlushDirectory(_sealedRoot);
                var sealedMetadata = PhotonCadWindowsFilePolicy.InspectRegularFileMetadata(partial.SafeFileHandle);
                if (partialMetadata.Identity != sealedMetadata.Identity || sealedMetadata.ByteLength != bytes)
                    throw new PhotonCadArtifactException("sealed_identity_changed");

                var deadline = _clock.Start(_options.ArtifactTimeToLive);

                var descriptor = new PhotonCadArtifactDescriptor(
                    handle,
                    request.Context,
                    source.Descriptor.Kind,
                    source.Descriptor.MediaType,
                    source.Descriptor.Profile,
                    digest,
                    bytes,
                    source.Descriptor.DisplayName,
                    deadline.DisplayExpiresAtUtc);
                _artifacts.Add(handle.Value, new StoredArtifact(
                    descriptor,
                    deadline,
                    sealedPath,
                    sealedMetadata.Identity,
                    cleanupIntent));
                _pendingCleanup.Remove(cleanupIntent);
                return descriptor;
            }
            catch
            {
                if (cleanupIntent is not null)
                    await TryCleanupIntentAsync(cleanupIntent, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<PhotonCadArtifactDescriptor> DescribeAsync(
        PhotonCadArtifactHandle handle,
        PhotonCadArtifactContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(context);
        PhotonCadArtifactGuards.CurrentContext(_contextAuthority, context);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var entry = await RequiredArtifactAsync(handle, context, cancellationToken).ConfigureAwait(false);
            return entry.Descriptor;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<PhotonCadArtifactReadLease> OpenVerifiedAsync(
        PhotonCadArtifactHandle handle,
        PhotonCadArtifactContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(context);
        PhotonCadArtifactGuards.CurrentContext(_contextAuthority, context);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var entry = await RequiredArtifactAsync(handle, context, cancellationToken).ConfigureAwait(false);
            return await OpenVerifiedCoreAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<PhotonCadArtifactResourceLease> IssueResourceAsync(
        PhotonCadArtifactHandle handle,
        PhotonCadArtifactContext context,
        TimeSpan timeToLive,
        CancellationToken cancellationToken = default)
    {
        if (timeToLive <= TimeSpan.Zero) throw new PhotonCadArtifactException("invalid_resource_ttl");
        PhotonCadArtifactGuards.CurrentContext(_contextAuthority, context);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var entry = await RequiredArtifactAsync(handle, context, cancellationToken).ConfigureAwait(false);
            var deadline = _clock.StartCapped(timeToLive, entry.Deadline);
            var expires = deadline.DisplayExpiresAtUtc;
            var resource = PhotonCadArtifactResourceHandle.New();
            _resources.Add(resource.Value, new StoredResource(resource, handle, context, expires, deadline));
            return new PhotonCadArtifactResourceLease(resource, handle, context, expires);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<PhotonCadArtifactReadLease> ConsumeResourceAsync(
        PhotonCadArtifactResourceHandle resource,
        PhotonCadArtifactContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        PhotonCadArtifactGuards.CurrentContext(_contextAuthority, context);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!_resources.Remove(resource.Value, out var stored) || stored.Context != context ||
                _clock.IsExpired(stored.Deadline))
                throw new PhotonCadArtifactException("resource_unavailable");
            var entry = await RequiredArtifactAsync(stored.ArtifactHandle, context, cancellationToken).ConfigureAwait(false);
            return await OpenVerifiedCoreAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask RevokeResourceAsync(PhotonCadArtifactResourceHandle resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_disposed) _resources.Remove(resource.Value);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask RevokeContextAsync(PhotonCadArtifactContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            foreach (var resource in _resources.Values.Where(value => value.Context == context).ToArray())
                _resources.Remove(resource.ResourceHandle.Value);
            CloseContextLeases(context);
            foreach (var artifact in _artifacts.Values.Where(value => value.Descriptor.Context == context).ToArray())
            {
                _artifacts.Remove(artifact.Descriptor.ArtifactHandle.Value);
                _pendingCleanup.Add(artifact.CleanupIntent);
            }
            await RetryPendingCleanupCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<PhotonCadArtifactCleanupStatus> RetryPendingCleanupAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_disposed) await RetryPendingCleanupCoreAsync().ConfigureAwait(false);
            var reasons = _pendingCleanup.Select(entry => entry.ReasonCode)
                .Concat(_cleanupJournal.BlockedReasonCodes)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return new PhotonCadArtifactCleanupStatus(
                checked(_pendingCleanup.Count + _cleanupJournal.BlockedReasonCodes.Count),
                reasons);
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
            _resources.Clear();
            CloseAllLeases();
            foreach (var artifact in _artifacts.Values.ToArray())
                _pendingCleanup.Add(artifact.CleanupIntent);
            _artifacts.Clear();
            await RetryPendingCleanupCoreAsync().ConfigureAwait(false);
            if (_pendingCleanup.Count != 0 || _cleanupJournal.BlockedReasonCodes.Count != 0)
                throw new PhotonCadArtifactException("artifact_cleanup_pending");
            _disposed = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<StoredArtifact> RequiredArtifactAsync(
        PhotonCadArtifactHandle handle,
        PhotonCadArtifactContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_artifacts.TryGetValue(handle.Value, out var entry) || entry.Descriptor.Context != context)
            throw new PhotonCadArtifactException("artifact_unavailable");
        if (_clock.IsExpired(entry.Deadline))
        {
            _artifacts.Remove(handle.Value);
            _pendingCleanup.Add(entry.CleanupIntent);
            await RetryPendingCleanupCoreAsync().ConfigureAwait(false);
            throw new PhotonCadArtifactException("artifact_expired");
        }
        return entry;
    }

    private async ValueTask<PhotonCadArtifactReadLease> OpenVerifiedCoreAsync(
        StoredArtifact entry,
        CancellationToken cancellationToken)
    {
        var stream = new FileStream(
            entry.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            var metadata = PhotonCadWindowsFilePolicy.InspectRegularFileMetadata(stream.SafeFileHandle);
            if (metadata.Identity != entry.Identity)
                throw new PhotonCadArtifactException("artifact_identity_changed");
            if (metadata.ByteLength != entry.Descriptor.ByteLength)
                throw new PhotonCadArtifactException("artifact_length_changed");
            var digest = CanonicalDigest(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(digest, entry.Descriptor.ContentDigest, StringComparison.Ordinal))
                throw new PhotonCadArtifactException("artifact_digest_changed");
            stream.Position = 0;
            var leaseId = Guid.NewGuid();
            if (!_openLeases.TryAdd(leaseId, new OpenLeaseRegistration(entry.Descriptor.Context, stream)))
                throw new PhotonCadArtifactException("artifact_lease_unavailable");
            return new PhotonCadArtifactReadLease(
                entry.Descriptor,
                stream,
                () => _openLeases.TryRemove(leaseId, out _));
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask CleanupExpiredCoreAsync()
    {
        foreach (var resource in _resources.Values.Where(value => _clock.IsExpired(value.Deadline)).ToArray())
            _resources.Remove(resource.ResourceHandle.Value);
        foreach (var artifact in _artifacts.Values.Where(value => _clock.IsExpired(value.Deadline)).ToArray())
        {
            _artifacts.Remove(artifact.Descriptor.ArtifactHandle.Value);
            _pendingCleanup.Add(artifact.CleanupIntent);
        }
        await RetryPendingCleanupCoreAsync().ConfigureAwait(false);
    }

    private async ValueTask RetryPendingCleanupCoreAsync()
    {
        foreach (var pending in _pendingCleanup.ToArray())
            await TryCleanupIntentAsync(pending, CancellationToken.None).ConfigureAwait(false);
    }

    private async ValueTask<bool> TryCleanupIntentAsync(
        PhotonCadArtifactCleanupIntent pending,
        CancellationToken cancellationToken)
    {
        try
        {
            var partial = await _cleanupCustody.DeleteIfExactAsync(
                pending.PartialPath,
                _sealedRoot,
                pending.ArtifactIdentity,
                pending.ByteLength,
                pending.ContentDigest,
                cancellationToken).ConfigureAwait(false);
            var sealedResult = await _cleanupCustody.DeleteIfExactAsync(
                pending.SealedPath,
                _sealedRoot,
                pending.ArtifactIdentity,
                pending.ByteLength,
                pending.ContentDigest,
                cancellationToken).ConfigureAwait(false);
            if (!IsClean(partial.Outcome) || !IsClean(sealedResult.Outcome)) return false;
            if (!await _cleanupJournal.RetireAsync(pending, cancellationToken).ConfigureAwait(false)) return false;
            _pendingCleanup.Remove(pending);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PhotonCadArtifactException)
        {
            return false;
        }
    }

    private static bool IsClean(PhotonCadExactDeleteOutcome outcome) =>
        outcome is PhotonCadExactDeleteOutcome.Deleted or PhotonCadExactDeleteOutcome.Missing;

    private static async ValueTask<(string Digest, long Bytes)> CopyAndHashAsync(
        Stream source,
        FileStream output,
        long expectedBytes,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long total = 0;
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                total = checked(total + read);
                if (total > expectedBytes || total > maximumBytes)
                    throw new PhotonCadArtifactException("artifact_size_exceeded");
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
            return ($"sha256:{Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()}", total);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static string CanonicalDigest(byte[] digest) =>
        $"sha256:{Convert.ToHexString(digest).ToLowerInvariant()}";

    private void CloseContextLeases(PhotonCadArtifactContext context)
    {
        foreach (var pair in _openLeases.Where(pair => pair.Value.Context == context).ToArray())
        {
            pair.Value.Content.Dispose();
            _openLeases.TryRemove(pair.Key, out _);
        }
    }

    private void CloseAllLeases()
    {
        foreach (var pair in _openLeases.ToArray()) pair.Value.Content.Dispose();
        _openLeases.Clear();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed || _closing) throw new ObjectDisposedException(nameof(PhotonCadArtifactStorage));
    }

    private sealed class StoredArtifact
    {
        internal StoredArtifact(
            PhotonCadArtifactDescriptor descriptor,
            PhotonCadDeadline deadline,
            string path,
            PhotonCadWindowsFileIdentity identity,
            PhotonCadArtifactCleanupIntent cleanupIntent)
        {
            Descriptor = descriptor;
            Deadline = deadline;
            Path = path;
            Identity = identity;
            CleanupIntent = cleanupIntent;
        }

        internal PhotonCadArtifactDescriptor Descriptor { get; }
        internal PhotonCadDeadline Deadline { get; }
        internal string Path { get; }
        internal PhotonCadWindowsFileIdentity Identity { get; }
        internal PhotonCadArtifactCleanupIntent CleanupIntent { get; }
        internal bool Accessible { get; set; } = true;
    }

    private sealed record StoredResource(
        PhotonCadArtifactResourceHandle ResourceHandle,
        PhotonCadArtifactHandle ArtifactHandle,
        PhotonCadArtifactContext Context,
        DateTimeOffset ExpiresAtUtc,
        PhotonCadDeadline Deadline);

    private sealed record OpenLeaseRegistration(PhotonCadArtifactContext Context, FileStream Content);
}
