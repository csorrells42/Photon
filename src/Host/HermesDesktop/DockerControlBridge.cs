using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HermesDesktop;

internal sealed partial class DockerControlBridge : IAsyncDisposable
{
    internal const int ProtocolVersion = 1;
    private const int MaximumRequestIdCharacters = 128;
    private const int MaximumLogLines = 200;
    private const int MaximumLogLineCharacters = 512;
    private const int MaximumLogCharacters = 32 * 1024;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan MutationTimeout = TimeSpan.FromSeconds(35);
    private static readonly TimeSpan ReviewLifetime = TimeSpan.FromSeconds(45);

    private readonly Action<object> _post;
    private readonly IDockerControlRunner _runner;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string> _createToken;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReviewRecord> _reviews = new(StringComparer.Ordinal);
    private readonly object _reviewLock = new();
    private readonly SemaphoreSlim _snapshotGate = new(1, 1);
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private string? _lastStateFingerprint;
    private long _snapshotRevision;
    private bool _disposed;

    internal DockerControlBridge(
        string trustedStackRoot,
        Action<object> post,
        IDockerControlRunner? runner = null,
        TimeProvider? timeProvider = null,
        Func<string>? createToken = null)
    {
        _post = post ?? throw new ArgumentNullException(nameof(post));
        _runner = runner ?? new DockerControlProcessRunner(trustedStackRoot);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _createToken = createToken ?? CreateOpaqueToken;
    }

    internal Task DescribeAsync(int version, string? requestId) => RunAsync(version, requestId, ReadTimeout, async cancellationToken =>
    {
        var observed = await CaptureAsync(cancellationToken).ConfigureAwait(false);
        var available = observed.Value.EngineState == "running";
        var manageable = observed.Value.Services.Where(service => service.Manageable).Select(service => ServiceId(service.Id)).ToArray();
        _post(new
        {
            type = "dockerControl.describe.result",
            version = ProtocolVersion,
            requestId,
            value = new
            {
                protocolVersion = ProtocolVersion,
                availability = available
                    ? (object)new { state = "available" }
                    : new { state = "unavailable", reason = "engine-unavailable" },
                services = observed.Value.Services.Where(service => service.State != "unavailable").Select(service => ServiceId(service.Id)).Distinct(StringComparer.Ordinal).ToArray(),
                operations = new
                {
                    startStack = manageable.Length > 0,
                    stopStack = manageable.Length > 0,
                    restartService = manageable.Length > 0,
                    update = false,
                },
                updateReason = "derived-runtime-updater-not-integrated",
            },
        });
    });

    internal Task SnapshotAsync(int version, string? requestId) => RunAsync(version, requestId, ReadTimeout, async cancellationToken =>
    {
        var observed = await CaptureAsync(cancellationToken).ConfigureAwait(false);
        _post(new
        {
            type = "dockerControl.snapshot.result",
            version = ProtocolVersion,
            requestId,
            value = SnapshotValue(observed),
        });
    });

    internal Task LogsAsync(int version, string? requestId, string? service, int maximumLines) => RunAsync(version, requestId, ReadTimeout, async cancellationToken =>
    {
        if (!TryService(service, out var target) || maximumLines is < 1 or > MaximumLogLines)
            throw new DockerControlUnavailableException("invalid_logs_request", "The bounded Docker logs request is invalid.");
        var observed = await CaptureAsync(cancellationToken).ConfigureAwait(false);
        if (!observed.Value.Services.Any(candidate => candidate.Id == target && candidate.State != "unavailable"))
            throw new DockerControlUnavailableException("service_unavailable", "The requested product service is unavailable.");
        var raw = await _runner.ReadLogsAsync(target, maximumLines, cancellationToken).ConfigureAwait(false);
        var total = 0;
        var truncated = raw.Truncated || raw.Entries.Count > maximumLines;
        var entries = new List<object>();
        foreach (var entry in raw.Entries.Take(maximumLines))
        {
            var text = Scrub(entry.Text, MaximumLogLineCharacters);
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (total + text.Length > MaximumLogCharacters) { truncated = true; break; }
            total += text.Length;
            entries.Add(new
            {
                timestampUtc = entry.TimestampUtc?.ToUniversalTime().ToString("O"),
                stream = entry.Stream is "stdout" or "stderr" ? entry.Stream : "system",
                text,
            });
        }
        _post(new
        {
            type = "dockerControl.logs.result",
            version = ProtocolVersion,
            requestId,
            value = new { protocolVersion = ProtocolVersion, requestId, service = ServiceId(target), entries, truncated },
        });
    });

