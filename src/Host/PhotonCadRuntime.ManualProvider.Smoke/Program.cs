using PhotonCadRuntime.ManualProvider;
using PhotonCadProjects;
using PhotonCadProjects.Codec;
using PhotonCadProjects.RuntimeSync;

var tests = new (string Name, Func<Task> Run)[]
{
    ("unavailable catalog is honest", () => RunSync(UnavailableCatalog)),
    ("rectangle and circle sketch extrude bindings are typed", () => RunSync(SketchExtrudeBinding)),
    ("subtractive operations bind existing target", () => RunSync(ExistingTargetBinding)),
    ("manual input bounds reject", () => RunSync(InputBounds)),
    ("protocol requirements remain explicit", () => RunSync(ProtocolRequirements)),
    ("manual image evidence is exact and accepted", EvidenceVerificationAsync),
};
if (args.Contains("--real", StringComparer.Ordinal))
    tests = [.. tests, ("real manual add cut hole persists and reopens 0 to 2 to 4 to 6", RealManualRoundTripAsync)];
var passed = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        passed++;
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"FAIL {test.Name}: {exception.GetType().Name}: {exception.Message}");
    }
}
Console.WriteLine($"PhotonCadRuntime.ManualProvider.Smoke: {passed}/{tests.Length} passed");
return passed == tests.Length ? 0 : 1;

static void UnavailableCatalog()
{
    var runtime = PhotonCadManualProviderRuntime.CreateUnavailable();
    var catalog = runtime.GetCatalog();
    Equal(9, catalog.Count, "catalog count");
    True(catalog.All(item => item.Availability == PhotonCadManualAvailability.Unavailable), "availability");
    True(catalog.All(item => item.UnavailableReason == "manual_geometry_protocol_v1_not_installed"), "reason");
}

static void SketchExtrudeBinding()
{
    var bound = PhotonCadManualProviderRuntime.CreateUnavailable().BindSketchExtrudeAdd(
        "manual-request", "pcsid:manual", "pcpid:manual", 0, "manual-body", "xy", 10, 20, 30);
    Equal(PhotonCadManualCapabilityIds.SketchExtrudeAdd, bound.Request.CapabilityId, "capability");
    Equal(1, bound.Request.TargetEntityIds.Count, "target count");
    Equal("manual-body", bound.Request.TargetEntityIds[0], "target");
    Equal(5, bound.Request.Inputs.Count, "rectangle input count");
    var circular = PhotonCadManualProviderRuntime.CreateUnavailable().BindCircularSketchExtrudeAdd(
        "circle-request", "pcsid:manual", "pcpid:manual", 0, "circle-body", "xy", 5, 12);
    Equal(4, circular.Request.Inputs.Count, "circle input count");

    var mouse = new PhotonCadManualMouseSketch(
        "polygon",
        [new ManualSketchPoint(0, 0), new ManualSketchPoint(12, 0), new ManualSketchPoint(12, 8), new ManualSketchPoint(0, 8)],
        [], 0, 0, 0, 1, 0, 0, 0, 0, 1);
    var mouseAdd = PhotonCadManualProviderRuntime.CreateUnavailable().BindMouseSketchExtrudeAdd(
        "mouse-request", "pcsid:manual", "pcpid:manual", 0, "mouse-body", mouse, 4, "polygon");
    Equal(PhotonCadManualCapabilityIds.MouseSketchExtrudeAdd, mouseAdd.Request.CapabilityId, "mouse capability");
    Equal(2, mouseAdd.Request.Inputs.Count, "mouse input count");
}

