using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using PhotonCadProjects;
using PhotonCadProjects.Codec;
using PhotonCadProjects.RuntimeSync;

internal sealed class SequenceIdentityIssuer(IEnumerable<(string SessionId, string ProjectId)> identities)
    : IPhotonCadProjectIdentityIssuerV1
{
    private readonly Queue<(string SessionId, string ProjectId)> _identities = new(identities);

    public (string SessionId, string ProjectId) NewIdentity()
    {
        lock (_identities)
        {
            return _identities.Count > 0
                ? _identities.Dequeue()
                : throw new InvalidOperationException("No deterministic test identity remains.");
        }
    }
}

internal sealed class InMemoryCanonicalAuthority : IPhotonCadCanonicalMutationAuthority
{
    private readonly object _gate = new();
    private readonly PhotonCadCanonicalProjectCodecV1 _codec;
    private readonly Dictionary<string, Entry> _projects = new(StringComparer.Ordinal);

    internal InMemoryCanonicalAuthority(
        PhotonCadCanonicalProjectCodecV1 codec,
        IEnumerable<PhotonCadCanonicalProject> projects)
    {
        _codec = codec;
        foreach (var project in projects)
        {
            _projects.Add(Key(project.SessionId, project.ProjectId), new Entry(
                new PhotonCadProjectHandle($"cad-project:{_projects.Count.ToString("D32", System.Globalization.CultureInfo.InvariantCulture)}"),
                project));
        }
    }

    internal bool FailNextCommit { get; set; }
    internal bool ReturnDirtyAfterCommit { get; set; }
    internal int ResolveCount { get; private set; }
    internal int CommitCount { get; private set; }

    public ValueTask<PhotonCadCanonicalMutationBinding> ResolveAsync(
        PhotonCadRuntimeSyncRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ResolveCount++;
            if (!_projects.TryGetValue(Key(request.SessionId, request.ProjectId), out var entry)
                || entry.Project.Revision != request.BaseRevision
                || entry.Project.Dirty)
                throw new PhotonCadRuntimeSyncException("stale_revision", nameof(request));
            return ValueTask.FromResult(new PhotonCadCanonicalMutationBinding(entry.Handle, entry.Project));
        }
    }

    public ValueTask<PhotonCadCanonicalProject> CommitAsync(
        PhotonCadCanonicalMutationBinding expected,
        PhotonCadCanonicalProject updated,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (FailNextCommit)
            {
                FailNextCommit = false;
                throw new PhotonCadRuntimeSyncException("injected_commit_failure", nameof(updated));
            }
            var key = Key(expected.Current.SessionId, expected.Current.ProjectId);
            if (!_projects.TryGetValue(key, out var entry)
                || !entry.Handle.Equals(expected.ProjectHandle)
                || entry.Project.Revision != expected.Current.Revision
                || !DigestEquals(entry.Project.ContentDigest, expected.Current.ContentDigest))
                throw new PhotonCadRuntimeSyncException("stale_commit", nameof(expected));
            var saved = _codec.MarkSaved(updated);
            entry.Project = saved;
            CommitCount++;
            return ValueTask.FromResult(ReturnDirtyAfterCommit ? updated : saved);
        }
    }

    internal PhotonCadCanonicalProject Get(string sessionId, string projectId)
    {
        lock (_gate) return _projects[Key(sessionId, projectId)].Project;
    }

    private static string Key(string sessionId, string projectId) => $"{sessionId}\0{projectId}";

    private static bool DigestEquals(string left, string right)
    {
        var first = Encoding.ASCII.GetBytes(left);
        var second = Encoding.ASCII.GetBytes(right);
        return first.Length == second.Length && CryptographicOperations.FixedTimeEquals(first, second);
    }

    private sealed class Entry(PhotonCadProjectHandle handle, PhotonCadCanonicalProject project)
    {
        internal PhotonCadProjectHandle Handle { get; } = handle;
        internal PhotonCadCanonicalProject Project { get; set; } = project;
    }
}

internal sealed class DelegateMutationProvider(
    Func<PhotonCadSealedMutationProviderRequest, CancellationToken, ValueTask<PhotonCadSealedMutationDelta>> apply)
    : IPhotonCadSealedMutationProvider
{
    private readonly Func<PhotonCadSealedMutationProviderRequest, CancellationToken, ValueTask<PhotonCadSealedMutationDelta>> _apply = apply;
    internal int Calls => _calls;
    private int _calls;

    public ValueTask<PhotonCadSealedMutationDelta> ApplyAsync(
        PhotonCadSealedMutationProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        return _apply(request, cancellationToken);
    }
}

internal sealed class RecordingCompensator : IPhotonCadSealedMutationCompensator
{
    private readonly ConcurrentQueue<(string MutationId, string Reason)> _calls = new();
    internal int Count => _calls.Count;
    internal IReadOnlyList<(string MutationId, string Reason)> Calls => _calls.ToArray();
    internal bool Fail { get; set; }

    public ValueTask CompensateAsync(
        PhotonCadSealedMutationDelta mutation,
        string reason,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _calls.Enqueue((mutation.MutationId, reason));
        if (Fail) throw new InvalidOperationException("Injected compensation failure.");
        return ValueTask.CompletedTask;
    }
}

internal static class SmokeFactory
{
    internal static string Digest(char value) => $"sha256:{new string(value, 64)}";

