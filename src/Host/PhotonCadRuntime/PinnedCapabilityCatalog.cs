namespace PhotonCadRuntime;

public static class CadPinnedCapabilityCatalog
{
    public const string BoxCapabilityId = "geometry.box.create.v1";
    public const string CylinderCapabilityId = "geometry.cylinder.create.v1";
    public const string MeasureCapabilityId = "geometry.measure.v1";
    public const string ValidateCapabilityId = "geometry.validate.v1";
    public const string ExportStepCapabilityId = "geometry.step.export.v1";
    public const string Revision = "b123dmcp-0.3.80-minimal-v1";

    public static CadCapabilityCatalog Create(DateTimeOffset generatedAtUtc)
    {
        var source = new CadSourceIdentity(
            "build123d-mcp",
            CadGeometryMcpClient.RuntimeVersion,
            CadDockerRuntimeIdentity.GeometrySourceArchiveSha256,
            "Apache-2.0");
        var length = NumberParameter("length", "Length", "Box length in the project length unit.", 10);
        var width = NumberParameter("width", "Width", "Box width in the project length unit.", 10);
        var height = NumberParameter("height", "Height", "Solid height in the project length unit.", 10);
        var radius = NumberParameter("radius", "Radius", "Cylinder radius in the project length unit.", 5);
        var capabilities = new[]
        {
            new CadCapability(
                BoxCapabilityId,
                CadBackend.Geometry,
                "Primitive solids",
                "Box",
                "Create one exact B-rep box from bounded numeric dimensions.",
                CadCapabilityOperationKind.Create,
                [length, width, height],
                source,
                previewSupported: false,
                experimental: false),
            new CadCapability(
                CylinderCapabilityId,
                CadBackend.Geometry,
                "Primitive solids",
                "Cylinder",
                "Create one exact B-rep cylinder from bounded radius and height.",
                CadCapabilityOperationKind.Create,
                [radius, height],
                source,
                previewSupported: false,
                experimental: false),
            new CadCapability(
                MeasureCapabilityId,
                CadBackend.Geometry,
                "Inspection",
                "Measure solid",
                "Measure one host-owned solid selected by opaque entity identifier.",
                CadCapabilityOperationKind.Measure,
                [],
                source,
                previewSupported: false,
                experimental: false),
            new CadCapability(
                ValidateCapabilityId,
                CadBackend.Geometry,
                "Inspection",
                "Validate solid",
                "Run the pinned geometry validator for one host-owned solid.",
                CadCapabilityOperationKind.Validate,
                [],
                source,
                previewSupported: false,
                experimental: false),
            new CadCapability(
                ExportStepCapabilityId,
                CadBackend.Geometry,
                "Interchange",
                "Export STEP copy",
                "Create a new workspace-contained STEP artifact without changing the source solid. The v1 UI operation vocabulary classifies artifact production as modify.",
                CadCapabilityOperationKind.Modify,
                [],
                source,
                previewSupported: false,
                experimental: false),
        };
        return new CadCapabilityCatalog(
            Revision,
            generatedAtUtc,
            capabilities,
            new CadCatalogCoverage(
                discovered: 6,
                available: 5,
                unavailable: 1,
                ["PartCAD is verified for health and policy only; no assembly operation is mapped in this minimal host."]));
    }

    private static CadParameterDefinition NumberParameter(
        string id,
        string label,
        string description,
        double defaultValue) => new(
            id,
            label,
            description,
            CadParameterKind.Number,
            required: true,
            CadParameterUnit.Length,
            minimum: 0.001,
            maximum: 1_000_000,
            step: 0.001,
            defaultValue: new CadNumberInputValue(defaultValue));
}
