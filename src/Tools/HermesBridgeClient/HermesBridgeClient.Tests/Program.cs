using System.Net;
using System.Text;
using System.Text.Json;
using HermesBridgeClient;

var suite = new OfflineTestSuite();
var syntheticToken = new string('A', 64);
var loopback = new Uri("http://127.0.0.1:8972/");

BridgeConnectionSettings Settings() =>
    new(loopback, BridgeAuthentication.Create(syntheticToken));

await suite.TestAsync("default endpoint is the Hermes loopback port", () =>
{
    Check.Equal(loopback, BridgeEndpointPolicy.DefaultEndpoint);
    return Task.CompletedTask;
});

await suite.TestAsync("literal IPv4 and IPv6 loopback endpoints are accepted", () =>
{
    _ = BridgeEndpointPolicy.RequireLoopback(new Uri("http://127.0.0.1:8972/"));
    _ = BridgeEndpointPolicy.RequireLoopback(new Uri("http://[::1]:8972/"));
    return Task.CompletedTask;
});

await suite.TestAsync("remote endpoints are refused", () =>
{
    Check.Throws<ArgumentException>(() => BridgeEndpointPolicy.RequireLoopback(new Uri("http://192.0.2.1:8972/")));
    Check.Throws<ArgumentException>(() => BridgeEndpointPolicy.RequireLoopback(new Uri("https://127.0.0.1:8972/")));
    return Task.CompletedTask;
});

await suite.TestAsync("DNS loopback names are refused", () =>
{
    Check.Throws<ArgumentException>(() => BridgeEndpointPolicy.RequireLoopback(new Uri("http://localhost:8972/")));
    return Task.CompletedTask;
});

await suite.TestAsync("endpoint decorations are refused", () =>
{
    Check.Throws<ArgumentException>(() => BridgeEndpointPolicy.RequireLoopback(new Uri("http://user@127.0.0.1:8972/")));
    Check.Throws<ArgumentException>(() => BridgeEndpointPolicy.RequireLoopback(new Uri("http://127.0.0.1:8972/v1")));
    Check.Throws<ArgumentException>(() => BridgeEndpointPolicy.RequireLoopback(new Uri("http://127.0.0.1:8972/?x=1")));
    return Task.CompletedTask;
});

await suite.TestAsync("client refuses a remote endpoint before transport", async () =>
{
    var handler = new RecordingHandler((_, _) => throw new InvalidOperationException("Transport must not run."));
    using var settings = new BridgeConnectionSettings(
        new Uri("http://192.0.2.1:8972/"), BridgeAuthentication.Create(syntheticToken));
    using var client = new BridgeApiClient(handler);
    var result = await client.StatusAsync(settings);
    Check.Equal(BridgeExitCode.UsageOrConfiguration, result.ExitCode);
    Check.Equal(0, handler.CallCount);
});

