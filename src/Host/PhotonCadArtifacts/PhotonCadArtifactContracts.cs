using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace PhotonCadArtifacts;

public static class PhotonCadArtifactContract
{
    public const int Version = 1;
    public const string StepKind = "step-part21";
    public const string StepMediaType = "model/step";
    public const string UnspecifiedProfile = "unspecified";
    public const long AbsoluteMaximumArtifactBytes = 8L * 1024 * 1024 * 1024;
}

public sealed class PhotonCadArtifactException : Exception
{
    public PhotonCadArtifactException(string code) : base(code)
    {
        Code = PhotonCadArtifactGuards.Reason(code, nameof(code));
    }

    public string Code { get; }
}

internal static partial class PhotonCadArtifactGuards
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierExpression();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex DigestExpression();

    [GeneratedRegex("^[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenExpression();

    internal static string Identifier(string? value, string field)
    {
        if (value is null || !IdentifierExpression().IsMatch(value))
            throw new PhotonCadArtifactException($"invalid_{field}");
        return value;
    }

    internal static string Reason(string? value, string field)
    {
        if (value is null || value.Length is < 1 or > 96 ||
            !value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-'))
            throw new ArgumentException("A bounded reason code is required.", field);
        return value;
    }

    internal static string Digest(string? value, string field)
    {
        if (value is null) throw new PhotonCadArtifactException($"invalid_{field}");
        var normalized = value.ToLowerInvariant();
        if (!normalized.StartsWith("sha256:", StringComparison.Ordinal) ||
            !DigestExpression().IsMatch(normalized[7..]))
            throw new PhotonCadArtifactException($"invalid_{field}");
        return normalized;
    }

    internal static string Opaque(string? value, string prefix, string field)
    {
        if (value is null || !value.StartsWith(prefix + ":", StringComparison.Ordinal) ||
            !TokenExpression().IsMatch(value[(prefix.Length + 1)..]))
            throw new PhotonCadArtifactException($"invalid_{field}");
        return value;
    }

    internal static string NewOpaque(string prefix)
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{prefix}:{token}";
    }

    internal static long ByteLength(long value, long maximum, string field)
    {
        if (value <= 0 || value > maximum || maximum > PhotonCadArtifactContract.AbsoluteMaximumArtifactBytes)
            throw new PhotonCadArtifactException($"invalid_{field}");
        return value;
    }

    internal static string DisplayName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "photon-cad.step";
        var trimmed = value.Trim();
        var fileName = Path.GetFileName(trimmed);
        if (!string.Equals(fileName, trimmed, StringComparison.Ordinal))
            throw new PhotonCadArtifactException("invalid_display_name");
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var safe = new string(fileName.Select(character =>
            char.IsControl(character) || invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        if (safe.Length is < 1 or > 128) throw new PhotonCadArtifactException("invalid_display_name");
        if (!Path.GetExtension(safe).Equals(".step", StringComparison.OrdinalIgnoreCase))
            safe += ".step";
        if (safe.Length > 128) throw new PhotonCadArtifactException("invalid_display_name");
        return safe;
    }

    internal static void CurrentContext(
        IPhotonCadArtifactContextAuthority authority,
        PhotonCadArtifactContext expected)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (authority.Current() is not { } current || current != expected)
            throw new PhotonCadArtifactException("artifact_context_changed");
    }
}

public sealed record PhotonCadArtifactHandle
{
    public PhotonCadArtifactHandle(string value) => Value = PhotonCadArtifactGuards.Opaque(value, "cad-artifact", nameof(value));
    public string Value { get; }
    public static PhotonCadArtifactHandle New() => new(PhotonCadArtifactGuards.NewOpaque("cad-artifact"));
}

public sealed record PhotonCadArtifactResourceHandle
{
    public PhotonCadArtifactResourceHandle(string value) => Value = PhotonCadArtifactGuards.Opaque(value, "cad-resource", nameof(value));
    public string Value { get; }
    public string Token => Value["cad-resource:".Length..];
    public static PhotonCadArtifactResourceHandle New() => new(PhotonCadArtifactGuards.NewOpaque("cad-resource"));
}

public sealed record PhotonCadArtifactReviewHandle
{
    public PhotonCadArtifactReviewHandle(string value) => Value = PhotonCadArtifactGuards.Opaque(value, "cad-review", nameof(value));
    public string Value { get; }
    public static PhotonCadArtifactReviewHandle New() => new(PhotonCadArtifactGuards.NewOpaque("cad-review"));
}

public sealed record PhotonCadDestinationHandle
{
    public PhotonCadDestinationHandle(string value) => Value = PhotonCadArtifactGuards.Opaque(value, "cad-destination", nameof(value));
    public string Value { get; }
    public static PhotonCadDestinationHandle New() => new(PhotonCadArtifactGuards.NewOpaque("cad-destination"));
}

public sealed record PhotonCadCommitReceiptHandle
{
    public PhotonCadCommitReceiptHandle(string value) => Value = PhotonCadArtifactGuards.Opaque(value, "cad-commit", nameof(value));
    public string Value { get; }
    public static PhotonCadCommitReceiptHandle New() => new(PhotonCadArtifactGuards.NewOpaque("cad-commit"));
}

public sealed record PhotonCadArtifactContext
{
    public PhotonCadArtifactContext(
        string rendererSessionId,
        string controllerId,
        string cadSessionId,
        string projectId,
        long revision)
    {
        RendererSessionId = PhotonCadArtifactGuards.Identifier(rendererSessionId, nameof(rendererSessionId));
        ControllerId = PhotonCadArtifactGuards.Identifier(controllerId, nameof(controllerId));
        CadSessionId = PhotonCadArtifactGuards.Identifier(cadSessionId, nameof(cadSessionId));
        ProjectId = PhotonCadArtifactGuards.Identifier(projectId, nameof(projectId));
        if (revision < 0) throw new PhotonCadArtifactException("invalid_revision");
        Revision = revision;
    }

    public string RendererSessionId { get; }
    public string ControllerId { get; }
    public string CadSessionId { get; }
    public string ProjectId { get; }
    public long Revision { get; }
}

internal sealed record PhotonCadArtifactSourceRequest
{
    public PhotonCadArtifactSourceRequest(string sourceArtifactId, PhotonCadArtifactContext context)
    {
        SourceArtifactId = PhotonCadArtifactGuards.Identifier(sourceArtifactId, nameof(sourceArtifactId));
        Context = context ?? throw new PhotonCadArtifactException("invalid_context");
    }

    public string SourceArtifactId { get; }
    public PhotonCadArtifactContext Context { get; }
}

internal sealed record PhotonCadArtifactSourceDescriptor
{
    public PhotonCadArtifactSourceDescriptor(
        string sourceArtifactId,
        string kind,
        string mediaType,
        string profile,
        string contentDigest,
        long byteLength,
        string displayName)
    {
        SourceArtifactId = PhotonCadArtifactGuards.Identifier(sourceArtifactId, nameof(sourceArtifactId));
        if (kind != PhotonCadArtifactContract.StepKind ||
            mediaType != PhotonCadArtifactContract.StepMediaType ||
            profile != PhotonCadArtifactContract.UnspecifiedProfile)
            throw new PhotonCadArtifactException("artifact_format_unavailable");
        Kind = kind;
        MediaType = mediaType;
        Profile = profile;
        ContentDigest = PhotonCadArtifactGuards.Digest(contentDigest, nameof(contentDigest));
        ByteLength = PhotonCadArtifactGuards.ByteLength(
            byteLength,
            PhotonCadArtifactContract.AbsoluteMaximumArtifactBytes,
            nameof(byteLength));
        DisplayName = PhotonCadArtifactGuards.DisplayName(displayName);
    }

    public string SourceArtifactId { get; }
    public string Kind { get; }
    public string MediaType { get; }
    public string Profile { get; }
    public string ContentDigest { get; }
    public long ByteLength { get; }
    public string DisplayName { get; }
}

internal sealed class PhotonCadArtifactSourceLease : IAsyncDisposable
{
    public PhotonCadArtifactSourceLease(
        PhotonCadArtifactSourceDescriptor descriptor,
        Stream content,
        bool stableLockedRead)
    {
        Descriptor = descriptor ?? throw new PhotonCadArtifactException("invalid_source_descriptor");
        Content = content ?? throw new PhotonCadArtifactException("invalid_source_stream");
        if (!content.CanRead || !stableLockedRead)
            throw new PhotonCadArtifactException("stable_locked_source_required");
    }

    public PhotonCadArtifactSourceDescriptor Descriptor { get; }
    public Stream Content { get; }

    public async ValueTask DisposeAsync() => await Content.DisposeAsync().ConfigureAwait(false);
}

internal interface IPhotonCadArtifactSource
{
    ValueTask<PhotonCadArtifactSourceLease> AcquireAsync(
        PhotonCadArtifactSourceRequest request,
        CancellationToken cancellationToken = default);
}

public interface IPhotonCadArtifactContextAuthority
{
    PhotonCadArtifactContext? Current();
}

public sealed record PhotonCadArtifactDescriptor
{
    public PhotonCadArtifactDescriptor(
        PhotonCadArtifactHandle artifactHandle,
        PhotonCadArtifactContext context,
        string kind,
        string mediaType,
        string profile,
        string contentDigest,
        long byteLength,
        string displayName,
        DateTimeOffset expiresAtUtc)
    {
        ArtifactHandle = artifactHandle ?? throw new PhotonCadArtifactException("invalid_artifact_handle");
        Context = context ?? throw new PhotonCadArtifactException("invalid_context");
        if (kind != PhotonCadArtifactContract.StepKind || mediaType != PhotonCadArtifactContract.StepMediaType ||
            profile != PhotonCadArtifactContract.UnspecifiedProfile)
            throw new PhotonCadArtifactException("artifact_format_unavailable");
        Kind = kind;
        MediaType = mediaType;
        Profile = profile;
        ContentDigest = PhotonCadArtifactGuards.Digest(contentDigest, nameof(contentDigest));
        ByteLength = PhotonCadArtifactGuards.ByteLength(
            byteLength,
            PhotonCadArtifactContract.AbsoluteMaximumArtifactBytes,
            nameof(byteLength));
        DisplayName = PhotonCadArtifactGuards.DisplayName(displayName);
        if (expiresAtUtc == default || expiresAtUtc.Offset != TimeSpan.Zero)
            throw new PhotonCadArtifactException("invalid_artifact_expiry");
        ExpiresAtUtc = expiresAtUtc;
    }

    public PhotonCadArtifactHandle ArtifactHandle { get; }
    public PhotonCadArtifactContext Context { get; }
    public string Kind { get; }
    public string MediaType { get; }
    public string Profile { get; }
    public string ContentDigest { get; }
    public long ByteLength { get; }
    public string DisplayName { get; }
    public DateTimeOffset ExpiresAtUtc { get; }
}

public sealed record PhotonCadDestinationDescriptor
{
    public PhotonCadDestinationDescriptor(
        PhotonCadDestinationHandle destinationHandle,
        PhotonCadArtifactHandle artifactHandle,
        PhotonCadArtifactContext context,
        string displayLabel,
        DateTimeOffset expiresAtUtc)
    {
        DestinationHandle = destinationHandle ?? throw new PhotonCadArtifactException("invalid_destination_handle");
        ArtifactHandle = artifactHandle ?? throw new PhotonCadArtifactException("invalid_artifact_handle");
        Context = context ?? throw new PhotonCadArtifactException("invalid_context");
        DisplayLabel = PhotonCadArtifactGuards.DisplayName(displayLabel);
        if (expiresAtUtc == default || expiresAtUtc.Offset != TimeSpan.Zero)
            throw new PhotonCadArtifactException("invalid_destination_expiry");
        ExpiresAtUtc = expiresAtUtc;
    }

    public PhotonCadDestinationHandle DestinationHandle { get; }
    public PhotonCadArtifactHandle ArtifactHandle { get; }
    public PhotonCadArtifactContext Context { get; }
    public string DisplayLabel { get; }
    public DateTimeOffset ExpiresAtUtc { get; }
}

public sealed record PhotonCadDestinationCommitReceipt
{
    public PhotonCadDestinationCommitReceipt(
        PhotonCadCommitReceiptHandle receiptHandle,
        PhotonCadArtifactHandle artifactHandle,
        string contentDigest,
        long byteLength,
        string destinationLabel,
        DateTimeOffset committedAtUtc)
    {
        ReceiptHandle = receiptHandle ?? throw new PhotonCadArtifactException("invalid_commit_receipt");
        ArtifactHandle = artifactHandle ?? throw new PhotonCadArtifactException("invalid_artifact_handle");
        ContentDigest = PhotonCadArtifactGuards.Digest(contentDigest, nameof(contentDigest));
        ByteLength = PhotonCadArtifactGuards.ByteLength(
            byteLength,
            PhotonCadArtifactContract.AbsoluteMaximumArtifactBytes,
            nameof(byteLength));
        DestinationLabel = PhotonCadArtifactGuards.DisplayName(destinationLabel);
        if (committedAtUtc == default || committedAtUtc.Offset != TimeSpan.Zero)
            throw new PhotonCadArtifactException("invalid_commit_time");
        CommittedAtUtc = committedAtUtc;
    }

    public PhotonCadCommitReceiptHandle ReceiptHandle { get; }
    public PhotonCadArtifactHandle ArtifactHandle { get; }
    public string ContentDigest { get; }
    public long ByteLength { get; }
    public string DestinationLabel { get; }
    public DateTimeOffset CommittedAtUtc { get; }
}

public interface IPhotonCadArtifactDestinationAuthority : IAsyncDisposable
{
    ValueTask<PhotonCadDestinationDescriptor?> PickAsync(
        PhotonCadArtifactDescriptor artifact,
        CancellationToken cancellationToken = default);

    ValueTask<PhotonCadDestinationDescriptor> ResolveAsync(
        PhotonCadDestinationHandle handle,
        PhotonCadArtifactDescriptor artifact,
        PhotonCadArtifactContext context,
        CancellationToken cancellationToken = default);

    ValueTask<PhotonCadDestinationCommitReceipt> CommitAsync(
        PhotonCadDestinationHandle handle,
        PhotonCadArtifactDescriptor artifact,
        PhotonCadArtifactContext context,
        Stream verifiedContent,
        CancellationToken cancellationToken = default);

    ValueTask RevokeAsync(PhotonCadDestinationHandle handle);
    ValueTask RevokeContextAsync(PhotonCadArtifactContext context);
}

public sealed record PhotonCadArtifactStorageOptions
{
    public PhotonCadArtifactStorageOptions(
        string rootDirectory,
        long maximumArtifactBytes = 2L * 1024 * 1024 * 1024,
        long maximumSessionBytes = 4L * 1024 * 1024 * 1024,
        int maximumArtifactsPerContext = 32,
        TimeSpan? artifactTimeToLive = null,
        int maximumQuarantineEntries = 64)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || !Path.IsPathFullyQualified(rootDirectory) ||
            rootDirectory.StartsWith("\\\\", StringComparison.Ordinal))
            throw new PhotonCadArtifactException("invalid_storage_root");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        if (Path.GetPathRoot(full)?.Equals(full, StringComparison.OrdinalIgnoreCase) == true)
            throw new PhotonCadArtifactException("invalid_storage_root");
        if (maximumArtifactBytes <= 0 || maximumArtifactBytes > PhotonCadArtifactContract.AbsoluteMaximumArtifactBytes ||
            maximumSessionBytes < maximumArtifactBytes ||
            maximumSessionBytes > PhotonCadArtifactContract.AbsoluteMaximumArtifactBytes ||
            maximumArtifactsPerContext is < 1 or > 256 || maximumQuarantineEntries is < 1 or > 1_024)
            throw new PhotonCadArtifactException("invalid_storage_policy");
        var ttl = artifactTimeToLive ?? TimeSpan.FromMinutes(30);
        if (ttl < TimeSpan.FromSeconds(30) || ttl > TimeSpan.FromHours(24))
            throw new PhotonCadArtifactException("invalid_storage_policy");
        RootDirectory = full;
        MaximumArtifactBytes = maximumArtifactBytes;
        MaximumSessionBytes = maximumSessionBytes;
        MaximumArtifactsPerContext = maximumArtifactsPerContext;
        ArtifactTimeToLive = ttl;
        MaximumQuarantineEntries = maximumQuarantineEntries;
    }

    public string RootDirectory { get; }
    public long MaximumArtifactBytes { get; }
    public long MaximumSessionBytes { get; }
    public int MaximumArtifactsPerContext { get; }
    public TimeSpan ArtifactTimeToLive { get; }
    public int MaximumQuarantineEntries { get; }
}

