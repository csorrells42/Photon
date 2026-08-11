namespace PhotonCadRuntime;

public sealed class CadArtifactReadRequest
{
    public CadArtifactReadRequest(
        CadRequestId requestId,
        CadSessionHandle session,
        CadArtifactHandle artifact,
        long maximumByteLength)
    {
        RequestId = requestId ?? throw new CadContractException("required", nameof(requestId));
        Session = session ?? throw new CadContractException("required", nameof(session));
        Artifact = artifact ?? throw new CadContractException("required", nameof(artifact));
        if (maximumByteLength <= 0 || maximumByteLength > CadContractLimits.MaximumBrokeredArtifactBytes)
            throw new CadContractException("invalid_artifact_read_bound", nameof(maximumByteLength));
        MaximumByteLength = maximumByteLength;
    }

    public CadRequestId RequestId { get; }
    public CadSessionHandle Session { get; }
    public CadArtifactHandle Artifact { get; }
    public long MaximumByteLength { get; }
}

public sealed class CadArtifactDescriptor
{
    public CadArtifactDescriptor(
        CadSessionHandle session,
        CadArtifactHandle artifact,
        string contentDigest,
        long byteLength,
        string mediaType)
    {
        Session = session ?? throw new CadContractException("required", nameof(session));
        Artifact = artifact ?? throw new CadContractException("required", nameof(artifact));
        ContentDigest = ContractGuards.Sha256(contentDigest, nameof(contentDigest));
        ByteLength = ContractGuards.ByteLength(byteLength, nameof(byteLength));
        if (ByteLength > CadContractLimits.MaximumBrokeredArtifactBytes)
            throw new CadContractException("artifact_too_large_to_broker", nameof(byteLength));
        MediaType = ContractGuards.RequiredText(mediaType, nameof(mediaType), 128);
    }

    public int ContractVersion => CadContractVersions.Host;
    public CadSessionHandle Session { get; }
    public CadArtifactHandle Artifact { get; }
    public string ContentDigest { get; }
    public long ByteLength { get; }
    public string MediaType { get; }
}

/// <summary>
/// A bounded, read-only lease over an artifact whose bytes have been checked
/// against its immutable receipt while the underlying file is locked against
/// writes and deletion. Disposing the stream releases that lock.
/// </summary>
public sealed class CadArtifactReadLease : Stream
{
    private readonly Stream _content;
    private readonly Action<CadArtifactReadLease>? _onDisposed;
    private int _disposed;

    internal CadArtifactReadLease(
        CadArtifactDescriptor descriptor,
        Stream content,
        Action<CadArtifactReadLease>? onDisposed = null)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        _content = content ?? throw new ArgumentNullException(nameof(content));
        if (!content.CanRead || content.Length != descriptor.ByteLength)
            throw new CadContractException("artifact_lease_mismatch", nameof(content));
        _onDisposed = onDisposed;
    }

    public CadArtifactDescriptor Descriptor { get; }
    public override bool CanRead => _content.CanRead;
    public override bool CanSeek => _content.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _content.Length;
    public override long Position { get => _content.Position; set => _content.Position = value; }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => _content.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => _content.Read(buffer);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _content.ReadAsync(buffer, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => _content.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!disposing || Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            base.Dispose(disposing);
            return;
        }

        try
        {
            _content.Dispose();
        }
        finally
        {
            _onDisposed?.Invoke(this);
            base.Dispose(disposing);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            GC.SuppressFinalize(this);
            return;
        }

        try
        {
            await _content.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _onDisposed?.Invoke(this);
            GC.SuppressFinalize(this);
        }
    }
}
