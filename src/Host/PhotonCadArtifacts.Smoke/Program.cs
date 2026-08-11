using System.Text.Json;
using PhotonCadArtifacts;
using PhotonCadArtifacts.Smoke;
using PhotonCadRuntime;

var tests = new (string Name, Func<ValueTask> Run)[]
{
    ("contracts and opaque handles", ContractsAndOpaqueHandlesAsync),
    ("stable runtime lease adapter boundary", RuntimeAdapterAsync),
    ("sealed source digest length and STEP envelope", SealAndVerifyAsync),
    ("complete STEP Part-21 lexical envelope", StepPart21LexicalEnvelopeAsync),
    ("malformed sources and quota fail closed", RejectMalformedSourcesAsync),
    ("fixed-origin single-use resource", ResourcePolicyAsync),
    ("resource responder pathless failure containment", ResourceResponderContainmentAsync),
    ("reviewed no-overwrite single-use commit", ReviewedCommitAsync),
    ("commit failure cleanup and context ownership", CommitFailureCleanupAsync),
    ("fingerprint context and expiry invalidation", InvalidationAsync),
    ("monotonic deadlines survive UTC rollback", MonotonicDeadlineAsync),
    ("destination parent identity", DestinationIdentityAsync),
    ("bounded cleanup quarantine", CleanupAsync),
    ("protocol frames and future-format honesty", ProtocolAsync),
    ("identity-bound custody recovery and hostile journals", ArtifactSecuritySmoke.CustodyRecoveryAsync),
    ("bounded protocol saturation cancellation replay and shutdown", ArtifactSecuritySmoke.ProtocolBoundsAsync),
    ("DPAPI destination crash recovery tamper and TTL", ArtifactSecuritySmoke.DestinationJournalAsync),
};

var started = DateTimeOffset.UtcNow;
foreach (var test in tests)
{
    await test.Run().ConfigureAwait(false);
    Console.WriteLine($"PASS {test.Name}");
}
Console.WriteLine($"PhotonCadArtifacts smoke passed {tests.Length}/{tests.Length} in {(DateTimeOffset.UtcNow - started).TotalMilliseconds:F0} ms.");

static ValueTask ContractsAndOpaqueHandlesAsync()
{
    var handles = Enumerable.Range(0, 256).Select(_ => PhotonCadArtifactHandle.New().Value).ToArray();
    Check.Equal(handles.Length, handles.Distinct(StringComparer.Ordinal).Count(), "artifact handles are unique");
    Check.True(handles.All(value => value.StartsWith("cad-artifact:", StringComparison.Ordinal) &&
        value.Length == "cad-artifact:".Length + 43 && !value.Contains('/') && !value.Contains('\\')),
        "artifact handles are 256-bit base64url capabilities");
    Check.Code("invalid_value", () => _ = new PhotonCadArtifactHandle("cad-artifact:C:\\secret.step"),
        "paths cannot be handles");
    Check.Code("artifact_format_unavailable", () => _ = new PhotonCadArtifactSourceDescriptor(
        "source-1", "glb", "model/gltf-binary", "unspecified", "sha256:" + new string('0', 64), 100, "preview.glb"),
        "future GLB is not advertised");
    Check.Code("artifact_format_unavailable", () => _ = new PhotonCadArtifactSourceDescriptor(
        "source-1", "bom-pdf", "application/pdf", "unspecified", "sha256:" + new string('0', 64), 100, "bom.pdf"),
        "future PDF is not advertised");
    Check.Code("invalid_display_name", () => _ = new PhotonCadArtifactSourceDescriptor(
        "source-1", PhotonCadArtifactContract.StepKind, PhotonCadArtifactContract.StepMediaType,
        PhotonCadArtifactContract.UnspecifiedProfile, "sha256:" + new string('0', 64), 100, "C:\\secret.step"),
        "source paths are rejected as display names");
    return ValueTask.CompletedTask;
}

static async ValueTask RuntimeAdapterAsync()
{
    Check.True(!typeof(IPhotonCadArtifactSource).IsPublic, "source adapter SPI remains internal");
    var runtimeSession = CadSessionHandle.New();
    var runtimeArtifact = CadArtifactHandle.New();
    var resolver = new FixedRuntimeBindingResolver(new PhotonCadRuntimeArtifactBinding(
        runtimeSession,
        runtimeArtifact,
        "runtime.step"));
    var adapter = new PhotonCadRuntimeArtifactSource(new UnavailableCadRuntimeBroker(), resolver);
    var context = new PhotonCadArtifactContext("renderer-1", "controller-1", "session-1", "project-1", 1);
    var runtimeBytes = SmokeData.OpenCascadeStep();
    var exactAdapter = new PhotonCadRuntimeArtifactSource(
        new ExactLeaseCadRuntimeBroker(runtimeSession, runtimeArtifact, runtimeBytes),
        resolver);
    await using (var lease = await exactAdapter.AcquireAsync(
        new PhotonCadArtifactSourceRequest(runtimeArtifact.Value, context)).ConfigureAwait(false))
    {
        Check.Equal(SmokeData.Digest(runtimeBytes), lease.Descriptor.ContentDigest,
            "production adapter canonicalizes the exact runtime receipt digest");
        using var copy = new MemoryStream();
        await lease.Content.CopyToAsync(copy).ConfigureAwait(false);
        Check.True(runtimeBytes.SequenceEqual(copy.ToArray()),
            "production adapter preserves the exact locked runtime lease bytes");
    }
    await Check.CodeAsync("runtime_artifact_unavailable", async () =>
    {
        await using var ignored = await adapter.AcquireAsync(
            new PhotonCadArtifactSourceRequest(runtimeArtifact.Value, context)).ConfigureAwait(false);
    }, "runtime broker failures remain pathless and fail closed").ConfigureAwait(false);
    Check.Code("invalid_runtime_artifact_bound", () => _ = new PhotonCadRuntimeArtifactSource(
        new UnavailableCadRuntimeBroker(),
        resolver,
        CadContractLimits.MaximumBrokeredArtifactBytes + 1), "runtime adapter enforces broker bound");
}