static void ExistingTargetBinding()
{
    var runtime = PhotonCadManualProviderRuntime.CreateUnavailable();
    var cut = runtime.BindSketchExtrudeCut("cut-request", "pcsid:manual", "pcpid:manual", 2, "body-1", "xy", 4, 5, 6);
    var hole = runtime.BindHoleCut("hole-request", "pcsid:manual", "pcpid:manual", 2, "body-1", 5, 6, 0, 0, 0);
    var fillet = runtime.BindFillet("fillet-request", "pcsid:manual", "pcpid:manual", 2, "body-1", 1, ["edge-1"]);
    Equal(PhotonCadManualCapabilityIds.SketchExtrudeCut, cut.Request.CapabilityId, "cut capability");
    Equal(PhotonCadManualCapabilityIds.HoleCut, hole.Request.CapabilityId, "hole capability");
    Equal(PhotonCadManualCapabilityIds.Fillet, fillet.Request.CapabilityId, "fillet capability");
}

static void InputBounds()
{
    var runtime = PhotonCadManualProviderRuntime.CreateUnavailable();
    Throws(() => runtime.BindSketchExtrudeAdd("request", "pcsid:manual", "pcpid:manual", 0, "body", "invalid", 1, 1, 1));
    Throws(() => runtime.BindHoleCut("request", "pcsid:manual", "pcpid:manual", 0, "body", 0, 1, 0, 0, 0));
    Throws(() => runtime.BindLinearPattern("request", "pcsid:manual", "pcpid:manual", 0, "body", "seed", 1, 1));
    Throws(() => runtime.BindMouseSketchExtrudeAdd(
        "request", "pcsid:manual", "pcpid:manual", 0, "body",
        new PhotonCadManualMouseSketch("polygon", [new ManualSketchPoint(0, 0), new ManualSketchPoint(1, 0), new ManualSketchPoint(2, 0)], [], 0, 0, 0, 1, 0, 0, 0, 0, 1),
        1, "polygon"));
}

static void ProtocolRequirements()
{
    True(PhotonCadManualProtocolRequirements.RequiredOperations.Count >= 8, "requirements count");
    True(PhotonCadManualProtocolRequirements.RequiredOperations.Any(item => item.Contains("sealed result STEP/GLB", StringComparison.Ordinal)), "sealed output requirement");
}

static async Task EvidenceVerificationAsync()
{
    var root = FindRepositoryRoot();
    var selection = Path.Combine(root, "runtime-assets", "photon-cad-manual", "evidence-selection.json");
    var evidence = await ManualEvidenceVerifier.VerifyAsync(selection, CancellationToken.None);
    Equal(ManualEvidenceVerifier.AcceptedDerivedImageId, evidence.DerivedImageId, "manual image");
    Equal(ManualEvidenceVerifier.AcceptedReceiptSha256, evidence.ReceiptSha256, "manual receipt");

    var publishedSelection = Path.Combine(root, "artifacts", "desktop", "win-x64", "runtime-assets",
        "photon-cad-manual", "evidence-selection.json");
    var published = await ManualEvidenceVerifier.VerifyAsync(publishedSelection, CancellationToken.None);
    Equal(evidence.DerivedImageId, published.DerivedImageId, "source and published manual image");
    Equal(evidence.ReceiptSha256, published.ReceiptSha256, "source and published manual receipt");
}

