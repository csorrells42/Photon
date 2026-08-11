using PhotonCadProjects;
using PhotonCadProjects.Codec;
using PhotonCadVerification;
using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var tests = new (string Name, Func<Task> Run)[]
{
    ("canonical assembly verifies with unavailable geometry checks", CanonicalAssemblyAsync),
    ("seekable bounded stream verifies without ownership transfer", StreamAsync),
    ("canonical byte tamper fails closed", ByteTamperAsync),
    ("custody digest mismatch fails closed", CustodyMismatchAsync),
    ("stale BOM recomputation fails", StaleBomAsync),
    ("ambiguous BOM source fails as a check", AmbiguousBomAsync),
    ("unplaced sealed project part remains verifiable", UnplacedProjectPartAsync),
    ("preview entity tag mismatch fails", PreviewTagAsync),
    ("occurrence cycle is rejected during canonical decode", CycleAsync),
    ("non-rigid transform is rejected during canonical decode", TransformAsync),
    ("cancellation is honored before work", CancellationAsync),
    ("oversize stream is rejected before allocation", OversizeAsync),
    ("preview absence is explicit unavailable", PreviewUnavailableAsync),
    ("public report rejects contradictory truth flags", ContradictoryReportAsync),
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{test.Name}: {exception.GetType().Name}: {exception.Message}");
        Console.Error.WriteLine($"FAIL {failures[^1]}");
    }
}
Console.WriteLine($"SUMMARY {tests.Length - failures.Count}/{tests.Length}");
return failures.Count == 0 ? 0 : 1;

static Task CanonicalAssemblyAsync()
{
    var fixture = Fixture();
    var report = Verify(fixture);
    True(report.Verified, "canonical assembly not verified");
    True(!report.Complete, "container-required checks were falsely complete");
    Equal(10, report.Checks.Count(value => value.Status == PhotonCadVerificationStatus.Passed), "passed check count");
    Equal(3, report.Checks.Count(value => value.Status == PhotonCadVerificationStatus.Unavailable), "unavailable check count");
    return Task.CompletedTask;
}

static async Task StreamAsync()
{
    var fixture = Fixture();
    using var stream = new MemoryStream(fixture.Project.CanonicalBytes.ToArray(), writable: false);
    var report = await new PhotonCadCanonicalVerifier().VerifyAsync(stream, Request(fixture.Project));
    True(report.Verified, "stream verification failed");
    True(stream.CanRead, "verification took stream ownership");
    Equal(stream.Length, stream.Position, "stream was not consumed exactly");
}

static Task ByteTamperAsync()
{
    var fixture = Fixture();
    var bytes = fixture.Project.CanonicalBytes.ToArray();
    bytes[^1] ^= 0x01;
    var report = new PhotonCadCanonicalVerifier().Verify(bytes, Request(fixture.Project));
    True(!report.Verified, "tampered canonical bytes verified");
    return Task.CompletedTask;
}

static Task CustodyMismatchAsync()
{
    var fixture = Fixture();
    var request = new PhotonCadVerificationRequest(
        fixture.Project.CanonicalBytes.Length,
        $"sha256:{new string('0', 64)}",
        fixture.Project.ContentDigest,
        fixture.Project.BomDigest);
    var report = new PhotonCadCanonicalVerifier().Verify(fixture.Project.CanonicalBytes, request);
    Equal("canonical_file_digest_mismatch", report.Reason, "wrong custody rejection");
    return Task.CompletedTask;
}

static Task StaleBomAsync()
{
    var fixture = Fixture(bomQuantityForSecond: 2);
    var report = Verify(fixture);
    Failed(report, "bom_consistency");
    return Task.CompletedTask;
}

static Task AmbiguousBomAsync()
{
    var fixture = Fixture(duplicateBomSource: true);
    var report = Verify(fixture);
    Failed(report, "bom_consistency");
    return Task.CompletedTask;
}

