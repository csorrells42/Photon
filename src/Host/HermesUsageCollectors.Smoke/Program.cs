using System.Net;
using System.Text;
using System.Text.Json;
using HermesUsageCollectors;
using HermesUsageCollectors.Smoke;

var suite = new SmokeSuite();
var window = new ObservationWindow(
    new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
    new DateTimeOffset(2026, 7, 2, 0, 0, 0, TimeSpan.Zero));
var request = new UsageCollectionRequest(window, MaximumPages: 4, MaximumRows: 100);
var syntheticCredential = string.Concat("synthetic", "-credential");

DelegateSecretSource Secrets() => new((_, _) =>
    ValueTask.FromResult<ProviderSecret?>(ProviderSecret.FromString(syntheticCredential)));

ProviderCredentialProfile Profile(string provider, string id = "profile-1", IReadOnlyDictionary<string, string>? settings = null) =>
    new(provider, id, "Synthetic profile", "host-secret-reference", settings);

await suite.RunAsync("OpenAI request shape, pagination, and normalization", async () =>
{
    var handler = new ScriptedHandler((message, _, _) =>
    {
        if (message.RequestUri!.AbsolutePath.EndsWith("/costs", StringComparison.Ordinal))
            return Task.FromResult(SyntheticHttp.Json(OpenAiCostPage(2.50m)));
        var second = message.RequestUri.Query.Contains("page=usage-page-2", StringComparison.Ordinal);
        return Task.FromResult(SyntheticHttp.Json(OpenAiUsagePage(second ? 7 : 11, !second, second ? null : "usage-page-2")));
    }, syntheticCredential);
    using var collector = new OpenAiUsageCollector(Secrets(), handler);
    var result = await collector.CollectAsync(Profile(UsageProviderIds.OpenAiApi), request);
    Verify.Equal(3, handler.Requests.Count);
    Verify.True(handler.Requests.All(item => item.Method == HttpMethod.Get && item.Scheme == "https" &&
        item.Host == "api.openai.com" && item.AuthorizationScheme == "Bearer" && item.CredentialMatched));
    Verify.True(handler.Requests[0].Query.Contains("start_time=", StringComparison.Ordinal));
    Verify.True(handler.Requests[1].Query.Contains("page=usage-page-2", StringComparison.Ordinal));
    Verify.True(result.Observations.Any(item => item.Metric == "input-tokens"),
        $"OpenAI metrics: {string.Join(',', result.Observations.Select(item => item.Metric))}; errors: {string.Join(',', result.Errors.Select(item => item.Code))}");
    Verify.Equal(18m, result.Observations.Single(item => item.Metric == "input-tokens").Value);
    Verify.True(result.Observations.Any(item => item.Metric == "actual-cost"),
        $"OpenAI metrics: {string.Join(',', result.Observations.Select(item => item.Metric))}; errors: {string.Join(',', result.Errors.Select(item => item.Code))}");
    Verify.Equal(2.50m, result.Observations.Single(item => item.Metric == "actual-cost").Value);
});

await suite.RunAsync("Anthropic request shape and normalization", async () =>
{
    var handler = new ScriptedHandler((message, _, _) => Task.FromResult(SyntheticHttp.Json(
        message.RequestUri!.AbsolutePath.EndsWith("cost_report", StringComparison.Ordinal)
            ? AnthropicCostPage("123.45") : AnthropicUsagePage())), syntheticCredential);
    using var collector = new AnthropicUsageCollector(Secrets(), handler);
    var result = await collector.CollectAsync(Profile(UsageProviderIds.AnthropicApi), request);
    Verify.Equal(2, handler.Requests.Count);
    Verify.True(handler.Requests.All(item => item.Method == HttpMethod.Get && item.Host == "api.anthropic.com" &&
        item.AuthorizationScheme is null && item.CredentialMatched && item.AnthropicVersionMatched));
    Verify.True(handler.Requests[0].Query.Contains("starting_at=", StringComparison.Ordinal));
    Verify.True(result.Observations.Any(item => item.Metric == "actual-cost"),
        $"Anthropic metrics: {string.Join(',', result.Observations.Select(item => item.Metric))}; errors: {string.Join(',', result.Errors.Select(item => item.Code))}");
    Verify.Equal(1.2345m, result.Observations.Single(item => item.Metric == "actual-cost").Value);
    Verify.Equal(9m, result.Observations.Single(item => item.Metric == "cache-write-input-tokens").Value);
});

