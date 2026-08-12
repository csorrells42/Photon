using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PhotonCadProjects;
using PhotonCadProjects.Codec;
using PhotonCadProjects.RuntimeSync;
using PhotonCadRuntime.IndustrialProvider;

var smoke = new AssemblySmoke();
return await smoke.RunAsync();

internal sealed class AssemblySmoke
{
    private int _passed;
    private int _failed;

    internal async Task<int> RunAsync()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("two-part assembly saves and reopens 0-2-4", InitialAssemblyRoundTripAsync),
            ("place transform nested place and branch remove persist exact DAG", AssemblyLifecycleAsync),
            ("repeated source quantities derive exact replace-all BOM", RepeatedPartBomAsync),
            ("retained primitive and catalog definitions can be re-placed after their final occurrence is removed", RePlaceAfterZeroAsync),
            ("stale foreign duplicate source and deletion requests fail before runner", BindingHostilesAsync),
            ("root cycle duplicate depth and rigid-transform hostiles fail closed", GraphHostilesAsync),
            ("preview replacement is digest-bound and complete-project", PreviewCasAsync),
            ("assembly provider is one-shot and compensation-bound", LifecycleHostilesAsync),
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

        Console.WriteLine($"PhotonCadRuntime.IndustrialProvider.AssemblySmoke: {_passed}/{_passed + _failed} passed");
        return _failed == 0 ? 0 : 1;
    }

    private static async Task InitialAssemblyRoundTripAsync()
    {
        await using var environment = await AssemblyEnvironment.CreateAsync("initial");
        Equal(4L, environment.Current.Revision, "two-part revision");
        var state = environment.Reopen();
        Equal(2, state.Entities.Count, "entity count");
        Equal(2, state.Occurrences.Count, "occurrence count");
        Equal(2, state.Bom.Count, "BOM row count");
        Equal("box.occ", state.Occurrences.Single(value => value.OccurrenceId == "cylinder.occ").ParentOccurrenceId,
            "cylinder parent");
        True(state.Artifacts.Count(value => value.Role == PhotonCadArtifactRoleV1.AuthoritativeGeometry) == 2,
            "authoritative geometry count");
        True(state.Artifacts.Count(value => value.Role == PhotonCadArtifactRoleV1.ProjectPreview) == 1,
            "preview count");
        AssertPreview(state, ["box.occ", "cylinder.occ"]);
    }

    private static async Task AssemblyLifecycleAsync()
    {
        await using var environment = await AssemblyEnvironment.CreateAsync("lifecycle");
        var originalGeometry = environment.Reopen().Artifacts
            .Where(value => value.Role == PhotonCadArtifactRoleV1.AuthoritativeGeometry)
            .ToDictionary(value => value.OwnerEntityId!, value => (value.Digest, value.ByteLength, value.Content.ToArray()), StringComparer.Ordinal);

        var placed = environment.Runtime.BindAssemblyPlace(
            "place-box-copy", environment.SessionId, environment.ProjectId, 4,
            "box-copy.occ", "box", "box.occ", Translation(100, 0, 0));
        var place = await environment.CommitAsync(placed.Request, placed.Provider);
        Equal(6L, place.Mutation.ResultingRevision, "place revision");
        Equal(2, place.Mutation.Operations.Count, "place operation count");
        Equal(PhotonCadAssemblyContract.PlaceCapabilityId, place.Mutation.Operations[0].CapabilityId, "place capability");
        Equal(PhotonCadAssemblyContract.PreviewCapabilityId, place.Mutation.Operations[1].CapabilityId, "preview capability");
        Equal(PhotonCadCollectionMergeMode.ReplaceAll, place.Mutation.OccurrenceMergeMode, "occurrence replace-all");
        Equal(PhotonCadCollectionMergeMode.ReplaceAll, place.Mutation.BomMergeMode, "BOM replace-all");
        Equal(1, place.Mutation.Artifacts.Count, "place must emit only complete preview");
        Equal(2d, place.State.Bom.Single(value => value.SourceEntityId == "box").Quantity, "box quantity after place");
        AssertGeometryUnchanged(place.State, originalGeometry);

        var transformed = environment.Runtime.BindAssemblyTransform(
            "transform-box-copy", environment.SessionId, environment.ProjectId, 6,
            "box-copy.occ", "box", Translation(100, 50, 25));
        var transform = await environment.CommitAsync(transformed.Request, transformed.Provider);
        Equal(8L, transform.State.Revision, "transform revision");
        True(transform.State.Occurrences.Single(value => value.OccurrenceId == "box-copy.occ")
            .Transform.SequenceEqual(Translation(100, 50, 25)), "exact rigid transform was not persisted");
        AssertGeometryUnchanged(transform.State, originalGeometry);

        var nested = environment.Runtime.BindAssemblyPlace(
            "place-cylinder-nested", environment.SessionId, environment.ProjectId, 8,
            "cylinder-copy.occ", "cylinder", "box-copy.occ", Translation(0, 25, 0));
        var nestedResult = await environment.CommitAsync(nested.Request, nested.Provider);
        Equal(10L, nestedResult.State.Revision, "nested place revision");
        Equal("box-copy.occ", nestedResult.State.Occurrences.Single(value => value.OccurrenceId == "cylinder-copy.occ").ParentOccurrenceId,
            "nested parent");
        Equal(2d, nestedResult.State.Bom.Single(value => value.SourceEntityId == "cylinder").Quantity,
            "nested cylinder quantity");
        AssertGeometryUnchanged(nestedResult.State, originalGeometry);
        AssertPreview(nestedResult.State, ["box-copy.occ", "box.occ", "cylinder-copy.occ", "cylinder.occ"]);

        var removed = environment.Runtime.BindAssemblyRemove(
            "remove-box-branch", environment.SessionId, environment.ProjectId, 10,
            "box-copy.occ", "box");
        var remove = await environment.CommitAsync(removed.Request, removed.Provider);
        Equal(12L, remove.State.Revision, "remove revision");
        True(remove.State.Occurrences.Select(value => value.OccurrenceId).ToHashSet(StringComparer.Ordinal)
            .SetEquals(["box.occ", "cylinder.occ"]), "branch removal did not remove exactly the branch and descendants");
        Equal(1d, remove.State.Bom.Single(value => value.SourceEntityId == "box").Quantity, "box quantity after remove");
        Equal(1d, remove.State.Bom.Single(value => value.SourceEntityId == "cylinder").Quantity, "cylinder quantity after remove");
        AssertGeometryUnchanged(remove.State, originalGeometry);
        AssertPreview(environment.Reopen(), ["box.occ", "cylinder.occ"]);
    }

    private static async Task RepeatedPartBomAsync()
    {
        await using var environment = await AssemblyEnvironment.CreateAsync("bom");
        for (var index = 1; index <= 3; index++)
        {
            var bound = environment.Runtime.BindAssemblyPlace(
                $"repeat-box-{index}", environment.SessionId, environment.ProjectId, environment.Current.Revision,
                $"box-repeat-{index}.occ", "box", "box.occ", Translation(index * 10, 0, 0));
            _ = await environment.CommitAsync(bound.Request, bound.Provider);
        }

        var state = environment.Reopen();
        Equal(10L, state.Revision, "repeat revision");
        var box = state.Bom.Single(value => value.SourceEntityId == "box");
        var cylinder = state.Bom.Single(value => value.SourceEntityId == "cylinder");
        Equal(4d, box.Quantity, "repeated box quantity");
        Equal(1d, cylinder.Quantity, "unmodified cylinder quantity");
        Equal("BOX", box.PartNumber, "box part number");
        Equal("CYLINDER", cylinder.PartNumber, "cylinder part number");
        Equal(5, state.Occurrences.Count, "repeat occurrence count");
        True(state.Occurrences.Select(value => value.OccurrenceId).SequenceEqual(
            state.Occurrences.Select(value => value.OccurrenceId).OrderBy(value => value, StringComparer.Ordinal),
            StringComparer.Ordinal), "occurrences are not deterministic");
    }

    private static async Task RePlaceAfterZeroAsync()
    {
        await using var environment = await AssemblyEnvironment.CreateAsync("replace-after-zero");
        var removed = environment.Runtime.BindAssemblyRemove(
            "remove-final-cylinder", environment.SessionId, environment.ProjectId, 4,
            "cylinder.occ", "cylinder");
        var afterRemove = await environment.CommitAsync(removed.Request, removed.Provider);
        Equal(6L, afterRemove.State.Revision, "remove final source occurrence revision");
        True(afterRemove.State.Entities.Any(value => value.Id == "cylinder"),
            "final occurrence removal deleted the retained source entity");
        True(afterRemove.State.Bom.All(value => value.SourceEntityId != "cylinder"),
            "final occurrence removal retained a non-existent BOM quantity");

        var replaced = environment.Runtime.BindAssemblyPlace(
            "replace-cylinder", environment.SessionId, environment.ProjectId, 6,
            "cylinder-replaced.occ", "cylinder", "box.occ", Translation(25, 0, 0));
        var afterPlace = await environment.CommitAsync(replaced.Request, replaced.Provider);
        Equal(8L, afterPlace.State.Revision, "re-place revision");
        var row = afterPlace.State.Bom.Single(value => value.SourceEntityId == "cylinder");
        Equal("CYLINDER", row.PartNumber, "recovered part number");
        Equal("Create Cylinder", row.Description, "recovered part description");
        Equal(1d, row.Quantity, "recovered part quantity");
        AssertPreview(environment.Reopen(), ["box.occ", "cylinder-replaced.occ"]);

        var catalog = await environment.Runtime.GetCatalogAsync();
        var definition = catalog.Items.Single(value => value.Title == "Smoke Bearing");
        var catalogBound = await environment.Runtime.BindCatalogItemAsync(
            "create-retained-catalog-part",
            environment.SessionId,
            environment.ProjectId,
            8,
            "catalog-bearing",
            definition.CapabilityId,
            new Dictionary<string, PhotonCadIndustrialCatalogInputValue?>
            {
                ["size"] = PhotonCadIndustrialCatalogInputValue.Choice(
                    definition.Parameters.Single().Choices.Single().Token),
            });
        var catalogResult = await environment.CommitAsync(catalogBound.Request, catalogBound.Provider);
        Equal(10L, catalogResult.State.Revision, "catalog create revision");
        var catalogOccurrence = catalogResult.State.Occurrences.Single(value => value.SourceEntityId == "catalog-bearing");
        var catalogRow = catalogResult.State.Bom.Single(value => value.SourceEntityId == "catalog-bearing");

        var removeCatalog = environment.Runtime.BindAssemblyRemove(
            "remove-final-catalog-occurrence",
            environment.SessionId,
            environment.ProjectId,
            10,
            catalogOccurrence.OccurrenceId,
            "catalog-bearing");
        var catalogRemoved = await environment.CommitAsync(removeCatalog.Request, removeCatalog.Provider);
        Equal(12L, catalogRemoved.State.Revision, "catalog remove revision");
        True(catalogRemoved.State.Entities.Any(value => value.Id == "catalog-bearing"),
            "final catalog occurrence removal deleted the retained source entity");
        True(catalogRemoved.State.Bom.All(value => value.SourceEntityId != "catalog-bearing"),
            "final catalog occurrence removal retained a non-existent BOM quantity");

        var replaceCatalog = environment.Runtime.BindAssemblyPlace(
            "replace-catalog-bearing",
            environment.SessionId,
            environment.ProjectId,
            12,
            "catalog-bearing-replaced.occ",
            "catalog-bearing",
            "box.occ",
            Translation(50, 0, 0));
        var catalogReplaced = await environment.CommitAsync(replaceCatalog.Request, replaceCatalog.Provider);
        Equal(14L, catalogReplaced.State.Revision, "catalog re-place revision");
        var recoveredCatalogRow = catalogReplaced.State.Bom.Single(value => value.SourceEntityId == "catalog-bearing");
        Equal(catalogRow.PartNumber, recoveredCatalogRow.PartNumber, "recovered catalog part number");
        Equal(catalogRow.Description, recoveredCatalogRow.Description, "recovered catalog description");
        Equal(1d, recoveredCatalogRow.Quantity, "recovered catalog quantity");
        AssertPreview(environment.Reopen(), ["box.occ", "catalog-bearing-replaced.occ", "cylinder-replaced.occ"]);
    }

    private static async Task BindingHostilesAsync()
    {
        await using var environment = await AssemblyEnvironment.CreateAsync("binding");
        var initialCalls = environment.Runner.Requests.Count;

        var stale = environment.Runtime.BindAssemblyTransform(
            "stale", environment.SessionId, environment.ProjectId, 3,
            "box.occ", "box", Identity());
        Throws<PhotonCadRuntimeSyncException>(() => environment.Prepare(stale.Request), "canonical_base_binding_mismatch");

        var bound = environment.Runtime.BindAssemblyTransform(
            "foreign", environment.SessionId, environment.ProjectId, 4,
            "box.occ", "box", Identity());
        var foreignRequest = new PhotonCadRuntimeSyncRequest(
            bound.Request.RequestId, bound.Request.SessionId, bound.Request.ProjectId, bound.Request.BaseRevision,
            bound.Request.CapabilityId, bound.Request.Mode, bound.Request.Inputs, bound.Request.TargetEntityIds);
        await ThrowsAsync<InvalidOperationException>(() => bound.Provider.ApplyAsync(environment.Prepare(foreignRequest)).AsTask(), "not_bound");

        var duplicate = environment.Runtime.BindAssemblyPlace(
            "duplicate", environment.SessionId, environment.ProjectId, 4,
            "box.occ", "box", "box.occ", Identity());
        await ThrowsAsync<InvalidOperationException>(() => duplicate.Provider.ApplyAsync(environment.Prepare(duplicate.Request)).AsTask(), "duplicate");

        var wrongSource = environment.Runtime.BindAssemblyTransform(
            "wrong-source", environment.SessionId, environment.ProjectId, 4,
            "box.occ", "cylinder", Identity());
        await ThrowsAsync<InvalidOperationException>(() => wrongSource.Provider.ApplyAsync(environment.Prepare(wrongSource.Request)).AsTask(),
            "target_missing");

        var missingParent = environment.Runtime.BindAssemblyPlace(
            "missing-parent", environment.SessionId, environment.ProjectId, 4,
            "new.occ", "box", "absent.occ", Identity());
        await ThrowsAsync<InvalidOperationException>(() => missingParent.Provider.ApplyAsync(environment.Prepare(missingParent.Request)).AsTask(),
            "parent_missing");

        var removeRoot = environment.Runtime.BindAssemblyRemove(
            "remove-root", environment.SessionId, environment.ProjectId, 4,
            "box.occ", "box");
        await ThrowsAsync<InvalidOperationException>(() => removeRoot.Provider.ApplyAsync(environment.Prepare(removeRoot.Request)).AsTask(),
            "last_occurrence");

        Equal(initialCalls, environment.Runner.Requests.Count, "hostile binding reached container runner");
    }

    private static Task GraphHostilesAsync()
    {
        var box = "box";
        Throws<InvalidOperationException>(() => AssemblyMutationProvider.ValidateOccurrenceDag(
        [
            Occurrence("root-a", null, box),
            Occurrence("root-b", null, box),
        ]), "root_count");
        Throws<InvalidOperationException>(() => AssemblyMutationProvider.ValidateOccurrenceDag(
        [
            Occurrence("root", null, box),
            Occurrence("duplicate", "root", box),
            Occurrence("DUPLICATE", "root", box),
        ]), "duplicate");
        Throws<InvalidOperationException>(() => AssemblyMutationProvider.ValidateOccurrenceDag(
        [
            Occurrence("root", null, box),
            Occurrence("cycle-a", "cycle-b", box),
            Occurrence("cycle-b", "cycle-a", box),
        ]), "cycle");
        var tooMany = Enumerable.Range(0, 1_025)
            .Select(index => Occurrence($"count-{index}", index == 0 ? null : "count-0", box))
            .ToArray();
        Throws<InvalidOperationException>(() => AssemblyMutationProvider.ValidateOccurrenceDag(tooMany), "count_invalid");

        var tooDeep = new List<PhotonCadOccurrenceV1> { Occurrence("root", null, box) };
        for (var index = 1; index <= PhotonCadProjectFileV1.MaximumEntityTreeDepth; index++)
            tooDeep.Add(Occurrence($"depth-{index}", index == 1 ? "root" : $"depth-{index - 1}", box));
        Throws<InvalidOperationException>(() => AssemblyMutationProvider.ValidateOccurrenceDag(tooDeep), "too_deep");

        Throws<ArgumentException>(() => new AssemblyMutationCommand(
            AssemblyMutationKind.Transform, "root", box, null, Scale()), "not_rigid");
        Throws<ArgumentException>(() => new AssemblyMutationCommand(
            AssemblyMutationKind.Transform, "root", box, null, Identity()[..15]), "length");
        Throws<ArgumentException>(() => new AssemblyMutationCommand(
            AssemblyMutationKind.Transform, "root", box, null, Reflection()), "orientation");
        var nan = Identity();
        nan[0] = double.NaN;
        Throws<ArgumentException>(() => new AssemblyMutationCommand(
            AssemblyMutationKind.Transform, "root", box, null, nan), "number");
        return Task.CompletedTask;
    }

    private static async Task PreviewCasAsync()
    {
        await using var environment = await AssemblyEnvironment.CreateAsync("cas");
        var basePreview = environment.Reopen().Artifacts.Single(value => value.Role == PhotonCadArtifactRoleV1.ProjectPreview);
        var bound = environment.Runtime.BindAssemblyPlace(
            "cas-place", environment.SessionId, environment.ProjectId, 4,
            "cas-copy.occ", "box", "box.occ", Translation(25, 0, 0));
        var providerRequest = environment.Prepare(bound.Request);
        var mutation = await bound.Provider.ApplyAsync(providerRequest);
        Equal(6L, mutation.ResultingRevision, "CAS mutation revision");
        Equal(2, mutation.Operations.Count, "CAS operation count");
        var preview = mutation.Artifacts.Single();
        Equal(basePreview.Digest, preview.ReplacesContentDigest, "preview replacement digest");
        Equal(mutation.Operations[1].Id, preview.OperationId, "preview operation binding");
        Equal(mutation.ResultingRevision, preview.Revision, "preview revision binding");

        var wrongPreview = new PhotonCadSealedArtifactDelta(
            preview.Role, preview.Kind, preview.OwnerEntityId, preview.Revision, preview.Content,
            preview.ByteLength, preview.ContentDigest, preview.MediaType, preview.Bounds,
            preview.OperationId, preview.Evidence, "sha256:" + new string('f', 64));
        var wrong = new PhotonCadSealedMutationDelta(
            "wrong-preview-cas", mutation.RequestId, mutation.SessionId, mutation.ProjectId,
            mutation.BaseRevision, mutation.ResultingRevision, mutation.Operations,
            mutation.Entities, mutation.Occurrences, mutation.Issues, mutation.Bom, [wrongPreview],
            mutation.OccurrenceMergeMode, mutation.IssueMergeMode, mutation.BomMergeMode);
        Throws<PhotonCadRuntimeSyncException>(() => environment.Mapper.Apply(environment.Binding(), bound.Request, wrong),
            "replacement_binding");
        await bound.Compensator.CompensateAsync(mutation, "canonical_commit_failed");
    }

    private static async Task LifecycleHostilesAsync()
    {
        await using var environment = await AssemblyEnvironment.CreateAsync("lifecycle-hostile");
        var bound = environment.Runtime.BindAssemblyTransform(
            "one-shot", environment.SessionId, environment.ProjectId, 4,
            "cylinder.occ", "cylinder", Translation(0, 10, 0));
        var request = environment.Prepare(bound.Request);
        var mutation = await bound.Provider.ApplyAsync(request);
        await ThrowsAsync<InvalidOperationException>(() => bound.Provider.ApplyAsync(request).AsTask(), "single_use");
        await bound.Compensator.CompensateAsync(mutation, "commit_failed");
        await bound.Compensator.CompensateAsync(mutation, "commit_failed_retry");
        var other = environment.Runtime.BindAssemblyTransform(
            "other", environment.SessionId, environment.ProjectId, 4,
            "cylinder.occ", "cylinder", Identity());
        var otherMutation = await other.Provider.ApplyAsync(environment.Prepare(other.Request));
        await ThrowsAsync<InvalidOperationException>(() => bound.Compensator.CompensateAsync(otherMutation, "foreign").AsTask(),
            "binding_mismatch");
    }

    private static PhotonCadOccurrenceV1 Occurrence(string id, string? parent, string source) =>
        new(id, parent, source.ToUpperInvariant(), source, Identity());

    private static void AssertPreview(PhotonCadProjectStateV1 state, IReadOnlyCollection<string> expectedIds)
    {
        var preview = state.Artifacts.Single(value => value.Role == PhotonCadArtifactRoleV1.ProjectPreview);
        var nodes = ReadGlbNodes(preview.Content.Span);
        var ids = nodes.Select(value => value.Id).ToHashSet(StringComparer.Ordinal);
        True(ids.SetEquals(expectedIds), "preview entity tags do not match canonical occurrences");
        Equal(state.Occurrences.Count, nodes.Count, "preview node count");
        foreach (var occurrence in state.Occurrences)
        {
            var node = nodes.Single(value => StringComparer.Ordinal.Equals(value.Id, occurrence.OccurrenceId));
            Equal(occurrence.ParentOccurrenceId, node.ParentId, $"preview parent for {occurrence.OccurrenceId}");
            True(occurrence.Transform.Zip(node.Matrix).All(value =>
                    BitConverter.DoubleToInt64Bits(value.First) == BitConverter.DoubleToInt64Bits(value.Second)),
                $"preview transform for {occurrence.OccurrenceId}");
        }
        Equal(state.Revision, preview.Revision, "preview is not at canonical revision");
    }

    private static void AssertGeometryUnchanged(
        PhotonCadProjectStateV1 state,
        IReadOnlyDictionary<string, (string Digest, long ByteLength, byte[] Content)> expected)
    {
        var actual = state.Artifacts.Where(value => value.Role == PhotonCadArtifactRoleV1.AuthoritativeGeometry)
            .ToDictionary(value => value.OwnerEntityId!, StringComparer.Ordinal);
        Equal(expected.Count, actual.Count, "geometry artifact count changed");
        foreach (var pair in expected)
        {
            var artifact = actual[pair.Key];
            Equal(pair.Value.Digest, artifact.Digest, $"geometry digest for {pair.Key}");
            Equal(pair.Value.ByteLength, artifact.ByteLength, $"geometry length for {pair.Key}");
            True(pair.Value.Content.AsSpan().SequenceEqual(artifact.Content.Span), $"geometry bytes for {pair.Key}");
            True(artifact.Bounds is null, $"geometry bounds for {pair.Key}");
        }
    }

    internal static byte[] Step(string marker) => Encoding.ASCII.GetBytes(
        $"ISO-10303-21;\nHEADER;\nFILE_DESCRIPTION(('sealed-{marker}'),'2;1');\nENDSEC;\nDATA;\nENDSEC;\nEND-ISO-10303-21;\n");

    internal static byte[] Glb(IndustrialPreviewCommand command)
    {
        var sourceIndexes = command.Sources.Select((source, index) => (source.SourcePartId, index))
            .ToDictionary(value => value.SourcePartId, value => value.index, StringComparer.Ordinal);
        var occurrenceIndexes = command.Occurrences.Select((occurrence, index) => (occurrence.EntityId, index))
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
        var roots = command.Occurrences.Where(value => value.ParentEntityId is null)
            .Select(value => occurrenceIndexes[value.EntityId]).ToArray();
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            asset = new { version = "2.0", generator = "Photon CAD assembly smoke" },
            scene = 0,
            scenes = new[] { new { nodes = roots } },
            nodes,
            meshes = command.Sources.Select(_ => new { primitives = Array.Empty<object>() }).ToArray(),
            buffers = new[] { new { byteLength = 4 } },
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

    private static IReadOnlyList<(string Id, string? ParentId, double[] Matrix)> ReadGlbNodes(ReadOnlySpan<byte> bytes)
    {
        var jsonLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]));
        using var document = JsonDocument.Parse(bytes.Slice(20, jsonLength).ToArray());
        var nodes = document.RootElement.GetProperty("nodes").EnumerateArray().ToArray();
        var parentByIndex = new Dictionary<int, int>();
        for (var index = 0; index < nodes.Length; index++)
            if (nodes[index].TryGetProperty("children", out var children))
                foreach (var child in children.EnumerateArray()) parentByIndex.Add(child.GetInt32(), index);
        return nodes.Select((node, index) =>
        {
            var id = node.GetProperty("extras").GetProperty("photonEntityId").GetString()
                ?? throw new InvalidDataException("missing GLB entity tag");
            var parent = parentByIndex.TryGetValue(index, out var parentIndex)
                ? nodes[parentIndex].GetProperty("extras").GetProperty("photonEntityId").GetString()
                : null;
            var columnMajor = node.GetProperty("matrix").EnumerateArray().Select(value => value.GetDouble()).ToArray();
            return (id, parent, RowMajor(columnMajor));
        }).ToArray();
    }

    private static double[] ColumnMajor(IReadOnlyList<double> rowMajor) =>
        [.. Enumerable.Range(0, 4).SelectMany(column => Enumerable.Range(0, 4).Select(row => rowMajor[row * 4 + column]))];

    private static double[] RowMajor(IReadOnlyList<double> columnMajor) =>
        [.. Enumerable.Range(0, 4).SelectMany(row => Enumerable.Range(0, 4).Select(column => columnMajor[column * 4 + row]))];

    internal static double[] Identity() =>
    [
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    ];

    internal static double[] Translation(double x, double y, double z) =>
    [
        1, 0, 0, x,
        0, 1, 0, y,
        0, 0, 1, z,
        0, 0, 0, 1,
    ];

    private static double[] Scale() =>
    [
        2, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    ];

    private static double[] Reflection() =>
    [
        -1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    ];

    internal static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
    }

    internal static void True(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    internal static void Throws<T>(Action action, string contains) where T : Exception
    {
        try { action(); }
        catch (T exception) when (exception.Message.Contains(contains, StringComparison.OrdinalIgnoreCase)) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name} containing {contains}");
    }

    internal static async Task ThrowsAsync<T>(Func<Task> action, string contains) where T : Exception
    {
        try { await action(); }
        catch (T exception) when (exception.Message.Contains(contains, StringComparison.OrdinalIgnoreCase)) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name} containing {contains}");
    }
}

