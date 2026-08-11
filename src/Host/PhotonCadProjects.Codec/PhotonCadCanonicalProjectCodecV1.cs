namespace PhotonCadProjects.Codec;

/// <summary>
/// Deterministic, fail-closed Photon CAD project codec. V1 is an uncompressed,
/// path-free framing format; CAD execution and artifact handles never enter this assembly.
/// </summary>
public sealed class PhotonCadCanonicalProjectCodecV1 : IPhotonCadProjectCodec, IPhotonCadProjectCodecPolicy
{
    private readonly IPhotonCadProjectIdentityIssuerV1 _identities;

    public PhotonCadCanonicalProjectCodecV1(IPhotonCadProjectIdentityIssuerV1? identities = null)
    {
        _identities = identities ?? new CryptographicPhotonCadProjectIdentityIssuerV1();
    }

    public int MaximumEncodedBytes => PhotonCadProjectFileV1.MaximumEncodedBytes;

    public ValueTask<PhotonCadCanonicalProject> CreateAsync(
        string title,
        PhotonCadProjectUnit units,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var identity = _identities.NewIdentity();
        var state = new PhotonCadProjectStateV1(
            identity.SessionId,
            identity.ProjectId,
            revision: 0,
            title,
            units,
            entities: [],
            operations: [],
            occurrences: [],
            issues: [],
            bom: [],
            artifacts: [],
            dirty: false);
        return ValueTask.FromResult(Encode(state));
    }

    public PhotonCadCanonicalProject Encode(PhotonCadProjectStateV1 state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var manifest = PhotonCadManifestWriterV1.Encode(state);
        var bytes = PhotonCadProjectFramingV1.Encode(manifest);
        var logicalDigest = $"sha256:{Convert.ToHexStringLower(PhotonCadProjectFramingV1.ComputeLogicalDigest(manifest.Bytes))}";
        return ToCanonical(state, bytes, logicalDigest, state.Dirty);
    }

    public PhotonCadCanonicalProject Decode(ReadOnlyMemory<byte> canonicalBytes)
    {
        var decoded = PhotonCadProjectFramingV1.Decode(canonicalBytes);
        return ToCanonical(decoded.State, canonicalBytes, decoded.LogicalDigest, dirty: false);
    }

    public PhotonCadProjectStateV1 Inspect(PhotonCadCanonicalProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var decoded = PhotonCadProjectFramingV1.Decode(project.CanonicalBytes);
        ValidateWrapper(project, decoded);
        var state = decoded.State;
        return new PhotonCadProjectStateV1(
            state.SessionId,
            state.ProjectId,
            state.Revision,
            state.Title,
            state.Units,
            state.Entities,
            state.Operations,
            state.Occurrences,
            state.Issues,
            state.Bom,
            state.Artifacts,
            project.Dirty);
    }

    public PhotonCadCanonicalProject MarkSaved(PhotonCadCanonicalProject current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var bytes = current.CanonicalBytes;
        var decoded = PhotonCadProjectFramingV1.Decode(bytes);
        ValidateWrapper(current, decoded);
        return ToCanonical(decoded.State, bytes, decoded.LogicalDigest, dirty: false);
    }

    public string ComputeLogicalContentDigest(ReadOnlyMemory<byte> canonicalBytes) =>
        PhotonCadProjectFramingV1.Decode(canonicalBytes).LogicalDigest;

    private static PhotonCadCanonicalProject ToCanonical(
        PhotonCadProjectStateV1 state,
        ReadOnlyMemory<byte> bytes,
        string logicalDigest,
        bool dirty)
    {
        var bom = state.Bom
            .OrderBy(row => row.SourceEntityId, StringComparer.Ordinal)
            .ThenBy(row => row.PartNumber, StringComparer.Ordinal)
            .ToArray();
        return new PhotonCadCanonicalProject(
            state.SessionId,
            state.ProjectId,
            state.Revision,
            state.Title,
            state.Units,
            logicalDigest,
            PhotonCadBomCanonicalizer.Compute(state.Units, bom),
            dirty,
            bytes,
            bom);
    }

    private static void ValidateWrapper(PhotonCadCanonicalProject project, PhotonCadDecodedFileV1 decoded)
    {
        var state = decoded.State;
        if (!StringComparer.Ordinal.Equals(project.SessionId, state.SessionId)
            || !StringComparer.Ordinal.Equals(project.ProjectId, state.ProjectId)
            || project.Revision != state.Revision
            || !StringComparer.Ordinal.Equals(project.DisplayName, state.Title)
            || project.Units != state.Units
            || !PhotonCadFileGuardsV1.FixedDigestEquals(project.ContentDigest, decoded.LogicalDigest)
            || !PhotonCadFileGuardsV1.FixedDigestEquals(project.BomDigest, PhotonCadBomCanonicalizer.Compute(state.Units, state.Bom))
            || !BomEquivalent(project.Bom, state.Bom))
            throw PhotonCadFileGuardsV1.Failure("project_wrapper_binding_mismatch", "project");
    }

    private static bool BomEquivalent(IReadOnlyList<PhotonCadBomRow> left, IReadOnlyList<PhotonCadBomRow> right)
    {
        var first = left.OrderBy(row => row.SourceEntityId, StringComparer.Ordinal).ThenBy(row => row.PartNumber, StringComparer.Ordinal).ToArray();
        var second = right.OrderBy(row => row.SourceEntityId, StringComparer.Ordinal).ThenBy(row => row.PartNumber, StringComparer.Ordinal).ToArray();
        if (first.Length != second.Length) return false;
        for (var index = 0; index < first.Length; index++)
        {
            if (!StringComparer.Ordinal.Equals(first[index].SourceEntityId, second[index].SourceEntityId)
                || !StringComparer.Ordinal.Equals(first[index].PartNumber, second[index].PartNumber)
                || !StringComparer.Ordinal.Equals(first[index].Description, second[index].Description)
                || BitConverter.DoubleToInt64Bits(first[index].Quantity) != BitConverter.DoubleToInt64Bits(second[index].Quantity)
                || first[index].Unit != second[index].Unit)
                return false;
        }
        return true;
    }
}
