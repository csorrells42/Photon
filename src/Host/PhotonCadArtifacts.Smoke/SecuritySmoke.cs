using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using PhotonCadArtifacts;

namespace PhotonCadArtifacts.Smoke;

internal static class ArtifactSecuritySmoke
{
    internal static async ValueTask CustodyRecoveryAsync()
    {
        await FreshCleanupRegistryAsync().ConfigureAwait(false);
        await ReplacementAndCapacityAsync().ConfigureAwait(false);
        ForgedCleanupJournal();
    }

    internal static async ValueTask ProtocolBoundsAsync()
    {
        await CapacityCancellationAndReplayAsync().ConfigureAwait(false);
        await RetryableShutdownAsync().ConfigureAwait(false);
    }

    internal static async ValueTask DestinationJournalAsync()
    {
        await CompletedOutputRecoveryAsync().ConfigureAwait(false);
        await IncompleteAndReplacedOutputAsync().ConfigureAwait(false);
        DpapiTamperWrongUserAndTtl();
    }

    private static async ValueTask FreshCleanupRegistryAsync()
    {
        using var directory = new TemporaryDirectory();
        var root = directory.Child("broker");
        var context = Context("recovery");
        var authority = new MutableContextAuthority(context);
        var source = new FakeArtifactSource();
        var bytes = SmokeData.Step("RECOVERY");
        source.Register("source-recovery", bytes);
        var options = StorageOptions(root, maximumPending: 4);
        var original = new PhotonCadArtifactStorage(options, source, authority);
        _ = await original.SealAsync(new PhotonCadArtifactSourceRequest("source-recovery", context)).ConfigureAwait(false);
        var sealedPath = SingleFile(Path.Combine(root, "sealed"), "*.step");

        var recovered = new PhotonCadArtifactStorage(options, source, authority);
        var status = await recovered.RetryPendingCleanupAsync().ConfigureAwait(false);
        Check.Equal(0, status.PendingEntries, "fresh registry replays exact durable cleanup");
        Check.True(!File.Exists(sealedPath), "fresh registry removes only the registered sealed identity");
        await recovered.DisposeAsync().ConfigureAwait(false);
        await original.DisposeAsync().ConfigureAwait(false);
    }