await suite.RunAsync("Google budgets remain configuration, not spend", async () =>
{
    var handler = new ScriptedHandler((_, _, _) => Task.FromResult(SyntheticHttp.Json(GoogleBudgetPage())), syntheticCredential);
    using var collector = new GoogleCloudBudgetCollector(Secrets(), handler);
    var settings = new Dictionary<string, string> { [GoogleCloudBudgetCollector.BillingAccountSetting] = "ABCDEF-123456-ABCDEF" };
    var result = await collector.CollectAsync(Profile(UsageProviderIds.GoogleCloudBudgets, settings: settings), request);
    Verify.True(handler.Requests.Single().Path == "/v1/billingAccounts/ABCDEF-123456-ABCDEF/budgets");
    Verify.True(handler.Requests.Single().AuthorizationScheme == "Bearer" && handler.Requests.Single().CredentialMatched);
    Verify.True(result.Observations.Any(item => item.Metric == "configured-budget-amount"),
        $"Google metrics: {string.Join(',', result.Observations.Select(item => item.Metric))}; errors: {string.Join(',', result.Errors.Select(item => item.Code))}");
    Verify.Equal(1000.5m, result.Observations.Single(item => item.Metric == "configured-budget-amount").Value);
    Verify.True(result.Observations.All(item => item.Provenance == ValueProvenance.ConfiguredThreshold));
    Verify.True(result.Capabilities.Any(item => item.Capability == "actual-spend" && item.State == CapabilityState.SetupRequired));
});

await suite.RunAsync("Google billing export normalizes net actual cost", async () =>
{
    var source = new StubBillingExportSource([
        new GoogleBillingExportPage([
            new GoogleBillingExportCostRow(12m, -2m, "usd", window.Start, window.End, window.End)
        ], null)
    ]);
    var collector = new GoogleBillingExportCollector(source);
    var settings = new Dictionary<string, string> { [GoogleBillingExportCollector.TargetReferenceSetting] = "configured-export-1" };
    var result = await collector.CollectAsync(Profile(UsageProviderIds.GoogleCloudBillingExport, settings: settings), request);
    var observation = result.Observations.Single();
    Verify.Equal(10m, observation.Value);
    Verify.Equal(ValueProvenance.ProviderReported, observation.Provenance);
    Verify.Equal("USD", observation.Currency!);
});

await suite.RunAsync("Gemini monitoring uses fixed project-level meters without key attribution", async () =>
{
    var projectMarker = "synthetic-project";
    var labelMarker = "provider-label-private-marker";
    var handler = new ScriptedHandler((message, _, _) =>
    {
        var query = Uri.UnescapeDataString(message.RequestUri!.Query);
        var value = query switch
        {
            var text when text.Contains("generate_content_usage_output_token_count", StringComparison.Ordinal) => 11,
            var text when text.Contains("generate_content_free_tier_input_token_count", StringComparison.Ordinal) => 7,
            var text when text.Contains("generate_content_paid_tier_input_token_count", StringComparison.Ordinal) => 8,
            var text when text.Contains("generate_content_free_tier_requests", StringComparison.Ordinal) => 2,
            var text when text.Contains("generate_requests_per_model", StringComparison.Ordinal) => 3,
            _ => throw new InvalidOperationException("Unexpected metric filter."),
        };
        return Task.FromResult(SyntheticHttp.Json(GoogleTimeSeriesPage(value, labelMarker)));
    }, syntheticCredential);
    using var collector = new GoogleGeminiMonitoringCollector(Secrets(), handler);
    var settings = new Dictionary<string, string> { [GoogleGeminiMonitoringCollector.ProjectSetting] = projectMarker };
    var result = await collector.CollectAsync(Profile(UsageProviderIds.GoogleAiStudio, settings: settings), request);

    Verify.Equal(5, handler.Requests.Count);
    Verify.True(handler.Requests.All(item => item.Method == HttpMethod.Get && item.Scheme == "https"
        && item.Host == "monitoring.googleapis.com" && item.Path == $"/v3/projects/{projectMarker}/timeSeries"
        && item.AuthorizationScheme == "Bearer" && item.CredentialMatched));
    Verify.True(handler.Requests.All(item => Uri.UnescapeDataString(item.Query).Contains("metric.type = \"generativelanguage.googleapis.com/", StringComparison.Ordinal)));
    Verify.Equal(15m, result.Observations.Single(item => item.Metric == "input-tokens").Value);
    Verify.Equal(11m, result.Observations.Single(item => item.Metric == "output-tokens").Value);
    Verify.Equal(5m, result.Observations.Single(item => item.Metric == "requests").Value);
    Verify.True(result.Observations.All(item => item.Qualifier!.Contains("not API-key attributed", StringComparison.Ordinal)));
    Verify.True(result.Capabilities.Any(item => item.Capability == "gemini-per-key-usage" && item.State == CapabilityState.Unavailable));
    var serialized = JsonSerializer.Serialize(result);
    Verify.True(!serialized.Contains(projectMarker, StringComparison.Ordinal));
    Verify.True(!serialized.Contains(labelMarker, StringComparison.Ordinal));
    Verify.True(!serialized.Contains(syntheticCredential, StringComparison.Ordinal));
});