static async ValueTask SealAndVerifyAsync()
{
    await using var fixture = new ArtifactFixture();
    var step = SmokeData.Step();
    var artifact = await fixture.SealAsync("source-1", step).ConfigureAwait(false);
    Check.Equal(PhotonCadArtifactContract.StepKind, artifact.Kind, "generic STEP kind");
    Check.Equal(PhotonCadArtifactContract.StepMediaType, artifact.MediaType, "generic STEP media type");
    Check.Equal(PhotonCadArtifactContract.UnspecifiedProfile, artifact.Profile, "STEP profile remains unspecified");
    Check.Equal(SmokeData.Digest(step), artifact.ContentDigest, "sealed digest");
    Check.Equal((long)step.Length, artifact.ByteLength, "sealed byte length");
    await using var lease = await fixture.Storage.OpenVerifiedAsync(artifact.ArtifactHandle, fixture.Context).ConfigureAwait(false);
    using var copied = new MemoryStream();
    await lease.Content.CopyToAsync(copied).ConfigureAwait(false);
    Check.True(step.SequenceEqual(copied.ToArray()), "sealed bytes are exact");
    var serialized = JsonSerializer.Serialize(artifact);
    Check.True(!serialized.Contains(fixture.Directory.Path, StringComparison.OrdinalIgnoreCase), "descriptor contains no host path");
    Check.Equal(1, Directory.GetFiles(fixture.Directory.Child("broker\\sealed"), "*.step").Length, "one sealed file");
}

static async ValueTask StepPart21LexicalEnvelopeAsync()
{
    var realistic = SmokeData.Step("REALISTIC");
    await ValidateAsync(realistic).ConfigureAwait(false);
    await ValidateAsync(SmokeData.OpenCascadeStep()).ConfigureAwait(false);
    await ValidateAsync(System.Text.Encoding.ASCII.GetBytes(
        System.Text.Encoding.ASCII.GetString(realistic) + "\r\n\t ")).ConfigureAwait(false);

    var earlyNul = realistic.ToArray();
    earlyNul[20] = 0;
    await RejectAsync("step_non_ascii_content", earlyNul, "early NUL").ConfigureAwait(false);
    var binaryMiddle = realistic.ToArray();
    binaryMiddle[binaryMiddle.Length / 2] = 0x80;
    await RejectAsync("step_non_ascii_content", binaryMiddle, "binary middle").ConfigureAwait(false);

    await RejectTextAsync("step_header_required", """
        ISO-10303-21;
        DATA;
        ENDSEC;
        END-ISO-10303-21;
        """, "HEADER required").ConfigureAwait(false);
    await RejectTextAsync("step_data_required", """
        ISO-10303-21;
        HEADER;
        ENDSEC;
        END-ISO-10303-21;
        """, "DATA required").ConfigureAwait(false);
    await RejectTextAsync("step_section_order_invalid", """
        ISO-10303-21;
        HEADER;
        DATA;
        ENDSEC;
        END-ISO-10303-21;
        """, "section order").ConfigureAwait(false);
    await RejectTextAsync("step_section_order_invalid", """
        ISO-10303-21;
        HEADER;
        ENDSEC;
        DATA;
        END-ISO-10303-21;
        ENDSEC;
        END-ISO-10303-21;
        """, "embedded terminal").ConfigureAwait(false);

    foreach (var suffix in new[] { "PK\u0003\u0004", "MZ", "<script>alert(1)</script>", "END-ISO-10303-21;" })
    {
        await RejectAsync(
            "step_appended_content",
            realistic.Concat(System.Text.Encoding.ASCII.GetBytes(suffix)).ToArray(),
            "appended polyglot").ConfigureAwait(false);
    }

    await RejectTextAsync(
        "step_token_too_long",
        "ISO-10303-21;\nHEADER;\n" + new string('A', 4_097) + ";\nENDSEC;\nDATA;\nENDSEC;\nEND-ISO-10303-21;",
        "overlong token").ConfigureAwait(false);
    var control = realistic.ToArray();
    control[30] = 0x1b;
    await RejectAsync("step_non_ascii_content", control, "control byte").ConfigureAwait(false);

    static async ValueTask ValidateAsync(byte[] bytes)
    {
        await using var stream = new MemoryStream(bytes, writable: false);
        await PhotonCadStepPart21Validator.ValidateAsync(stream, bytes.LongLength).ConfigureAwait(false);
        Check.Equal(0L, stream.Position, "validated seekable STEP is rewound");
    }

    static ValueTask RejectTextAsync(string code, string text, string message) =>
        RejectAsync(code, System.Text.Encoding.ASCII.GetBytes(text), message);

    static ValueTask RejectAsync(string code, byte[] bytes, string message) => Check.CodeAsync(code, async () =>
    {
        await using var stream = new MemoryStream(bytes, writable: false);
        await PhotonCadStepPart21Validator.ValidateAsync(stream, bytes.LongLength).ConfigureAwait(false);
    }, message);
}

