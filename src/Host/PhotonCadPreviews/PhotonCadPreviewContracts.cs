using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using PhotonCadProjects;
using PhotonCadProjects.Codec;

namespace PhotonCadPreviews;

public static class PhotonCadPreviewContract
{
    public const int Version = 1;
    public const string MediaType = "model/gltf-binary";
    public const long DefaultMaximumPreviewBytes = 128L * 1024 * 1024;
}

public sealed class PhotonCadPreviewException : Exception
{
    public PhotonCadPreviewException(string code) : base(code) => Code = PreviewGuards.Reason(code);
    public string Code { get; }
}

internal static partial class PreviewGuards
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex DigestPattern();
    [GeneratedRegex("^[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    internal static string Identifier(string? value, string field) =>
        value is not null && IdentifierPattern().IsMatch(value) ? value : throw Failure($"invalid_{field}");

    internal static string Digest(string? value, string field)
    {
        if (value is null) throw Failure($"invalid_{field}");
        var normalized = value.ToLowerInvariant();
        if (!normalized.StartsWith("sha256:", StringComparison.Ordinal) || !DigestPattern().IsMatch(normalized[7..]))
            throw Failure($"invalid_{field}");
        return normalized;
    }

    internal static string Token(string? value, string field) =>
        value is not null && TokenPattern().IsMatch(value) ? value : throw Failure($"invalid_{field}");

    internal static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    internal static string Reason(string? value) => value is not null && value.Length is >= 1 and <= 96
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
            ? value
            : throw new ArgumentException("A bounded reason code is required.", nameof(value));

    internal static PhotonCadPreviewException Failure(string code) => new(code);
}

public sealed record PhotonCadPreviewContext
{
    public PhotonCadPreviewContext(long rendererGeneration, string rendererSessionId, string cadSessionId, string projectId, long revision)
    {
        if (rendererGeneration < 0 || revision < 0) throw PreviewGuards.Failure("invalid_context_revision");
        RendererGeneration = rendererGeneration;
        RendererSessionId = PreviewGuards.Identifier(rendererSessionId, nameof(rendererSessionId));
        CadSessionId = PreviewGuards.Identifier(cadSessionId, nameof(cadSessionId));
        ProjectId = PreviewGuards.Identifier(projectId, nameof(projectId));
        Revision = revision;
    }

    public long RendererGeneration { get; }
    public string RendererSessionId { get; }
    public string CadSessionId { get; }
    public string ProjectId { get; }
    public long Revision { get; }
}

public sealed record PhotonCadPreviewVector(double X, double Y, double Z);
public sealed record PhotonCadPreviewBounds(PhotonCadPreviewVector Minimum, PhotonCadPreviewVector Maximum);

public sealed record PhotonCadPreviewReceipt
{
    public PhotonCadPreviewReceipt(string previewId, PhotonCadPreviewContext context, string contentDigest,
        long byteLength, PhotonCadProjectUnit units, PhotonCadPreviewBounds bounds, int entityCount)
    {
        PreviewId = PreviewGuards.Identifier(previewId, nameof(previewId));
        Context = context ?? throw PreviewGuards.Failure("invalid_context");
        ContentDigest = PreviewGuards.Digest(contentDigest, nameof(contentDigest));
        if (byteLength is < 20 or > PhotonCadPreviewContract.DefaultMaximumPreviewBytes)
            throw PreviewGuards.Failure("invalid_byte_length");
        if (!Enum.IsDefined(units) || entityCount is < 1 or > 100_000)
            throw PreviewGuards.Failure("invalid_preview_metadata");
        Bounds = bounds ?? throw PreviewGuards.Failure("invalid_bounds");
        ValidateBounds(bounds);
        ByteLength = byteLength;
        Units = units;
        EntityCount = entityCount;
    }

    public string PreviewId { get; }
    public PhotonCadPreviewContext Context { get; }
    public string ContentDigest { get; }
    public long ByteLength { get; }
    public PhotonCadProjectUnit Units { get; }
    public PhotonCadPreviewBounds Bounds { get; }
    public int EntityCount { get; }

    private static void ValidateBounds(PhotonCadPreviewBounds bounds)
    {
        var values = new[] { bounds.Minimum.X, bounds.Minimum.Y, bounds.Minimum.Z, bounds.Maximum.X, bounds.Maximum.Y, bounds.Maximum.Z };
        if (values.Any(value => !double.IsFinite(value) || Math.Abs(value) > 1_000_000_000)
            || bounds.Minimum.X > bounds.Maximum.X || bounds.Minimum.Y > bounds.Maximum.Y || bounds.Minimum.Z > bounds.Maximum.Z)
            throw PreviewGuards.Failure("invalid_bounds");
    }
}

public sealed record PhotonCadCommittedPreviewReadback
{
    public PhotonCadCommittedPreviewReadback(PhotonCadPreviewContext context, PhotonCadCanonicalProject project)
    {
        Context = context ?? throw PreviewGuards.Failure("invalid_context");
        Project = project ?? throw PreviewGuards.Failure("invalid_project");
    }
    public PhotonCadPreviewContext Context { get; }
    public PhotonCadCanonicalProject Project { get; }
}

public sealed record PhotonCadPreviewResolveRequest
{
    public PhotonCadPreviewResolveRequest(string requestId, PhotonCadPreviewContext context, string previewId,
        string expectedDigest, long maximumBytes)
    {
        RequestId = PreviewGuards.Identifier(requestId, nameof(requestId));
        Context = context ?? throw PreviewGuards.Failure("invalid_context");
        PreviewId = PreviewGuards.Identifier(previewId, nameof(previewId));
        ExpectedDigest = PreviewGuards.Digest(expectedDigest, nameof(expectedDigest));
        if (maximumBytes is < 20 or > PhotonCadPreviewContract.DefaultMaximumPreviewBytes)
            throw PreviewGuards.Failure("invalid_maximum_bytes");
        MaximumBytes = maximumBytes;
    }
    public string RequestId { get; }
    public PhotonCadPreviewContext Context { get; }
    public string PreviewId { get; }
    public string ExpectedDigest { get; }
    public long MaximumBytes { get; }
}

public sealed record PhotonCadPreviewAsset
{
    public PhotonCadPreviewAsset(Uri url, string contentDigest, long byteLength, DateTimeOffset expiresAtUtc)
    {
        Url = url ?? throw PreviewGuards.Failure("invalid_resource_url");
        ContentDigest = PreviewGuards.Digest(contentDigest, nameof(contentDigest));
        if (byteLength is < 20 or > PhotonCadPreviewContract.DefaultMaximumPreviewBytes || expiresAtUtc.Offset != TimeSpan.Zero)
            throw PreviewGuards.Failure("invalid_resource_metadata");
        ByteLength = byteLength;
        ExpiresAtUtc = expiresAtUtc;
    }
    public Uri Url { get; }
    public string ContentDigest { get; }
    public long ByteLength { get; }
    public string MediaType => PhotonCadPreviewContract.MediaType;
    public DateTimeOffset ExpiresAtUtc { get; }
}

public sealed record PhotonCadPreviewResourceRequest(string Method, Uri RequestUri, bool RendererAuthorized,
    PhotonCadPreviewContext? Context, IReadOnlyDictionary<string, string>? Headers = null);

public sealed class PhotonCadPreviewResourceResponse : IDisposable
{
    internal PhotonCadPreviewResourceResponse(int statusCode, string reason, IReadOnlyDictionary<string, string> headers, Stream? content)
    {
        StatusCode = statusCode;
        Reason = PreviewGuards.Reason(reason);
        Headers = headers;
        Content = content;
    }
    public int StatusCode { get; }
    public string Reason { get; }
    public IReadOnlyDictionary<string, string> Headers { get; }
    public Stream? Content { get; }
    public void Dispose() => Content?.Dispose();

    internal static PhotonCadPreviewResourceResponse NotFound() => new(404, "resource_unavailable",
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Cache-Control"] = "no-store",
            ["Content-Length"] = "0",
            ["Cross-Origin-Resource-Policy"] = "same-origin",
            ["X-Content-Type-Options"] = "nosniff",
        }), null);
}

