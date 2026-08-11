using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using PhotonCadProjects;
using PhotonCadProjects.Codec;

const string GoldenFileSha256 = "8921d3e33bb310bbc83f6ac1822b5a64d142be406319c290c8767d93e137d22f";
var tests = new (string Name, Action Run)[]
{
    ("deterministic create", DeterministicCreate),
    ("canonical roundtrip", CanonicalRoundtrip),
    ("golden file bytes", GoldenFileBytes),
    ("mark saved preserves identity", MarkSavedPreservesIdentity),
    ("wrapper tampering rejected", WrapperTamperingRejected),
    ("duplicate JSON rejected", DuplicateJsonRejected),
    ("reordered JSON rejected", ReorderedJsonRejected),
    ("unknown JSON rejected", UnknownJsonRejected),
    ("invalid UTF-8 rejected", InvalidUtf8Rejected),
    ("noncanonical Unicode rejected", NoncanonicalUnicodeRejected),
    ("control character rejected", ControlCharacterRejected),
    ("numeric bits deterministic", NumericBitsDeterministic),
    ("nonfinite numeric rejected", NonfiniteNumericRejected),
    ("manifest tampering rejected", ManifestTamperingRejected),
    ("blob tampering rejected", BlobTamperingRejected),
    ("trailing data rejected", TrailingDataRejected),
    ("malicious lengths rejected", MaliciousLengthsRejected),
    ("layout cap arithmetic", LayoutCapArithmetic),
    ("duplicate entity rejected", DuplicateEntityRejected),
    ("entity graph rejected", EntityGraphRejected),
    ("nonrigid transform rejected", NonrigidTransformRejected),
    ("occurrence root count enforced", OccurrenceRootCountEnforced),
    ("geometry artifact required", GeometryArtifactRequired),
    ("artifact operation binding", ArtifactOperationBinding),
    ("BOM binding rejected", BomBindingRejected),
    ("BOM digest tampering rejected", BomDigestTamperingRejected),
    ("STEP structure rejected", StepStructureRejected),
    ("GLB external resource rejected", GlbExternalResourceRejected),
    ("translation convention enforced", TranslationConventionEnforced),
    ("ZIP input rejected", ZipInputRejected),
    ("path-like title rejected", PathLikeTitleRejected),
    ("opaque runtime handle rejected", OpaqueRuntimeHandleRejected),
    ("canonical schema path free", CanonicalSchemaPathFree),
    ("forbidden dependency scan", ForbiddenDependencyScan),
};

var failures = new List<string>();
var started = System.Diagnostics.Stopwatch.StartNew();
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{test.Name}: {exception.GetType().Name}: {exception.Message}");
        Console.WriteLine($"FAIL {test.Name}");
    }
}
started.Stop();
Console.WriteLine($"RESULT {tests.Length - failures.Count}/{tests.Length} in {started.Elapsed.TotalMilliseconds:F1} ms");
foreach (var failure in failures) Console.Error.WriteLine(failure);
return failures.Count == 0 ? 0 : 1;

void DeterministicCreate()
{
    var codec = Codec();
    var first = codec.CreateAsync("Photon Gearbox", PhotonCadProjectUnit.Millimeter).AsTask().GetAwaiter().GetResult();
    var second = codec.CreateAsync("Photon Gearbox", PhotonCadProjectUnit.Millimeter).AsTask().GetAwaiter().GetResult();
    EqualBytes(first.CanonicalBytes.Span, second.CanonicalBytes.Span, "fixed identity create bytes");
    Equal(128 * 1024 * 1024, codec.MaximumEncodedBytes, "operational cap");
    Equal(0L, first.Revision, "new revision");
    False(first.Dirty, "new project is clean");
}

void CanonicalRoundtrip()
{
    var codec = Codec();
    var encoded = codec.Encode(SampleState());
    var decoded = codec.Decode(encoded.CanonicalBytes);
    EqualBytes(encoded.CanonicalBytes.Span, decoded.CanonicalBytes.Span, "roundtrip bytes");
    Equal(encoded.ContentDigest, decoded.ContentDigest, "logical digest");
    Equal(encoded.BomDigest, decoded.BomDigest, "BOM digest");
    True(encoded.Dirty, "runtime state remains dirty before save");
    False(decoded.Dirty, "disk decode is clean");
    True(codec.Inspect(encoded).Dirty, "inspect preserves wrapper dirty state");
}

