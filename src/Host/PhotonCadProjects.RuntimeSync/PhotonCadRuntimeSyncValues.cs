using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PhotonCadProjects.Codec;

namespace PhotonCadProjects.RuntimeSync;

public sealed class PhotonCadSyncVector3
{
    public PhotonCadSyncVector3(double x, double y, double z)
    {
        X = RuntimeSyncGuards.Finite(x, nameof(x));
        Y = RuntimeSyncGuards.Finite(y, nameof(y));
        Z = RuntimeSyncGuards.Finite(z, nameof(z));
    }

    public double X { get; }
    public double Y { get; }
    public double Z { get; }
}

public sealed class PhotonCadSyncInputValue
{
    private readonly object? _value;

    private PhotonCadSyncInputValue(PhotonCadInputKindV1 kind, object? value)
    {
        Kind = RuntimeSyncGuards.EnumValue(kind, nameof(kind));
        _value = value;
    }

    public PhotonCadInputKindV1 Kind { get; }

    public static PhotonCadSyncInputValue Null(PhotonCadInputKindV1 kind) => new(kind, null);
    public static PhotonCadSyncInputValue Number(double value) => new(PhotonCadInputKindV1.Number, RuntimeSyncGuards.Finite(value, nameof(value)));

    public static PhotonCadSyncInputValue Integer(long value)
    {
        if (value < -PhotonCadProjectContract.MaximumSafeInteger || value > PhotonCadProjectContract.MaximumSafeInteger)
            throw RuntimeSyncGuards.Failure("invalid_safe_integer", nameof(value));
        return new PhotonCadSyncInputValue(PhotonCadInputKindV1.Integer, value);
    }

    public static PhotonCadSyncInputValue Boolean(bool value) => new(PhotonCadInputKindV1.Boolean, value);
    public static PhotonCadSyncInputValue Text(string value) => new(PhotonCadInputKindV1.Text, RuntimeSyncGuards.Text(value, nameof(value), 4_096, false));
    public static PhotonCadSyncInputValue Choice(string value) => new(PhotonCadInputKindV1.Choice, RuntimeSyncGuards.Identifier(value, nameof(value)));
    public static PhotonCadSyncInputValue Entity(string value) => new(PhotonCadInputKindV1.Entity, RuntimeSyncGuards.Identifier(value, nameof(value)));
    public static PhotonCadSyncInputValue Vector3(PhotonCadSyncVector3 value) => new(PhotonCadInputKindV1.Vector3, value ?? throw RuntimeSyncGuards.Failure("required", nameof(value)));

    public static PhotonCadSyncInputValue EntityList(IEnumerable<string> values)
    {
        var copy = RuntimeSyncGuards.Copy(values, nameof(values), 1_000)
            .Select(value => RuntimeSyncGuards.Identifier(value, nameof(values)))
            .ToArray();
        RuntimeSyncGuards.RequireUnique(copy, nameof(values));
        return new PhotonCadSyncInputValue(PhotonCadInputKindV1.EntityList, Array.AsReadOnly(copy));
    }

    internal PhotonCadInputValueV1 ToCanonical() => Kind switch
    {
        PhotonCadInputKindV1.Number when _value is double number => PhotonCadInputValueV1.Number(number),
        PhotonCadInputKindV1.Integer when _value is long integer => PhotonCadInputValueV1.Integer(integer),
        PhotonCadInputKindV1.Boolean when _value is bool boolean => PhotonCadInputValueV1.Boolean(boolean),
        PhotonCadInputKindV1.Text when _value is string text => PhotonCadInputValueV1.Text(text),
        PhotonCadInputKindV1.Choice when _value is string choice => PhotonCadInputValueV1.Choice(choice),
        PhotonCadInputKindV1.Entity when _value is string entity => PhotonCadInputValueV1.Entity(entity),
        PhotonCadInputKindV1.Vector3 when _value is PhotonCadSyncVector3 vector => PhotonCadInputValueV1.Vector3(new PhotonCadVector3V1(vector.X, vector.Y, vector.Z)),
        PhotonCadInputKindV1.EntityList when _value is IReadOnlyList<string> entities => PhotonCadInputValueV1.EntityList(entities),
        _ when _value is null => PhotonCadInputValueV1.Null(Kind),
        _ => throw RuntimeSyncGuards.Failure("input_value_kind_mismatch", nameof(Kind)),
    };