static async ValueTask RejectMalformedSourcesAsync()
{
    await using var fixture = new ArtifactFixture(maximumArtifacts: 1);
    var step = SmokeData.Step();
    fixture.Source.Register("bad-digest", step, digest: "sha256:" + new string('0', 64));
    await Check.CodeAsync("source_digest_mismatch", async () =>
    {
        _ = await fixture.Storage.SealAsync(new PhotonCadArtifactSourceRequest("bad-digest", fixture.Context)).ConfigureAwait(false);
    }, "source digest mismatch").ConfigureAwait(false);

    fixture.Source.Register("bad-length", step, byteLength: step.Length - 1);
    await Check.CodeAsync("artifact_size_exceeded", async () =>
    {
        _ = await fixture.Storage.SealAsync(new PhotonCadArtifactSourceRequest("bad-length", fixture.Context)).ConfigureAwait(false);
    }, "source length mismatch").ConfigureAwait(false);

    var invalidStep = System.Text.Encoding.ASCII.GetBytes("not a STEP exchange file");
    fixture.Source.Register("bad-envelope", invalidStep);
    await Check.CodeAsync("invalid_step_initial_marker", async () =>
    {
        _ = await fixture.Storage.SealAsync(new PhotonCadArtifactSourceRequest("bad-envelope", fixture.Context)).ConfigureAwait(false);
    }, "STEP envelope").ConfigureAwait(false);

    fixture.Source.Register("unlocked", step, stableLockedRead: false);
    await Check.CodeAsync("stable_locked_source_required", async () =>
    {
        _ = await fixture.Storage.SealAsync(new PhotonCadArtifactSourceRequest("unlocked", fixture.Context)).ConfigureAwait(false);
    }, "locked source lease").ConfigureAwait(false);

    var first = await fixture.SealAsync("first", step).ConfigureAwait(false);
    fixture.Source.Register("second", SmokeData.Step("SECOND"));
    await Check.CodeAsync("artifact_quota_exceeded", async () =>
    {
        _ = await fixture.Storage.SealAsync(new PhotonCadArtifactSourceRequest("second", fixture.Context)).ConfigureAwait(false);
    }, "per-context artifact quota").ConfigureAwait(false);
    Check.Equal(1, Directory.GetFiles(fixture.Directory.Child("broker\\sealed"), "*.step").Length,
        "failed seals leave no partial files");

    fixture.ContextAuthority.CurrentContext = new PhotonCadArtifactContext(
        "renderer-1", "controller-1", "session-1", "project-1", 8);
    await Check.CodeAsync("artifact_context_changed", async () =>
    {
        _ = await fixture.Storage.DescribeAsync(first.ArtifactHandle, fixture.Context).ConfigureAwait(false);
    }, "stale context").ConfigureAwait(false);
}

static async ValueTask ResourcePolicyAsync()
{
    await using var fixture = new ArtifactFixture();
    var step = SmokeData.Step("RESOURCE");
    var artifact = await fixture.SealAsync("resource", step).ConfigureAwait(false);
    var target = Path.Combine(fixture.OutputDirectory, "resource.step");
    fixture.Picker.Enqueue(target);
    var destination = await fixture.Destinations.PickAsync(artifact).ConfigureAwait(false)
        ?? throw new InvalidOperationException("destination unexpectedly cancelled");
    var review = await fixture.Coordinator.ReviewAsync(new PhotonCadArtifactReviewRequest(
        "review-resource", fixture.Context, artifact.ArtifactHandle, artifact.Kind,
        artifact.ContentDigest, destination.DestinationHandle)).ConfigureAwait(false);
    Check.Equal("ready", review.Status, "resource review ready");
    var url = review.ResourceUrl ?? throw new InvalidOperationException("resource URL missing");
    Check.True(url.Origin() == fixture.ReleaseOptions.WorkbenchOrigin.GetLeftPart(UriPartial.Authority),
        "resource URL uses exact Workbench origin");

    var foreign = new UriBuilder(url) { Host = "localhost" }.Uri;
    Check.True(await fixture.Responder.TryHandleAsync(new PhotonCadArtifactResourceRequest(
        "GET", foreign, true, fixture.Context)).ConfigureAwait(false) is null, "foreign origin not handled");

    var malformedToken = new Uri(fixture.ReleaseOptions.WorkbenchOrigin,
        fixture.ReleaseOptions.ResourcePathPrefix + "short");
    await using (var malformed = await fixture.Responder.TryHandleAsync(new PhotonCadArtifactResourceRequest(
        "GET", malformedToken, true, fixture.Context)).ConfigureAwait(false))
    {
        Check.Equal(404, malformed?.StatusCode, "fixed namespace does not fall through on malformed tokens");
    }

    await using (var unauthorized = await fixture.Responder.TryHandleAsync(new PhotonCadArtifactResourceRequest(
        "GET", url, false, fixture.Context)).ConfigureAwait(false))
    {
        Check.Equal(404, unauthorized?.StatusCode, "unauthorized resource hidden");
    }
    await using (var wrongMethod = await fixture.Responder.TryHandleAsync(new PhotonCadArtifactResourceRequest(
        "POST", url, true, fixture.Context)).ConfigureAwait(false))
    {
        Check.Equal(404, wrongMethod?.StatusCode, "GET-only resource");
    }
    await using (var range = await fixture.Responder.TryHandleAsync(new PhotonCadArtifactResourceRequest(
        "GET", url, true, fixture.Context, new Dictionary<string, string> { ["Range"] = "bytes=0-5" })).ConfigureAwait(false))
    {
        Check.Equal(404, range?.StatusCode, "range requests rejected");
    }

    await using (var response = await fixture.Responder.TryHandleAsync(new PhotonCadArtifactResourceRequest(
        "GET", url, true, fixture.Context)).ConfigureAwait(false))
    {
        Check.Equal(200, response?.StatusCode, "same-origin resource response");
        Check.Equal(PhotonCadArtifactContract.StepMediaType, response!.Headers["Content-Type"], "resource media type");
        Check.Equal("no-store", response.Headers["Cache-Control"], "resource cache policy");
        using var bytes = new MemoryStream();
        await response.Content!.CopyToAsync(bytes).ConfigureAwait(false);
        Check.True(step.SequenceEqual(bytes.ToArray()), "resource bytes match sealed artifact");
    }
    await using (var replay = await fixture.Responder.TryHandleAsync(new PhotonCadArtifactResourceRequest(
        "GET", url, true, fixture.Context)).ConfigureAwait(false))
    {
        Check.Equal(404, replay?.StatusCode, "resource capability is single use");
    }
    Check.True(!JsonSerializer.Serialize(review).Contains(fixture.Directory.Path, StringComparison.OrdinalIgnoreCase),
        "review frame contains no host path");
}

