namespace PhotonCadProjects.Codec;

public sealed class PhotonCadVector3V1
{
    public PhotonCadVector3V1(double x, double y, double z)
    {
        X = PhotonCadFileGuardsV1.Finite(x, nameof(x));
        Y = PhotonCadFileGuardsV1.Finite(y, nameof(y));
        Z = PhotonCadFileGuardsV1.Finite(z, nameof(z));
    }

    public double X { get; }
    public double Y { get; }
    public double Z { get; }
}

public sealed class PhotonCadBoundsV1
{
    public PhotonCadBoundsV1(PhotonCadVector3V1 minimum, PhotonCadVector3V1 maximum)
    {
        Minimum = minimum ?? throw PhotonCadFileGuardsV1.Failure("required", nameof(minimum));
        Maximum = maximum ?? throw PhotonCadFileGuardsV1.Failure("required", nameof(maximum));
        if (minimum.X > maximum.X || minimum.Y > maximum.Y || minimum.Z > maximum.Z)
            throw PhotonCadFileGuardsV1.Failure("invalid_bounds", nameof(maximum));
    }

    public PhotonCadVector3V1 Minimum { get; }
    public PhotonCadVector3V1 Maximum { get; }
}

public sealed class PhotonCadSourceIdentityV1
{
    public PhotonCadSourceIdentityV1(string package, string version, string digest, string license)
    {
        Package = PhotonCadFileGuardsV1.Token(package, nameof(package));
        Version = PhotonCadFileGuardsV1.Token(version, nameof(version));
        Digest = PhotonCadFileGuardsV1.Digest(digest, nameof(digest));
        License = PhotonCadFileGuardsV1.Token(license, nameof(license));
    }

    public string Package { get; }
    public string Version { get; }
    public string Digest { get; }
    public string License { get; }
}

public sealed class PhotonCadArtifactProvenanceV1
{
    public PhotonCadArtifactProvenanceV1(
        PhotonCadBackendV1 backend,
        string bundleId,
        string bundleManifestSha256,
        string capabilityId,
        string operationId,
        PhotonCadSourceIdentityV1 source)
    {
        Backend = PhotonCadFileGuardsV1.EnumValue(backend, nameof(backend));
        BundleId = PhotonCadFileGuardsV1.Identifier(bundleId, nameof(bundleId));
        BundleManifestSha256 = PhotonCadFileGuardsV1.Digest(bundleManifestSha256, nameof(bundleManifestSha256));
        CapabilityId = PhotonCadFileGuardsV1.Identifier(capabilityId, nameof(capabilityId));
        OperationId = PhotonCadFileGuardsV1.Identifier(operationId, nameof(operationId));
        Source = source ?? throw PhotonCadFileGuardsV1.Failure("required", nameof(source));
    }

    public PhotonCadBackendV1 Backend { get; }
    public string BundleId { get; }
    public string BundleManifestSha256 { get; }
    public string CapabilityId { get; }
    public string OperationId { get; }
    public PhotonCadSourceIdentityV1 Source { get; }
}

public sealed class PhotonCadEntityV1
{
    public PhotonCadEntityV1(
        string id,
        string? parentId,
        PhotonCadEntityKindV1 kind,
        string name,
        bool visible,
        bool suppressed,
        string? sourceCapabilityId = null)
    {
        Id = PhotonCadFileGuardsV1.Identifier(id, nameof(id));
        ParentId = parentId is null ? null : PhotonCadFileGuardsV1.Identifier(parentId, nameof(parentId));
        Kind = PhotonCadFileGuardsV1.EnumValue(kind, nameof(kind));
        Name = PhotonCadFileGuardsV1.Text(name, nameof(name), 256, required: true);
        Visible = visible;
        Suppressed = suppressed;
        SourceCapabilityId = sourceCapabilityId is null
            ? null
            : PhotonCadFileGuardsV1.Identifier(sourceCapabilityId, nameof(sourceCapabilityId));
        if (StringComparer.OrdinalIgnoreCase.Equals(Id, ParentId))
            throw PhotonCadFileGuardsV1.Failure("entity_self_parent", nameof(parentId));
    }

    public string Id { get; }
    public string? ParentId { get; }
    public PhotonCadEntityKindV1 Kind { get; }
    public string Name { get; }
    public bool Visible { get; }
    public bool Suppressed { get; }
    public string? SourceCapabilityId { get; }
}

