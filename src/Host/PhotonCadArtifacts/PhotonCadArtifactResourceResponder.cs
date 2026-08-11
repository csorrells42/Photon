using System.Collections.ObjectModel;
using System.Security.Cryptography;

namespace PhotonCadArtifacts;

internal sealed record PhotonCadArtifactResourceRequest(
    string Method,
    Uri RequestUri,
    bool RendererAuthorized,
    PhotonCadArtifactContext? Context,
    IReadOnlyDictionary<string, string>? Headers = null);

internal sealed class PhotonCadArtifactResourceResponse : IAsyncDisposable
{
    internal PhotonCadArtifactResourceResponse(
        int statusCode,
        string reason,
        IReadOnlyDictionary<string, string> headers,
        PhotonCadArtifactReadLease? content)
    {
        StatusCode = statusCode;
        Reason = reason;
        Headers = headers;
        Content = content?.Content;
        _lease = content;
    }

    private readonly PhotonCadArtifactReadLease? _lease;
    public int StatusCode { get; }
    public string Reason { get; }
    public IReadOnlyDictionary<string, string> Headers { get; }
    public Stream? Content { get; }

    public async ValueTask DisposeAsync()
    {
        if (_lease is not null) await _lease.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Provider-neutral WebView resource policy. A desktop adapter may translate a
/// CoreWebView2 request into this type; this assembly does not claim a live mount.
/// </summary>
internal sealed class PhotonCadArtifactResourceResponder
{
    private readonly IPhotonCadArtifactResourceStore _storage;
    private readonly IPhotonCadArtifactContextAuthority _contextAuthority;
    private readonly PhotonCadArtifactReleaseOptions _options;

    public PhotonCadArtifactResourceResponder(
        IPhotonCadArtifactResourceStore storage,
        IPhotonCadArtifactContextAuthority contextAuthority,
        PhotonCadArtifactReleaseOptions options)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _contextAuthority = contextAuthority ?? throw new ArgumentNullException(nameof(contextAuthority));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async ValueTask<PhotonCadArtifactResourceResponse?> TryHandleAsync(
        PhotonCadArtifactResourceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.RequestUri);
        if (!TargetsFixedNamespace(request.RequestUri)) return null;
        if (!ValidResourceUri(request.RequestUri) || !request.Method.Equals("GET", StringComparison.Ordinal) ||
            !request.RendererAuthorized || request.Context is null ||
            request.Headers?.Keys.Any(key => key.Equals("Range", StringComparison.OrdinalIgnoreCase)) == true)
            return Error(404, "resource_unavailable");
        PhotonCadArtifactReadLease? lease = null;
        try
        {
            PhotonCadArtifactGuards.CurrentContext(_contextAuthority, request.Context);
            var token = request.RequestUri.AbsolutePath[_options.ResourcePathPrefix.Length..];
            var resource = new PhotonCadArtifactResourceHandle("cad-resource:" + token);
            lease = await _storage.ConsumeResourceAsync(resource, request.Context, cancellationToken).ConfigureAwait(false);
            if (!lease.Content.CanRead)
                throw new InvalidDataException("Artifact content is not readable.");
            var digestBytes = Convert.FromHexString(lease.Descriptor.ContentDigest[7..]);
            var headers = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Cache-Control"] = "no-store",
                ["Content-Length"] = lease.Descriptor.ByteLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Content-Type"] = lease.Descriptor.MediaType,
                ["Cross-Origin-Resource-Policy"] = "same-origin",
                ["Digest"] = "sha-256=" + Convert.ToBase64String(digestBytes),
                ["X-Content-Type-Options"] = "nosniff",
            });
            var response = new PhotonCadArtifactResourceResponse(200, "ok", headers, lease);
            lease = null;
            return response;
        }
        catch (Exception exception) when (IsPathlessStorageFailure(exception))
        {
            if (lease is not null)
            {
                try
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception cleanupException) when (IsPathlessStorageFailure(cleanupException))
                {
                    // The response remains deliberately pathless even when storage cleanup also fails.
                }
            }
            return Error(404, "resource_unavailable");
        }
    }

    private bool TargetsFixedNamespace(Uri uri) =>
        uri.IsAbsoluteUri &&
        uri.Scheme.Equals(_options.WorkbenchOrigin.Scheme, StringComparison.OrdinalIgnoreCase) &&
        uri.Host.Equals(_options.WorkbenchOrigin.Host, StringComparison.OrdinalIgnoreCase) &&
        uri.Port == _options.WorkbenchOrigin.Port &&
        uri.AbsolutePath.StartsWith(_options.ResourcePathPrefix, StringComparison.Ordinal);

    private bool ValidResourceUri(Uri uri)
    {
        if (uri.UserInfo.Length != 0 || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || uri.OriginalString.Contains('%'))
            return false;
        var token = uri.AbsolutePath[_options.ResourcePathPrefix.Length..];
        return token.Length == 43 && token.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
    }

    private static PhotonCadArtifactResourceResponse Error(int status, string reason) => new(
        status,
        reason,
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Cache-Control"] = "no-store",
            ["Content-Length"] = "0",
            ["Cross-Origin-Resource-Policy"] = "same-origin",
            ["X-Content-Type-Options"] = "nosniff",
        }),
        null);

    private static bool IsPathlessStorageFailure(Exception exception) => exception is
        PhotonCadArtifactException or
        IOException or
        UnauthorizedAccessException or
        ObjectDisposedException or
        CryptographicException or
        InvalidDataException or
        FormatException or
        InvalidOperationException or
        NotSupportedException or
        ArgumentException;
}