var temporaryRoot = Path.Combine(Path.GetTempPath(), $"hermes-bridge-client-tests-{Guid.NewGuid():N}");
Directory.CreateDirectory(temporaryRoot);
try
{
    await suite.TestAsync("valid settings are loaded without exposing authentication", () =>
    {
        var path = Path.Combine(temporaryRoot, "valid.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { port = 8972, authenticationToken = syntheticToken }));
        using var settings = new BridgeSettingsLoader().Load(path);
        Check.Equal(loopback, settings.Endpoint);
        Check.True(!settings.ToString().Contains(syntheticToken, StringComparison.Ordinal));
        return Task.CompletedTask;
    });

    await suite.TestAsync("missing settings have a fixed safe error", () =>
    {
        var exception = Check.Throws<BridgeSettingsException>(() =>
            new BridgeSettingsLoader().Load(Path.Combine(temporaryRoot, "missing.json")));
        Check.True(!exception.Message.Contains(temporaryRoot, StringComparison.Ordinal));
        return Task.CompletedTask;
    });

    await suite.TestAsync("malformed settings are rejected", () =>
    {
        var path = Path.Combine(temporaryRoot, "malformed.json");
        File.WriteAllText(path, "{invalid");
        _ = Check.Throws<BridgeSettingsException>(() => new BridgeSettingsLoader().Load(path));
        return Task.CompletedTask;
    });

    await suite.TestAsync("invalid authentication code is rejected without echo", () =>
    {
        var path = Path.Combine(temporaryRoot, "invalid-token.json");
        var invalid = string.Concat("not-", "a-valid-code");
        File.WriteAllText(path, JsonSerializer.Serialize(new { port = 8972, authenticationToken = invalid }));
        var exception = Check.Throws<BridgeSettingsException>(() => new BridgeSettingsLoader().Load(path));
        Check.True(!exception.Message.Contains(invalid, StringComparison.Ordinal));
        return Task.CompletedTask;
    });

    await suite.TestAsync("oversized settings are rejected", () =>
    {
        var path = Path.Combine(temporaryRoot, "oversized.json");
        File.WriteAllText(path, new string('x', 300));
        var policy = new BridgeClientPolicy(TimeSpan.FromSeconds(1), 1024, 256);
        _ = Check.Throws<BridgeSettingsException>(() => new BridgeSettingsLoader(policy).Load(path));
        return Task.CompletedTask;
    });
}
finally
{
    Directory.Delete(temporaryRoot, recursive: true);
}

await suite.TestAsync("health is GET and never authenticated", async () =>
{
    var handler = new RecordingHandler((request, _) =>
    {
        Check.Equal(HttpMethod.Get, request.Method);
        Check.Equal("/health", request.RequestUri!.AbsolutePath);
        Check.True(request.Headers.Authorization is null);
        return Task.FromResult(Http.Json("{\"service\":\"Hermes Conversation Bridge\"}"));
    });
    using var client = new BridgeApiClient(handler);
    Check.True((await client.HealthAsync()).IsSuccess);
    Check.Equal(1, handler.CallCount);
});

await suite.TestAsync("status authenticates and redacts an echoed code", async () =>
{
    var handler = new RecordingHandler((request, _) =>
    {
        Check.Equal(HttpMethod.Get, request.Method);
        Check.Equal("/v1/session", request.RequestUri!.AbsolutePath);
        Check.True(request.Headers.Authorization?.Scheme == "Bearer");
        Check.True(request.Headers.Authorization?.Parameter == syntheticToken);
        return Task.FromResult(Http.Json(JsonSerializer.Serialize(new { value = $"prefix-{syntheticToken}-suffix" })));
    });
    using var settings = Settings();
    using var client = new BridgeApiClient(handler);
    var result = await client.StatusAsync(settings);
    Check.True(result.IsSuccess);
    Check.True(!result.Output!.Contains(syntheticToken, StringComparison.Ordinal));
    Check.True(result.Output.Contains("[redacted]", StringComparison.Ordinal));
});

await suite.TestAsync("send uses explicit JSON on an in-memory transport only", async () =>
{
    var handler = new RecordingHandler(async (request, token) =>
    {
        Check.Equal(HttpMethod.Post, request.Method);
        Check.Equal("/v1/turns", request.RequestUri!.AbsolutePath);
        Check.True(request.Headers.Authorization?.Parameter == syntheticToken);
        var body = await request.Content!.ReadAsStringAsync(token);
        using var json = JsonDocument.Parse(body);
        Check.Equal("synthetic explicit text", json.RootElement.GetProperty("text").GetString()!);
        return Http.Json("{\"accepted\":true}");
    });
    using var settings = Settings();
    using var client = new BridgeApiClient(handler);
    Check.True((await client.SendAsync(settings, "synthetic explicit text")).IsSuccess);
    Check.Equal(1, handler.CallCount);
});

await suite.TestAsync("empty send is rejected before transport", async () =>
{
    var handler = new RecordingHandler((_, _) => throw new InvalidOperationException("Transport must not run."));
    using var settings = Settings();
    using var client = new BridgeApiClient(handler);
    var result = await client.SendAsync(settings, "  ");
    Check.Equal(BridgeExitCode.UsageOrConfiguration, result.ExitCode);
    Check.Equal(0, handler.CallCount);
});

await suite.TestAsync("interrupt posts without a message", async () =>
{
    var handler = new RecordingHandler((request, _) =>
    {
        Check.Equal(HttpMethod.Post, request.Method);
        Check.Equal("/v1/interrupt", request.RequestUri!.AbsolutePath);
        Check.True(request.Content is null);
        return Task.FromResult(Http.Json("{\"accepted\":true}"));
    });
    using var settings = Settings();
    using var client = new BridgeApiClient(handler);
    Check.True((await client.InterruptAsync(settings)).IsSuccess);
});

await VerifyStatusAsync("401 is explicit", HttpStatusCode.Unauthorized, BridgeExitCode.AuthenticationFailed);
await VerifyStatusAsync("409 is explicit", HttpStatusCode.Conflict, BridgeExitCode.Conflict);
await VerifyStatusAsync("503 is explicit", HttpStatusCode.ServiceUnavailable, BridgeExitCode.ServiceUnavailable);
await VerifyStatusAsync("504 is explicit", HttpStatusCode.GatewayTimeout, BridgeExitCode.GatewayTimeout);

await suite.TestAsync("redirects are refused", async () =>
{
    var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
    {
        Headers = { Location = new Uri("http://192.0.2.1/not-followed") }
    }));
    using var client = new BridgeApiClient(handler);
    var result = await client.HealthAsync();
    Check.Equal(BridgeExitCode.ConnectivityOrProtocol, result.ExitCode);
});

