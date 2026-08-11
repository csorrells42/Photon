namespace PhotonCadRuntime;

public enum CadProjectMode
{
    Canonical,
    Scratch,
}

public enum CadEntityKind
{
    Body,
    Part,
    Assembly,
    Occurrence,
    Drawing,
    Datum,
}

public enum CadOperationRecordState
{
    Proposed,
    Applied,
    Rejected,
}

public sealed class CadProjectEntity
{
    public CadProjectEntity(
        string id,
        string? parentId,
        CadEntityKind kind,
        string name,
        bool visible,
        bool suppressed,
        string? sourceCapabilityId = null)
    {
        Id = ContractGuards.Identifier(id, nameof(id));
        ParentId = parentId is null ? null : ContractGuards.Identifier(parentId, nameof(parentId));
        KindValue = ContractGuards.EnumValue(kind, nameof(kind));
        Name = ContractGuards.RequiredText(name, nameof(name), CadContractLimits.DisplayNameLength);
        Visible = visible;
        Suppressed = suppressed;
        SourceCapabilityId = sourceCapabilityId is null
            ? null
            : ContractGuards.Identifier(sourceCapabilityId, nameof(sourceCapabilityId));
        if (Id == ParentId)
            throw new CadContractException("entity_self_parent", nameof(parentId));
    }

    public string Id { get; }
    public string? ParentId { get; }
    public CadEntityKind KindValue { get; }
    public string Name { get; }
    public bool Visible { get; }
    public bool Suppressed { get; }
    public string? SourceCapabilityId { get; }
}

public sealed class CadProjectOperationRecord
{
    public CadProjectOperationRecord(
        string id,
        string capabilityId,
        string label,
        DateTimeOffset createdAtUtc,
        CadOperationRecordState state)
    {
        Id = ContractGuards.Identifier(id, nameof(id));
        CapabilityId = ContractGuards.Identifier(capabilityId, nameof(capabilityId));
        Label = ContractGuards.RequiredText(label, nameof(label), CadContractLimits.DisplayNameLength);
        CreatedAtUtc = ContractGuards.Utc(createdAtUtc, nameof(createdAtUtc));
        StateValue = ContractGuards.EnumValue(state, nameof(state));
    }

    public string Id { get; }
    public string CapabilityId { get; }
    public string Label { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public CadOperationRecordState StateValue { get; }
}

public sealed class CadProjectSnapshot
{
    public CadProjectSnapshot(
        CadSessionHandle session,
        CadProjectHandle project,
        CadRevision revision,
        string title,
        CadLengthUnit units,
        CadProjectMode mode,
        IEnumerable<CadProjectEntity> entities,
        IEnumerable<CadProjectOperationRecord> operations,
        IEnumerable<CadValidationFinding> issues,
        bool dirty)
    {
        Session = session ?? throw new CadContractException("required", nameof(session));
        Project = project ?? throw new CadContractException("required", nameof(project));
        Revision = revision ?? throw new CadContractException("required", nameof(revision));
        Title = ContractGuards.RequiredText(title, nameof(title), CadContractLimits.DisplayNameLength);
        Units = ContractGuards.EnumValue(units, nameof(units));
        ModeValue = ContractGuards.EnumValue(mode, nameof(mode));
        Entities = ContractGuards.Copy(entities, nameof(entities), 10_000);
        Operations = ContractGuards.Copy(operations, nameof(operations), 5_000);
        Issues = ContractGuards.Copy(issues, nameof(issues), 2_000);
        Dirty = dirty;
        ContractGuards.RequireUnique(Entities.Select(entity => entity.Id), nameof(entities));
        ContractGuards.RequireUnique(Operations.Select(operation => operation.Id), nameof(operations));
        ValidateEntityTree();
    }

    public int ContractVersion => CadContractVersions.Host;
    public CadSessionHandle Session { get; }
    public CadProjectHandle Project { get; }
    public CadRevision Revision { get; }
    public string Title { get; }
    public CadLengthUnit Units { get; }
    public CadProjectMode ModeValue { get; }
    public IReadOnlyList<CadProjectEntity> Entities { get; }
    public IReadOnlyList<CadProjectOperationRecord> Operations { get; }
    public IReadOnlyList<CadValidationFinding> Issues { get; }
    public bool Dirty { get; }

    private void ValidateEntityTree()
    {
        var byId = Entities.ToDictionary(entity => entity.Id, StringComparer.Ordinal);
        foreach (var entity in Entities)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var cursor = entity;
            while (cursor.ParentId is not null)
            {
                if (!visited.Add(cursor.Id))
                    throw new CadContractException("entity_cycle", nameof(Entities));
                if (!byId.TryGetValue(cursor.ParentId, out var parent))
                    throw new CadContractException("entity_parent_missing", nameof(Entities));
                cursor = parent;
            }
        }
    }
}