    internal static PhotonCadSyncInputValue FromCanonical(PhotonCadInputValueV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Kind switch
        {
            PhotonCadInputKindV1.Number when value.Value is double number => Number(number),
            PhotonCadInputKindV1.Integer when value.Value is long integer => Integer(integer),
            PhotonCadInputKindV1.Boolean when value.Value is bool boolean => Boolean(boolean),
            PhotonCadInputKindV1.Text when value.Value is string text => Text(text),
            PhotonCadInputKindV1.Choice when value.Value is string choice => Choice(choice),
            PhotonCadInputKindV1.Entity when value.Value is string entity => Entity(entity),
            PhotonCadInputKindV1.Vector3 when value.Value is PhotonCadVector3V1 vector =>
                Vector3(new PhotonCadSyncVector3(vector.X, vector.Y, vector.Z)),
            PhotonCadInputKindV1.EntityList when value.Value is IReadOnlyList<string> entities => EntityList(entities),
            _ when value.Value is null => Null(value.Kind),
            _ => throw RuntimeSyncGuards.Failure("input_value_kind_mismatch", nameof(value)),
        };
    }

    public bool TryGetNumber(out double value) => TryGet(_value, out value);
    public bool TryGetInteger(out long value) => TryGet(_value, out value);
    public bool TryGetBoolean(out bool value) => TryGet(_value, out value);
    public bool TryGetText(out string value)
    {
        value = _value as string ?? string.Empty;
        return _value is string;
    }

    public bool TryGetVector3(out PhotonCadSyncVector3? value)
    {
        value = _value as PhotonCadSyncVector3;
        return value is not null;
    }

    public bool TryGetEntityList(out IReadOnlyList<string>? value)
    {
        value = _value as IReadOnlyList<string>;
        return value is not null;
    }

    private static bool TryGet<T>(object? candidate, out T value) where T : struct
    {
        if (candidate is T typed)
        {
            value = typed;
            return true;
        }
        value = default;
        return false;
    }

    internal bool Equivalent(PhotonCadSyncInputValue other)
    {
        if (other is null || Kind != other.Kind) return false;
        if (_value is double leftNumber && other._value is double rightNumber)
            return BitConverter.DoubleToInt64Bits(leftNumber) == BitConverter.DoubleToInt64Bits(rightNumber);
        if (_value is PhotonCadSyncVector3 leftVector && other._value is PhotonCadSyncVector3 rightVector)
            return BitConverter.DoubleToInt64Bits(leftVector.X) == BitConverter.DoubleToInt64Bits(rightVector.X)
                && BitConverter.DoubleToInt64Bits(leftVector.Y) == BitConverter.DoubleToInt64Bits(rightVector.Y)
                && BitConverter.DoubleToInt64Bits(leftVector.Z) == BitConverter.DoubleToInt64Bits(rightVector.Z);
        if (_value is IReadOnlyList<string> leftList && other._value is IReadOnlyList<string> rightList)
            return leftList.SequenceEqual(rightList, StringComparer.Ordinal);
        return Equals(_value, other._value);
    }

    internal IEnumerable<string> ReferencedEntityIds() => Kind switch
    {
        PhotonCadInputKindV1.Entity when _value is string entity => [entity],
        PhotonCadInputKindV1.EntityList when _value is IReadOnlyList<string> entities => entities,
        _ => [],
    };
}

public sealed class PhotonCadSyncOperationInput
{
    public PhotonCadSyncOperationInput(string id, PhotonCadSyncInputValue value)
    {
        Id = RuntimeSyncGuards.Identifier(id, nameof(id));
        Value = value ?? throw RuntimeSyncGuards.Failure("required", nameof(value));
    }

    public string Id { get; }
    public PhotonCadSyncInputValue Value { get; }
}

