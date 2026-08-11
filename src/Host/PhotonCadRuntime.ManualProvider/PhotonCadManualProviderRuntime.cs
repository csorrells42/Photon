using PhotonCadProjects;
using PhotonCadProjects.Codec;
using PhotonCadProjects.RuntimeSync;

namespace PhotonCadRuntime.ManualProvider;

/// <summary>
/// Provider-neutral manual CAD front door. It defines truthful direct-modeling operations now,
/// but deliberately has no fallback geometry implementation: without a sealed-geometry authority
/// every capability is advertised unavailable and every execution fails before persistence.
/// </summary>
public sealed class PhotonCadManualProviderRuntime
{
    private const double MaximumDimensionMm = 1_000_000;
    private readonly IPhotonCadManualGeometryAuthority? _authority;
    private readonly IReadOnlyList<PhotonCadManualCapability> _catalog;

    private PhotonCadManualProviderRuntime(IPhotonCadManualGeometryAuthority? authority)
    {
        _authority = authority;
        _catalog = CreateCatalog(authority is not null);
    }

    public static PhotonCadManualProviderRuntime CreateUnavailable() => new(authority: null);

    public static PhotonCadManualProviderRuntime Create(IPhotonCadManualGeometryAuthority authority) =>
        new(authority ?? throw new ArgumentNullException(nameof(authority)));

    public static async ValueTask<PhotonCadManualProviderRuntime> CreateLocalEngineeringAsync(
        string dockerExecutable,
        string dockerConfigDirectory,
        string workspaceRoot,
        string evidenceSelectionPath,
        TimeSpan? operationTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var authority = await PhotonCadManualContainerAuthority.CreateAsync(
            dockerExecutable,
            dockerConfigDirectory,
            workspaceRoot,
            evidenceSelectionPath,
            operationTimeout,
            cancellationToken).ConfigureAwait(false);
        return Create(authority);
    }

    public IReadOnlyList<PhotonCadManualCapability> GetCatalog() => _catalog;

    public PhotonCadManualBoundMutation BindSketchExtrudeAdd(
        string requestId, string sessionId, string projectId, long baseRevision, string newEntityId,
        string plane, double profileWidthMm, double profileHeightMm, double extrusionDepthMm) => Bind(
            PhotonCadManualOperationKind.SketchExtrudeAdd,
            PhotonCadManualCapabilityIds.SketchExtrudeAdd,
            requestId, sessionId, projectId, baseRevision, newEntityId, createsEntity: true,
            new ManualSketchParameters("rectangle", "xy", profileWidthMm, profileHeightMm, 0, extrusionDepthMm),
            [
                ProfileKind("rectangle"), Choice("sketchPlane", plane), Number("profileWidthMm", profileWidthMm),
                Number("profileHeightMm", profileHeightMm), Number("extrusionDepthMm", extrusionDepthMm),
            ]);

    public PhotonCadManualBoundMutation BindCircularSketchExtrudeAdd(
        string requestId, string sessionId, string projectId, long baseRevision, string newEntityId,
        string plane, double profileRadiusMm, double extrusionDepthMm) => Bind(
            PhotonCadManualOperationKind.SketchExtrudeAdd,
            PhotonCadManualCapabilityIds.SketchExtrudeAdd,
            requestId, sessionId, projectId, baseRevision, newEntityId, createsEntity: true,
            new ManualSketchParameters("circle", "xy", 0, 0, profileRadiusMm, extrusionDepthMm),
            [
                ProfileKind("circle"), Choice("sketchPlane", plane), Number("profileRadiusMm", profileRadiusMm),
                Number("extrusionDepthMm", extrusionDepthMm),
            ]);

    public PhotonCadManualBoundMutation BindSketchExtrudeCut(
        string requestId, string sessionId, string projectId, long baseRevision, string targetEntityId,
        string plane, double profileWidthMm, double profileHeightMm, double cutDepthMm) => Bind(
            PhotonCadManualOperationKind.SketchExtrudeCut,
            PhotonCadManualCapabilityIds.SketchExtrudeCut,
            requestId, sessionId, projectId, baseRevision, targetEntityId, createsEntity: false,
            new ManualSketchParameters("rectangle", "xy", profileWidthMm, profileHeightMm, 0, cutDepthMm),
            [
                ProfileKind("rectangle"), Choice("sketchPlane", plane), Number("profileWidthMm", profileWidthMm),
                Number("profileHeightMm", profileHeightMm), Number("cutDepthMm", cutDepthMm),
            ]);

