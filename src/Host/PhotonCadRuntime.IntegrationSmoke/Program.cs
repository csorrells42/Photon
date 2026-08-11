using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using PhotonCadRuntime;

namespace PhotonCadRuntimeIntegrationSmoke;

internal static class Program
{
    private static int _passed;

    private static async Task<int> Main(string[] args)
    {
        var started = DateTimeOffset.UtcNow;
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("evidence-pass", EvidencePassAsync),
            ("receipt-digest-fail-closed", ReceiptDigestRejectedAsync),
            ("archive-tamper-fail-closed", ArchiveTamperRejectedAsync),
            ("policy-tamper-fail-closed", PolicyTamperRejectedAsync),
            ("base-image-role-swap-fail-closed", BaseImageRejectedAsync),
            ("runtime-adaptation-tamper-fail-closed", RuntimeAdaptationRejectedAsync),
            ("image-label-fail-closed", ImageLabelRejectedAsync),
            ("image-environment-fail-closed", ImageEnvironmentRejectedAsync),
            ("factory-never-advertises-invalid-evidence", FactoryRejectsInvalidEvidenceAsync),
            ("factory-rejects-malformed-inspection", FactoryRejectsMalformedInspectionAsync),
            ("launch-plan-exact-containment", LaunchPlanAsync),
            ("ndjson-round-trip", NdjsonRoundTripAsync),
            ("content-length-round-trip", ContentLengthRoundTripAsync),
            ("foreign-frame-poisons-session", ForeignFrameRejectedAsync),
            ("expected-remote-error-keeps-sequence", RemoteErrorSequenceAsync),
            ("partcad-policy-surface-is-exact", PartCadPolicySurfaceAsync),
            ("catalog-is-honestly-bounded", CatalogAsync),
            ("evidence-paths-reject-alternate-streams", EvidencePathsRejectAlternateStreamsAsync),
            ("windows-path-inspector-detects-hardlinks", WindowsPathInspectorAsync),
        };

        foreach (var test in tests)
        {
            await test.Run().ConfigureAwait(false);
            _passed++;
            Console.WriteLine($"PASS {test.Name}");
        }

        if (args.Contains("--real", StringComparer.Ordinal))
        {
            await RealRuntimeAsync().ConfigureAwait(false);
            _passed++;
            Console.WriteLine("PASS real-runtime");
        }

