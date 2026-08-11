using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PhotonCadProjects;
using PhotonCadProjects.Codec;

namespace PhotonCadPreviews;

public sealed class PhotonCadPreviewCustody : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly PhotonCadCanonicalProjectCodecV1 _codec;
    private readonly PhotonCadPreviewCustodyOptions _options;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _capacity;
    private readonly Dictionary<string, StoredPreview> _previews = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StoredResource> _resources = new(StringComparer.Ordinal);
    private readonly Dictionary<long, RevocablePreviewStream> _activeStreams = [];
    private long _sequence;
    private long _streamSequence;
    private bool _disposed;

    public PhotonCadPreviewCustody(PhotonCadCanonicalProjectCodecV1 codec, PhotonCadPreviewCustodyOptions options,
        TimeProvider? timeProvider = null)
    {
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = timeProvider ?? TimeProvider.System;
        _capacity = new SemaphoreSlim(options.MaximumConcurrentOperations, options.MaximumConcurrentOperations);
    }

    public PhotonCadPreviewReceipt SealCommitted(PhotonCadCommittedPreviewReadback readback)
    {
        ArgumentNullException.ThrowIfNull(readback);
        using var operation = Enter();
        var project = readback.Project;
        var context = readback.Context;
        if (project.Dirty || project.Revision != context.Revision
            || !StringComparer.Ordinal.Equals(project.SessionId, context.CadSessionId)
            || !StringComparer.Ordinal.Equals(project.ProjectId, context.ProjectId))
            throw PreviewGuards.Failure("committed_readback_binding_mismatch");

        PhotonCadProjectStateV1 state;
        try { state = _codec.Inspect(project); }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException or PhotonCadProjectException)
        { throw new PhotonCadPreviewException("committed_readback_invalid"); }
        if (state.Dirty || state.Revision != context.Revision || state.Units != project.Units)
            throw PreviewGuards.Failure("committed_readback_binding_mismatch");
        var matches = state.Artifacts.Where(artifact => artifact.Role == PhotonCadArtifactRoleV1.ProjectPreview).ToArray();
        if (matches.Length != 1) throw PreviewGuards.Failure("committed_preview_cardinality_mismatch");
        var artifact = matches[0];
        if (artifact.Kind != PhotonCadArtifactKindV1.Glb || artifact.OwnerEntityId is not null
            || artifact.Revision != state.Revision || artifact.Bounds is null)
            throw PreviewGuards.Failure("committed_preview_binding_mismatch");
        var bytes = artifact.Content.ToArray();
        if (bytes.LongLength != artifact.ByteLength || bytes.LongLength > _options.MaximumPreviewBytes)
            throw PreviewGuards.Failure("committed_preview_length_mismatch");
        var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(digest), Encoding.ASCII.GetBytes(artifact.Digest)))
            throw PreviewGuards.Failure("committed_preview_digest_mismatch");
        var expectedOccurrenceIds = state.Occurrences
            .Select(occurrence => occurrence.OccurrenceId).ToHashSet(StringComparer.Ordinal);
        if (expectedOccurrenceIds.Count == 0 || expectedOccurrenceIds.Count > 100_000)
            throw PreviewGuards.Failure("glb_resource_limit");
        PhotonCadGlbEnvelopeValidator.Validate(bytes, expectedOccurrenceIds);
        var bounds = new PhotonCadPreviewBounds(
            new PhotonCadPreviewVector(artifact.Bounds.Minimum.X, artifact.Bounds.Minimum.Y, artifact.Bounds.Minimum.Z),
            new PhotonCadPreviewVector(artifact.Bounds.Maximum.X, artifact.Bounds.Maximum.Y, artifact.Bounds.Maximum.Z));
        var previewId = "preview-" + PreviewGuards.NewToken();
        var receipt = new PhotonCadPreviewReceipt(previewId, context, digest, bytes.LongLength, state.Units, bounds, expectedOccurrenceIds.Count);
        lock (_sync)
        {
            ThrowIfDisposed();
            CleanupExpiredLocked();
            RevokeProjectLocked(context.RendererGeneration, context.RendererSessionId, context.CadSessionId, context.ProjectId);
            EnsureCapacityLocked(bytes.LongLength);
            _previews.Add(previewId, new StoredPreview(receipt, bytes, Deadline.Start(_time, _options.PreviewTimeToLive), ++_sequence));
        }
        return receipt;
    }

    public PhotonCadPreviewAsset Resolve(PhotonCadPreviewResolveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var operation = Enter();
        lock (_sync)
        {
            ThrowIfDisposed();
            CleanupExpiredLocked();
            if (!_previews.TryGetValue(request.PreviewId, out var preview) || preview.Receipt.Context != request.Context
                || preview.Receipt.ByteLength > request.MaximumBytes
                || !FixedDigestEquals(preview.Receipt.ContentDigest, request.ExpectedDigest))
                throw PreviewGuards.Failure("preview_unavailable");
            if (_resources.Count >= _options.MaximumOutstandingResources)
                throw PreviewGuards.Failure("preview_resource_capacity_exceeded");
            preview.Sequence = ++_sequence;
            var token = PreviewGuards.NewToken();
            var deadline = Deadline.StartCapped(_time, _options.ResourceTimeToLive, preview.Deadline);
            _resources.Add(token, new StoredResource(token, preview.Receipt.PreviewId, request.Context, deadline));
            var url = new Uri(_options.WorkbenchOrigin, _options.ResourcePathPrefix + token);
            return new PhotonCadPreviewAsset(url, preview.Receipt.ContentDigest, preview.Receipt.ByteLength, deadline.ExpiresAtUtc);
        }
    }

    public PhotonCadPreviewResourceResponse? TryRespond(PhotonCadPreviewResourceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.RequestUri);
        if (!TargetsNamespace(request.RequestUri)) return null;
        if (!request.RendererAuthorized || request.Context is null || !request.Method.Equals("GET", StringComparison.Ordinal)
            || request.Headers?.Keys.Any(key => key.Equals("Range", StringComparison.OrdinalIgnoreCase)) == true
            || !ValidResourceUri(request.RequestUri, out var token)) return PhotonCadPreviewResourceResponse.NotFound();
        try
        {
            using var operation = Enter();
            lock (_sync)
            {
                ThrowIfDisposed();
                CleanupExpiredLocked();
                if (!_resources.TryGetValue(token, out var resource) || resource.Context != request.Context
                    || !_previews.TryGetValue(resource.PreviewId, out var preview)
                    || preview.Receipt.Context != request.Context || resource.Deadline.Expired(_time))
                    return PhotonCadPreviewResourceResponse.NotFound();
                if (_activeStreams.Count >= _options.MaximumConcurrentOperations)
                    return PhotonCadPreviewResourceResponse.NotFound();
                _resources.Remove(token);
                var streamId = ++_streamSequence;
                var stream = new RevocablePreviewStream(preview.Content, () => ReleaseStream(streamId));
                _activeStreams.Add(streamId, stream);
                var digestBytes = Convert.FromHexString(preview.Receipt.ContentDigest[7..]);
                var headers = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Cache-Control"] = "no-store",
                    ["Content-Length"] = preview.Receipt.ByteLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["Content-Type"] = PhotonCadPreviewContract.MediaType,
                    ["Cross-Origin-Resource-Policy"] = "same-origin",
                    ["Digest"] = "sha-256=" + Convert.ToBase64String(digestBytes),
                    ["X-Content-Type-Options"] = "nosniff",
                });
                return new PhotonCadPreviewResourceResponse(200, "ok", headers, stream);
            }
        }
        catch (Exception exception) when (exception is PhotonCadPreviewException or ObjectDisposedException or InvalidOperationException)
        {
            return PhotonCadPreviewResourceResponse.NotFound();
        }
    }

    public void RevokeProject(PhotonCadPreviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_sync)
        {
            if (_disposed) return;
            RevokeProjectLocked(context.RendererGeneration, context.RendererSessionId, context.CadSessionId, context.ProjectId);
        }
    }

    public void RevokeRendererGeneration(long generation, string rendererSessionId)
    {
        PreviewGuards.Identifier(rendererSessionId, nameof(rendererSessionId));
        lock (_sync)
        {
            if (_disposed) return;
            var previews = _previews.Values.Where(value => value.Receipt.Context.RendererGeneration == generation
                && StringComparer.Ordinal.Equals(value.Receipt.Context.RendererSessionId, rendererSessionId)).Select(value => value.Receipt.PreviewId).ToHashSet(StringComparer.Ordinal);
            foreach (var id in previews) _previews.Remove(id);
            foreach (var token in _resources.Where(pair => previews.Contains(pair.Value.PreviewId)).Select(pair => pair.Key).ToArray()) _resources.Remove(token);
            RevokeActiveStreamsLocked();
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _previews.Clear();
            _resources.Clear();
            RevokeActiveStreamsLocked();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            _previews.Clear();
            _resources.Clear();
            RevokeActiveStreamsLocked();
        }
        // Operations are synchronous and every state transition is guarded by _sync. Leaving the
        // small semaphore for GC avoids racing Dispose against a lease that must still Release.
        return ValueTask.CompletedTask;
    }

    private IDisposable Enter()
    {
        var acquired = false;
        try
        {
            if (!_capacity.Wait(0)) throw PreviewGuards.Failure("preview_capacity_exceeded");
            acquired = true;
            lock (_sync) ThrowIfDisposed();
            return new CapacityLease(_capacity);
        }
        catch (ObjectDisposedException)
        {
            if (acquired) _capacity.Release();
            throw PreviewGuards.Failure("preview_custody_disposed");
        }
        catch
        {
            if (acquired) _capacity.Release();
            throw;
        }
    }

    private void EnsureCapacityLocked(long incoming)
    {
        while (_previews.Count >= _options.MaximumPreviews || checked(_previews.Values.Sum(value => value.Receipt.ByteLength) + incoming) > _options.MaximumTotalBytes)
        {
            var oldest = _previews.Values.OrderBy(value => value.Sequence).FirstOrDefault()
                ?? throw PreviewGuards.Failure("preview_capacity_exceeded");
            _previews.Remove(oldest.Receipt.PreviewId);
            foreach (var token in _resources.Where(pair => pair.Value.PreviewId == oldest.Receipt.PreviewId).Select(pair => pair.Key).ToArray()) _resources.Remove(token);
        }
    }

    private void CleanupExpiredLocked()
    {
        foreach (var preview in _previews.Values.Where(value => value.Deadline.Expired(_time)).ToArray()) _previews.Remove(preview.Receipt.PreviewId);
        foreach (var resource in _resources.Where(pair => pair.Value.Deadline.Expired(_time) || !_previews.ContainsKey(pair.Value.PreviewId)).Select(pair => pair.Key).ToArray()) _resources.Remove(resource);
    }

    private void RevokeProjectLocked(long generation, string rendererSessionId, string cadSessionId, string projectId)
    {
        var ids = _previews.Values.Where(value => value.Receipt.Context.RendererGeneration == generation
            && StringComparer.Ordinal.Equals(value.Receipt.Context.RendererSessionId, rendererSessionId)
            && StringComparer.Ordinal.Equals(value.Receipt.Context.CadSessionId, cadSessionId)
            && StringComparer.Ordinal.Equals(value.Receipt.Context.ProjectId, projectId)).Select(value => value.Receipt.PreviewId).ToHashSet(StringComparer.Ordinal);
        foreach (var id in ids) _previews.Remove(id);
        foreach (var token in _resources.Where(pair => ids.Contains(pair.Value.PreviewId)).Select(pair => pair.Key).ToArray()) _resources.Remove(token);
        RevokeActiveStreamsLocked();
    }

    private void RevokeActiveStreamsLocked()
    {
        foreach (var stream in _activeStreams.Values.ToArray()) stream.Revoke();
        _activeStreams.Clear();
    }

    private void ReleaseStream(long id)
    {
        lock (_sync) _activeStreams.Remove(id);
    }

    private bool TargetsNamespace(Uri uri) => uri.IsAbsoluteUri
        && uri.Scheme.Equals(_options.WorkbenchOrigin.Scheme, StringComparison.OrdinalIgnoreCase)
        && uri.Host.Equals(_options.WorkbenchOrigin.Host, StringComparison.OrdinalIgnoreCase)
        && uri.Port == _options.WorkbenchOrigin.Port
        && uri.AbsolutePath.StartsWith(_options.ResourcePathPrefix, StringComparison.Ordinal);

    private bool ValidResourceUri(Uri uri, out string token)
    {
        token = "";
        if (uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.OriginalString.Contains('%')) return false;
        token = uri.AbsolutePath[_options.ResourcePathPrefix.Length..];
        return token.Length == 43 && token.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw PreviewGuards.Failure("preview_custody_disposed");
    }

    private static bool FixedDigestEquals(string left, string right)
    {
        var a = Encoding.ASCII.GetBytes(left.ToLowerInvariant());
        var b = Encoding.ASCII.GetBytes(right.ToLowerInvariant());
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private sealed class StoredPreview(PhotonCadPreviewReceipt receipt, byte[] content, Deadline deadline, long sequence)
    {
        internal PhotonCadPreviewReceipt Receipt { get; } = receipt;
        internal byte[] Content { get; } = content;
        internal Deadline Deadline { get; } = deadline;
        internal long Sequence { get; set; } = sequence;
    }
    private sealed record StoredResource(string Token, string PreviewId, PhotonCadPreviewContext Context, Deadline Deadline);
    private readonly record struct Deadline(long Timestamp, DateTimeOffset ExpiresAtUtc)
    {
        internal static Deadline Start(TimeProvider time, TimeSpan ttl) => new(checked(time.GetTimestamp() + (long)(ttl.TotalSeconds * time.TimestampFrequency)), time.GetUtcNow().Add(ttl));
        internal static Deadline StartCapped(TimeProvider time, TimeSpan ttl, Deadline cap)
        {
            var candidate = Start(time, ttl);
            return candidate.Timestamp <= cap.Timestamp ? candidate : cap;
        }
        internal bool Expired(TimeProvider time) => time.GetTimestamp() >= Timestamp;
    }
    private sealed class CapacityLease(SemaphoreSlim capacity) : IDisposable
    {
        private SemaphoreSlim? _capacity = capacity;
        public void Dispose() => Interlocked.Exchange(ref _capacity, null)?.Release();
    }
    private sealed class RevocablePreviewStream : MemoryStream
    {
        private Action? _release;
        internal RevocablePreviewStream(byte[] bytes, Action release) : base(bytes, 0, bytes.Length, writable: false, publiclyVisible: false) => _release = release;
        internal void Revoke() => Dispose();
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Interlocked.Exchange(ref _release, null)?.Invoke();
        }
    }
}