/// <summary>Immutable, value-only execution and supply-chain evidence. Paths are not representable.</summary>
public sealed class PhotonCadProviderEvidence
{
    public PhotonCadProviderEvidence(
        PhotonCadBackendV1 backend,
        string providerId,
        IEnumerable<string> protocolIds,
        string catalogRevision,
        string bundleId,
        string bundleManifestSha256,
        string immutableReceiptSha256,
        string derivedImageId,
        string baseImageId,
        PhotonCadSourceIdentityV1 source)
    {
        Backend = RuntimeSyncGuards.EnumValue(backend, nameof(backend));
        ProviderId = RuntimeSyncGuards.Token(providerId, nameof(providerId));
        ProtocolIds = Array.AsReadOnly(RuntimeSyncGuards.Copy(
                protocolIds,
                nameof(protocolIds),
                PhotonCadRuntimeSyncContract.MaximumProtocolIds,
                requireAny: true)
            .Select(value => RuntimeSyncGuards.Token(value, nameof(protocolIds)))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray());
        RuntimeSyncGuards.RequireUnique(ProtocolIds, nameof(protocolIds));
        CatalogRevision = RuntimeSyncGuards.Token(catalogRevision, nameof(catalogRevision));
        BundleId = RuntimeSyncGuards.Identifier(bundleId, nameof(bundleId));
        BundleManifestSha256 = RuntimeSyncGuards.Digest(bundleManifestSha256, nameof(bundleManifestSha256));
        ImmutableReceiptSha256 = RuntimeSyncGuards.Digest(immutableReceiptSha256, nameof(immutableReceiptSha256));
        DerivedImageId = RuntimeSyncGuards.Digest(derivedImageId, nameof(derivedImageId));
        BaseImageId = RuntimeSyncGuards.Digest(baseImageId, nameof(baseImageId));
        Source = source ?? throw RuntimeSyncGuards.Failure("required", nameof(source));
        // V1 canonical provenance has one durable bundle commitment and one durable source
        // commitment. Require those fields to commit the exact receipt and derived image rather
        // than silently losing distinct transient evidence on reopen. The receipt transitively
        // binds base image, protocol/package hashes, and policy in the verified runtime layer.
        if (!RuntimeSyncGuards.FixedDigestEquals(BundleManifestSha256, ImmutableReceiptSha256))
            throw RuntimeSyncGuards.Failure("receipt_commitment_not_persistable", nameof(immutableReceiptSha256));
        if (!RuntimeSyncGuards.FixedDigestEquals(Source.Digest, DerivedImageId))
            throw RuntimeSyncGuards.Failure("image_commitment_not_persistable", nameof(derivedImageId));
    }

    public PhotonCadBackendV1 Backend { get; }
    public string ProviderId { get; }
    public IReadOnlyList<string> ProtocolIds { get; }
    public string CatalogRevision { get; }
    public string BundleId { get; }
    public string BundleManifestSha256 { get; }
    public string ImmutableReceiptSha256 { get; }
    public string DerivedImageId { get; }
    public string BaseImageId { get; }
    public PhotonCadSourceIdentityV1 Source { get; }

    internal bool Equivalent(PhotonCadProviderEvidence other) => other is not null
        && Backend == other.Backend
        && StringComparer.Ordinal.Equals(ProviderId, other.ProviderId)
        && ProtocolIds.SequenceEqual(other.ProtocolIds, StringComparer.Ordinal)
        && StringComparer.Ordinal.Equals(CatalogRevision, other.CatalogRevision)
        && StringComparer.Ordinal.Equals(BundleId, other.BundleId)
        && RuntimeSyncGuards.FixedDigestEquals(BundleManifestSha256, other.BundleManifestSha256)
        && RuntimeSyncGuards.FixedDigestEquals(ImmutableReceiptSha256, other.ImmutableReceiptSha256)
        && RuntimeSyncGuards.FixedDigestEquals(DerivedImageId, other.DerivedImageId)
        && RuntimeSyncGuards.FixedDigestEquals(BaseImageId, other.BaseImageId)
        && StringComparer.Ordinal.Equals(Source.Package, other.Source.Package)
        && StringComparer.Ordinal.Equals(Source.Version, other.Source.Version)
        && RuntimeSyncGuards.FixedDigestEquals(Source.Digest, other.Source.Digest)
        && StringComparer.Ordinal.Equals(Source.License, other.Source.License);
}

/// <summary>
/// Immutable pathless view of a trusted canonical artifact supplied to a provider. Construction is
/// synchronizer-owned; each content access returns a copy and no storage/runtime handle survives.
/// </summary>
public sealed class PhotonCadProviderBaseArtifact
{
    private readonly byte[] _content;

    internal PhotonCadProviderBaseArtifact(PhotonCadArtifactV1 artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        Role = artifact.Role;
        Kind = artifact.Kind;
        OwnerEntityId = artifact.OwnerEntityId;
        Revision = artifact.Revision;
        _content = artifact.Content.ToArray();
        if (_content.Length <= 0 || _content.Length > PhotonCadRuntimeSyncContract.MaximumBaseArtifactBytes)
            throw RuntimeSyncGuards.Failure("base_artifact_size_rejected", nameof(artifact));
        ByteLength = _content.LongLength;
        ContentDigest = RuntimeSyncGuards.Sha256(_content);
        if (!RuntimeSyncGuards.FixedDigestEquals(ContentDigest, artifact.Digest))
            throw RuntimeSyncGuards.Failure("base_artifact_digest_mismatch", nameof(artifact));
        MediaType = Kind switch
        {
            PhotonCadArtifactKindV1.Step => "model/step",
            PhotonCadArtifactKindV1.Glb => "model/gltf-binary",
            _ => throw RuntimeSyncGuards.Failure("unsupported_artifact_kind", nameof(artifact)),
        };
        Bounds = artifact.Bounds;
        Provenance = new PhotonCadProviderBaseProvenance(artifact.Provenance);
    }