await suite.TestAsync("malformed JSON is rejected", async () =>
{
    var handler = new RecordingHandler((_, _) => Task.FromResult(Http.Json("{invalid")));
    using var client = new BridgeApiClient(handler);
    Check.Equal(BridgeExitCode.ConnectivityOrProtocol, (await client.HealthAsync()).ExitCode);
});

await suite.TestAsync("non-JSON content is rejected", async () =>
{
    var handler = new RecordingHandler((_, _) => Task.FromResult(Http.Text("not json")));
    using var client = new BridgeApiClient(handler);
    Check.Equal(BridgeExitCode.ConnectivityOrProtocol, (await client.HealthAsync()).ExitCode);
});

await suite.TestAsync("oversized responses are rejected", async () =>
{
    var handler = new RecordingHandler((_, _) => Task.FromResult(Http.Json(new string('x', 2048))));
    using var client = new BridgeApiClient(handler, new BridgeClientPolicy(TimeSpan.FromSeconds(1), 1024, 256));
    Check.Equal(BridgeExitCode.ConnectivityOrProtocol, (await client.HealthAsync()).ExitCode);
});

await suite.TestAsync("client timeout is explicit", async () =>
{
    var handler = new RecordingHandler(async (_, token) =>
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        throw new InvalidOperationException();
    });
    using var client = new BridgeApiClient(handler, new BridgeClientPolicy(TimeSpan.FromMilliseconds(20), 1024, 256));
    Check.Equal(BridgeExitCode.GatewayTimeout, (await client.HealthAsync()).ExitCode);
});

await suite.TestAsync("caller cancellation is explicit", async () =>
{
    var handler = new RecordingHandler(async (_, token) =>
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        throw new InvalidOperationException();
    });
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
    using var client = new BridgeApiClient(handler, new BridgeClientPolicy(TimeSpan.FromSeconds(1), 1024, 256));
    var result = await client.HealthAsync(cancellationToken: cancellation.Token);
    Check.Equal(BridgeExitCode.GatewayTimeout, result.ExitCode);
    Check.True(result.Error!.Contains("cancelled", StringComparison.OrdinalIgnoreCase));
});

await suite.TestAsync("error response bodies cannot leak the code", async () =>
{
    var handler = new RecordingHandler((_, _) => Task.FromResult(Http.Json(
        JsonSerializer.Serialize(new { error = syntheticToken }), HttpStatusCode.Unauthorized)));
    using var settings = Settings();
    using var client = new BridgeApiClient(handler);
    var result = await client.StatusAsync(settings);
    Check.True(!result.Error!.Contains(syntheticToken, StringComparison.Ordinal));
});

await suite.TestAsync("CLI rejects implicit send text without a request", async () =>
{
    using var output = new StringWriter();
    using var error = new StringWriter();
    var exit = await BridgeCli.RunAsync(["send", "implicit text"], output, error, CancellationToken.None);
    Check.Equal((int)BridgeExitCode.UsageOrConfiguration, exit);
    Check.True(error.ToString().Contains("Invalid arguments", StringComparison.Ordinal));
});

suite.Complete();

async Task VerifyStatusAsync(string name, HttpStatusCode status, BridgeExitCode expected)
{
    await suite.TestAsync(name, async () =>
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Http.Json("{\"error\":\"suppressed\"}", status)));
        using var settings = Settings();
        using var client = new BridgeApiClient(handler);
        Check.Equal(expected, (await client.StatusAsync(settings)).ExitCode);
    });
}

internal sealed class RecordingHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
{
    private int _calls;
    internal int CallCount => _calls;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        return responder(request, cancellationToken);
    }
}

internal static class Http
{
    internal static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    internal static HttpResponseMessage Text(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };
}

internal sealed class OfflineTestSuite
{
    private int _passed;
    private int _failed;

    internal async Task TestAsync(string name, Func<Task> test)
    {
        try
        {
            await test();
            _passed++;
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception exception)
        {
            _failed++;
            Console.WriteLine($"FAIL {name}: {exception.GetType().Name}");
        }
    }

    internal void Complete()
    {
        Console.WriteLine($"RESULT {_passed} passed, {_failed} failed, {_passed + _failed} total");
        if (_failed != 0) Environment.ExitCode = 1;
    }
}

internal static class Check
{
    internal static void True(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed.");
    }

    internal static void Equal<T>(T expected, T actual) where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException("Values differ.");
    }

    internal static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T exception) { return exception; }
        throw new InvalidOperationException("Expected exception was not thrown.");
    }
}