internal static class PhotonCadGlbEnvelopeValidator
{
    private const uint Magic = 0x46546C67;
    private const uint JsonChunk = 0x4E4F534A;
    private const uint BinaryChunk = 0x004E4942;

    internal static void Validate(ReadOnlySpan<byte> bytes, IReadOnlySet<string> expectedEntityIds)
    {
        if (bytes.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != 2
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) != bytes.Length)
            throw PreviewGuards.Failure("invalid_glb_header");
        var offset = 12;
        JsonDocument? document = null;
        var binarySeen = false;
        try
        {
            while (offset < bytes.Length)
            {
                if (offset > bytes.Length - 8) throw PreviewGuards.Failure("invalid_glb_chunk");
                var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]));
                var type = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 4)..]);
                var start = offset + 8;
                var end = checked(start + length);
                if (length <= 0 || end > bytes.Length || end % 4 != 0) throw PreviewGuards.Failure("invalid_glb_chunk");
                if (type == JsonChunk)
                {
                    if (document is not null || offset != 12 || length > 32 * 1024 * 1024) throw PreviewGuards.Failure("invalid_glb_json");
                    var json = bytes[start..end];
                    while (!json.IsEmpty && json[^1] is 0 or 0x20) json = json[..^1];
                    try { document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 64 }); }
                    catch (JsonException) { throw PreviewGuards.Failure("invalid_glb_json"); }
                }
                else if (type == BinaryChunk)
                {
                    if (binarySeen) throw PreviewGuards.Failure("invalid_glb_binary");
                    binarySeen = true;
                }
                else throw PreviewGuards.Failure("unsupported_glb_chunk");
                offset = end;
            }
            if (offset != bytes.Length || document is null || !binarySeen) throw PreviewGuards.Failure("incomplete_glb");
            ValidateDocument(document.RootElement, expectedEntityIds);
        }
        finally { document?.Dispose(); }
    }

    private static void ValidateDocument(JsonElement root, IReadOnlySet<string> expectedEntityIds)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("asset", out var asset)
            || asset.ValueKind != JsonValueKind.Object || !asset.TryGetProperty("version", out var version)
            || version.GetString() != "2.0" || !root.TryGetProperty("nodes", out var nodes)
            || nodes.ValueKind != JsonValueKind.Array || nodes.GetArrayLength() != expectedEntityIds.Count)
            throw PreviewGuards.Failure("glb_binding_mismatch");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<(JsonElement Value, int Depth)>();
        pending.Push((root, 0));
        var inspected = 0;
        while (pending.Count != 0)
        {
            var (value, depth) = pending.Pop();
            if (++inspected > 250_000 || depth > 64) throw PreviewGuards.Failure("glb_resource_limit");
            if (value.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in value.EnumerateObject())
                {
                    if (property.Name.Equals("uri", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                        throw PreviewGuards.Failure("external_glb_resource_forbidden");
                    pending.Push((property.Value, depth + 1));
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (var item in value.EnumerateArray()) pending.Push((item, depth + 1));
        }
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty("extras", out var extras)
                || extras.ValueKind != JsonValueKind.Object || !extras.TryGetProperty("photonEntityId", out var id)
                || id.ValueKind != JsonValueKind.String || !ids.Add(PreviewGuards.Identifier(id.GetString(), "glb_entity")))
                throw PreviewGuards.Failure("glb_entity_binding_mismatch");
        }
        if (!ids.SetEquals(expectedEntityIds)) throw PreviewGuards.Failure("glb_entity_binding_mismatch");
    }
}