    public PhotonCadManualBoundMutation BindHoleCut(
        string requestId, string sessionId, string projectId, long baseRevision, string targetEntityId,
        double diameterMm, double depthMm, double xMm, double yMm, double zMm) => Bind(
            PhotonCadManualOperationKind.HoleCut,
            PhotonCadManualCapabilityIds.HoleCut,
            requestId, sessionId, projectId, baseRevision, targetEntityId, createsEntity: false,
            new ManualHoleParameters(diameterMm / 2, depthMm, xMm, yMm, zMm),
            [
                Number("diameterMm", diameterMm), Number("depthMm", depthMm),
                Number("xMm", xMm, allowZero: true), Number("yMm", yMm, allowZero: true), Number("zMm", zMm, allowZero: true),
            ]);

    public PhotonCadManualBoundMutation BindFillet(
        string requestId, string sessionId, string projectId, long baseRevision, string targetEntityId,
        double radiusMm, IEnumerable<string> edgeIds) => BindEdges(
            PhotonCadManualOperationKind.Fillet, PhotonCadManualCapabilityIds.Fillet,
            requestId, sessionId, projectId, baseRevision, targetEntityId, radiusMm, edgeIds, "radiusMm");

    public PhotonCadManualBoundMutation BindChamfer(
        string requestId, string sessionId, string projectId, long baseRevision, string targetEntityId,
        double distanceMm, IEnumerable<string> edgeIds) => BindEdges(
            PhotonCadManualOperationKind.Chamfer, PhotonCadManualCapabilityIds.Chamfer,
            requestId, sessionId, projectId, baseRevision, targetEntityId, distanceMm, edgeIds, "distanceMm");

    public PhotonCadManualBoundMutation BindLinearPattern(
        string requestId, string sessionId, string projectId, long baseRevision, string targetEntityId,
        string seedEntityId, long count, double spacingMm) => Bind(
            PhotonCadManualOperationKind.LinearPattern, PhotonCadManualCapabilityIds.LinearPattern,
            requestId, sessionId, projectId, baseRevision, targetEntityId, createsEntity: false,
            new ManualUnsupportedParameters(nameof(PhotonCadManualOperationKind.LinearPattern)),
            [Entity("seedEntityId", seedEntityId), Count(count), Number("spacingMm", spacingMm)]);

    public PhotonCadManualBoundMutation BindCircularPattern(
        string requestId, string sessionId, string projectId, long baseRevision, string targetEntityId,
        string seedEntityId, long count, double angleDegrees) => Bind(
            PhotonCadManualOperationKind.CircularPattern, PhotonCadManualCapabilityIds.CircularPattern,
            requestId, sessionId, projectId, baseRevision, targetEntityId, createsEntity: false,
            new ManualUnsupportedParameters(nameof(PhotonCadManualOperationKind.CircularPattern)),
            [Entity("seedEntityId", seedEntityId), Count(count), Number("angleDegrees", angleDegrees)]);

    private PhotonCadManualBoundMutation BindEdges(
        PhotonCadManualOperationKind kind, string capabilityId,
        string requestId, string sessionId, string projectId, long baseRevision, string targetEntityId,
        double lengthMm, IEnumerable<string> edgeIds, string lengthId)
    {
        if (edgeIds is null) throw new ArgumentNullException(nameof(edgeIds));
        var copiedEdges = edgeIds.ToArray();
        if (copiedEdges.Length is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(edgeIds));
        return Bind(kind, capabilityId, requestId, sessionId, projectId, baseRevision, targetEntityId, createsEntity: false,
            new ManualUnsupportedParameters(kind.ToString()),
            [Number(lengthId, lengthMm), new PhotonCadSyncOperationInput("edgeIds", PhotonCadSyncInputValue.EntityList(copiedEdges))]);
    }

    private PhotonCadManualBoundMutation Bind(
        PhotonCadManualOperationKind kind, string capabilityId,
        string requestId, string sessionId, string projectId, long baseRevision, string targetEntityId,
        bool createsEntity, ManualExecutionParameters execution, IReadOnlyList<PhotonCadSyncOperationInput> inputs)
    {
        var command = new PhotonCadManualCommand(kind, capabilityId, targetEntityId, createsEntity, execution, inputs);
        var request = new PhotonCadRuntimeSyncRequest(
            requestId, sessionId, projectId, baseRevision, capabilityId, PhotonCadOperationModeV1.Scratch, inputs, [targetEntityId]);
        var provider = new ManualProvider(request, command, _authority);
        return new PhotonCadManualBoundMutation(request, provider, new ManualCompensator(provider));
    }