        Console.WriteLine($"Photon CAD integration smoke passed {_passed} checks in {(DateTimeOffset.UtcNow - started).TotalSeconds:F3}s.");
        return 0;
    }

    private static async Task EvidencePassAsync()
    {
        using var fixture = EvidenceFixture.Create();
        var evidence = await CadDockerRuntimeEvidenceVerifier.VerifyAsync(fixture.Settings, fixture.Inspector);
        Equal(fixture.Settings.ReceiptSha256, evidence.ReceiptSha256, "receipt digest");
        Equal(CadDockerRuntimeIdentity.GeometryTag, evidence.Geometry.Tag, "geometry tag");
        Equal(CadDockerRuntimeIdentity.AssemblyTag, evidence.Assembly.Tag, "assembly tag");
    }

    private static async Task ReceiptDigestRejectedAsync()
    {
        using var fixture = EvidenceFixture.Create(receiptDigestOverride: new string('a', 64));
        await ThrowsCodeAsync<CadContractException>(
            () => CadDockerRuntimeEvidenceVerifier.VerifyAsync(fixture.Settings, fixture.Inspector).AsTask(),
            "receipt_hash_mismatch");
    }

    private static async Task ArchiveTamperRejectedAsync()
    {
        using var fixture = EvidenceFixture.Create();
        await File.AppendAllTextAsync(fixture.GeometryArchivePath, "tamper", Encoding.UTF8);
        await ThrowsCodeAsync<CadContractException>(
            () => CadDockerRuntimeEvidenceVerifier.VerifyAsync(fixture.Settings, fixture.Inspector).AsTask(),
            "file_length_mismatch");
    }

    private static async Task ImageLabelRejectedAsync()
    {
        using var fixture = EvidenceFixture.Create(badGeometryLabel: true);
        await ThrowsCodeAsync<CadContractException>(
            () => CadDockerRuntimeEvidenceVerifier.VerifyAsync(fixture.Settings, fixture.Inspector).AsTask(),
            "image_label_mismatch");
    }

    private static async Task PolicyTamperRejectedAsync()
    {
        using var fixture = EvidenceFixture.Create();
        await File.AppendAllTextAsync(fixture.Settings.PolicyPath, " ", Encoding.UTF8);
        await ThrowsCodeAsync<CadContractException>(
            () => CadDockerRuntimeEvidenceVerifier.VerifyAsync(fixture.Settings, fixture.Inspector).AsTask(),
            "policy_hash_mismatch");
    }

    private static async Task ImageEnvironmentRejectedAsync()
    {
        using var fixture = EvidenceFixture.Create(badGeometryEnvironment: true);
        await ThrowsCodeAsync<CadContractException>(
            () => CadDockerRuntimeEvidenceVerifier.VerifyAsync(fixture.Settings, fixture.Inspector).AsTask(),
            "image_environment_mismatch");
    }

    private static async Task BaseImageRejectedAsync()
    {
        using var fixture = EvidenceFixture.Create(badAssemblyBaseImage: true);
        await ThrowsCodeAsync<CadContractException>(
            () => CadDockerRuntimeEvidenceVerifier.VerifyAsync(fixture.Settings, fixture.Inspector).AsTask(),
            "bundle_base_image_mismatch");
    }

    private static async Task RuntimeAdaptationRejectedAsync()
    {
        using var fixture = EvidenceFixture.Create(badAdaptationHash: true);
        await ThrowsCodeAsync<CadContractException>(
            () => CadDockerRuntimeEvidenceVerifier.VerifyAsync(fixture.Settings, fixture.Inspector).AsTask(),
            "evidence_value_mismatch");
    }

    private static async Task FactoryRejectsInvalidEvidenceAsync()
    {
        using var fixture = EvidenceFixture.Create(receiptDigestOverride: new string('b', 64));
        var broker = await CadDockerRuntimeBrokerFactory.ProvisionAsync(
            fixture.Settings,
            fixture.Inspector,
            new CadWindowsPathInspector());
        Equal(CadRuntimeAvailability.Rejected, broker.Describe().Availability, "invalid evidence availability");
        True(broker.Describe().Catalog is null, "invalid evidence catalog hidden");
        var unavailable = await CadDockerRuntimeBrokerFactory.ProvisionAsync(null);
        Equal(CadRuntimeAvailability.NotProvisioned, unavailable.Describe().Availability, "missing settings availability");
    }

    private static async Task FactoryRejectsMalformedInspectionAsync()
    {
        using var fixture = EvidenceFixture.Create();
        var broker = await CadDockerRuntimeBrokerFactory.ProvisionAsync(
            fixture.Settings,
            new MalformedImageInspector(),
            new CadWindowsPathInspector());
        Equal(CadRuntimeAvailability.Rejected, broker.Describe().Availability, "malformed inspection availability");
        True(broker.Describe().Catalog is null, "malformed inspection catalog hidden");
    }

    private static async Task LaunchPlanAsync()
    {
        using var fixture = EvidenceFixture.Create();
        var evidence = await CadDockerRuntimeEvidenceVerifier.VerifyAsync(fixture.Settings, fixture.Inspector);
        var plan = CadDockerLaunchPlan.Create(evidence, CadDockerRuntimeRole.Geometry, CadSessionHandle.New(), CadProjectHandle.New());
        Equal(1, Count(plan.Arguments, "--mount"), "mount count");
        Equal(2, Count(plan.Arguments, "--env"), "environment count");
        ContainsPair(plan.Arguments, "--network", "none");
        ContainsPair(plan.Arguments, "--user", "65532:65532");
        ContainsPair(plan.Arguments, "--cap-drop", "ALL");
        ContainsPair(plan.Arguments, "--security-opt", "no-new-privileges:true");
        True(!plan.Arguments.Contains("--privileged"), "privileged mode absent");
        True(plan.Arguments.Contains(evidence.Geometry.ImageId), "launch by pinned image id");
    }

    private static async Task NdjsonRoundTripAsync()
    {
        var sink = new MemoryStream();
        var source = new MemoryStream(Encoding.UTF8.GetBytes("{\"ok\":true}\n"));
        await using var transport = new CadNdjsonTransport(sink, source);
        var received = await transport.ReceiveAsync(CancellationToken.None);
        Equal("{\"ok\":true}", Encoding.UTF8.GetString(received), "NDJSON receive");
        await transport.SendAsync(Encoding.UTF8.GetBytes("{\"sent\":true}"), CancellationToken.None);
        Equal("{\"sent\":true}\n", Encoding.UTF8.GetString(sink.ToArray()), "NDJSON send");
    }

    private static async Task ContentLengthRoundTripAsync()
    {
        var payload = Encoding.UTF8.GetBytes("{\"ok\":true}");
        var framed = Frame(payload);
        var sink = new MemoryStream();
        await using var transport = new CadContentLengthTransport(sink, new MemoryStream(framed));
        var received = await transport.ReceiveAsync(CancellationToken.None);
        True(received.SequenceEqual(payload), "Content-Length receive");
        await transport.SendAsync(payload, CancellationToken.None);
        True(sink.ToArray().SequenceEqual(framed), "Content-Length send");
    }

    private static async Task ForeignFrameRejectedAsync()
    {
        var source = new MemoryStream(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{}}\n"));
        await using var client = new CadJsonRpcClient(
            new CadNdjsonTransport(new MemoryStream(), source),
            TimeSpan.FromSeconds(2));
        await ThrowsCodeAsync<CadProtocolException>(
            () => client.RequestAsync("version", new { }).AsTask(),
            "foreign_or_stale_protocol_frame");
        True(client.IsTerminal, "foreign response poisons protocol session");
    }

    private static async Task RemoteErrorSequenceAsync()
    {
        var error = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"code\":-32601,\"message\":\"Method not found\"}}");
        var success = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"partcad\":\"0.7.158\"}}");
        var sourceBytes = Frame(error).Concat(Frame(success)).ToArray();
        await using var client = new CadJsonRpcClient(
            new CadContentLengthTransport(new MemoryStream(), new MemoryStream(sourceBytes)),
            TimeSpan.FromSeconds(2));
        try
        {
            _ = await client.RequestAsync("install", new { });
            throw new InvalidOperationException("Expected method-not-found response.");
        }
        catch (CadRemoteProtocolException exception)
        {
            Equal(-32601L, exception.RemoteCode, "remote error code");
        }
        True(!client.IsTerminal, "valid remote error preserves sequencing");
        var result = await client.RequestAsync("version", new { });
        Equal("0.7.158", result.GetProperty("partcad").GetString(), "second response id");
    }

    private static Task CatalogAsync()
    {
        var catalog = CadPinnedCapabilityCatalog.Create(DateTimeOffset.UtcNow);
        Equal(5, catalog.Capabilities.Count, "available capability count");
        Equal(6, catalog.Coverage.Discovered, "discovered capability count");
        Equal(1, catalog.Coverage.Unavailable, "unmapped capability count");
        True(catalog.Capabilities.All(capability => capability.Source.Digest == CadDockerRuntimeIdentity.GeometrySourceArchiveSha256),
            "capabilities bind pinned source digest");
        True(catalog.Capabilities.All(capability => !capability.PreviewSupported), "no unimplemented preview claim");
        return Task.CompletedTask;
    }

    private static Task PartCadPolicySurfaceAsync()
    {
        string[] expectedAllowed =
        [
            "context.create", "export.assembly", "export.part", "healthcheck", "info.object",
            "inspect.assembly", "inspect.object", "inspect.part", "list.objects", "list.packages", "version",
        ];
        string[] expectedForbidden =
        [
            "activate", "add.assembly", "add.object", "add.part", "adhoc.convert", "convert.object",
            "daemon.reset", "daemon.set.telemetry", "daemon.status", "ensure_loaded", "import.object", "init",
            "info", "inspect.file", "inspect.interface", "inspect.sketch", "install", "lint.run", "list.all",
            "list.mates", "list.providers", "package.load", "package.path", "package.refresh", "render.objects",
            "rpc.discover", "search.objects", "test", "test.run", "update",
        ];
        True(CadPartCadProtocolClient.AllowedMethods.SetEquals(expectedAllowed), "exact PartCAD read/export allowlist");
        True(CadPartCadProtocolClient.ForbiddenProbeMethods.SetEquals(expectedForbidden), "exact PartCAD negative probe set");
        True(!CadPartCadProtocolClient.AllowedMethods.Overlaps(CadPartCadProtocolClient.ForbiddenProbeMethods),
            "PartCAD allowlist and negative probes are disjoint");
        return Task.CompletedTask;
    }

    private static async Task WindowsPathInspectorAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = NewTemporaryDirectory();
        try
        {
            var original = Path.Combine(root, "original.step");
            var linked = Path.Combine(root, "linked.step");
            await File.WriteAllTextAsync(original, "ISO-10303-21;");
            if (!CreateHardLink(linked, original, IntPtr.Zero))
                throw new InvalidOperationException($"Hard-link fixture creation failed: {Marshal.GetLastWin32Error()}.");
            var inspector = new CadWindowsPathInspector();
            True(inspector.Inspect(original).HasMultipleHardLinks, "hard-link count");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task EvidencePathsRejectAlternateStreamsAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = NewTemporaryDirectory();
        try
        {
            var file = Path.Combine(root, "receipt.json");
            var stream = $"{file}:untrusted";
            await File.WriteAllTextAsync(file, "base");
            await File.WriteAllTextAsync(stream, "stream");
            await ThrowsCodeAsync<CadContractException>(
                () => Task.Run(() => { _ = CadDockerPathSecurity.RequireFile(stream, "path"); }),
                "alternate_data_stream_rejected");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task RealRuntimeAsync()
    {
        var docker = RequiredEnvironment("PHOTON_CAD_DOCKER_PATH");
        var dockerSha = RequiredEnvironment("PHOTON_CAD_DOCKER_SHA256");
        var policy = RequiredEnvironment("PHOTON_CAD_POLICY_PATH");
        var receipt = RequiredEnvironment("PHOTON_CAD_RECEIPT_PATH");
        var receiptSha = RequiredEnvironment("PHOTON_CAD_RECEIPT_SHA256");
        var expected = RequiredEnvironment("PHOTON_CAD_EXPECT");
        var workspace = NewTemporaryDirectory();
        ICadRuntimeBroker? broker = null;
        try
        {
            var settings = new CadDockerRuntimeSettings(
                docker,
                dockerSha,
                workspace,
                policy,
                receipt,
                receiptSha,
                commandTimeout: TimeSpan.FromSeconds(60),
                requestTimeout: TimeSpan.FromMinutes(3));
            var cliInspector = new CadDockerCliImageInspector();
            if (expected == "ready")
            {
                var inspectedGeometry = await cliInspector.InspectAsync(docker, CadDockerRuntimeIdentity.GeometryTag, TimeSpan.FromSeconds(60), CancellationToken.None);
                var inspectedAssembly = await cliInspector.InspectAsync(docker, CadDockerRuntimeIdentity.AssemblyTag, TimeSpan.FromSeconds(60), CancellationToken.None);
                using var receiptDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(receipt));
                var receiptIds = receiptDocument.RootElement.GetProperty("bundles").EnumerateArray()
                    .ToDictionary(item => item.GetProperty("role").GetString()!, item => item.GetProperty("imageId").GetString()!, StringComparer.Ordinal);
                Equal(receiptIds["geometry"], inspectedGeometry.ImageId, "real geometry image id");
                Equal(receiptIds["assembly"], inspectedAssembly.ImageId, "real assembly image id");
                var evidence = await CadDockerRuntimeEvidenceVerifier.VerifyAsync(settings, cliInspector);
                Equal(receiptSha, evidence.ReceiptSha256, "real immutable evidence");
            }
            broker = await CadDockerRuntimeBrokerFactory.ProvisionAsync(settings);
            if (expected == "rejected")
            {
                Equal(CadRuntimeAvailability.Rejected, broker.Describe().Availability, "broken bundle rejected");
                return;
            }
            if (expected != "ready") throw new InvalidOperationException("PHOTON_CAD_EXPECT must be ready or rejected.");
            Equal(CadRuntimeAvailability.Ready, broker.Describe().Availability, "verified runtime ready");
            var opened = await broker.OpenProjectAsync(new CadOpenProjectRequest(
                CadRequestId.New(), CadWorkspaceHandle.New(), "Integration Box and Cylinder"));
            var project = Require(opened).Project;
            var started = await broker.StartSessionAsync(new CadStartSessionRequest(
                CadRequestId.New(), project, new CadRevision(0)));
            var session = Require(started).Session;

            var rawCodeAttempt = await broker.ExecuteAsync(new CadOperationRequest(
                CadRequestId.New(), session, project, new CadRevision(0), CadOperationMode.Scratch,
                CadPinnedCapabilityCatalog.BoxCapabilityId,
                [new CadOperationInput("code", new CadTextInputValue("__import__('os').system('whoami')"))]));
            True(rawCodeAttempt.Value is null, "renderer code input has no operation result");
            Equal("invalid_cad_request", rawCodeAttempt.Error?.Code, "renderer code input rejected before Docker tool call");

            var box = await ExecuteAsync(broker, session, project, 0, CadPinnedCapabilityCatalog.BoxCapabilityId,
                [Number("length", 10), Number("width", 20), Number("height", 30)]);
            Equal(1L, box.ResultingRevision.Value, "box revision");
            var boxId = box.Snapshot!.Entities.Single().Id;

            var stale = Require(await broker.ExecuteAsync(new CadOperationRequest(
                CadRequestId.New(), session, project, new CadRevision(0), CadOperationMode.Scratch,
                CadPinnedCapabilityCatalog.CylinderCapabilityId,
                [Number("radius", 5), Number("height", 12)])));
            Equal(CadOperationStatus.Rejected, stale.Status, "stale operation rejected");
            True(stale.Stale, "stale operation identified");

            var cylinder = await ExecuteAsync(broker, session, project, 1, CadPinnedCapabilityCatalog.CylinderCapabilityId,
                [Number("radius", 5), Number("height", 12)]);
            Equal(2L, cylinder.ResultingRevision.Value, "cylinder revision");

            var measured = await ExecuteAsync(broker, session, project, 2, CadPinnedCapabilityCatalog.MeasureCapabilityId,
                [], [boxId]);
            True(measured.Issues.Any(issue => issue.Code == "measurement_summary"), "measurement evidence");

            _ = await ExecuteAsync(broker, session, project, 3, CadPinnedCapabilityCatalog.ValidateCapabilityId,
                [], [boxId]);
            var exported = await ExecuteAsync(broker, session, project, 4, CadPinnedCapabilityCatalog.ExportStepCapabilityId,
                [], [boxId]);
            Equal(1, exported.Artifacts.Count, "STEP artifact handle");
            True(!exported.Artifacts[0].Value.Contains('/') && !exported.Artifacts[0].Value.Contains('\\'),
                "STEP result contains only an opaque artifact handle");
            var stepPath = Directory.GetFiles(workspace, "photon-art_*.step", SearchOption.TopDirectoryOnly).Single();
            var stepFile = new FileInfo(stepPath);
            True(stepFile.Length is > 100 and < 2_097_152, "bounded box STEP file size");
            var stepText = await File.ReadAllTextAsync(stepPath);
            True(stepText.StartsWith("ISO-10303-21;", StringComparison.Ordinal), "STEP exchange header");
            True(stepText.Contains("END-ISO-10303-21;", StringComparison.Ordinal), "STEP exchange trailer");

            var artifactRequest = new CadArtifactReadRequest(
                CadRequestId.New(),
                session,
                exported.Artifacts[0],
                2_097_152);
            var artifactReceipt = Require(await broker.GetArtifactReceiptAsync(artifactRequest));
            Equal(exported.Artifacts[0], artifactReceipt.Artifact, "artifact receipt handle binding");
            Equal(stepFile.Length, artifactReceipt.ByteLength, "artifact receipt length");
            var expectedArtifactDigest = Convert.ToHexString(
                SHA256.HashData(await File.ReadAllBytesAsync(stepPath))).ToLowerInvariant();
            Equal(expectedArtifactDigest, artifactReceipt.ContentDigest, "artifact receipt digest");
            await using (var lease = Require(await broker.OpenArtifactReadAsync(artifactRequest)))
            {
                Equal(artifactReceipt.ContentDigest, lease.Descriptor.ContentDigest, "lease receipt binding");
                var leasedDigest = Convert.ToHexString(await SHA256.HashDataAsync(lease)).ToLowerInvariant();
                Equal(expectedArtifactDigest, leasedDigest, "lease bytes digest");
            }
            var hardLinkPath = stepPath + ".hardlink";
            if (!CreateHardLink(hardLinkPath, stepPath, IntPtr.Zero))
                throw new InvalidOperationException($"Artifact hard-link fixture creation failed: {Marshal.GetLastWin32Error()}.");
            try
            {
                var hardLinkedArtifact = await broker.OpenArtifactReadAsync(artifactRequest);
                Equal("invalid_cad_request", hardLinkedArtifact.Error?.Code, "artifact hard link rejected");
            }
            finally
            {
                File.Delete(hardLinkPath);
            }
            await using (var restoredLease = Require(await broker.OpenArtifactReadAsync(artifactRequest)))
                Equal(artifactReceipt.ByteLength, restoredLease.Length, "legitimate artifact remains readable after hard-link removal");
            var boundedOut = await broker.GetArtifactReceiptAsync(new CadArtifactReadRequest(
                CadRequestId.New(), session, exported.Artifacts[0], 100));
            Equal("artifact_not_found", boundedOut.Error?.Code, "artifact bound enforced");

            var replacementPath = stepPath + ".replacement";
            await File.WriteAllBytesAsync(replacementPath, await File.ReadAllBytesAsync(stepPath));
            File.Move(replacementPath, stepPath, overwrite: true);
            var swappedArtifact = await broker.OpenArtifactReadAsync(artifactRequest);
            Equal("invalid_cad_request", swappedArtifact.Error?.Code, "same-byte file identity swap rejected");

            var verified = await broker.VerifyAsync(new CadVerificationRequest(
                CadRequestId.New(), session, project, new CadRevision(5),
                [CadVerificationCheck.ValidSolids, CadVerificationCheck.ExportReadiness]));
            Equal(CadVerificationStatus.Passed, Require(verified).Status, "solid/export verification");
            var closed = await broker.CloseSessionAsync(new CadCloseSessionRequest(CadRequestId.New(), session));
            Equal(CadSessionState.Closed, Require(closed).State, "closed session");
        }
        finally
        {
            if (broker is IAsyncDisposable disposable) await disposable.DisposeAsync();
            Directory.Delete(workspace, recursive: true);
        }
    }

    private static async Task<CadOperationResult> ExecuteAsync(
        ICadRuntimeBroker broker,
        CadSessionHandle session,
        CadProjectHandle project,
        long revision,
        string capability,
        IEnumerable<CadOperationInput> inputs,
        IEnumerable<string>? targets = null)
    {
        var result = await broker.ExecuteAsync(new CadOperationRequest(
            CadRequestId.New(), session, project, new CadRevision(revision), CadOperationMode.Scratch,
            capability, inputs, targets));
        var operation = Require(result);
        Equal(CadOperationStatus.Accepted, operation.Status, $"{capability} accepted");
        return operation;
    }

    private static CadOperationInput Number(string id, double value) => new(id, new CadNumberInputValue(value));

    private static T Require<T>(CadResult<T> result) where T : class =>
        result.Value ?? throw new InvalidOperationException($"CAD call failed: {result.Error?.Code}");

    private static byte[] Frame(byte[] payload) => Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n")
        .Concat(payload).ToArray();

    private static int Count(IReadOnlyList<string> values, string value) => values.Count(item => item == value);

    private static void ContainsPair(IReadOnlyList<string> values, string first, string second)
    {
        for (var index = 0; index < values.Count - 1; index++)
            if (values[index] == first && values[index + 1] == second) return;
        throw new InvalidOperationException($"Missing required argument pair: {first} {second}");
    }

    private static async Task ThrowsCodeAsync<TException>(Func<Task> action, string expectedCode)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException exception)
        {
            var code = exception switch
            {
                CadContractException contract => contract.Code,
                CadProtocolException protocol => protocol.Code,
                _ => null,
            };
            Equal(expectedCode, code, "exception code");
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name} with code {expectedCode}.");
    }

    private static void Equal<T>(T expected, T actual, string field)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{field}: expected {expected}, got {actual}.");
    }

    private static void True(bool condition, string field)
    {
        if (!condition) throw new InvalidOperationException($"Assertion failed: {field}.");
    }

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Required environment variable is missing: {name}");

    private static string NewTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"photon-cad-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}

