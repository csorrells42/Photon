using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace AssistantConversationBus;

public sealed record AssistantBusWaitResult(
    string WaitId,
    bool Cancelled,
    IReadOnlyList<AssistantBusMessage> Messages);

public sealed record AssistantBusCancelResult(string WaitId, bool Cancelled);

public sealed class AssistantBusHttpClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
    };
    private readonly HttpClient _http;
    private char[]? _token;

    public AssistantBusHttpClient(
        AssistantBusServiceSettings settings,
        AssistantIdentity identity,
        HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        AssistantIdentityPolicy.RequireSender(identity);
        _token = settings.TokenFor(identity).ToCharArray();
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
        };
        _http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri($"http://127.0.0.1:{settings.Port}/"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public Task<AssistantBusSendResult> SendAsync(
        AssistantBusSendRequest request,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<AssistantBusSendRequest, AssistantBusSendResult>(HttpMethod.Post, "v1/messages", request, cancellationToken);

    public Task<IReadOnlyList<AssistantParticipantStatus>> ParticipantsAsync(CancellationToken cancellationToken = default) =>
        GetJsonAsync<IReadOnlyList<AssistantParticipantStatus>>("v1/participants", cancellationToken);

    public Task<IReadOnlyList<AssistantBusMessage>> ReadAsync(
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default) =>
        GetJsonAsync<IReadOnlyList<AssistantBusMessage>>(
            $"v1/messages?afterSequence={afterSequence}&limit={limit}",
            cancellationToken);

    public Task<AssistantBusMessage> FindAsync(string messageId, CancellationToken cancellationToken = default) =>
        GetJsonAsync<AssistantBusMessage>($"v1/messages/{Uri.EscapeDataString(messageId)}", cancellationToken);

    public Task<AssistantBusWaitResult> WaitAsync(
        long afterSequence,
        int timeoutMs,
        string? waitId = null,
        CancellationToken cancellationToken = default) =>
        GetJsonAsync<AssistantBusWaitResult>(
            $"v1/wait?afterSequence={afterSequence}&timeoutMs={timeoutMs}"
                + (waitId is null ? string.Empty : $"&waitId={Uri.EscapeDataString(waitId)}"),
            cancellationToken);

    public Task<AssistantBusCancelResult> CancelAsync(string waitId, CancellationToken cancellationToken = default) =>
        SendJsonAsync<object, AssistantBusCancelResult>(
            HttpMethod.Post,
            $"v1/waits/{Uri.EscapeDataString(waitId)}/cancel",
            new { },
            cancellationToken);

    private async Task<TResponse> GetJsonAsync<TResponse>(string path, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, path);
        return await SendAndReadAsync<TResponse>(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TResponse> SendJsonAsync<TRequest, TResponse>(
        HttpMethod method,
        string path,
        TRequest body,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(method, path);
        request.Content = JsonContent.Create(body, options: JsonOptions);
        return await SendAndReadAsync<TResponse>(request, cancellationToken).ConfigureAwait(false);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        ObjectDisposedException.ThrowIf(_token is null, this);
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new string(_token));
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("AssistantConversationBusClient/1.0");
        return request;
    }

    private async Task<T> SendAndReadAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidDataException($"Assistant bus service rejected the request ({(int)response.StatusCode}).");
        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength is > AssistantBusLimits.MaximumPeerResponseBytes)
            throw new InvalidDataException("Assistant bus service response exceeded the bounded size.");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (destination.Length + read > AssistantBusLimits.MaximumPeerResponseBytes)
                throw new InvalidDataException("Assistant bus service response exceeded the bounded size.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        destination.Position = 0;
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(destination, JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Assistant bus service returned an empty response.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Assistant bus service returned malformed JSON.", exception);
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        if (_token is null) return;
        CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_token.AsSpan()));
        _token = null;
    }
}