    private static PhotonCadSyncOperationInput Number(string id, double value, bool allowZero = false)
    {
        if (!double.IsFinite(value) || value > MaximumDimensionMm || value < (allowZero ? -MaximumDimensionMm : double.Epsilon))
            throw new ArgumentOutOfRangeException(nameof(value));
        return new PhotonCadSyncOperationInput(id, PhotonCadSyncInputValue.Number(value));
    }

    private static PhotonCadSyncOperationInput Choice(string id, string value)
    {
        if (!StringComparer.Ordinal.Equals(value, "xy"))
            throw new ArgumentException("manual_sketch_plane_invalid", nameof(value));
        return new PhotonCadSyncOperationInput(id, PhotonCadSyncInputValue.Choice(value));
    }

    private static PhotonCadSyncOperationInput ProfileKind(string value)
    {
        if (!StringComparer.Ordinal.Equals(value, "rectangle") && !StringComparer.Ordinal.Equals(value, "circle"))
            throw new ArgumentException("manual_sketch_profile_invalid", nameof(value));
        return new PhotonCadSyncOperationInput("profileKind", PhotonCadSyncInputValue.Choice(value));
    }

    private static PhotonCadSyncOperationInput Entity(string id, string value) =>
        new(id, PhotonCadSyncInputValue.Entity(value));

    private static PhotonCadSyncOperationInput Count(long value)
    {
        if (value is < 2 or > 256) throw new ArgumentOutOfRangeException(nameof(value));
        return new PhotonCadSyncOperationInput("count", PhotonCadSyncInputValue.Integer(value));
    }

    private static IReadOnlyList<PhotonCadManualCapability> CreateCatalog(bool authorityInstalled)
    {
        const string authorityUnavailable = "manual_geometry_protocol_v1_not_installed";
        const string operationUnavailable = "manual_operation_not_installed";
        PhotonCadManualCapability Entry(string id, PhotonCadManualOperationKind kind, string title, string description) => new(
            id, kind, title, description,
            authorityInstalled && kind is PhotonCadManualOperationKind.SketchExtrudeAdd
                or PhotonCadManualOperationKind.SketchExtrudeCut
                or PhotonCadManualOperationKind.HoleCut
                ? PhotonCadManualAvailability.Available
                : PhotonCadManualAvailability.Unavailable,
            authorityInstalled
                ? kind is PhotonCadManualOperationKind.SketchExtrudeAdd
                    or PhotonCadManualOperationKind.SketchExtrudeCut
                    or PhotonCadManualOperationKind.HoleCut
                    ? null
                    : operationUnavailable
                : authorityUnavailable);
        return
        [
            Entry(PhotonCadManualCapabilityIds.SketchExtrudeAdd, PhotonCadManualOperationKind.SketchExtrudeAdd, "Sketch + extrude", "Create a bounded rectangle or circle profile on XY and add one solid."),
            Entry(PhotonCadManualCapabilityIds.SketchExtrudeCut, PhotonCadManualOperationKind.SketchExtrudeCut, "Sketch cut", "Cut a bounded rectangular XY profile through one existing solid."),
            Entry(PhotonCadManualCapabilityIds.HoleCut, PhotonCadManualOperationKind.HoleCut, "Hole", "Cut a cylindrical hole into one existing solid."),
            Entry(PhotonCadManualCapabilityIds.Fillet, PhotonCadManualOperationKind.Fillet, "Fillet", "Round selected stable edge identifiers on one existing solid."),
            Entry(PhotonCadManualCapabilityIds.Chamfer, PhotonCadManualOperationKind.Chamfer, "Chamfer", "Chamfer selected stable edge identifiers on one existing solid."),
            Entry(PhotonCadManualCapabilityIds.LinearPattern, PhotonCadManualOperationKind.LinearPattern, "Linear pattern", "Pattern one seeded feature along a bounded linear direction."),
            Entry(PhotonCadManualCapabilityIds.CircularPattern, PhotonCadManualOperationKind.CircularPattern, "Circular pattern", "Pattern one seeded feature around a bounded circular axis."),
        ];
    }

