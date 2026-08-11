using System.Security.Cryptography;
using System.Reflection;
using PhotonCadArtifacts;
using PhotonCadRuntime;

namespace PhotonCadArtifacts.Smoke;

internal sealed class MutableContextAuthority : IPhotonCadArtifactContextAuthority
{
    internal MutableContextAuthority(PhotonCadArtifactContext? current) => CurrentContext = current;
    internal PhotonCadArtifactContext? CurrentContext { get; set; }
    public PhotonCadArtifactContext? Current() => CurrentContext;
}

internal sealed class ManualTimeProvider : TimeProvider
{
    private long _timestamp;

    internal ManualTimeProvider(DateTimeOffset now) => UtcNow = now;
    internal DateTimeOffset UtcNow { get; private set; }
    public override DateTimeOffset GetUtcNow() => UtcNow;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _timestamp;

    internal void Advance(TimeSpan duration)
    {
        UtcNow += duration;
        _timestamp = checked(_timestamp + duration.Ticks);
    }

    internal void RollbackUtc(TimeSpan duration) => UtcNow -= duration;
}

internal sealed class FakeArtifactSource : IPhotonCadArtifactSource
{
    private readonly Dictionary<string, SourceCase> _cases = new(StringComparer.Ordinal);

    internal int Acquisitions { get; private set; }

    internal void Register(
        string id,
        byte[] content,
        string? digest = null,
        long? byteLength = null,
        string kind = PhotonCadArtifactContract.StepKind,
        string mediaType = PhotonCadArtifactContract.StepMediaType,
        string profile = PhotonCadArtifactContract.UnspecifiedProfile,
        bool stableLockedRead = true,
        string displayName = "part.step") =>
        _cases[id] = new SourceCase(
            content.ToArray(),
            digest ?? SmokeData.Digest(content),
            byteLength ?? content.LongLength,
            kind,
            mediaType,
            profile,
            stableLockedRead,
            displayName);

    public ValueTask<PhotonCadArtifactSourceLease> AcquireAsync(
        PhotonCadArtifactSourceRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Acquisitions++;
        if (!_cases.TryGetValue(request.SourceArtifactId, out var item))
            throw new PhotonCadArtifactException("source_unavailable");
        var descriptor = new PhotonCadArtifactSourceDescriptor(
            request.SourceArtifactId,
            item.Kind,
            item.MediaType,
            item.Profile,
            item.Digest,
            item.ByteLength,
            item.DisplayName);
        return ValueTask.FromResult(new PhotonCadArtifactSourceLease(
            descriptor,
            new MemoryStream(item.Content, writable: false),
            item.StableLockedRead));
    }

    private sealed record SourceCase(
        byte[] Content,
        string Digest,
        long ByteLength,
        string Kind,
        string MediaType,
        string Profile,
        bool StableLockedRead,
        string DisplayName);
}

internal sealed class QueueDestinationPicker : IPhotonCadNativeDestinationPicker
{
    private readonly Queue<string?> _paths = new();
    internal void Enqueue(string? path) => _paths.Enqueue(path);

    public ValueTask<string?> PickNewStepPathAsync(
        string suggestedFileName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_paths.Count == 0 ? null : _paths.Dequeue());
    }
}

internal sealed class BlockingDestinationPicker : IPhotonCadNativeDestinationPicker
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _active;
    private int _maximumConcurrent;

    internal Task Entered => _entered.Task;
    internal int MaximumConcurrent => Volatile.Read(ref _maximumConcurrent);
    internal void Release() => _release.TrySetResult();

    public async ValueTask<string?> PickNewStepPathAsync(
        string suggestedFileName,
        CancellationToken cancellationToken = default)
    {
        var active = Interlocked.Increment(ref _active);
        while (true)
        {
            var previous = Volatile.Read(ref _maximumConcurrent);
            if (previous >= active || Interlocked.CompareExchange(ref _maximumConcurrent, active, previous) == previous)
                break;
        }
        _entered.TrySetResult();
        try
        {
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }
}