static Task UnplacedProjectPartAsync()
{
    var fixture = Fixture(includeSecondOccurrence: false);
    var report = Verify(fixture);
    True(report.Verified, "unplaced sealed project part should remain available for later assembly placement");
    Equal(PhotonCadVerificationStatus.Passed,
        report.Checks.Single(value => value.Id == "occurrence_source_coverage").Status,
        "unplaced project part coverage status");
    return Task.CompletedTask;
}

static Task PreviewTagAsync()
{
    var fixture = Fixture(previewSecondId: "wrong.occ");
    var report = Verify(fixture);
    Failed(report, "preview_entity_coverage");
    return Task.CompletedTask;
}

static Task CycleAsync()
{
    var fixture = Fixture();
    var root = fixture.State.Occurrences[0];
    SetBackingField(root, "<ParentOccurrenceId>k__BackingField", fixture.State.Occurrences[1].OccurrenceId);
    Throws<PhotonCadProjectException>(() => new PhotonCadCanonicalProjectCodecV1().Encode(fixture.State));
    return Task.CompletedTask;
}

static Task TransformAsync()
{
    var fixture = Fixture();
    var field = typeof(PhotonCadOccurrenceV1).GetField("_transform", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("transform field missing");
    var transform = (double[])(field.GetValue(fixture.State.Occurrences[1])
        ?? throw new InvalidOperationException("transform unavailable"));
    transform[0] = 2;
    Throws<PhotonCadProjectException>(() => new PhotonCadCanonicalProjectCodecV1().Encode(fixture.State));
    return Task.CompletedTask;
}

static Task CancellationAsync()
{
    var fixture = Fixture();
    using var source = new CancellationTokenSource();
    source.Cancel();
    Throws<OperationCanceledException>(() =>
        new PhotonCadCanonicalVerifier().Verify(fixture.Project.CanonicalBytes, Request(fixture.Project), source.Token));
    return Task.CompletedTask;
}

static async Task OversizeAsync()
{
    using var stream = new LengthOnlyStream(PhotonCadVerificationContract.MaximumCanonicalBytes + 1L);
    var request = new PhotonCadVerificationRequest(1, $"sha256:{new string('1', 64)}",
        $"sha256:{new string('2', 64)}", $"sha256:{new string('3', 64)}");
    var report = await new PhotonCadCanonicalVerifier().VerifyAsync(stream, request);
    Equal("canonical_length_mismatch", report.Reason, "oversize stream rejection");
    Equal(0, stream.ReadCount, "oversize stream was read");
}

static Task PreviewUnavailableAsync()
{
    var fixture = Fixture(includePreview: false);
    var report = Verify(fixture);
    True(report.Verified, "preview-less canonical project should pass available checks");
    var preview = report.Checks.Single(value => value.Id == "preview_entity_coverage");
    Equal(PhotonCadVerificationStatus.Unavailable, preview.Status, "preview absence status");
    return Task.CompletedTask;
}

static Task ContradictoryReportAsync()
{
    Throws<ArgumentException>(() => new PhotonCadVerificationReport(
        verified: true,
        complete: true,
        "passed",
        "pcpid:test",
        1,
        [new PhotonCadVerificationCheck("test", PhotonCadVerificationStatus.Failed, "failed", 1)]));
    return Task.CompletedTask;
}

static PhotonCadVerificationReport Verify(FixtureValue fixture) =>
    new PhotonCadCanonicalVerifier().Verify(fixture.Project.CanonicalBytes, Request(fixture.Project));

static PhotonCadVerificationRequest Request(PhotonCadCanonicalProject project) => new(
    project.CanonicalBytes.Length,
    Sha256(project.CanonicalBytes.Span),
    project.ContentDigest,
    project.BomDigest);

static FixtureValue Fixture(
    double bomQuantityForSecond = 1,
    bool includeSecondOccurrence = true,
    string previewSecondId = "child.occ",
    bool includePreview = true,
    bool duplicateBomSource = false)
{
    var codec = new PhotonCadCanonicalProjectCodecV1();
    var source = new PhotonCadSourceIdentityV1("fixture", "1.0", Hex('a'), "Apache-2.0");
    var firstStep = Step("first");
    var secondStep = Step("second");
    var rootTransform = Identity();
    var childTransform = Translation(25, 5, 0);
    var entities = new[]
    {
        new PhotonCadEntityV1("root-part", null, PhotonCadEntityKindV1.Part, "Root part", true, false, "geometry.create.v1"),
        new PhotonCadEntityV1("child-part", null, PhotonCadEntityKindV1.Part, "Child part", true, false, "geometry.create.v1"),
    };
    var occurrences = new List<PhotonCadOccurrenceV1>
    {
        new("root.occ", null, "ROOT-001", "root-part", rootTransform),
    };
    if (includeSecondOccurrence)
        occurrences.Add(new("child.occ", "root.occ", "CHILD-001", "child-part", childTransform));
    var bom = new List<PhotonCadBomRow>
    {
        new("ROOT-001", "Root part", 1, PhotonCadBomUnit.Each, "root-part"),
    };
    if (includeSecondOccurrence)
        bom.Add(new("CHILD-001", "Child part", bomQuantityForSecond, PhotonCadBomUnit.Each, "child-part"));
    if (duplicateBomSource)
        bom.Add(new("ROOT-ALT", "Conflicting root", 1, PhotonCadBomUnit.Each, "root-part"));
    var operationCount = includePreview ? 3 : 2;
    var operations = Enumerable.Range(0, operationCount).Select(index => new PhotonCadOperationV1(
        index,
        $"op-{index + 1}",
        index == 2 ? "preview.create.v1" : "geometry.create.v1",
        $"Operation {index + 1}",
        DateTimeOffset.UnixEpoch.AddSeconds(index + 1),
        PhotonCadOperationStateV1.Applied,
        PhotonCadOperationModeV1.Scratch,
        [],
        index == 0 ? ["root-part"] : index == 1 ? ["child-part"] : [],
        source)).ToArray();
    var artifacts = new List<PhotonCadArtifactV1>
    {
        Artifact(PhotonCadArtifactRoleV1.AuthoritativeGeometry, PhotonCadArtifactKindV1.Step,
            "root-part", 1, firstStep, null, operations[0], source),
        Artifact(PhotonCadArtifactRoleV1.AuthoritativeGeometry, PhotonCadArtifactKindV1.Step,
            "child-part", 2, secondStep, null, operations[1], source),
    };
    var revision = 2L;
    if (includePreview)
    {
        var nodes = new List<(string Id, double[] Matrix)>
        {
            ("root.occ", rootTransform),
        };
        if (includeSecondOccurrence) nodes.Add((previewSecondId, childTransform));
        var glb = Glb(nodes);
        revision = 3;
        artifacts.Add(Artifact(PhotonCadArtifactRoleV1.ProjectPreview, PhotonCadArtifactKindV1.Glb,
            null, revision, glb,
            new PhotonCadBoundsV1(new PhotonCadVector3V1(0, 0, 0), new PhotonCadVector3V1(100, 100, 100)),
            operations[2], source));
    }
    var state = new PhotonCadProjectStateV1(
        "pcsid:verification-fixture",
        "pcpid:verification-fixture",
        revision,
        "Verification fixture",
        PhotonCadProjectUnit.Millimeter,
        entities,
        operations,
        occurrences,
        [],
        bom,
        artifacts,
        dirty: false);
    return new(state, codec.Encode(state));
}

static PhotonCadArtifactV1 Artifact(
    PhotonCadArtifactRoleV1 role,
    PhotonCadArtifactKindV1 kind,
    string? owner,
    long revision,
    byte[] content,
    PhotonCadBoundsV1? bounds,
    PhotonCadOperationV1 operation,
    PhotonCadSourceIdentityV1 source) => new(
        role,
        kind,
        owner,
        revision,
        content,
        bounds,
        new PhotonCadArtifactProvenanceV1(
            PhotonCadBackendV1.Geometry,
            "bundle.fixture",
            Hex('b'),
            operation.CapabilityId,
            operation.Id,
            source));

static byte[] Step(string name) => Encoding.ASCII.GetBytes(
    $"ISO-10303-21;\nHEADER;\nFILE_DESCRIPTION(('{name}'),'2;1');\nENDSEC;\nDATA;\n#1=PRODUCT('{name}','','',());\nENDSEC;\nEND-ISO-10303-21;");

static byte[] Glb(IReadOnlyList<(string Id, double[] Matrix)> values)
{
    var nodes = values.Select((value, index) => new
    {
        extras = new { photonEntityId = value.Id },
        mesh = index,
        matrix = ToGlb(value.Matrix),
        children = index == 0 && values.Count > 1 ? new[] { 1 } : null,
    }).ToArray();
    var payload = JsonSerializer.SerializeToUtf8Bytes(new
    {
        asset = new { version = "2.0" },
        scene = 0,
        scenes = new[] { new { nodes = new[] { 0 } } },
        nodes,
        meshes = values.Select(_ => new { primitives = Array.Empty<object>() }).ToArray(),
        buffers = new[] { new { byteLength = 4 } },
    }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
    var jsonLength = (payload.Length + 3) & ~3;
    var total = 12 + 8 + jsonLength + 8 + 4;
    var bytes = new byte[total];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x46546c67);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 2);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), checked((uint)total));
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), checked((uint)jsonLength));
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 0x4e4f534a);
    payload.CopyTo(bytes.AsSpan(20));
    bytes.AsSpan(20 + payload.Length, jsonLength - payload.Length).Fill(0x20);
    var binHeader = 20 + jsonLength;
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(binHeader), 4);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(binHeader + 4), 0x004e4942);
    return bytes;
}