void GoldenFileBytes()
{
    var file = Codec().Encode(SampleState()).CanonicalBytes;
    var actual = Convert.ToHexStringLower(SHA256.HashData(file.Span));
    Console.WriteLine($"GOLDEN {actual} {file.Length}");
    Equal(GoldenFileSha256, actual, "golden SHA-256");
}

void MarkSavedPreservesIdentity()
{
    var codec = Codec();
    var current = codec.Encode(SampleState());
    var saved = codec.MarkSaved(current);
    False(saved.Dirty, "saved state");
    EqualBytes(current.CanonicalBytes.Span, saved.CanonicalBytes.Span, "saved bytes");
    Equal(current.ContentDigest, saved.ContentDigest, "saved logical digest");
    Equal(current.BomDigest, saved.BomDigest, "saved BOM digest");
    EqualBytes(saved.CanonicalBytes.Span, codec.MarkSaved(saved).CanonicalBytes.Span, "idempotent save");
}

void WrapperTamperingRejected()
{
    var codec = Codec();
    var current = codec.Encode(SampleState());
    var mismatched = new PhotonCadCanonicalProject(
        current.SessionId,
        current.ProjectId,
        current.Revision,
        "Different title",
        current.Units,
        current.ContentDigest,
        current.BomDigest,
        true,
        current.CanonicalBytes,
        current.Bom);
    ExpectFailure(() => codec.MarkSaved(mismatched), "wrapper mismatch");
}

void DuplicateJsonRejected()
{
    var fixture = Fixture();
    var text = Encoding.UTF8.GetString(fixture.Manifest.Bytes);
    var mutated = text.Replace("{\"schema\":", "{\"schema\":\"photon.cad.project\",\"schema\":", StringComparison.Ordinal);
    ExpectFailure(() => DecodeManifestMutation(fixture, Encoding.UTF8.GetBytes(mutated)), "duplicate property");
}

void ReorderedJsonRejected()
{
    var fixture = Fixture();
    var text = Encoding.UTF8.GetString(fixture.Manifest.Bytes);
    const string canonical = "{\"schema\":\"photon.cad.project\",\"formatVersion\":1,";
    const string reordered = "{\"formatVersion\":1,\"schema\":\"photon.cad.project\",";
    True(text.StartsWith(canonical, StringComparison.Ordinal), "fixture prefix");
    ExpectFailure(() => DecodeManifestMutation(fixture, Encoding.UTF8.GetBytes(reordered + text[canonical.Length..])), "reordered property");
}

void UnknownJsonRejected()
{
    var fixture = Fixture();
    var text = Encoding.UTF8.GetString(fixture.Manifest.Bytes).Replace("\"schema\"", "\"schemx\"", StringComparison.Ordinal);
    ExpectFailure(() => DecodeManifestMutation(fixture, Encoding.UTF8.GetBytes(text)), "unknown property");
}

void InvalidUtf8Rejected()
{
    var fixture = Fixture();
    var manifest = fixture.Manifest.Bytes.ToArray();
    var title = Encoding.UTF8.GetBytes("Café Gearbox");
    var index = manifest.AsSpan().IndexOf(title);
    True(index >= 0, "title exists");
    manifest[index + 3] = 0xff;
    ExpectFailure(() => DecodeManifestMutation(fixture, manifest), "invalid UTF-8");
}

void NoncanonicalUnicodeRejected()
{
    var fixture = Fixture();
    var text = Encoding.UTF8.GetString(fixture.Manifest.Bytes).Replace("Café Gearbox", "Cafe\u0301 Gearbox", StringComparison.Ordinal);
    ExpectFailure(() => DecodeManifestMutation(fixture, Encoding.UTF8.GetBytes(text)), "NFD encoding");
}

void ControlCharacterRejected()
{
    var fixture = Fixture();
    var text = Encoding.UTF8.GetString(fixture.Manifest.Bytes).Replace("Café Gearbox", "Bad\\u0001Title", StringComparison.Ordinal);
    ExpectFailure(() => DecodeManifestMutation(fixture, Encoding.UTF8.GetBytes(text)), "control character");
}