public sealed class PhotonCadInputValueV1
{
    private PhotonCadInputValueV1(PhotonCadInputKindV1 kind, object? value)
    {
        Kind = PhotonCadFileGuardsV1.EnumValue(kind, nameof(kind));
        Value = value;
    }

    public PhotonCadInputKindV1 Kind { get; }
    internal object? Value { get; }

    public static PhotonCadInputValueV1 Null(PhotonCadInputKindV1 kind) => new(kind, null);
    public static PhotonCadInputValueV1 Number(double value) => new(PhotonCadInputKindV1.Number, PhotonCadFileGuardsV1.Finite(value, nameof(value)));

    public static PhotonCadInputValueV1 Integer(long value)
    {
        if (value < -PhotonCadProjectContract.MaximumSafeInteger || value > PhotonCadProjectContract.MaximumSafeInteger)
            throw PhotonCadFileGuardsV1.Failure("invalid_safe_integer", nameof(value));
        return new PhotonCadInputValueV1(PhotonCadInputKindV1.Integer, value);
    }

    public static PhotonCadInputValueV1 Boolean(bool value) => new(PhotonCadInputKindV1.Boolean, value);
    public static PhotonCadInputValueV1 Text(string value) => new(PhotonCadInputKindV1.Text, PhotonCadFileGuardsV1.Text(value, nameof(value), 4_096, false));
    public static PhotonCadInputValueV1 Choice(string value) => new(PhotonCadInputKindV1.Choice, PhotonCadFileGuardsV1.Identifier(value, nameof(value)));
    public static PhotonCadInputValueV1 Vector3(PhotonCadVector3V1 value) => new(PhotonCadInputKindV1.Vector3, value ?? throw PhotonCadFileGuardsV1.Failure("required", nameof(value)));
    public static PhotonCadInputValueV1 Entity(string value) => new(PhotonCadInputKindV1.Entity, PhotonCadFileGuardsV1.Identifier(value, nameof(value)));

    public static PhotonCadInputValueV1 EntityList(IEnumerable<string> values)
    {
        var copy = PhotonCadFileGuardsV1.Copy(values, nameof(values), 1_000)
            .Select(value => PhotonCadFileGuardsV1.Identifier(value, nameof(values)))
            .ToArray();
        if (copy.Distinct(StringComparer.OrdinalIgnoreCase).Count() != copy.Length)
            throw PhotonCadFileGuardsV1.Failure("duplicate_entity_input", nameof(values));
        return new PhotonCadInputValueV1(PhotonCadInputKindV1.EntityList, Array.AsReadOnly(copy));
    }
}

public sealed class PhotonCadOperationInputV1
{
    public PhotonCadOperationInputV1(string id, PhotonCadInputValueV1 value)
    {
        Id = PhotonCadFileGuardsV1.Identifier(id, nameof(id));
        Value = value ?? throw PhotonCadFileGuardsV1.Failure("required", nameof(value));
    }

    public string Id { get; }
    public PhotonCadInputValueV1 Value { get; }
}

public sealed class PhotonCadOperationV1
{
    public PhotonCadOperationV1(
        int ordinal,
        string id,
        string capabilityId,
        string label,
        DateTimeOffset createdAtUtc,
        PhotonCadOperationStateV1 state,
        PhotonCadOperationModeV1 mode,
        IEnumerable<PhotonCadOperationInputV1> inputs,
        IEnumerable<string> targetEntityIds,
        PhotonCadSourceIdentityV1 source)
    {
        if (ordinal < 0 || ordinal >= PhotonCadProjectFileV1.MaximumOperations)
            throw PhotonCadFileGuardsV1.Failure("invalid_ordinal", nameof(ordinal));
        Ordinal = ordinal;
        Id = PhotonCadFileGuardsV1.Identifier(id, nameof(id));
        CapabilityId = PhotonCadFileGuardsV1.Identifier(capabilityId, nameof(capabilityId));
        Label = PhotonCadFileGuardsV1.Text(label, nameof(label), 256, required: true);
        CreatedAtUtc = PhotonCadFileGuardsV1.Utc(createdAtUtc, nameof(createdAtUtc));
        State = PhotonCadFileGuardsV1.EnumValue(state, nameof(state));
        Mode = PhotonCadFileGuardsV1.EnumValue(mode, nameof(mode));
        Inputs = PhotonCadFileGuardsV1.Copy(inputs, nameof(inputs), PhotonCadProjectFileV1.MaximumInputsPerOperation);
        TargetEntityIds = Array.AsReadOnly(PhotonCadFileGuardsV1.Copy(targetEntityIds, nameof(targetEntityIds), PhotonCadProjectFileV1.MaximumTargetsPerOperation)
            .Select(value => PhotonCadFileGuardsV1.Identifier(value, nameof(targetEntityIds)))
            .ToArray());
        Source = source ?? throw PhotonCadFileGuardsV1.Failure("required", nameof(source));
    }