    private static async ValueTask ReplacementAndCapacityAsync()
    {
        var inaccessibleCustody = new ScriptedCleanupCustody();
        inaccessibleCustody.Enqueue(PhotonCadExactDeleteOutcome.Missing);
        inaccessibleCustody.Enqueue(PhotonCadExactDeleteOutcome.Blocked, "cleanup_access_denied");
        await using (var fixture = new ArtifactFixture(
            maximumPendingCleanup: 4,
            cleanupCustody: inaccessibleCustody))
        {
            var bytes = SmokeData.Step("INACCESSIBLE");
            _ = await fixture.SealAsync("inaccessible", bytes).ConfigureAwait(false);
            var sealedPath = SingleFile(fixture.Directory.Child(@"broker\sealed"), "*.step");
            await fixture.Storage.RevokeContextAsync(fixture.Context).ConfigureAwait(false);
            var pending = await fixture.Storage.RetryPendingCleanupAsync().ConfigureAwait(false);
            Check.Equal(1, pending.PendingEntries, "inaccessible exact artifact remains in retryable custody");
            Check.True(inaccessibleCustody.Calls.All(call =>
                call.Root == fixture.Directory.Child(@"broker\sealed") &&
                call.Length == bytes.LongLength && call.Digest == SmokeData.Digest(bytes)),
                "cleanup attempts always carry exact root length digest and file identity");
            File.Delete(sealedPath);
            inaccessibleCustody.Enqueue(PhotonCadExactDeleteOutcome.Missing);
            inaccessibleCustody.Enqueue(PhotonCadExactDeleteOutcome.Missing);
            var cleared = await fixture.Storage.RetryPendingCleanupAsync().ConfigureAwait(false);
            Check.Equal(0, cleared.PendingEntries, "inaccessible cleanup remains idempotently retryable");
        }

        await using (var fixture = new ArtifactFixture(maximumPendingCleanup: 4))
        {
            var bytes = SmokeData.Step("REPLACED");
            var artifact = await fixture.SealAsync("replaced", bytes).ConfigureAwait(false);
            var sealedPath = SingleFile(fixture.Directory.Child(@"broker\sealed"), "*.step");
            File.Delete(sealedPath);
            await File.WriteAllBytesAsync(sealedPath, bytes).ConfigureAwait(false);
            await fixture.Storage.RevokeContextAsync(artifact.Context).ConfigureAwait(false);
            var pending = await fixture.Storage.RetryPendingCleanupAsync().ConfigureAwait(false);
            Check.True(pending.PendingEntries == 1, "same-byte replacement retains exact custody record");
            Check.True(File.Exists(sealedPath) && (await File.ReadAllBytesAsync(sealedPath).ConfigureAwait(false)).SequenceEqual(bytes),
                "same-byte foreign identity is never deleted");
            File.Delete(sealedPath);
            var cleared = await fixture.Storage.RetryPendingCleanupAsync().ConfigureAwait(false);
            Check.Equal(0, cleared.PendingEntries, "missing exact identity retires custody after foreign file is removed");
        }

        await using (var fixture = new ArtifactFixture(maximumArtifacts: 4, maximumPendingCleanup: 1))
        {
            _ = await fixture.SealAsync("capacity-one", SmokeData.Step("ONE")).ConfigureAwait(false);
            await Check.CodeAsync("cleanup_journal_capacity", async () =>
            {
                _ = await fixture.SealAsync("capacity-two", SmokeData.Step("TWO")).ConfigureAwait(false);
            }, "cleanup capacity is reserved before a second artifact becomes live").ConfigureAwait(false);
            var sealedRoot = fixture.Directory.Child(@"broker\sealed");
            Check.Equal(1, Directory.GetFiles(sealedRoot, "*.step", SearchOption.TopDirectoryOnly).Length,
                "capacity failure preserves the existing artifact");
            Check.Equal(0, Directory.GetFiles(sealedRoot, "*.partial", SearchOption.TopDirectoryOnly).Length,
                "capacity failure leaves no surviving unregistered partial");
            await fixture.Storage.RevokeContextAsync(fixture.Context).ConfigureAwait(false);
        }
    }

    private static void ForgedCleanupJournal()
    {
        using var directory = new TemporaryDirectory();
        var broker = directory.Child("broker");
        var sealedRoot = Path.Combine(broker, "sealed");
        PhotonCadWindowsFilePolicy.EnsureOwnedDirectory(broker);
        PhotonCadWindowsFilePolicy.EnsureOwnedDirectory(sealedRoot);
        var journal = new PhotonCadArtifactCleanupJournal(broker, sealedRoot, 2);
        var foreign = Path.Combine(sealedRoot, "foreign.step");
        File.WriteAllBytes(foreign, SmokeData.Step("FOREIGN"));
        var slot = Path.Combine(broker, "transactions", "cleanup-0000.intent");
        File.WriteAllBytes(slot, "{\"version\":1"u8.ToArray());
        Check.Equal(0, journal.Load().Count, "truncated cleanup journal is never accepted");
        Check.True(journal.BlockedReasonCodes.Contains("cleanup_journal_invalid", StringComparer.Ordinal),
            "truncated cleanup journal consumes bounded custody capacity");
        Check.True(File.Exists(foreign), "foreign sealed file is untouched by journal recovery");
    }