void NumericBitsDeterministic()
{
    var fixture = Fixture();
    var text = Encoding.UTF8.GetString(fixture.Manifest.Bytes);
    True(text.Contains("3ff4000000000000", StringComparison.Ordinal), "1.25 IEEE bits");
    Equal("0000000000000000", PhotonCadFileGuardsV1.F64Hex(-0d, "value"), "negative zero normalization");
    Equal(1.25d, PhotonCadFileGuardsV1.ParseF64Hex("3ff4000000000000", "value"), "IEEE parse");
    ExpectFailure(() => PhotonCadFileGuardsV1.ParseF64Hex("8000000000000000", "value"), "negative zero decode");
}

void NonfiniteNumericRejected()
{
    ExpectFailure(() => PhotonCadInputValueV1.Number(double.NaN), "NaN");
    ExpectFailure(() => PhotonCadInputValueV1.Number(double.PositiveInfinity), "infinity");
}

void ManifestTamperingRejected()
{
    var bytes = Codec().Encode(SampleState()).CanonicalBytes.ToArray();
    bytes[PhotonCadProjectFileV1.HeaderLength + 10] ^= 0x01;
    ExpectFailure(() => Codec().Decode(bytes), "manifest digest");
}

void BlobTamperingRejected()
{
    var bytes = Codec().Encode(SampleState()).CanonicalBytes.ToArray();
    bytes[^1] ^= 0x01;
    ExpectFailure(() => Codec().Decode(bytes), "blob digest");
}

void TrailingDataRejected()
{
    var bytes = Codec().Encode(SampleState()).CanonicalBytes.ToArray();
    Array.Resize(ref bytes, bytes.Length + 1);
    ExpectFailure(() => Codec().Decode(bytes), "trailing data");
}

void MaliciousLengthsRejected()
{
    var bytes = Codec().Encode(SampleState()).CanonicalBytes.ToArray();
    BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(32), ulong.MaxValue);
    ExpectFailure(() => Codec().Decode(bytes), "manifest ulong max");
    bytes = Codec().Encode(SampleState()).CanonicalBytes.ToArray();
    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(40), uint.MaxValue);
    ExpectFailure(() => Codec().Decode(bytes), "blob count max");
}

void LayoutCapArithmetic()
{
    const long manifest = 1;
    var exactPayload = PhotonCadProjectFileV1.MaximumEncodedBytes
        - PhotonCadProjectFileV1.HeaderLength
        - PhotonCadProjectFileV1.BlobHeaderLength
        - manifest;
    Equal((long)PhotonCadProjectFileV1.MaximumEncodedBytes, PhotonCadProjectFramingV1.ValidateDeclaredLayout(manifest, [exactPayload]), "exact cap");
    ExpectFailure(() => PhotonCadProjectFramingV1.ValidateDeclaredLayout(manifest, [exactPayload + 1]), "cap plus one");
    ExpectFailure(() => PhotonCadProjectFramingV1.ValidateDeclaredLayout(long.MaxValue, [1]), "malicious manifest length");
    ExpectFailure(() => PhotonCadProjectFramingV1.ValidateDeclaredLayout(1, [long.MaxValue]), "malicious payload length");
}

void DuplicateEntityRejected()
{
    var state = SampleState();
    var duplicate = new PhotonCadEntityV1("BODY-1", null, PhotonCadEntityKindV1.Datum, "Duplicate", true, false);
    ExpectFailure(() => CopyState(state, entities: state.Entities.Append(duplicate)), "case-insensitive duplicate");
}

void EntityGraphRejected()
{
    var state = SampleState();
    var orphan = new PhotonCadEntityV1("datum-1", "missing-parent", PhotonCadEntityKindV1.Datum, "Orphan", true, false);
    ExpectFailure(() => CopyState(state, entities: state.Entities.Append(orphan)), "missing parent");
}

void NonrigidTransformRejected()
{
    var state = SampleState();
    var matrix = IdentityTransform();
    matrix[0] = 2;
    var invalid = new PhotonCadOccurrenceV1("occurrence-1", null, "P-001", "body-1", matrix);
    ExpectFailure(() => CopyState(state, occurrences: [invalid]), "nonrigid matrix");
}

