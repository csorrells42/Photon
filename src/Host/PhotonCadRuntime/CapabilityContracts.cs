namespace PhotonCadRuntime;

public enum CadBackend
{
    Geometry,
    Assembly,
}

public enum CadCapabilityOperationKind
{
    Create,
    Modify,
    Measure,
    Validate,
    Assemble,
    Drawing,
}

public enum CadParameterKind
{
    Number,
    Integer,
    Boolean,
    Text,
    Choice,
    Vector3,
    Entity,
    EntityList,
}

public enum CadParameterUnit
{
    Length,
    Angle,
    Ratio,
    Count,
}

public enum CadOperationMode
{
    Suggest,
    Scratch,
}

public sealed record CadVector3
{
    public CadVector3(double x, double y, double z)
    {
        X = ContractGuards.Finite(x, nameof(x), 1_000_000_000);
        Y = ContractGuards.Finite(y, nameof(y), 1_000_000_000);
        Z = ContractGuards.Finite(z, nameof(z), 1_000_000_000);
    }

    public double X { get; }
    public double Y { get; }
    public double Z { get; }
}

public sealed class CadBounds
{
    public CadBounds(CadVector3 minimum, CadVector3 maximum)
    {
        Minimum = minimum ?? throw new CadContractException("required", nameof(minimum));
        Maximum = maximum ?? throw new CadContractException("required", nameof(maximum));
        if (Minimum.X > Maximum.X || Minimum.Y > Maximum.Y || Minimum.Z > Maximum.Z)
            throw new CadContractException("invalid_bounds", nameof(maximum));
    }

    public CadVector3 Minimum { get; }
    public CadVector3 Maximum { get; }
}

public abstract class CadInputValue
{
    protected CadInputValue(CadParameterKind kind) => Kind = ContractGuards.EnumValue(kind, nameof(kind));
    public CadParameterKind Kind { get; }
}

public sealed class CadNullInputValue : CadInputValue
{
    public CadNullInputValue(CadParameterKind kind) : base(kind) { }
}

public sealed class CadNumberInputValue : CadInputValue
{
    public CadNumberInputValue(double value) : base(CadParameterKind.Number) =>
        Value = ContractGuards.Finite(value, nameof(value), 1_000_000_000);
    public double Value { get; }
}

public sealed class CadIntegerInputValue : CadInputValue
{
    public CadIntegerInputValue(long value) : base(CadParameterKind.Integer)
    {
        if (Math.Abs((decimal)value) > 1_000_000_000m)
            throw new CadContractException("invalid_number", nameof(value));
        Value = value;
    }
    public long Value { get; }
}

public sealed class CadBooleanInputValue : CadInputValue
{
    public CadBooleanInputValue(bool value) : base(CadParameterKind.Boolean) => Value = value;
    public bool Value { get; }
}

public sealed class CadTextInputValue : CadInputValue
{
    public CadTextInputValue(string value, CadParameterKind kind = CadParameterKind.Text) : base(kind)
    {
        if (kind is not CadParameterKind.Text and not CadParameterKind.Choice and not CadParameterKind.Entity)
            throw new CadContractException("invalid_input_kind", nameof(kind));
        Value = kind == CadParameterKind.Text
            ? ContractGuards.RequiredText(value, nameof(value), 4_096)
            : ContractGuards.Identifier(value, nameof(value));
    }
    public string Value { get; }
}

public sealed class CadVectorInputValue : CadInputValue
{
    public CadVectorInputValue(CadVector3 value) : base(CadParameterKind.Vector3) =>
        Value = value ?? throw new CadContractException("required", nameof(value));
    public CadVector3 Value { get; }
}

public sealed class CadEntityListInputValue : CadInputValue
{
    public CadEntityListInputValue(IEnumerable<string> entityIds) : base(CadParameterKind.EntityList)
    {
        EntityIds = ContractGuards.Copy(
            entityIds?.Select(entityId => ContractGuards.Identifier(entityId, nameof(entityIds))),
            nameof(entityIds),
            1_000);
        ContractGuards.RequireUnique(EntityIds, nameof(entityIds));
    }
    public IReadOnlyList<string> EntityIds { get; }
}

public sealed class CadChoice
{
    public CadChoice(string value, string label)
    {
        Value = ContractGuards.Identifier(value, nameof(value));
        Label = ContractGuards.RequiredText(label, nameof(label), CadContractLimits.DisplayNameLength);
    }
    public string Value { get; }
    public string Label { get; }
}

