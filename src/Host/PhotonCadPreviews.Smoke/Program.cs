using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using PhotonCadPreviews;
using PhotonCadProjects;
using PhotonCadProjects.Codec;

var tests = new (string Name, Action Run)[]
{
    ("seal committed canonical GLB and serve one exact resource", SealAndServe),
    ("reject foreign resource routes and replay", RejectRoutesAndReplay),
    ("reject dirty foreign and malformed canonical previews", RejectInvalidReadback),
    ("bind resolve to exact renderer generation session project revision and digest", RejectForeignResolve),
    ("expire resources on monotonic time despite UTC rollback", MonotonicExpiry),
    ("revoke project generation reset and active streams", Revocation),
    ("evict bounded preview custody without affecting canonical projects", Capacity),
    ("bound outstanding resources and active streams", ResourceCapacity),
    ("concurrent dispose drains leases without semaphore races", ConcurrentDispose),
    ("dispose idempotently and fail closed", DisposeIdempotently),
};

var started = DateTimeOffset.UtcNow;
foreach (var (name, run) in tests)
{
    run();
    Console.WriteLine($"PASS {name}");
}
Console.WriteLine($"PhotonCadPreviews smoke passed {tests.Length}/{tests.Length} in {(DateTimeOffset.UtcNow - started).TotalMilliseconds:F0} ms.");

static void SealAndServe()
{
    using var fixture = CreateFixture();
    var receipt = fixture.Custody.SealCommitted(new(fixture.Context, fixture.Project));
    Equal(fixture.Context.ProjectId, receipt.Context.ProjectId, "project binding");
    Equal(2L, receipt.Context.Revision, "revision binding");
    Equal(1, receipt.EntityCount, "entity count");
    var asset = fixture.Custody.Resolve(new("resolve-1", fixture.Context, receipt.PreviewId, receipt.ContentDigest, 1024 * 1024));
    Require(asset.Url.AbsolutePath.StartsWith("/api/photon-cad/previews/", StringComparison.Ordinal), "fixed resource path");
    Require(!asset.Url.ToString().Contains(fixture.Context.ProjectId, StringComparison.Ordinal), "URL is opaque");
    using var response = fixture.Custody.TryRespond(new("GET", asset.Url, true, fixture.Context))!;
    Equal(200, response.StatusCode, "resource status");
    Equal(PhotonCadPreviewContract.MediaType, response.Headers["Content-Type"], "media type");
    Equal("no-store", response.Headers["Cache-Control"], "cache policy");
    using var memory = new MemoryStream();
    response.Content!.CopyTo(memory);
    Require(memory.ToArray().SequenceEqual(fixture.Glb), "exact GLB bytes");
}

static void RejectRoutesAndReplay()
{
    using var fixture = CreateFixture();
    var receipt = fixture.Custody.SealCommitted(new(fixture.Context, fixture.Project));
    var asset = fixture.Custody.Resolve(new("resolve-2", fixture.Context, receipt.PreviewId, receipt.ContentDigest, 1024 * 1024));
    Require(fixture.Custody.TryRespond(new("GET", new Uri("https://example.test/not-preview"), true, fixture.Context)) is null, "unrelated route ignored");
    using var post = fixture.Custody.TryRespond(new("POST", asset.Url, true, fixture.Context))!;
    Equal(404, post.StatusCode, "POST denied");
    using var range = fixture.Custody.TryRespond(new("GET", asset.Url, true, fixture.Context,
        new Dictionary<string, string> { ["Range"] = "bytes=0-1" }))!;
    Equal(404, range.StatusCode, "range denied");
    using var first = fixture.Custody.TryRespond(new("GET", asset.Url, true, fixture.Context))!;
    Equal(200, first.StatusCode, "first GET");
    using var replay = fixture.Custody.TryRespond(new("GET", asset.Url, true, fixture.Context))!;
    Equal(404, replay.StatusCode, "resource replay denied");
    Require(first.Headers.Values.All(value => !value.Contains("C:\\", StringComparison.OrdinalIgnoreCase)), "headers pathless");
}

static void RejectInvalidReadback()
{
    using var fixture = CreateFixture();
    var dirty = BuildProject("cad-session-1", "project-1", BuildGlb("entity-1"), dirty: true);
    Throws("committed_readback_binding_mismatch", () => fixture.Custody.SealCommitted(new(fixture.Context, dirty)));
    var malformed = BuildProject("cad-session-1", "project-1", BuildGlb("foreign-entity"), dirty: false);
    Throws("glb_entity_binding_mismatch", () => fixture.Custody.SealCommitted(new(fixture.Context, malformed)));
    var wrong = new PhotonCadPreviewContext(4, "renderer-session-1", "cad-session-1", "project-1", 3);
    Throws("committed_readback_binding_mismatch", () => fixture.Custody.SealCommitted(new(wrong, fixture.Project)));
}