static async ValueTask ResourceResponderContainmentAsync()
{
    var context = new PhotonCadArtifactContext("renderer-1", "controller-1", "session-1", "project-1", 7);
    var authority = new MutableContextAuthority(context);
    var options = new PhotonCadArtifactReleaseOptions(new Uri("http://127.0.0.1:9119/"));
    var store = new DelegateArtifactResourceStore();
    var responder = new PhotonCadArtifactResourceResponder(store, authority, options);
    var resource = PhotonCadArtifactResourceHandle.New();
    var url = new Uri(options.WorkbenchOrigin, options.ResourcePathPrefix + resource.Token);
    var request = new PhotonCadArtifactResourceRequest("GET", url, true, context);
    const string secretPath = @"C:\Users\owner\secret\part.step";
    var failures = new Exception[]
    {
        new PhotonCadArtifactException("resource_unavailable"),
        new IOException(secretPath),
        new UnauthorizedAccessException(secretPath),
        new ObjectDisposedException(secretPath),
        new System.Security.Cryptography.CryptographicException(secretPath),
        new InvalidDataException(secretPath),
        new FormatException(secretPath),
        new InvalidOperationException(secretPath),
        new NotSupportedException(secretPath),
        new ArgumentException(secretPath),
    };

    foreach (var failure in failures)
    {
        store.Handler = (_, _, _) => ValueTask.FromException<PhotonCadArtifactReadLease>(failure);
        await using var response = await responder.TryHandleAsync(request).ConfigureAwait(false)
            ?? throw new InvalidOperationException("fixed responder namespace fell through");
        Check.Equal(404, response.StatusCode, "storage failures are hidden");
        Check.Equal("resource_unavailable", response.Reason, "storage reason is fixed");
        Check.Equal("no-store", response.Headers["Cache-Control"], "failure response is no-store");
        var serialized = JsonSerializer.Serialize(new
        {
            response.StatusCode,
            response.Reason,
            response.Headers,
        });
        Check.True(!serialized.Contains(secretPath, StringComparison.OrdinalIgnoreCase) &&
            !serialized.Contains("secret", StringComparison.OrdinalIgnoreCase),
            "absolute-path exception text never reaches the response");
    }

    var step = SmokeData.Step("RESPONDER-DISPOSE");
    var artifact = new PhotonCadArtifactDescriptor(
        PhotonCadArtifactHandle.New(),
        context,
        PhotonCadArtifactContract.StepKind,
        PhotonCadArtifactContract.StepMediaType,
        PhotonCadArtifactContract.UnspecifiedProfile,
        SmokeData.Digest(step),
        step.LongLength,
        "responder.step",
        DateTimeOffset.UtcNow.AddMinutes(1));
    var throwingStream = new ThrowingReadableStream(
        new IOException(secretPath),
        new UnauthorizedAccessException(secretPath));
    store.Handler = (_, _, _) => ValueTask.FromResult(new PhotonCadArtifactReadLease(
        artifact,
        throwingStream,
        () => { }));
    await using (var response = await responder.TryHandleAsync(request).ConfigureAwait(false))
    {
        Check.Equal(404, response?.StatusCode, "post-acquisition failure is hidden");
    }
    Check.True(throwingStream.DisposeAttempted, "acquired lease is disposed on response construction failure");
}

static async ValueTask ReviewedCommitAsync()
{
    await using var fixture = new ArtifactFixture();
    var step = SmokeData.Step("COMMIT");
    var artifact = await fixture.SealAsync("commit", step).ConfigureAwait(false);
    var target = Path.Combine(fixture.OutputDirectory, "commit.step");
    fixture.Picker.Enqueue(target);
    var destination = await fixture.Destinations.PickAsync(artifact).ConfigureAwait(false)
        ?? throw new InvalidOperationException("destination unexpectedly cancelled");
    var review = await fixture.Coordinator.ReviewAsync(new PhotonCadArtifactReviewRequest(
        "review-commit", fixture.Context, artifact.ArtifactHandle, artifact.Kind,
        artifact.ContentDigest, destination.DestinationHandle)).ConfigureAwait(false);
    var commit = await fixture.Coordinator.CommitAsync(new PhotonCadArtifactCommitRequest(
        "commit-1", fixture.Context, review.ReviewHandle!, review.Fingerprint!)).ConfigureAwait(false);
    Check.Equal("committed", commit.Status, "reviewed commit");
    Check.True(File.Exists(target), "destination created");
    var committedBytes = await File.ReadAllBytesAsync(target).ConfigureAwait(false);
    Check.True(Enumerable.SequenceEqual(step, committedBytes), "destination bytes are exact");
    Check.Equal(artifact.ContentDigest, commit.Receipt?.ContentDigest, "commit digest receipt");
    Check.Equal(Path.GetFileName(target), commit.Receipt?.DestinationLabel, "receipt exposes label only");
    Check.True(!JsonSerializer.Serialize(commit).Contains(fixture.OutputDirectory, StringComparison.OrdinalIgnoreCase),
        "commit receipt contains no path");

    var replay = await fixture.Coordinator.CommitAsync(new PhotonCadArtifactCommitRequest(
        "commit-2", fixture.Context, review.ReviewHandle!, review.Fingerprint!)).ConfigureAwait(false);
    Check.Equal("rejected", replay.Status, "review is single use");
    Check.Equal("review_unavailable", replay.Reason, "single-use reason");

    var existing = Path.Combine(fixture.OutputDirectory, "existing.step");
    await File.WriteAllTextAsync(existing, "existing").ConfigureAwait(false);
    fixture.Picker.Enqueue(existing);
    await Check.CodeAsync("destination_exists", async () =>
    {
        _ = await fixture.Destinations.PickAsync(artifact).ConfigureAwait(false);
    }, "existing destinations are never overwritten").ConfigureAwait(false);
    Check.Equal("existing", await File.ReadAllTextAsync(existing).ConfigureAwait(false), "existing destination unchanged");

    fixture.Picker.Enqueue(null);
    Check.True(await fixture.Destinations.PickAsync(artifact).ConfigureAwait(false) is null,
        "picker cancellation is honest");
}