void OccurrenceRootCountEnforced()
{
    var state = SampleState();
    var secondRoot = new PhotonCadOccurrenceV1("occurrence-2", null, "P-001", "body-1", IdentityTransform());
    ExpectFailure(() => CopyState(state, occurrences: state.Occurrences.Append(secondRoot)), "single occurrence root");
}

void GeometryArtifactRequired()
{
    var state = SampleState();
    ExpectFailure(() => CopyState(state, artifacts: state.Artifacts.Where(artifact => artifact.Kind != PhotonCadArtifactKindV1.Step)), "missing STEP");
}

void ArtifactOperationBinding()
{
    var state = SampleState();
    var artifact = state.Artifacts.First();
    var wrong = new PhotonCadArtifactV1(
        artifact.Role,
        artifact.Kind,
        artifact.OwnerEntityId,
        artifact.Revision,
        artifact.Content,
        artifact.Bounds,
        new PhotonCadArtifactProvenanceV1(
            PhotonCadBackendV1.Geometry,
            "geometry.bundle-v1",
            Digest('b'),
            "photon.geometry.other",
            "operation-1",
            Source()));
    ExpectFailure(() => CopyState(state, artifacts: state.Artifacts.Skip(1).Prepend(wrong)), "operation binding");
}

void BomBindingRejected()
{
    var state = SampleState();
    var orphan = new PhotonCadBomRow("P-002", "Orphan", 1, PhotonCadBomUnit.Each, "missing-entity");
    ExpectFailure(() => CopyState(state, bom: [orphan]), "orphan BOM row");
}

void BomDigestTamperingRejected()
{
    var fixture = Fixture();
    var text = Encoding.UTF8.GetString(fixture.Manifest.Bytes);
    var marker = "\"bom\":{\"digest\":\"sha256:";
    var index = text.IndexOf(marker, StringComparison.Ordinal);
    True(index >= 0, "BOM digest marker");
    var digestIndex = index + marker.Length;
    var replacement = text[digestIndex] == '0' ? '1' : '0';
    var mutated = text[..digestIndex] + replacement + text[(digestIndex + 1)..];
    ExpectFailure(() => DecodeManifestMutation(fixture, Encoding.UTF8.GetBytes(mutated)), "BOM digest tampering");
}

void StepStructureRejected()
{
    var state = SampleState();
    var valid = state.Artifacts.First(artifact => artifact.Kind == PhotonCadArtifactKindV1.Step);
    var appended = new PhotonCadArtifactV1(
        valid.Role, valid.Kind, valid.OwnerEntityId, valid.Revision,
        Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(valid.Content.Span) + "EVIL"),
        valid.Bounds, valid.Provenance);
    var modified = state.Artifacts.Where(artifact => artifact.Kind != PhotonCadArtifactKindV1.Step).Prepend(appended);
    ExpectFailure(() => Codec().Encode(CopyState(state, artifacts: modified)), "appended STEP");
}

void GlbExternalResourceRejected()
{
    var state = SampleState();
    var valid = state.Artifacts.First(artifact => artifact.Kind == PhotonCadArtifactKindV1.Glb);
    var external = new PhotonCadArtifactV1(
        valid.Role, valid.Kind, valid.OwnerEntityId, valid.Revision,
        Glb("{\"asset\":{\"version\":\"2.0\"},\"buffers\":[{\"uri\":\"https://example.invalid/a.bin\",\"byteLength\":0}]}"),
        valid.Bounds, valid.Provenance);
    var modified = state.Artifacts.Where(artifact => artifact.Kind != PhotonCadArtifactKindV1.Glb).Append(external);
    ExpectFailure(() => Codec().Encode(CopyState(state, artifacts: modified)), "external GLB URI");
}

void TranslationConventionEnforced()
{
    var state = SampleState();
    var transposed = IdentityTransform();
    transposed[12] = 10;
    transposed[13] = 20;
    transposed[14] = 30;
    var invalid = new PhotonCadOccurrenceV1("occurrence-1", null, "P-001", "body-1", transposed);
    ExpectFailure(() => CopyState(state, occurrences: [invalid]), "translation must use indices 3, 7, 11");
}

void ZipInputRejected()
{
    var zip = new byte[PhotonCadProjectFileV1.HeaderLength];
    zip[0] = (byte)'P';
    zip[1] = (byte)'K';
    ExpectFailure(() => Codec().Decode(zip), "ZIP magic");
}

