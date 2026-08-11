using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PhotonCadProjects;
using PhotonCadProjects.Codec;
using PhotonCadProjects.RuntimeSync;
using PhotonCadRuntime.IndustrialProvider;

var smoke = new Smoke();
return await smoke.RunAsync();

internal sealed class Smoke
{
    private int _passed;
    private int _failed;

    internal async Task<int> RunAsync()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("exact evidence receipt and redistribution block", EvidenceAsync),
            ("bound box emits copied STEP and GLB", BoundBoxAsync),
            ("first and second mutations round-trip preview CAS", FirstAndSecondMutationAsync),
            ("bound cylinder preserves normalized scalars", BoundCylinderAsync),
            ("equal foreign request identity rejected", ForeignRequestAsync),
            ("provider is one shot", OneShotAsync),
            ("compensation is bound and idempotent", CompensationAsync),
            ("protocol rejects unknown members", UnknownMemberAsync),
            ("protocol rejects duplicate members", DuplicateMemberAsync),
            ("artifact digest mismatch rejected", DigestMismatchAsync),
            ("GLB external URI rejected", ExternalUriAsync),
            ("catalog cache copies and loads once", CatalogCacheAsync),
            ("public mutation seam leaks no path or process type", PublicSurfaceAsync),
        };
        foreach (var test in tests)
        {
            try
            {
                await test.Run();
                _passed++;
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception exception)
            {
                _failed++;
                Console.Error.WriteLine($"FAIL {test.Name}: {exception.GetType().Name}: {exception.Message}");
            }
        }
        Console.WriteLine($"PhotonCadRuntime.IndustrialProvider.Smoke: {_passed}/{_passed + _failed} passed");
        return _failed == 0 ? 0 : 1;
    }

    private static async Task EvidenceAsync()
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "PhotonCadIndustrial.ContainerSmoke", "artifacts", "last-smoke-receipt.json"));
        var evidence = await EvidenceVerifier.VerifyAsync(path, CancellationToken.None);
        Equal(EvidenceVerifier.AcceptedReceiptSha256, evidence.ReceiptSha256, "receipt digest");
        Equal(EvidenceVerifier.AcceptedDerivedImageId, evidence.DerivedImageId, "derived image");
        Equal("redistribution-blocked", evidence.ProviderEvidence.Source.License, "redistribution status");
    }

    private static async Task BoundBoxAsync()
    {
        await using var runner = new FakeRunner();
        var (bound, providerRequest) = await BoxAsync(runner);
        var mutation = await bound.Provider.ApplyAsync(providerRequest);
        Equal(2L, mutation.ResultingRevision, "result revision");
        Equal(2, mutation.Operations.Count, "operation count");
        Equal("industrial.preview.glb.v1", mutation.Operations[1].CapabilityId, "preview capability");
        Equal(1, mutation.Entities.Count, "entity count");
        Equal(1, mutation.Occurrences.Count, "occurrence count");
        Equal(1, mutation.Bom.Count, "BOM row count");
        Equal("BOX", mutation.Bom[0].PartNumber, "BOM part number");
        Equal("box-root", mutation.Bom[0].SourceEntityId, "BOM source entity");
        Equal(2, mutation.Artifacts.Count, "artifact count");
        True(mutation.Artifacts.Any(value => value.Kind == PhotonCadArtifactKindV1.Step), "STEP missing");
        True(mutation.Artifacts.Any(value => value.Kind == PhotonCadArtifactKindV1.Glb), "GLB missing");
        var stepArtifact = mutation.Artifacts.Single(value => value.Kind == PhotonCadArtifactKindV1.Step);
        var previewArtifact = mutation.Artifacts.Single(value => value.Kind == PhotonCadArtifactKindV1.Glb);
        True(stepArtifact.Bounds is null, "authoritative STEP bounds must be null");
        True(previewArtifact.ReplacesContentDigest is null, "first preview must not claim replacement");
        True(mutation.Artifacts.All(value => value.ByteLength <= PhotonCadRuntimeSyncContract.MaximumSealedArtifactBytes), "per artifact codec bound");
        True(mutation.TotalArtifactBytes <= PhotonCadRuntimeSyncContract.MaximumSealedArtifactBytesPerMutation, "aggregate codec bound");
        True(runner.Requests.Count == 2 && runner.Requests.All(value => !ContainsForbiddenTransportTruth(value)), "transport truth leaked");
        var step = mutation.Artifacts.Single(value => value.Kind == PhotonCadArtifactKindV1.Step).Content.ToArray();
        runner.OverwriteLastOutput();
        ArtifactReader.ValidateStep(step);
    }

    private static async Task FirstAndSecondMutationAsync()
    {
        await using var runner = new FakeRunner();
        var runtime = Runtime(runner);
        const string sessionId = "pcsid:two-mutations";
        const string projectId = "pcpid:two-mutations";
        var codec = new PhotonCadCanonicalProjectCodecV1(new FixedIdentityIssuer(sessionId, projectId));
        var mapper = new PhotonCadRuntimeCanonicalMapperV1(codec, IndustrialPolicy());
        var initial = await codec.CreateAsync("Two industrial mutations", PhotonCadProjectUnit.Millimeter);
        var handle = new PhotonCadProjectHandle("cad-project:11111111111111111111111111111111");

        var first = runtime.BindBox(
            "request-first-canonical",
            sessionId,
            projectId,
            initial.Revision,
            "first-root",
            10,
            20,
            30);
        var firstBinding = new PhotonCadCanonicalMutationBinding(handle, initial);
        var firstProviderRequest = mapper.PrepareProviderRequest(firstBinding, first.Request);
        var firstMutation = await first.Provider.ApplyAsync(firstProviderRequest);
        var firstStep = firstMutation.Artifacts.Single(artifact => artifact.Role == PhotonCadArtifactRoleV1.AuthoritativeGeometry);
        var firstPreviewDelta = firstMutation.Artifacts.Single(artifact => artifact.Role == PhotonCadArtifactRoleV1.ProjectPreview);
        True(firstStep.Bounds is null, "first STEP carried forbidden bounds");
        True(firstPreviewDelta.ReplacesContentDigest is null, "first preview replacement must be null");
        var firstUpdated = mapper.Apply(firstBinding, first.Request, firstMutation);
        var firstSaved = codec.MarkSaved(firstUpdated);
        var firstState = codec.Inspect(codec.Decode(firstSaved.CanonicalBytes));
        var firstPreview = firstState.Artifacts.Single(artifact => artifact.Role == PhotonCadArtifactRoleV1.ProjectPreview);
        Equal(2L, firstState.Revision, "first canonical revision");
        True(firstState.Artifacts.Single(artifact => artifact.Role == PhotonCadArtifactRoleV1.AuthoritativeGeometry).Bounds is null,
            "first canonical STEP bounds");

        var second = runtime.BindCylinder(
            "request-second-canonical",
            sessionId,
            projectId,
            firstSaved.Revision,
            "second-root",
            5,
            12);
        var secondBinding = new PhotonCadCanonicalMutationBinding(handle, firstSaved);
        var secondProviderRequest = mapper.PrepareProviderRequest(secondBinding, second.Request);
        var secondMutation = await second.Provider.ApplyAsync(secondProviderRequest);
        var secondPreviewDelta = secondMutation.Artifacts.Single(artifact => artifact.Role == PhotonCadArtifactRoleV1.ProjectPreview);
        Equal(firstPreview.Digest, secondPreviewDelta.ReplacesContentDigest, "second preview CAS digest");
        var secondUpdated = mapper.Apply(secondBinding, second.Request, secondMutation);
        var secondSaved = codec.MarkSaved(secondUpdated);
        var reopened = codec.Decode(secondSaved.CanonicalBytes);
        var secondState = codec.Inspect(reopened);
        Equal(4L, secondState.Revision, "second canonical revision");
        Equal(2, secondState.Entities.Count, "second canonical entity count");
        Equal(2, secondState.Bom.Count, "second canonical BOM count");
        True(secondState.Bom.Select(row => row.SourceEntityId).ToHashSet(StringComparer.Ordinal)
            .SetEquals(["first-root", "second-root"]), "second canonical BOM source binding");
        Equal(2, secondState.Artifacts.Count(artifact => artifact.Role == PhotonCadArtifactRoleV1.AuthoritativeGeometry),
            "second canonical geometry count");
        Equal(1, secondState.Artifacts.Count(artifact => artifact.Role == PhotonCadArtifactRoleV1.ProjectPreview),
            "second canonical preview count");
        True(secondState.Artifacts
            .Where(artifact => artifact.Role == PhotonCadArtifactRoleV1.AuthoritativeGeometry)
            .All(artifact => artifact.Bounds is null), "reopened STEP bounds must all be null");
        var secondPreview = secondState.Artifacts.Single(artifact => artifact.Role == PhotonCadArtifactRoleV1.ProjectPreview);
        Equal(secondPreviewDelta.ContentDigest, secondPreview.Digest, "reopened preview digest");
        True(!ProtocolV1.FixedDigestEquals(firstPreview.Digest, secondPreview.Digest), "second preview did not replace first bytes");
        using var secondPreviewRequest = JsonDocument.Parse(runner.Requests[3]);
        Equal(2, secondPreviewRequest.RootElement.GetProperty("sources").GetArrayLength(), "complete preview source count");
        Equal(2, secondPreviewRequest.RootElement.GetProperty("occurrences").GetArrayLength(), "complete preview occurrence count");
        var tags = ReadGlbEntityTags(secondPreview.Content.Span);
        True(tags.SetEquals(["first-root.occ", "second-root.occ"]), "complete preview entity tags");
    }

    private static async Task BoundCylinderAsync()
    {
        await using var runner = new FakeRunner();
        var runtime = Runtime(runner);
        var bound = runtime.BindCylinder("request-cylinder", "pcsid:cylinder", "pcpid:cylinder", 0, "cylinder-root", 5, 12);
        var providerRequest = await ProviderRequestAsync(bound.Request);
        var mutation = await bound.Provider.ApplyAsync(providerRequest);
        var operation = mutation.Operations[0];
        Equal("radiusMm", operation.Inputs[0].Id, "radius input");
        Equal("heightMm", operation.Inputs[1].Id, "height input");
        True(runner.Requests[0].Contains("\"radiusMm\":5", StringComparison.Ordinal), "normalized radius not serialized");
    }

    private static async Task ForeignRequestAsync()
    {
        await using var runner = new FakeRunner();
        var runtime = Runtime(runner);
        var bound = runtime.BindBox("request-foreign", "pcsid:foreign", "pcpid:foreign", 0, "foreign-root", 10, 20, 30);
        var foreign = new PhotonCadRuntimeSyncRequest(
            bound.Request.RequestId,
            bound.Request.SessionId,
            bound.Request.ProjectId,
            bound.Request.BaseRevision,
            bound.Request.CapabilityId,
            bound.Request.Mode,
            bound.Request.Inputs,
            bound.Request.TargetEntityIds);
        var providerRequest = await ProviderRequestAsync(foreign);
        await ThrowsAsync<InvalidOperationException>(() => bound.Provider.ApplyAsync(providerRequest).AsTask(), "not_bound");
        Equal(0, runner.Requests.Count, "foreign request reached runner");
    }

    private static async Task OneShotAsync()
    {
        await using var runner = new FakeRunner();
        var (bound, providerRequest) = await BoxAsync(runner, "oneshot");
        _ = await bound.Provider.ApplyAsync(providerRequest);
        await ThrowsAsync<InvalidOperationException>(() => bound.Provider.ApplyAsync(providerRequest).AsTask(), "single_use");
    }

    private static async Task CompensationAsync()
    {
        await using var runner = new FakeRunner();
        var (bound, providerRequest) = await BoxAsync(runner, "compensate");
        var mutation = await bound.Provider.ApplyAsync(providerRequest);
        await bound.Compensator.CompensateAsync(mutation, "commit_failed");
        await bound.Compensator.CompensateAsync(mutation, "commit_failed_retry");
    }

    private static Task UnknownMemberAsync()
    {
        var command = BoxCommand();
        var payload = Encoding.UTF8.GetBytes(PrimitiveResponse(command, Step(), addUnknown: true));
        Throws<InvalidDataException>(() => ProtocolV1.ParsePrimitiveResponse(payload, command), "unexpected_protocol_member");
        return Task.CompletedTask;
    }

    private static Task DuplicateMemberAsync()
    {
        var command = BoxCommand();
        var valid = PrimitiveResponse(command, Step());
        var payload = Encoding.UTF8.GetBytes(valid.Replace("\"ok\":true", "\"ok\":true,\"ok\":true", StringComparison.Ordinal));
        Throws<InvalidDataException>(() => ProtocolV1.ParsePrimitiveResponse(payload, command), "duplicate_protocol_member");
        return Task.CompletedTask;
    }

    private static async Task DigestMismatchAsync()
    {
        await using var runner = new FakeRunner { CorruptPrimitiveDigest = true };
        var (bound, providerRequest) = await BoxAsync(runner, "digest");
        await ThrowsAsync<InvalidDataException>(() => bound.Provider.ApplyAsync(providerRequest).AsTask(), "artifact_digest_mismatch");
    }

    private static Task ExternalUriAsync()
    {
        var command = new IndustrialPreviewCommand(
            [new IndustrialPreviewSource("root", "part0000", ProtocolV1.Sha256(Step()), Step().LongLength)],
            [new IndustrialPreviewOccurrence("root.occ", "root", null, MutationMapperV1.IdentityTransform)]);
        var bytes = Glb(command, externalUri: true);
        Throws<InvalidDataException>(() => GlbValidator.Validate(bytes, command, new IndustrialBounds(0, 0, 0, 1, 1, 1)), "uri");
        return Task.CompletedTask;
    }

    private static async Task CatalogCacheAsync()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"schema\":\"photon.cad.industrial.catalog/v1\",\"items\":[]}");
        var calls = 0;
        var cache = new CatalogCache(_ =>
        {
            calls++;
            return ValueTask.FromResult(bytes.ToArray());
        }, ProtocolV1.Sha256(bytes));
        var first = await cache.GetAsync(CancellationToken.None);
        var second = await cache.GetAsync(CancellationToken.None);
        Equal(1, calls, "catalog load count");
        True(first.Span.SequenceEqual(second.Span), "catalog cache bytes");
    }

    private static Task PublicSurfaceAsync()
    {
        var forbidden = new[]
        {
            typeof(Process), typeof(Stream), typeof(FileInfo), typeof(DirectoryInfo),
            typeof(System.Runtime.InteropServices.SafeHandle),
        };
        foreach (var property in typeof(PhotonCadIndustrialBoundMutation).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            True(forbidden.All(type => !type.IsAssignableFrom(property.PropertyType)), $"forbidden property type: {property.Name}");
        var providerMethod = typeof(IPhotonCadSealedMutationProvider).GetMethod(nameof(IPhotonCadSealedMutationProvider.ApplyAsync))!;
        True(providerMethod.GetParameters().All(parameter => forbidden.All(type => !type.IsAssignableFrom(parameter.ParameterType))), "provider process/path parameter leak");
        return Task.CompletedTask;
    }

    private static async Task<(PhotonCadIndustrialBoundMutation Bound, PhotonCadSealedMutationProviderRequest Request)> BoxAsync(
        FakeRunner runner,
        string suffix = "box")
    {
        var runtime = Runtime(runner);
        var bound = runtime.BindBox($"request-{suffix}", $"pcsid:{suffix}", $"pcpid:{suffix}", 0, $"{suffix}-root", 10, 20, 30);
        return (bound, await ProviderRequestAsync(bound.Request));
    }

    private static PhotonCadIndustrialProviderRuntime Runtime(FakeRunner runner)
    {
        var image = EvidenceVerifier.AcceptedDerivedImageId;
        var receipt = EvidenceVerifier.AcceptedReceiptSha256;
        var evidence = new PhotonCadProviderEvidence(
            PhotonCadBackendV1.Assembly,
            "photon.cad.industrial.docker.v1",
            ["photon.cad.industrial.protocol.v1"],
            EvidenceVerifier.AcceptedCatalogDigest,
            "photon.cad.industrial.container.v1",
            receipt,
            receipt,
            image,
            EvidenceVerifier.AcceptedBaseImageId,
            new PhotonCadSourceIdentityV1("photon-cad-industrial", "0.1.0", image, "redistribution-blocked"));
        return PhotonCadIndustrialProviderRuntime.CreateForSmoke(
            runner,
            new VerifiedIndustrialEvidence(receipt, image, EvidenceVerifier.AcceptedBaseImageId, EvidenceVerifier.AcceptedCatalogDigest, evidence));
    }

    private static async Task<PhotonCadSealedMutationProviderRequest> ProviderRequestAsync(PhotonCadRuntimeSyncRequest request)
    {
        var codec = new PhotonCadCanonicalProjectCodecV1(new FixedIdentityIssuer(request.SessionId, request.ProjectId));
        var project = await codec.CreateAsync("Industrial smoke", PhotonCadProjectUnit.Millimeter);
        var binding = new PhotonCadCanonicalMutationBinding(
            new PhotonCadProjectHandle("cad-project:00000000000000000000000000000000"),
            project);
        var mapper = new PhotonCadRuntimeCanonicalMapperV1(codec, IndustrialPolicy());
        return mapper.PrepareProviderRequest(binding, request);
    }

    private static PhotonCadRuntimeSyncPolicy IndustrialPolicy() => new(
            PhotonCadRuntimeSyncContract.MaximumSealedArtifactBytes,
            PhotonCadRuntimeSyncContract.MaximumSealedArtifactBytesPerMutation,
            PhotonCadRuntimeSyncContract.MaximumBaseArtifactBytes,
            PhotonCadRuntimeSyncContract.MaximumBaseArtifactBytesPerRequest);

    internal static IndustrialPrimitiveCommand BoxCommand() => new(
        IndustrialPrimitiveKind.Box, 10, 20, 30, 0, "root", "BOX", "Create industrial box");

    internal static byte[] Step() => Encoding.ASCII.GetBytes(
        "ISO-10303-21;\nHEADER;\nFILE_DESCRIPTION(('sealed'),'2;1');\nENDSEC;\nDATA;\nENDSEC;\nEND-ISO-10303-21;\n");

    internal static string PrimitiveResponse(IndustrialPrimitiveCommand command, byte[] step, bool addUnknown = false)
    {
        var digest = ProtocolV1.Sha256(step);
        object provenance = command.Kind == IndustrialPrimitiveKind.Box
            ? new { generator = "primitive", kind = "box", parameters = new { heightMm = command.HeightMm, lengthMm = command.LengthMm, widthMm = command.WidthMm } }
            : new { generator = "primitive", kind = "cylinder", parameters = new { heightMm = command.HeightMm, radiusMm = command.RadiusMm } };
        var payload = new Dictionary<string, object?>
        {
            ["schema"] = ProtocolV1.ResponseSchema,
            ["ok"] = true,
            ["operation"] = "createPrimitive",
            ["artifact"] = new { format = "step", contentDigest = digest, byteLength = step.Length },
            ["measurement"] = new
            {
                units = "millimeter",
                volumeMm3 = command.Kind == IndustrialPrimitiveKind.Box
                    ? command.LengthMm * command.WidthMm * command.HeightMm
                    : Math.PI * command.RadiusMm * command.RadiusMm * command.HeightMm,
                solidCount = 1,
                bounds = new { minimum = new[] { 0d, 0d, 0d }, maximum = new[] { 10d, 20d, 30d } },
            },
            ["provenance"] = provenance,
        };
        if (addUnknown) payload["outputPath"] = "C:\\forbidden.step";
        return JsonSerializer.Serialize(payload);
    }

    internal static byte[] Glb(IndustrialPreviewCommand command, bool externalUri = false)
    {
        object buffer = externalUri ? new { byteLength = 4, uri = "file:///forbidden.bin" } : new { byteLength = 4 };
        var sourceIndexes = command.Sources
            .Select((source, index) => (source.SourcePartId, index))
            .ToDictionary(value => value.SourcePartId, value => value.index, StringComparer.Ordinal);
        var occurrenceIndexes = command.Occurrences
            .Select((occurrence, index) => (occurrence.EntityId, index))
            .ToDictionary(value => value.EntityId, value => value.index, StringComparer.Ordinal);
        var nodes = command.Occurrences.Select(occurrence =>
        {
            var node = new Dictionary<string, object?>
            {
                ["mesh"] = sourceIndexes[occurrence.SourcePartId],
                ["matrix"] = ColumnMajor(occurrence.Transform),
                ["extras"] = new { photonEntityId = occurrence.EntityId },
            };
            var children = command.Occurrences
                .Where(candidate => StringComparer.Ordinal.Equals(candidate.ParentEntityId, occurrence.EntityId))
                .Select(candidate => occurrenceIndexes[candidate.EntityId])
                .ToArray();
            if (children.Length > 0) node["children"] = children;
            return node;
        }).ToArray();
        var roots = command.Occurrences
            .Where(occurrence => occurrence.ParentEntityId is null)
            .Select(occurrence => occurrenceIndexes[occurrence.EntityId])
            .ToArray();
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            asset = new { version = "2.0", generator = "Photon CAD industrial container" },
            scene = 0,
            scenes = new[] { new { nodes = roots } },
            nodes,
            meshes = command.Sources.Select(_ => new { primitives = Array.Empty<object>() }).ToArray(),
            buffers = new[] { buffer },
        }).ToList();
        while (json.Count % 4 != 0) json.Add((byte)' ');
        var total = 12 + 8 + json.Count + 8 + 4;
        var result = new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(result, 0x46546C67);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), (uint)total);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12), (uint)json.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16), 0x4E4F534A);
        json.ToArray().CopyTo(result, 20);
        var binaryHeader = 20 + json.Count;
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(binaryHeader), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(binaryHeader + 4), 0x004E4942);
        return result;
    }

    private static double[] ColumnMajor(IReadOnlyList<double> rowMajor) =>
        [.. Enumerable.Range(0, 4).SelectMany(column => Enumerable.Range(0, 4).Select(row => rowMajor[row * 4 + column]))];

    private static HashSet<string> ReadGlbEntityTags(ReadOnlySpan<byte> bytes)
    {
        var jsonLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]));
        using var document = JsonDocument.Parse(bytes.Slice(20, jsonLength).ToArray());
        return document.RootElement.GetProperty("nodes").EnumerateArray()
            .Select(node => node.GetProperty("extras").GetProperty("photonEntityId").GetString()
                ?? throw new InvalidDataException("missing GLB entity tag"))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool ContainsForbiddenTransportTruth(string json) =>
        json.Contains("path", StringComparison.OrdinalIgnoreCase)
        || json.Contains("python", StringComparison.OrdinalIgnoreCase)
        || json.Contains("docker", StringComparison.OrdinalIgnoreCase)
        || json.Contains("command", StringComparison.OrdinalIgnoreCase)
        || json.Contains("executable", StringComparison.OrdinalIgnoreCase);

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Throws<T>(Action action, string contains) where T : Exception
    {
        try { action(); }
        catch (T exception) when (exception.Message.Contains(contains, StringComparison.OrdinalIgnoreCase)) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name} containing {contains}");
    }

    private static async Task ThrowsAsync<T>(Func<Task> action, string contains) where T : Exception
    {
        try { await action(); }
        catch (T exception) when (exception.Message.Contains(contains, StringComparison.OrdinalIgnoreCase)) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name} containing {contains}");
    }
}