static async ValueTask CommitFailureCleanupAsync()
{
    await RunCancellationCaseAsync(sourceCancellation: true).ConfigureAwait(false);
    await RunCancellationCaseAsync(sourceCancellation: false).ConfigureAwait(false);

    var ioCase = await CreateCaseAsync().ConfigureAwait(false);
    await using (ioCase.Coordinator.ConfigureAwait(false))
    {
        ioCase.Destinations.CommitHandler = (_, _, _, _) =>
            ValueTask.FromException<PhotonCadDestinationCommitReceipt>(
                new IOException(@"C:\Users\owner\secret\destination.step"));
        var result = await ioCase.Coordinator.CommitAsync(new PhotonCadArtifactCommitRequest(
            "commit-io",
            ioCase.Context,
            ioCase.Review.ReviewHandle!,
            ioCase.Review.Fingerprint!)).ConfigureAwait(false);
        Check.Equal("rejected", result.Status, "raw destination IO is rejected");
        Check.Equal("artifact_commit_failed", result.Reason, "raw destination IO is pathless");
        Check.Equal(1, ioCase.Storage.ResourceRevocations, "IO failure revokes resource");
        Check.Equal(1, ioCase.Destinations.Revocations, "IO failure revokes destination");
    }

    var ownerCase = await CreateCaseAsync().ConfigureAwait(false);
    await using (ownerCase.Coordinator.ConfigureAwait(false))
    {
        ownerCase.Destinations.CommitHandler = SuccessfulCommit;
        var foreign = new PhotonCadArtifactContext(
            "renderer-2",
            ownerCase.Context.ControllerId,
            ownerCase.Context.CadSessionId,
            ownerCase.Context.ProjectId,
            ownerCase.Context.Revision);
        var denied = await ownerCase.Coordinator.CommitAsync(new PhotonCadArtifactCommitRequest(
            "commit-foreign",
            foreign,
            ownerCase.Review.ReviewHandle!,
            ownerCase.Review.Fingerprint!)).ConfigureAwait(false);
        Check.Equal("review_unavailable", denied.Reason, "foreign context cannot consume review");
        Check.Equal(0, ownerCase.Storage.ResourceRevocations, "foreign context cannot revoke resource");
        var accepted = await ownerCase.Coordinator.CommitAsync(new PhotonCadArtifactCommitRequest(
            "commit-owner",
            ownerCase.Context,
            ownerCase.Review.ReviewHandle!,
            ownerCase.Review.Fingerprint!)).ConfigureAwait(false);
        Check.Equal("committed", accepted.Status, "owner retains review after foreign attempt");
    }

    var successCase = await CreateCaseAsync().ConfigureAwait(false);
    await using (successCase.Coordinator.ConfigureAwait(false))
    {
        successCase.Storage.RevokeFailure = new OperationCanceledException("cleanup cancellation");
        successCase.Destinations.RevokeFailure = new IOException(@"C:\Users\owner\secret\cleanup.step");
        successCase.Destinations.CommitHandler = SuccessfulCommit;
        var success = await successCase.Coordinator.CommitAsync(new PhotonCadArtifactCommitRequest(
            "commit-success-cleanup",
            successCase.Context,
            successCase.Review.ReviewHandle!,
            successCase.Review.Fingerprint!)).ConfigureAwait(false);
        Check.Equal("committed", success.Status, "post-commit cleanup cannot turn success into failure");
        Check.True(success.Receipt is not null, "successful receipt survives cleanup failure");
    }

    var discardCase = await CreateCaseAsync().ConfigureAwait(false);
    await using (discardCase.Coordinator.ConfigureAwait(false))
    {
        var foreign = new PhotonCadArtifactContext(
            "renderer-2",
            discardCase.Context.ControllerId,
            discardCase.Context.CadSessionId,
            discardCase.Context.ProjectId,
            discardCase.Context.Revision);
        Check.True(!await discardCase.Coordinator.DiscardAsync(
            discardCase.Review.ReviewHandle!, foreign).ConfigureAwait(false),
            "foreign context cannot claim discard success");
        Check.Equal(0, discardCase.Storage.ResourceRevocations,
            "foreign discard cannot revoke resource");
        Check.True(await discardCase.Coordinator.DiscardAsync(
            discardCase.Review.ReviewHandle!, discardCase.Context).ConfigureAwait(false),
            "owner can discard review");
        Check.Equal(1, discardCase.Storage.ResourceRevocations,
            "owner discard revokes resource exactly once");
        Check.Equal(1, discardCase.Destinations.Revocations,
            "owner discard revokes destination exactly once");
    }

    static async ValueTask RunCancellationCaseAsync(bool sourceCancellation)
    {
        var item = await CreateCaseAsync().ConfigureAwait(false);
        await using (item.Coordinator.ConfigureAwait(false))
        {
            if (sourceCancellation)
            {
                item.Storage.OpenHandler = _ => ValueTask.FromException<PhotonCadArtifactReadLease>(
                    new OperationCanceledException("source rehash cancelled"));
            }
            else
            {
                item.Destinations.CommitHandler = (_, _, _, _) =>
                    ValueTask.FromException<PhotonCadDestinationCommitReceipt>(
                        new OperationCanceledException("destination copy cancelled"));
            }
            try
            {
                _ = await item.Coordinator.CommitAsync(new PhotonCadArtifactCommitRequest(
                    sourceCancellation ? "commit-source-cancel" : "commit-destination-cancel",
                    item.Context,
                    item.Review.ReviewHandle!,
                    item.Review.Fingerprint!)).ConfigureAwait(false);
                throw new InvalidOperationException("commit cancellation was not preserved");
            }
            catch (OperationCanceledException)
            {
            }
            Check.Equal(1, item.Storage.ResourceRevocations, "cancelled commit revokes resource");
            Check.Equal(1, item.Destinations.Revocations, "cancelled commit revokes destination");
            var replay = await item.Coordinator.CommitAsync(new PhotonCadArtifactCommitRequest(
                sourceCancellation ? "commit-source-replay" : "commit-destination-replay",
                item.Context,
                item.Review.ReviewHandle!,
                item.Review.Fingerprint!)).ConfigureAwait(false);
            Check.Equal("review_unavailable", replay.Reason, "cancelled review is consumed");
        }
    }

    static ValueTask<PhotonCadDestinationCommitReceipt> SuccessfulCommit(
        PhotonCadArtifactDescriptor artifact,
        PhotonCadArtifactContext context,
        Stream content,
        CancellationToken cancellationToken) => ValueTask.FromResult(new PhotonCadDestinationCommitReceipt(
            PhotonCadCommitReceiptHandle.New(),
            artifact.ArtifactHandle,
            artifact.ContentDigest,
            artifact.ByteLength,
            "release.step",
            DateTimeOffset.UtcNow));

    static async ValueTask<(
        PhotonCadArtifactReleaseCoordinator Coordinator,
        DelegateArtifactReleaseStorage Storage,
        DelegateDestinationAuthority Destinations,
        PhotonCadArtifactContext Context,
        PhotonCadArtifactReviewResult Review)> CreateCaseAsync()
    {
        var context = new PhotonCadArtifactContext("renderer-1", "controller-1", "session-1", "project-1", 7);
        var authority = new MutableContextAuthority(context);
        var step = SmokeData.Step("COMMIT-CLEANUP");
        var artifact = new PhotonCadArtifactDescriptor(
            PhotonCadArtifactHandle.New(),
            context,
            PhotonCadArtifactContract.StepKind,
            PhotonCadArtifactContract.StepMediaType,
            PhotonCadArtifactContract.UnspecifiedProfile,
            SmokeData.Digest(step),
            step.LongLength,
            "release.step",
            DateTimeOffset.UtcNow.AddMinutes(5));
        var resource = new PhotonCadArtifactResourceLease(
            PhotonCadArtifactResourceHandle.New(),
            artifact.ArtifactHandle,
            context,
            DateTimeOffset.UtcNow.AddMinutes(5));
        var storage = new DelegateArtifactReleaseStorage
        {
            Artifact = artifact,
            Resource = resource,
            OpenHandler = _ => ValueTask.FromResult(new PhotonCadArtifactReadLease(
                artifact,
                new MemoryStream(step, writable: false),
                () => { })),
        };
        var destination = new PhotonCadDestinationDescriptor(
            PhotonCadDestinationHandle.New(),
            artifact.ArtifactHandle,
            context,
            "release.step",
            DateTimeOffset.UtcNow.AddMinutes(5));
        var destinations = new DelegateDestinationAuthority
        {
            Destination = destination,
            CommitHandler = SuccessfulCommit,
        };
        var coordinator = new PhotonCadArtifactReleaseCoordinator(
            storage,
            destinations,
            authority,
            new PhotonCadArtifactReleaseOptions(new Uri("http://127.0.0.1:9119/")));
        var review = await coordinator.ReviewAsync(new PhotonCadArtifactReviewRequest(
            "review-cleanup",
            context,
            artifact.ArtifactHandle,
            artifact.Kind,
            artifact.ContentDigest,
            destination.DestinationHandle)).ConfigureAwait(false);
        Check.Equal("ready", review.Status, "cleanup case review ready");
        return (coordinator, storage, destinations, context, review);
    }
}