    public int Ordinal { get; }
    public string Id { get; }
    public string CapabilityId { get; }
    public string Label { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public PhotonCadOperationStateV1 State { get; }
    public PhotonCadOperationModeV1 Mode { get; }
    public IReadOnlyList<PhotonCadOperationInputV1> Inputs { get; }
    public IReadOnlyList<string> TargetEntityIds { get; }
    public PhotonCadSourceIdentityV1 Source { get; }
}

public sealed class PhotonCadOccurrenceV1
{
    private readonly double[] _transform;

    public PhotonCadOccurrenceV1(
        string occurrenceId,
        string? parentOccurrenceId,
        string partNumber,
        string sourceEntityId,
        IEnumerable<double> transform)
    {
        OccurrenceId = PhotonCadFileGuardsV1.Identifier(occurrenceId, nameof(occurrenceId));
        ParentOccurrenceId = parentOccurrenceId is null ? null : PhotonCadFileGuardsV1.Identifier(parentOccurrenceId, nameof(parentOccurrenceId));
        PartNumber = PhotonCadFileGuardsV1.Text(partNumber, nameof(partNumber), 256, required: true);
        SourceEntityId = PhotonCadFileGuardsV1.Identifier(sourceEntityId, nameof(sourceEntityId));
        _transform = (transform ?? throw PhotonCadFileGuardsV1.Failure("required", nameof(transform)))
            .Select(value => PhotonCadFileGuardsV1.Finite(value, nameof(transform)))
            .ToArray();
        if (_transform.Length != 16) throw PhotonCadFileGuardsV1.Failure("invalid_transform", nameof(transform));
        if (StringComparer.OrdinalIgnoreCase.Equals(OccurrenceId, ParentOccurrenceId))
            throw PhotonCadFileGuardsV1.Failure("occurrence_self_parent", nameof(parentOccurrenceId));
    }

    public string OccurrenceId { get; }
    public string? ParentOccurrenceId { get; }
    public string PartNumber { get; }
    public string SourceEntityId { get; }
    public IReadOnlyList<double> Transform => Array.AsReadOnly((double[])_transform.Clone());
    internal ReadOnlySpan<double> TransformSpan => _transform;
}

public sealed class PhotonCadIssueV1
{
    public PhotonCadIssueV1(string code, PhotonCadIssueSeverityV1 severity, string message, IEnumerable<string> entityIds)
    {
        Code = PhotonCadFileGuardsV1.Identifier(code, nameof(code));
        Severity = PhotonCadFileGuardsV1.EnumValue(severity, nameof(severity));
        Message = PhotonCadFileGuardsV1.Text(message, nameof(message), 4_096, required: true);
        EntityIds = Array.AsReadOnly(PhotonCadFileGuardsV1.Copy(entityIds, nameof(entityIds), PhotonCadProjectFileV1.MaximumEntities)
            .Select(value => PhotonCadFileGuardsV1.Identifier(value, nameof(entityIds)))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray());
        if (EntityIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != EntityIds.Count)
            throw PhotonCadFileGuardsV1.Failure("duplicate_issue_entity", nameof(entityIds));
    }

    public string Code { get; }
    public PhotonCadIssueSeverityV1 Severity { get; }
    public string Message { get; }
    public IReadOnlyList<string> EntityIds { get; }
}

public sealed class PhotonCadArtifactV1
{
    private readonly byte[] _content;