    private static async ValueTask CapacityCancellationAndReplayAsync()
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero));
        var contextOne = Context("one");
        var contextTwo = Context("two");
        var contextThree = Context("three");
        var authority = new MutableContextAuthority(contextOne);
        var source = new FakeArtifactSource();
        var storage = new PhotonCadArtifactStorage(StorageOptions(directory.Child("broker"), 8), source, authority, clock);
        source.Register("source-one", SmokeData.Step("ONE"));
        var artifactOne = await storage.SealAsync(new PhotonCadArtifactSourceRequest("source-one", contextOne)).ConfigureAwait(false);
        authority.CurrentContext = contextTwo;
        source.Register("source-two", SmokeData.Step("TWO"));
        var artifactTwo = await storage.SealAsync(new PhotonCadArtifactSourceRequest("source-two", contextTwo)).ConfigureAwait(false);
        authority.CurrentContext = contextThree;
        source.Register("source-three", SmokeData.Step("THREE"));
        var artifactThree = await storage.SealAsync(new PhotonCadArtifactSourceRequest("source-three", contextThree)).ConfigureAwait(false);

        var picker = new BlockingDestinationPicker();
        var destinations = new WindowsPhotonCadArtifactDestinationAuthority(
            picker,
            authority,
            directory.Child("destination-transactions"),
            clock,
            TimeSpan.FromMinutes(2));
        var release = new PhotonCadArtifactReleaseOptions(new Uri("http://127.0.0.1:9119/"));
        var coordinator = new PhotonCadArtifactReleaseCoordinator(storage, destinations, authority, release, clock);
        var frames = new ConcurrentQueue<string>();
        var limits = new PhotonCadArtifactProtocolLimits(
            maximumActiveRequests: 2,
            maximumActiveRequestsPerContext: 1,
            maximumReplayEntries: 32,
            replayTimeToLive: TimeSpan.FromSeconds(30),
            shutdownTimeout: TimeSpan.FromSeconds(2));
        var bridge = new PhotonCadArtifactProtocolBridge(
            storage,
            coordinator,
            destinations,
            value => frames.Enqueue(JsonSerializer.Serialize(value)),
            limits: limits,
            timeProvider: clock);

        authority.CurrentContext = contextOne;
        var first = bridge.HandleAsync("photonCad.artifact.destination.pick", PickFrame("active-one", contextOne, artifactOne)).AsTask();
        await picker.Entered.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await bridge.HandleAsync(
            "photonCad.artifact.destination.pick",
            PickFrame("context-excess", contextOne, artifactOne)).ConfigureAwait(false);
        Check.True(frames.Last().Contains("artifact_request_capacity", StringComparison.Ordinal),
            "per-context saturation fails without consuming unrelated global capacity");
        authority.CurrentContext = contextTwo;
        var second = bridge.HandleAsync("photonCad.artifact.destination.pick", PickFrame("active-two", contextTwo, artifactTwo)).AsTask();
        authority.CurrentContext = contextThree;
        await bridge.HandleAsync("photonCad.artifact.destination.pick", PickFrame("global-excess", contextThree, artifactThree)).ConfigureAwait(false);
        Check.True(frames.Any(frame => frame.Contains("artifact_request_capacity", StringComparison.Ordinal)),
            "global saturation fails with one fixed pathless code");

        await bridge.HandleAsync("photonCad.artifact.cancel", CancelFrame("foreign-cancel", contextTwo, "active-one"))
            .ConfigureAwait(false);
        Check.True(frames.Last().Contains("request_not_active", StringComparison.Ordinal),
            "foreign context cannot cancel an active request");
        await bridge.HandleAsync("photonCad.artifact.cancel", CancelFrame("owner-cancel", contextOne, "active-one"))
            .ConfigureAwait(false);
        Check.True(frames.Last().Contains("cancellation_requested", StringComparison.Ordinal),
            "owner Cancel remains available while active capacity is full");
        picker.Release();
        await Task.WhenAll(first, second).ConfigureAwait(false);
        Check.Equal(1, picker.MaximumConcurrent, "native picker is globally serialized");

        var discard = DiscardFrame("replay-id", contextTwo);
        await bridge.HandleAsync("photonCad.artifact.discard", discard).ConfigureAwait(false);
        await bridge.HandleAsync("photonCad.artifact.discard", discard).ConfigureAwait(false);
        Check.True(frames.Last().Contains("duplicate_request", StringComparison.Ordinal), "live replay ID is rejected");
        clock.Advance(TimeSpan.FromSeconds(31));
        await bridge.HandleAsync("photonCad.artifact.discard", discard).ConfigureAwait(false);
        Check.True(!frames.Last().Contains("duplicate_request", StringComparison.Ordinal),
            "expired replay ID is accepted after monotonic TTL");
        for (var index = 0; index < 40; index++)
            await bridge.HandleAsync("photonCad.artifact.discard", DiscardFrame($"lru-{index}", contextTwo)).ConfigureAwait(false);
        Check.True(!frames.Any(frame => frame.Contains("request_replay_capacity", StringComparison.Ordinal)),
            "finite replay registry evicts inactive LRU entries instead of saturating permanently");

        await bridge.DisposeAsync().ConfigureAwait(false);
    }

    private static async ValueTask RetryableShutdownAsync()
    {
        using var directory = new TemporaryDirectory();
        var context = Context("shutdown");
        var authority = new MutableContextAuthority(context);
        var source = new FakeArtifactSource();
        source.Register("source", SmokeData.Step("SHUTDOWN"));
        var storage = new PhotonCadArtifactStorage(StorageOptions(directory.Child("broker"), 4), source, authority);
        var artifact = await storage.SealAsync(new PhotonCadArtifactSourceRequest("source", context)).ConfigureAwait(false);
        var picker = new StubbornDestinationPicker();
        var destinations = new WindowsPhotonCadArtifactDestinationAuthority(
            picker,
            authority,
            directory.Child("destination-transactions"));
        var coordinator = new PhotonCadArtifactReleaseCoordinator(
            storage,
            destinations,
            authority,
            new PhotonCadArtifactReleaseOptions(new Uri("http://127.0.0.1:9119/")));
        var bridge = new PhotonCadArtifactProtocolBridge(
            storage,
            coordinator,
            destinations,
            _ => { },
            limits: new PhotonCadArtifactProtocolLimits(shutdownTimeout: TimeSpan.FromSeconds(1)));
        var active = bridge.HandleAsync("photonCad.artifact.destination.pick", PickFrame("shutdown-active", context, artifact)).AsTask();
        await picker.Entered.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Check.CodeAsync("artifact_shutdown_incomplete", () => bridge.ShutdownAsync(cancelled.Token),
            "cancelled shutdown leaves a retryable closing broker").ConfigureAwait(false);
        picker.Release();
        await active.ConfigureAwait(false);
        await bridge.ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
        await bridge.DisposeAsync().ConfigureAwait(false);
    }

    private static async ValueTask CompletedOutputRecoveryAsync()
    {
        using var directory = new TemporaryDirectory();
        var state = CreateDestinationState(directory, "completed");
        var transaction = await StageTransactionAsync(state, moveToTarget: true).ConfigureAwait(false);
        var recovered = new WindowsPhotonCadArtifactDestinationAuthority(
            new QueueDestinationPicker(),
            state.Authority,
            state.JournalRoot,
            state.Clock,
            TimeSpan.FromMinutes(2));
        _ = await recovered.ResolveAsync(transaction.DestinationHandle, state.Artifact, state.Context).ConfigureAwait(false);
        await using var verified = new MemoryStream(state.Bytes, writable: false);
        var receipt = await recovered.CommitAsync(
            transaction.DestinationHandle,
            state.Artifact,
            state.Context,
            verified).ConfigureAwait(false);
        Check.Equal(state.Artifact.ContentDigest, receipt.ContentDigest, "fresh registry returns exact completed output receipt");
        Check.True(receipt.GetType().GetProperties().All(property => !property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase)),
            "recovered receipt remains pathless");
        Check.True(File.Exists(state.TargetPath), "receipt recovery never deletes completed customer output");
        Check.Equal(0, Directory.GetFiles(state.JournalRoot, "*.txn", SearchOption.TopDirectoryOnly).Length,
            "recovered receipt immediately retires its encrypted transaction");
        await recovered.DisposeAsync().ConfigureAwait(false);
    }

    private static async ValueTask IncompleteAndReplacedOutputAsync()
    {
        using (var directory = new TemporaryDirectory())
        {
            var state = CreateDestinationState(directory, "incomplete");
            var transaction = await StageTransactionAsync(state, moveToTarget: false).ConfigureAwait(false);
            var recovered = new WindowsPhotonCadArtifactDestinationAuthority(
                new QueueDestinationPicker(), state.Authority, state.JournalRoot, state.Clock, TimeSpan.FromMinutes(2));
            await Check.CodeAsync("destination_recovery_incomplete", async () =>
            {
                _ = await recovered.ResolveAsync(transaction.DestinationHandle, state.Artifact, state.Context).ConfigureAwait(false);
            }, "crash before target rename never invents a completed receipt").ConfigureAwait(false);
            Check.True(!File.Exists(state.TargetPath), "incomplete recovery does not create customer output");
            await recovered.DisposeAsync().ConfigureAwait(false);
        }

        using (var directory = new TemporaryDirectory())
        {
            var state = CreateDestinationState(directory, "replacement");
            var transaction = await StageTransactionAsync(state, moveToTarget: true).ConfigureAwait(false);
            File.Delete(state.TargetPath);
            await File.WriteAllBytesAsync(state.TargetPath, state.Bytes).ConfigureAwait(false);
            var recovered = new WindowsPhotonCadArtifactDestinationAuthority(
                new QueueDestinationPicker(), state.Authority, state.JournalRoot, state.Clock, TimeSpan.FromMinutes(2));
            await Check.CodeAsync("destination_recovery_mismatch", async () =>
            {
                _ = await recovered.ResolveAsync(transaction.DestinationHandle, state.Artifact, state.Context).ConfigureAwait(false);
            }, "same-byte replacement is not the completed customer output").ConfigureAwait(false);
            Check.True(File.Exists(state.TargetPath), "foreign replacement is never overwritten or deleted");
            await recovered.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static void DpapiTamperWrongUserAndTtl()
    {
        using (var directory = new TemporaryDirectory())
        {
            var state = CreateDestinationState(directory, "tamper");
            var transaction = StageTransactionAsync(state, moveToTarget: true).AsTask().GetAwaiter().GetResult();
            var bytes = File.ReadAllBytes(transaction.JournalPath);
            bytes[^1] ^= 0x5a;
            File.WriteAllBytes(transaction.JournalPath, bytes);
            var journal = new PhotonCadDestinationTransactionJournal(state.JournalRoot, timeProvider: state.Clock);
            Check.Equal(0, journal.Load().Count, "DPAPI tamper is rejected");
            Check.True(journal.BlockedReasons.Contains("destination_journal_invalid", StringComparer.Ordinal),
                "DPAPI tamper remains a fixed pathless recovery error");
            Check.True(File.Exists(state.TargetPath), "tampered journal never causes customer output deletion");
        }

        using (var directory = new TemporaryDirectory())
        {
            var state = CreateDestinationState(directory, "wrong-user");
            var journal = new PhotonCadDestinationTransactionJournal(
                state.JournalRoot,
                protector: new IdentityJournalProtector(),
                timeProvider: state.Clock);
            _ = ReserveSynthetic(journal, state);
            var wrongUser = new PhotonCadDestinationTransactionJournal(
                state.JournalRoot,
                protector: new WrongUserJournalProtector(),
                timeProvider: state.Clock);
            Check.Equal(0, wrongUser.Load().Count, "wrong-user journal cannot be decrypted");
            Check.True(wrongUser.BlockedReasons.Count == 1, "wrong-user failure is bounded and pathless");
        }

        using (var directory = new TemporaryDirectory())
        {
            var state = CreateDestinationState(directory, "expired");
            var protector = new IdentityJournalProtector();
            var journal = new PhotonCadDestinationTransactionJournal(
                state.JournalRoot,
                protector: protector,
                timeProvider: state.Clock);
            _ = ReserveSynthetic(journal, state);
            var preRollback = new PhotonCadDestinationTransactionJournal(
                state.JournalRoot,
                protector: protector,
                timeProvider: state.Clock).Load().Single();
            state.Clock.RollbackUtc(TimeSpan.FromMinutes(1));
            Check.True(journal.IsExpired(preRollback), "UTC rollback cannot extend destination recovery TTL");
            state.Clock.Advance(TimeSpan.FromHours(25));
            var loaded = new PhotonCadDestinationTransactionJournal(
                state.JournalRoot,
                protector: protector,
                timeProvider: state.Clock).Load().Single();
            Check.True(journal.IsExpired(loaded), "destination transaction expires no later than 24 hours");
        }
    }

    private static async ValueTask<PhotonCadDestinationTransaction> StageTransactionAsync(
        DestinationState state,
        bool moveToTarget)
    {
        var temporary = Path.Combine(state.OutputRoot, $".{state.Name}.photon-stage.tmp");
        await using var output = PhotonCadWindowsFilePolicy.CreateNewOwnedFile(temporary, state.OutputRoot, 65_536);
        var identity = PhotonCadWindowsFilePolicy.InspectRegularFileMetadata(output.SafeFileHandle).Identity;
        PhotonCadWindowsFilePolicy.SetDeleteOnClose(output.SafeFileHandle, delete: true);
        await output.WriteAsync(state.Bytes).ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
        output.Flush(flushToDisk: true);
        var journal = new PhotonCadDestinationTransactionJournal(state.JournalRoot, timeProvider: state.Clock);
        var transaction = journal.Reserve(
            state.Destination,
            state.TargetPath,
            state.OutputRoot,
            PhotonCadWindowsFilePolicy.InspectDirectory(state.OutputRoot),
            temporary,
            identity,
            state.Bytes.LongLength,
            state.Artifact.ContentDigest);
        if (!moveToTarget) return transaction;
        PhotonCadWindowsFilePolicy.SetDeleteOnClose(output.SafeFileHandle, delete: false);
        PhotonCadWindowsFilePolicy.RenameOwnedHandle(output.SafeFileHandle, state.TargetPath, state.OutputRoot);
        PhotonCadWindowsFilePolicy.FlushHandle(output.SafeFileHandle);
        PhotonCadWindowsFilePolicy.FlushDirectory(state.OutputRoot);
        return transaction;
    }

    private static PhotonCadDestinationTransaction ReserveSynthetic(
        PhotonCadDestinationTransactionJournal journal,
        DestinationState state)
    {
        var target = Path.Combine(state.OutputRoot, $"{state.Name}-synthetic.step");
        File.WriteAllBytes(target, state.Bytes);
        var metadata = PhotonCadWindowsFilePolicy.InspectRegularFileMetadata(target, state.OutputRoot);
        return journal.Reserve(
            state.Destination,
            target,
            state.OutputRoot,
            PhotonCadWindowsFilePolicy.InspectDirectory(state.OutputRoot),
            Path.Combine(state.OutputRoot, $".{state.Name}-synthetic.tmp"),
            metadata.Identity,
            metadata.ByteLength,
            state.Artifact.ContentDigest);
    }

    private static DestinationState CreateDestinationState(TemporaryDirectory directory, string name)
    {
        var output = directory.Child("outputs");
        Directory.CreateDirectory(output);
        var journal = directory.Child("destination-transactions");
        var context = Context(name);
        var authority = new MutableContextAuthority(context);
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero));
        var bytes = SmokeData.Step(name.ToUpperInvariant());
        var artifact = new PhotonCadArtifactDescriptor(
            PhotonCadArtifactHandle.New(),
            context,
            PhotonCadArtifactContract.StepKind,
            PhotonCadArtifactContract.StepMediaType,
            PhotonCadArtifactContract.UnspecifiedProfile,
            SmokeData.Digest(bytes),
            bytes.LongLength,
            $"{name}.step",
            clock.UtcNow.AddHours(1));
        var destination = new PhotonCadDestinationDescriptor(
            PhotonCadDestinationHandle.New(),
            artifact.ArtifactHandle,
            context,
            $"{name}.step",
            clock.UtcNow.AddMinutes(30));
        return new DestinationState(
            name,
            output,
            journal,
            Path.Combine(output, $"{name}.step"),
            bytes,
            context,
            authority,
            clock,
            artifact,
            destination);
    }

    private static PhotonCadArtifactStorageOptions StorageOptions(string root, int maximumPending) => new(
        root,
        maximumArtifactBytes: 1_048_576,
        maximumSessionBytes: 4_194_304,
        maximumArtifactsPerContext: 8,
        artifactTimeToLive: TimeSpan.FromMinutes(2),
        maximumQuarantineEntries: maximumPending);

    private static PhotonCadArtifactContext Context(string suffix) => new(
        $"renderer-{suffix}",
        $"controller-{suffix}",
        $"session-{suffix}",
        $"project-{suffix}",
        7);

    private static JsonElement PickFrame(
        string requestId,
        PhotonCadArtifactContext context,
        PhotonCadArtifactDescriptor artifact) => JsonSerializer.SerializeToElement(new
        {
            version = 1,
            requestId,
            rendererSessionId = context.RendererSessionId,
            controllerId = context.ControllerId,
            sessionId = context.CadSessionId,
            projectId = context.ProjectId,
            revision = context.Revision,
            artifactHandle = artifact.ArtifactHandle.Value,
        });

    private static JsonElement CancelFrame(
        string requestId,
        PhotonCadArtifactContext context,
        string targetRequestId) => JsonSerializer.SerializeToElement(new
        {
            version = 1,
            requestId,
            rendererSessionId = context.RendererSessionId,
            controllerId = context.ControllerId,
            sessionId = context.CadSessionId,
            projectId = context.ProjectId,
            revision = context.Revision,
            targetRequestId,
        });

    private static JsonElement DiscardFrame(string requestId, PhotonCadArtifactContext context) =>
        JsonSerializer.SerializeToElement(new
        {
            version = 1,
            requestId,
            rendererSessionId = context.RendererSessionId,
            controllerId = context.ControllerId,
            sessionId = context.CadSessionId,
            projectId = context.ProjectId,
            revision = context.Revision,
            reviewHandle = PhotonCadArtifactReviewHandle.New().Value,
        });

    private static string SingleFile(string root, string pattern)
    {
        var files = Directory.GetFiles(root, pattern, SearchOption.TopDirectoryOnly);
        Check.Equal(1, files.Length, "expected one owned smoke target");
        return files[0];
    }

    private sealed class StubbornDestinationPicker : IPhotonCadNativeDestinationPicker
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Entered => _entered.Task;
        internal void Release() => _release.TrySetResult();

        public async ValueTask<string?> PickNewStepPathAsync(
            string suggestedFileName,
            CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            return null;
        }
    }

    private sealed class IdentityJournalProtector : IPhotonCadDestinationJournalProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext) => plaintext.ToArray();
        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext) => ciphertext.ToArray();
    }

    private sealed class WrongUserJournalProtector : IPhotonCadDestinationJournalProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext) => throw new PhotonCadArtifactException("wrong_user");
        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext) => throw new PhotonCadArtifactException("wrong_user");
    }

    private sealed record DestinationState(
        string Name,
        string OutputRoot,
        string JournalRoot,
        string TargetPath,
        byte[] Bytes,
        PhotonCadArtifactContext Context,
        MutableContextAuthority Authority,
        ManualTimeProvider Clock,
        PhotonCadArtifactDescriptor Artifact,
        PhotonCadDestinationDescriptor Destination);
}