static void RejectForeignResolve()
{
    using var fixture = CreateFixture();
    var receipt = fixture.Custody.SealCommitted(new(fixture.Context, fixture.Project));
    var foreign = new PhotonCadPreviewContext(5, "renderer-session-1", "cad-session-1", "project-1", 2);
    Throws("preview_unavailable", () => fixture.Custody.Resolve(new("resolve-3", foreign, receipt.PreviewId, receipt.ContentDigest, 1024 * 1024)));
    Throws("preview_unavailable", () => fixture.Custody.Resolve(new("resolve-4", fixture.Context, receipt.PreviewId,
        "sha256:" + new string('a', 64), 1024 * 1024)));
    Throws("preview_unavailable", () => fixture.Custody.Resolve(new("resolve-5", fixture.Context, receipt.PreviewId,
        receipt.ContentDigest, receipt.ByteLength - 1)));
}

static void MonotonicExpiry()
{
    var clock = new ManualTimeProvider();
    using var fixture = CreateFixture(clock, previewTtl: TimeSpan.FromSeconds(30), resourceTtl: TimeSpan.FromSeconds(15));
    var receipt = fixture.Custody.SealCommitted(new(fixture.Context, fixture.Project));
    var asset = fixture.Custody.Resolve(new("resolve-6", fixture.Context, receipt.PreviewId, receipt.ContentDigest, 1024 * 1024));
    clock.RollUtcBack(TimeSpan.FromDays(30));
    clock.AdvanceMonotonic(TimeSpan.FromSeconds(16));
    using var expired = fixture.Custody.TryRespond(new("GET", asset.Url, true, fixture.Context))!;
    Equal(404, expired.StatusCode, "monotonic expiry");
}

static void Revocation()
{
    using var fixture = CreateFixture();
    var receipt = fixture.Custody.SealCommitted(new(fixture.Context, fixture.Project));
    var asset = fixture.Custody.Resolve(new("resolve-7", fixture.Context, receipt.PreviewId, receipt.ContentDigest, 1024 * 1024));
    using var live = fixture.Custody.TryRespond(new("GET", asset.Url, true, fixture.Context))!;
    Equal(200, live.StatusCode, "active resource");
    fixture.Custody.RevokeProject(fixture.Context);
    ThrowsException<ObjectDisposedException>(() => live.Content!.ReadByte());

    receipt = fixture.Custody.SealCommitted(new(fixture.Context, fixture.Project));
    fixture.Custody.RevokeRendererGeneration(fixture.Context.RendererGeneration, fixture.Context.RendererSessionId);
    Throws("preview_unavailable", () => fixture.Custody.Resolve(new("resolve-8", fixture.Context, receipt.PreviewId, receipt.ContentDigest, 1024 * 1024)));

    receipt = fixture.Custody.SealCommitted(new(fixture.Context, fixture.Project));
    fixture.Custody.Reset();
    Throws("preview_unavailable", () => fixture.Custody.Resolve(new("resolve-9", fixture.Context, receipt.PreviewId, receipt.ContentDigest, 1024 * 1024)));
}

