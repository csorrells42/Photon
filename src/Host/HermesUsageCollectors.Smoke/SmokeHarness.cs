using System.Net;
using System.Text;
using HermesUsageCollectors;

namespace HermesUsageCollectors.Smoke;

internal sealed record RequestShape(
    HttpMethod Method,
    string Scheme,
    string Host,
    string Path,
    string Query,
    string? AuthorizationScheme,
    bool CredentialMatched,
    bool AnthropicVersionMatched);

internal sealed class ScriptedHandler(
    Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> responder,
    string expectedCredential) : HttpMessageHandler
{
    private int _calls;
    internal List<RequestShape> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref _calls);
        var uri = request.RequestUri ?? throw new InvalidOperationException("A request URI is required.");
        var bearerMatched = request.Headers.Authorization?.Parameter == expectedCredential;
        var apiKeyMatched = request.Headers.TryGetValues("x-api-key", out var keyValues) &&
            keyValues.Count() == 1 && keyValues.Single() == expectedCredential;
        var versionMatched = request.Headers.TryGetValues("anthropic-version", out var versions) &&
            versions.Count() == 1 && versions.Single() == "2023-06-01";
        Requests.Add(new RequestShape(request.Method, uri.Scheme, uri.Host, uri.AbsolutePath, uri.Query,
            request.Headers.Authorization?.Scheme, bearerMatched || apiKeyMatched, versionMatched));
        return responder(request, call, cancellationToken);
    }
}

internal static class SyntheticHttp
{
    internal static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        return response;
    }

    internal static HttpResponseMessage Bytes(byte[] bytes, string mediaType = "application/json")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
        return response;
    }
}

internal sealed class SmokeSuite
{
    private int _passed;
    private int _failed;

    internal async Task RunAsync(string name, Func<Task> test)
    {
        try
        {
            await test().ConfigureAwait(false);
            _passed++;
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception exception)
        {
            _failed++;
            Console.WriteLine($"FAIL {name}: {exception.GetType().Name}: {exception.Message}");
        }
    }

    internal void Complete()
    {
        Console.WriteLine($"RESULT {_passed} passed, {_failed} failed, {_passed + _failed} total");
        if (_failed != 0) Environment.ExitCode = 1;
    }
}

internal static class Verify
{
    internal static void True(bool condition, string message = "Assertion failed.")
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static void Equal<T>(T expected, T actual, string message = "Values differ.") where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message} Expected {expected}; actual {actual}.");
    }

    internal static void Category(ProviderCollectionResult result, ProviderErrorCategory category) =>
        True(result.Errors.Any(error => error.Category == category),
            $"Expected {category}; actual [{string.Join(',', result.Errors.Select(error => error.Category))}].");
}

internal sealed class StubCollector(
    string providerId,
    Func<ProviderCredentialProfile, UsageCollectionRequest, CancellationToken, Task<ProviderCollectionResult>> collect) : IUsageCollector
{
    public string ProviderId { get; } = providerId;

    public Task<ProviderCollectionResult> CollectAsync(
        ProviderCredentialProfile profile,
        UsageCollectionRequest request,
        CancellationToken cancellationToken = default) => collect(profile, request, cancellationToken);
}

internal sealed class StubBillingExportSource(IEnumerable<GoogleBillingExportPage> pages) : IGoogleBillingExportSource
{
    private readonly Queue<GoogleBillingExportPage> _pages = new(pages);

    public ValueTask<GoogleBillingExportPage> QueryCostsAsync(
        string targetReference,
        ObservationWindow window,
        string? pageToken,
        int maximumRows,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_pages.Count == 0) throw new InvalidOperationException("No synthetic page remains.");
        return ValueTask.FromResult(_pages.Dequeue());
    }
}
