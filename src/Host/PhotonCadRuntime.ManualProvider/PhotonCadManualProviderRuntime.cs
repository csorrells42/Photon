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
            new ManualSketchParameters("rectangle", "xy", profileWidthMm, profileHeightMm, 0, extrusionDepthMm, 0, 0, 0, []),
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
            new ManualSketchParameters("circle", "xy", 0, 0, profileRadiusMm, extrusionDepthMm, 0, 0, 0, []),
            [
                ProfileKind("circle"), Choice("sketchPlane", plane), Number("profileRadiusMm", profileRadiusMm),
                Number("extrusionDepthMm", extrusionDepthMm),
            ]);

    public PhotonCadManualBoundMutation BindSketchExtrudeCut(
        string requestId, string sessionId, string projectId, long baseRevision, string targetEntityId,
        string plane, double profileWidthMm, double profileHeightMm, double cutDepthMm) => Bind(
            PhotonCadManualOperationKind.SketchExtrudeCut,
            PhotonCadManualCapabilityIds.SketchExtrudeCut,
            requestId, sessionId, projectId, baseRevision, targetEntityId, NewFeatureId(), createsEntity: false,
            new ManualSketchParameters("rectangle", "xy", profileWidthMm, profileHeightMm, 0, cutDepthMm, 0, 0, 0, []),
            [
                ProfileKind("rectangle"), Choice("sketchPlane", plane), Number("profileWidthMm", profileWidthMm),
                Number("profileHeightMm", profileHeightMm), Number("cutDepthMm", cutDepthMm),
            ]);

    public PhotonCadManualBoundMutation BindMouseSketchExtrudeAdd(
        string requestId, string sessionId, string projectId, long baseRevision, string targetEntityId,
        PhotonCadManualMouseSketch sketch, double extrusionDepthMm, string sketchInput, bool joinsExistingSolid = false)
    {
        ValidateMouseSketch(sketch);
        return Bind(
            PhotonCadManualOperationKind.SketchExtrudeAdd,
            PhotonCadManualCapabilityIds.MouseSketchExtrudeAdd,
            requestId, sessionId, projectId, baseRevision, targetEntityId,
            joinsExistingSolid ? NewFeatureId() : null, createsEntity: !joinsExistingSolid,
            new ManualMouseSketchParameters(sketch, extrusionDepthMm),
            [Text("sketch", sketchInput), Number("extrusionDepthMm", extrusionDepthMm)]);
    }

    public PhotonCadManualBoundMutation BindMouseSketchExtrudeCut(
        string requestId, string sessionId, string projectId, long baseRevision, string targetEntityId,
        PhotonCadManualMouseSketch sketch, double cutDepthMm, string sketchInput)
    {
        ValidateMouseSketch(sketch);
        return Bind(
            PhotonCadManualOperationKind.SketchExtrudeCut,
            PhotonCadManualCapabilityIds.MouseSketchExtrudeCut,
            requestId, sessionId, projectId, baseRevision, targetEntityId, NewFeatureId(), createsEntity: false,
            new ManualMouseSketchParameters(sketch, cutDepthMm),
            [Text("sketch", sketchInput), Number("cutDepthMm", cutDepthMm)]);
    }

    public PhotonCadManualBoundMutation BindHoleCut(
        string requestId, string sessionId, string projectId, long baseRevision, string targetEntityId,
        double diameterMm, double depthMm, double xMm, double yMm, double zMm) => Bind(
            PhotonCadManualOperationKind.HoleCut,
            PhotonCadManualCapabilityIds.HoleCut,
            requestId, sessionId, projectId, baseRevision, targetEntityId, NewFeatureId(), createsEntity: false,
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
            requestId, sessionId, projectId, baseRevision, targetEntityId, NewFeatureId(), createsEntity: false,
            new ManualPatternParameters(seedEntityId, count, spacingMm, 0),
            [Entity("seedFeatureId", seedEntityId), Count(count), Number("spacingMm", spacingMm)]);

    public PhotonCadManualBoundMutation BindCircularPattern(
        string requestId, string sessionId, string projectId, long baseRevision, string targetEntityId,
        string seedEntityId, long count, double angleDegrees) => Bind(
            PhotonCadManualOperationKind.CircularPattern, PhotonCadManualCapabilityIds.CircularPattern,
            requestId, sessionId, projectId, baseRevision, targetEntityId, NewFeatureId(), createsEntity: false,
            new ManualPatternParameters(seedEntityId, count, 0, angleDegrees),
            [Entity("seedFeatureId", seedEntityId), Count(count), Number("angleDegrees", angleDegrees)]);

    private PhotonCadManualBoundMutation BindEdges(
        PhotonCadManualOperationKind kind, string capabilityId,
        string requestId, string sessionId, string projectId, long baseRevision, string targetEntityId,
        double lengthMm, IEnumerable<string> edgeIds, string lengthId)
    {
        if (edgeIds is null) throw new ArgumentNullException(nameof(edgeIds));
        var copiedEdges = edgeIds.ToArray();
        if (copiedEdges.Length is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(edgeIds));
        return Bind(kind, capabilityId, requestId, sessionId, projectId, baseRevision, targetEntityId, NewFeatureId(), createsEntity: false,
            new ManualPatternParameters(copiedEdges[0], 0, lengthMm, 0),
            [Number(lengthId, lengthMm), new PhotonCadSyncOperationInput("edgeIds", PhotonCadSyncInputValue.EntityList(copiedEdges))]);
    }

    private PhotonCadManualBoundMutation Bind(
        PhotonCadManualOperationKind kind, string capabilityId,
        string requestId, string sessionId, string projectId, long baseRevision, string targetEntityId,
        bool createsEntity, ManualExecutionParameters execution, IReadOnlyList<PhotonCadSyncOperationInput> inputs) =>
        Bind(kind, capabilityId, requestId, sessionId, projectId, baseRevision, targetEntityId,
            featureEntityId: null, createsEntity, execution, inputs);

    private PhotonCadManualBoundMutation Bind(
        PhotonCadManualOperationKind kind, string capabilityId,
        string requestId, string sessionId, string projectId, long baseRevision, string targetEntityId,
        string? featureEntityId, bool createsEntity, ManualExecutionParameters execution, IReadOnlyList<PhotonCadSyncOperationInput> inputs)
    {
        var command = new PhotonCadManualCommand(kind, capabilityId, targetEntityId, featureEntityId, createsEntity, execution, inputs);
        var targets = featureEntityId is null ? [targetEntityId] : new[] { targetEntityId, featureEntityId };
        var request = new PhotonCadRuntimeSyncRequest(
            requestId, sessionId, projectId, baseRevision, capabilityId, PhotonCadOperationModeV1.Scratch, inputs, targets);
        var provider = new ManualProvider(request, command, _authority);
        return new PhotonCadManualBoundMutation(request, provider, new ManualCompensator(provider));
    }

    private static string NewFeatureId() => $"feature-{Guid.NewGuid():N}";

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

    private static PhotonCadSyncOperationInput Text(string id, string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8_192 || value.Any(char.IsControl))
            throw new ArgumentException("manual_sketch_text_invalid", nameof(value));
        return new PhotonCadSyncOperationInput(id, PhotonCadSyncInputValue.Text(value));
    }

    /// <summary>
    /// Checks the renderer-independent sketch packet before it can enter the sealed geometry
    /// authority. The container repeats these checks; keeping them here prevents malformed mouse
    /// input from becoming a persisted operation request or a costly container invocation.
    /// </summary>
    private static void ValidateMouseSketch(PhotonCadManualMouseSketch sketch)
    {
        ArgumentNullException.ThrowIfNull(sketch);
        ArgumentNullException.ThrowIfNull(sketch.Points);
        ArgumentNullException.ThrowIfNull(sketch.CornerRadiiMm);
        if (sketch.ProfileKind is not ("rectangle" or "circle" or "polygon" or "filletedPolygon"))
            throw new ArgumentException("manual_mouse_sketch_profile_invalid", nameof(sketch));
        if (!FiniteBounded(sketch.OriginXMm) || !FiniteBounded(sketch.OriginYMm) || !FiniteBounded(sketch.OriginZMm)
            || !IsUnitVector(sketch.XDirectionX, sketch.XDirectionY, sketch.XDirectionZ)
            || !IsUnitVector(sketch.NormalX, sketch.NormalY, sketch.NormalZ)
            || Math.Abs(sketch.XDirectionX * sketch.NormalX
                + sketch.XDirectionY * sketch.NormalY
                + sketch.XDirectionZ * sketch.NormalZ) > 1e-8)
            throw new ArgumentException("manual_mouse_sketch_frame_invalid", nameof(sketch));

        var expectedPoints = sketch.ProfileKind is "rectangle" or "circle" ? 2 : -1;
        if ((expectedPoints >= 0 && sketch.Points.Count != expectedPoints)
            || (expectedPoints < 0 && sketch.Points.Count is < 3 or > 64)
            || sketch.Points.Any(point => !FiniteBounded(point.XMm) || !FiniteBounded(point.YMm))
            || sketch.Points.Distinct().Count() != sketch.Points.Count)
            throw new ArgumentException("manual_mouse_sketch_points_invalid", nameof(sketch));

        if (sketch.ProfileKind == "rectangle"
            && (BitConverter.DoubleToInt64Bits(sketch.Points[0].XMm) == BitConverter.DoubleToInt64Bits(sketch.Points[1].XMm)
                || BitConverter.DoubleToInt64Bits(sketch.Points[0].YMm) == BitConverter.DoubleToInt64Bits(sketch.Points[1].YMm)))
            throw new ArgumentException("manual_mouse_sketch_rectangle_invalid", nameof(sketch));
        if (sketch.ProfileKind == "circle"
            && Math.Sqrt(Math.Pow(sketch.Points[1].XMm - sketch.Points[0].XMm, 2)
                + Math.Pow(sketch.Points[1].YMm - sketch.Points[0].YMm, 2)) <= double.Epsilon)
            throw new ArgumentException("manual_mouse_sketch_circle_invalid", nameof(sketch));
        if (sketch.ProfileKind is "polygon" or "filletedPolygon"
            && Math.Abs(SignedAreaTwice(sketch.Points)) <= 1e-8)
            throw new ArgumentException("manual_mouse_sketch_polygon_invalid", nameof(sketch));

        if (sketch.ProfileKind == "filletedPolygon")
        {
            if (sketch.CornerRadiiMm.Count != sketch.Points.Count
                || sketch.CornerRadiiMm.Any(radius => !double.IsFinite(radius) || radius < 0 || radius > MaximumDimensionMm)
                || !sketch.CornerRadiiMm.Any(radius => radius > 0))
                throw new ArgumentException("manual_mouse_sketch_corner_radii_invalid", nameof(sketch));
        }
        else if (sketch.CornerRadiiMm.Count != 0)
        {
            throw new ArgumentException("manual_mouse_sketch_corner_radii_unexpected", nameof(sketch));
        }
    }

    private static bool FiniteBounded(double value) =>
        double.IsFinite(value) && value >= -MaximumDimensionMm && value <= MaximumDimensionMm;

    private static bool IsUnitVector(double x, double y, double z) =>
        FiniteBounded(x) && FiniteBounded(y) && FiniteBounded(z)
        && Math.Abs(Math.Sqrt(x * x + y * y + z * z) - 1) <= 1e-6;

    private static double SignedAreaTwice(IReadOnlyList<ManualSketchPoint> points)
    {
        var area = 0d;
        for (var index = 0; index < points.Count; index++)
        {
            var next = points[(index + 1) % points.Count];
            area += points[index].XMm * next.YMm - next.XMm * points[index].YMm;
        }
        return area;
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
                or PhotonCadManualOperationKind.LinearPattern
                or PhotonCadManualOperationKind.CircularPattern
                ? PhotonCadManualAvailability.Available
                : PhotonCadManualAvailability.Unavailable,
            authorityInstalled
                ? kind is PhotonCadManualOperationKind.SketchExtrudeAdd
                    or PhotonCadManualOperationKind.SketchExtrudeCut
                    or PhotonCadManualOperationKind.HoleCut
                    or PhotonCadManualOperationKind.LinearPattern
                    or PhotonCadManualOperationKind.CircularPattern
                    ? null
                    : operationUnavailable
                : authorityUnavailable);
        return
        [
            Entry(PhotonCadManualCapabilityIds.SketchExtrudeAdd, PhotonCadManualOperationKind.SketchExtrudeAdd, "Sketch + extrude", "Create a bounded rectangle or circle profile on a principal sketch plane and add one solid."),
            Entry(PhotonCadManualCapabilityIds.SketchExtrudeCut, PhotonCadManualOperationKind.SketchExtrudeCut, "Sketch cut", "Cut a bounded rectangular profile through one existing solid."),
            Entry(PhotonCadManualCapabilityIds.MouseSketchExtrudeAdd, PhotonCadManualOperationKind.SketchExtrudeAdd, "Mouse sketch + extrude", "Create a line-loop, circle, or box on a datum plane or model face and add one solid."),
            Entry(PhotonCadManualCapabilityIds.MouseSketchExtrudeCut, PhotonCadManualOperationKind.SketchExtrudeCut, "Mouse sketch cut", "Create a line-loop, circle, or box on a model face and cut one existing solid."),
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
                || request.Request.TargetEntityIds.Count != (_command.FeatureEntityId is null ? 1 : 2)
                || !StringComparer.Ordinal.Equals(request.Request.TargetEntityIds[0], _command.TargetEntityId)
                || _command.FeatureEntityId is not null
                    && !StringComparer.Ordinal.Equals(request.Request.TargetEntityIds[1], _command.FeatureEntityId))
                throw Failure("manual_provider_request_not_bound");
            var exists = request.ExistingEntityIds.Contains(_command.TargetEntityId, StringComparer.OrdinalIgnoreCase);
            if (exists == _command.CreatesEntity) throw Failure("manual_target_existence_mismatch");
            if (_command.FeatureEntityId is not null
                && request.ExistingEntityIds.Contains(_command.FeatureEntityId, StringComparer.OrdinalIgnoreCase))
                throw Failure("manual_feature_identity_collision");
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
                || !mutation.Operations[0].TargetEntityIds.SequenceEqual(_boundRequest.TargetEntityIds, StringComparer.Ordinal))
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