static void Capacity()
{
    var clock = new ManualTimeProvider();
    var codec = new PhotonCadCanonicalProjectCodecV1();
    var options = new PhotonCadPreviewCustodyOptions(new Uri("https://127.0.0.1:9119/"), maximumPreviews: 1,
        maximumTotalBytes: PhotonCadPreviewContract.DefaultMaximumPreviewBytes);
    var custody = new PhotonCadPreviewCustody(codec, options, clock);
    try
    {
        var firstContext = new PhotonCadPreviewContext(4, "renderer-session-1", "cad-session-1", "project-1", 2);
        var first = custody.SealCommitted(new(firstContext, BuildProject("cad-session-1", "project-1", BuildGlb("entity-1"), false, "entity-1")));
        var secondContext = new PhotonCadPreviewContext(4, "renderer-session-1", "cad-session-2", "project-2", 2);
        _ = custody.SealCommitted(new(secondContext, BuildProject("cad-session-2", "project-2", BuildGlb("entity-2"), false, "entity-2")));
        Throws("preview_unavailable", () => custody.Resolve(new("resolve-10", firstContext, first.PreviewId, first.ContentDigest, 1024 * 1024)));
    }
    finally { custody.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
}

static void DisposeIdempotently()
{
    var fixture = CreateFixture();
    fixture.Custody.DisposeAsync().AsTask().GetAwaiter().GetResult();
    fixture.Custody.DisposeAsync().AsTask().GetAwaiter().GetResult();
    Throws("preview_custody_disposed", () => fixture.Custody.SealCommitted(new(fixture.Context, fixture.Project)));
    fixture.Dispose();
}

static void ResourceCapacity()
{
    var codec = new PhotonCadCanonicalProjectCodecV1();
    var options = new PhotonCadPreviewCustodyOptions(new Uri("https://127.0.0.1:9119/"),
        maximumConcurrentOperations: 1, maximumOutstandingResources: 2);
    var custody = new PhotonCadPreviewCustody(codec, options);
    try
    {
        var context = new PhotonCadPreviewContext(4, "renderer-session-1", "cad-session-1", "project-1", 2);
        var receipt = custody.SealCommitted(new(context, BuildProject("cad-session-1", "project-1", BuildGlb("entity-1"), false, "entity-1")));
        var first = custody.Resolve(new("resolve-cap-1", context, receipt.PreviewId, receipt.ContentDigest, 1024 * 1024));
        _ = custody.Resolve(new("resolve-cap-2", context, receipt.PreviewId, receipt.ContentDigest, 1024 * 1024));
        Throws("preview_resource_capacity_exceeded", () => custody.Resolve(new("resolve-cap-3", context, receipt.PreviewId, receipt.ContentDigest, 1024 * 1024)));
        using var live = custody.TryRespond(new("GET", first.Url, true, context))!;
        Equal(200, live.StatusCode, "first active stream");
        var second = custody.Resolve(new("resolve-cap-4", context, receipt.PreviewId, receipt.ContentDigest, 1024 * 1024));
        using var held = custody.TryRespond(new("GET", second.Url, true, context))!;
        Equal(404, held.StatusCode, "second active stream denied while first is open");
        live.Dispose();
        using var retried = custody.TryRespond(new("GET", second.Url, true, context))!;
        Equal(200, retried.StatusCode, "resource remains available after bounded stream refusal");
    }
    finally { custody.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
}

static void ConcurrentDispose()
{
    var codec = new PhotonCadCanonicalProjectCodecV1();
    var options = new PhotonCadPreviewCustodyOptions(new Uri("https://127.0.0.1:9119/"),
        maximumConcurrentOperations: 8, maximumOutstandingResources: 256);
    var custody = new PhotonCadPreviewCustody(codec, options);
    var context = new PhotonCadPreviewContext(4, "renderer-session-1", "cad-session-1", "project-1", 2);
    var receipt = custody.SealCommitted(new(context, BuildProject("cad-session-1", "project-1", BuildGlb("entity-1"), false, "entity-1")));
    var unexpected = new ConcurrentQueue<Exception>();
    var workers = Enumerable.Range(0, 16).Select(worker => Task.Run(() =>
    {
        for (var index = 0; index < 64; index++)
        {
            try
            {
                _ = custody.Resolve(new($"dispose-{worker}-{index}", context, receipt.PreviewId,
                    receipt.ContentDigest, 1024 * 1024));
            }
            catch (PhotonCadPreviewException exception) when (exception.Code is "preview_custody_disposed"
                or "preview_resource_capacity_exceeded" or "preview_unavailable" or "preview_capacity_exceeded")
            { }
            catch (Exception exception) { unexpected.Enqueue(exception); }
        }
    })).ToArray();
    var disposer = Task.Run(() => custody.DisposeAsync().AsTask());
    Task.WaitAll(workers.Append(disposer).ToArray());
    Require(unexpected.IsEmpty, "concurrent dispose leaked an unexpected exception");
    custody.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

static Fixture CreateFixture(TimeProvider? clock = null, TimeSpan? previewTtl = null, TimeSpan? resourceTtl = null)
{
    var glb = BuildGlb("entity-1");
    var options = new PhotonCadPreviewCustodyOptions(new Uri("https://127.0.0.1:9119/"),
        previewTimeToLive: previewTtl, resourceTimeToLive: resourceTtl);
    return new Fixture(new PhotonCadPreviewCustody(new(), options, clock),
        new PhotonCadPreviewContext(4, "renderer-session-1", "cad-session-1", "project-1", 2),
        BuildProject("cad-session-1", "project-1", glb, false), glb);
}

static PhotonCadCanonicalProject BuildProject(string sessionId, string projectId, byte[] glb, bool dirty, string entityId = "entity-1")
{
    var codec = new PhotonCadCanonicalProjectCodecV1();
    var digest = "sha256:" + new string('1', 64);
    var source = new PhotonCadSourceIdentityV1("photon-cad-industrial", "0.1.0", digest, "redistribution-blocked");
    var create = new PhotonCadOperationV1(0, "operation-create", "geometry.box.create.v1", "Create box",
        new DateTimeOffset(2026, 8, 11, 0, 0, 0, TimeSpan.Zero), PhotonCadOperationStateV1.Applied,
        PhotonCadOperationModeV1.Scratch, [], [entityId], source);
    var preview = new PhotonCadOperationV1(1, "operation-preview", "industrial.preview.glb.v1", "Create preview",
        new DateTimeOffset(2026, 8, 11, 0, 0, 1, TimeSpan.Zero), PhotonCadOperationStateV1.Applied,
        PhotonCadOperationModeV1.Scratch, [], [entityId], source);
    var stepProvenance = new PhotonCadArtifactProvenanceV1(PhotonCadBackendV1.Assembly, "industrial-bundle", digest,
        create.CapabilityId, create.Id, source);
    var previewProvenance = new PhotonCadArtifactProvenanceV1(PhotonCadBackendV1.Assembly, "industrial-bundle", digest,
        preview.CapabilityId, preview.Id, source);
    var state = new PhotonCadProjectStateV1(sessionId, projectId, 2, "Smoke project", PhotonCadProjectUnit.Millimeter,
        [new PhotonCadEntityV1(entityId, null, PhotonCadEntityKindV1.Body, "Box", true, false, create.CapabilityId)],
        [create, preview],
        [new PhotonCadOccurrenceV1(entityId + "-occ", null, "BOX", entityId, new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 })],
        [], [],
        [
            new PhotonCadArtifactV1(PhotonCadArtifactRoleV1.AuthoritativeGeometry, PhotonCadArtifactKindV1.Step,
                entityId, 1, Encoding.ASCII.GetBytes("ISO-10303-21;\nHEADER;\nENDSEC;\nDATA;\nENDSEC;\nEND-ISO-10303-21;\n"), null, stepProvenance),
            new PhotonCadArtifactV1(PhotonCadArtifactRoleV1.ProjectPreview, PhotonCadArtifactKindV1.Glb,
                null, 2, glb, new PhotonCadBoundsV1(new(0,0,0), new(10,20,30)), previewProvenance),
        ], dirty);
    return codec.Encode(state);
}

static byte[] BuildGlb(string entityId)
{
    var json = JsonSerializer.SerializeToUtf8Bytes(new
    {
        asset = new { version = "2.0" },
        scene = 0,
        scenes = new[] { new { nodes = new[] { 0 } } },
        nodes = new[] { new { mesh = 0, extras = new { photonEntityId = entityId } } },
        meshes = new[] { new { primitives = new[] { new { attributes = new { POSITION = 0 } } } } },
        accessors = new[] { new { bufferView = 0, componentType = 5126, count = 1, type = "VEC3" } },
        bufferViews = new[] { new { buffer = 0, byteOffset = 0, byteLength = 12 } },
        buffers = new[] { new { byteLength = 12 } },
    });
    var jsonLength = (json.Length + 3) & ~3;
    var binLength = 12;
    var output = new byte[12 + 8 + jsonLength + 8 + binLength];
    BinaryPrimitives.WriteUInt32LittleEndian(output, 0x46546C67);
    BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4), 2);
    BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(8), checked((uint)output.Length));
    BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(12), checked((uint)jsonLength));
    BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(16), 0x4E4F534A);
    json.CopyTo(output.AsSpan(20));
    output.AsSpan(20 + json.Length, jsonLength - json.Length).Fill(0x20);
    var binaryHeader = 20 + jsonLength;
    BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(binaryHeader), checked((uint)binLength));
    BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(binaryHeader + 4), 0x004E4942);
    return output;
}

