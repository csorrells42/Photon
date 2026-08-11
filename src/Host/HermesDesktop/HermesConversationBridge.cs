using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace HermesDesktop;

internal sealed class HermesConversationBridge : IAsyncDisposable
{
    public const int ProtocolVersion = 1;
    public const int DefaultPort = 8972;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Action<object> _postHostMessage;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RendererReply>> _pending = new();
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private WebApplication? _application;

    public HermesConversationBridge(Action<object> postHostMessage) => _postHostMessage = postHostMessage;

    public string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "hermes",
        "conversation-bridge.json");

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_application is not null) return;
            var settings = LoadOrCreateSettings();
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(HermesConversationBridge).Assembly.FullName,
                Args = [],
            });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls(settings.Endpoint);
            var application = builder.Build();
            application.Use(async (context, next) =>
            {
                if (!context.Request.Path.Equals("/health", StringComparison.OrdinalIgnoreCase)
                    && !HasValidBearerToken(context, settings.AuthenticationToken))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.Headers.WWWAuthenticate = "Bearer";
                    await context.Response.WriteAsync("Hermes conversation bridge authentication failed.", context.RequestAborted).ConfigureAwait(false);
                    return;
                }
                await next(context).ConfigureAwait(false);
            });
            application.MapGet("/health", () => Results.Json(new
            {
                service = "Hermes Conversation Bridge",
                state = "running",
                mode = "live-workbench-session",
                port = settings.Port,
            }));
            application.MapGet("/v1/session", (Func<HttpContext, Task<IResult>>)(async context =>
                await RelayAsync("snapshot", payload: null, context.RequestAborted).ConfigureAwait(false)));
            application.MapPost("/v1/turns", (Func<HttpContext, Task<IResult>>)SubmitTurnAsync);
            application.MapPost("/v1/interrupt", (Func<HttpContext, Task<IResult>>)(async context =>
                await RelayAsync("interrupt", payload: null, context.RequestAborted).ConfigureAwait(false)));
            try
            {
                await application.StartAsync(cancellationToken).ConfigureAwait(false);
                _application = application;
                DesktopLog.Write($"Hermes conversation bridge listening on {settings.Endpoint}.");
            }
            catch
            {
                await application.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public bool TryComplete(JsonElement root)
    {
        var requestId = root.TryGetProperty("requestId", out var idElement) && idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(requestId) || !_pending.TryRemove(requestId, out var completion)) return false;
        var ok = root.TryGetProperty("ok", out var okElement) && okElement.ValueKind == JsonValueKind.True;
        var error = root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.String
            ? errorElement.GetString()
            : null;
        var snapshot = root.TryGetProperty("snapshot", out var snapshotElement)
            ? snapshotElement.Clone()
            : default;
        return completion.TrySetResult(new RendererReply(ok, snapshot, error));
    }

    private async Task<IResult> SubmitTurnAsync(HttpContext context)
    {
        BridgeTurnRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync<BridgeTurnRequest>(JsonOptions, context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "The turn request was not valid JSON." });
        }
        var text = request?.Text?.Trim() ?? string.Empty;
        if (text.Length == 0) return Results.BadRequest(new { error = "Enter a message before submitting a turn." });
        if (text.Length > 64 * 1024) return Results.BadRequest(new { error = "Text exceeds the 64 KiB bridge limit." });
        if (!await _turnGate.WaitAsync(0, context.RequestAborted).ConfigureAwait(false))
        {
            return Results.Conflict(new { error = "Another Hermes bridge turn is already in progress." });
        }
        try
        {
            return await RelayAsync("turn", new { text }, context.RequestAborted, TimeSpan.FromMinutes(30)).ConfigureAwait(false);
        }
        finally
        {
            _turnGate.Release();
        }
    }

    private async Task<IResult> RelayAsync(string operation, object? payload, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var requestId = $"bridge:{Guid.NewGuid():N}";
        var completion = new TaskCompletionSource<RendererReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(requestId, completion)) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        try
        {
            _postHostMessage(new
            {
                type = "conversationBridge.request",
                version = ProtocolVersion,
                requestId,
                operation,
                payload,
            });
            using var timeoutSource = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(8));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
            var reply = await completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            if (!reply.Ok)
            {
                return Results.Json(new { error = reply.Error ?? "The Hermes dock rejected the bridge request." }, statusCode: StatusCodes.Status409Conflict);
            }
            return reply.Snapshot.ValueKind == JsonValueKind.Undefined
                ? Results.Json(new { accepted = true })
                : Results.Json(reply.Snapshot);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Json(new { error = "The visible Hermes dock did not answer the bridge request in time." }, statusCode: StatusCodes.Status504GatewayTimeout);
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    private BridgeSettings LoadOrCreateSettings()
    {
        var path = SettingsPath;
        if (File.Exists(path))
        {
            try
            {
                var existing = JsonSerializer.Deserialize<BridgeSettings>(File.ReadAllText(path), JsonOptions);
                if (existing is not null && existing.Port is >= 1024 and <= 65535 && existing.AuthenticationToken?.Length >= 32) return existing;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                DesktopLog.Write($"Hermes bridge settings could not be read ({exception.GetType().Name}); generating current settings.");
            }
        }
        var settings = new BridgeSettings(DefaultPort, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporary, path, overwrite: true);
        return settings;
    }

    private static bool HasValidBearerToken(HttpContext context, string expected)
    {
        var supplied = context.Request.Headers.Authorization.ToString();
        if (!supplied.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        var token = supplied[7..].Trim();
        var left = System.Text.Encoding.UTF8.GetBytes(token);
        var right = System.Text.Encoding.UTF8.GetBytes(expected);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_application is not null)
            {
                await _application.StopAsync().ConfigureAwait(false);
                await _application.DisposeAsync().ConfigureAwait(false);
                _application = null;
            }
            foreach (var completion in _pending.Values) completion.TrySetCanceled();
            _pending.Clear();
        }
        finally
        {
            _lifecycleGate.Release();
            _lifecycleGate.Dispose();
            _turnGate.Dispose();
        }
    }

    private sealed record BridgeSettings(int Port, string AuthenticationToken)
    {
        public string Endpoint => $"http://127.0.0.1:{Port}";
    }
    private sealed record BridgeTurnRequest(string? Text);
    private sealed record RendererReply(bool Ok, JsonElement Snapshot, string? Error);
}
