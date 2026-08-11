using System.Collections.ObjectModel;
using System.Security.Cryptography;

namespace PhotonCadFileConversion;

/// <summary>Formats that can be identified without trusting a file name.</summary>
public enum PhotonCadExternalFormat
{
    Unknown = 0,
    StepPart21 = 1,
    Glb = 2,
    OleCompoundDocument = 3,
}

/// <summary>How an accepted source relates to a canonical Photon CAD payload.</summary>
public enum PhotonCadConversionMode
{
    Unavailable = 0,
    VerifiedBytePreservingPassThrough = 1,
}

/// <summary>Whether imported assembly semantics were normalized by this bounded authority.</summary>
public enum PhotonCadAssemblyDisposition
{
    NotApplicable = 0,
    PreservedOpaquePart21 = 1,
    NotNormalized = 2,
}

/// <summary>One discovered input format and its actual conversion availability.</summary>
public sealed class PhotonCadConversionCapability
{
    public PhotonCadConversionCapability(
        PhotonCadExternalFormat format,
        bool available,
        string reasonCode,
        string targetMediaType,
        bool assemblyPreservationPossible)
    {
        Format = format;
        Available = available;
        ReasonCode = PhotonCadFileConversionGuard.RequireToken(reasonCode, nameof(reasonCode));
        TargetMediaType = PhotonCadFileConversionGuard.RequireMediaType(targetMediaType, nameof(targetMediaType));
        AssemblyPreservationPossible = assemblyPreservationPossible;
    }

    public PhotonCadExternalFormat Format { get; }
    public bool Available { get; }
    public string ReasonCode { get; }
    public string TargetMediaType { get; }
    public bool AssemblyPreservationPossible { get; }
}

/// <summary>A pathless, bounded external-file request. The caller owns any picker and file custody.</summary>
public sealed class PhotonCadConversionRequest
{
    private readonly byte[] _content;

    public PhotonCadConversionRequest(string fileName, ReadOnlyMemory<byte> content)
        : this(fileName, content.ToArray(), takeOwnership: true)
    {
    }

    internal PhotonCadConversionRequest(string fileName, byte[] content, bool takeOwnership)
    {
        FileName = PhotonCadFileConversionGuard.RequireFileName(fileName, nameof(fileName));
        if (content.Length == 0) throw PhotonCadFileConversionGuard.Failure("source_empty", nameof(content));
        _content = takeOwnership ? content : content.ToArray();
    }

    public string FileName { get; }
    public ReadOnlyMemory<byte> Content => _content.ToArray();
}

/// <summary>Immutable, digest-bound canonical artifact returned by a conversion authority.</summary>
public sealed class PhotonCadConvertedArtifact
{
    private readonly byte[] _content;

    public PhotonCadConvertedArtifact(string mediaType, ReadOnlyMemory<byte> content)
    {
        MediaType = PhotonCadFileConversionGuard.RequireMediaType(mediaType, nameof(mediaType));
        if (content.Length == 0) throw PhotonCadFileConversionGuard.Failure("artifact_empty", nameof(content));
        _content = content.ToArray();
        ByteLength = _content.Length;
        Digest = Convert.ToHexStringLower(SHA256.HashData(_content));
    }

    public string MediaType { get; }
    public ReadOnlyMemory<byte> Content => _content.ToArray();
    public long ByteLength { get; }
    public string Digest { get; }
}

/// <summary>Truthful conversion receipt. No source path or native-handle data is exposed.</summary>
public sealed class PhotonCadConversionResult
{
    public PhotonCadConversionResult(
        PhotonCadExternalFormat sourceFormat,
        PhotonCadConversionMode mode,
        PhotonCadAssemblyDisposition assemblyDisposition,
        PhotonCadConvertedArtifact artifact,
        bool containsAssemblyConstructs)
    {
        SourceFormat = sourceFormat;
        Mode = mode;
        AssemblyDisposition = assemblyDisposition;
        Artifact = artifact ?? throw PhotonCadFileConversionGuard.Failure("artifact_required", nameof(artifact));
        ContainsAssemblyConstructs = containsAssemblyConstructs;
    }

    public PhotonCadExternalFormat SourceFormat { get; }
    public PhotonCadConversionMode Mode { get; }
    public PhotonCadAssemblyDisposition AssemblyDisposition { get; }
    public PhotonCadConvertedArtifact Artifact { get; }
    public bool ContainsAssemblyConstructs { get; }
}

/// <summary>Provider-neutral external CAD conversion boundary.</summary>
public interface IPhotonCadFileConversionAuthority
{
    int MaximumInputBytes { get; }
    IReadOnlyList<PhotonCadConversionCapability> Capabilities { get; }
    ValueTask<PhotonCadConversionResult> ConvertAsync(PhotonCadConversionRequest request, CancellationToken cancellationToken = default);
}

internal static class PhotonCadFileConversionGuard
{
    internal static InvalidDataException Failure(string code, string field) => new($"{code}:{field}");

    internal static string RequireFileName(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || value.Contains(Path.DirectorySeparatorChar) || value.Contains(Path.AltDirectorySeparatorChar))
            throw Failure("invalid_file_name", field);
        return value;
    }

    internal static string RequireToken(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
            throw Failure("invalid_token", field);
        return value;
    }

    internal static string RequireMediaType(string value, string field)
    {
        if (value is not ("application/step" or "model/gltf-binary")) throw Failure("invalid_media_type", field);
        return value;
    }
}