    private sealed class ManualProvider : IPhotonCadSealedMutationProvider
    {
        private readonly PhotonCadRuntimeSyncRequest _boundRequest;
        private readonly PhotonCadManualCommand _command;
        private readonly IPhotonCadManualGeometryAuthority? _authority;
        private int _started;
        private int _revoked;
        private PhotonCadManualAuthorityRequest? _authorityRequest;
        private PhotonCadSealedMutationDelta? _mutation;

        public ManualProvider(PhotonCadRuntimeSyncRequest boundRequest, PhotonCadManualCommand command, IPhotonCadManualGeometryAuthority? authority)
        {
            _boundRequest = boundRequest;
            _command = command;
            _authority = authority;
        }

        public async ValueTask<PhotonCadSealedMutationDelta> ApplyAsync(
            PhotonCadSealedMutationProviderRequest request,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) throw Failure("manual_provider_single_use");
            if (Volatile.Read(ref _revoked) != 0) throw Failure("manual_provider_revoked");
            RequireBound(request);
            if (_authority is null) throw Failure("manual_geometry_authority_unavailable");

            var authorityRequest = new PhotonCadManualAuthorityRequest(request, _command);
            var mutation = await _authority.ExecuteAsync(authorityRequest, cancellationToken).ConfigureAwait(false);
            RequireReturnedMutation(mutation);
            _authorityRequest = authorityRequest;
            _mutation = mutation;
            return mutation;
        }

        internal async ValueTask CompensateAsync(PhotonCadSealedMutationDelta mutation, string reason, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(reason) || reason.Length > 128 || reason.Any(char.IsControl))
                throw new ArgumentException("manual_compensation_reason_invalid", nameof(reason));
            if (!ReferenceEquals(mutation, _mutation) || _authorityRequest is null)
                throw Failure("manual_compensation_binding_mismatch");
            Interlocked.Exchange(ref _revoked, 1);
            if (_authority is not null)
                await _authority.CompensateAsync(_authorityRequest, mutation, reason, cancellationToken).ConfigureAwait(false);
        }

        private void RequireBound(PhotonCadSealedMutationProviderRequest request)
        {
            if (!ReferenceEquals(request.Request, _boundRequest)
                || request.Units != PhotonCadProjectUnit.Millimeter
                || request.Request.Mode != PhotonCadOperationModeV1.Scratch
                || request.Request.TargetEntityIds.Count != 1
                || !StringComparer.Ordinal.Equals(request.Request.TargetEntityIds[0], _command.TargetEntityId))
                throw Failure("manual_provider_request_not_bound");
            var exists = request.ExistingEntityIds.Contains(_command.TargetEntityId, StringComparer.OrdinalIgnoreCase);
            if (exists == _command.CreatesEntity) throw Failure("manual_target_existence_mismatch");
        }

        private void RequireReturnedMutation(PhotonCadSealedMutationDelta mutation)
        {
            if (mutation is null
                || !StringComparer.Ordinal.Equals(mutation.RequestId, _boundRequest.RequestId)
                || !StringComparer.Ordinal.Equals(mutation.SessionId, _boundRequest.SessionId)
                || !StringComparer.Ordinal.Equals(mutation.ProjectId, _boundRequest.ProjectId)
                || mutation.BaseRevision != _boundRequest.BaseRevision
                || mutation.ResultingRevision != checked(_boundRequest.BaseRevision + 2)
                || mutation.Operations.Count != 2
                || !StringComparer.Ordinal.Equals(mutation.Operations[0].CapabilityId, _command.CapabilityId)
                || !StringComparer.Ordinal.Equals(mutation.Operations[1].CapabilityId, "industrial.preview.glb.v1")
                || !mutation.Operations[0].TargetEntityIds.SequenceEqual([_command.TargetEntityId], StringComparer.Ordinal))
                throw Failure("manual_authority_mutation_not_bound");
        }

        private static InvalidOperationException Failure(string code) => new(code);
    }

    private sealed class ManualCompensator : IPhotonCadSealedMutationCompensator
    {
        private readonly ManualProvider _provider;

        public ManualCompensator(ManualProvider provider) => _provider = provider;

        public ValueTask CompensateAsync(PhotonCadSealedMutationDelta mutation, string reason, CancellationToken cancellationToken = default) =>
            _provider.CompensateAsync(mutation, reason, cancellationToken);
    }
}