static double[] ToGlb(IReadOnlyList<double> rowMajor)
{
    var result = new double[16];
    for (var column = 0; column < 4; column++)
    {
        for (var row = 0; row < 4; row++) result[(column * 4) + row] = rowMajor[(row * 4) + column];
    }
    return result;
}

static double[] Identity() =>
[
    1, 0, 0, 0,
    0, 1, 0, 0,
    0, 0, 1, 0,
    0, 0, 0, 1,
];

static double[] Translation(double x, double y, double z) =>
[
    1, 0, 0, x,
    0, 1, 0, y,
    0, 0, 1, z,
    0, 0, 0, 1,
];

static string Hex(char value) => $"sha256:{new string(value, 64)}";
static string Sha256(ReadOnlySpan<byte> value) => $"sha256:{Convert.ToHexStringLower(SHA256.HashData(value))}";

static void Failed(PhotonCadVerificationReport report, string id)
{
    True(!report.Verified, $"{id} failure did not fail report");
    Equal(PhotonCadVerificationStatus.Failed, report.Checks.Single(value => value.Id == id).Status, $"{id} status");
}

static void SetBackingField(object target, string name, object? value)
{
    var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"field {name} missing");
    field.SetValue(target, value);
}

static void True(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

static void Equal<T>(T expected, T actual, string message) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{message}: expected {expected}, actual {actual}");
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"expected {typeof(T).Name}");
}

internal sealed record FixtureValue(PhotonCadProjectStateV1 State, PhotonCadCanonicalProject Project);

internal sealed class LengthOnlyStream(long length) : Stream
{
    public int ReadCount { get; private set; }
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get; set; }
    public override int Read(byte[] buffer, int offset, int count) { ReadCount++; return 0; }
    public override int Read(Span<byte> buffer) { ReadCount++; return 0; }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() { }
}