public sealed class CadParameterDefinition
{
    public CadParameterDefinition(
        string id,
        string label,
        string description,
        CadParameterKind kind,
        bool required,
        CadParameterUnit? unit = null,
        double? minimum = null,
        double? maximum = null,
        double? step = null,
        CadInputValue? defaultValue = null,
        IEnumerable<CadChoice>? choices = null)
    {
        Id = ContractGuards.Identifier(id, nameof(id));
        Label = ContractGuards.RequiredText(label, nameof(label), CadContractLimits.DisplayNameLength);
        Description = ContractGuards.RequiredText(description, nameof(description), 4_096);
        Kind = ContractGuards.EnumValue(kind, nameof(kind));
        Required = required;
        Unit = unit is null ? null : ContractGuards.EnumValue(unit.Value, nameof(unit));
        Minimum = minimum is null ? null : ContractGuards.Finite(minimum.Value, nameof(minimum), 1_000_000_000);
        Maximum = maximum is null ? null : ContractGuards.Finite(maximum.Value, nameof(maximum), 1_000_000_000);
        Step = step is null ? null : ContractGuards.Finite(step.Value, nameof(step), 1_000_000_000);
        if (Minimum > Maximum)
            throw new CadContractException("invalid_parameter_range", nameof(maximum));
        if (Step <= 0)
            throw new CadContractException("invalid_parameter_step", nameof(step));
        DefaultValue = defaultValue;
        if (defaultValue is not null && defaultValue.Kind != kind)
            throw new CadContractException("default_kind_mismatch", nameof(defaultValue));
        Choices = ContractGuards.Copy(choices ?? [], nameof(choices), 1_000);
        ContractGuards.RequireUnique(Choices.Select(choice => choice.Value), nameof(choices));
        if (kind == CadParameterKind.Choice && Choices.Count == 0)
            throw new CadContractException("choices_required", nameof(choices));
        if (kind != CadParameterKind.Choice && Choices.Count != 0)
            throw new CadContractException("choices_not_supported", nameof(choices));
    }

    public string Id { get; }
    public string Label { get; }
    public string Description { get; }
    public CadParameterKind Kind { get; }
    public bool Required { get; }
    public CadParameterUnit? Unit { get; }
    public double? Minimum { get; }
    public double? Maximum { get; }
    public double? Step { get; }
    public CadInputValue? DefaultValue { get; }
    public IReadOnlyList<CadChoice> Choices { get; }
}

public sealed class CadCapability
{
    public CadCapability(
        string id,
        CadBackend backend,
        string category,
        string title,
        string description,
        CadCapabilityOperationKind operation,
        IEnumerable<CadParameterDefinition> parameters,
        CadSourceIdentity source,
        bool previewSupported,
        bool experimental)
    {
        Id = ContractGuards.Identifier(id, nameof(id));
        Backend = ContractGuards.EnumValue(backend, nameof(backend));
        Category = ContractGuards.RequiredText(category, nameof(category), 128);
        Title = ContractGuards.RequiredText(title, nameof(title), CadContractLimits.DisplayNameLength);
        Description = ContractGuards.RequiredText(description, nameof(description), 4_096);
        Operation = ContractGuards.EnumValue(operation, nameof(operation));
        Parameters = ContractGuards.Copy(parameters, nameof(parameters), 128);
        ContractGuards.RequireUnique(Parameters.Select(parameter => parameter.Id), nameof(parameters));
        Source = source ?? throw new CadContractException("required", nameof(source));
        PreviewSupported = previewSupported;
        Experimental = experimental;
    }

    public string Id { get; }
    public CadBackend Backend { get; }
    public string Category { get; }
    public string Title { get; }
    public string Description { get; }
    public CadCapabilityOperationKind Operation { get; }
    public IReadOnlyList<CadParameterDefinition> Parameters { get; }
    public CadSourceIdentity Source { get; }
    public bool PreviewSupported { get; }
    public bool Experimental { get; }
}