void PathLikeTitleRejected()
{
    var state = SampleState();
    ExpectFailure(() => new PhotonCadProjectStateV1(
        state.SessionId, state.ProjectId, state.Revision, "C:\\secret\\part", state.Units,
        state.Entities, state.Operations, state.Occurrences, state.Issues, state.Bom, state.Artifacts, true), "path title");
}

void OpaqueRuntimeHandleRejected()
{
    ExpectFailure(
        () => new PhotonCadEntityV1("art_0123456789abcdef0123456789abcdef", null, PhotonCadEntityKindV1.Datum, "Leaked handle", true, false),
        "runtime artifact handle");
    ExpectFailure(
        () => new PhotonCadSourceIdentityV1("cad-project:0123456789abcdef0123456789abcdef", "1.0", Digest('a'), "Apache-2.0"),
        "native project handle");
}

void CanonicalSchemaPathFree()
{
    var bytes = Codec().Encode(SampleState()).CanonicalBytes;
    var text = Encoding.Latin1.GetString(bytes.Span);
    foreach (var token in new[] { "workspaceHandle", "projectHandle", "artifactHandle", "previewId", "storageVersion", "overwriteGrant", "C:\\", "file://" })
        False(text.Contains(token, StringComparison.OrdinalIgnoreCase), $"persisted authority token {token}");
}

void ForbiddenDependencyScan()
{
    var assembly = typeof(PhotonCadCanonicalProjectCodecV1).Assembly;
    var references = assembly.GetReferencedAssemblies().Select(reference => reference.Name ?? string.Empty).ToArray();
    False(references.Any(reference => reference.StartsWith("System.Net", StringComparison.OrdinalIgnoreCase)
        || reference.Equals("System.Diagnostics.Process", StringComparison.OrdinalIgnoreCase)), "forbidden framework dependency");
    var image = File.ReadAllBytes(assembly.Location);
    var strings = Encoding.Latin1.GetString(image);
    foreach (var token in new[] { "ProcessStartInfo", "System.Net.Http", "build123d", "partcad", "cadquery-ocp", "CadRuntimeBroker" })
        False(strings.Contains(token, StringComparison.OrdinalIgnoreCase), $"forbidden token {token}");
}

PhotonCadCanonicalProjectCodecV1 Codec() => new(new FixedIdentityIssuer());

(PhotonCadProjectStateV1 State, PhotonCadManifestEncodingV1 Manifest) Fixture()
{
    var state = SampleState();
    return (state, PhotonCadManifestWriterV1.Encode(state));
}

void DecodeManifestMutation((PhotonCadProjectStateV1 State, PhotonCadManifestEncodingV1 Manifest) fixture, byte[] manifest)
{
    var file = PhotonCadProjectFramingV1.Encode(new PhotonCadManifestEncodingV1(manifest, fixture.Manifest.Blobs));
    _ = Codec().Decode(file);
}

