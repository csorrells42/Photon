using System.Security.Cryptography;
using System.Text;
using PhotonCadFileConversion;

var tests = new (string Name, Func<Task> Run)[]
{
    ("capability catalog is truthful", CapabilityCatalogAsync),
    ("step extension and bytes are independently required", StepExtensionAndBytesAsync),
    ("valid STP preserves exact generic Part-21 bytes", StepPreservesExactBytesAsync),
    ("assembly STEP remains opaque but exactly preserved", AssemblyPreservationAsync),
    ("malformed, appended, and polyglot STEP reject", HostileStepAsync),
    ("GLB and Inventor compound inputs fail closed", UnsupportedFormatsAsync),
    ("input cap and cancellation reject before acceptance", BoundsAndCancellationAsync),
    ("stream intake caps before allocation and preserves caller ownership", StreamIntakeAsync),
    ("results defensively copy source bytes", DefensiveCopiesAsync),
};

var failures = new List<string>();
foreach (var test in tests)
{
    try { await test.Run().ConfigureAwait(false); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception exception) { failures.Add($"{test.Name}: {exception.Message}"); Console.WriteLine($"FAIL {test.Name}: {exception}"); }
}
if (failures.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
Console.WriteLine($"Photon CAD file conversion smoke passed {tests.Length}/{tests.Length}.");

static Task CapabilityCatalogAsync()
{
    var authority = new PhotonCadStepPassthroughConversionAuthority();
    Equal(4, authority.Capabilities.Count, "capability count");
    var step = authority.Capabilities.Single(value => value.Format == PhotonCadExternalFormat.StepPart21);
    True(step.Available && step.AssemblyPreservationPossible && step.ReasonCode == "verified-byte-preserving-passthrough", "STEP must be the only available intake");
    var compound = authority.Capabilities.Single(value => value.Format == PhotonCadExternalFormat.OleCompoundDocument);
    True(!compound.Available && compound.ReasonCode == "compound-document-converter-unavailable", "generic OLE documents must not be claimed as Inventor");
    return Task.CompletedTask;
}

static async Task StepExtensionAndBytesAsync()
{
    var authority = new PhotonCadStepPassthroughConversionAuthority();
    await ExpectFailureAsync(() => authority.ConvertAsync(new PhotonCadConversionRequest("shape.ipt", Step("part"))).AsTask(), "source_extension_mismatch");
    await ExpectFailureAsync(() => authority.ConvertAsync(new PhotonCadConversionRequest("shape.step", Encoding.ASCII.GetBytes("not STEP"))).AsTask(), "source_format_unsupported");
}

static async Task StepPreservesExactBytesAsync()
{
    var source = Step("simple");
    var result = await new PhotonCadStepPassthroughConversionAuthority().ConvertAsync(new PhotonCadConversionRequest("simple.stp", source));
    Equal(PhotonCadExternalFormat.StepPart21, result.SourceFormat, "format");
    Equal(PhotonCadConversionMode.VerifiedBytePreservingPassThrough, result.Mode, "mode");
    Equal(PhotonCadAssemblyDisposition.NotApplicable, result.AssemblyDisposition, "assembly disposition");
    Equal("application/step", result.Artifact.MediaType, "media type");
    True(result.Artifact.Content.Span.SequenceEqual(source), "bytes changed");
    Equal(Convert.ToHexStringLower(SHA256.HashData(source)), result.Artifact.Digest, "digest");
}

static async Task AssemblyPreservationAsync()
{
    var source = Step("assembly", "#2=NEXT_ASSEMBLY_USAGE_OCCURRENCE('use','',#1,#1,$);", "#3=ASSEMBLY_COMPONENT_USAGE('component','',#1,#1,$);");
    var result = await new PhotonCadStepPassthroughConversionAuthority().ConvertAsync(new PhotonCadConversionRequest("gearbox.step", source));
    True(result.ContainsAssemblyConstructs, "assembly constructs missing");
    Equal(PhotonCadAssemblyDisposition.PreservedOpaquePart21, result.AssemblyDisposition, "assembly disposition");
    True(result.Artifact.Content.Span.SequenceEqual(source), "assembly payload was regenerated");
}

static async Task HostileStepAsync()
{
    var authority = new PhotonCadStepPassthroughConversionAuthority();
    foreach (var source in new[]
    {
        Encoding.ASCII.GetBytes("ISO-10303-21;\nHEADER;\nENDSEC;\nDATA;\nENDSEC;\nEND-ISO-10303-21;\nMZ"),
        Encoding.ASCII.GetBytes("ISO-10303-21;\nHEADER;\nENDSEC;\nDATA;\nENDSEC;\nEND-ISO-10303-21;\n<script/>"),
        Encoding.ASCII.GetBytes("ISO-10303-21;\nHEADER;\nENDSEC;\nDATA;\n/*unterminated\nENDSEC;\nEND-ISO-10303-21;"),
        Encoding.ASCII.GetBytes("ISO-10303-21;\nHEADER;\nENDSEC;\nDATA;\nENDSEC;\nEND-ISO-10303-21;\0"),
    })
    {
        await ExpectFailureAsync(() => authority.ConvertAsync(new PhotonCadConversionRequest("bad.step", source)).AsTask(), "step_");
    }
}

static async Task UnsupportedFormatsAsync()
{
    var authority = new PhotonCadStepPassthroughConversionAuthority();
    var glb = new byte[12]; glb[0] = (byte)'g'; glb[1] = (byte)'l'; glb[2] = (byte)'T'; glb[3] = (byte)'F';
    await ExpectFailureAsync(() => authority.ConvertAsync(new PhotonCadConversionRequest("preview.glb", glb)).AsTask(), "glb-import-authority-unavailable");
    var compound = new byte[512]; new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 }.CopyTo(compound, 0);
    await ExpectFailureAsync(() => authority.ConvertAsync(new PhotonCadConversionRequest("model.ipt", compound)).AsTask(), "autodesk-inventor-authority-unavailable");
    await ExpectFailureAsync(() => authority.ConvertAsync(new PhotonCadConversionRequest("assembly.iam", compound)).AsTask(), "autodesk-inventor-authority-unavailable");
}

