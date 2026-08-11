using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HermesUsageCollectors;

public sealed record CollectorHttpPolicy(
    TimeSpan RequestTimeout,
    int MaximumResponseBytes = 512 * 1024,
    int MaximumErrorBytes = 8 * 1024)
{
    public static CollectorHttpPolicy Default { get; } = new(TimeSpan.FromSeconds(15));

    internal void Validate()
    {
        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
        if (MaximumResponseBytes is < 1024 or > 4 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaximumResponseBytes));
        if (MaximumErrorBytes is < 256 or > 32 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaximumErrorBytes));
    }
}

internal sealed class CollectorFailureException(SanitizedProviderError error, Exception? inner = null)
    : Exception(error.Message, inner)
{
    internal SanitizedProviderError Error { get; } = error;
}

internal sealed class FixedEndpointHttpClient : IDisposable
{
    private static readonly Regex SafeProviderCode = new("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant);
    private readonly Uri _origin;
    private readonly CollectorHttpPolicy _policy;
    private readonly HttpClient _client;

    internal FixedEndpointHttpClient(Uri origin, HttpMessageHandler? handler = null, CollectorHttpPolicy? policy = null)
    {
        if (origin.Scheme != Uri.UriSchemeHttps || !origin.IsDefaultPort || origin.AbsolutePath != "/")
            throw new ArgumentException("Collector origins must be default-port HTTPS origins.", nameof(origin));

        _origin = origin;
        _policy = policy ?? CollectorHttpPolicy.Default;
        _policy.Validate();
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            MaxResponseHeadersLength = 16,
        };
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal async Task<byte[]> GetJsonAsync(Uri endpoint, Action<HttpRequestMessage> authorize, CancellationToken cancellationToken)
    {
        ValidateEndpoint(endpoint);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("HermesWorkbench-UsageCollectors/1.0");
        authorize(request);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_policy.RequestTimeout);

        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw Failure(ProviderErrorCategory.Cancelled, "cancelled", "Usage collection was cancelled.", true, inner: exception);
        }
        catch (OperationCanceledException exception)
        {
            throw Failure(ProviderErrorCategory.Timeout, "timeout", "The provider did not respond before the collector timeout.", true, inner: exception);
        }
        catch (HttpRequestException exception)
        {
            throw Failure(ProviderErrorCategory.Network, "network", "The provider could not be reached by the trusted host.", true, inner: exception);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400)
                throw Failure(ProviderErrorCategory.Redirected, "redirect-rejected", "The provider returned a redirect, which the collector rejected.", false, status);