PhotonCadProjectStateV1 SampleState()
{
    var source = Source();
    var operation = new PhotonCadOperationV1(
        0,
        "operation-1",
        "photon.geometry.create-box",
        "Create box",
        new DateTimeOffset(2026, 8, 10, 15, 30, 45, 123, TimeSpan.Zero).AddTicks(4567),
        PhotonCadOperationStateV1.Applied,
        PhotonCadOperationModeV1.Scratch,
        [
            new PhotonCadOperationInputV1("length", PhotonCadInputValueV1.Number(1.25)),
            new PhotonCadOperationInputV1("segments", PhotonCadInputValueV1.Integer(24)),
            new PhotonCadOperationInputV1("origin", PhotonCadInputValueV1.Vector3(new PhotonCadVector3V1(-0d, 2, 3))),
        ],
        [],
        source);
    var provenance = new PhotonCadArtifactProvenanceV1(
        PhotonCadBackendV1.Geometry,
        "geometry.bundle-v1",
        Digest('b'),
        operation.CapabilityId,
        operation.Id,
        source);
    return new PhotonCadProjectStateV1(
        "pcsid:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        "pcpid:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        1,
        "Café Gearbox",
        PhotonCadProjectUnit.Millimeter,
        [new PhotonCadEntityV1("body-1", null, PhotonCadEntityKindV1.Body, "Drive body", true, false, operation.CapabilityId)],
        [operation],
        [new PhotonCadOccurrenceV1("occurrence-1", null, "P-001", "body-1", IdentityTransform(10, 20, 30))],
        [new PhotonCadIssueV1("verified-solid", PhotonCadIssueSeverityV1.Information, "Solid validation passed.", ["body-1"])],
        [new PhotonCadBomRow("P-001", "Drive body", 1.25, PhotonCadBomUnit.Each, "body-1")],
        [
            new PhotonCadArtifactV1(
                PhotonCadArtifactRoleV1.AuthoritativeGeometry,
                PhotonCadArtifactKindV1.Step,
                "body-1",
                1,
                Step(),
                null,
                provenance),
            new PhotonCadArtifactV1(
                PhotonCadArtifactRoleV1.ProjectPreview,
                PhotonCadArtifactKindV1.Glb,
                null,
                1,
                Glb("{\"asset\":{\"version\":\"2.0\"}}"),
                new PhotonCadBoundsV1(new PhotonCadVector3V1(0, 0, 0), new PhotonCadVector3V1(10, 20, 30)),
                provenance),
        ],
        dirty: true);
}

PhotonCadProjectStateV1 CopyState(
    PhotonCadProjectStateV1 state,
    IEnumerable<PhotonCadEntityV1>? entities = null,
    IEnumerable<PhotonCadOperationV1>? operations = null,
    IEnumerable<PhotonCadOccurrenceV1>? occurrences = null,
    IEnumerable<PhotonCadIssueV1>? issues = null,
    IEnumerable<PhotonCadBomRow>? bom = null,
    IEnumerable<PhotonCadArtifactV1>? artifacts = null) => new(
        state.SessionId,
        state.ProjectId,
        state.Revision,
        state.Title,
        state.Units,
        entities ?? state.Entities,
        operations ?? state.Operations,
        occurrences ?? state.Occurrences,
        issues ?? state.Issues,
        bom ?? state.Bom,
        artifacts ?? state.Artifacts,
        state.Dirty);

PhotonCadSourceIdentityV1 Source() => new("build123d-mcp", "0.3.80", Digest('a'), "Apache-2.0");
string Digest(char value) => $"sha256:{new string(value, 64)}";
byte[] Step() => Encoding.ASCII.GetBytes("ISO-10303-21;\nHEADER;\nFILE_DESCRIPTION(('Photon'),'2;1');\nENDSEC;\nDATA;\n#1=PRODUCT('P-001','Drive body','',());\nENDSEC;\nEND-ISO-10303-21;\n");

byte[] Glb(string json)
{
    var jsonBytes = Encoding.UTF8.GetBytes(json);
    var padded = checked((jsonBytes.Length + 3) & ~3);
    var result = new byte[checked(20 + padded)];
    BinaryPrimitives.WriteUInt32LittleEndian(result, 0x46546c67);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), 2);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), checked((uint)result.Length));
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12), checked((uint)padded));
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16), 0x4e4f534a);
    result.AsSpan(20).Fill(0x20);
    jsonBytes.CopyTo(result.AsSpan(20));
    return result;
}

double[] IdentityTransform(double x = 0, double y = 0, double z = 0) =>
[
    1, 0, 0, x,
    0, 1, 0, y,
    0, 0, 1, z,
    0, 0, 0, 1,
];

void ExpectFailure(Action action, string message)
{
    try { action(); }
    catch (PhotonCadProjectException) { return; }
    throw new InvalidOperationException($"Expected fail-closed result: {message}");
}

void True(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

void False(bool value, string message) => True(!value, message);

void Equal<T>(T expected, T actual, string message) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{message}: expected {expected}, actual {actual}");
}

void EqualBytes(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, string message)
{
    if (!expected.SequenceEqual(actual)) throw new InvalidOperationException(message);
}

sealed class FixedIdentityIssuer : IPhotonCadProjectIdentityIssuerV1
{
    public (string SessionId, string ProjectId) NewIdentity() =>
        ("pcsid:ccccccccccccccccccccccccccccccccccccccccccc", "pcpid:ddddddddddddddddddddddddddddddddddddddddddd");
}