    internal static PhotonCadProviderEvidence Evidence(PhotonCadBackendV1 backend = PhotonCadBackendV1.Geometry)
    {
        var image = Digest('d');
        var receipt = Digest('e');
        return new PhotonCadProviderEvidence(
            backend,
            backend == PhotonCadBackendV1.Geometry ? "build123d.mcp" : "partcad.lsp",
            backend == PhotonCadBackendV1.Geometry ? ["mcp.initialize", "mcp.tools.call"] : ["lsp.initialize", "partcad.context.create"],
            "catalog.1",
            backend == PhotonCadBackendV1.Geometry ? "photon.cad.geometry.container.v1" : "photon.cad.industrial.container.v1",
            receipt,
            receipt,
            image,
            Digest('b'),
            new PhotonCadSourceIdentityV1(
                backend == PhotonCadBackendV1.Geometry ? "build123d-mcp" : "partcad",
                backend == PhotonCadBackendV1.Geometry ? "0.3.80" : "0.7.158",
                image,
                "Apache-2.0"));
    }

    internal static byte[] Step(string marker = "part") => Encoding.ASCII.GetBytes(
        $"ISO-10303-21;\nHEADER;\nFILE_DESCRIPTION(('{marker}'),'2;1');\nENDSEC;\nDATA;\nENDSEC;\nEND-ISO-10303-21;\n");

    internal static PhotonCadRuntimeSyncRequest Request(
        PhotonCadCanonicalProject project,
        string requestId,
        string capabilityId = "geometry.create.box",
        IEnumerable<PhotonCadSyncOperationInput>? inputs = null,
        IEnumerable<string>? targets = null) => new(
            requestId,
            project.SessionId,
            project.ProjectId,
            project.Revision,
            capabilityId,
            PhotonCadOperationModeV1.Suggest,
            inputs,
            targets);

    internal static PhotonCadSealedMutationDelta CreateBodyMutation(
        PhotonCadSealedMutationProviderRequest providerRequest,
        string entityId,
        bool includeExportOperation,
        string? requestIdOverride = null,
        byte[]? step = null,
        string? replacesDigest = null,
        bool includeEntity = true,
        IEnumerable<PhotonCadBomRow>? bom = null,
        PhotonCadCollectionMergeMode bomMode = PhotonCadCollectionMergeMode.Append)
    {
        var request = providerRequest.Request;
        var evidence = Evidence(includeExportOperation ? PhotonCadBackendV1.Geometry : PhotonCadBackendV1.Assembly);
        var firstRevision = checked(request.BaseRevision + 1);
        var create = new PhotonCadAppliedOperationDelta(
            firstRevision,
            $"op-{request.RequestId}-create",
            request.CapabilityId,
            "Create body",
            DateTimeOffset.Parse("2026-08-10T12:00:00Z"),
            request.Mode,
            request.Inputs,
            request.TargetEntityIds,
            evidence);
        var operations = new List<PhotonCadAppliedOperationDelta> { create };
        var artifactOperation = create;
        if (includeExportOperation)
        {
            artifactOperation = new PhotonCadAppliedOperationDelta(
                checked(firstRevision + 1),
                $"op-{request.RequestId}-export",
                "geometry.export.step",
                "Seal STEP",
                DateTimeOffset.Parse("2026-08-10T12:00:01Z"),
                request.Mode,
                [],
                [entityId],
                evidence);
            operations.Add(artifactOperation);
        }

        var content = step ?? Step(entityId);
        var artifact = new PhotonCadSealedArtifactDelta(
            PhotonCadArtifactRoleV1.AuthoritativeGeometry,
            PhotonCadArtifactKindV1.Step,
            entityId,
            artifactOperation.AppliedRevision,
            content,
            content.LongLength,
            $"sha256:{Convert.ToHexStringLower(SHA256.HashData(content))}",
            "model/step",
            null,
            artifactOperation.Id,
            evidence,
            replacesDigest);
        return new PhotonCadSealedMutationDelta(
            $"mutation-{request.RequestId}",
            requestIdOverride ?? request.RequestId,
            request.SessionId,
            request.ProjectId,
            request.BaseRevision,
            artifactOperation.AppliedRevision,
            operations,
            includeEntity
                ? [new PhotonCadEntityV1(entityId, null, PhotonCadEntityKindV1.Body, entityId, true, false, request.CapabilityId)]
                : [],
            artifacts: [artifact],
            bom: bom,
            bomMergeMode: bomMode);
    }

    internal static PhotonCadRuntimeProjectSynchronizer Synchronizer(
        PhotonCadCanonicalProjectCodecV1 codec,
        InMemoryCanonicalAuthority authority,
        DelegateMutationProvider provider,
        RecordingCompensator compensator,
        PhotonCadRuntimeSyncPolicy? policy = null)
    {
        var effective = policy ?? new PhotonCadRuntimeSyncPolicy();
        return new PhotonCadRuntimeProjectSynchronizer(
            authority,
            provider,
            compensator,
            new PhotonCadRuntimeCanonicalMapperV1(codec, effective));
    }

    internal static async Task<(PhotonCadCanonicalProjectCodecV1 Codec, PhotonCadCanonicalProject Project)> ProjectAsync(
        string suffix = "one")
    {
        var codec = new PhotonCadCanonicalProjectCodecV1(new SequenceIdentityIssuer(
            [("pcsid:" + suffix, "pcpid:" + suffix)]));
        return (codec, await codec.CreateAsync("Photon test", PhotonCadProjectUnit.Millimeter));
    }
}
