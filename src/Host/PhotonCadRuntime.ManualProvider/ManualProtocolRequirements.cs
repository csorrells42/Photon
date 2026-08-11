namespace PhotonCadRuntime.ManualProvider;

/// <summary>
/// Exact container protocol work required before this provider can be mounted. Kept in code so a
/// host cannot mistake the unavailable catalog for a renderer-only feature toggle.
/// </summary>
public static class PhotonCadManualProtocolRequirements
{
    public const string ProtocolId = "photon.cad.manual.geometry.protocol.v1";

    public static IReadOnlyList<string> RequiredOperations { get; } =
    [
        "sketchExtrudeAdd: accepts bounded plane/profile/depth and emits authoritative STEP plus complete GLB",
        "sketchExtrudeCut: accepts a trusted target STEP and emits replacement STEP plus complete GLB",
        "holeCut: accepts a trusted target STEP and emits replacement STEP plus complete GLB",
        "fillet: accepts selected stable edge ids and emits replacement STEP plus complete GLB",
        "chamfer: accepts selected stable edge ids and emits replacement STEP plus complete GLB",
        "linearPattern: accepts trusted seed ids and emits replacement STEP plus complete GLB",
        "circularPattern: accepts trusted seed ids and emits replacement STEP plus complete GLB",
        "every response: exact source STEP digest/length, sealed result STEP/GLB digest/length, occurrence DAG, BOM, and replacement digest CAS",
    ];
}