internal sealed class DelegateArtifactResourceStore : IPhotonCadArtifactResourceStore
{
    internal Func<PhotonCadArtifactResourceHandle, PhotonCadArtifactContext, CancellationToken,
        ValueTask<PhotonCadArtifactReadLease>> Handler
    { get; set; } =
        (_, _, _) => ValueTask.FromException<PhotonCadArtifactReadLease>(
            new PhotonCadArtifactException("resource_unavailable"));

    public ValueTask<PhotonCadArtifactReadLease> ConsumeResourceAsync(
        PhotonCadArtifactResourceHandle resource,
        PhotonCadArtifactContext context,
        CancellationToken cancellationToken = default) => Handler(resource, context, cancellationToken);
}

internal sealed class DelegateArtifactReleaseStorage : IPhotonCadArtifactReleaseStorage
{
    internal required PhotonCadArtifactDescriptor Artifact { get; init; }
    internal required PhotonCadArtifactResourceLease Resource { get; init; }
    internal Func<CancellationToken, ValueTask<PhotonCadArtifactReadLease>> OpenHandler { get; set; } =
        _ => ValueTask.FromException<PhotonCadArtifactReadLease>(new PhotonCadArtifactException("artifact_unavailable"));
    internal Exception? RevokeFailure { get; set; }
    internal int ResourceRevocations { get; private set; }
    internal int ContextRevocations { get; private set; }

    public ValueTask<PhotonCadArtifactDescriptor> DescribeAsync(
        PhotonCadArtifactHandle handle,
        PhotonCadArtifactContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (handle != Artifact.ArtifactHandle || context != Artifact.Context)
            throw new PhotonCadArtifactException("artifact_unavailable");
        return ValueTask.FromResult(Artifact);
    }

    public ValueTask<PhotonCadArtifactReadLease> OpenVerifiedAsync(
        PhotonCadArtifactHandle handle,
        PhotonCadArtifactContext context,
        CancellationToken cancellationToken = default)
    {
        if (handle != Artifact.ArtifactHandle || context != Artifact.Context)
            throw new PhotonCadArtifactException("artifact_unavailable");
        return OpenHandler(cancellationToken);
    }

    public ValueTask<PhotonCadArtifactResourceLease> IssueResourceAsync(
        PhotonCadArtifactHandle handle,
        PhotonCadArtifactContext context,
        TimeSpan timeToLive,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (handle != Artifact.ArtifactHandle || context != Artifact.Context)
            throw new PhotonCadArtifactException("artifact_unavailable");
        return ValueTask.FromResult(Resource);
    }

    public ValueTask<PhotonCadArtifactReadLease> ConsumeResourceAsync(
        PhotonCadArtifactResourceHandle resource,
        PhotonCadArtifactContext context,
        CancellationToken cancellationToken = default) => OpenHandler(cancellationToken);

    public ValueTask RevokeResourceAsync(PhotonCadArtifactResourceHandle resource)
    {
        ResourceRevocations++;
        return RevokeFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(RevokeFailure);
    }