    public PhotonCadArtifactRoleV1 Role { get; }
    public PhotonCadArtifactKindV1 Kind { get; }
    public string? OwnerEntityId { get; }
    public long Revision { get; }
    public long ByteLength { get; }
    public string ContentDigest { get; }
    public string MediaType { get; }
    public PhotonCadBoundsV1? Bounds { get; }
    public PhotonCadProviderBaseProvenance Provenance { get; }
    public ReadOnlyMemory<byte> Content => _content.ToArray();
}

public sealed class PhotonCadProviderBaseEntity
{
    internal PhotonCadProviderBaseEntity(PhotonCadEntityV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Id = value.Id;
        ParentId = value.ParentId;
        Kind = value.Kind;
        Name = value.Name;
        Visible = value.Visible;
        Suppressed = value.Suppressed;
        SourceCapabilityId = value.SourceCapabilityId;
    }

    public string Id { get; }
    public string? ParentId { get; }
    public PhotonCadEntityKindV1 Kind { get; }
    public string Name { get; }
    public bool Visible { get; }
    public bool Suppressed { get; }
    public string? SourceCapabilityId { get; }
}

/// <summary>
/// Immutable, pathless copy of one trusted canonical operation. Providers may use this history to
/// replay an explicitly selected feature, but cannot mutate it or recover host/storage handles.
/// </summary>
public sealed class PhotonCadProviderBaseOperation
{
    internal PhotonCadProviderBaseOperation(PhotonCadOperationV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Id = value.Id;
        CapabilityId = value.CapabilityId;
        Label = value.Label;
        Mode = value.Mode;
        Inputs = Array.AsReadOnly(value.Inputs
            .Select(input => new PhotonCadSyncOperationInput(input.Id, PhotonCadSyncInputValue.FromCanonical(input.Value)))
            .ToArray());
        TargetEntityIds = Array.AsReadOnly(value.TargetEntityIds.ToArray());
    }

    public string Id { get; }
    public string CapabilityId { get; }
    public string Label { get; }
    public PhotonCadOperationModeV1 Mode { get; }
    public IReadOnlyList<PhotonCadSyncOperationInput> Inputs { get; }
    public IReadOnlyList<string> TargetEntityIds { get; }
}

public sealed class PhotonCadProviderBaseBomRow
{
    internal PhotonCadProviderBaseBomRow(PhotonCadBomRow value)
    {
        ArgumentNullException.ThrowIfNull(value);
        PartNumber = value.PartNumber;
        Description = value.Description;
        Quantity = value.Quantity;
        Unit = value.Unit;
        SourceEntityId = value.SourceEntityId;
    }

    public string PartNumber { get; }
    public string Description { get; }
    public double Quantity { get; }
    public PhotonCadBomUnit Unit { get; }
    public string SourceEntityId { get; }
}

public sealed class PhotonCadProviderBaseProvenance
{
    internal PhotonCadProviderBaseProvenance(PhotonCadArtifactProvenanceV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Backend = value.Backend;
        BundleId = value.BundleId;
        BundleManifestSha256 = value.BundleManifestSha256;
        CapabilityId = value.CapabilityId;
        OperationId = value.OperationId;
        Source = new PhotonCadSourceIdentityV1(
            value.Source.Package,
            value.Source.Version,
            value.Source.Digest,
            value.Source.License);
    }

    public PhotonCadBackendV1 Backend { get; }
    public string BundleId { get; }
    public string BundleManifestSha256 { get; }
    public string CapabilityId { get; }
    public string OperationId { get; }
    public PhotonCadSourceIdentityV1 Source { get; }
}

public sealed class PhotonCadProviderBaseOccurrence
{
    private readonly double[] _transform;

    internal PhotonCadProviderBaseOccurrence(PhotonCadOccurrenceV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        OccurrenceId = value.OccurrenceId;
        ParentOccurrenceId = value.ParentOccurrenceId;
        PartNumber = value.PartNumber;
        SourceEntityId = value.SourceEntityId;
        _transform = value.Transform.ToArray();
    }