static async Task RealManualRoundTripAsync()
{
    const string sessionId = "pcsid:manual-real-roundtrip";
    const string projectId = "pcpid:manual-real-roundtrip";
    const string entityId = "manual-body";
    var root = FindRepositoryRoot();
    var evidence = Path.Combine(root, "runtime-assets", "photon-cad-manual", "evidence-selection.json");
    var docker = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "Docker", "Docker", "resources", "bin", "docker.exe");
    if (!File.Exists(docker)) throw new InvalidOperationException("docker executable not found");

    var parent = Path.Combine(Path.GetTempPath(), "PhotonCadManualProviderSmoke");
    Directory.CreateDirectory(parent);
    var workspace = Path.Combine(parent, $"real-{Guid.NewGuid():N}");
    var config = Path.Combine(workspace, "docker-config");
    var jobs = Path.Combine(workspace, "jobs");
    Directory.CreateDirectory(config);
    Directory.CreateDirectory(jobs);
    try
    {
        var runtime = await PhotonCadManualProviderRuntime.CreateLocalEngineeringAsync(
            docker, config, jobs, evidence, TimeSpan.FromSeconds(90));
        var codec = new PhotonCadCanonicalProjectCodecV1(new FixedIdentityIssuer(sessionId, projectId));
        var mapper = new PhotonCadRuntimeCanonicalMapperV1(codec, IndustrialPolicy());
        var handle = new PhotonCadProjectHandle("cad-project:44444444444444444444444444444444");
        var initial = await codec.CreateAsync("Manual real roundtrip", PhotonCadProjectUnit.Millimeter);

        var add = runtime.BindSketchExtrudeAdd(
            "manual-add", sessionId, projectId, 0, entityId, "xy", 30, 24, 12);
        var addBinding = new PhotonCadCanonicalMutationBinding(handle, initial);
        var addMutation = await add.Provider.ApplyAsync(mapper.PrepareProviderRequest(addBinding, add.Request));
        var afterAdd = codec.MarkSaved(mapper.Apply(addBinding, add.Request, addMutation));
        Equal(2L, afterAdd.Revision, "add revision");

        var cut = runtime.BindSketchExtrudeCut(
            "manual-cut", sessionId, projectId, 2, entityId, "xy", 8, 6, 16);
        var cutBinding = new PhotonCadCanonicalMutationBinding(handle, afterAdd);
        var cutMutation = await cut.Provider.ApplyAsync(mapper.PrepareProviderRequest(cutBinding, cut.Request));
        var afterCut = codec.MarkSaved(mapper.Apply(cutBinding, cut.Request, cutMutation));
        Equal(4L, afterCut.Revision, "cut revision");
        var cutFeatureId = cut.Request.TargetEntityIds[1];

        var hole = runtime.BindHoleCut(
            "manual-hole", sessionId, projectId, 4, entityId, 4, 16, 8, 0, 0);
        var holeBinding = new PhotonCadCanonicalMutationBinding(handle, afterCut);
        var holeMutation = await hole.Provider.ApplyAsync(mapper.PrepareProviderRequest(holeBinding, hole.Request));
        var afterHole = codec.MarkSaved(mapper.Apply(holeBinding, hole.Request, holeMutation));
        Equal(6L, afterHole.Revision, "hole revision");
        var holeFeatureId = hole.Request.TargetEntityIds[1];

        var linear = runtime.BindLinearPattern(
            "manual-linear", sessionId, projectId, 6, entityId, cutFeatureId, 3, 8);
        var linearBinding = new PhotonCadCanonicalMutationBinding(handle, afterHole);
        var linearMutation = await linear.Provider.ApplyAsync(mapper.PrepareProviderRequest(linearBinding, linear.Request));
        var afterLinear = codec.MarkSaved(mapper.Apply(linearBinding, linear.Request, linearMutation));
        Equal(8L, afterLinear.Revision, "linear pattern revision");
        var linearFeatureId = linear.Request.TargetEntityIds[1];

        var circular = runtime.BindCircularPattern(
            "manual-circular", sessionId, projectId, 8, entityId, holeFeatureId, 4, 360);
        var circularBinding = new PhotonCadCanonicalMutationBinding(handle, afterLinear);
        var circularMutation = await circular.Provider.ApplyAsync(mapper.PrepareProviderRequest(circularBinding, circular.Request));
        var afterCircular = codec.MarkSaved(mapper.Apply(circularBinding, circular.Request, circularMutation));
        Equal(10L, afterCircular.Revision, "circular pattern revision");
        var circularFeatureId = circular.Request.TargetEntityIds[1];

        var reopened = codec.Inspect(codec.Decode(afterCircular.CanonicalBytes));
        Equal(10L, reopened.Revision, "reopened revision");
        Equal(5, reopened.Entities.Count, "entity count");
        var body = reopened.Entities.Single(value => StringComparer.Ordinal.Equals(value.Id, entityId));
        True(body.Kind == PhotonCadEntityKindV1.Body, "body kind");
        var expectedFeatures = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [cutFeatureId] = PhotonCadManualCapabilityIds.SketchExtrudeCut,
            [holeFeatureId] = PhotonCadManualCapabilityIds.HoleCut,
            [linearFeatureId] = PhotonCadManualCapabilityIds.LinearPattern,
            [circularFeatureId] = PhotonCadManualCapabilityIds.CircularPattern,
        };
        foreach (var (featureId, capabilityId) in expectedFeatures)
        {
            var feature = reopened.Entities.Single(value => StringComparer.Ordinal.Equals(value.Id, featureId));
            True(feature.Kind == PhotonCadEntityKindV1.Datum, $"{capabilityId} kind");
            Equal(entityId, feature.ParentId!, $"{capabilityId} parent");
            Equal(capabilityId, feature.SourceCapabilityId!, $"{capabilityId} source capability");
        }
        Equal(1, reopened.Occurrences.Count, "occurrence count");
        Equal(1, reopened.Bom.Count, "BOM count");
        Equal(1, reopened.Artifacts.Count(value => value.Role == PhotonCadArtifactRoleV1.AuthoritativeGeometry), "STEP count");
        Equal(1, reopened.Artifacts.Count(value => value.Role == PhotonCadArtifactRoleV1.ProjectPreview), "preview count");
        True(reopened.Operations.Select(value => value.CapabilityId).SequenceEqual([
            PhotonCadManualCapabilityIds.SketchExtrudeAdd, "industrial.preview.glb.v1",
            PhotonCadManualCapabilityIds.SketchExtrudeCut, "industrial.preview.glb.v1",
            PhotonCadManualCapabilityIds.HoleCut, "industrial.preview.glb.v1",
            PhotonCadManualCapabilityIds.LinearPattern, "industrial.preview.glb.v1",
            PhotonCadManualCapabilityIds.CircularPattern, "industrial.preview.glb.v1",
        ], StringComparer.Ordinal), "operation history");
    }
    finally
    {
        DeleteOwnedWorkspace(parent, workspace);
    }
}

