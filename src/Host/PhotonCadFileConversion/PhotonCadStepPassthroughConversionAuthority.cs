namespace PhotonCadFileConversion;

/// <summary>
/// The v1 external-intake authority. It accepts only complete generic STEP Part-21 and retains exact
/// bytes; no geometry is regenerated and no assembly hierarchy is inferred or flattened.
/// </summary>
public sealed class PhotonCadStepPassthroughConversionAuthority : IPhotonCadFileConversionAuthority
{
    public const int DefaultMaximumInputBytes = 64 * 1024 * 1024;

    private static readonly IReadOnlyList<PhotonCadConversionCapability> CapabilityList = Array.AsReadOnly(new[]
    {
        new PhotonCadConversionCapability(PhotonCadExternalFormat.StepPart21, true, "verified-byte-preserving-passthrough", "application/step", true),
        new PhotonCadConversionCapability(PhotonCadExternalFormat.Glb, false, "glb-import-authority-unavailable", "model/gltf-binary", false),
        new PhotonCadConversionCapability(PhotonCadExternalFormat.OleCompoundDocument, false, "compound-document-converter-unavailable", "application/step", false),
        new PhotonCadConversionCapability(PhotonCadExternalFormat.Unknown, false, "format-authority-unavailable", "application/step", false),
    });

    public PhotonCadStepPassthroughConversionAuthority(int maximumInputBytes = DefaultMaximumInputBytes)
    {
        if (maximumInputBytes is < 48 or > DefaultMaximumInputBytes) throw PhotonCadFileConversionGuard.Failure("invalid_maximum_input_bytes", nameof(maximumInputBytes));
        MaximumInputBytes = maximumInputBytes;
    }

    public int MaximumInputBytes { get; }
    public IReadOnlyList<PhotonCadConversionCapability> Capabilities => CapabilityList;

    /// <summary>
    /// Reads exactly one caller-owned, seekable source stream. The source is never closed; the length
    /// cap is checked before allocating, and an additional byte check catches a concurrent file grow.
    /// </summary>
    public async ValueTask<PhotonCadConversionResult> ConvertAsync(
        string fileName,
        Stream source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        if (!source.CanRead || !source.CanSeek) throw PhotonCadFileConversionGuard.Failure("source_stream_not_seekable", nameof(source));
        long remaining;
        try { remaining = checked(source.Length - source.Position); }
        catch (Exception exception) when (exception is IOException or NotSupportedException)
        {
            throw PhotonCadFileConversionGuard.Failure("source_length_unavailable", nameof(source));
        }
        if (remaining <= 0 || remaining > MaximumInputBytes) throw PhotonCadFileConversionGuard.Failure("source_exceeds_cap", nameof(source));
        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)remaining));
        await source.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (source.ReadByte() != -1) throw PhotonCadFileConversionGuard.Failure("source_identity_changed", nameof(source));
        return await ConvertAsync(new PhotonCadConversionRequest(fileName, bytes, takeOwnership: true), cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<PhotonCadConversionResult> ConvertAsync(PhotonCadConversionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        var content = request.Content.Span;
        if (content.Length > MaximumInputBytes) throw PhotonCadFileConversionGuard.Failure("source_exceeds_cap", nameof(request));
        var format = PhotonCadFileFormatSniffer.Detect(content);
        if (format != PhotonCadExternalFormat.StepPart21)
        {
            var reason = format == PhotonCadExternalFormat.OleCompoundDocument && IsInventorExtension(request.FileName)
                ? "autodesk-inventor-authority-unavailable"
                : format == PhotonCadExternalFormat.Glb
                    ? "glb-import-authority-unavailable"
                    : "source_format_unsupported";
            throw PhotonCadFileConversionGuard.Failure(reason, nameof(request));
        }
        PhotonCadFileFormatSniffer.RequireMatchingStepExtension(request.FileName);
        PhotonCadStepPart21Validator.Validate(content);
        cancellationToken.ThrowIfCancellationRequested();
        var hasAssemblyConstructs = PhotonCadFileFormatSniffer.ContainsAssemblyConstructs(content);
        return ValueTask.FromResult(new PhotonCadConversionResult(
            PhotonCadExternalFormat.StepPart21,
            PhotonCadConversionMode.VerifiedBytePreservingPassThrough,
            hasAssemblyConstructs ? PhotonCadAssemblyDisposition.PreservedOpaquePart21 : PhotonCadAssemblyDisposition.NotApplicable,
            new PhotonCadConvertedArtifact("application/step", content.ToArray()),
            hasAssemblyConstructs));
    }

    private static bool IsInventorExtension(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return StringComparer.OrdinalIgnoreCase.Equals(extension, ".ipt") || StringComparer.OrdinalIgnoreCase.Equals(extension, ".iam");
    }
}