    public string OccurrenceId { get; }
    public string? ParentOccurrenceId { get; }
    public string PartNumber { get; }
    public string SourceEntityId { get; }
    public IReadOnlyList<double> Transform => Array.AsReadOnly((double[])_transform.Clone());
}

public sealed class PhotonCadAppliedOperationDelta
{
    public PhotonCadAppliedOperationDelta(
        long appliedRevision,
        string id,
        string capabilityId,
        string label,
        DateTimeOffset createdAtUtc,
        PhotonCadOperationModeV1 mode,
        IEnumerable<PhotonCadSyncOperationInput>? inputs,
        IEnumerable<string>? targetEntityIds,
        PhotonCadProviderEvidence evidence)
    {
        AppliedRevision = RuntimeSyncGuards.Revision(appliedRevision, nameof(appliedRevision));
        Id = RuntimeSyncGuards.Identifier(id, nameof(id));
        CapabilityId = RuntimeSyncGuards.Identifier(capabilityId, nameof(capabilityId));
        Label = RuntimeSyncGuards.Text(label, nameof(label), 256, true);
        CreatedAtUtc = RuntimeSyncGuards.Utc(createdAtUtc, nameof(createdAtUtc));
        Mode = RuntimeSyncGuards.EnumValue(mode, nameof(mode));
        Inputs = RuntimeSyncGuards.Copy(inputs ?? [], nameof(inputs), PhotonCadProjectFileV1.MaximumInputsPerOperation);
        RuntimeSyncGuards.RequireUnique(Inputs.Select(input => input.Id), nameof(inputs));
        TargetEntityIds = Array.AsReadOnly(RuntimeSyncGuards.Copy(
                targetEntityIds ?? [],
                nameof(targetEntityIds),
                PhotonCadProjectFileV1.MaximumTargetsPerOperation)
            .Select(value => RuntimeSyncGuards.Identifier(value, nameof(targetEntityIds)))
            .ToArray());
        RuntimeSyncGuards.RequireUnique(TargetEntityIds, nameof(targetEntityIds));
        Evidence = evidence ?? throw RuntimeSyncGuards.Failure("required", nameof(evidence));
    }

    public long AppliedRevision { get; }
    public string Id { get; }
    public string CapabilityId { get; }
    public string Label { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public PhotonCadOperationModeV1 Mode { get; }
    public IReadOnlyList<PhotonCadSyncOperationInput> Inputs { get; }
    public IReadOnlyList<string> TargetEntityIds { get; }
    public PhotonCadProviderEvidence Evidence { get; }
}

/// <summary>
/// Immutable artifact delta. Construction copies the exact bytes and checks the provider's claimed
/// length and SHA-256. No path, stream, lease, or provider handle is retained.
/// </summary>
public sealed class PhotonCadSealedArtifactDelta
{
    private readonly byte[] _content;

    public PhotonCadSealedArtifactDelta(
        PhotonCadArtifactRoleV1 role,
        PhotonCadArtifactKindV1 kind,
        string? ownerEntityId,
        long revision,
        ReadOnlyMemory<byte> content,
        long claimedByteLength,
        string claimedContentDigest,
        string mediaType,
        PhotonCadBoundsV1? bounds,
        string operationId,
        PhotonCadProviderEvidence evidence,
        string? replacesContentDigest = null)
    {
        Role = RuntimeSyncGuards.EnumValue(role, nameof(role));
        Kind = RuntimeSyncGuards.EnumValue(kind, nameof(kind));
        OwnerEntityId = ownerEntityId is null ? null : RuntimeSyncGuards.Identifier(ownerEntityId, nameof(ownerEntityId));
        Revision = RuntimeSyncGuards.Revision(revision, nameof(revision), minimum: 1);
        if (content.Length <= 0 || content.Length > PhotonCadRuntimeSyncContract.MaximumSealedArtifactBytes)
            throw RuntimeSyncGuards.Failure("sealed_artifact_size_rejected", nameof(content));
        if (claimedByteLength != content.Length)
            throw RuntimeSyncGuards.Failure("sealed_artifact_length_mismatch", nameof(claimedByteLength));
        _content = content.ToArray();
        ByteLength = _content.LongLength;
        ContentDigest = RuntimeSyncGuards.Sha256(_content);
        if (!RuntimeSyncGuards.FixedDigestEquals(ContentDigest, claimedContentDigest))
            throw RuntimeSyncGuards.Failure("sealed_artifact_digest_mismatch", nameof(claimedContentDigest));
        MediaType = RuntimeSyncGuards.Token(mediaType, nameof(mediaType), 128);
        if (Kind == PhotonCadArtifactKindV1.Step && !StringComparer.Ordinal.Equals(MediaType, "model/step"))
            throw RuntimeSyncGuards.Failure("step_media_type_required", nameof(mediaType));
        if (Kind == PhotonCadArtifactKindV1.Glb && !StringComparer.Ordinal.Equals(MediaType, "model/gltf-binary"))
            throw RuntimeSyncGuards.Failure("glb_media_type_required", nameof(mediaType));
        Bounds = bounds;
        OperationId = RuntimeSyncGuards.Identifier(operationId, nameof(operationId));
        Evidence = evidence ?? throw RuntimeSyncGuards.Failure("required", nameof(evidence));
        ReplacesContentDigest = replacesContentDigest is null
            ? null
            : RuntimeSyncGuards.Digest(replacesContentDigest, nameof(replacesContentDigest));
    }