internal sealed class FixedIdentityIssuer(string sessionId, string projectId) : IPhotonCadProjectIdentityIssuerV1
{
    public (string SessionId, string ProjectId) NewIdentity() => (sessionId, projectId);
}

internal sealed class FakeRunner : IIndustrialContainerRunner, IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotonCadIndustrialProviderSmoke", Guid.NewGuid().ToString("N"));
    private string? _lastOutput;
    internal List<string> Requests { get; } = [];
    internal bool CorruptPrimitiveDigest { get; set; }

    public ValueTask<IndustrialContainerInvocation> ExecuteAsync(
        ReadOnlyMemory<byte> request,
        IReadOnlyList<IndustrialInputArtifact> inputs,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var json = Encoding.UTF8.GetString(request.Span);
        Requests.Add(json);
        using var document = JsonDocument.Parse(request);
        var operation = document.RootElement.GetProperty("operation").GetString();
        var job = Path.Combine(_root, $"job-{Requests.Count}");
        var output = Path.Combine(job, "output");
        Directory.CreateDirectory(output);
        _lastOutput = output;
        if (operation == "createPrimitive")
        {
            var primitive = document.RootElement.GetProperty("primitive");
            var kind = primitive.GetProperty("kind").GetString();
            var dimensions = primitive.GetProperty("dimensions");
            var command = kind == "box"
                ? new IndustrialPrimitiveCommand(
                    IndustrialPrimitiveKind.Box,
                    dimensions.GetProperty("lengthMm").GetDouble(),
                    dimensions.GetProperty("widthMm").GetDouble(),
                    dimensions.GetProperty("heightMm").GetDouble(),
                    0, "unused", "BOX", "Create industrial box")
                : new IndustrialPrimitiveCommand(
                    IndustrialPrimitiveKind.Cylinder,
                    0, 0,
                    dimensions.GetProperty("heightMm").GetDouble(),
                    dimensions.GetProperty("radiusMm").GetDouble(),
                    "unused", "CYLINDER", "Create industrial cylinder");
            var step = Smoke.Step();
            File.WriteAllBytes(Path.Combine(output, "model.step"), step);
            var response = Smoke.PrimitiveResponse(command, step);
            if (CorruptPrimitiveDigest)
                response = response.Replace(ProtocolV1.Sha256(step), "sha256:" + new string('0', 64), StringComparison.Ordinal);
            return ValueTask.FromResult(new IndustrialContainerInvocation(
                Encoding.UTF8.GetBytes(response), output, () => ValueTask.CompletedTask));
        }
        if (operation == "createPreview")
        {
            var sources = document.RootElement.GetProperty("sources").EnumerateArray()
                .Select(source =>
                {
                    var slot = source.GetProperty("inputSlot").GetString()!;
                    var input = inputs.Single(candidate => StringComparer.Ordinal.Equals(candidate.Slot, slot));
                    return new IndustrialPreviewSource(
                        source.GetProperty("sourcePartId").GetString()!,
                        slot,
                        source.GetProperty("expectedDigest").GetString()!,
                        input.Content.Length);
                })
                .ToArray();
            var occurrences = document.RootElement.GetProperty("occurrences").EnumerateArray()
                .Select(occurrence => new IndustrialPreviewOccurrence(
                    occurrence.GetProperty("entityId").GetString()!,
                    occurrence.GetProperty("sourcePartId").GetString()!,
                    occurrence.GetProperty("parentEntityId").ValueKind == JsonValueKind.Null
                        ? null
                        : occurrence.GetProperty("parentEntityId").GetString(),
                    occurrence.GetProperty("transform").EnumerateArray().Select(value => value.GetDouble()).ToArray()))
                .ToArray();
            var command = new IndustrialPreviewCommand(sources, occurrences);
            EqualInputs(inputs, command);
            var glb = Smoke.Glb(command);
            File.WriteAllBytes(Path.Combine(output, "preview.glb"), glb);
            var response = JsonSerializer.Serialize(new
            {
                schema = ProtocolV1.ResponseSchema,
                ok = true,
                operation = "createPreview",
                artifact = new { format = "glb", contentDigest = ProtocolV1.Sha256(glb), byteLength = glb.Length },
                entityCount = command.Occurrences.Count,
                sourceCount = command.Sources.Count,
                bounds = new { minimum = new[] { 0d, 0d, 0d }, maximum = new[] { 10d, 20d, 30d } },
                units = "millimeter",
                provenance = new
                {
                    sources = command.Sources.Select(source => new
                    {
                        sourcePartId = source.SourcePartId,
                        contentDigest = source.ExpectedDigest,
                        byteLength = source.ExpectedByteLength,
                    }).ToArray(),
                    occurrences = command.Occurrences.Select(occurrence => new
                    {
                        entityId = occurrence.EntityId,
                        sourcePartId = occurrence.SourcePartId,
                        parentEntityId = occurrence.ParentEntityId,
                        transform = occurrence.Transform,
                    }).ToArray(),
                    tessellation = new { linearToleranceMm = 0.1, angularToleranceRad = 0.1 },
                },
            });
            return ValueTask.FromResult(new IndustrialContainerInvocation(
                Encoding.UTF8.GetBytes(response), output, () => ValueTask.CompletedTask));
        }
        throw new InvalidOperationException("unexpected fake operation");
    }

    internal void OverwriteLastOutput()
    {
        if (_lastOutput is not null) File.WriteAllText(Path.Combine(_lastOutput, "model.step"), "overwritten");
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    private static void EqualInputs(IReadOnlyList<IndustrialInputArtifact> inputs, IndustrialPreviewCommand command)
    {
        if (inputs.Count != command.Sources.Count
            || inputs.Any(input => !ProtocolV1.FixedDigestEquals(ProtocolV1.Sha256(input.Content.Span), input.Digest))
            || command.Sources.Any(source => inputs.All(input => !StringComparer.Ordinal.Equals(input.Slot, source.InputSlot)
                || !ProtocolV1.FixedDigestEquals(input.Digest, source.ExpectedDigest)
                || input.Content.Length != source.ExpectedByteLength)))
            throw new InvalidOperationException("fake input mismatch");
    }
}