static async ValueTask InvalidationAsync()
{
    await using var fixture = new ArtifactFixture();
    var artifact = await fixture.SealAsync("invalidate", SmokeData.Step("INVALIDATE")).ConfigureAwait(false);
    var tamperTarget = Path.Combine(fixture.OutputDirectory, "tamper.step");
    fixture.Picker.Enqueue(tamperTarget);
    var destination = await fixture.Destinations.PickAsync(artifact).ConfigureAwait(false)
        ?? throw new InvalidOperationException("destination unexpectedly cancelled");
    var review = await fixture.Coordinator.ReviewAsync(new PhotonCadArtifactReviewRequest(
        "review-tamper", fixture.Context, artifact.ArtifactHandle, artifact.Kind,
        artifact.ContentDigest, destination.DestinationHandle)).ConfigureAwait(false);
    var fingerprint = review.Fingerprint!;
    var tampered = fingerprint[..^1] + (fingerprint[^1] == '0' ? '1' : '0');
    var rejected = await fixture.Coordinator.CommitAsync(new PhotonCadArtifactCommitRequest(
        "commit-tamper", fixture.Context, review.ReviewHandle!, tampered)).ConfigureAwait(false);
    Check.Equal("review_fingerprint_mismatch", rejected.Reason, "fingerprint is exact");
    Check.True(!File.Exists(tamperTarget), "tampered review writes nothing");
    var consumed = await fixture.Coordinator.CommitAsync(new PhotonCadArtifactCommitRequest(
        "commit-after-tamper", fixture.Context, review.ReviewHandle!, fingerprint)).ConfigureAwait(false);
    Check.Equal("review_unavailable", consumed.Reason, "failed commit consumes review");

    var staleTarget = Path.Combine(fixture.OutputDirectory, "stale.step");
    fixture.Picker.Enqueue(staleTarget);
    var staleDestination = await fixture.Destinations.PickAsync(artifact).ConfigureAwait(false)
        ?? throw new InvalidOperationException("destination unexpectedly cancelled");
    var staleReview = await fixture.Coordinator.ReviewAsync(new PhotonCadArtifactReviewRequest(
        "review-stale", fixture.Context, artifact.ArtifactHandle, artifact.Kind,
        artifact.ContentDigest, staleDestination.DestinationHandle)).ConfigureAwait(false);
    fixture.ContextAuthority.CurrentContext = new PhotonCadArtifactContext(
        "renderer-1", "controller-1", "session-1", "project-1", 8);
    var staleCommit = await fixture.Coordinator.CommitAsync(new PhotonCadArtifactCommitRequest(
        "commit-stale", fixture.Context, staleReview.ReviewHandle!, staleReview.Fingerprint!)).ConfigureAwait(false);
    Check.Equal("artifact_context_changed", staleCommit.Reason, "revision change invalidates review");
    Check.True(!File.Exists(staleTarget), "stale review writes nothing");

    fixture.ContextAuthority.CurrentContext = fixture.Context;
    var expiryTarget = Path.Combine(fixture.OutputDirectory, "expired.step");
    fixture.Picker.Enqueue(expiryTarget);
    var expiryDestination = await fixture.Destinations.PickAsync(artifact).ConfigureAwait(false)
        ?? throw new InvalidOperationException("destination unexpectedly cancelled");
    var expiryReview = await fixture.Coordinator.ReviewAsync(new PhotonCadArtifactReviewRequest(
        "review-expiry", fixture.Context, artifact.ArtifactHandle, artifact.Kind,
        artifact.ContentDigest, expiryDestination.DestinationHandle)).ConfigureAwait(false);
    fixture.Clock.Advance(TimeSpan.FromMinutes(2));
    var expired = await fixture.Coordinator.CommitAsync(new PhotonCadArtifactCommitRequest(
        "commit-expiry", fixture.Context, expiryReview.ReviewHandle!, expiryReview.Fingerprint!)).ConfigureAwait(false);
    Check.Equal("review_expired", expired.Reason, "expired review rejected");
    Check.True(!File.Exists(expiryTarget), "expired review writes nothing");
}