await suite.RunAsync("Gemini monitoring enforces pagination bounds", async () =>
{
    var handler = new ScriptedHandler((_, call, _) => Task.FromResult(
        SyntheticHttp.Json(GoogleTimeSeriesPage(1, "ignored", $"page-{call}"))), syntheticCredential);
    using var collector = new GoogleGeminiMonitoringCollector(Secrets(), handler);
    var settings = new Dictionary<string, string> { [GoogleGeminiMonitoringCollector.ProjectSetting] = "synthetic-project" };
    var bounded = new UsageCollectionRequest(window, MaximumPages: 1, MaximumRows: 100);
    Verify.Category(await collector.CollectAsync(Profile(UsageProviderIds.GoogleAiStudio, settings: settings), bounded),
        ProviderErrorCategory.PaginationLimit);
});

await suite.RunAsync("Missing host credential yields setup required", async () =>
{
    var secrets = new DelegateSecretSource((_, _) => ValueTask.FromResult<ProviderSecret?>(null));
    using var collector = new OpenAiUsageCollector(secrets, new ScriptedHandler((_, _, _) =>
        throw new InvalidOperationException("HTTP must not run."), syntheticCredential));
    var result = await collector.CollectAsync(Profile(UsageProviderIds.OpenAiApi), request);
    Verify.True(result.Capabilities.Any(item => item.State == CapabilityState.SetupRequired));
    Verify.True(result.Errors.Count == 0 && result.Observations.Count == 0);
});

await suite.RunAsync("Credential-source failures are sanitized", async () =>
{
    var sensitiveMarker = string.Concat("credential", "-resolver-private-marker");
    var secrets = new DelegateSecretSource((_, _) => throw new InvalidOperationException(sensitiveMarker));
    using var collector = new OpenAiUsageCollector(secrets, new ScriptedHandler((_, _, _) =>
        throw new InvalidOperationException("HTTP must not run."), syntheticCredential));
    var result = await collector.CollectAsync(Profile(UsageProviderIds.OpenAiApi), request);
    Verify.True(result.Errors.Single().Code == "credential-source-failure");
    Verify.True(!JsonSerializer.Serialize(result).Contains(sensitiveMarker, StringComparison.Ordinal));
});

await suite.RunAsync("Unauthorized is classified", () => VerifyHttpFailure(HttpStatusCode.Unauthorized, ProviderErrorCategory.Unauthorized));
await suite.RunAsync("Forbidden is classified", () => VerifyHttpFailure(HttpStatusCode.Forbidden, ProviderErrorCategory.Forbidden));
await suite.RunAsync("Rate limiting is classified", () => VerifyHttpFailure((HttpStatusCode)429, ProviderErrorCategory.RateLimited));
await suite.RunAsync("Provider failure is classified", () => VerifyHttpFailure(HttpStatusCode.ServiceUnavailable, ProviderErrorCategory.RemoteFailure));

await suite.RunAsync("Malformed JSON is rejected", async () =>
{
    var handler = new ScriptedHandler((_, _, _) => Task.FromResult(SyntheticHttp.Json("{invalid")), syntheticCredential);
    using var collector = new OpenAiUsageCollector(Secrets(), handler);
    Verify.Category(await collector.CollectAsync(Profile(UsageProviderIds.OpenAiApi), request), ProviderErrorCategory.MalformedResponse);
});

await suite.RunAsync("Oversized response is rejected", async () =>
{
    var handler = new ScriptedHandler((_, _, _) => Task.FromResult(SyntheticHttp.Bytes(new byte[2048])), syntheticCredential);
    using var collector = new OpenAiUsageCollector(Secrets(), handler, new CollectorHttpPolicy(TimeSpan.FromSeconds(1), 1024, 256));
    Verify.Category(await collector.CollectAsync(Profile(UsageProviderIds.OpenAiApi), request), ProviderErrorCategory.OversizedResponse);
});