internal sealed class AssemblyEnvironment : IAsyncDisposable
{
    private AssemblyEnvironment(string suffix, AssemblyFakeRunner runner, PhotonCadIndustrialProviderRuntime runtime,
        PhotonCadCanonicalProjectCodecV1 codec, PhotonCadRuntimeCanonicalMapperV1 mapper, PhotonCadProjectHandle handle,
        PhotonCadCanonicalProject current)
    {
        SessionId = $"pcsid:assembly-{suffix}";
        ProjectId = $"pcpid:assembly-{suffix}";
        Runner = runner;
        Runtime = runtime;
        Codec = codec;
        Mapper = mapper;
        Handle = handle;
        Current = current;
    }

    internal string SessionId { get; }
    internal string ProjectId { get; }
    internal AssemblyFakeRunner Runner { get; }
    internal PhotonCadIndustrialProviderRuntime Runtime { get; }
    internal PhotonCadCanonicalProjectCodecV1 Codec { get; }
    internal PhotonCadRuntimeCanonicalMapperV1 Mapper { get; }
    internal PhotonCadProjectHandle Handle { get; }
    internal PhotonCadCanonicalProject Current { get; private set; }

    internal static async Task<AssemblyEnvironment> CreateAsync(string suffix)
    {
        var sessionId = $"pcsid:assembly-{suffix}";
        var projectId = $"pcpid:assembly-{suffix}";
        var runner = new AssemblyFakeRunner();
        var runtime = RuntimeFor(runner);
        var codec = new PhotonCadCanonicalProjectCodecV1(new FixedIdentityIssuer(sessionId, projectId));
        var mapper = new PhotonCadRuntimeCanonicalMapperV1(codec, Policy());
        var handle = new PhotonCadProjectHandle($"cad-project:{Guid.NewGuid():N}");
        var current = await codec.CreateAsync($"Assembly {suffix}", PhotonCadProjectUnit.Millimeter);
        var environment = new AssemblyEnvironment(suffix, runner, runtime, codec, mapper, handle, current);

        var box = runtime.BindBox("create-box", sessionId, projectId, 0, "box", 10, 20, 30, "BOX");
        _ = await environment.CommitAsync(box.Request, box.Provider);
        var cylinder = runtime.BindCylinder("create-cylinder", sessionId, projectId, 2, "cylinder", 5, 12, "CYLINDER");
        _ = await environment.CommitAsync(cylinder.Request, cylinder.Provider);
        return environment;
    }