static PhotonCadRuntimeSyncPolicy IndustrialPolicy() => new(
    PhotonCadRuntimeSyncContract.MaximumSealedArtifactBytes,
    PhotonCadRuntimeSyncContract.MaximumSealedArtifactBytesPerMutation,
    PhotonCadRuntimeSyncContract.MaximumBaseArtifactBytes,
    PhotonCadRuntimeSyncContract.MaximumBaseArtifactBytesPerRequest);

static void DeleteOwnedWorkspace(string parent, string workspace)
{
    var fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    var fullWorkspace = Path.GetFullPath(workspace);
    if (!fullWorkspace.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase)
        || Path.GetFileName(fullWorkspace).StartsWith("real-", StringComparison.Ordinal) is false)
        throw new InvalidOperationException("manual smoke workspace ownership mismatch");
    if (Directory.Exists(fullWorkspace)) Directory.Delete(fullWorkspace, recursive: true);
}

static Task RunSync(Action action)
{
    action();
    return Task.CompletedTask;
}

static string FindRepositoryRoot()
{
    for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
    {
        if (File.Exists(Path.Combine(current.FullName, "Launch-Hermes.ps1"))
            && Directory.Exists(Path.Combine(current.FullName, "src", "Host")))
            return current.FullName;
    }
    throw new InvalidOperationException("repository root not found");
}

static void True(bool condition, string field)
{
    if (!condition) throw new InvalidOperationException($"Assertion failed: {field}.");
}

static void Equal<T>(T expected, T actual, string field) where T : IEquatable<T>
{
    if (!expected.Equals(actual)) throw new InvalidOperationException($"{field}: expected {expected}, got {actual}.");
}

static void Throws(Action action)
{
    try { action(); }
    catch (ArgumentException) { return; }
    throw new InvalidOperationException("Expected argument validation failure.");
}

internal sealed class FixedIdentityIssuer(string sessionId, string projectId) : IPhotonCadProjectIdentityIssuerV1
{
    public (string SessionId, string ProjectId) NewIdentity() => (sessionId, projectId);
}