            if (!response.IsSuccessStatusCode)
            {
                var providerCode = await ReadProviderCodeAsync(response.Content, timeout.Token).ConfigureAwait(false);
                throw status switch
                {
                    401 => Failure(ProviderErrorCategory.Unauthorized, "unauthorized", "The provider rejected the configured credential.", false, status, providerCode),
                    403 => Failure(ProviderErrorCategory.Forbidden, "forbidden", "The configured credential lacks permission for this telemetry.", false, status, providerCode),
                    429 => Failure(ProviderErrorCategory.RateLimited, "rate-limited", "The provider rate-limited the telemetry request.", true, status, providerCode),
                    >= 500 => Failure(ProviderErrorCategory.RemoteFailure, "provider-unavailable", "The provider telemetry service is temporarily unavailable.", true, status, providerCode),
                    _ => Failure(ProviderErrorCategory.RemoteFailure, "provider-error", $"The provider rejected the telemetry request with HTTP {status.ToString(CultureInfo.InvariantCulture)}.", false, status, providerCode),
                };
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is null || !(mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)))
                throw Failure(ProviderErrorCategory.MalformedResponse, "invalid-content-type", "The provider returned a non-JSON telemetry response.", false, status);
            if (response.Content.Headers.ContentLength is long declared && declared > _policy.MaximumResponseBytes)
                throw Failure(ProviderErrorCategory.OversizedResponse, "oversized-response", "The provider returned an oversized telemetry response.", false, status);

            return await ReadLimitedAsync(response.Content, _policy.MaximumResponseBytes, timeout.Token).ConfigureAwait(false);
        }
    }

    private void ValidateEndpoint(Uri endpoint)
    {
        if (endpoint.Scheme != Uri.UriSchemeHttps || !endpoint.IsDefaultPort
            || !string.Equals(endpoint.Host, _origin.Host, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw Failure(ProviderErrorCategory.Configuration, "endpoint-rejected", "The telemetry endpoint was outside the fixed official allowlist.", false);
        }
    }

    private async Task<string?> ReadProviderCodeAsync(HttpContent content, CancellationToken cancellationToken)
    {
        try
        {
            if (content.Headers.ContentLength is long length && length > _policy.MaximumErrorBytes) return null;
            var bytes = await ReadLimitedAsync(content, _policy.MaximumErrorBytes, cancellationToken).ConfigureAwait(false);
            try
            {
                using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
                var root = json.RootElement;
                JsonElement code;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error)
                    && error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out code))
                {
                    var text = code.ValueKind == JsonValueKind.String ? code.GetString() : code.GetRawText();
                    return text is not null && SafeProviderCode.IsMatch(text) ? text : null;
                }
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("type", out code) && code.ValueKind == JsonValueKind.String)
                {
                    var text = code.GetString();
                    return text is not null && SafeProviderCode.IsMatch(text) ? text : null;
                }
            }
            finally { Array.Clear(bytes); }
        }
        catch { }
        return null;
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, int maximumBytes, CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (output.Length + read > maximumBytes)
                    throw Failure(ProviderErrorCategory.OversizedResponse, "oversized-response", "The provider returned an oversized telemetry response.", false);
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        finally { Array.Clear(buffer); }
    }

    internal static CollectorFailureException Failure(
        ProviderErrorCategory category, string code, string message, bool retryable,
        int? status = null, string? providerCode = null, Exception? inner = null) =>
        new(new SanitizedProviderError(category, code, message, retryable, status, providerCode), inner);

    public void Dispose() => _client.Dispose();
}

internal static class CollectorJson
{
    internal static JsonDocument Parse(byte[] payload)
    {
        try
        {
            // Parse through a stream so JsonDocument owns its backing storage before the
            // transport buffer is zeroed in finally.
            using var stream = new MemoryStream(payload, writable: false);
            return JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
        }
        catch (JsonException exception)
        {
            throw FixedEndpointHttpClient.Failure(ProviderErrorCategory.MalformedResponse, "malformed-json", "The provider returned malformed telemetry JSON.", false, inner: exception);
        }
        finally { Array.Clear(payload); }
    }

    internal static decimal RequiredNonNegativeDecimal(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Number
            || !element.TryGetDecimal(out var value) || value < 0) throw Invalid(name);
        return value;
    }

    internal static long RequiredUnixSeconds(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var value))
            throw Invalid(name);
        return value;
    }

    internal static string RequiredString(JsonElement parent, string name, int maximumLength = 128)
    {
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String) throw Invalid(name);
        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength) throw Invalid(name);
        return value;
    }

    internal static JsonElement RequiredArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Array) throw Invalid(name);
        return element;
    }

    internal static CollectorFailureException Invalid(string field) =>
        FixedEndpointHttpClient.Failure(ProviderErrorCategory.MalformedResponse, "invalid-json-shape", $"The provider returned an invalid {field} field.", false);
}

internal static class CollectorUris
{
    internal static Uri Build(Uri origin, string path, IEnumerable<KeyValuePair<string, string?>> query)
    {
        var encoded = query.Where(item => item.Value is not null)
            .Select(item => $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value!)}");
        return new UriBuilder(origin) { Path = path, Query = string.Join("&", encoded) }.Uri;
    }
}