internal sealed class EvidenceFixture : IDisposable
{
    private EvidenceFixture(
        string root,
        CadDockerRuntimeSettings settings,
        FakeImageInspector inspector,
        string geometryArchivePath)
    {
        Root = root;
        Settings = settings;
        Inspector = inspector;
        GeometryArchivePath = geometryArchivePath;
    }

    internal string Root { get; }
    internal CadDockerRuntimeSettings Settings { get; }
    internal FakeImageInspector Inspector { get; }
    internal string GeometryArchivePath { get; }

    internal static EvidenceFixture Create(
        string? receiptDigestOverride = null,
        bool badGeometryLabel = false,
        bool badGeometryEnvironment = false,
        bool badAssemblyBaseImage = false,
        bool badAdaptationHash = false)
    {
        var root = Path.Combine(Path.GetTempPath(), $"photon-cad-evidence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        var docker = Path.Combine(root, "docker.exe");
        var policy = Path.Combine(root, "runtime-policy.json");
        var receipt = Path.Combine(root, "bundle-receipt.json");
        var geometryArchive = Path.Combine(root, "photon-cad-geometry.tar");
        var assemblyArchive = Path.Combine(root, "photon-cad-assembly.tar");
        File.WriteAllBytes(docker, Encoding.UTF8.GetBytes("synthetic pinned docker executable"));
        File.WriteAllBytes(geometryArchive, Encoding.UTF8.GetBytes("synthetic geometry archive"));
        File.WriteAllBytes(assemblyArchive, Encoding.UTF8.GetBytes("synthetic assembly archive"));

        var policyBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = CadDockerRuntimeIdentity.PolicySchema,
            platform = CadDockerRuntimeIdentity.Platform,
            workspaceMount = "/workspace",
            runtimeUser = "65532:65532",
            network = "none",
            readOnlyRoot = true,
            dropCapabilities = new[] { "ALL" },
            securityOptions = new[] { "no-new-privileges:true" },
            pidsLimit = 64,
            memoryBytes = 2_147_483_648L,
            nanoCpus = 2_000_000_000L,
            stopTimeoutSeconds = 10,
            tmpfs = new[]
            {
                "/session:rw,nosuid,nodev,noexec,size=268435456,mode=0700,uid=65532,gid=65532",
                "/tmp:rw,nosuid,nodev,noexec,size=268435456,mode=0700,uid=65532,gid=65532",
            },
            forbiddenMounts = new[] { "/var/run/docker.sock", "/run/docker.sock", "/root", "/home", "/mnt/c/Users" },
            environmentAllowlist = new[] { "PHOTON_CAD_SESSION_ID", "PHOTON_CAD_PROJECT_ID" },
            geometry = new { tag = CadDockerRuntimeIdentity.GeometryTag, entrypoint = CadDockerRuntimeIdentity.GeometryEntrypoint },
            assembly = new { tag = CadDockerRuntimeIdentity.AssemblyTag, entrypoint = CadDockerRuntimeIdentity.AssemblyEntrypoint },
        });
        File.WriteAllBytes(policy, policyBytes);
        var policySha = Hash(policyBytes);
        const string geometryImageId = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
        const string assemblyImageId = "sha256:2222222222222222222222222222222222222222222222222222222222222222";
        var receiptBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = CadDockerRuntimeIdentity.ReceiptSchema,
            createdAtUtc = "2026-08-10T20:06:02.2065683Z",
            policySha256 = policySha,
            geometrySource = new
            {
                repository = CadDockerRuntimeIdentity.GeometryRepository,
                revision = CadDockerRuntimeIdentity.GeometryRevision,
                archiveSha256 = CadDockerRuntimeIdentity.GeometrySourceArchiveSha256,
            },
            assemblySource = new
            {
                repository = CadDockerRuntimeIdentity.AssemblyRepository,
                revision = CadDockerRuntimeIdentity.AssemblyRevision,
                archiveSha256 = CadDockerRuntimeIdentity.AssemblySourceArchiveSha256,
                runtimeAdaptation = new
                {
                    kind = CadDockerRuntimeIdentity.AssemblyAdaptationKind,
                    patch = CadDockerRuntimeIdentity.AssemblyAdaptationPatch,
                    patchSha256 = badAdaptationHash ? new string('c', 64) : CadDockerRuntimeIdentity.AssemblyAdaptationPatchSha256,
                    target = CadDockerRuntimeIdentity.AssemblyAdaptationTarget,
                    patchedTargetSha256 = CadDockerRuntimeIdentity.AssemblyAdaptationTargetSha256,
                },
            },
            bundles = new object[]
            {
                new
                {
                    role = "geometry", tag = CadDockerRuntimeIdentity.GeometryTag, imageId = geometryImageId,
                    platform = CadDockerRuntimeIdentity.Platform, baseImage = CadDockerRuntimeIdentity.GeometryBaseImage,
                    archive = Path.GetFileName(geometryArchive),
                    archiveSha256 = Hash(File.ReadAllBytes(geometryArchive)), archiveByteLength = new FileInfo(geometryArchive).Length,
                },
                new
                {
                    role = "assembly", tag = CadDockerRuntimeIdentity.AssemblyTag, imageId = assemblyImageId,
                    platform = CadDockerRuntimeIdentity.Platform,
                    baseImage = badAssemblyBaseImage ? CadDockerRuntimeIdentity.GeometryBaseImage : CadDockerRuntimeIdentity.AssemblyBaseImage,
                    archive = Path.GetFileName(assemblyArchive),
                    archiveSha256 = Hash(File.ReadAllBytes(assemblyArchive)), archiveByteLength = new FileInfo(assemblyArchive).Length,
                },
            },
        });
        File.WriteAllBytes(receipt, receiptBytes);
        var settings = new CadDockerRuntimeSettings(
            docker,
            Hash(File.ReadAllBytes(docker)),
            workspace,
            policy,
            receipt,
            receiptDigestOverride ?? Hash(receiptBytes));
        return new EvidenceFixture(
            root,
            settings,
            new FakeImageInspector(geometryImageId, assemblyImageId, badGeometryLabel, badGeometryEnvironment),
            geometryArchive);
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