    internal Task ReviewAsync(int version, string? requestId, long snapshotRevision, string? kind, string? service) =>
        RunAsync(version, requestId, ReadTimeout, async cancellationToken =>
        {
            if (kind == "request-update")
            {
                PostReviewRejected(requestId!, "Updates remain unavailable until the immutable derived-runtime updater is integrated.");
                return;
            }
            var observed = await CaptureAsync(cancellationToken).ConfigureAwait(false);
            if (snapshotRevision != observed.Revision)
            {
                PostReviewRejected(requestId!, "The Docker snapshot changed. Refresh before preparing this operation.");
                return;
            }
            if (!TryMutation(kind, service, observed.Value, out var mutation, out var rejection))
            {
                PostReviewRejected(requestId!, rejection);
                return;
            }

            var token = _createToken();
            if (!ValidReviewToken(token)) throw new InvalidOperationException("The host review token generator returned an invalid token.");
            var expiresAt = _timeProvider.GetUtcNow().Add(ReviewLifetime);
            var fingerprint = MutationFingerprint(mutation, observed.Revision, observed.StateFingerprint);
            var record = new ReviewRecord(token, fingerprint, observed.Revision, observed.StateFingerprint, mutation, expiresAt);
            lock (_reviewLock)
            {
                RemoveExpiredReviews();
                _reviews.Clear();
                _reviews.Add(token, record);
            }
            _post(new
            {
                type = "dockerControl.review.result",
                version = ProtocolVersion,
                requestId,
                value = new
                {
                    protocolVersion = ProtocolVersion,
                    requestId,
                    status = "ready",
                    snapshotRevision = observed.Revision,
                    reviewToken = token,
                    fingerprint,
                    expiresAtUtc = expiresAt.ToString("O"),
                    affectedServices = mutation.Targets.Select(ServiceId).ToArray(),
                    summary = MutationSummary(mutation),
                    warnings = mutation.Kind == DockerControlMutationKind.StopStack
                        ? new[] { "Stopping services interrupts active Photon work." }
                        : Array.Empty<string>(),
                },
            });
        });

