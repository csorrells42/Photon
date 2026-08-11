using System.Reflection;
using System.Text.Json;
using HermesDesktop;

internal static class DockerControlBridgeSmoke
{
    internal static async Task<bool> RunAsync()
    {
        try
        {
            await VerifyTypedBoundaryAsync().ConfigureAwait(false);
            await VerifyDuplicateAndInvalidRootAsync().ConfigureAwait(false);
            Console.WriteLine("Docker Control Center typed review, one-use commit, redaction, and unavailable-runtime smoke passed.");
            return true;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Docker Control Center smoke failed: {exception.Message}");
            return false;
        }
    }

    private static async Task VerifyTypedBoundaryAsync()
    {
        var frames = new LockedFrames();
        var runner = new FakeDockerControlRunner();
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 8, 10, 18, 0, 0, TimeSpan.Zero));
        var tokenSequence = 0;
        await using var bridge = new DockerControlBridge(
            Path.GetTempPath(),
            frames.Add,
            runner,
            clock,
            () => $"{new string('T', 46)}{++tokenSequence:D2}");

        var rendererMethods = typeof(DockerControlBridge).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(method => method.Name is "DescribeAsync" or "SnapshotAsync" or "LogsAsync" or "ReviewAsync" or "CommitAsync" or "DiscardAsync")
            .ToArray();
        Require(rendererMethods.Length == 6, "The bridge does not expose exactly the six reviewed renderer operations.");
        Require(rendererMethods.SelectMany(method => method.GetParameters()).All(parameter =>
            parameter.Name is not ("command" or "executable" or "arguments" or "environment" or "path" or "composeFile")),
            "A renderer operation exposed a command, executable, argument, environment, path, or Compose-file parameter.");

        await bridge.DescribeAsync(1, "docker:describe").ConfigureAwait(false);
        var description = frames.Required("dockerControl.describe.result", "docker:describe").GetProperty("value");
        Require(description.GetProperty("availability").GetProperty("state").GetString() == "available", "Available fake runtime was not described accurately.");
        Require(!description.GetProperty("operations").GetProperty("update").GetBoolean(), "The unintegrated updater was advertised as available.");
        Require(description.GetProperty("updateReason").GetString() == "derived-runtime-updater-not-integrated", "The updater limitation was not explicit.");
        Require(!description.GetProperty("services").EnumerateArray().Any(item => item.GetString() == "model-runner"), "An unconfigured Model Runner was exposed.");

        await bridge.SnapshotAsync(1, "docker:snapshot").ConfigureAwait(false);
        var snapshotFrame = frames.Required("dockerControl.snapshot.result", "docker:snapshot");
        var snapshot = snapshotFrame.GetProperty("value");
        var revision = snapshot.GetProperty("revision").GetInt64();
        Require(revision == 1, "The initial Docker state revision was not monotonic from one.");
        var snapshotJson = snapshot.GetRawText();
        Require(!snapshotJson.Contains("workflow-secret", StringComparison.Ordinal), "A secret-like workflow message reached the renderer.");
        Require(!snapshotJson.Contains("C:\\", StringComparison.OrdinalIgnoreCase), "A host path reached the renderer snapshot.");
        Require(snapshot.GetProperty("services").GetArrayLength() == 2, "The exact Hermes and separately supervised Serena evidence was not projected.");

        await bridge.LogsAsync(1, "docker:logs", "hermes", 200).ConfigureAwait(false);
        var logs = frames.Required("dockerControl.logs.result", "docker:logs").GetProperty("value").GetRawText();
        foreach (var secret in new[] { "log-secret", "bearer-secret", "user:password", "github_pat_" })
            Require(!logs.Contains(secret, StringComparison.OrdinalIgnoreCase), $"Docker logs leaked {secret}.");
        Require(logs.Contains("REDACTED", StringComparison.Ordinal), "Docker logs did not visibly redact secret-like content.");

        var logCalls = runner.LogCalls;
        await bridge.LogsAsync(1, "docker:bad-service", "../../arbitrary", 200).ConfigureAwait(false);
        Require(frames.Required("dockerControl.error", "docker:bad-service").GetProperty("code").GetString() == "invalid_logs_request", "An arbitrary renderer service was not rejected.");
        Require(runner.LogCalls == logCalls, "An arbitrary renderer service reached the runner.");

        await bridge.ReviewAsync(1, "docker:update", revision, "request-update", null).ConfigureAwait(false);
        var update = frames.Required("dockerControl.review.result", "docker:update").GetProperty("value");
        Require(update.GetProperty("status").GetString() == "rejected" && runner.ExecuteCalls == 0, "The unintegrated updater did not fail closed.");

        await bridge.ReviewAsync(1, "docker:review-start", revision, "start-stack", null).ConfigureAwait(false);
        var startReview = frames.Required("dockerControl.review.result", "docker:review-start").GetProperty("value");
        Require(startReview.GetProperty("status").GetString() == "ready", "The typed start review was not prepared.");
        var affected = startReview.GetProperty("affectedServices").EnumerateArray().Select(item => item.GetString()).ToArray();
        Require(affected.SequenceEqual(new[] { "hermes" }), "The review did not display the exact manageable service set.");
        var startToken = startReview.GetProperty("reviewToken").GetString()!;
        await bridge.CommitAsync(1, "docker:commit-start", startToken).ConfigureAwait(false);
        Require(frames.Required("dockerControl.commit.result", "docker:commit-start").GetProperty("value").GetProperty("status").GetString() == "succeeded", "A valid reviewed operation did not commit.");
        Require(runner.ExecuteCalls == 1 && runner.LastMutation?.Targets.SequenceEqual([DockerControlService.Hermes]) == true, "The commit did not use the exact review-bound target.");
        await bridge.CommitAsync(1, "docker:replay", startToken).ConfigureAwait(false);
        Require(frames.Required("dockerControl.commit.result", "docker:replay").GetProperty("value").GetProperty("status").GetString() == "stale", "A consumed review token was replayable.");
        Require(runner.ExecuteCalls == 1, "A replay reached the runner.");

        await bridge.SnapshotAsync(1, "docker:snapshot-after-start").ConfigureAwait(false);
        var currentRevision = frames.Required("dockerControl.snapshot.result", "docker:snapshot-after-start").GetProperty("value").GetProperty("revision").GetInt64();
        await bridge.ReviewAsync(1, "docker:review-stale", currentRevision, "restart-service", "hermes").ConfigureAwait(false);
        var staleToken = frames.Required("dockerControl.review.result", "docker:review-stale").GetProperty("value").GetProperty("reviewToken").GetString()!;
        runner.HermesState = "stopped";
        await bridge.CommitAsync(1, "docker:commit-stale", staleToken).ConfigureAwait(false);
        Require(frames.Required("dockerControl.commit.result", "docker:commit-stale").GetProperty("value").GetProperty("status").GetString() == "stale", "A state-stale review committed.");
        Require(runner.ExecuteCalls == 1, "A stale review reached the runner.");

        await bridge.SnapshotAsync(1, "docker:snapshot-stopped").ConfigureAwait(false);
        currentRevision = frames.Required("dockerControl.snapshot.result", "docker:snapshot-stopped").GetProperty("value").GetProperty("revision").GetInt64();
        await bridge.ReviewAsync(1, "docker:review-discard", currentRevision, "restart-service", "hermes").ConfigureAwait(false);
        var discardToken = frames.Required("dockerControl.review.result", "docker:review-discard").GetProperty("value").GetProperty("reviewToken").GetString()!;
        await bridge.DiscardAsync(1, "docker:discard", discardToken).ConfigureAwait(false);
        Require(frames.Required("dockerControl.discard.result", "docker:discard").GetProperty("value").GetProperty("discarded").GetBoolean(), "The review token was not explicitly discarded.");
        await bridge.CommitAsync(1, "docker:commit-discarded", discardToken).ConfigureAwait(false);
        Require(frames.Required("dockerControl.commit.result", "docker:commit-discarded").GetProperty("value").GetProperty("status").GetString() == "stale", "A discarded token remained usable.");

        await bridge.ReviewAsync(1, "docker:review-expire", currentRevision, "restart-service", "hermes").ConfigureAwait(false);
        var expiryToken = frames.Required("dockerControl.review.result", "docker:review-expire").GetProperty("value").GetProperty("reviewToken").GetString()!;
        clock.Advance(TimeSpan.FromMinutes(1));
        await bridge.CommitAsync(1, "docker:commit-expired", expiryToken).ConfigureAwait(false);
        Require(frames.Required("dockerControl.commit.result", "docker:commit-expired").GetProperty("value").GetProperty("status").GetString() == "expired", "An expired review token remained usable.");

        await bridge.ReviewAsync(1, "docker:model", currentRevision, "restart-service", "model-runner").ConfigureAwait(false);
        Require(frames.Required("dockerControl.review.result", "docker:model").GetProperty("value").GetProperty("status").GetString() == "rejected", "An unconfigured Model Runner was accepted.");
        await bridge.CommitAsync(1, "docker:malformed-token", "not-valid").ConfigureAwait(false);
        Require(frames.Required("dockerControl.commit.result", "docker:malformed-token").GetProperty("value").GetProperty("status").GetString() == "stale", "A malformed review token was not rejected.");
        Require(runner.ExecuteCalls == 1, "A rejected operation reached the fake runner.");
    }

    private static async Task VerifyDuplicateAndInvalidRootAsync()
    {
        var frames = new LockedFrames();
        var runner = new FakeDockerControlRunner { CaptureBlocker = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using (var bridge = new DockerControlBridge(Path.GetTempPath(), frames.Add, runner))
        {
            var first = bridge.SnapshotAsync(1, "docker:duplicate");
            await runner.CaptureEntered.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            await bridge.SnapshotAsync(1, "docker:duplicate").ConfigureAwait(false);
            Require(frames.Required("dockerControl.error", "docker:duplicate").GetProperty("code").GetString() == "duplicate_request", "A duplicate request identifier was not rejected.");
            runner.CaptureBlocker.SetResult();
            await first.ConfigureAwait(false);
        }

        var invalidFrames = new LockedFrames();
        var missingRoot = Path.Combine(Path.GetTempPath(), $"missing-photon-stack-{Guid.NewGuid():N}");
        await using (var bridge = new DockerControlBridge(missingRoot, invalidFrames.Add))
        {
            await bridge.DescribeAsync(1, "docker:missing-root").ConfigureAwait(false);
            var availability = invalidFrames.Required("dockerControl.describe.result", "docker:missing-root").GetProperty("value").GetProperty("availability");
            Require(availability.GetProperty("state").GetString() == "unavailable", "An invalid trusted root did not fail closed without Docker execution.");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class LockedFrames
    {
        private readonly object _gate = new();
        private readonly List<JsonElement> _values = [];
        internal void Add(object value) { lock (_gate) _values.Add(JsonSerializer.SerializeToElement(value)); }
        internal JsonElement Required(string type, string requestId)
        {
            lock (_gate)
            {
                var value = _values.LastOrDefault(frame => String(frame, "type") == type && String(frame, "requestId") == requestId);
                return value.ValueKind == JsonValueKind.Object ? value : throw new InvalidOperationException($"Missing {type} for {requestId}.");
            }
        }
        private static string? String(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }

    private sealed class FakeDockerControlRunner : IDockerControlRunner
    {
        internal int LogCalls { get; private set; }
        internal int ExecuteCalls { get; private set; }
        internal DockerControlMutation? LastMutation { get; private set; }
        internal string HermesState { get; set; } = "running";
        internal TaskCompletionSource CaptureEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource? CaptureBlocker { get; set; }

        public async Task<DockerControlHostSnapshot> CaptureAsync(CancellationToken cancellationToken)
        {
            CaptureEntered.TrySetResult();
            if (CaptureBlocker is not null) await CaptureBlocker.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            var hash = $"sha256:{new string('a', 64)}";
            return new(
                new DateTimeOffset(2026, 8, 10, 18, 0, 0, TimeSpan.Zero),
                "running", "28.1.0", HermesState == "running" ? "running" : "stopped", hash, "abc123", "docker-control/v1",
                [
                    new(DockerControlService.Hermes, HermesState, HermesState == "running" ? "healthy" : "unknown", "1.0.0", new(hash, hash, "abc123", "verified"), [new("127.0.0.1", 9119, 9119, "tcp")], true),
                    new(DockerControlService.Serena, "unknown", "unknown", null, null, [], false),
                ],
                [new("data", "mounted", true), new("workspace", "mounted", true)],
                new("update", "succeeded", new DateTimeOffset(2026, 8, 10, 17, 0, 0, TimeSpan.Zero), "password=workflow-secret"));
        }

        public Task<DockerControlLogs> ReadLogsAsync(DockerControlService service, int maximumLines, CancellationToken cancellationToken)
        {
            LogCalls++;
            return Task.FromResult(new DockerControlLogs([
                new(null, "stdout", "password=log-secret"),
                new(null, "stderr", "Authorization: Bearer bearer-secret-value"),
                new(null, "system", "https://user:password@example.test/path"),
                new(null, "stdout", "github_pat_abcdefghijklmnopqrstuvwxyz012345"),
            ], false));
        }

        public Task<DockerControlMutationOutcome> ExecuteAsync(DockerControlMutation mutation, CancellationToken cancellationToken)
        {
            ExecuteCalls++;
            LastMutation = mutation;
            return Task.FromResult(new DockerControlMutationOutcome(true, "Reviewed fake operation completed."));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