public sealed record PhotonCadArtifactReleaseOptions
{
    public PhotonCadArtifactReleaseOptions(
        Uri workbenchOrigin,
        string resourcePathPrefix = "/workbench-api/photon-cad/artifacts/v1/",
        TimeSpan? reviewTimeToLive = null,
        TimeSpan? resourceTimeToLive = null)
    {
        ArgumentNullException.ThrowIfNull(workbenchOrigin);
        if (!workbenchOrigin.IsAbsoluteUri || workbenchOrigin.UserInfo.Length != 0 ||
            workbenchOrigin.Scheme is not ("http" or "https") || workbenchOrigin.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(workbenchOrigin.Query) || !string.IsNullOrEmpty(workbenchOrigin.Fragment))
            throw new PhotonCadArtifactException("invalid_workbench_origin");
        if (!resourcePathPrefix.StartsWith('/') || !resourcePathPrefix.EndsWith('/') ||
            resourcePathPrefix.Contains("..", StringComparison.Ordinal) ||
            resourcePathPrefix.Contains('\\') || resourcePathPrefix.Contains('%'))
            throw new PhotonCadArtifactException("invalid_resource_prefix");
        var reviewTtl = reviewTimeToLive ?? TimeSpan.FromMinutes(5);
        var resourceTtl = resourceTimeToLive ?? TimeSpan.FromMinutes(5);
        if (reviewTtl < TimeSpan.FromSeconds(15) || reviewTtl > TimeSpan.FromMinutes(30) ||
            resourceTtl < TimeSpan.FromSeconds(15) || resourceTtl > reviewTtl)
            throw new PhotonCadArtifactException("invalid_release_policy");
        WorkbenchOrigin = new Uri(workbenchOrigin.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);
        ResourcePathPrefix = resourcePathPrefix;
        ReviewTimeToLive = reviewTtl;
        ResourceTimeToLive = resourceTtl;
    }

    public Uri WorkbenchOrigin { get; }
    public string ResourcePathPrefix { get; }
    public TimeSpan ReviewTimeToLive { get; }
    public TimeSpan ResourceTimeToLive { get; }
}