    internal Task CommitAsync(int version, string? requestId, string? reviewToken) => RunAsync(version, requestId, MutationTimeout, async cancellationToken =>
    {
        if (!ValidReviewToken(reviewToken))
        {
            PostCommitResult(requestId!, "stale", "The Docker review token is invalid.");
            return;
        }
        ReviewRecord? review;
        lock (_reviewLock)
        {
            _reviews.Remove(reviewToken!, out review);
        }
        if (review is null)
        {
            PostCommitResult(requestId!, "stale", "The Docker review is unknown, consumed, or superseded.");
            return;
        }
        if (review.ExpiresAtUtc <= _timeProvider.GetUtcNow())
        {
            PostCommitResult(requestId!, "expired", "The Docker review expired. Refresh and review again.");
            return;
        }

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var before = await CaptureAsync(cancellationToken).ConfigureAwait(false);
            if (before.Revision != review.SnapshotRevision || before.StateFingerprint != review.StateFingerprint)
            {
                PostCommitResult(requestId!, "stale", "Docker state changed after review. Refresh and review again.");
                return;
            }
            DockerControlMutationOutcome outcome;
            try { outcome = await _runner.ExecuteAsync(review.Mutation, cancellationToken).ConfigureAwait(false); }
            catch (DockerControlUnavailableException exception)
            {
                PostCommitResult(requestId!, "failed", SafeMessage(exception.Message, "The reviewed Docker operation is unavailable."));
                return;
            }
            var after = await CaptureAsync(cancellationToken).ConfigureAwait(false);
            lock (_reviewLock) _reviews.Clear();
            _post(new
            {
                type = "dockerControl.commit.result",
                version = ProtocolVersion,
                requestId,
                value = new
                {
                    protocolVersion = ProtocolVersion,
                    requestId,
                    status = outcome.Succeeded ? "succeeded" : "failed",
                    message = SafeMessage(outcome.Message, outcome.Succeeded ? "The reviewed Docker operation completed." : "The reviewed Docker operation failed."),
                    snapshot = SnapshotValue(after),
                },
            });
        }
        finally { _mutationGate.Release(); }
    });

    internal Task DiscardAsync(int version, string? requestId, string? reviewToken) => RunAsync(version, requestId, ReadTimeout, _ =>
    {
        var discarded = false;
        if (ValidReviewToken(reviewToken))
        {
            lock (_reviewLock) discarded = _reviews.Remove(reviewToken!);
        }
        _post(new
        {
            type = "dockerControl.discard.result",
            version = ProtocolVersion,
            requestId,
            value = new { discarded },
        });
        return Task.CompletedTask;
    });

    private async Task<ObservedSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        await _snapshotGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var raw = await _runner.CaptureAsync(cancellationToken).ConfigureAwait(false);
            var value = NormalizeSnapshot(raw);
            var stateFingerprint = StateFingerprint(value);
            if (!string.Equals(stateFingerprint, _lastStateFingerprint, StringComparison.Ordinal))
            {
                _snapshotRevision = _snapshotRevision == long.MaxValue ? 1 : _snapshotRevision + 1;
                _lastStateFingerprint = stateFingerprint;
                lock (_reviewLock) _reviews.Clear();
            }
            return new(value, _snapshotRevision, stateFingerprint);
        }
        finally { _snapshotGate.Release(); }
    }

    private async Task RunAsync(int version, string? requestId, TimeSpan timeout, Func<CancellationToken, Task> operation)
    {
        if (_disposed) return;
        if (!ValidateEnvelope(version, requestId)) return;
        using var cancellation = new CancellationTokenSource(timeout);
        if (!_active.TryAdd(requestId!, cancellation))
        {
            PostError(requestId!, "duplicate_request", "A Docker Control Center request with this identifier is already pending.");
            return;
        }
        try { await operation(cancellation.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            PostError(requestId!, "cancelled", "The bounded Docker operation was cancelled or timed out.", true);
        }
        catch (DockerControlUnavailableException exception)
        {
            PostError(requestId!, SafeCode(exception.Code), SafeMessage(exception.Message, "Docker Control Center is unavailable."), true);
        }
        catch (Exception exception)
        {
            DesktopLog.Write($"Docker Control Center failed safely: {exception.GetType().Name}");
            PostError(requestId!, "unexpected", "The native Docker Control Center encountered an unexpected error.", true);
        }
        finally { _active.TryRemove(requestId!, out _); }
    }

    private bool ValidateEnvelope(int version, string? requestId)
    {
        if (version == ProtocolVersion && ValidRequestId(requestId)) return true;
        if (ValidRequestId(requestId)) PostError(requestId!, "invalid_envelope", "The Docker Control Center request envelope is invalid.");
        return false;
    }

    private static bool TryMutation(
        string? kind,
        string? service,
        DockerControlHostSnapshot snapshot,
        out DockerControlMutation mutation,
        out string rejection)
    {
        mutation = null!;
        rejection = "The requested Docker operation is invalid.";
        var manageable = snapshot.Services.Where(candidate => candidate.Manageable).Select(candidate => candidate.Id).Distinct().OrderBy(value => value).ToArray();
        if (kind is "start-stack" or "stop-stack")
        {
            if (service is not null || manageable.Length == 0) { rejection = "No approved Docker services are currently manageable."; return false; }
            mutation = new(kind == "start-stack" ? DockerControlMutationKind.StartStack : DockerControlMutationKind.StopStack, null, manageable);
            return true;
        }
        if (kind != "restart-service" || !TryService(service, out var target)
            || !snapshot.Services.Any(candidate => candidate.Id == target && candidate.Manageable))
        {
            rejection = "The requested product service is not configured for Docker restart.";
            return false;
        }
        mutation = new(DockerControlMutationKind.RestartService, target, [target]);
        return true;
    }

    private static DockerControlHostSnapshot NormalizeSnapshot(DockerControlHostSnapshot raw)
    {
        var services = raw.Services
            .Where(service => Enum.IsDefined(service.Id))
            .GroupBy(service => service.Id)
            .Select(group => group.First())
            .Take(3)
            .Select(service => new DockerControlServiceEvidence(
                service.Id,
                SafeObservedState(service.State),
                SafeHealth(service.Health),
                SafeIdentity(service.Version),
                service.Image is null ? null : new(
                    SafeSha256(service.Image.ImageId),
                    SafeSha256(service.Image.ApprovedDigest),
                    SafeIdentity(service.Image.OciRevision),
                    service.Image.Verification is "verified" or "mismatch" or "unverified" or "unavailable" ? service.Image.Verification : "unverified"),
                service.Ports.Where(port => port.Address is "127.0.0.1" or "::1" && port.HostPort is >= 1 and <= 65535 && port.ContainerPort is >= 1 and <= 65535 && port.Protocol is "tcp" or "udp").Take(16).ToArray(),
                service.Manageable && service.Id != DockerControlService.Serena))
            .OrderBy(service => service.Id)
            .ToArray();
        var volumes = raw.Volumes.Where(volume => volume.Role is "data" or "workspace")
            .GroupBy(volume => volume.Role, StringComparer.Ordinal).Select(group => group.First()).Take(2)
            .Select(volume => new DockerControlVolumeEvidence(volume.Role, volume.State is "mounted" or "unmounted" or "unavailable" ? volume.State : "unknown", volume.Persistent)).ToArray();
        var workflow = raw.LastWorkflow is null ? null : new DockerControlWorkflowEvidence(
            raw.LastWorkflow.Kind is "update" or "rollback" ? raw.LastWorkflow.Kind : "update",
            raw.LastWorkflow.State is "succeeded" or "failed" or "cancelled" ? raw.LastWorkflow.State : "unknown",
            raw.LastWorkflow.CompletedAtUtc,
            SafeMessage(raw.LastWorkflow.Summary, "Docker workflow result unavailable."));
        return new(
            raw.ObservedAtUtc,
            SafeObservedState(raw.EngineState),
            SafeIdentity(raw.EngineVersion),
            SafeObservedState(raw.ComposeState),
            SafeSha256(raw.DefinitionFingerprint),
            SafeIdentity(raw.UpstreamRevision),
            SafeIdentity(raw.RuntimeProtocol) ?? "docker-control/v1",
            services,
            volumes,
            workflow);
    }

    private static object SnapshotValue(ObservedSnapshot observed) => new
    {
        protocolVersion = ProtocolVersion,
        revision = observed.Revision,
        observedAtUtc = observed.Value.ObservedAtUtc.ToUniversalTime().ToString("O"),
        engine = new { state = observed.Value.EngineState, version = observed.Value.EngineVersion },
        compose = new
        {
            state = observed.Value.ComposeState,
            definitionFingerprint = observed.Value.DefinitionFingerprint,
            upstreamRevision = observed.Value.UpstreamRevision,
            runtimeProtocol = observed.Value.RuntimeProtocol,
        },
        services = observed.Value.Services.Select(service => new
        {
            id = ServiceId(service.Id),
            state = service.State,
            health = service.Health,
            version = service.Version,
            image = service.Image is null ? null : new
            {
                imageId = service.Image.ImageId,
                approvedDigest = service.Image.ApprovedDigest,
                ociRevision = service.Image.OciRevision,
                verification = service.Image.Verification,
            },
            ports = service.Ports.Select(port => new { address = port.Address, hostPort = port.HostPort, containerPort = port.ContainerPort, protocol = port.Protocol }).ToArray(),
        }).ToArray(),
        volumes = observed.Value.Volumes.Select(volume => new { role = volume.Role, state = volume.State, persistent = volume.Persistent }).ToArray(),
        lastWorkflow = observed.Value.LastWorkflow is null ? null : new
        {
            kind = observed.Value.LastWorkflow.Kind,
            state = observed.Value.LastWorkflow.State,
            completedAtUtc = observed.Value.LastWorkflow.CompletedAtUtc.ToUniversalTime().ToString("O"),
            summary = observed.Value.LastWorkflow.Summary,
        },
    };

    private static string StateFingerprint(DockerControlHostSnapshot snapshot)
    {
        var canonical = new StringBuilder()
            .Append(snapshot.EngineState).Append('\n').Append(snapshot.EngineVersion).Append('\n')
            .Append(snapshot.ComposeState).Append('\n').Append(snapshot.DefinitionFingerprint).Append('\n')
            .Append(snapshot.UpstreamRevision).Append('\n').Append(snapshot.RuntimeProtocol).Append('\n');
        foreach (var service in snapshot.Services.OrderBy(service => service.Id))
        {
            canonical.Append(ServiceId(service.Id)).Append('|').Append(service.State).Append('|').Append(service.Health).Append('|')
                .Append(service.Version).Append('|').Append(service.Image?.ImageId).Append('|').Append(service.Image?.ApprovedDigest)
                .Append('|').Append(service.Image?.OciRevision).Append('|').Append(service.Image?.Verification).Append('|').Append(service.Manageable).Append('\n');
            foreach (var port in service.Ports.OrderBy(port => port.Address, StringComparer.Ordinal).ThenBy(port => port.HostPort))
                canonical.Append(port.Address).Append(':').Append(port.HostPort).Append('>').Append(port.ContainerPort).Append('/').Append(port.Protocol).Append('\n');
        }
        foreach (var volume in snapshot.Volumes.OrderBy(volume => volume.Role, StringComparer.Ordinal))
            canonical.Append(volume.Role).Append('|').Append(volume.State).Append('|').Append(volume.Persistent).Append('\n');
        return Sha256(canonical.ToString());
    }

    private static string MutationFingerprint(DockerControlMutation mutation, long revision, string stateFingerprint)
    {
        var canonical = $"docker-control/v1\n{mutation.Kind}\n{ServiceIdOrDash(mutation.Service)}\n{revision}\n{string.Join(',', mutation.Targets.OrderBy(value => value).Select(ServiceId))}\n{stateFingerprint}";
        return Sha256(canonical);
    }

    private static string MutationSummary(DockerControlMutation mutation) => mutation.Kind switch
    {
        DockerControlMutationKind.StartStack => $"Start {string.Join(" and ", mutation.Targets.Select(ServiceId))}.",
        DockerControlMutationKind.StopStack => $"Stop {string.Join(" and ", mutation.Targets.Select(ServiceId))}.",
        _ => $"Restart {ServiceIdOrDash(mutation.Service)}.",
    };

    private void PostReviewRejected(string requestId, string message) => _post(new
    {
        type = "dockerControl.review.result",
        version = ProtocolVersion,
        requestId,
        value = new { protocolVersion = ProtocolVersion, requestId, status = "rejected", message = SafeMessage(message, "Docker rejected this request.") },
    });

    private void PostCommitResult(string requestId, string status, string message) => _post(new
    {
        type = "dockerControl.commit.result",
        version = ProtocolVersion,
        requestId,
        value = new { protocolVersion = ProtocolVersion, requestId, status, message = SafeMessage(message, "Docker operation rejected.") },
    });

    private void PostError(string requestId, string code, string message, bool retryable = false) => _post(new
    {
        type = "dockerControl.error",
        version = ProtocolVersion,
        requestId,
        code = SafeCode(code),
        message = SafeMessage(message, "Docker Control Center failed safely."),
        retryable,
    });

    private void RemoveExpiredReviews()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var token in _reviews.Where(pair => pair.Value.ExpiresAtUtc <= now).Select(pair => pair.Key).ToArray()) _reviews.Remove(token);
    }

    private static string Scrub(string? value, int maximum)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var result = ControlsRegex().Replace(value, " ");
        result = UriUserInfoRegex().Replace(result, "$1[REDACTED]@");
        result = AuthorizationRegex().Replace(result, "$1 [REDACTED]");
        result = KeyValueRegex().Replace(result, "$1=[REDACTED]");
        result = JwtRegex().Replace(result, "[REDACTED]");
        result = TokenRegex().Replace(result, "[REDACTED]");
        return result.Length <= maximum ? result : result[..maximum];
    }

    private static string SafeMessage(string? value, string fallback) => string.IsNullOrWhiteSpace(Scrub(value, 256)) ? fallback : Scrub(value, 256);
    private static string SafeCode(string? value) => SafeIdentity(value) ?? "unexpected";
    private static string? SafeIdentity(string? value) => value is not null && value.Length is > 0 and <= 128 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '+' or ':' or '/' or '-') ? value : null;
    private static string? SafeSha256(string? value) => value is not null && Sha256Regex().IsMatch(value) ? value.ToLowerInvariant() : null;
    private static string SafeObservedState(string? value) => value is "running" or "stopped" or "degraded" or "unavailable" ? value : "unknown";
    private static string SafeHealth(string? value) => value is "healthy" or "unhealthy" or "starting" or "not-configured" ? value : "unknown";
    private static bool ValidRequestId(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= MaximumRequestIdCharacters && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or ':' or '-' or '.');
    private static bool ValidReviewToken(string? value) => value is not null && value.Length is >= 32 and <= 512 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
    private static bool TryService(string? value, out DockerControlService service)
    {
        service = value switch { "hermes" => DockerControlService.Hermes, "serena" => DockerControlService.Serena, "model-runner" => DockerControlService.ModelRunner, _ => (DockerControlService)(-1) };
        return Enum.IsDefined(service);
    }
    private static string ServiceId(DockerControlService service) => service switch { DockerControlService.Hermes => "hermes", DockerControlService.Serena => "serena", DockerControlService.ModelRunner => "model-runner", _ => throw new ArgumentOutOfRangeException(nameof(service)) };
    private static string ServiceIdOrDash(DockerControlService? service) => service is null ? "-" : ServiceId(service.Value);
    private static string Sha256(string value) => $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))}";
    private static string CreateOpaqueToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var cancellation in _active.Values) cancellation.Cancel();
        lock (_reviewLock) _reviews.Clear();
        await _runner.DisposeAsync().ConfigureAwait(false);
    }

    private sealed record ObservedSnapshot(DockerControlHostSnapshot Value, long Revision, string StateFingerprint);
    private sealed record ReviewRecord(string Token, string Fingerprint, long SnapshotRevision, string StateFingerprint, DockerControlMutation Mutation, DateTimeOffset ExpiresAtUtc);

    [GeneratedRegex("[\\u0000-\\u001f\\u007f-\\u009f\\u200b-\\u200f\\u202a-\\u202e\\u2060-\\u2069\\ufeff]", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex ControlsRegex();
    [GeneratedRegex("\\b(https?://)[^\\s/@:]+:[^\\s/@]+@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex UriUserInfoRegex();
    [GeneratedRegex("\\b(Bearer|Basic)\\s+[A-Za-z0-9+/._=-]{8,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex AuthorizationRegex();
    [GeneratedRegex("\\b(api[-_ ]?key|access[-_ ]?token|refresh[-_ ]?token|authorization|password|passwd|secret|cookie|set-cookie)\\s*[:=]\\s*(?:\"[^\"]*\"|'[^']*'|[^\\s,;]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex KeyValueRegex();
    [GeneratedRegex("\\beyJ[A-Za-z0-9_-]{8,}\\.[A-Za-z0-9_-]{8,}\\.[A-Za-z0-9_-]{8,}\\b", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex JwtRegex();
    [GeneratedRegex("\\b(?:gh[opusr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[0-9A-Z]{16})\\b", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex TokenRegex();
    [GeneratedRegex("^sha256:[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Sha256Regex();
}