    public PhotonCadArtifactRoleV1 Role { get; }
    public PhotonCadArtifactKindV1 Kind { get; }
    public string? OwnerEntityId { get; }
    public long Revision { get; }
    public long ByteLength { get; }
    public string ContentDigest { get; }
    public string MediaType { get; }
    public PhotonCadBoundsV1? Bounds { get; }
    public string OperationId { get; }
    public PhotonCadProviderEvidence Evidence { get; }
    /// <summary>
    /// Exact canonical digest expected when replacing a prior preview or owner geometry artifact.
    /// It must be null for a new artifact and is validated by the synchronizer as an optimistic CAS.
    /// </summary>
    public string? ReplacesContentDigest { get; }
    public ReadOnlyMemory<byte> Content => _content.ToArray();
    internal ReadOnlyMemory<byte> ContentUnsafe => _content;
}

/// <summary>
/// Provider-produced operation/entity suffix plus explicitly merged collection deltas. Operations
/// and entities are always new-only. Occurrences, issues, and BOM declare Append or ReplaceAll;
/// project preview and owner geometry artifacts use digest-bound compare-and-swap replacement.
/// The ordered operation suffix is generic: a primitive
/// adapter may truthfully return create+STEP-export (base+2), while an industrial adapter may
/// return one create operation that already owns sealed STEP bytes (base+1). RuntimeSync validates
/// the explicit contiguous suffix and never assumes either provider's internal revision behavior.
/// </summary>
public sealed class PhotonCadSealedMutationDelta
{
    public PhotonCadSealedMutationDelta(
        string mutationId,
        string requestId,
        string sessionId,
        string projectId,
        long baseRevision,
        long resultingRevision,
        IEnumerable<PhotonCadAppliedOperationDelta> operations,
        IEnumerable<PhotonCadEntityV1>? entities = null,
        IEnumerable<PhotonCadOccurrenceV1>? occurrences = null,
        IEnumerable<PhotonCadIssueV1>? issues = null,
        IEnumerable<PhotonCadBomRow>? bom = null,
        IEnumerable<PhotonCadSealedArtifactDelta>? artifacts = null,
        PhotonCadCollectionMergeMode occurrenceMergeMode = PhotonCadCollectionMergeMode.Append,
        PhotonCadCollectionMergeMode issueMergeMode = PhotonCadCollectionMergeMode.Append,
        PhotonCadCollectionMergeMode bomMergeMode = PhotonCadCollectionMergeMode.Append)
    {
        MutationId = RuntimeSyncGuards.Identifier(mutationId, nameof(mutationId));
        RequestId = RuntimeSyncGuards.Identifier(requestId, nameof(requestId));
        SessionId = RuntimeSyncGuards.Identifier(sessionId, nameof(sessionId));
        ProjectId = RuntimeSyncGuards.Identifier(projectId, nameof(projectId));
        BaseRevision = RuntimeSyncGuards.Revision(baseRevision, nameof(baseRevision));
        ResultingRevision = RuntimeSyncGuards.Revision(resultingRevision, nameof(resultingRevision));
        Operations = RuntimeSyncGuards.Copy(
            operations,
            nameof(operations),
            PhotonCadRuntimeSyncContract.MaximumOperationsPerMutation,
            requireAny: true);
        RuntimeSyncGuards.RequireUnique(Operations.Select(operation => operation.Id), nameof(operations));
        if (ResultingRevision <= BaseRevision || ResultingRevision - BaseRevision != Operations.Count)
            throw RuntimeSyncGuards.Failure("operation_suffix_revision_mismatch", nameof(resultingRevision));
        for (var index = 0; index < Operations.Count; index++)
        {
            if (Operations[index].AppliedRevision != checked(BaseRevision + index + 1))
                throw RuntimeSyncGuards.Failure("operation_suffix_not_contiguous", nameof(operations));
        }

        Entities = RuntimeSyncGuards.Copy(entities ?? [], nameof(entities), PhotonCadRuntimeSyncContract.MaximumEntitiesPerMutation);
        RuntimeSyncGuards.RequireUnique(Entities.Select(entity => entity.Id), nameof(entities));
        Occurrences = RuntimeSyncGuards.Copy(occurrences ?? [], nameof(occurrences), PhotonCadRuntimeSyncContract.MaximumOccurrencesPerMutation);
        RuntimeSyncGuards.RequireUnique(Occurrences.Select(occurrence => occurrence.OccurrenceId), nameof(occurrences));
        Issues = RuntimeSyncGuards.Copy(issues ?? [], nameof(issues), PhotonCadRuntimeSyncContract.MaximumIssuesPerMutation);
        Bom = RuntimeSyncGuards.Copy(bom ?? [], nameof(bom), PhotonCadRuntimeSyncContract.MaximumBomRowsPerMutation);
        RuntimeSyncGuards.RequireUnique(Bom.Select(row => $"{row.SourceEntityId}\0{row.PartNumber}"), nameof(bom));
        Artifacts = RuntimeSyncGuards.Copy(artifacts ?? [], nameof(artifacts), PhotonCadRuntimeSyncContract.MaximumArtifactsPerMutation);
        OccurrenceMergeMode = RuntimeSyncGuards.EnumValue(occurrenceMergeMode, nameof(occurrenceMergeMode));
        IssueMergeMode = RuntimeSyncGuards.EnumValue(issueMergeMode, nameof(issueMergeMode));
        BomMergeMode = RuntimeSyncGuards.EnumValue(bomMergeMode, nameof(bomMergeMode));
        long total = 0;
        try
        {
            foreach (var artifact in Artifacts) total = checked(total + artifact.ByteLength);
        }
        catch (OverflowException exception)
        {
            throw new PhotonCadRuntimeSyncException("sealed_artifact_budget_overflow", nameof(artifacts), exception);
        }
        if (total > PhotonCadRuntimeSyncContract.MaximumSealedArtifactBytesPerMutation)
            throw RuntimeSyncGuards.Failure("sealed_artifact_budget_rejected", nameof(artifacts));
        TotalArtifactBytes = total;
    }