static async ValueTask MonotonicDeadlineAsync()
{
    await using var fixture = new ArtifactFixture();
    var artifact = await fixture.SealAsync("monotonic", SmokeData.Step("MONOTONIC")).ConfigureAwait(false);
    fixture.Picker.Enqueue(Path.Combine(fixture.OutputDirectory, "monotonic.step"));
    var destination = await fixture.Destinations.PickAsync(artifact).ConfigureAwait(false)
        ?? throw new InvalidOperationException("destination unexpectedly cancelled");
    var review = await fixture.Coordinator.ReviewAsync(new PhotonCadArtifactReviewRequest(
        "review-monotonic",
        fixture.Context,
        artifact.ArtifactHandle,
        artifact.Kind,
        artifact.ContentDigest,
        destination.DestinationHandle)).ConfigureAwait(false);
    Check.Equal("ready", review.Status, "monotonic review ready");

    fixture.Clock.RollbackUtc(TimeSpan.FromDays(30));
    fixture.Clock.Advance(TimeSpan.FromSeconds(61));
    var expired = await fixture.Coordinator.CommitAsync(new PhotonCadArtifactCommitRequest(
        "commit-monotonic-expired",
        fixture.Context,
        review.ReviewHandle!,
        review.Fingerprint!)).ConfigureAwait(false);
    Check.Equal("review_expired", expired.Reason, "UTC rollback cannot extend review TTL");
    Check.True(!File.Exists(Path.Combine(fixture.OutputDirectory, "monotonic.step")),
        "expired review writes nothing after UTC rollback");
}

static async ValueTask DestinationIdentityAsync()
{
    await using var fixture = new ArtifactFixture();
    var artifact = await fixture.SealAsync("identity", SmokeData.Step("IDENTITY")).ConfigureAwait(false);
    var parent = Path.Combine(fixture.OutputDirectory, "selected");
    var moved = Path.Combine(fixture.OutputDirectory, "selected-old");
    Directory.CreateDirectory(parent);
    var target = Path.Combine(parent, "identity.step");
    fixture.Picker.Enqueue(target);
    var destination = await fixture.Destinations.PickAsync(artifact).ConfigureAwait(false)
        ?? throw new InvalidOperationException("destination unexpectedly cancelled");
    Directory.Move(parent, moved);
    Directory.CreateDirectory(parent);
    await Check.CodeAsync("destination_identity_changed", async () =>
    {
        _ = await fixture.Destinations.ResolveAsync(destination.DestinationHandle, artifact, fixture.Context)
            .ConfigureAwait(false);
    }, "destination parent replacement").ConfigureAwait(false);
    Check.True(!File.Exists(target), "replaced parent receives no output");
}

