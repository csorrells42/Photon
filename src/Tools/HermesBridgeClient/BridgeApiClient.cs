using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace HermesBridgeClient;

public sealed class BridgeApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly BridgeClientPolicy _policy;

    public BridgeApiClient(HttpMessageHandler? handler = null, BridgeClientPolicy? policy = null)
    {
        _policy = policy ?? BridgeClientPolicy.Default;
        _policy.Validate();
        var ownsHandler = handler is null;
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
        };
        _http = new HttpClient(handler, disposeHandler: ownsHandler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public Task<BridgeCommandResult> HealthAsync(
        Uri? endpoint = null, CancellationToken cancellationToken = default) =>
        ExecuteAsync(endpoint ?? BridgeEndpointPolicy.DefaultEndpoint, HttpMethod.Get, "health", null, null, cancellationToken);

    public Task<BridgeCommandResult> StatusAsync(
        BridgeConnectionSettings settings, CancellationToken cancellationToken = default) =>
        ExecuteAsync(settings.Endpoint, HttpMethod.Get, "v1/session", settings.Authentication, null, cancellationToken);

    public Task<BridgeCommandResult> InterruptAsync(
        BridgeConnectionSettings settings, CancellationToken cancellationToken = default) =>
        ExecuteAsync(settings.Endpoint, HttpMethod.Post, "v1/interrupt", settings.Authentication, null, cancellationToken);

    public Task<BridgeCommandResult> NewSessionAsync(
        BridgeConnectionSettings settings, CancellationToken cancellationToken = default) =>
        ExecuteAsync(settings.Endpoint, HttpMethod.Post, "v1/new", settings.Authentication, null, cancellationToken);

    public Task<BridgeCommandResult> SendAsync(
        BridgeConnectionSettings settings, string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Task.FromResult(BridgeCommandResult.Failure(BridgeExitCode.UsageOrConfiguration,
                "Send requires explicit non-empty text."));
        if (text.Length > 64 * 1024)
            return Task.FromResult(BridgeCommandResult.Failure(BridgeExitCode.UsageOrConfiguration,
                "Send text exceeds the 64 KiB bridge limit."));
        return ExecuteAsync(settings.Endpoint, HttpMethod.Post, "v1/turns", settings.Authentication,
            JsonContent.Create(new TurnRequest(text)), cancellationToken, _policy.EffectiveTurnTimeout);
    }

    private async Task<BridgeCommandResult> ExecuteAsync(
        Uri endpoint,
        HttpMethod method,
        string relativePath,
        BridgeAuthentication? authentication,
        HttpContent? content,
        CancellationToken cancellationToken,
        TimeSpan? commandTimeout = null)
    {
        try
        {
            endpoint = BridgeEndpointPolicy.RequireLoopback(endpoint);
        }
        catch (ArgumentException)
        {
            content?.Dispose();
            return BridgeCommandResult.Failure(BridgeExitCode.UsageOrConfiguration,
                "The bridge endpoint was rejected because it is not a safe loopback endpoint.");
        }

        using var request = new HttpRequestMessage(method, new Uri(endpoint, relativePath)) { Content = content };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("HermesBridgeClient/1.0");
        authentication?.Apply(request);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(commandTimeout ?? _policy.Timeout);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return BridgeCommandResult.Failure(BridgeExitCode.GatewayTimeout, "The bridge command was cancelled.");
        }
        catch (OperationCanceledException)
        {
            return BridgeCommandResult.Failure(BridgeExitCode.GatewayTimeout, "The bridge did not respond before the client timeout.");
        }
        catch (HttpRequestException)
        {
            return BridgeCommandResult.Failure(BridgeExitCode.ConnectivityOrProtocol,
                "The Hermes conversation bridge could not be reached on loopback.");
        }

        using (response)
        {
            var failure = MapStatus(response.StatusCode);
            if (failure is not null) return failure;
            if (!response.IsSuccessStatusCode)
            {
                return BridgeCommandResult.Failure(BridgeExitCode.ConnectivityOrProtocol,
                    $"The bridge rejected the request with HTTP {(int)response.StatusCode}.");
            }
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is null || !(mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
                || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)))
            {
                return BridgeCommandResult.Failure(BridgeExitCode.ConnectivityOrProtocol,
                    "The bridge returned a non-JSON response.");
            }

            byte[]? payload = null;
            try
            {
                payload = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
                using var document = JsonDocument.Parse(payload, new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64,
                });
                var output = JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
                if (authentication is not null) output = authentication.Redact(output);
                return BridgeCommandResult.Success(output);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return BridgeCommandResult.Failure(BridgeExitCode.GatewayTimeout, "The bridge command was cancelled.");
            }
            catch (OperationCanceledException)
            {
                return BridgeCommandResult.Failure(BridgeExitCode.GatewayTimeout, "The bridge response exceeded the client timeout.");
            }
            catch (ResponseLimitException)
            {
                return BridgeCommandResult.Failure(BridgeExitCode.ConnectivityOrProtocol,
                    "The bridge response exceeded the safe size limit.");
            }
            catch (JsonException)
            {
                return BridgeCommandResult.Failure(BridgeExitCode.ConnectivityOrProtocol,
                    "The bridge returned malformed JSON.");
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException)
            {
                return BridgeCommandResult.Failure(BridgeExitCode.ConnectivityOrProtocol,
                    "The bridge response could not be read safely.");
            }
            finally
            {
                if (payload is not null) Array.Clear(payload);
            }
        }
    }

    private static BridgeCommandResult? MapStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => BridgeCommandResult.Failure(BridgeExitCode.AuthenticationFailed,
            "Bridge authentication failed. Restart Hermes Workbench if its local bridge settings are stale."),
        HttpStatusCode.Conflict => BridgeCommandResult.Failure(BridgeExitCode.Conflict,
            "The bridge rejected the command because another turn is active or the visible Hermes dock reported a conflict."),
        HttpStatusCode.ServiceUnavailable => BridgeCommandResult.Failure(BridgeExitCode.ServiceUnavailable,
            "The visible Hermes dock is temporarily unavailable."),
        HttpStatusCode.GatewayTimeout => BridgeCommandResult.Failure(BridgeExitCode.GatewayTimeout,
            "The visible Hermes dock did not answer before the bridge timeout."),
        >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest =>
            BridgeCommandResult.Failure(BridgeExitCode.ConnectivityOrProtocol, "The bridge returned a redirect, which the client refused."),
        _ => null,
    };

    private async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long declared && declared > _policy.MaximumResponseBytes)
            throw new ResponseLimitException();
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[_policy.MaximumResponseBytes + 1];
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            offset += read;
        }
        if (offset > _policy.MaximumResponseBytes)
        {
            Array.Clear(buffer);
            throw new ResponseLimitException();
        }
        if (offset == buffer.Length) return buffer;
        var result = buffer.AsSpan(0, offset).ToArray();
        Array.Clear(buffer);
        return result;
    }

    public void Dispose() => _http.Dispose();

    private sealed record TurnRequest(string Text);
    private sealed class ResponseLimitException : Exception;
}