    public int ContractVersion => PhotonCadRuntimeSyncContract.Version;
    public string MutationId { get; }
    public string RequestId { get; }
    public string SessionId { get; }
    public string ProjectId { get; }
    public long BaseRevision { get; }
    public long ResultingRevision { get; }
    public IReadOnlyList<PhotonCadAppliedOperationDelta> Operations { get; }
    public IReadOnlyList<PhotonCadEntityV1> Entities { get; }
    public IReadOnlyList<PhotonCadOccurrenceV1> Occurrences { get; }
    public IReadOnlyList<PhotonCadIssueV1> Issues { get; }
    public IReadOnlyList<PhotonCadBomRow> Bom { get; }
    public IReadOnlyList<PhotonCadSealedArtifactDelta> Artifacts { get; }
    public PhotonCadCollectionMergeMode OccurrenceMergeMode { get; }
    public PhotonCadCollectionMergeMode IssueMergeMode { get; }
    public PhotonCadCollectionMergeMode BomMergeMode { get; }
    public long TotalArtifactBytes { get; }
}

internal static class RuntimeSyncGuards
{
    internal static PhotonCadRuntimeSyncException Failure(string code, string field) => new(code, field);

    internal static IReadOnlyList<T> Copy<T>(
        IEnumerable<T>? values,
        string field,
        int maximum,
        bool requireAny = false)
    {
        if (values is null) throw Failure("required", field);
        var result = new List<T>();
        foreach (var value in values)
        {
            if (value is null) throw Failure("null_item", field);
            if (result.Count >= maximum) throw Failure("collection_too_large", field);
            result.Add(value);
        }
        if (requireAny && result.Count == 0) throw Failure("collection_empty", field);
        return new ReadOnlyCollection<T>(result);
    }

    internal static void RequireUnique(IEnumerable<string> values, string field)
    {
        var observed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (values.Any(value => !observed.Add(value))) throw Failure("duplicate_item", field);
    }