static void Throws(string code, Action action)
{
    try { action(); }
    catch (PhotonCadPreviewException exception) when (exception.Code == code) { return; }
    throw new InvalidOperationException($"Expected {code}.");
}

static void ThrowsException<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

static void Require(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

static void Equal<T>(T expected, T actual, string message) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
}

sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _utc = new(2026, 8, 11, 0, 0, 0, TimeSpan.Zero);
    private long _timestamp;
    public override DateTimeOffset GetUtcNow() => _utc;
    public override long GetTimestamp() => _timestamp;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    internal void AdvanceMonotonic(TimeSpan value) => _timestamp += value.Ticks;
    internal void RollUtcBack(TimeSpan value) => _utc -= value;
}

sealed class Fixture : IDisposable
{
    internal Fixture(PhotonCadPreviewCustody custody, PhotonCadPreviewContext context, PhotonCadCanonicalProject project, byte[] glb)
    { Custody = custody; Context = context; Project = project; Glb = glb; }
    internal PhotonCadPreviewCustody Custody { get; }
    internal PhotonCadPreviewContext Context { get; }
    internal PhotonCadCanonicalProject Project { get; }
    internal byte[] Glb { get; }
    public void Dispose() => Custody.DisposeAsync().AsTask().GetAwaiter().GetResult();
}