await suite.RunAsync("Redirect is rejected", async () =>
{
    var handler = new ScriptedHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
    {
        Headers = { Location = new Uri("https://example.invalid/not-followed") }
    }), syntheticCredential);
    using var collector = new OpenAiUsageCollector(Secrets(), handler);
    Verify.Category(await collector.CollectAsync(Profile(UsageProviderIds.OpenAiApi), request), ProviderErrorCategory.Redirected);
});

await suite.RunAsync("Timeout is classified", async () =>
{
    var handler = new ScriptedHandler(async (_, _, token) =>
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        throw new InvalidOperationException();
    }, syntheticCredential);
    using var collector = new OpenAiUsageCollector(Secrets(), handler, new CollectorHttpPolicy(TimeSpan.FromMilliseconds(20), 1024, 256));
    Verify.Category(await collector.CollectAsync(Profile(UsageProviderIds.OpenAiApi), request), ProviderErrorCategory.Timeout);
});

await suite.RunAsync("Caller cancellation is classified", async () =>
{
    var handler = new ScriptedHandler(async (_, _, token) =>
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        throw new InvalidOperationException();
    }, syntheticCredential);
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
    using var collector = new OpenAiUsageCollector(Secrets(), handler, new CollectorHttpPolicy(TimeSpan.FromSeconds(2), 1024, 256));
    Verify.Category(await collector.CollectAsync(Profile(UsageProviderIds.OpenAiApi), request, cancellation.Token), ProviderErrorCategory.Cancelled);
});

await suite.RunAsync("Provider errors are sanitized", async () =>
{
    var sensitiveMarker = string.Concat("private", "-provider-message-marker");
    var body = JsonSerializer.Serialize(new { error = new { code = "safe_code", message = sensitiveMarker } });
    var handler = new ScriptedHandler((_, _, _) => Task.FromResult(SyntheticHttp.Json(body, HttpStatusCode.BadRequest)), syntheticCredential);
    using var collector = new OpenAiUsageCollector(Secrets(), handler);
    var result = await collector.CollectAsync(Profile(UsageProviderIds.OpenAiApi), request);
    var serialized = JsonSerializer.Serialize(result);
    Verify.True(!serialized.Contains(sensitiveMarker, StringComparison.Ordinal));
    Verify.True(!serialized.Contains(syntheticCredential, StringComparison.Ordinal));
    Verify.True(result.Errors.All(item => item.ProviderCode == "safe_code"));
});

await suite.RunAsync("Pagination bounds stop unbounded pages", async () =>
{
    var handler = new ScriptedHandler((message, call, _) =>
    {
        if (message.RequestUri!.AbsolutePath.EndsWith("/costs", StringComparison.Ordinal))
            return Task.FromResult(SyntheticHttp.Json(OpenAiCostPage(0m)));
        return Task.FromResult(SyntheticHttp.Json(OpenAiUsagePage(1, true, $"cursor-{call}")));
    }, syntheticCredential);
    using var collector = new OpenAiUsageCollector(Secrets(), handler);
    var bounded = new UsageCollectionRequest(window, MaximumPages: 2, MaximumRows: 100);
    Verify.Category(await collector.CollectAsync(Profile(UsageProviderIds.OpenAiApi), bounded), ProviderErrorCategory.PaginationLimit);
});

await suite.RunAsync("Row bounds stop oversized logical responses", async () =>
{
    var handler = new ScriptedHandler((message, _, _) => Task.FromResult(SyntheticHttp.Json(
        message.RequestUri!.AbsolutePath.EndsWith("/costs", StringComparison.Ordinal)
            ? OpenAiCostPage(0m) : OpenAiUsagePage(1, false, null))), syntheticCredential);
    using var collector = new OpenAiUsageCollector(Secrets(), handler);
    var bounded = new UsageCollectionRequest(window, MaximumPages: 2, MaximumRows: 1);
    Verify.Category(await collector.CollectAsync(Profile(UsageProviderIds.OpenAiApi), bounded), ProviderErrorCategory.PaginationLimit);
});