    public ValueTask RevokeContextAsync(PhotonCadArtifactContext context)
    {
        ContextRevocations++;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class DelegateDestinationAuthority : IPhotonCadArtifactDestinationAuthority
{
    internal required PhotonCadDestinationDescriptor Destination { get; init; }
    internal Func<PhotonCadArtifactDescriptor, PhotonCadArtifactContext, Stream, CancellationToken,
        ValueTask<PhotonCadDestinationCommitReceipt>> CommitHandler
    { get; set; } =
        (_, _, _, _) => ValueTask.FromException<PhotonCadDestinationCommitReceipt>(
            new PhotonCadArtifactException("destination_unavailable"));
    internal Exception? RevokeFailure { get; set; }
    internal int Revocations { get; private set; }

    public ValueTask<PhotonCadDestinationDescriptor?> PickAsync(
        PhotonCadArtifactDescriptor artifact,
        CancellationToken cancellationToken = default) => ValueTask.FromResult<PhotonCadDestinationDescriptor?>(Destination);

    public ValueTask<PhotonCadDestinationDescriptor> ResolveAsync(
        PhotonCadDestinationHandle handle,
        PhotonCadArtifactDescriptor artifact,
        PhotonCadArtifactContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (handle != Destination.DestinationHandle || artifact.ArtifactHandle != Destination.ArtifactHandle ||
            context != Destination.Context)
            throw new PhotonCadArtifactException("destination_unavailable");
        return ValueTask.FromResult(Destination);
    }

    public ValueTask<PhotonCadDestinationCommitReceipt> CommitAsync(
        PhotonCadDestinationHandle handle,
        PhotonCadArtifactDescriptor artifact,
        PhotonCadArtifactContext context,
        Stream verifiedContent,
        CancellationToken cancellationToken = default)
    {
        if (handle != Destination.DestinationHandle)
            throw new PhotonCadArtifactException("destination_unavailable");
        return CommitHandler(artifact, context, verifiedContent, cancellationToken);
    }

    public ValueTask RevokeAsync(PhotonCadDestinationHandle handle)
    {
        Revocations++;
        return RevokeFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(RevokeFailure);
    }

    public ValueTask RevokeContextAsync(PhotonCadArtifactContext context) => ValueTask.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class ScriptedCleanupCustody : IPhotonCadArtifactCleanupCustody
{
    private readonly Queue<PhotonCadExactDeleteResult> _results = new();

    internal List<(string Path, string Root, PhotonCadWindowsFileIdentity Identity, long Length, string Digest)> Calls { get; } = [];
    internal void Enqueue(PhotonCadExactDeleteOutcome outcome, string reason = "cleanup_test") =>
        _results.Enqueue(new PhotonCadExactDeleteResult(outcome, reason));

    public ValueTask<PhotonCadExactDeleteResult> DeleteIfExactAsync(
        string path,
        string expectedRoot,
        PhotonCadWindowsFileIdentity identity,
        long byteLength,
        string digest,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add((path, expectedRoot, identity, byteLength, digest));
        return ValueTask.FromResult(_results.Count == 0
            ? new PhotonCadExactDeleteResult(PhotonCadExactDeleteOutcome.Blocked, "cleanup_test_blocked")
            : _results.Dequeue());
    }
}

internal sealed class ThrowingReadableStream : Stream
{
    private readonly Exception _readabilityFailure;
    private readonly Exception? _disposeFailure;

    internal ThrowingReadableStream(Exception readabilityFailure, Exception? disposeFailure = null)
    {
        _readabilityFailure = readabilityFailure;
        _disposeFailure = disposeFailure;
    }

    internal bool DisposeAttempted { get; private set; }
    public override bool CanRead => throw _readabilityFailure;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override ValueTask DisposeAsync()
    {
        DisposeAttempted = true;
        return _disposeFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(_disposeFailure);
    }
}

internal sealed class FixedRuntimeBindingResolver : IPhotonCadRuntimeArtifactBindingResolver
{
    internal FixedRuntimeBindingResolver(PhotonCadRuntimeArtifactBinding binding) => Binding = binding;
    internal PhotonCadRuntimeArtifactBinding? Binding { get; set; }
    public PhotonCadRuntimeArtifactBinding? Resolve(PhotonCadArtifactSourceRequest request) => Binding;
}

internal sealed class ExactLeaseCadRuntimeBroker : ICadRuntimeBroker
{
    private readonly UnavailableCadRuntimeBroker _fallback = new();
    private readonly CadArtifactDescriptor _descriptor;
    private readonly byte[] _content;

    internal ExactLeaseCadRuntimeBroker(
        CadSessionHandle session,
        CadArtifactHandle artifact,
        byte[] content)
    {
        _content = content.ToArray();
        _descriptor = new CadArtifactDescriptor(
            session,
            artifact,
            Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
            content.LongLength,
            PhotonCadArtifactContract.StepMediaType);
    }

    public CadRuntimeDescription Describe() => _fallback.Describe();
    public ValueTask<CadResult<CadProjectDescriptor>> OpenProjectAsync(CadOpenProjectRequest request, CancellationToken cancellationToken = default) =>
        _fallback.OpenProjectAsync(request, cancellationToken);
    public ValueTask<CadResult<CadSessionDescriptor>> StartSessionAsync(CadStartSessionRequest request, CancellationToken cancellationToken = default) =>
        _fallback.StartSessionAsync(request, cancellationToken);
    public ValueTask<CadResult<CadCapabilityCatalog>> GetCatalogAsync(CadRequestId requestId, CancellationToken cancellationToken = default) =>
        _fallback.GetCatalogAsync(requestId, cancellationToken);
    public ValueTask<CadResult<CadOperationResult>> ExecuteAsync(CadOperationRequest request, CancellationToken cancellationToken = default) =>
        _fallback.ExecuteAsync(request, cancellationToken);
    public ValueTask<CadResult<CadVerificationResult>> VerifyAsync(CadVerificationRequest request, CancellationToken cancellationToken = default) =>
        _fallback.VerifyAsync(request, cancellationToken);
    public ValueTask<CadResult<CadSessionDescriptor>> CloseSessionAsync(CadCloseSessionRequest request, CancellationToken cancellationToken = default) =>
        _fallback.CloseSessionAsync(request, cancellationToken);

    public ValueTask<CadResult<CadArtifactDescriptor>> GetArtifactReceiptAsync(
        CadArtifactReadRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(request.Session == _descriptor.Session && request.Artifact == _descriptor.Artifact &&
            request.MaximumByteLength >= _descriptor.ByteLength
            ? CadResult<CadArtifactDescriptor>.Success(_descriptor)
            : CadResult<CadArtifactDescriptor>.Failure(new CadError("artifact_not_found", "Artifact unavailable.", false)));
    }

    public ValueTask<CadResult<CadArtifactReadLease>> OpenArtifactReadAsync(
        CadArtifactReadRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Session != _descriptor.Session || request.Artifact != _descriptor.Artifact ||
            request.MaximumByteLength < _descriptor.ByteLength)
            return ValueTask.FromResult(CadResult<CadArtifactReadLease>.Failure(
                new CadError("artifact_not_found", "Artifact unavailable.", false)));

        var constructor = typeof(CadArtifactReadLease).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate => candidate.GetParameters().Length == 3);
        var lease = (CadArtifactReadLease)constructor.Invoke([
            _descriptor,
            new MemoryStream(_content, writable: false),
            null,
        ]);
        return ValueTask.FromResult(CadResult<CadArtifactReadLease>.Success(lease));
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    internal TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "PhotonCadArtifacts.Smoke",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    internal string Path { get; }
    internal string Child(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        if (!Directory.Exists(Path)) return;
        var full = System.IO.Path.GetFullPath(Path);
        var expected = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PhotonCadArtifacts.Smoke") +
            System.IO.Path.DirectorySeparatorChar;
        if (!full.StartsWith(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to clean an unexpected smoke directory.");
        Directory.Delete(full, recursive: true);
    }
}

internal static class SmokeData
{
    internal static byte[] Step(string marker = "BOX") => System.Text.Encoding.ASCII.GetBytes($$"""
        ISO-10303-21;
        HEADER;
        FILE_DESCRIPTION(('PHOTON CAD SMOKE'),'2;1');
        FILE_NAME('{{marker}}.step','2026-08-10T00:00:00',('Photon'),('Photon'),'','','');
        FILE_SCHEMA(('CONFIG_CONTROL_DESIGN'));
        ENDSEC;
        DATA;
        #1=PRODUCT('{{marker}}','{{marker}}','',());
        ENDSEC;
        END-ISO-10303-21;
        """);

    internal static byte[] OpenCascadeStep() => System.Text.Encoding.ASCII.GetBytes("""
        ISO-10303-21;
        HEADER;
        FILE_DESCRIPTION(('Open CASCADE Shape Model'),'2;1');
        FILE_NAME('Open CASCADE Shape Model','2026-08-10T00:00:00',('build123d'),(''),'Open CASCADE STEP processor 7.8','Open CASCADE 7.8','Unknown');
        FILE_SCHEMA(('AUTOMOTIVE_DESIGN { 1 0 10303 214 3 1 1 }'));
        ENDSEC;
        DATA;
        #1 = APPLICATION_PROTOCOL_DEFINITION('international standard','automotive_design',2000,#2);
        #2 = APPLICATION_CONTEXT('core data for automotive mechanical design processes');
        #3 = PRODUCT('GEAR-001','GEAR-001','',(#4));
        #4 = PRODUCT_CONTEXT('',#2,'mechanical');
        #5 = CARTESIAN_POINT('',(0.,0.,0.));
        /* Open CASCADE writes escaped text such as \X2\03A6\X0\ in ordinary strings. */
        #6 = PRODUCT('\\X2\\03A6\\X0\\','GEAR','',(#4));
        ENDSEC;
        END-ISO-10303-21;
        """);

    internal static string Digest(byte[] content) =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()}";
}

internal static class Check
{
    internal static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected {expected}; actual {actual}");
    }

    internal static async ValueTask CodeAsync(string code, Func<ValueTask> action, string message)
    {
        try
        {
            await action().ConfigureAwait(false);
            throw new InvalidOperationException($"{message}: expected {code}");
        }
        catch (PhotonCadArtifactException exception)
        {
            Equal(code, exception.Code, message);
        }
    }

    internal static void Code(string code, Action action, string message)
    {
        try
        {
            action();
            throw new InvalidOperationException($"{message}: expected {code}");
        }
        catch (PhotonCadArtifactException exception)
        {
            Equal(code, exception.Code, message);
        }
    }
}

internal sealed class ArtifactFixture : IAsyncDisposable
{
    internal ArtifactFixture(
        int maximumArtifacts = 16,
        int maximumPendingCleanup = 64,
        IPhotonCadArtifactCleanupCustody? cleanupCustody = null)
    {
        Directory = new TemporaryDirectory();
        OutputDirectory = Directory.Child("outputs");
        System.IO.Directory.CreateDirectory(OutputDirectory);
        Context = new PhotonCadArtifactContext("renderer-1", "controller-1", "session-1", "project-1", 7);
        ContextAuthority = new MutableContextAuthority(Context);
        Clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero));
        Source = new FakeArtifactSource();
        Picker = new QueueDestinationPicker();
        Storage = new PhotonCadArtifactStorage(
            new PhotonCadArtifactStorageOptions(
                Directory.Child("broker"),
                maximumArtifactBytes: 1_048_576,
                maximumSessionBytes: 2_097_152,
                maximumArtifactsPerContext: maximumArtifacts,
                artifactTimeToLive: TimeSpan.FromMinutes(2),
                maximumQuarantineEntries: maximumPendingCleanup),
            Source,
            ContextAuthority,
            Clock,
            cleanupCustody);
        Destinations = new WindowsPhotonCadArtifactDestinationAuthority(
            Picker,
            ContextAuthority,
            Directory.Child("destination-transactions"),
            Clock,
            TimeSpan.FromMinutes(2));
        ReleaseOptions = new PhotonCadArtifactReleaseOptions(
            new Uri("http://127.0.0.1:9119/"),
            reviewTimeToLive: TimeSpan.FromMinutes(1),
            resourceTimeToLive: TimeSpan.FromMinutes(1));
        Coordinator = new PhotonCadArtifactReleaseCoordinator(
            Storage,
            Destinations,
            ContextAuthority,
            ReleaseOptions,
            Clock);
        Responder = new PhotonCadArtifactResourceResponder(Storage, ContextAuthority, ReleaseOptions);
    }

    internal TemporaryDirectory Directory { get; }
    internal string OutputDirectory { get; }
    internal PhotonCadArtifactContext Context { get; }
    internal MutableContextAuthority ContextAuthority { get; }
    internal ManualTimeProvider Clock { get; }
    internal FakeArtifactSource Source { get; }
    internal QueueDestinationPicker Picker { get; }
    internal PhotonCadArtifactStorage Storage { get; }
    internal WindowsPhotonCadArtifactDestinationAuthority Destinations { get; }
    internal PhotonCadArtifactReleaseOptions ReleaseOptions { get; }
    internal PhotonCadArtifactReleaseCoordinator Coordinator { get; }
    internal PhotonCadArtifactResourceResponder Responder { get; }

    internal async ValueTask<PhotonCadArtifactDescriptor> SealAsync(string id, byte[] content)
    {
        Source.Register(id, content);
        return await Storage.SealAsync(new PhotonCadArtifactSourceRequest(id, Context)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await Coordinator.DisposeAsync().ConfigureAwait(false);
        Directory.Dispose();
    }
}