static async Task BoundsAndCancellationAsync()
{
    var authority = new PhotonCadStepPassthroughConversionAuthority(128);
    await ExpectFailureAsync(() => authority.ConvertAsync(new PhotonCadConversionRequest("large.step", new byte[129])).AsTask(), "source_exceeds_cap");
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    await ExpectCancellationAsync(() => authority.ConvertAsync(new PhotonCadConversionRequest("part.step", Step("cancel")), cancelled.Token).AsTask());
}

static async Task StreamIntakeAsync()
{
    var authority = new PhotonCadStepPassthroughConversionAuthority(1_024);
    var oversized = new MemoryStream(new byte[1_025], writable: false);
    await ExpectFailureAsync(() => authority.ConvertAsync("large.step", oversized).AsTask(), "source_exceeds_cap");
    var source = Step("stream");
    await using var stream = new MemoryStream(source, writable: false);
    var result = await authority.ConvertAsync("stream.step", stream);
    True(result.Artifact.Content.Span.SequenceEqual(source), "stream bytes changed");
    True(stream.CanRead, "authority closed caller stream");
    await using var unknownLength = new NonSeekableReadStream(source);
    await ExpectFailureAsync(() => authority.ConvertAsync("unknown.step", unknownLength).AsTask(), "source_stream_not_seekable");
}

static async Task DefensiveCopiesAsync()
{
    var source = Step("copy");
    var request = new PhotonCadConversionRequest("copy.step", source);
    source[0] = (byte)'X';
    var result = await new PhotonCadStepPassthroughConversionAuthority().ConvertAsync(request);
    var observed = result.Artifact.Content.ToArray();
    observed[0] = (byte)'Y';
    True(result.Artifact.Content.Span[0] == (byte)'I', "result exposed mutable backing bytes");
}

static byte[] Step(string label, params string[] entities)
{
    var data = entities.Length == 0 ? "#1=PRODUCT('P','" + label + "','',());" : string.Join("\n", entities);
    return Encoding.ASCII.GetBytes($"ISO-10303-21;\nHEADER;\nFILE_DESCRIPTION(('{label}'),'2;1');\nENDSEC;\nDATA;\n{data}\nENDSEC;\nEND-ISO-10303-21;\n");
}

static async Task ExpectFailureAsync(Func<Task> action, string expected)
{
    try { await action().ConfigureAwait(false); throw new InvalidOperationException("Expected failure was accepted."); }
    catch (InvalidDataException exception) when (exception.Message.Contains(expected, StringComparison.Ordinal)) { }
}

static async Task ExpectCancellationAsync(Func<Task> action)
{
    try { await action().ConfigureAwait(false); throw new InvalidOperationException("Expected cancellation was accepted."); }
    catch (OperationCanceledException) { }
}

static void Equal<T>(T expected, T actual, string label) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{label}: expected {expected}, actual {actual}");
}

static void True(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException(label);
}

sealed class NonSeekableReadStream(byte[] bytes) : Stream
{
    private readonly MemoryStream _inner = new(bytes, writable: false);
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
}
