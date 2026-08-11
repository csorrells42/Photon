using PhotonCadProjects.Codec;
using PhotonCadProjects.RuntimeSync;

namespace PhotonCadRuntime.ManualProvider;

/// <summary>Stable manual-operation identifiers. These are deliberately separate from the industrial catalog.</summary>
public static class PhotonCadManualCapabilityIds
{
    public const string SketchExtrudeAdd = "manual.solid.extrude.add.v1";
    public const string SketchExtrudeCut = "manual.solid.extrude.cut.v1";
    public const string HoleCut = "manual.solid.hole.cut.v1";
    public const string Fillet = "manual.edge.fillet.v1";
    public const string Chamfer = "manual.edge.chamfer.v1";
    public const string LinearPattern = "manual.feature.pattern.linear.v1";
    public const string CircularPattern = "manual.feature.pattern.circular.v1";
}

public enum PhotonCadManualOperationKind
{
    SketchExtrudeAdd,
    SketchExtrudeCut,
    HoleCut,
    Fillet,
    Chamfer,
    LinearPattern,
    CircularPattern,
}

public enum PhotonCadManualAvailability
{
    Available,
    Unavailable,
}

public sealed class PhotonCadManualCapability
{
    internal PhotonCadManualCapability(
        string capabilityId,
        PhotonCadManualOperationKind kind,
        string title,
        string description,
        PhotonCadManualAvailability availability,
        string? unavailableReason)
    {
        CapabilityId = capabilityId;
        Kind = kind;
        Title = title;
        Description = description;
        Availability = availability;
        UnavailableReason = unavailableReason;
    }

    public string CapabilityId { get; }
    public PhotonCadManualOperationKind Kind { get; }
    public string Title { get; }
    public string Description { get; }
    public PhotonCadManualAvailability Availability { get; }
    public string? UnavailableReason { get; }
}

/// <summary>
/// A host-bound, pathless manual command. Values are intentionally limited to dimensions,
/// vectors, and exact entity identifiers; the renderer never supplies STEP, GLB, code, or paths.
/// </summary>
public sealed class PhotonCadManualCommand
{
    internal PhotonCadManualCommand(
        PhotonCadManualOperationKind kind,
        string capabilityId,
        string targetEntityId,
        bool createsEntity,
        ManualExecutionParameters execution,
        IEnumerable<PhotonCadSyncOperationInput> inputs)
    {
        Kind = kind;
        CapabilityId = capabilityId;
        TargetEntityId = targetEntityId;
        CreatesEntity = createsEntity;
        Execution = execution ?? throw new ArgumentNullException(nameof(execution));
        Inputs = inputs.ToArray();
        if (Inputs.Count is < 1 or > 32 || Inputs.Select(value => value.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Inputs.Count)
            throw new ArgumentException("manual_input_set_invalid", nameof(inputs));
    }

    public PhotonCadManualOperationKind Kind { get; }
    public string CapabilityId { get; }
    public string TargetEntityId { get; }
    public bool CreatesEntity { get; }
    public IReadOnlyList<PhotonCadSyncOperationInput> Inputs { get; }
    internal ManualExecutionParameters Execution { get; }
}

internal abstract record ManualExecutionParameters;

internal sealed record ManualSketchParameters(
    string ProfileKind,
    string Plane,
    double WidthMm,
    double HeightMm,
    double RadiusMm,
    double DepthMm) : ManualExecutionParameters;

internal sealed record ManualHoleParameters(
    double RadiusMm,
    double DepthMm,
    double XMm,
    double YMm,
    double ZMm) : ManualExecutionParameters;

internal sealed record ManualUnsupportedParameters(string Operation) : ManualExecutionParameters;

/// <summary>
/// The only manual-geometry execution boundary. A future pinned container implementation must
/// return a complete sealed STEP/GLB mutation; it must not return a live shape, filesystem path,
/// script, or mesh-only approximation.
/// </summary>
public interface IPhotonCadManualGeometryAuthority
{
    ValueTask<PhotonCadSealedMutationDelta> ExecuteAsync(
        PhotonCadManualAuthorityRequest request,
        CancellationToken cancellationToken = default);

    ValueTask CompensateAsync(
        PhotonCadManualAuthorityRequest request,
        PhotonCadSealedMutationDelta mutation,
        string reason,
        CancellationToken cancellationToken = default);
}

public sealed class PhotonCadManualAuthorityRequest
{
    internal PhotonCadManualAuthorityRequest(
        PhotonCadSealedMutationProviderRequest providerRequest,
        PhotonCadManualCommand command)
    {
        ProviderRequest = providerRequest ?? throw new ArgumentNullException(nameof(providerRequest));
        Command = command ?? throw new ArgumentNullException(nameof(command));
    }

    public PhotonCadSealedMutationProviderRequest ProviderRequest { get; }
    public PhotonCadManualCommand Command { get; }
}

public sealed class PhotonCadManualBoundMutation
{
    internal PhotonCadManualBoundMutation(
        PhotonCadRuntimeSyncRequest request,
        IPhotonCadSealedMutationProvider provider,
        IPhotonCadSealedMutationCompensator compensator)
    {
        Request = request;
        Provider = provider;
        Compensator = compensator;
    }

    public PhotonCadRuntimeSyncRequest Request { get; }
    public IPhotonCadSealedMutationProvider Provider { get; }
    public IPhotonCadSealedMutationCompensator Compensator { get; }
}