    public PhotonCadArtifactV1(
        PhotonCadArtifactRoleV1 role,
        PhotonCadArtifactKindV1 kind,
        string? ownerEntityId,
        long revision,
        ReadOnlyMemory<byte> content,
        PhotonCadBoundsV1? bounds,
        PhotonCadArtifactProvenanceV1 provenance)
    {
        Role = PhotonCadFileGuardsV1.EnumValue(role, nameof(role));
        Kind = PhotonCadFileGuardsV1.EnumValue(kind, nameof(kind));
        OwnerEntityId = ownerEntityId is null ? null : PhotonCadFileGuardsV1.Identifier(ownerEntityId, nameof(ownerEntityId));
        Revision = PhotonCadFileGuardsV1.SafeInteger(revision, nameof(revision), minimum: 1);
        if (content.Length <= 0 || content.Length > PhotonCadProjectFileV1.MaximumEncodedBytes)
            throw PhotonCadFileGuardsV1.Failure("invalid_artifact_length", nameof(content));
        _content = content.ToArray();
        Digest = PhotonCadFileGuardsV1.Sha256(_content);
        Bounds = bounds;
        Provenance = provenance ?? throw PhotonCadFileGuardsV1.Failure("required", nameof(provenance));
    }

    public PhotonCadArtifactRoleV1 Role { get; }
    public PhotonCadArtifactKindV1 Kind { get; }
    public string? OwnerEntityId { get; }
    public long Revision { get; }
    public long ByteLength => _content.LongLength;
    public string Digest { get; }
    public PhotonCadBoundsV1? Bounds { get; }
    public PhotonCadArtifactProvenanceV1 Provenance { get; }
    public ReadOnlyMemory<byte> Content => _content.ToArray();
    internal ReadOnlyMemory<byte> ContentUnsafe => _content;
}

public sealed class PhotonCadProjectStateV1
{
    public PhotonCadProjectStateV1(
        string sessionId,
        string projectId,
        long revision,
        string title,
        PhotonCadProjectUnit units,
        IEnumerable<PhotonCadEntityV1> entities,
        IEnumerable<PhotonCadOperationV1> operations,
        IEnumerable<PhotonCadOccurrenceV1> occurrences,
        IEnumerable<PhotonCadIssueV1> issues,
        IEnumerable<PhotonCadBomRow> bom,
        IEnumerable<PhotonCadArtifactV1> artifacts,
        bool dirty)
    {
        SessionId = PhotonCadFileGuardsV1.Identifier(sessionId, nameof(sessionId));
        ProjectId = PhotonCadFileGuardsV1.Identifier(projectId, nameof(projectId));
        Revision = PhotonCadFileGuardsV1.SafeInteger(revision, nameof(revision));
        Title = PhotonCadFileGuardsV1.DisplayName(title, nameof(title), PhotonCadProjectContract.MaximumDisplayNameLength);
        Units = PhotonCadFileGuardsV1.EnumValue(units, nameof(units));
        Entities = PhotonCadFileGuardsV1.Copy(entities, nameof(entities), PhotonCadProjectFileV1.MaximumEntities);
        Operations = PhotonCadFileGuardsV1.Copy(operations, nameof(operations), PhotonCadProjectFileV1.MaximumOperations);
        Occurrences = PhotonCadFileGuardsV1.Copy(occurrences, nameof(occurrences), PhotonCadProjectFileV1.MaximumOccurrences);
        Issues = PhotonCadFileGuardsV1.Copy(issues, nameof(issues), PhotonCadProjectFileV1.MaximumIssues);
        Bom = PhotonCadFileGuardsV1.Copy(bom, nameof(bom), PhotonCadProjectContract.MaximumBomRows);
        Artifacts = PhotonCadFileGuardsV1.Copy(artifacts, nameof(artifacts), PhotonCadProjectFileV1.MaximumBlobCount);
        Dirty = dirty;
        PhotonCadProjectSemanticValidatorV1.Validate(this);
    }

    public string SessionId { get; }
    public string ProjectId { get; }
    public long Revision { get; }
    public string Title { get; }
    public PhotonCadProjectUnit Units { get; }
    public IReadOnlyList<PhotonCadEntityV1> Entities { get; }
    public IReadOnlyList<PhotonCadOperationV1> Operations { get; }
    public IReadOnlyList<PhotonCadOccurrenceV1> Occurrences { get; }
    public IReadOnlyList<PhotonCadIssueV1> Issues { get; }
    public IReadOnlyList<PhotonCadBomRow> Bom { get; }
    public IReadOnlyList<PhotonCadArtifactV1> Artifacts { get; }
    public bool Dirty { get; }
}