public sealed record PhotonCadPreviewCustodyOptions
{
    public PhotonCadPreviewCustodyOptions(Uri workbenchOrigin, string resourcePathPrefix = "/api/photon-cad/previews/",
        long maximumPreviewBytes = PhotonCadPreviewContract.DefaultMaximumPreviewBytes,
        long maximumTotalBytes = 512L * 1024 * 1024, int maximumPreviews = 8, int maximumConcurrentOperations = 8,
        int maximumOutstandingResources = 64,
        TimeSpan? previewTimeToLive = null, TimeSpan? resourceTimeToLive = null)
    {
        ArgumentNullException.ThrowIfNull(workbenchOrigin);
        if (!workbenchOrigin.IsAbsoluteUri || workbenchOrigin.UserInfo.Length != 0 || workbenchOrigin.Scheme is not ("http" or "https")
            || workbenchOrigin.AbsolutePath != "/" || workbenchOrigin.Query.Length != 0 || workbenchOrigin.Fragment.Length != 0)
            throw PreviewGuards.Failure("invalid_workbench_origin");
        if (!resourcePathPrefix.StartsWith('/') || !resourcePathPrefix.EndsWith('/') || resourcePathPrefix.Contains("..", StringComparison.Ordinal)
            || resourcePathPrefix.Contains('\\') || resourcePathPrefix.Contains('%')) throw PreviewGuards.Failure("invalid_resource_prefix");
        if (maximumPreviewBytes is < 20 or > PhotonCadPreviewContract.DefaultMaximumPreviewBytes || maximumTotalBytes < maximumPreviewBytes
            || maximumTotalBytes > 2L * 1024 * 1024 * 1024 || maximumPreviews is < 1 or > 64 || maximumConcurrentOperations is < 1 or > 32
            || maximumOutstandingResources is < 1 or > 256)
            throw PreviewGuards.Failure("invalid_capacity_policy");
        var previewTtl = previewTimeToLive ?? TimeSpan.FromMinutes(10);
        var resourceTtl = resourceTimeToLive ?? TimeSpan.FromMinutes(2);
        if (previewTtl < TimeSpan.FromSeconds(30) || previewTtl > TimeSpan.FromHours(1)
            || resourceTtl < TimeSpan.FromSeconds(15) || resourceTtl > previewTtl) throw PreviewGuards.Failure("invalid_ttl_policy");
        WorkbenchOrigin = new Uri(workbenchOrigin.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);
        ResourcePathPrefix = resourcePathPrefix;
        MaximumPreviewBytes = maximumPreviewBytes;
        MaximumTotalBytes = maximumTotalBytes;
        MaximumPreviews = maximumPreviews;
        MaximumConcurrentOperations = maximumConcurrentOperations;
        MaximumOutstandingResources = maximumOutstandingResources;
        PreviewTimeToLive = previewTtl;
        ResourceTimeToLive = resourceTtl;
    }
    public Uri WorkbenchOrigin { get; }
    public string ResourcePathPrefix { get; }
    public long MaximumPreviewBytes { get; }
    public long MaximumTotalBytes { get; }
    public int MaximumPreviews { get; }
    public int MaximumConcurrentOperations { get; }
    public int MaximumOutstandingResources { get; }
    public TimeSpan PreviewTimeToLive { get; }
    public TimeSpan ResourceTimeToLive { get; }
}