    internal PhotonCadCanonicalMutationBinding Binding() => new(Handle, Current);

    internal PhotonCadSealedMutationProviderRequest Prepare(PhotonCadRuntimeSyncRequest request) =>
        Mapper.PrepareProviderRequest(Binding(), request);

    internal async Task<TransactionResult> CommitAsync(
        PhotonCadRuntimeSyncRequest request,
        IPhotonCadSealedMutationProvider provider)
    {
        var binding = Binding();
        var mutation = await provider.ApplyAsync(Mapper.PrepareProviderRequest(binding, request));
        var updated = Mapper.Apply(binding, request, mutation);
        Current = Codec.MarkSaved(updated);
        var reopened = Codec.Decode(Current.CanonicalBytes);
        var state = Codec.Inspect(reopened);
        AssemblySmoke.Equal(Current.Revision, state.Revision, "reopen revision");
        Current = reopened;
        return new TransactionResult(mutation, state);
    }

    internal PhotonCadProjectStateV1 Reopen() => Codec.Inspect(Codec.Decode(Current.CanonicalBytes));

    public ValueTask DisposeAsync() => Runner.DisposeAsync();

    private static PhotonCadIndustrialProviderRuntime RuntimeFor(AssemblyFakeRunner runner)
    {
        var image = EvidenceVerifier.AcceptedDerivedImageId;
        var receipt = EvidenceVerifier.AcceptedReceiptSha256;
        var evidence = new PhotonCadProviderEvidence(
            PhotonCadBackendV1.Assembly,
            "photon.cad.industrial.docker.v1",
            ["photon.cad.industrial.protocol.v1"],
            runner.CatalogDigest,
            "photon.cad.industrial.container.v1",
            receipt,
            receipt,
            image,
            EvidenceVerifier.AcceptedBaseImageId,
            new PhotonCadSourceIdentityV1("photon-cad-industrial", "0.1.0", image, "redistribution-blocked"));
        return PhotonCadIndustrialProviderRuntime.CreateForSmoke(
            runner,
            new VerifiedIndustrialEvidence(receipt, image, EvidenceVerifier.AcceptedBaseImageId, runner.CatalogDigest, evidence));
    }

