using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace HermesDesktop;

internal static class NativeUsageProtocol
{
    internal const int Version = 3;
    internal const string OpenRouterProvider = "openrouter";
    internal const string OpenAiProvider = "openai-api";
    internal const string AnthropicProvider = "anthropic-api";
    internal const string GoogleAiStudioProvider = "google-ai-studio";

    internal static bool IsValidProvider(string? provider) =>
        provider is OpenRouterProvider or OpenAiProvider or AnthropicProvider or GoogleAiStudioProvider;

    internal static bool IsOrganizationProvider(string? provider) =>
        provider is OpenAiProvider or AnthropicProvider or GoogleAiStudioProvider;

    internal static bool IsValidRequestId(string? requestId) =>
        Guid.TryParse(requestId, out _);
}

internal sealed record OpenRouterUsageSnapshot(
    double Usage,
    double? UsageDaily,
    double? UsageWeekly,
    double? UsageMonthly,
    double? Limit,
    double? LimitRemaining,
    string? LimitReset,
    bool IsFreeTier,
    DateTimeOffset CollectedAt);

internal sealed class UsageCollectionException(
    string code,
    string message,
    bool retryable,
    Exception? innerException = null) : Exception(message, innerException)
{
    internal string Code { get; } = code;
    internal bool Retryable { get; } = retryable;
}

internal sealed class OpenRouterUsageCollector : IDisposable
{
    private const int MaximumResponseBytes = 64 * 1024;
    private static readonly Uri Endpoint = new("https://openrouter.ai/api/v1/key");
    private readonly HttpClient _client;

    internal OpenRouterUsageCollector(HttpMessageHandler? handler = null)
    {
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            MaxResponseHeadersLength = 16,
        };
        _client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(12),
        };
    }

    internal async Task<OpenRouterUsageSnapshot> CollectAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new UsageCollectionException("not-configured", "The stored OpenRouter key is empty.", false);

        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("HermesWorkbench/0.1");

        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UsageCollectionException("unavailable", "OpenRouter did not respond before the collector timeout.", true, exception);
        }
        catch (HttpRequestException exception)
        {
            throw new UsageCollectionException("unavailable", "OpenRouter could not be reached from the desktop host.", true, exception);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new UsageCollectionException("permission-denied", "OpenRouter rejected the stored key. Replace it in the credential vault.", false);
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new UsageCollectionException("permission-denied", "The stored OpenRouter key cannot read its usage metadata.", false);
            if ((int)response.StatusCode == 429)
                throw new UsageCollectionException("rate-limited", "OpenRouter temporarily rate-limited the usage check.", true);
            if ((int)response.StatusCode >= 500)
                throw new UsageCollectionException("unavailable", "OpenRouter usage reporting is temporarily unavailable.", true);
            if (!response.IsSuccessStatusCode)
                throw new UsageCollectionException("unexpected", $"OpenRouter returned HTTP {(int)response.StatusCode} for the usage check.", false);

            var declaredLength = response.Content.Headers.ContentLength;
            if (declaredLength > MaximumResponseBytes)
                throw new UsageCollectionException("unexpected", "OpenRouter returned an oversized usage response.", false);

            byte[] payload;
            try
            {
                payload = await ReadLimitedAsync(response.Content, cancellationToken);
            }
            catch (UsageCollectionException) { throw; }
            catch (Exception exception) when (exception is IOException or HttpRequestException)
            {
                throw new UsageCollectionException("unavailable", "OpenRouter usage data could not be read.", true, exception);
            }

            try
            {
                using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 16 });
                if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                    throw new UsageCollectionException("unexpected", "OpenRouter returned an invalid usage response.", false);

                var usage = RequiredNonNegative(data, "usage");
                return new OpenRouterUsageSnapshot(
                    usage,
                    OptionalNonNegative(data, "usage_daily"),
                    OptionalNonNegative(data, "usage_weekly"),
                    OptionalNonNegative(data, "usage_monthly"),
                    OptionalNonNegative(data, "limit"),
                    OptionalNonNegative(data, "limit_remaining"),
                    OptionalReset(data),
                    OptionalBoolean(data, "is_free_tier"),
                    DateTimeOffset.UtcNow);
            }
            catch (JsonException exception)
            {
                throw new UsageCollectionException("unexpected", "OpenRouter returned malformed usage JSON.", false, exception);
            }
            finally
            {
                Array.Clear(payload);
            }
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (read == 0) break;
                if (output.Length + read > MaximumResponseBytes)
                    throw new UsageCollectionException("unexpected", "OpenRouter returned an oversized usage response.", false);
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        finally
        {
            Array.Clear(buffer);
        }
    }

    private static double RequiredNonNegative(JsonElement data, string property)
    {
        var value = OptionalNonNegative(data, property);
        return value ?? throw new UsageCollectionException("unexpected", $"OpenRouter omitted the required {property} value.", false);
    }

    private static double? OptionalNonNegative(JsonElement data, string property)
    {
        if (!data.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var parsed) || !double.IsFinite(parsed) || parsed < 0)
            throw new UsageCollectionException("unexpected", $"OpenRouter returned an invalid {property} value.", false);
        return parsed;
    }

    private static bool OptionalBoolean(JsonElement data, string property) =>
        data.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? OptionalReset(JsonElement data)
    {
        if (!data.TryGetProperty("limit_reset", out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) return null;
        var reset = value.GetString();
        return reset is "daily" or "weekly" or "monthly" ? reset : null;
    }

    public void Dispose() => _client.Dispose();
}
