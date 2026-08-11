using PhotonCadRuntime;

namespace PhotonCadArtifacts;

internal sealed record PhotonCadRuntimeArtifactBinding(
    CadSessionHandle Session,
    CadArtifactHandle Artifact,
    string DisplayName);

internal interface IPhotonCadRuntimeArtifactBindingResolver
{
    PhotonCadRuntimeArtifactBinding? Resolve(PhotonCadArtifactSourceRequest request);
}

/// <summary>
/// Internal adapter over the authoritative runtime lease contract. It remains
/// unmounted until HermesDesktop supplies an authoritative session/handle map.
/// </summary>
internal sealed class PhotonCadRuntimeArtifactSource : IPhotonCadArtifactSource
{
    private readonly ICadRuntimeBroker _broker;
    private readonly IPhotonCadRuntimeArtifactBindingResolver _bindings;
    private readonly long _maximumBytes;

    internal PhotonCadRuntimeArtifactSource(
        ICadRuntimeBroker broker,
        IPhotonCadRuntimeArtifactBindingResolver bindings,
        long maximumBytes = CadContractLimits.MaximumBrokeredArtifactBytes)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
        if (maximumBytes <= 0 || maximumBytes > CadContractLimits.MaximumBrokeredArtifactBytes)
            throw new PhotonCadArtifactException("invalid_runtime_artifact_bound");
        _maximumBytes = maximumBytes;
    }

    public async ValueTask<PhotonCadArtifactSourceLease> AcquireAsync(
        PhotonCadArtifactSourceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var binding = _bindings.Resolve(request) ?? throw new PhotonCadArtifactException("source_binding_unavailable");
        if (!binding.Artifact.Value.Equals(request.SourceArtifactId, StringComparison.Ordinal))
            throw new PhotonCadArtifactException("source_artifact_mismatch");

        var receiptRequest = new CadArtifactReadRequest(
            CadRequestId.New(),
            binding.Session,
            binding.Artifact,
            _maximumBytes);
        var receiptResult = await _broker.GetArtifactReceiptAsync(receiptRequest, cancellationToken).ConfigureAwait(false);
        var receipt = Required(receiptResult, cancellationToken);
        ValidateReceipt(receipt, binding);

        var leaseResult = await _broker.OpenArtifactReadAsync(
            new CadArtifactReadRequest(CadRequestId.New(), binding.Session, binding.Artifact, _maximumBytes),
            cancellationToken).ConfigureAwait(false);
        var runtimeLease = Required(leaseResult, cancellationToken);
        try
        {
            ValidateReceipt(runtimeLease.Descriptor, binding);
            if (!SameReceipt(receipt, runtimeLease.Descriptor) || !runtimeLease.CanRead || runtimeLease.CanWrite ||
                !runtimeLease.CanSeek || runtimeLease.Position != 0 || runtimeLease.Length != receipt.ByteLength)
                throw new PhotonCadArtifactException("runtime_lease_mismatch");
            var descriptor = new PhotonCadArtifactSourceDescriptor(
                request.SourceArtifactId,
                PhotonCadArtifactContract.StepKind,
                PhotonCadArtifactContract.StepMediaType,
                PhotonCadArtifactContract.UnspecifiedProfile,
                "sha256:" + receipt.ContentDigest.ToLowerInvariant(),
                receipt.ByteLength,
                binding.DisplayName);
            return new PhotonCadArtifactSourceLease(descriptor, runtimeLease, stableLockedRead: true);
        }
        catch
        {
            await runtimeLease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static T Required<T>(CadResult<T> result, CancellationToken cancellationToken) where T : class
    {
        if (result.Succeeded && result.Value is { } value) return value;
        if (cancellationToken.IsCancellationRequested || result.Error?.Code == "cancelled")
            throw new OperationCanceledException(cancellationToken);
        throw new PhotonCadArtifactException("runtime_artifact_unavailable");
    }

    private void ValidateReceipt(CadArtifactDescriptor descriptor, PhotonCadRuntimeArtifactBinding binding)
    {
        if (descriptor.Session != binding.Session || descriptor.Artifact != binding.Artifact ||
            descriptor.MediaType != PhotonCadArtifactContract.StepMediaType || descriptor.ByteLength <= 0 ||
            descriptor.ByteLength > _maximumBytes || descriptor.ContentDigest.Length != 64 ||
            descriptor.ContentDigest.Any(character => !Uri.IsHexDigit(character)))
            throw new PhotonCadArtifactException("runtime_receipt_mismatch");
    }

    private static bool SameReceipt(CadArtifactDescriptor left, CadArtifactDescriptor right) =>
        left.Session == right.Session && left.Artifact == right.Artifact &&
        left.ContentDigest.Equals(right.ContentDigest, StringComparison.OrdinalIgnoreCase) &&
        left.ByteLength == right.ByteLength && left.MediaType == right.MediaType;
}