    private static PhotonCadRuntimeSyncPolicy Policy() => new(
        PhotonCadRuntimeSyncContract.MaximumSealedArtifactBytes,
        PhotonCadRuntimeSyncContract.MaximumSealedArtifactBytesPerMutation,
        PhotonCadRuntimeSyncContract.MaximumBaseArtifactBytes,
        PhotonCadRuntimeSyncContract.MaximumBaseArtifactBytesPerRequest);

    internal sealed record TransactionResult(PhotonCadSealedMutationDelta Mutation, PhotonCadProjectStateV1 State);
}

internal sealed class FixedIdentityIssuer(string sessionId, string projectId) : IPhotonCadProjectIdentityIssuerV1
{
    public (string SessionId, string ProjectId) NewIdentity() => (sessionId, projectId);
}

internal sealed class AssemblyFakeRunner : IIndustrialContainerRunner, IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"PhotonCadAssemblySmoke-{Guid.NewGuid():N}");
    internal List<string> Requests { get; } = [];
    internal string CatalogDigest => ProtocolV1.Sha256(CatalogBytes);

    private static byte[] CatalogBytes => JsonSerializer.SerializeToUtf8Bytes(new
    {
        schema = "photon.cad.industrial.catalog/v1",
        runtime = new { identity = "assembly-smoke" },
        categories = Array.Empty<object>(),
        items = new object[]
        {
            new
            {
                availability = "supported",
                category = "bearings",
                id = "bdw_111111111111111111111111111111111111111111111111",
                parameters = new object[]
                {
                    new
                    {
                        choices = new[] { "M10-30-9" },
                        id = "size",
                        kind = "choice",
                        label = "Size",
                        nullable = false,
                        required = true,
                    },
                },
                title = "Smoke Bearing",
            },
            new
            {
                availability = "supported",
                category = "gears",
                id = "bdw_222222222222222222222222222222222222222222222222",
                parameters = new object[]
                {
                    new { id = "module", kind = "number", label = "Module", maximum = 1000000d, minimum = 0.000001d, nullable = false, required = true },
                    new { id = "pressure_angle", kind = "number", label = "Pressure angle", maximum = 89d, minimum = 0.1d, nullable = false, required = true },
                    new { id = "thickness", kind = "number", label = "Thickness", maximum = 1000000d, minimum = 0.000001d, nullable = false, required = true },
                    new { id = "tooth_count", kind = "integer", label = "Tooth count", maximum = 1000, minimum = 3, nullable = false, required = true },
                },
                title = "Spur Gear",
            },
            new
            {
                availability = "supported",
                category = "fasteners",
                id = "bdw_333333333333333333333333333333333333333333333333",
                parameters = new object[]
                {
                    new
                    {
                        choices = new[] { "M6-1x20" },
                        id = "size",
                        kind = "choice",
                        label = "Size",
                        nullable = false,
                        required = true,
                    },
                },
                title = "Socket Head Cap Screw",
            },
        },
    });

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
        var job = Path.Combine(_root, $"job-{Requests.Count:D4}");
        var output = Path.Combine(job, "output");
        Directory.CreateDirectory(output);

        if (operation == "catalog")
        {
            using var catalog = JsonDocument.Parse(CatalogBytes);
            var response = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = ProtocolV1.ResponseSchema,
                ok = true,
                operation = "catalog",
                catalogDigest = CatalogDigest,
                catalog = catalog.RootElement,
            });
            return ValueTask.FromResult(new IndustrialContainerInvocation(response, output, () => CleanupAsync(job)));
        }

        if (operation == "createPrimitive")
        {
            var primitive = document.RootElement.GetProperty("primitive");
            var kind = primitive.GetProperty("kind").GetString()!;
            var dimensions = primitive.GetProperty("dimensions");
            var step = AssemblySmoke.Step(kind);
            File.WriteAllBytes(Path.Combine(output, "model.step"), step);
            var digest = ProtocolV1.Sha256(step);
            object parameters = kind == "box"
                ? new
                {
                    heightMm = dimensions.GetProperty("heightMm").GetDouble(),
                    lengthMm = dimensions.GetProperty("lengthMm").GetDouble(),
                    widthMm = dimensions.GetProperty("widthMm").GetDouble(),
                }
                : new
                {
                    heightMm = dimensions.GetProperty("heightMm").GetDouble(),
                    radiusMm = dimensions.GetProperty("radiusMm").GetDouble(),
                };
            var response = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = ProtocolV1.ResponseSchema,
                ok = true,
                operation = "createPrimitive",
                artifact = new { format = "step", contentDigest = digest, byteLength = step.Length },
                measurement = new
                {
                    units = "millimeter",
                    volumeMm3 = 100d,
                    solidCount = 1,
                    bounds = new { minimum = new[] { 0d, 0d, 0d }, maximum = new[] { 10d, 20d, 30d } },
                },
                provenance = new { generator = "primitive", kind, parameters },
            });
            return ValueTask.FromResult(new IndustrialContainerInvocation(response, output, () => CleanupAsync(job)));
        }

        if (operation == "createCatalogItem")
        {
            var root = document.RootElement;
            var step = AssemblySmoke.Step("catalog");
            File.WriteAllBytes(Path.Combine(output, "model.step"), step);
            var response = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = ProtocolV1.ResponseSchema,
                ok = true,
                operation = "createCatalogItem",
                catalogDigest = root.GetProperty("catalogDigest").GetString(),
                itemId = root.GetProperty("itemId").GetString(),
                artifact = new
                {
                    format = "step",
                    contentDigest = ProtocolV1.Sha256(step),
                    byteLength = step.Length,
                },
                measurement = new
                {
                    units = "millimeter",
                    volumeMm3 = 100d,
                    solidCount = 4,
                    bounds = new
                    {
                        minimum = new[] { 0d, 0d, 0d },
                        maximum = new[] { 10d, 20d, 30d },
                    },
                },
                provenance = new
                {
                    generator = "catalog",
                    catalogDigest = root.GetProperty("catalogDigest").GetString(),
                    itemId = root.GetProperty("itemId").GetString(),
                    parameters = root.GetProperty("parameters").Clone(),
                },
            });
            return ValueTask.FromResult(new IndustrialContainerInvocation(response, output, () => CleanupAsync(job)));
        }

        if (operation == "createPreview")
        {
            var sources = document.RootElement.GetProperty("sources").EnumerateArray()
                .Select(source =>
                {
                    var slot = source.GetProperty("inputSlot").GetString()!;
                    var input = inputs.Single(value => StringComparer.Ordinal.Equals(value.Slot, slot));
                    return new IndustrialPreviewSource(
                        source.GetProperty("sourcePartId").GetString()!,
                        slot,
                        source.GetProperty("expectedDigest").GetString()!,
                        input.Content.Length);
                }).ToArray();
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
            ValidateInputs(command, inputs);
            var glb = AssemblySmoke.Glb(command);
            File.WriteAllBytes(Path.Combine(output, "preview.glb"), glb);
            var response = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = ProtocolV1.ResponseSchema,
                ok = true,
                operation = "createPreview",
                artifact = new { format = "glb", contentDigest = ProtocolV1.Sha256(glb), byteLength = glb.Length },
                entityCount = occurrences.Length,
                sourceCount = sources.Length,
                bounds = new { minimum = new[] { -100d, -100d, -100d }, maximum = new[] { 200d, 200d, 200d } },
                units = "millimeter",
                provenance = new
                {
                    sources = sources.Select(source => new
                    {
                        sourcePartId = source.SourcePartId,
                        contentDigest = source.ExpectedDigest,
                        byteLength = source.ExpectedByteLength,
                    }).ToArray(),
                    occurrences = occurrences.Select(occurrence => new
                    {
                        entityId = occurrence.EntityId,
                        sourcePartId = occurrence.SourcePartId,
                        parentEntityId = occurrence.ParentEntityId,
                        transform = occurrence.Transform,
                    }).ToArray(),
                    tessellation = new { linearToleranceMm = 0.1, angularToleranceRad = 0.1 },
                },
            });
            return ValueTask.FromResult(new IndustrialContainerInvocation(response, output, () => CleanupAsync(job)));
        }

        throw new InvalidOperationException("unexpected_assembly_smoke_operation");
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    private static void ValidateInputs(IndustrialPreviewCommand command, IReadOnlyList<IndustrialInputArtifact> inputs)
    {
        if (inputs.Count != command.Sources.Count
            || inputs.Any(input => !ProtocolV1.FixedDigestEquals(ProtocolV1.Sha256(input.Content.Span), input.Digest))
            || command.Sources.Any(source => inputs.All(input => !StringComparer.Ordinal.Equals(input.Slot, source.InputSlot)
                || !ProtocolV1.FixedDigestEquals(input.Digest, source.ExpectedDigest)
                || input.Content.Length != source.ExpectedByteLength)))
            throw new InvalidOperationException("assembly_smoke_input_mismatch");
    }

    private static ValueTask CleanupAsync(string job)
    {
        if (Directory.Exists(job)) Directory.Delete(job, recursive: true);
        return ValueTask.CompletedTask;
    }
}
