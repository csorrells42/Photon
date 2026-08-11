using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AssistantConversationBus;

public sealed class AssistantBusHttpService : IAsyncDisposable
{
    private readonly WebApplication _application;
    private readonly AssistantBusRuntime _runtime;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _waits = new(StringComparer.Ordinal);

    private AssistantBusHttpService(WebApplication application, AssistantBusRuntime runtime)
    {
        _application = application;
        _runtime = runtime;
    }

    public static async Task<AssistantBusHttpService> StartAsync(
        AssistantBusServiceSettings settings,
        CancellationToken cancellationToken = default,
        string? dataRoot = null,
        IEnumerable<IAssistantPeerBridge>? peers = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var runtime = await AssistantBusRuntime.OpenAsync(cancellationToken, dataRoot, peers).ConfigureAwait(false);
        try
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(IPAddress.Loopback, settings.Port, listen => listen.Protocols = HttpProtocols.Http1);
                options.Limits.MaxRequestBodySize = AssistantBusLimits.MaximumServiceRequestBytes;
                options.AddServerHeader = false;
            });
            builder.Services.ConfigureHttpJsonOptions(options =>
            {
                options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                options.SerializerOptions.MaxDepth = 32;
            });
            var application = builder.Build();
            var service = new AssistantBusHttpService(application, runtime);
            service.MapRoutes(settings);
            await application.StartAsync(cancellationToken).ConfigureAwait(false);
            return service;
        }
        catch
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task WaitForShutdownAsync(CancellationToken cancellationToken = default) =>
        _application.WaitForShutdownAsync(cancellationToken);

    private void MapRoutes(AssistantBusServiceSettings settings)
    {
        _application.MapGet("/health", () => Results.Json(new
        {
            status = "ready",
            protocolVersion = AssistantBusLimits.ProtocolVersion,
        }));

        _application.MapGet("/v1/participants", async (HttpContext context) =>
        {
            if (!TryAuthenticate(context, settings, out _)) return Unauthorized();
            return Results.Json(await _runtime.Router.ParticipantsAsync(context.RequestAborted).ConfigureAwait(false));
        });

        _application.MapGet("/v1/messages", async (HttpContext context) =>
        {
            if (!TryAuthenticate(context, settings, out _)) return Unauthorized();
            if (!TryLong(context, "afterSequence", 0, long.MaxValue, out var after)
                || !TryInt(context, "limit", 1, AssistantBusLimits.MaximumReadMessages, out var limit))
                return Invalid("invalid_query");
            return Results.Json(await _runtime.Router.ReadAsync(after, limit, context.RequestAborted).ConfigureAwait(false));
        });

        _application.MapGet("/v1/messages/{messageId}", async (HttpContext context, string messageId) =>
        {
            if (!TryAuthenticate(context, settings, out _)) return Unauthorized();
            if (messageId.Length is < 5 or > 128) return Invalid("invalid_message_id");
            var message = await _runtime.Router.FindAsync(messageId, context.RequestAborted).ConfigureAwait(false);
            return message is null ? Results.NotFound() : Results.Json(message);
        });

        _application.MapPost("/v1/messages", async (HttpContext context, AssistantBusSendRequest request) =>
        {
            if (!TryAuthenticate(context, settings, out var sender)) return Unauthorized();
            try
            {
                return Results.Json(
                    await _runtime.Router.SendAsync(sender, request, context.RequestAborted).ConfigureAwait(false));
            }
            catch (AssistantBusValidationException exception)
            {
                return Invalid(exception.Code);
            }
        });

        _application.MapGet("/v1/wait", async (HttpContext context) =>
        {
            if (!TryAuthenticate(context, settings, out _)) return Unauthorized();
            if (!TryLong(context, "afterSequence", 0, long.MaxValue, out var after)
                || !TryInt(context, "timeoutMs", 1, 1_800_000, out var timeoutMs))
                return Invalid("invalid_query");
            if (_waits.Count >= AssistantBusLimits.MaximumConcurrentWaits) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            var requestedWaitId = context.Request.Query["waitId"].ToString();
            var waitId = string.IsNullOrEmpty(requestedWaitId) ? $"wait:{Guid.NewGuid():N}" : requestedWaitId;
            if (waitId.Length != 37 || !waitId.StartsWith("wait:", StringComparison.Ordinal)) return Invalid("invalid_wait_id");
            using var manual = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, manual.Token);
            linked.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
            if (!_waits.TryAdd(waitId, manual)) return Results.StatusCode(StatusCodes.Status409Conflict);
            context.Response.Headers["X-Assistant-Wait-Id"] = waitId;
            try
            {
                while (!linked.IsCancellationRequested)
                {
                    var messages = await _runtime.Router.ReadAsync(after, AssistantBusLimits.MaximumReadMessages, linked.Token).ConfigureAwait(false);
                    if (messages.Count > 0) return Results.Json(new { waitId, cancelled = false, messages });
                    await Task.Delay(100, linked.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            finally { _waits.TryRemove(waitId, out _); }
            return Results.Json(new { waitId, cancelled = manual.IsCancellationRequested || context.RequestAborted.IsCancellationRequested, messages = Array.Empty<AssistantBusMessage>() });
        });

        _application.MapPost("/v1/waits/{waitId}/cancel", (HttpContext context, string waitId) =>
        {
            if (!TryAuthenticate(context, settings, out _)) return Unauthorized();
            if (waitId.Length != 37 || !waitId.StartsWith("wait:", StringComparison.Ordinal)) return Invalid("invalid_wait_id");
            if (!_waits.TryGetValue(waitId, out var wait)) return Results.NotFound();
            wait.Cancel();
            return Results.Json(new { waitId, cancelled = true });
        });
    }

    private static bool TryAuthenticate(
        HttpContext context,
        AssistantBusServiceSettings settings,
        out AssistantIdentity identity)
    {
        identity = default;
        var authorization = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return authorization.StartsWith(prefix, StringComparison.Ordinal)
            && settings.TryAuthenticate(authorization[prefix.Length..], out identity);
    }

    private static bool TryLong(HttpContext context, string name, long minimum, long maximum, out long value) =>
        long.TryParse(context.Request.Query[name], out value) && value >= minimum && value <= maximum;

    private static bool TryInt(HttpContext context, string name, int minimum, int maximum, out int value) =>
        int.TryParse(context.Request.Query[name], out value) && value >= minimum && value <= maximum;

    private static IResult Unauthorized() => Results.Json(new { code = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);
    private static IResult Invalid(string code) => Results.Json(new { code }, statusCode: StatusCodes.Status400BadRequest);

    public async ValueTask DisposeAsync()
    {
        foreach (var wait in _waits.Values) wait.Cancel();
        await _application.StopAsync().ConfigureAwait(false);
        await _application.DisposeAsync().ConfigureAwait(false);
        await _runtime.DisposeAsync().ConfigureAwait(false);
    }
}