internal sealed class FakeImageInspector : ICadDockerImageInspector
{
    private readonly string _geometryImageId;
    private readonly string _assemblyImageId;
    private readonly bool _badGeometryLabel;
    private readonly bool _badGeometryEnvironment;

    internal FakeImageInspector(
        string geometryImageId,
        string assemblyImageId,
        bool badGeometryLabel,
        bool badGeometryEnvironment)
    {
        _geometryImageId = geometryImageId;
        _assemblyImageId = assemblyImageId;
        _badGeometryLabel = badGeometryLabel;
        _badGeometryEnvironment = badGeometryEnvironment;
    }

    public ValueTask<CadDockerImageInspection> InspectAsync(
        string verifiedDockerExecutablePath,
        string exactTag,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var geometry = exactTag == CadDockerRuntimeIdentity.GeometryTag;
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["io.photon.cad.role"] = geometry && _badGeometryLabel ? "assembly" : geometry ? "geometry" : "assembly",
            ["io.photon.cad.source-revision"] = geometry ? CadDockerRuntimeIdentity.GeometryRevision : CadDockerRuntimeIdentity.AssemblyRevision,
            ["org.opencontainers.image.source"] = geometry ? CadDockerRuntimeIdentity.GeometryRepository : CadDockerRuntimeIdentity.AssemblyRepository,
            ["org.opencontainers.image.licenses"] = "Apache-2.0",
            ["org.opencontainers.image.version"] = geometry
                ? CadDockerRuntimeIdentity.GeometryBundleVersion
                : CadDockerRuntimeIdentity.AssemblyBundleVersion,
            ["org.opencontainers.image.base.name"] = geometry
                ? CadDockerRuntimeIdentity.GeometryBaseName
                : CadDockerRuntimeIdentity.AssemblyBaseName,
            ["org.opencontainers.image.base.digest"] = geometry
                ? CadDockerRuntimeIdentity.GeometryBaseDigest
                : CadDockerRuntimeIdentity.AssemblyBaseDigest,
        };
        var environment = (geometry ? CadDockerRuntimeIdentity.GeometryEnvironment : CadDockerRuntimeIdentity.AssemblyEnvironment).ToArray();
        if (geometry && _badGeometryEnvironment) environment[0] = "HTTP_PROXY=http://untrusted.invalid";
        return ValueTask.FromResult(new CadDockerImageInspection(
            geometry ? _geometryImageId : _assemblyImageId,
            [exactTag],
            "linux",
            "amd64",
            "65532:65532",
            "/workspace",
            geometry ? CadDockerRuntimeIdentity.GeometryEntrypoint : CadDockerRuntimeIdentity.AssemblyEntrypoint,
            environment,
            [],
            labels));
    }
}

internal sealed class MalformedImageInspector : ICadDockerImageInspector
{
    public ValueTask<CadDockerImageInspection> InspectAsync(
        string verifiedDockerExecutablePath,
        string exactTag,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        throw new KeyNotFoundException("Synthetic malformed Docker inspection frame.");
}