static async ValueTask CleanupAsync()
{
    await using var fixture = new ArtifactFixture();
    var artifact = await fixture.SealAsync("cleanup", SmokeData.Step("CLEANUP")).ConfigureAwait(false);
    var target = Path.Combine(fixture.OutputDirectory, "cleanup.step");
    fixture.Picker.Enqueue(target);
    var destination = await fixture.Destinations.PickAsync(artifact).ConfigureAwait(false)
        ?? throw new InvalidOperationException("destination unexpectedly cancelled");
    var review = await fixture.Coordinator.ReviewAsync(new PhotonCadArtifactReviewRequest(
        "review-cleanup", fixture.Context, artifact.ArtifactHandle, artifact.Kind,
        artifact.ContentDigest, destination.DestinationHandle)).ConfigureAwait(false);
    var response = await fixture.Responder.TryHandleAsync(new PhotonCadArtifactResourceRequest(
        "GET", review.ResourceUrl!, true, fixture.Context)).ConfigureAwait(false)
        ?? throw new InvalidOperationException("resource not handled");
    Check.Equal(200, response.StatusCode, "locked cleanup resource");
    await fixture.Storage.RevokeContextAsync(fixture.Context).ConfigureAwait(false);
    Check.Code("resource_stream_closed", () =>
    {
        try
        {
            _ = response.Content!.ReadByte();
        }
        catch (ObjectDisposedException)
        {
            throw new PhotonCadArtifactException("resource_stream_closed");
        }
    }, "context cleanup forcibly closes broker-owned resource leases");
    await response.DisposeAsync().ConfigureAwait(false);
    var directCleanup = await fixture.Storage.RetryPendingCleanupAsync().ConfigureAwait(false);
    Check.Equal(0, directCleanup.PendingEntries, "tracked resource cleanup is immediate");

    var externallyLocked = await fixture.SealAsync("cleanup-locked", SmokeData.Step("LOCKED")).ConfigureAwait(false);
    var sealedPath = Directory.GetFiles(fixture.Directory.Child("broker\\sealed"), "*.step").Single();
    await using var externalLease = new FileStream(sealedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
    await fixture.Storage.RevokeContextAsync(externallyLocked.Context).ConfigureAwait(false);
    var pending = await fixture.Storage.RetryPendingCleanupAsync().ConfigureAwait(false);
    Check.True(pending.PendingEntries >= 1, "locked artifact is quarantined as pending");
    Check.True(pending.ReasonCodes.All(code => !code.Contains('\\') && !code.Contains('/')),
        "cleanup status exposes reason codes only");
    await externalLease.DisposeAsync().ConfigureAwait(false);
    var cleared = await fixture.Storage.RetryPendingCleanupAsync().ConfigureAwait(false);
    Check.Equal(0, cleared.PendingEntries, "exact pending cleanup succeeds after lease closes");
}

static async ValueTask ProtocolAsync()
{
    await using var fixture = new ArtifactFixture();
    var artifact = await fixture.SealAsync("protocol", SmokeData.Step("PROTOCOL")).ConfigureAwait(false);
    var target = Path.Combine(fixture.OutputDirectory, "protocol.step");
    fixture.Picker.Enqueue(target);
    var frames = new List<string>();
    await using var bridge = new PhotonCadArtifactProtocolBridge(
        fixture.Storage,
        fixture.Coordinator,
        fixture.Destinations,
        value => frames.Add(JsonSerializer.Serialize(value)));
    bridge.PublishAvailable(artifact);

    await bridge.HandleAsync("photonCad.artifact.destination.pick", Frame(new
    {
        type = "photonCad.artifact.destination.pick",
        version = 1,
        requestId = "pick-1",
        rendererSessionId = fixture.Context.RendererSessionId,
        controllerId = fixture.Context.ControllerId,
        sessionId = fixture.Context.CadSessionId,
        projectId = fixture.Context.ProjectId,
        revision = fixture.Context.Revision,
        artifactHandle = artifact.ArtifactHandle.Value,
    })).ConfigureAwait(false);
    var destinationHandle = ValueString(frames[^1], "destinationHandle");

    await bridge.HandleAsync("photonCad.artifact.review", Frame(new
    {
        type = "photonCad.artifact.review",
        version = 1,
        requestId = "review-1",
        rendererSessionId = fixture.Context.RendererSessionId,
        controllerId = fixture.Context.ControllerId,
        sessionId = fixture.Context.CadSessionId,
        projectId = fixture.Context.ProjectId,
        revision = fixture.Context.Revision,
        artifactHandle = artifact.ArtifactHandle.Value,
        kind = artifact.Kind,
        contentDigest = artifact.ContentDigest,
        destinationHandle,
    })).ConfigureAwait(false);
    var reviewHandle = ValueString(frames[^1], "reviewHandle");
    var fingerprint = ValueString(frames[^1], "fingerprint");

    await bridge.HandleAsync("photonCad.artifact.commit", Frame(new
    {
        type = "photonCad.artifact.commit",
        version = 1,
        requestId = "commit-1",
        rendererSessionId = fixture.Context.RendererSessionId,
        controllerId = fixture.Context.ControllerId,
        sessionId = fixture.Context.CadSessionId,
        projectId = fixture.Context.ProjectId,
        revision = fixture.Context.Revision,
        reviewHandle,
        fingerprint,
    })).ConfigureAwait(false);
    Check.True(File.Exists(target), "protocol commit created destination");
    Check.True(frames[^1].Contains("\"status\":\"committed\"", StringComparison.Ordinal),
        "protocol commit is explicit");

    await bridge.HandleAsync("photonCad.artifact.commit", Frame(new
    {
        type = "photonCad.artifact.commit",
        version = 1,
        requestId = "commit-1",
        rendererSessionId = fixture.Context.RendererSessionId,
        controllerId = fixture.Context.ControllerId,
        sessionId = fixture.Context.CadSessionId,
        projectId = fixture.Context.ProjectId,
        revision = fixture.Context.Revision,
        reviewHandle,
        fingerprint,
    })).ConfigureAwait(false);
    Check.True(frames[^1].Contains("duplicate_request", StringComparison.Ordinal), "duplicate protocol request rejected");

    await bridge.HandleAsync("photonCad.artifact.review", Frame(new
    {
        type = "photonCad.artifact.review",
        version = 1,
        requestId = "future-1",
        rendererSessionId = fixture.Context.RendererSessionId,
        controllerId = fixture.Context.ControllerId,
        sessionId = fixture.Context.CadSessionId,
        projectId = fixture.Context.ProjectId,
        revision = fixture.Context.Revision,
        artifactHandle = artifact.ArtifactHandle.Value,
        kind = "glb",
        contentDigest = artifact.ContentDigest,
        destinationHandle = PhotonCadDestinationHandle.New().Value,
    })).ConfigureAwait(false);
    Check.True(frames[^1].Contains("artifact_format_unavailable", StringComparison.Ordinal),
        "future format returns typed unavailable");

    Check.True(frames.All(frame => !frame.Contains(fixture.Directory.Path, StringComparison.OrdinalIgnoreCase) &&
        !frame.Contains("\\broker\\", StringComparison.OrdinalIgnoreCase) &&
        !frame.Contains("\\outputs\\", StringComparison.OrdinalIgnoreCase)),
        "protocol frames never expose host paths");
}

static JsonElement Frame(object value) => JsonSerializer.SerializeToElement(value);

static string ValueString(string frame, string property)
{
    using var document = JsonDocument.Parse(frame);
    return document.RootElement.GetProperty("value").GetProperty(property).GetString()
        ?? throw new InvalidOperationException($"Missing {property}.");
}

internal static class UriSmokeExtensions
{
    internal static string Origin(this Uri uri) => uri.GetLeftPart(UriPartial.Authority);
}
