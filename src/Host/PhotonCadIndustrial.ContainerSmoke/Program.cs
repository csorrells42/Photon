using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

internal static class Program
{
    private const string BaseImage = "photon-cad-geometry:0.1.0-b123dmcp-0.3.80-8fb7cefb";
    private const string BaseImageId = "sha256:33d9c839840115640b08dd3c4142b7f29624329408155fe1484e1d88c3891703";
    private const string DerivedImage = "photon-cad-industrial:0.1.0-b123dmcp-0.3.80-bdw0.2.0";
    private const string RequestSchema = "photon.cad.industrial.request/v1";
    private const string ResponseSchema = "photon.cad.industrial.response/v1";
    private const string ExpectedCatalogDigest = "sha256:aae5554ce9e57133f508e3343663704d35c82e6a31e5201f6224a9b299ddf6b6";
    private const string ExpectedPackageDigest = "sha256:2e1b8d0a41f0562071c3fdc5cbe803871e3c94fdf8927b0238550b6844aea5b9";
    private const int MaximumGlbBytes = 128 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false,
    };

    public static async Task<int> Main()
    {
        var startedUtc = DateTimeOffset.UtcNow;
        var started = Stopwatch.StartNew();
        var scenarios = new List<ScenarioReceipt>();
        var runId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var tempRoot = Path.Combine(Path.GetTempPath(), $"photon-cad-industrial-smoke-{runId}");
        var dockerConfig = Path.Combine(tempRoot, "docker-config");
        var cidRoot = Path.Combine(tempRoot, "cids");
        var smokeRoot = FindSmokeRoot();
        var industrialRoot = Path.GetFullPath(Path.Combine(smokeRoot, "..", "PhotonCadRuntime.Container", "industrial"));
        var artifactRoot = Path.Combine(smokeRoot, "artifacts");
        string? derivedImageId = null;
        string? catalogDigest = null;
        string? packageDigest = null;
        string? boxDigest = null;
        string? cylinderDigest = null;
        string? bearingDigest = null;
        string? gearDigest = null;
        string? previewDigest = null;
        var catalogBytes = 0;
        var baseStagingReference = $"photon-cad-geometry-staging:{runId}";
        var derivedStagingReference = $"photon-cad-industrial-staging:{runId}";
        HardenedDockerHarness? harness = null;
        ImageIdentity? baseIdentity = null;
        try
        {
            Directory.CreateDirectory(tempRoot);
            Directory.CreateDirectory(dockerConfig);
            Directory.CreateDirectory(cidRoot);
            harness = new HardenedDockerHarness(dockerConfig, runId, cidRoot);
            AssertNewOnlyRoots(smokeRoot, industrialRoot);
            ParseContractFiles(industrialRoot);
            ScanForbiddenAdapterApis(industrialRoot);

            await ScenarioAsync(scenarios, "exact-base-image-identity", async () =>
            {
                using var inspect = await InspectImageAsync(harness, BaseImage);
                baseIdentity = ImageIdentity.From(inspect.RootElement);
                Require(baseIdentity.Value.Id == BaseImageId, "base image identity drifted");
                using var exactInspect = await InspectImageAsync(harness, BaseImageId);
                RequireIdenticalImage(baseIdentity.Value, ImageIdentity.From(exactInspect.RootElement), "base tag/exact ID");
            });

            await ScenarioAsync(scenarios, "immutable-base-staging-binding", async () =>
            {
                var capturedBase = baseIdentity ?? throw new InvalidOperationException("base identity was not captured");
                RequireSuccess(
                    await harness.RunDockerAsync(
                        new[] { "image", "tag", BaseImageId, baseStagingReference },
                        timeout: TimeSpan.FromSeconds(30)),
                    "bind unique base staging reference");
                using var inspect = await InspectImageAsync(harness, baseStagingReference);
                RequireIdenticalImage(capturedBase, ImageIdentity.From(inspect.RootElement), "unique base staging reference");
            });

            await ScenarioAsync(scenarios, "offline-derived-image-build", async () =>
            {
                var capturedBase = baseIdentity ?? throw new InvalidOperationException("base identity was not captured");
                using var stagingBefore = await InspectImageAsync(harness, baseStagingReference);
                RequireIdenticalImage(capturedBase, ImageIdentity.From(stagingBefore.RootElement), "pre-build base staging reference");
                var result = await harness.RunDockerAsync(
                    new[]
                    {
                        "build", "--pull=false", "--network=none",
                        "--build-arg", $"PHOTON_CAD_GEOMETRY_BASE={baseStagingReference}",
                        "--build-arg", $"PHOTON_CAD_GEOMETRY_BASE_ID={BaseImageId}",
                        "--tag", derivedStagingReference,
                        "--file", Path.Combine(industrialRoot, "Dockerfile"),
                        industrialRoot,
                    },
                    workingDirectory: industrialRoot,
                    timeout: TimeSpan.FromMinutes(5));
                RequireSuccess(result, "offline derived build");
                using var stagingAfter = await InspectImageAsync(harness, baseStagingReference);
                RequireIdenticalImage(capturedBase, ImageIdentity.From(stagingAfter.RootElement), "post-build base staging reference");
                using var mutableTagAfter = await InspectImageAsync(harness, BaseImage);
                RequireIdenticalImage(capturedBase, ImageIdentity.From(mutableTagAfter.RootElement), "post-build friendly base tag");
                using var inspect = await InspectImageAsync(harness, derivedStagingReference);
                var derivedIdentity = ImageIdentity.From(inspect.RootElement);
                derivedImageId = derivedIdentity.Id;
                Require(IsDigest(derivedImageId), "derived image has no immutable ID");
                RequireRootFsDescendsFrom(capturedBase, derivedIdentity);
                var config = inspect.RootElement.GetProperty("Config");
                Require(config.GetProperty("User").GetString() == "65532:65532", "derived image is not non-root");
                Require(config.GetProperty("WorkingDir").GetString() == "/tmp", "derived working directory drifted");
                var entrypoint = config.GetProperty("Entrypoint").EnumerateArray().Select(value => value.GetString()).ToArray();
                Require(entrypoint.SequenceEqual(new[] { "/opt/photon/venv/bin/python", "/opt/photon/bin/photon_industrial_adapter.py" }), "derived entrypoint drifted");
                var labels = config.GetProperty("Labels");
                Require(labels.GetProperty("io.photon.cad.base-image-id").GetString() == BaseImageId, "derived image lost base binding");
                Require(labels.GetProperty("io.photon.cad.engineering-status").GetString() == "local-evaluation-only", "engineering status label drifted");
                Require(labels.GetProperty("io.photon.cad.redistribution").GetString() == "blocked-pending-license-review", "redistribution label drifted");
                Require(!labels.TryGetProperty("org.opencontainers.image.licenses", out var aggregateLicense) || string.IsNullOrEmpty(aggregateLicense.GetString()), "derived image makes an aggregate license claim");
            });

            await ScenarioAsync(scenarios, "strict-json-hostile-inputs", async () =>
            {
                var hostile = new (string Name, byte[] Payload)[]
                {
                    ("duplicate-top", Encoding.UTF8.GetBytes($"{{\"schema\":\"{RequestSchema}\",\"schema\":\"{RequestSchema}\",\"operation\":\"catalog\"}}")),
                    ("duplicate-nested", Encoding.UTF8.GetBytes($"{{\"schema\":\"{RequestSchema}\",\"operation\":\"createPrimitive\",\"primitive\":{{\"kind\":\"box\",\"dimensions\":{{\"lengthMm\":1,\"lengthMm\":2,\"widthMm\":1,\"heightMm\":1}}}}}}")),
                    ("nan", Encoding.UTF8.GetBytes($"{{\"schema\":\"{RequestSchema}\",\"operation\":\"catalog\",\"value\":NaN}}")),
                    ("infinity", Encoding.UTF8.GetBytes($"{{\"schema\":\"{RequestSchema}\",\"operation\":\"catalog\",\"value\":Infinity}}")),
                    ("float-overflow", Encoding.UTF8.GetBytes($"{{\"schema\":\"{RequestSchema}\",\"operation\":\"catalog\",\"value\":1e309}}")),
                    ("integer-overflow", Encoding.UTF8.GetBytes($"{{\"schema\":\"{RequestSchema}\",\"operation\":\"catalog\",\"value\":1000000000001}}")),
                    ("invalid-utf8", new byte[] { (byte)'{', 0xff, (byte)'}' }),
                    ("utf8-bom", new byte[] { 0xef, 0xbb, 0xbf }.Concat(Encoding.UTF8.GetBytes(CatalogRequest())).ToArray()),
                    ("surrogate", Encoding.UTF8.GetBytes($"{{\"schema\":\"{RequestSchema}\",\"operation\":\"catalog\",\"value\":\"\\ud800\"}}")),
                    ("depth", Encoding.UTF8.GetBytes(new string('[', 65) + "0" + new string(']', 65))),
                };
                foreach (var item in hostile)
                {
                    var result = await InvokeAdapterAsync(harness!, derivedImageId!, NewJob(tempRoot, "json-" + item.Name), item.Payload);
                    RequireErrorResponse(result, "invalid-json");
                }
            });

            await ScenarioAsync(scenarios, "mutable-tag-drift-detection-simulation", () =>
            {
                var captured = baseIdentity ?? throw new InvalidOperationException("base identity was not captured");
                var altered = captured with { Id = "sha256:" + new string('0', 64) };
                Require(!ImagesAreIdentical(captured, altered), "image identity comparison accepted deterministic tag drift");
                return Task.CompletedTask;
            });

            await ScenarioAsync(scenarios, "host-deadline-kills-exact-container", async () =>
            {
                var job = NewJob(tempRoot, "hung-workload");
                var rejected = false;
                try
                {
                    await harness!.RunWorkloadAsync(
                        derivedImageId!,
                        Path.Combine(job, "input"),
                        Path.Combine(job, "output"),
                        "while True:\n    pass",
                        TimeSpan.FromSeconds(1),
                        stdoutLimit: 64 * 1024,
                        stderrLimit: 64 * 1024);
                }
                catch (DockerDeadlineExceededException)
                {
                    rejected = true;
                }
                Require(rejected, "deliberately hung workload escaped host deadline");
                await harness!.AssertNoRunContainersAsync();
            });

            await ScenarioAsync(scenarios, "stdout-cap-kills-exact-container", async () =>
            {
                var job = NewJob(tempRoot, "stdout-flood");
                var rejected = false;
                try
                {
                    await harness!.RunWorkloadAsync(
                        derivedImageId!,
                        Path.Combine(job, "input"),
                        Path.Combine(job, "output"),
                        "import os\nwhile True:\n    os.write(1, b'x' * 16384)",
                        TimeSpan.FromSeconds(10),
                        stdoutLimit: 32 * 1024,
                        stderrLimit: 64 * 1024);
                }
                catch (DockerOutputLimitExceededException)
                {
                    rejected = true;
                }
                Require(rejected, "stdout flood escaped the host output cap");
                await harness!.AssertNoRunContainersAsync();
            });

            AdapterResult firstCatalog = default!;
            AdapterResult secondCatalog = default!;
            await ScenarioAsync(scenarios, "deterministic-catalog-bytes-and-identity", async () =>
            {
                firstCatalog = await InvokeAdapterAsync(harness!, derivedImageId!, NewJob(tempRoot, "catalog-a"), CatalogRequest());
                secondCatalog = await InvokeAdapterAsync(harness!, derivedImageId!, NewJob(tempRoot, "catalog-b"), CatalogRequest());
                RequireSuccess(firstCatalog.Process, "catalog A");
                RequireSuccess(secondCatalog.Process, "catalog B");
                Require(firstCatalog.StandardOutputBytes.AsSpan().SequenceEqual(secondCatalog.StandardOutputBytes), "catalog bytes are nondeterministic");
                catalogBytes = firstCatalog.StandardOutputBytes.Length;
                RequireSuccessResponse(firstCatalog.Response, "catalog");
                catalogDigest = firstCatalog.Response.GetProperty("catalogDigest").GetString();
                Require(catalogDigest == ExpectedCatalogDigest, "catalog digest drifted from golden");
                var catalog = firstCatalog.Response.GetProperty("catalog");
                var runtime = catalog.GetProperty("runtime");
                Require(runtime.GetProperty("baseImageId").GetString() == BaseImageId, "catalog runtime is not base-bound");
                Require(runtime.GetProperty("build123dVersion").GetString() == "0.11.0", "build123d version drifted");
                var library = runtime.GetProperty("library");
                Require(library.GetProperty("version").GetString() == "0.2.0", "bd_warehouse version drifted");
                packageDigest = library.GetProperty("contentDigest").GetString();
                Require(packageDigest == ExpectedPackageDigest, "library content digest drifted from golden");
                ValidateCatalog(catalog);
                RejectSensitiveCatalogKeys(catalog);
            });

            byte[] boxBytes = Array.Empty<byte>();
            byte[] cylinderBytes = Array.Empty<byte>();
            await ScenarioAsync(scenarios, "box-6000-and-new-only-output", async () =>
            {
                var job = NewJob(tempRoot, "box");
                var result = await InvokeAdapterAsync(harness!, derivedImageId!, job, PrimitiveRequest("box", new Dictionary<string, object>
                {
                    ["lengthMm"] = 10,
                    ["widthMm"] = 20,
                    ["heightMm"] = 30,
                }));
                RequireSuccess(result.Process, "box");
                RequireSuccessResponse(result.Response, "createPrimitive");
                RequireApproximately(result.Response.GetProperty("measurement").GetProperty("volumeMm3").GetDouble(), 6000.0, 1e-9, "box volume");
                ValidatePrimitiveProvenance(result.Response, "box", new[] { "heightMm", "lengthMm", "widthMm" });
                (boxBytes, boxDigest) = ValidateArtifact(result.Response, Path.Combine(job, "output", "model.step"), "step");

                var before = SHA256.HashData(boxBytes);
                var overwrite = await InvokeAdapterAsync(harness!, derivedImageId!, job, PrimitiveRequest("box", new Dictionary<string, object>
                {
                    ["lengthMm"] = 1,
                    ["widthMm"] = 1,
                    ["heightMm"] = 1,
                }));
                RequireErrorResponse(overwrite, "artifact-invalid");
                Require(before.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(Path.Combine(job, "output", "model.step")))), "failed overwrite changed existing bytes");
            });

            await ScenarioAsync(scenarios, "cylinder-formula", async () =>
            {
                var job = NewJob(tempRoot, "cylinder");
                var result = await InvokeAdapterAsync(harness!, derivedImageId!, job, PrimitiveRequest("cylinder", new Dictionary<string, object>
                {
                    ["radiusMm"] = 5,
                    ["heightMm"] = 12,
                }));
                RequireSuccess(result.Process, "cylinder");
                RequireApproximately(result.Response.GetProperty("measurement").GetProperty("volumeMm3").GetDouble(), Math.PI * 5 * 5 * 12, 1e-9, "cylinder volume");
                ValidatePrimitiveProvenance(result.Response, "cylinder", new[] { "heightMm", "radiusMm" });
                (cylinderBytes, cylinderDigest) = ValidateArtifact(result.Response, Path.Combine(job, "output", "model.step"), "step");
            });

            await ScenarioAsync(scenarios, "manual-rectangle-circle-extrude-cut-hole-and-preview", async () =>
            {
                var rectangleJob = NewJob(tempRoot, "manual-rectangle-add");
                var rectangle = await InvokeAdapterAsync(
                    harness!,
                    derivedImageId!,
                    rectangleJob,
                    ManualSketchExtrudeAddRequest("rectangle", 10, 20, depthMm: 30));
                RequireSuccess(rectangle.Process, "manual rectangle add");
                RequireSuccessResponse(rectangle.Response, "manualSketchExtrudeAdd");
                RequireApproximately(rectangle.Response.GetProperty("measurement").GetProperty("volumeMm3").GetDouble(), 6000.0, 1e-9, "manual rectangle volume");
                ValidateManualProvenance(rectangle.Response, "sketchExtrudeAdd", "rectangle");
                var (rectangleBytes, rectangleDigest) = ValidateArtifact(rectangle.Response, Path.Combine(rectangleJob, "output", "model.step"), "step");

                var circleJob = NewJob(tempRoot, "manual-circle-add");
                var circle = await InvokeAdapterAsync(
                    harness!,
                    derivedImageId!,
                    circleJob,
                    ManualSketchExtrudeAddRequest("circle", 5, 0, depthMm: 12));
                RequireSuccess(circle.Process, "manual circle add");
                RequireSuccessResponse(circle.Response, "manualSketchExtrudeAdd");
                RequireApproximately(circle.Response.GetProperty("measurement").GetProperty("volumeMm3").GetDouble(), Math.PI * 5 * 5 * 12, 1e-8, "manual circle volume");
                ValidateManualProvenance(circle.Response, "sketchExtrudeAdd", "circle");

                var cutJob = NewJob(tempRoot, "manual-rectangle-cut");
                File.WriteAllBytes(Path.Combine(cutJob, "input", "base.step"), boxBytes);
                var cut = await InvokeAdapterAsync(
                    harness!,
                    derivedImageId!,
                    cutJob,
                    ManualSketchExtrudeCutRequest("base", boxDigest!, "rectangle", 4, 5, depthMm: 30));
                RequireSuccess(cut.Process, "manual rectangle cut");
                RequireSuccessResponse(cut.Response, "manualSketchExtrudeCut");
                var cutVolume = cut.Response.GetProperty("measurement").GetProperty("volumeMm3").GetDouble();
                Require(cutVolume > 0 && cutVolume < 6000, "manual cut did not subtract real solid volume");
                ValidateManualProvenance(cut.Response, "sketchExtrudeCut", "rectangle");
                var (cutBytes, cutDigest) = ValidateArtifact(cut.Response, Path.Combine(cutJob, "output", "model.step"), "step");

                var holeJob = NewJob(tempRoot, "manual-hole-cut");
                File.WriteAllBytes(Path.Combine(holeJob, "input", "base.step"), boxBytes);
                var hole = await InvokeAdapterAsync(
                    harness!,
                    derivedImageId!,
                    holeJob,
                    ManualHoleCutRequest("base", boxDigest!, radiusMm: 2, depthMm: 30, xMm: 0, yMm: 0, zMm: 0));
                RequireSuccess(hole.Process, "manual hole cut");
                RequireSuccessResponse(hole.Response, "manualHoleCut");
                var holeVolume = hole.Response.GetProperty("measurement").GetProperty("volumeMm3").GetDouble();
                Require(holeVolume > 0 && holeVolume < 6000, "manual hole did not subtract real solid volume");
                ValidateManualProvenance(hole.Response, "holeCut", null);

                var previewJob = NewJob(tempRoot, "manual-complete-preview");
                File.WriteAllBytes(Path.Combine(previewJob, "input", "cut.step"), cutBytes);
                var preview = await InvokeAdapterAsync(
                    harness!,
                    derivedImageId!,
                    previewJob,
                    SingleSourcePreviewRequest("cut", cutDigest, IdentityTransform()));
                RequireSuccess(preview.Process, "manual preview");
                RequireSuccessResponse(preview.Response, "createPreview");
                ValidateArtifact(preview.Response, Path.Combine(previewJob, "output", "preview.glb"), "glb");
                ValidateGlb(File.ReadAllBytes(Path.Combine(previewJob, "output", "preview.glb")), new[] { "entity" });
                Require(rectangleBytes.Length > 0 && circle.Response.GetProperty("artifact").GetProperty("byteLength").GetInt64() > 0, "manual artifacts were empty");
            });

            await ScenarioAsync(scenarios, "manual-protocol-hostiles-and-unimplemented-edit-operations", async () =>
            {
                var unknown = await InvokeAdapterAsync(
                    harness!,
                    derivedImageId!,
                    NewJob(tempRoot, "manual-unknown-member"),
                    JsonSerializer.Serialize(new
                    {
                        schema = RequestSchema,
                        operation = "manualSketchExtrudeAdd",
                        profile = new { kind = "rectangle", plane = "xy", widthMm = 1, heightMm = 1, script = "forbidden" },
                        depthMm = 1,
                    }, JsonOptions));
                RequireErrorResponse(unknown, "invalid-request");

                var unsupportedPlane = await InvokeAdapterAsync(
                    harness!,
                    derivedImageId!,
                    NewJob(tempRoot, "manual-plane"),
                    ManualSketchExtrudeAddRequest("rectangle", 1, 1, depthMm: 1, plane: "xz"));
                RequireErrorResponse(unsupportedPlane, "invalid-parameter");

                var badSource = await InvokeAdapterAsync(
                    harness!,
                    derivedImageId!,
                    NewJob(tempRoot, "manual-bad-source"),
                    ManualHoleCutRequest("../base", "sha256:" + new string('0', 64), 1, 1, 0, 0, 0));
                RequireErrorResponse(badSource, "invalid-parameter");

                var fillet = await InvokeAdapterAsync(
                    harness!,
                    derivedImageId!,
                    NewJob(tempRoot, "manual-fillet"),
                    JsonSerializer.Serialize(new { schema = RequestSchema, operation = "manualFillet", source = new { inputSlot = "base", expectedDigest = "sha256:" + new string('0', 64) } }, JsonOptions));
                RequireErrorResponse(fillet, "unsupported-operation");
            });

            await ScenarioAsync(scenarios, "dynamic-bearing-creation", async () =>
            {
                var item = SelectCatalogItem(firstCatalog.Response, "bearings", null);
                var job = NewJob(tempRoot, "bearing");
                var result = await InvokeAdapterAsync(harness!, derivedImageId!, job, CatalogItemRequest(catalogDigest!, item));
                RequireSuccess(result.Process, "bearing");
                RequireSuccessResponse(result.Response, "createCatalogItem");
                ValidateCatalogProvenance(result.Response, catalogDigest!, item);
                (_, bearingDigest) = ValidateArtifact(result.Response, Path.Combine(job, "output", "model.step"), "step");
            });

            await ScenarioAsync(scenarios, "dynamic-spur-gear-creation", async () =>
            {
                var item = SelectCatalogItem(firstCatalog.Response, "gears", "Spur Gear");
                var job = NewJob(tempRoot, "spur-gear");
                var result = await InvokeAdapterAsync(harness!, derivedImageId!, job, CatalogItemRequest(catalogDigest!, item));
                RequireSuccess(result.Process, "spur gear");
                RequireSuccessResponse(result.Response, "createCatalogItem");
                ValidateCatalogProvenance(result.Response, catalogDigest!, item);
                (_, gearDigest) = ValidateArtifact(result.Response, Path.Combine(job, "output", "model.step"), "step");
            });

            await ScenarioAsync(scenarios, "catalog-digest-and-parameter-fail-closed", async () =>
            {
                var item = SelectCatalogItem(firstCatalog.Response, "gears", "Spur Gear");
                var wrongDigest = new string('0', 64);
                var mismatch = await InvokeAdapterAsync(
                    harness!,
                    derivedImageId!,
                    NewJob(tempRoot, "catalog-mismatch"),
                    CatalogItemRequest($"sha256:{wrongDigest}", item));
                RequireErrorResponse(mismatch, "catalog-mismatch");
                var badParameters = ParametersFor(item);
                badParameters["python"] = "forbidden";
                var unknown = await InvokeAdapterAsync(
                    harness!,
                    derivedImageId!,
                    NewJob(tempRoot, "unknown-parameter"),
                    JsonSerializer.Serialize(new
                    {
                        schema = RequestSchema,
                        operation = "createCatalogItem",
                        catalogDigest,
                        itemId = item.Id,
                        parameters = badParameters,
                    }, JsonOptions));
                RequireErrorResponse(unknown, "invalid-parameter");
            });

            await ScenarioAsync(scenarios, "two-part-hierarchical-self-contained-glb", async () =>
            {
                var job = NewJob(tempRoot, "preview");
                var input = Path.Combine(job, "input");
                File.WriteAllBytes(Path.Combine(input, "box.step"), boxBytes);
                File.WriteAllBytes(Path.Combine(input, "cylinder.step"), cylinderBytes);
                var request = PreviewRequest(boxDigest!, cylinderDigest!);
                var result = await InvokeAdapterAsync(harness!, derivedImageId!, job, request);
                RequireSuccess(result.Process, "preview");
                RequireSuccessResponse(result.Response, "createPreview");
                Require(result.Response.GetProperty("entityCount").GetInt32() == 2, "preview entity count drifted");
                ValidatePreviewProvenance(result.Response, boxDigest!, boxBytes.Length, cylinderDigest!, cylinderBytes.Length);
                var artifact = Path.Combine(job, "output", "preview.glb");
                var glbBytes = File.ReadAllBytes(artifact);
                (_, previewDigest) = ValidateArtifact(result.Response, artifact, "glb");
                ValidateGlb(glbBytes, new[] { "assembly-root", "assembly-child" });
            });

            await ScenarioAsync(scenarios, "sealed-input-digest-and-rigid-transform", async () =>
            {
                var digestJob = NewJob(tempRoot, "bad-seal");
                File.WriteAllBytes(Path.Combine(digestJob, "input", "box.step"), boxBytes);
                File.WriteAllBytes(Path.Combine(digestJob, "input", "cylinder.step"), cylinderBytes);
                var wrong = PreviewRequest("sha256:" + new string('0', 64), cylinderDigest!);
                RequireErrorResponse(await InvokeAdapterAsync(harness!, derivedImageId!, digestJob, wrong), "artifact-invalid");

                var transformJob = NewJob(tempRoot, "bad-transform");
                File.WriteAllBytes(Path.Combine(transformJob, "input", "box.step"), boxBytes);
                File.WriteAllBytes(Path.Combine(transformJob, "input", "cylinder.step"), cylinderBytes);
                var invalid = PreviewRequest(boxDigest!, cylinderDigest!, scaleRoot: true);
                RequireErrorResponse(await InvokeAdapterAsync(harness!, derivedImageId!, transformJob, invalid), "invalid-parameter");

                var shortTransformJob = NewJob(tempRoot, "short-transform");
                File.WriteAllBytes(Path.Combine(shortTransformJob, "input", "box.step"), boxBytes);
                var shortTransform = SingleSourcePreviewRequest("box", boxDigest!, Enumerable.Repeat(0.0, 15).ToArray());
                RequireErrorResponse(await InvokeAdapterAsync(harness!, derivedImageId!, shortTransformJob, shortTransform), "invalid-parameter");
            });

            await ScenarioAsync(scenarios, "path-ads-traversal-and-sealed-step-fail-closed", async () =>
            {
                foreach (var slot in new[] { "../box", "box:ads", "/box", "box\\other", "..", "." })
                {
                    var pathJob = NewJob(tempRoot, "path-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
                    var request = SingleSourcePreviewRequest(slot, "sha256:" + new string('0', 64), IdentityTransform());
                    RequireErrorResponse(await InvokeAdapterAsync(harness!, derivedImageId!, pathJob, request), "invalid-parameter");
                }

                var malformedJob = NewJob(tempRoot, "malformed-step");
                var malformed = Encoding.ASCII.GetBytes("ISO-10303-21;\nTHIS IS NOT STEP\nEND-ISO-10303-21;\n");
                File.WriteAllBytes(Path.Combine(malformedJob, "input", "broken.step"), malformed);
                RequireErrorResponse(
                    await InvokeAdapterAsync(harness!, derivedImageId!, malformedJob, SingleSourcePreviewRequest("broken", Sha256(malformed), IdentityTransform())),
                    "artifact-invalid");

                var oversizedJob = NewJob(tempRoot, "oversized-step");
                var oversizedPath = Path.Combine(oversizedJob, "input", "huge.step");
                using (var stream = new FileStream(oversizedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.SetLength(64L * 1024 * 1024 + 1);
                    stream.Flush(flushToDisk: true);
                }
                RequireErrorResponse(
                    await InvokeAdapterAsync(harness!, derivedImageId!, oversizedJob, SingleSourcePreviewRequest("huge", "sha256:" + new string('0', 64), IdentityTransform())),
                    "resource-limit");
            });

            await ScenarioAsync(scenarios, "dag-cycle-and-depth-bounds", async () =>
            {
                var cycleJob = NewJob(tempRoot, "dag-cycle");
                RequireErrorResponse(
                    await InvokeAdapterAsync(harness!, derivedImageId!, cycleJob, HierarchyPreviewRequest(boxDigest!, 2, cycle: true)),
                    "invalid-request");
                var depthJob = NewJob(tempRoot, "dag-depth");
                RequireErrorResponse(
                    await InvokeAdapterAsync(harness!, derivedImageId!, depthJob, HierarchyPreviewRequest(boxDigest!, 257, cycle: false)),
                    "resource-limit");
            });

            await ScenarioAsync(scenarios, "atomic-output-race-has-one-winner", async () =>
            {
                var raceJob = NewJob(tempRoot, "output-race");
                var first = InvokeAdapterAsync(harness!, derivedImageId!, raceJob, PrimitiveRequest("box", new Dictionary<string, object>
                {
                    ["lengthMm"] = 4,
                    ["widthMm"] = 5,
                    ["heightMm"] = 6,
                }));
                var second = InvokeAdapterAsync(harness!, derivedImageId!, raceJob, PrimitiveRequest("cylinder", new Dictionary<string, object>
                {
                    ["radiusMm"] = 3,
                    ["heightMm"] = 7,
                }));
                var results = await Task.WhenAll(first, second);
                var winners = results.Where(result => result.Process.ExitCode == 0).ToArray();
                var losers = results.Where(result => result.Process.ExitCode == 2).ToArray();
                Require(winners.Length == 1 && losers.Length == 1, "new-only output race did not produce exactly one winner");
                RequireErrorResponse(losers[0], "artifact-invalid");
                RequireSuccessResponse(winners[0].Response, "createPrimitive");
                ValidateArtifact(winners[0].Response, Path.Combine(raceJob, "output", "model.step"), "step");
            });

            await ScenarioAsync(scenarios, "host-reparse-and-hardlink-rejection", () =>
            {
                var linkJob = NewJob(tempRoot, "host-links");
                var original = Path.Combine(linkJob, "output", "sealed.step");
                File.WriteAllBytes(original, boxBytes);
                var hardLink = Path.Combine(linkJob, "output", "sealed-hardlink.step");
                Require(CreateHardLink(hardLink, original, IntPtr.Zero), $"CreateHardLink failed: {Marshal.GetLastWin32Error()}");
                var hardLinkRejected = false;
                try
                {
                    ReadBoundedSealedHostFile(original, 64 * 1024 * 1024);
                }
                catch (InvalidOperationException)
                {
                    hardLinkRejected = true;
                }
                Require(hardLinkRejected, "host accepted a multiply linked artifact");
                File.Delete(hardLink);
                ReadBoundedSealedHostFile(original, 64 * 1024 * 1024);

                var symbolicLink = Path.Combine(linkJob, "output", "sealed-symlink.step");
                var symlinkSupported = true;
                try
                {
                    File.CreateSymbolicLink(symbolicLink, original);
                }
                catch (UnauthorizedAccessException)
                {
                    symlinkSupported = false;
                }
                catch (IOException)
                {
                    symlinkSupported = false;
                }
                if (symlinkSupported)
                {
                    try
                    {
                        var symlinkRejected = false;
                        try
                        {
                            ReadBoundedSealedHostFile(symbolicLink, 64 * 1024 * 1024);
                        }
                        catch (InvalidOperationException)
                        {
                            symlinkRejected = true;
                        }
                        Require(symlinkRejected, "host accepted a reparse-point artifact");
                    }
                    finally
                    {
                        File.Delete(symbolicLink);
                    }
                }
                return Task.CompletedTask;
            });

            await ScenarioAsync(scenarios, "no-residual-smoke-containers", async () =>
            {
                await harness!.AssertNoRunContainersAsync();
            });

            started.Stop();
            var evidence = await PublishEvidenceAsync(
                harness!,
                runId,
                startedUtc,
                DateTimeOffset.UtcNow,
                started.ElapsedMilliseconds,
                smokeRoot,
                industrialRoot,
                artifactRoot,
                derivedImageId!,
                catalogDigest!,
                catalogBytes,
                packageDigest!,
                boxDigest!,
                cylinderDigest!,
                bearingDigest!,
                gearDigest!,
                previewDigest!,
                scenarios);
            Console.WriteLine(evidence);
            return 0;
        }
        catch (Exception exception)
        {
            started.Stop();
            Console.Error.WriteLine($"Photon CAD industrial smoke FAILED after {started.ElapsedMilliseconds} ms: {exception.Message}");
            return 1;
        }
        finally
        {
            if (harness is not null)
            {
                await RemoveOwnedImageReferenceAsync(harness, derivedStagingReference);
                await RemoveOwnedImageReferenceAsync(harness, baseStagingReference);
            }
            DeleteOwnedTemporaryTree(tempRoot);
        }
    }

    private static async Task<string> PublishEvidenceAsync(
        HardenedDockerHarness harness,
        string runId,
        DateTimeOffset startedUtc,
        DateTimeOffset completedUtc,
        long durationMilliseconds,
        string smokeRoot,
        string industrialRoot,
        string artifactRoot,
        string derivedImageId,
        string catalogDigest,
        int catalogBytes,
        string packageDigest,
        string boxDigest,
        string cylinderDigest,
        string bearingDigest,
        string gearDigest,
        string previewDigest,
        IReadOnlyCollection<ScenarioReceipt> scenarios)
    {
        Require(IsDigest(derivedImageId), "cannot publish evidence without an exact derived image ID");
        Directory.CreateDirectory(artifactRoot);
        Require((File.GetAttributes(artifactRoot) & FileAttributes.ReparsePoint) == 0, "artifact root is a reparse point");
        var runRoot = Path.Combine(artifactRoot, "runs");
        Directory.CreateDirectory(runRoot);
        Require((File.GetAttributes(runRoot) & FileAttributes.ReparsePoint) == 0, "run evidence root is a reparse point");
        var sourceHashes = ComputeSourceHashes(smokeRoot, industrialRoot);
        var receipt = new
        {
            schema = "photon.cad.industrial.smoke-receipt/v2",
            runId,
            passed = true,
            startedUtc = startedUtc.ToString("O", CultureInfo.InvariantCulture),
            completedUtc = completedUtc.ToString("O", CultureInfo.InvariantCulture),
            durationMilliseconds,
            sourceHashes,
            baseImage = new { lookupTag = BaseImage, exactId = BaseImageId },
            derivedImage = new { exactId = derivedImageId, plannedFriendlyTag = DerivedImage, runsUsedExactIdOnly = true },
            catalog = new { digest = catalogDigest, byteLength = catalogBytes, packageDigest },
            artifacts = new { boxDigest, cylinderDigest, bearingDigest, gearDigest, previewDigest },
            runtimePolicy = new
            {
                network = "none",
                readOnlyRoot = true,
                user = "65532:65532",
                capabilities = "ALL dropped",
                noNewPrivileges = true,
                pidsLimit = 64,
                memory = "2g",
                cpus = 2,
                inputMount = "/photon-input:read-only",
                outputMount = "/photon-output:read-write",
                exactContainerCleanup = true,
            },
            commands = new[]
            {
                $"docker image tag {BaseImageId} photon-cad-geometry-staging:<run-id>",
                "docker build --pull=false --network=none --build-arg PHOTON_CAD_GEOMETRY_BASE=photon-cad-geometry-staging:<run-id> --tag photon-cad-industrial-staging:<run-id> --file industrial/Dockerfile industrial",
                $"docker run [hardened policy and unique name/CID] {derivedImageId}",
                $"docker image tag {derivedImageId} {DerivedImage} [only after immutable receipt]",
            },
            redistribution = new { status = "blocked-pending-license-review", aggregateLicenseClaim = false },
            scenarios,
        };
        var indentedJson = new JsonSerializerOptions(JsonOptions) { WriteIndented = true };
        var receiptBytes = JsonSerializer.SerializeToUtf8Bytes(receipt, indentedJson);
        var receiptRelativePath = $"runs/receipt-{runId}.json";
        var receiptPath = Path.Combine(artifactRoot, receiptRelativePath.Replace('/', Path.DirectorySeparatorChar));
        WriteImmutableAtomic(receiptPath, receiptBytes);
        var receiptSha = Sha256(receiptBytes);

        var previousFriendlyImageId = await TryInspectImageIdAsync(harness, DerivedImage);
        var promoted = false;
        try
        {
            RequireSuccess(
                await harness.RunDockerAsync(
                    new[] { "image", "tag", derivedImageId, DerivedImage },
                    timeout: TimeSpan.FromSeconds(30),
                    stdoutLimit: 256 * 1024,
                    stderrLimit: 256 * 1024),
                "promote verified derived image tag");
            promoted = true;
            using (var promotedInspect = await InspectImageAsync(harness, DerivedImage))
            {
                Require(promotedInspect.RootElement.GetProperty("Id").GetString() == derivedImageId, "friendly derived tag promotion drifted");
            }

            var reportBytes = Encoding.UTF8.GetBytes(BuildImplementationReport(
                runId,
                startedUtc,
                completedUtc,
                durationMilliseconds,
                derivedImageId,
                receiptRelativePath,
                receiptSha,
                catalogDigest,
                packageDigest,
                boxDigest,
                cylinderDigest,
                bearingDigest,
                gearDigest,
                previewDigest,
                sourceHashes,
                scenarios));
            var reportPath = Path.Combine(artifactRoot, "IMPLEMENTATION-REPORT.md");
            WriteReplaceAtomic(reportPath, reportBytes);
            var reportSha = Sha256(reportBytes);

            var canonical = new
            {
                schema = "photon.cad.industrial.evidence-selection/v1",
                selectedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                runId,
                receipt = new { path = receiptRelativePath, sha256 = receiptSha, immutable = true },
                report = new { path = "IMPLEMENTATION-REPORT.md", sha256 = reportSha },
                baseImageId = BaseImageId,
                derivedImageId,
                friendlyTag = DerivedImage,
                friendlyTagVerified = true,
                previousFriendlyImageId,
                scenarioCount = scenarios.Count,
            };
            var canonicalBytes = JsonSerializer.SerializeToUtf8Bytes(canonical, indentedJson);
            WriteReplaceAtomic(Path.Combine(artifactRoot, "last-smoke-receipt.json"), canonicalBytes);
            Require(Sha256(File.ReadAllBytes(receiptPath)) == receiptSha, "immutable receipt readback mismatch");
            Require(Sha256(File.ReadAllBytes(reportPath)) == reportSha, "implementation report readback mismatch");
            return JsonSerializer.Serialize(new
            {
                schema = "photon.cad.industrial.smoke-result/v2",
                passed = true,
                runId,
                durationMilliseconds,
                scenarioCount = scenarios.Count,
                baseImageId = BaseImageId,
                derivedImageId,
                catalogDigest,
                artifacts = new { boxDigest, cylinderDigest, bearingDigest, gearDigest, previewDigest },
                evidence = new { receipt = receiptRelativePath, receiptSha, reportSha },
            }, JsonOptions);
        }
        catch
        {
            if (promoted)
            {
                await RestoreFriendlyImageReferenceAsync(harness, previousFriendlyImageId);
            }
            throw;
        }
    }

    private static SortedDictionary<string, string> ComputeSourceHashes(string smokeRoot, string industrialRoot)
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(smokeRoot, "..", "..", ".."));
        var paths = new[]
        {
            Path.Combine(industrialRoot, "Dockerfile"),
            Path.Combine(industrialRoot, "README.md"),
            Path.Combine(industrialRoot, "photon_industrial_adapter.py"),
            Path.Combine(industrialRoot, "fixtures", "catalog.request.json"),
            Path.Combine(industrialRoot, "fixtures", "protocol-golden-v1.json"),
            Path.Combine(industrialRoot, "schemas", "catalog-v1.schema.json"),
            Path.Combine(industrialRoot, "schemas", "request-v1.schema.json"),
            Path.Combine(industrialRoot, "schemas", "response-v1.schema.json"),
            Path.Combine(smokeRoot, "HardenedDockerHarness.cs"),
            Path.Combine(smokeRoot, "PhotonCadIndustrial.ContainerSmoke.csproj"),
            Path.Combine(smokeRoot, "Program.cs"),
        };
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            Require(File.Exists(path), $"evidence source is missing: {path}");
            Require((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, $"evidence source is a reparse point: {path}");
            var relative = Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/');
            result.Add(relative, Sha256(File.ReadAllBytes(path)));
        }
        return result;
    }

    private static string BuildImplementationReport(
        string runId,
        DateTimeOffset startedUtc,
        DateTimeOffset completedUtc,
        long durationMilliseconds,
        string derivedImageId,
        string receiptRelativePath,
        string receiptSha,
        string catalogDigest,
        string packageDigest,
        string boxDigest,
        string cylinderDigest,
        string bearingDigest,
        string gearDigest,
        string previewDigest,
        IReadOnlyDictionary<string, string> sourceHashes,
        IReadOnlyCollection<ScenarioReceipt> scenarios)
    {
        var report = new StringBuilder();
        report.AppendLine("# Photon CAD industrial container verification");
        report.AppendLine();
        report.AppendLine($"Run `{runId}` passed {scenarios.Count}/{scenarios.Count} hardened scenarios in {durationMilliseconds} ms.");
        report.AppendLine($"Window: `{startedUtc:O}` through `{completedUtc:O}`.");
        report.AppendLine();
        report.AppendLine("## Immutable identities and evidence");
        report.AppendLine();
        report.AppendLine($"- Exact base image: `{BaseImageId}` (lookup tag `{BaseImage}`)");
        report.AppendLine($"- Exact derived image: `{derivedImageId}`; all scenarios ran this ID, never a friendly tag");
        report.AppendLine($"- Friendly tag promoted only after the receipt was durable: `{DerivedImage}`");
        report.AppendLine($"- Immutable receipt: `{receiptRelativePath}` (`{receiptSha}`)");
        report.AppendLine($"- Catalog: `{catalogDigest}`; bd_warehouse package content: `{packageDigest}`");
        report.AppendLine();
        report.AppendLine("## Sealed artifacts");
        report.AppendLine();
        report.AppendLine($"- Box STEP: `{boxDigest}`");
        report.AppendLine($"- Cylinder STEP: `{cylinderDigest}`");
        report.AppendLine($"- Discovered bearing STEP: `{bearingDigest}`");
        report.AppendLine($"- Spur gear STEP: `{gearDigest}`");
        report.AppendLine($"- Self-contained tagged GLB: `{previewDigest}`");
        report.AppendLine();
        report.AppendLine("## Security result");
        report.AppendLine();
        report.AppendLine("The verified runner used network-none, a read-only root, non-root UID/GID 65532, all capabilities dropped, no-new-privileges, bounded CPU/memory/PIDs, separate read-only input and read-write output mounts, host deadlines/output caps, and exact container kill/remove/absence checks. Strict JSON, hostile STEP, path/ADS/traversal, DAG, 15-value transform, atomic output race, hard-link and supported reparse-point, timeout, and stdout-flood cases failed closed.");
        report.AppendLine();
        report.AppendLine("Redistribution remains **blocked pending an independent dependency/license review**. This report makes no aggregate license, compliance, or SBOM claim.");
        report.AppendLine();
        report.AppendLine("## Source hashes");
        report.AppendLine();
        foreach (var item in sourceHashes)
        {
            report.AppendLine($"- `{item.Value}`  `{item.Key}`");
        }
        report.AppendLine();
        report.AppendLine("## Scenarios");
        report.AppendLine();
        foreach (var scenario in scenarios)
        {
            report.AppendLine($"- PASS `{scenario.Name}` ({scenario.DurationMilliseconds} ms)");
        }
        return report.ToString();
    }

    private static void WriteImmutableAtomic(string destination, byte[] bytes)
    {
        Require(!File.Exists(destination), $"immutable evidence already exists: {destination}");
        WriteAtomic(destination, bytes, replace: false);
    }

    private static void WriteReplaceAtomic(string destination, byte[] bytes) =>
        WriteAtomic(destination, bytes, replace: true);

    private static void WriteAtomic(string destination, byte[] bytes, bool replace)
    {
        var full = Path.GetFullPath(destination);
        var directory = Path.GetDirectoryName(full) ?? throw new InvalidOperationException("evidence path has no directory");
        Require(Directory.Exists(directory), "evidence directory is missing");
        Require((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0, "evidence directory is a reparse point");
        if (File.Exists(full))
        {
            Require(replace, "immutable evidence destination exists");
            Require((File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0, "evidence destination is a reparse point");
        }
        var temporary = Path.Combine(directory, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                temporary,
                new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    Options = FileOptions.WriteThrough,
                }))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, full, overwrite: replace);
            Require(Sha256(File.ReadAllBytes(full)) == Sha256(bytes), "atomic evidence readback mismatch");
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task<string?> TryInspectImageIdAsync(HardenedDockerHarness harness, string image)
    {
        var inspect = await harness.RunDockerAsync(
            new[] { "image", "inspect", image, "--format", "{{.Id}}" },
            timeout: TimeSpan.FromSeconds(20),
            stdoutLimit: 64 * 1024,
            stderrLimit: 64 * 1024);
        if (inspect.ExitCode != 0)
        {
            return null;
        }
        var id = inspect.StandardOutput.Trim();
        Require(IsDigest(id), "friendly image tag resolved to an invalid ID");
        return id;
    }

    private static async Task RestoreFriendlyImageReferenceAsync(HardenedDockerHarness harness, string? previousImageId)
    {
        ProcessResult rollback;
        if (previousImageId is null)
        {
            rollback = await harness.RunDockerAsync(
                new[] { "image", "rm", DerivedImage },
                timeout: TimeSpan.FromSeconds(30),
                stdoutLimit: 256 * 1024,
                stderrLimit: 256 * 1024);
        }
        else
        {
            rollback = await harness.RunDockerAsync(
                new[] { "image", "tag", previousImageId, DerivedImage },
                timeout: TimeSpan.FromSeconds(30),
                stdoutLimit: 256 * 1024,
                stderrLimit: 256 * 1024);
        }
        RequireSuccess(rollback, "restore friendly image tag after evidence failure");
        var restored = await TryInspectImageIdAsync(harness, DerivedImage);
        Require(restored == previousImageId, "friendly image tag rollback failed");
    }

    private static string FindSmokeRoot()
    {
        var candidate = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        Require(Path.GetFileName(candidate).Equals("PhotonCadIndustrial.ContainerSmoke", StringComparison.Ordinal), "cannot locate smoke source root");
        return candidate;
    }

    private static void AssertNewOnlyRoots(string smokeRoot, string industrialRoot)
    {
        Require(Directory.Exists(smokeRoot) && Directory.Exists(industrialRoot), "new-only roots are missing");
        Require(Path.GetFileName(smokeRoot) == "PhotonCadIndustrial.ContainerSmoke", "unexpected smoke root");
        Require(Path.GetFileName(industrialRoot) == "industrial", "unexpected industrial root");
        Require(Path.GetFileName(Directory.GetParent(industrialRoot)!.FullName) == "PhotonCadRuntime.Container", "unexpected industrial parent");
    }

    private static void ParseContractFiles(string industrialRoot)
    {
        foreach (var path in new[]
        {
            Path.Combine(industrialRoot, "schemas", "request-v1.schema.json"),
            Path.Combine(industrialRoot, "schemas", "response-v1.schema.json"),
            Path.Combine(industrialRoot, "schemas", "catalog-v1.schema.json"),
            Path.Combine(industrialRoot, "fixtures", "protocol-golden-v1.json"),
        })
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            Require(document.RootElement.ValueKind == JsonValueKind.Object, $"invalid contract JSON: {path}");
        }
    }

    private static void ScanForbiddenAdapterApis(string industrialRoot)
    {
        var source = File.ReadAllText(Path.Combine(industrialRoot, "photon_industrial_adapter.py"));
        var forbidden = new[]
        {
            @"\bsubprocess\b", @"\bsocket\b", @"\bPopen\b", @"\bos\.system\s*\(",
            @"\beval\s*\(", @"\bexec\s*\(", @"\b__import__\s*\(", @"\bimport_module\s*\(",
            @"\bpip\s+install\b", @"\brequests\.", @"\burllib\.", @"shell\s*=\s*True",
        };
        foreach (var pattern in forbidden)
        {
            Require(!Regex.IsMatch(source, pattern, RegexOptions.CultureInvariant), $"forbidden adapter API matched: {pattern}");
        }
    }

    private static async Task ScenarioAsync(List<ScenarioReceipt> receipts, string name, Func<Task> action)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"scenario {name} failed: {exception.Message}", exception);
        }
        stopwatch.Stop();
        receipts.Add(new ScenarioReceipt(name, stopwatch.ElapsedMilliseconds, true));
    }

    private static async Task<JsonDocument> InspectImageAsync(HardenedDockerHarness harness, string image)
    {
        var result = await harness.RunDockerAsync(
            new[] { "image", "inspect", image, "--format", "{{json .}}" },
            timeout: TimeSpan.FromSeconds(30));
        RequireSuccess(result, $"inspect {image}");
        return JsonDocument.Parse(result.StandardOutput);
    }

    private static async Task RemoveOwnedImageReferenceAsync(HardenedDockerHarness harness, string reference)
    {
        Require(
            Regex.IsMatch(reference, "^photon-cad-(geometry|industrial)-staging:[0-9a-f]{32}$", RegexOptions.CultureInvariant),
            "refusing to remove a non-staging image reference");
        var inspect = await harness.RunDockerAsync(
            new[] { "image", "inspect", reference, "--format", "{{.Id}}" },
            timeout: TimeSpan.FromSeconds(20),
            stdoutLimit: 64 * 1024,
            stderrLimit: 64 * 1024);
        if (inspect.ExitCode != 0)
        {
            return;
        }
        var remove = await harness.RunDockerAsync(
            new[] { "image", "rm", reference },
            timeout: TimeSpan.FromSeconds(30),
            stdoutLimit: 256 * 1024,
            stderrLimit: 256 * 1024);
        RequireSuccess(remove, $"remove owned staging image reference {reference}");
        var verify = await harness.RunDockerAsync(
            new[] { "image", "inspect", reference, "--format", "{{.Id}}" },
            timeout: TimeSpan.FromSeconds(20),
            stdoutLimit: 64 * 1024,
            stderrLimit: 64 * 1024);
        Require(verify.ExitCode != 0, $"owned staging image reference remained: {reference}");
    }

    private static void RequireIdenticalImage(ImageIdentity expected, ImageIdentity actual, string label)
    {
        Require(actual.Id == expected.Id, $"{label} image ID drifted");
        Require(actual.RootFsLayers.SequenceEqual(expected.RootFsLayers, StringComparer.Ordinal), $"{label} RootFS drifted");
        Require(actual.ConfigurationDigest == expected.ConfigurationDigest, $"{label} configuration drifted");
        Require(actual.LabelDigest == expected.LabelDigest, $"{label} labels drifted");
    }

    private static bool ImagesAreIdentical(ImageIdentity left, ImageIdentity right) =>
        left.Id == right.Id
        && left.RootFsLayers.SequenceEqual(right.RootFsLayers, StringComparer.Ordinal)
        && left.ConfigurationDigest == right.ConfigurationDigest
        && left.LabelDigest == right.LabelDigest;

    private static void RequireRootFsDescendsFrom(ImageIdentity baseImage, ImageIdentity derivedImage)
    {
        Require(derivedImage.RootFsLayers.Count >= baseImage.RootFsLayers.Count, "derived RootFS is shorter than base RootFS");
        Require(
            derivedImage.RootFsLayers.Take(baseImage.RootFsLayers.Count).SequenceEqual(baseImage.RootFsLayers, StringComparer.Ordinal),
            "derived RootFS does not descend from exact base RootFS");
    }

    private static async Task<AdapterResult> InvokeAdapterAsync(
        HardenedDockerHarness harness,
        string exactImageId,
        string workspace,
        string request) =>
        await InvokeAdapterAsync(harness, exactImageId, workspace, new UTF8Encoding(false, true).GetBytes(request));

    private static async Task<AdapterResult> InvokeAdapterAsync(
        HardenedDockerHarness harness,
        string exactImageId,
        string workspace,
        byte[] request)
    {
        var process = await harness.RunAdapterAsync(
            exactImageId,
            Path.Combine(workspace, "input"),
            Path.Combine(workspace, "output"),
            request);
        var output = process.StandardOutputBytes;
        Require(output.Length >= 2 && output[^1] == (byte)'\n', "adapter output is not one newline-terminated JSON value");
        Require(output.AsSpan(0, output.Length - 1).IndexOf((byte)'\n') < 0, "adapter emitted multiple JSON lines");
        using var document = JsonDocument.Parse(
            output.AsMemory(0, output.Length - 1),
            new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 64 });
        var response = document.RootElement.Clone();
        Require(response.GetProperty("schema").GetString() == ResponseSchema, "response schema drifted");
        Require(process.StandardErrorBytes.Length == 0, "adapter wrote to stderr");
        RejectTransportLeakage(response);
        return new AdapterResult(process, output, response);
    }

    private static void RejectTransportLeakage(JsonElement response)
    {
        var forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "path", "filePath", "filename", "handle", "inputSlot", "temporary", "tempPath",
        };
        var pending = new Stack<(JsonElement Value, int Depth)>();
        pending.Push((response, 0));
        var visited = 0;
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            Require(++visited <= 250_000 && current.Depth <= 64, "response is too complex");
            if (current.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in current.Value.EnumerateObject())
                {
                    Require(!forbidden.Contains(property.Name), $"response exposed transport field {property.Name}");
                    pending.Push((property.Value, current.Depth + 1));
                }
            }
            else if (current.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in current.Value.EnumerateArray())
                {
                    pending.Push((item, current.Depth + 1));
                }
            }
        }
    }

    private static string NewJob(string tempRoot, string name)
    {
        Require(Regex.IsMatch(name, "^[a-z0-9-]+$", RegexOptions.CultureInvariant), "unsafe job name");
        var path = Path.Combine(tempRoot, name);
        Require(!Directory.Exists(path), "duplicate job path");
        Directory.CreateDirectory(path);
        Directory.CreateDirectory(Path.Combine(path, "input"));
        Directory.CreateDirectory(Path.Combine(path, "output"));
        return path;
    }

    private static string CatalogRequest() => JsonSerializer.Serialize(new { schema = RequestSchema, operation = "catalog" }, JsonOptions);

    private static string PrimitiveRequest(string kind, Dictionary<string, object> dimensions) =>
        JsonSerializer.Serialize(new { schema = RequestSchema, operation = "createPrimitive", primitive = new { kind, dimensions } }, JsonOptions);

    private static string ManualSketchExtrudeAddRequest(string kind, double first, double second, double depthMm, string plane = "xy")
    {
        object profile = kind == "rectangle"
            ? new { kind, plane, widthMm = first, heightMm = second }
            : new { kind, plane, radiusMm = first };
        return JsonSerializer.Serialize(new { schema = RequestSchema, operation = "manualSketchExtrudeAdd", profile, depthMm }, JsonOptions);
    }

    private static string ManualSketchExtrudeCutRequest(string inputSlot, string digest, string kind, double first, double second, double depthMm)
    {
        object profile = kind == "rectangle"
            ? new { kind, plane = "xy", widthMm = first, heightMm = second }
            : new { kind, plane = "xy", radiusMm = first };
        return JsonSerializer.Serialize(new
        {
            schema = RequestSchema,
            operation = "manualSketchExtrudeCut",
            source = new { inputSlot, expectedDigest = digest },
            profile,
            depthMm,
        }, JsonOptions);
    }

    private static string ManualHoleCutRequest(
        string inputSlot, string digest, double radiusMm, double depthMm, double xMm, double yMm, double zMm) =>
        JsonSerializer.Serialize(new
        {
            schema = RequestSchema,
            operation = "manualHoleCut",
            source = new { inputSlot, expectedDigest = digest },
            hole = new { radiusMm, depthMm, xMm, yMm, zMm },
        }, JsonOptions);

    private static string CatalogItemRequest(string digest, CatalogItem item) =>
        JsonSerializer.Serialize(new
        {
            schema = RequestSchema,
            operation = "createCatalogItem",
            catalogDigest = digest,
            itemId = item.Id,
            parameters = ParametersFor(item),
        }, JsonOptions);

    private static string PreviewRequest(string boxDigest, string cylinderDigest, bool scaleRoot = false)
    {
        var identity = new double[] { scaleRoot ? 2 : 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
        var translated = new double[] { 1, 0, 0, 20, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
        return JsonSerializer.Serialize(new
        {
            schema = RequestSchema,
            operation = "createPreview",
            sources = new object[]
            {
                new { sourcePartId = "box-part", inputSlot = "box", expectedDigest = boxDigest },
                new { sourcePartId = "cylinder-part", inputSlot = "cylinder", expectedDigest = cylinderDigest },
            },
            occurrences = new object[]
            {
                new { entityId = "assembly-root", sourcePartId = "box-part", parentEntityId = (string?)null, transform = identity },
                new { entityId = "assembly-child", sourcePartId = "cylinder-part", parentEntityId = "assembly-root", transform = translated },
            },
            tessellation = new { linearToleranceMm = 0.1, angularToleranceRad = 0.1 },
        }, JsonOptions);
    }

    private static double[] IdentityTransform() =>
        new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

    private static string SingleSourcePreviewRequest(string inputSlot, string digest, double[] transform) =>
        JsonSerializer.Serialize(new
        {
            schema = RequestSchema,
            operation = "createPreview",
            sources = new object[]
            {
                new { sourcePartId = "part", inputSlot, expectedDigest = digest },
            },
            occurrences = new object[]
            {
                new { entityId = "entity", sourcePartId = "part", parentEntityId = (string?)null, transform },
            },
        }, JsonOptions);

    private static string HierarchyPreviewRequest(string digest, int count, bool cycle)
    {
        Require(count >= 2, "hierarchy fixture is too small");
        var transform = IdentityTransform();
        var occurrences = new List<object>(count);
        for (var index = 0; index < count; index++)
        {
            var entityId = $"entity-{index:D4}";
            string? parent = cycle
                ? $"entity-{(index + count - 1) % count:D4}"
                : index == 0 ? null : $"entity-{index - 1:D4}";
            occurrences.Add(new { entityId, sourcePartId = "part", parentEntityId = parent, transform });
        }
        return JsonSerializer.Serialize(new
        {
            schema = RequestSchema,
            operation = "createPreview",
            sources = new object[]
            {
                new { sourcePartId = "part", inputSlot = "part", expectedDigest = digest },
            },
            occurrences,
        }, JsonOptions);
    }

    private static void ValidateCatalog(JsonElement catalog)
    {
        var categories = catalog.GetProperty("categories").EnumerateArray().ToArray();
        var expected = new[]
        {
            "bearings", "fasteners", "flanges", "gears", "o-rings", "openbuilds",
            "pipes", "retaining-rings", "shaft-keys", "sprockets", "threads",
        };
        Require(categories.Select(category => category.GetProperty("id").GetString()).SequenceEqual(expected), "catalog categories drifted");
        foreach (var missing in new[] { "o-rings", "retaining-rings", "shaft-keys" })
        {
            var category = categories.Single(value => value.GetProperty("id").GetString() == missing);
            Require(category.GetProperty("availability").GetString() == "unsupported", $"{missing} was fabricated");
            Require(category.GetProperty("reason").GetString() == "not-installed-in-pinned-package", $"{missing} reason drifted");
        }
        var items = catalog.GetProperty("items").EnumerateArray().ToArray();
        Require(items.Any(item => item.GetProperty("category").GetString() == "bearings" && item.GetProperty("availability").GetString() == "supported"), "no real bearing was discovered");
        Require(items.Any(item => item.GetProperty("category").GetString() == "gears" && item.GetProperty("title").GetString() == "Spur Gear" && item.GetProperty("availability").GetString() == "supported"), "Spur Gear was not discovered");
    }

    private static void RejectSensitiveCatalogKeys(JsonElement catalog)
    {
        var forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "module", "moduleName", "pythonClass", "className", "file", "filename", "path", "executable", "command",
        };
        var pending = new Stack<JsonElement>();
        pending.Push(catalog);
        var inspected = 0;
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            Require(++inspected <= 250_000, "catalog is too complex");
            if (current.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in current.EnumerateObject())
                {
                    Require(!forbidden.Contains(property.Name), $"catalog exposed forbidden key {property.Name}");
                    pending.Push(property.Value);
                }
            }
            else if (current.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in current.EnumerateArray())
                {
                    pending.Push(item);
                }
            }
        }
    }

    private static CatalogItem SelectCatalogItem(JsonElement response, string category, string? title)
    {
        foreach (var item in response.GetProperty("catalog").GetProperty("items").EnumerateArray())
        {
            if (item.GetProperty("category").GetString() == category
                && item.GetProperty("availability").GetString() == "supported"
                && (title is null || item.GetProperty("title").GetString() == title))
            {
                return new CatalogItem(
                    item.GetProperty("id").GetString()!,
                    item.GetProperty("title").GetString()!,
                    item.GetProperty("parameters").Clone());
            }
        }
        throw new InvalidOperationException($"No supported catalog item for {category}/{title ?? "first"}");
    }

    private static Dictionary<string, object?> ParametersFor(CatalogItem item)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var parameter in item.Parameters.EnumerateArray())
        {
            var required = parameter.GetProperty("required").GetBoolean();
            if (!required)
            {
                continue;
            }
            var name = parameter.GetProperty("id").GetString()!;
            var kind = parameter.GetProperty("kind").GetString();
            result[name] = kind switch
            {
                "choice" => JsonScalar(parameter.GetProperty("choices")[0]),
                "boolean" => false,
                "integer" when name is "tooth_count" or "num_teeth" => 24,
                "integer" => 1,
                "number" when name == "module" => 2.0,
                "number" when name == "pressure_angle" => 20.0,
                "number" when name == "thickness" => 10.0,
                "number" => 1.0,
                _ => throw new InvalidOperationException($"Unsupported public parameter kind {kind}"),
            };
        }
        return result;
    }

    private static object? JsonScalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => throw new InvalidOperationException("Catalog choice is not scalar"),
    };

    private static void ValidatePrimitiveProvenance(JsonElement response, string expectedKind, IReadOnlyCollection<string> parameterNames)
    {
        var provenance = response.GetProperty("provenance");
        RequireExactKeys(provenance, "generator", "kind", "parameters");
        Require(provenance.GetProperty("generator").GetString() == "primitive", "primitive provenance generator drifted");
        Require(provenance.GetProperty("kind").GetString() == expectedKind, "primitive provenance kind drifted");
        RequireExactKeys(provenance.GetProperty("parameters"), parameterNames.ToArray());
    }

    private static void ValidateManualProvenance(JsonElement response, string expectedKind, string? expectedProfileKind)
    {
        var provenance = response.GetProperty("provenance");
        Require(provenance.GetProperty("generator").GetString() == "manual", "manual provenance generator drifted");
        Require(provenance.GetProperty("kind").GetString() == expectedKind, "manual provenance kind drifted");
        if (expectedProfileKind is null) return;
        var profile = provenance.GetProperty("profile");
        Require(profile.GetProperty("kind").GetString() == expectedProfileKind, "manual profile kind drifted");
        Require(profile.GetProperty("plane").GetString() == "xy", "manual profile plane drifted");
    }

    private static void ValidateCatalogProvenance(JsonElement response, string expectedDigest, CatalogItem item)
    {
        Require(response.GetProperty("catalogDigest").GetString() == expectedDigest, "catalog response digest drifted");
        Require(response.GetProperty("itemId").GetString() == item.Id, "catalog response item ID drifted");
        var provenance = response.GetProperty("provenance");
        RequireExactKeys(provenance, "generator", "catalogDigest", "itemId", "parameters");
        Require(provenance.GetProperty("generator").GetString() == "catalog", "catalog provenance generator drifted");
        Require(provenance.GetProperty("catalogDigest").GetString() == expectedDigest, "catalog provenance digest drifted");
        Require(provenance.GetProperty("itemId").GetString() == item.Id, "catalog provenance item ID drifted");
        using var expected = JsonDocument.Parse(JsonSerializer.Serialize(ParametersFor(item), JsonOptions));
        Require(provenance.GetProperty("parameters").GetRawText() == expected.RootElement.GetRawText(), "catalog provenance parameters drifted");
    }

    private static void ValidatePreviewProvenance(
        JsonElement response,
        string boxDigest,
        int boxBytes,
        string cylinderDigest,
        int cylinderBytes)
    {
        var provenance = response.GetProperty("provenance");
        RequireExactKeys(provenance, "sources", "occurrences", "tessellation");
        var sources = provenance.GetProperty("sources").EnumerateArray().ToArray();
        Require(sources.Length == 2, "preview provenance source count drifted");
        Require(sources.Select(source => source.GetProperty("sourcePartId").GetString()).SequenceEqual(new[] { "box-part", "cylinder-part" }), "preview source-part order drifted");
        foreach (var source in sources)
        {
            RequireExactKeys(source, "sourcePartId", "contentDigest", "byteLength");
            var sourcePartId = source.GetProperty("sourcePartId").GetString();
            var expectedDigest = sourcePartId == "box-part" ? boxDigest : cylinderDigest;
            var expectedBytes = sourcePartId == "box-part" ? boxBytes : cylinderBytes;
            Require(source.GetProperty("contentDigest").GetString() == expectedDigest, "preview source digest drifted");
            Require(source.GetProperty("byteLength").GetInt32() == expectedBytes, "preview source length drifted");
        }
        var occurrences = provenance.GetProperty("occurrences").EnumerateArray().ToArray();
        Require(occurrences.Length == 2, "preview provenance occurrence count drifted");
        Require(occurrences.Select(occurrence => occurrence.GetProperty("entityId").GetString()).SequenceEqual(new[] { "assembly-child", "assembly-root" }), "preview occurrence order drifted");
        foreach (var occurrence in occurrences)
        {
            RequireExactKeys(occurrence, "entityId", "sourcePartId", "parentEntityId", "transform");
            Require(occurrence.GetProperty("transform").GetArrayLength() == 16, "preview occurrence transform is not 4x4");
            var entityId = occurrence.GetProperty("entityId").GetString();
            if (entityId == "assembly-root")
            {
                Require(occurrence.GetProperty("sourcePartId").GetString() == "box-part", "root source-part binding drifted");
                Require(occurrence.GetProperty("parentEntityId").ValueKind == JsonValueKind.Null, "root gained a parent");
            }
            else
            {
                Require(occurrence.GetProperty("sourcePartId").GetString() == "cylinder-part", "child source-part binding drifted");
                Require(occurrence.GetProperty("parentEntityId").GetString() == "assembly-root", "child parent drifted");
            }
        }
        var tessellation = provenance.GetProperty("tessellation");
        RequireExactKeys(tessellation, "linearToleranceMm", "angularToleranceRad");
    }

    private static (byte[] Bytes, string Digest) ValidateArtifact(JsonElement response, string path, string format)
    {
        var artifact = response.GetProperty("artifact");
        RequireExactKeys(artifact, "format", "contentDigest", "byteLength");
        Require(artifact.GetProperty("format").GetString() == format, "artifact format drifted");
        var maximumBytes = format == "step" ? 64 * 1024 * 1024 : MaximumGlbBytes;
        var (bytes, digest) = ReadBoundedSealedHostFile(path, maximumBytes);
        Require(artifact.GetProperty("byteLength").GetInt64() == bytes.LongLength, "artifact byte length mismatch");
        Require(artifact.GetProperty("contentDigest").GetString() == digest, "artifact digest mismatch");
        return (bytes, digest);
    }

    private static (byte[] Bytes, string Digest) ReadBoundedSealedHostFile(string path, int maximumBytes)
    {
        var full = Path.GetFullPath(path);
        Require(File.Exists(full), $"artifact missing: {full}");
        Require((File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0, "artifact is a reparse point");
        using var stream = new FileStream(
            full,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.None,
                Options = FileOptions.SequentialScan,
                BufferSize = 64 * 1024,
            });
        Require(stream.Length is > 0 && stream.Length <= maximumBytes, "artifact exceeds host size policy");
        var before = GetFileIdentity(stream.SafeFileHandle);
        Require(before.LinkCount == 1, "artifact has multiple hard links");
        using var bytes = new MemoryStream(checked((int)stream.Length));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var count = stream.Read(buffer, 0, buffer.Length);
            if (count == 0)
            {
                break;
            }
            Require(bytes.Length + count <= maximumBytes, "artifact grew beyond host size policy");
            bytes.Write(buffer, 0, count);
            hash.AppendData(buffer, 0, count);
        }
        var after = GetFileIdentity(stream.SafeFileHandle);
        Require(before == after && after.LinkCount == 1 && bytes.Length == stream.Length, "artifact identity changed while reading");
        var digest = "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        return (bytes.ToArray(), digest);
    }

    private static HostFileIdentity GetFileIdentity(SafeFileHandle handle)
    {
        Require(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "host sealed-file validator requires Windows");
        Require(GetFileInformationByHandle(handle, out var information), $"GetFileInformationByHandle failed: {Marshal.GetLastWin32Error()}");
        return new HostFileIdentity(
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow,
            information.NumberOfLinks,
            ((ulong)information.FileSizeHigh << 32) | information.FileSizeLow,
            ((ulong)information.LastWriteTimeHigh << 32) | information.LastWriteTimeLow);
    }

    private static void RequireExactKeys(JsonElement value, params string[] expected)
    {
        Require(value.ValueKind == JsonValueKind.Object, "expected JSON object");
        var actual = value.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        var orderedExpected = expected.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        Require(actual.SequenceEqual(orderedExpected, StringComparer.Ordinal), $"JSON keys drifted: {string.Join(',', actual)}");
    }

    private static void ValidateGlb(byte[] bytes, IReadOnlyCollection<string> expectedEntities)
    {
        Require(bytes.Length is >= 20 and <= MaximumGlbBytes, "GLB size is outside viewer policy");
        Require(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4)) == 0x46546c67, "GLB magic mismatch");
        Require(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)) == 2, "GLB version mismatch");
        Require(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4)) == bytes.Length, "GLB total length mismatch");
        var offset = 12;
        JsonDocument? jsonDocument = null;
        int? binaryLength = null;
        while (offset < bytes.Length)
        {
            Require(offset + 8 <= bytes.Length, "truncated GLB chunk");
            var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4)));
            var type = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            var start = offset + 8;
            var end = checked(start + length);
            Require(length > 0 && end <= bytes.Length && end % 4 == 0, "invalid GLB chunk bounds");
            if (type == 0x4e4f534a)
            {
                Require(jsonDocument is null && offset == 12 && length <= 32 * 1024 * 1024, "invalid JSON chunk");
                jsonDocument = JsonDocument.Parse(bytes.AsMemory(start, length));
            }
            else if (type == 0x004e4942)
            {
                Require(binaryLength is null, "duplicate binary chunk");
                binaryLength = length;
            }
            else
            {
                throw new InvalidOperationException("unsupported GLB chunk");
            }
            offset = end;
        }
        Require(offset == bytes.Length && jsonDocument is not null, "GLB JSON is missing");
        using (var document = jsonDocument ?? throw new InvalidOperationException("GLB JSON is missing"))
        {
            var root = document.RootElement;
            Require(root.GetProperty("asset").GetProperty("version").GetString() == "2.0", "GLB asset version drifted");
            RejectExternalUris(root);
            foreach (var forbidden in new[] { "animations", "skins", "images", "textures" })
            {
                Require(!root.TryGetProperty(forbidden, out var value) || value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0, $"GLB contains {forbidden}");
            }
            var buffers = root.GetProperty("buffers");
            Require(buffers.GetArrayLength() == 1, "GLB must have one embedded buffer");
            var declaredBufferLength = buffers[0].GetProperty("byteLength").GetInt32();
            Require(binaryLength is not null && binaryLength >= declaredBufferLength && binaryLength <= declaredBufferLength + 3, "GLB binary length mismatch");
            var nodes = root.GetProperty("nodes").EnumerateArray().ToArray();
            Require(nodes.Length == expectedEntities.Count && nodes.Length <= 100_000, "GLB node/entity mapping drifted");
            var actualEntities = new HashSet<string>(StringComparer.Ordinal);
            var indegree = new int[nodes.Length];
            var children = new List<int>[nodes.Length];
            for (var index = 0; index < nodes.Length; index++)
            {
                var node = nodes[index];
                Require(node.TryGetProperty("mesh", out _), "occurrence node has no mesh");
                var entityId = node.GetProperty("extras").GetProperty("photonEntityId").GetString();
                Require(entityId is not null && actualEntities.Add(entityId), "GLB entity ID missing or duplicated");
                children[index] = new List<int>();
                if (!node.TryGetProperty("children", out var rawChildren))
                {
                    continue;
                }
                foreach (var child in rawChildren.EnumerateArray())
                {
                    var childIndex = child.GetInt32();
                    Require(childIndex >= 0 && childIndex < nodes.Length, "GLB child index out of bounds");
                    Require(++indegree[childIndex] <= 1, "GLB node has multiple parents");
                    children[index].Add(childIndex);
                }
            }
            Require(actualEntities.SetEquals(expectedEntities), "GLB entity IDs drifted");
            var queue = new Queue<(int Index, int Depth)>();
            for (var index = 0; index < indegree.Length; index++)
            {
                if (indegree[index] == 0)
                {
                    queue.Enqueue((index, 1));
                }
            }
            var visited = 0;
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                Require(current.Depth <= 256, "GLB node hierarchy too deep");
                visited++;
                foreach (var child in children[current.Index])
                {
                    queue.Enqueue((child, current.Depth + 1));
                }
            }
            Require(visited == nodes.Length, "GLB node cycle detected");
            var meshes = root.GetProperty("meshes");
            var accessors = root.GetProperty("accessors");
            var views = root.GetProperty("bufferViews");
            Require(meshes.GetArrayLength() <= 20_000 && accessors.GetArrayLength() <= 100_000 && views.GetArrayLength() <= 100_000, "GLB resource bound exceeded");
            long accessorElements = 0;
            foreach (var accessor in accessors.EnumerateArray())
            {
                var count = accessor.GetProperty("count").GetInt64();
                Require(count is >= 0 and <= 5_000_000, "GLB accessor count exceeded");
                accessorElements = checked(accessorElements + count);
            }
            Require(accessorElements <= 10_000_000, "GLB aggregate accessor bound exceeded");
            foreach (var view in views.EnumerateArray())
            {
                Require(view.GetProperty("buffer").GetInt32() == 0, "GLB buffer view references another buffer");
                var viewOffset = view.TryGetProperty("byteOffset", out var rawOffset) ? rawOffset.GetInt32() : 0;
                var viewLength = view.GetProperty("byteLength").GetInt32();
                Require(viewOffset >= 0 && viewLength >= 0 && (long)viewOffset + viewLength <= declaredBufferLength, "GLB buffer view out of bounds");
            }
        }
    }

    private static void RejectExternalUris(JsonElement root)
    {
        var pending = new Stack<(JsonElement Element, int Depth)>();
        pending.Push((root, 0));
        var inspected = 0;
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            Require(++inspected <= 250_000 && current.Depth <= 64, "GLB JSON too complex");
            if (current.Element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in current.Element.EnumerateObject())
                {
                    Require(!property.Name.Equals("uri", StringComparison.OrdinalIgnoreCase) || property.Value.ValueKind != JsonValueKind.String, "external GLB URI found");
                    pending.Push((property.Value, current.Depth + 1));
                }
            }
            else if (current.Element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in current.Element.EnumerateArray())
                {
                    pending.Push((item, current.Depth + 1));
                }
            }
        }
    }

    private static void RequireSuccessResponse(JsonElement response, string operation)
    {
        Require(response.GetProperty("ok").GetBoolean(), $"{operation} returned an error");
        Require(response.GetProperty("operation").GetString() == operation, $"{operation} response drifted");
    }

    private static void RequireErrorResponse(AdapterResult result, string expectedError)
    {
        Require(result.Process.ExitCode == 2, $"expected adapter failure {expectedError}, exit={result.Process.ExitCode}");
        Require(!result.Response.GetProperty("ok").GetBoolean(), $"expected adapter failure {expectedError}");
        Require(result.Response.GetProperty("error").GetString() == expectedError, $"expected {expectedError}, got {result.Response.GetProperty("error").GetString()}");
    }

    private static void RequireSuccess(ProcessResult result, string operation)
    {
        Require(
            result.ExitCode == 0,
            $"{operation} failed ({result.ExitCode}): stdout={Bounded(result.StandardOutput)} stderr={Bounded(result.StandardError)}");
    }

    private static string Bounded(string value) => value.Length <= 500 ? value : value[..500];

    private static string Sha256(byte[] bytes) => "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool IsDigest(string? value) => value is not null && Regex.IsMatch(value, "^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    private static void RequireApproximately(double actual, double expected, double relativeTolerance, string label)
    {
        Require(double.IsFinite(actual) && Math.Abs(actual - expected) <= Math.Max(1.0, Math.Abs(expected)) * relativeTolerance, $"{label} mismatch: {actual:R} != {expected:R}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void DeleteOwnedTemporaryTree(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }
        var full = Path.GetFullPath(path);
        var temp = Path.GetFullPath(Path.GetTempPath());
        Require(full.StartsWith(temp, StringComparison.OrdinalIgnoreCase), "refusing cleanup outside temp root");
        Require(Path.GetFileName(full).StartsWith("photon-cad-industrial-smoke-", StringComparison.Ordinal), "refusing cleanup of unexpected temp directory");
        foreach (var entry in Directory.EnumerateFileSystemEntries(full, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(entry);
            Require((attributes & FileAttributes.ReparsePoint) == 0, "refusing cleanup through a reparse point");
        }
        Directory.Delete(full, recursive: true);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle fileHandle,
        out ByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    private sealed record AdapterResult(ProcessResult Process, byte[] StandardOutputBytes, JsonElement Response);
    private sealed record CatalogItem(string Id, string Title, JsonElement Parameters);
    private sealed record ScenarioReceipt(string Name, long DurationMilliseconds, bool Passed);
    private readonly record struct HostFileIdentity(
        uint VolumeSerialNumber,
        ulong FileIndex,
        uint LinkCount,
        ulong FileSize,
        ulong LastWriteTime);

    private readonly record struct ImageIdentity(
        string Id,
        IReadOnlyList<string> RootFsLayers,
        string ConfigurationDigest,
        string LabelDigest)
    {
        public static ImageIdentity From(JsonElement root)
        {
            var id = root.GetProperty("Id").GetString()
                ?? throw new InvalidOperationException("image inspect omitted ID");
            var layers = root.GetProperty("RootFS").GetProperty("Layers").EnumerateArray()
                .Select(value => value.GetString() ?? throw new InvalidOperationException("image inspect emitted a null RootFS layer"))
                .ToArray();
            var config = root.GetProperty("Config");
            var configurationDigest = Sha256(Encoding.UTF8.GetBytes(config.GetRawText()));
            var labels = config.TryGetProperty("Labels", out var rawLabels) && rawLabels.ValueKind == JsonValueKind.Object
                ? rawLabels.EnumerateObject()
                    .OrderBy(property => property.Name, StringComparer.Ordinal)
                    .Select(property => $"{property.Name}={property.Value.GetString()}\n")
                : Array.Empty<string>();
            var labelDigest = Sha256(Encoding.UTF8.GetBytes(string.Concat(labels)));
            return new ImageIdentity(id, layers, configurationDigest, labelDigest);
        }
    }
}