public sealed class CadCatalogCoverage
{
    public CadCatalogCoverage(int discovered, int available, int unavailable, IEnumerable<string>? unavailableReasons = null)
    {
        Discovered = ContractGuards.Range(discovered, 0, 100_000, nameof(discovered));
        Available = ContractGuards.Range(available, 0, 100_000, nameof(available));
        Unavailable = ContractGuards.Range(unavailable, 0, 100_000, nameof(unavailable));
        if (Available + Unavailable != Discovered)
            throw new CadContractException("dishonest_catalog_coverage", nameof(discovered));
        UnavailableReasons = ContractGuards.Copy(
            (unavailableReasons ?? []).Select(reason => ContractGuards.RequiredText(reason, nameof(unavailableReasons), 512)),
            nameof(unavailableReasons),
            128);
    }

    public int Discovered { get; }
    public int Available { get; }
    public int Unavailable { get; }
    public IReadOnlyList<string> UnavailableReasons { get; }
}

public sealed class CadCapabilityCatalog
{
    public CadCapabilityCatalog(
        string catalogRevision,
        DateTimeOffset generatedAtUtc,
        IEnumerable<CadCapability> capabilities,
        CadCatalogCoverage coverage)
    {
        CatalogRevision = ContractGuards.Identifier(catalogRevision, nameof(catalogRevision));
        GeneratedAtUtc = ContractGuards.Utc(generatedAtUtc, nameof(generatedAtUtc));
        Capabilities = ContractGuards.Copy(capabilities, nameof(capabilities), 2_000);
        ContractGuards.RequireUnique(Capabilities.Select(capability => capability.Id), nameof(capabilities));
        Coverage = coverage ?? throw new CadContractException("required", nameof(coverage));
        if (Capabilities.Count > Coverage.Available)
            throw new CadContractException("dishonest_catalog_coverage", nameof(coverage));
    }

    public int ContractVersion => CadContractVersions.Host;
    public string CatalogRevision { get; }
    public DateTimeOffset GeneratedAtUtc { get; }
    public IReadOnlyList<CadCapability> Capabilities { get; }
    public CadCatalogCoverage Coverage { get; }
}

public sealed class CadOperationInput
{
    public CadOperationInput(string parameterId, CadInputValue value)
    {
        ParameterId = ContractGuards.Identifier(parameterId, nameof(parameterId));
        Value = value ?? throw new CadContractException("required", nameof(value));
    }
    public string ParameterId { get; }
    public CadInputValue Value { get; }
}

public sealed class CadOperationRequest
{
    public CadOperationRequest(
        CadRequestId requestId,
        CadSessionHandle session,
        CadProjectHandle project,
        CadRevision baseRevision,
        CadOperationMode mode,
        string capabilityId,
        IEnumerable<CadOperationInput>? inputs = null,
        IEnumerable<string>? targetEntityIds = null)
    {
        RequestId = requestId ?? throw new CadContractException("required", nameof(requestId));
        Session = session ?? throw new CadContractException("required", nameof(session));
        Project = project ?? throw new CadContractException("required", nameof(project));
        BaseRevision = baseRevision ?? throw new CadContractException("required", nameof(baseRevision));
        Mode = ContractGuards.EnumValue(mode, nameof(mode));
        CapabilityId = ContractGuards.Identifier(capabilityId, nameof(capabilityId));
        var inputEntries = ContractGuards.Copy(inputs ?? [], nameof(inputs), 128);
        ContractGuards.RequireUnique(inputEntries.Select(input => input.ParameterId), nameof(inputs));
        Inputs = new System.Collections.ObjectModel.ReadOnlyDictionary<string, CadInputValue>(
            inputEntries.ToDictionary(input => input.ParameterId, input => input.Value, StringComparer.Ordinal));
        TargetEntityIds = ContractGuards.Copy(
            (targetEntityIds ?? []).Select(id => ContractGuards.Identifier(id, nameof(targetEntityIds))),
            nameof(targetEntityIds),
            10_000);
        ContractGuards.RequireUnique(TargetEntityIds, nameof(targetEntityIds));
    }

    public int ContractVersion => CadContractVersions.Host;
    public CadRequestId RequestId { get; }
    public CadSessionHandle Session { get; }
    public CadProjectHandle Project { get; }
    public CadRevision BaseRevision { get; }
    public CadOperationMode Mode { get; }
    public string CapabilityId { get; }
    public IReadOnlyDictionary<string, CadInputValue> Inputs { get; }
    public IReadOnlyList<string> TargetEntityIds { get; }
}