    internal static string Identifier(string? value, string field)
    {
        var safe = Text(value, field, PhotonCadProjectContract.MaximumIdentifierLength, true);
        if (!char.IsAsciiLetterOrDigit(safe[0])
            || safe.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-' and not '.' and not ':'))
            throw Failure("invalid_identifier", field);
        if (safe.Length >= 2 && char.IsAsciiLetter(safe[0]) && safe[1] == ':')
            throw Failure("path_like_value_rejected", field);
        if (LooksLikeRuntimeHandle(safe)) throw Failure("runtime_handle_rejected", field);
        return safe;
    }

    internal static string Token(string? value, string field, int maximum = 256)
    {
        var safe = Text(value, field, maximum, true);
        if (!char.IsAsciiLetterOrDigit(safe[0])
            || safe.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-' and not '.' and not ':' and not '+' and not '/'))
            throw Failure("invalid_token", field);
        if (safe[0] == '/' || safe.Contains("//", StringComparison.Ordinal)
            || safe.Contains(":/", StringComparison.Ordinal) || safe.Contains("../", StringComparison.Ordinal)
            || safe.Contains("..\\", StringComparison.Ordinal) || safe.Contains('\\')
            || safe.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            throw Failure("path_like_value_rejected", field);
        if (LooksLikeRuntimeHandle(safe)) throw Failure("runtime_handle_rejected", field);
        return safe;
    }

    internal static string Text(string? value, string field, int maximum, bool required)
    {
        if (value is null) throw Failure("required", field);
        string safe;
        try { safe = value.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException exception) { throw new PhotonCadRuntimeSyncException("invalid_unicode", field, exception); }
        if (safe.Length > maximum || (required && string.IsNullOrWhiteSpace(safe)))
            throw Failure(safe.Length > maximum ? "too_long" : "required", field);
        foreach (var character in safe)
        {
            var category = char.GetUnicodeCategory(character);
            if (char.IsControl(character) || category == UnicodeCategory.Format)
                throw Failure("unsafe_character", field);
        }
        return safe;
    }

    internal static string Digest(string? value, string field)
    {
        var safe = Text(value, field, 71, true).ToLowerInvariant();
        if (safe.StartsWith("sha256:", StringComparison.Ordinal)) safe = safe[7..];
        if (safe.Length != 64 || safe.Any(character => !char.IsAsciiHexDigit(character)))
            throw Failure("invalid_digest", field);
        return $"sha256:{safe}";
    }

    internal static string Sha256(ReadOnlySpan<byte> value) => $"sha256:{Convert.ToHexStringLower(SHA256.HashData(value))}";

    internal static bool FixedDigestEquals(string left, string right)
    {
        var first = Encoding.ASCII.GetBytes(Digest(left, nameof(left)));
        var second = Encoding.ASCII.GetBytes(Digest(right, nameof(right)));
        return first.Length == second.Length && CryptographicOperations.FixedTimeEquals(first, second);
    }

    internal static long Revision(long value, string field, long minimum = 0)
    {
        if (value < minimum || value > PhotonCadProjectContract.MaximumSafeInteger)
            throw Failure("invalid_revision", field);
        return value;
    }

    internal static double Finite(double value, string field)
    {
        if (!double.IsFinite(value)) throw Failure("invalid_number", field);
        return value;
    }

    internal static DateTimeOffset Utc(DateTimeOffset value, string field)
    {
        if (value == DateTimeOffset.MinValue || value == DateTimeOffset.MaxValue)
            throw Failure("invalid_timestamp", field);
        return value.ToUniversalTime();
    }

    internal static T EnumValue<T>(T value, string field) where T : struct, Enum
    {
        if (!Enum.IsDefined(value)) throw Failure("unsupported_enum", field);
        return value;
    }

    private static bool LooksLikeRuntimeHandle(string value)
    {
        foreach (var prefix in new[] { "wsp_", "prj_", "ses_", "art_", "prv_", "pkg_", "req_" })
        {
            if (value.StartsWith(prefix, StringComparison.Ordinal) && value.Length == prefix.Length + 32
                && value[prefix.Length..].All(char.IsAsciiHexDigit)) return true;
        }
        foreach (var prefix in new[]
        {
            "cad-workspace:", "cad-project:", "cad-reopen:", "cad-save-receipt:",
            "cad-storage-target:", "cad-storage-stage:", "cad-recovery:", "cad-overwrite-grant:",
            "cad-bom-review:", "cad-commercial-review:", "cad-commercial-approval:",
            "cad-document-page:", "cad-destination:", "cad-printer:",
        })
        {
            if (value.StartsWith(prefix, StringComparison.Ordinal)
                && value.Length >= prefix.Length + 32
                && value[prefix.Length..].All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-'))
                return true;
        }
        return false;
    }
}