await suite.RunAsync("Partial profiles are retained and aggregates flagged", async () =>
{
    ProviderCollectionResult Result(ProviderCredentialProfile profile, decimal value, SanitizedProviderError? error) =>
        new(profile.Metadata, [new CapabilityStatus("usage", error is null ? CapabilityState.Supported : CapabilityState.Error, "Synthetic state.")],
            [new UsageObservation(ObservationKind.Usage, "input-tokens", value, "tokens", null, window,
                "synthetic", ValueProvenance.ProviderReported,
                new ObservationFreshness(window.End, window.End, FreshnessState.Current, "Synthetic."))],
            error is null ? [] : [error], window.End);
    var error = new SanitizedProviderError(ProviderErrorCategory.RemoteFailure, "synthetic-failure", "Synthetic failure.", true);
    var first = new StubCollector("provider-a", (profile, _, _) => Task.FromResult(Result(profile, 2, null)));
    var second = new StubCollector("provider-b", (profile, _, _) => Task.FromResult(Result(profile, 3, error)));
    var coordinator = new UsageCollectionCoordinator([first, second]);
    var result = await coordinator.CollectAsync([
        Profile("provider-a", "a"), Profile("provider-b", "b")
    ], request);
    Verify.Equal(2, result.Profiles.Count);
    Verify.Equal(5m, result.Aggregates.Single().Value);
    Verify.True(result.HasPartialFailures && result.Aggregates.Single().IsPartial);
});

await suite.RunAsync("Unsupported consumer and per-key surfaces are explicit", async () =>
{
    var collectors = new[]
    {
        UnavailableUsageCollector.ChatGptSubscription(),
        UnavailableUsageCollector.ClaudeConsumer(),
        UnavailableUsageCollector.GoogleAiStudioPerKey(),
    };
    foreach (var collector in collectors)
    {
        var result = await collector.CollectAsync(Profile(collector.ProviderId), request);
        Verify.True(result.Capabilities.Single().State == CapabilityState.Unavailable);
        Verify.True(result.Observations.Count == 0);
    }
});

suite.Complete();

async Task VerifyHttpFailure(HttpStatusCode status, ProviderErrorCategory category)
{
    var handler = new ScriptedHandler((_, _, _) => Task.FromResult(SyntheticHttp.Json(
        "{\"error\":{\"code\":\"synthetic_code\",\"message\":\"not retained\"}}", status)), syntheticCredential);
    using var collector = new OpenAiUsageCollector(Secrets(), handler);
    Verify.Category(await collector.CollectAsync(Profile(UsageProviderIds.OpenAiApi), request), category);
}

static string OpenAiUsagePage(int input, bool hasMore, string? nextPage) => JsonSerializer.Serialize(new
{
    @object = "page",
    data = new[] { new { @object = "bucket", start_time = 1782864000, end_time = 1782950400,
        results = new[] { new { @object = "organization.usage.completions.result", input_tokens = input,
            output_tokens = 3, num_model_requests = 1 } } } },
    has_more = hasMore,
    next_page = nextPage,
});

static string OpenAiCostPage(decimal value) => JsonSerializer.Serialize(new
{
    @object = "page",
    data = new[] { new { @object = "bucket", start_time = 1782864000, end_time = 1782950400,
        results = new[] { new { @object = "organization.costs.result", amount = new { value, currency = "usd" } } } } },
    has_more = false,
    next_page = (string?)null,
});

static string AnthropicUsagePage() => """
{"data":[{"starting_at":"2026-07-01T00:00:00Z","ending_at":"2026-07-02T00:00:00Z","results":[{"uncached_input_tokens":10,"cache_creation":{"ephemeral_1h_input_tokens":4,"ephemeral_5m_input_tokens":5},"cache_read_input_tokens":6,"output_tokens":3,"server_tool_use":{"web_search_requests":2}}]}],"has_more":false,"next_page":null}
""";

static string AnthropicCostPage(string amount) => JsonSerializer.Serialize(new
{
    data = new[] { new { starting_at = "2026-07-01T00:00:00Z", ending_at = "2026-07-02T00:00:00Z",
        results = new[] { new { amount, currency = "USD" } } } },
    has_more = false,
    next_page = (string?)null,
});

static string GoogleBudgetPage() => """
{"budgets":[{"amount":{"specifiedAmount":{"currencyCode":"USD","units":"1000","nanos":500000000}},"budgetFilter":{"calendarPeriod":"MONTH"},"thresholdRules":[{"thresholdPercent":0.5,"spendBasis":"CURRENT_SPEND"},{"thresholdPercent":1,"spendBasis":"FORECASTED_SPEND"}]}]}
""";

static string GoogleTimeSeriesPage(int value, string ignoredLabel, string? nextPageToken = null) => JsonSerializer.Serialize(new
{
    timeSeries = new[]
    {
        new
        {
            metric = new { labels = new Dictionary<string, string> { ["api_key"] = ignoredLabel } },
            points = new[]
            {
                new
                {
                    interval = new { startTime = "2026-07-01T23:59:00Z", endTime = "2026-07-02T00:00:00Z" },
                    value = new { int64Value = value.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                },
            },
        },
    },
    nextPageToken,
});
