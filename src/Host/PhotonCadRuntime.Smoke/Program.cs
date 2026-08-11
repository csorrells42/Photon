using System.Diagnostics;
using System.Text.Json;
using PhotonCadRuntime;

var suite = new SmokeSuite();

await suite.RunAsync("contract versions and dynamic capability metadata align", () =>
{
    Equal(1, CadContractVersions.Host, "host version");
    Equal(1, CadContractVersions.Interchange, "interchange version");

    var source = Source("geometry-runtime");
    var capability = new CadCapability(
        "spur_gear",
        CadBackend.Geometry,
        "power transmission",
        "Spur gear",
        "Creates a bounded parametric spur gear.",
        CadCapabilityOperationKind.Create,
        [
            new CadParameterDefinition(
                "module", "Module", "Gear module.", CadParameterKind.Number, true,
                CadParameterUnit.Length, 0.1, 100, 0.1, new CadNumberInputValue(2)),
            new CadParameterDefinition(
                "material", "Material", "Material choice.", CadParameterKind.Choice, false,
                defaultValue: new CadTextInputValue("steel", CadParameterKind.Choice),
                choices: [new CadChoice("steel", "Steel"), new CadChoice("bronze", "Bronze")]),
        ],
        source,
        previewSupported: true,
        experimental: false);
    var catalog = new CadCapabilityCatalog(
        "catalog.v1",
        DateTimeOffset.UtcNow,
        [capability],
        new CadCatalogCoverage(1, 1, 0));

    Equal(1, catalog.ContractVersion, "catalog contract version");
    Equal(CadBackend.Geometry, catalog.Capabilities.Single().Backend, "backend");
    Equal("geometry-runtime", catalog.Capabilities.Single().Source.Package, "source package");
    Equal(2, catalog.Capabilities.Single().Parameters.Count, "typed parameters");
    True(catalog.Capabilities.Single().PreviewSupported, "preview support");
    using (var wire = JsonDocument.Parse(CadWireJson.SerializeCatalog(catalog)))
    {
        var root = wire.RootElement;
        Equal(1, root.GetProperty("contractVersion").GetInt32(), "wire catalog version");
        Equal("geometry", root.GetProperty("capabilities")[0].GetProperty("backend").GetString(), "wire backend");
        Equal("create", root.GetProperty("capabilities")[0].GetProperty("operation").GetString(), "wire operation");
        Equal("number", root.GetProperty("capabilities")[0].GetProperty("parameters")[0].GetProperty("kind").GetString(), "wire parameter kind");
        Equal(JsonValueKind.Array, root.GetProperty("capabilities")[0].GetProperty("parameters")[0].GetProperty("choices").ValueKind, "wire empty choices array");
        Equal(0, root.GetProperty("capabilities")[0].GetProperty("parameters")[0].GetProperty("choices").GetArrayLength(), "wire empty choices count");
        True(root.GetProperty("generatedAtUtc").GetString()!.EndsWith('Z'), "wire UTC timestamp");
    }
    using (var pinnedWire = JsonDocument.Parse(CadWireJson.SerializeCatalog(CadPinnedCapabilityCatalog.Create(DateTimeOffset.UtcNow))))
    {
        foreach (var pinnedCapability in pinnedWire.RootElement.GetProperty("capabilities").EnumerateArray())
        foreach (var parameter in pinnedCapability.GetProperty("parameters").EnumerateArray())
            Equal(JsonValueKind.Array, parameter.GetProperty("choices").ValueKind, "pinned catalog choices are always arrays");
    }
    ThrowsCode(() => new CadCatalogCoverage(2, 1, 0), "dishonest_catalog_coverage");
    ThrowsCode(() => new CadCapabilityCatalog(
        "catalog.invalid", DateTimeOffset.UtcNow, [capability, null!], new CadCatalogCoverage(2, 2, 0)),
        "null_collection_item");
    ThrowsCode(() => new CadParameterDefinition(
        "bad", "Bad", "Bad default.", CadParameterKind.Integer, true,
        defaultValue: new CadTextInputValue("wrong")), "default_kind_mismatch");
    return Task.CompletedTask;
});

await suite.RunAsync("opaque handles cannot carry paths", () =>
{
    Equal("commercial-review:1", new CadRequestId("commercial-review:1").Value, "renderer request ID");
    True(CadWorkspaceHandle.New().Value.StartsWith("wsp_", StringComparison.Ordinal), "workspace handle");
    True(CadProjectHandle.New().Value.StartsWith("prj_", StringComparison.Ordinal), "project handle");
    True(CadSessionHandle.New().Value.StartsWith("ses_", StringComparison.Ordinal), "session handle");
    True(CadArtifactHandle.New().Value.StartsWith("art_", StringComparison.Ordinal), "artifact handle");
    True(CadPreviewHandle.New().Value.StartsWith("prv_", StringComparison.Ordinal), "preview handle");
    ThrowsCode(() => new CadArtifactHandle("art_C:\\private\\gear.step"), "invalid_handle");
    ThrowsCode(() => new CadProjectHandle("prj_../../outside-workspace"), "invalid_handle");
    ThrowsCode(() => new CadRequestId("../outside-workspace"), "invalid_identifier");
    return Task.CompletedTask;
});

await suite.RunAsync("artifact receipts are bounded pathless and wire stable", () =>
{
    var session = CadSessionHandle.New();
    var artifact = CadArtifactHandle.New();
    var request = new CadArtifactReadRequest(
        CadRequestId.New(),
        session,
        artifact,
        CadContractLimits.MaximumBrokeredArtifactBytes);
    Equal(CadContractLimits.MaximumBrokeredArtifactBytes, request.MaximumByteLength, "maximum broker bound");
    var descriptor = new CadArtifactDescriptor(
        session,
        artifact,
        $"sha256:{new string('A', 64)}",
        4_096,
        "model/step");
    Equal(new string('a', 64), descriptor.ContentDigest, "canonical descriptor digest");
    using (var wire = JsonDocument.Parse(CadWireJson.SerializeArtifactDescriptor(descriptor)))
    {
        Equal(session.Value, wire.RootElement.GetProperty("sessionHandle").GetString(), "wire session handle");
        Equal(artifact.Value, wire.RootElement.GetProperty("artifactHandle").GetString(), "wire artifact handle");
        Equal(new string('a', 64), wire.RootElement.GetProperty("contentDigest").GetString(), "wire artifact digest");
        True(!wire.RootElement.EnumerateObject().Any(property =>
            property.Name.Contains("path", StringComparison.OrdinalIgnoreCase)), "wire contains no path");
    }
    ThrowsCode(() => new CadArtifactReadRequest(
        CadRequestId.New(), session, artifact, 0), "invalid_artifact_read_bound");
    ThrowsCode(() => new CadArtifactReadRequest(
        CadRequestId.New(), session, artifact, CadContractLimits.MaximumBrokeredArtifactBytes + 1),
        "invalid_artifact_read_bound");
    ThrowsCode(() => new CadArtifactDescriptor(
        session, artifact, new string('b', 64), CadContractLimits.MaximumBrokeredArtifactBytes + 1, "model/step"),
        "artifact_too_large_to_broker");
    True(typeof(CadArtifactDescriptor).GetProperties().All(property =>
        !property.Name.Contains("path", StringComparison.OrdinalIgnoreCase)), "descriptor exposes no path property");
    return Task.CompletedTask;
});

await suite.RunAsync("relative path policy rejects Windows escape forms", () =>
{
    Equal("parts/drive gear.step", new CadRelativePath("parts\\drive gear.step").Value, "canonical path");
    foreach (var candidate in new[]
    {
        "../gear.step", ".\\gear.step", "C:\\gear.step", "\\\\server\\share\\gear.step",
        "\\\\?\\C:\\gear.step", "gear.step:secret", "parts\\CON.step", "parts\\gear. ",
        "parts//gear.step",
    })
        Throws<CadContractException>(() => _ = new CadRelativePath(candidate), $"reject {candidate}");
    return Task.CompletedTask;
});

await suite.RunAsync("path inspection hooks reject links before use", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "PhotonCadRuntimeSmoke", "workspace");
    var safeInspector = new DelegatePathInspector(path =>
        path.EndsWith("gear.step", StringComparison.OrdinalIgnoreCase)
            ? new CadPathInspection(CadPathEntryKind.File, false)
            : new CadPathInspection(CadPathEntryKind.Directory, false));
    var resolved = CadPathPolicy.ResolveUnderTrustedRoot(root, new CadRelativePath("parts/gear.step"), safeInspector);
    True(resolved.AbsolutePath.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase), "contained path");

    var linkedInspector = new DelegatePathInspector(path => new CadPathInspection(
        path.EndsWith("linked", StringComparison.OrdinalIgnoreCase) ? CadPathEntryKind.Directory : CadPathEntryKind.Directory,
        path.EndsWith("linked", StringComparison.OrdinalIgnoreCase)));
    ThrowsCode(
        () => CadPathPolicy.ResolveUnderTrustedRoot(root, new CadRelativePath("linked/gear.step"), linkedInspector),
        "reparse_point_rejected");
    var hardLinkInspector = new DelegatePathInspector(path => path.EndsWith("gear.step", StringComparison.OrdinalIgnoreCase)
        ? new CadPathInspection(CadPathEntryKind.File, false, true)
        : new CadPathInspection(CadPathEntryKind.Directory, false));
    ThrowsCode(
        () => CadPathPolicy.ResolveUnderTrustedRoot(root, new CadRelativePath("parts/gear.step"), hardLinkInspector),
        "hard_link_rejected");
    return Task.CompletedTask;
});

await suite.RunAsync("bundle identities keep geometry and assembly separate", () =>
{
    var geometry = Bundle(CadRuntimeRole.Geometry, "geometry.bundle", 'a');
    var assembly = Bundle(CadRuntimeRole.Assembly, "assembly.bundle", 'b');
    var files = new[] { new CadBundleArtifact(new CadRelativePath("runtime/python.exe"), Hash('c'), 10) };
    var manifest = new CadRuntimeBundleManifest(geometry, files);
    files[0] = new CadBundleArtifact(new CadRelativePath("mutated.exe"), Hash('d'), 11);
    Equal("runtime/python.exe", manifest.Artifacts.Single().Path.Value, "defensive artifact copy");
    Equal(10L, manifest.TotalByteLength, "bundle size");

    var set = new CadRuntimeBundleSetIdentity(geometry, assembly, new CadRevision(1));
    Equal(CadRuntimeRole.Geometry, set.Geometry.Role, "geometry role");
    Equal(CadRuntimeRole.Assembly, set.Assembly.Role, "assembly role");
    ThrowsCode(() => new CadRuntimeBundleSetIdentity(assembly, geometry, new CadRevision(1)), "wrong_bundle_role");
    ThrowsCode(() => new CadRuntimeBundleManifest(geometry,
    [
        new CadBundleArtifact(new CadRelativePath("Runtime/a.dll"), Hash('e'), 1),
        new CadBundleArtifact(new CadRelativePath("runtime/A.dll"), Hash('f'), 1),
    ]), "duplicate_item");
    ThrowsCode(() => new CadRuntimeBundleManifest(geometry,
    [
        new CadBundleArtifact(new CadRelativePath("runtime/caf\u00e9.dll"), Hash('e'), 1),
        new CadBundleArtifact(new CadRelativePath("runtime/cafe\u0301.dll"), Hash('f'), 1),
    ]), "duplicate_item");
    return Task.CompletedTask;
});

await suite.RunAsync("session state gate serializes ownership and stays terminal", () =>
{
    var gate = new CadSessionStateGate(CadSessionHandle.New());
    Equal(CadSessionState.Opening, gate.BeginOpening().State, "opening");
    Equal(CadSessionState.Ready, gate.MarkReady().State, "ready");
    var owner = CadRequestId.New();
    Equal(CadSessionState.Busy, gate.BeginOperation(owner).State, "busy");
    ThrowsState(() => gate.BeginOperation(CadRequestId.New()), "operation_already_active");
    ThrowsState(() => gate.CompleteOperation(CadRequestId.New()), "operation_ownership_mismatch");
    Equal(CadSessionState.Ready, gate.CompleteOperation(owner).State, "operation complete");
    Equal(CadSessionState.Closing, gate.BeginClosing().State, "closing");
    Equal(CadSessionState.Closed, gate.MarkClosed().State, "closed");
    ThrowsState(() => gate.BeginOpening(), "invalid_state_transition");
    return Task.CompletedTask;
});

await suite.RunAsync("typed capability execution is bounded and revision-bound", () =>
{
    var session = CadSessionHandle.New();
    var project = CadProjectHandle.New();
    var revision = new CadRevision(7);
    var request = new CadOperationRequest(
        CadRequestId.New(),
        session,
        project,
        revision,
        CadOperationMode.Scratch,
        "spur_gear",
        [
            new CadOperationInput("module", new CadNumberInputValue(2.5)),
            new CadOperationInput("teeth", new CadIntegerInputValue(24)),
            new CadOperationInput("axis", new CadVectorInputValue(new CadVector3(0, 0, 1))),
        ],
        ["shaft_1"]);
    Equal(1, request.ContractVersion, "operation version");
    Equal(3, request.Inputs.Count, "input count");
    Equal(7L, request.BaseRevision.Value, "base revision");
    using (var wire = JsonDocument.Parse(CadWireJson.SerializeOperationRequest(request)))
    {
        var root = wire.RootElement;
        Equal("scratch", root.GetProperty("mode").GetString(), "wire operation mode");
        Equal(2.5d, root.GetProperty("inputs").GetProperty("module").GetDouble(), "wire numeric input");
        Equal(24L, root.GetProperty("inputs").GetProperty("teeth").GetInt64(), "wire integer input");
        Equal("shaft_1", root.GetProperty("targetEntityIds")[0].GetString(), "wire target ID");
    }

    var snapshot = new CadProjectSnapshot(
        session,
        project,
        revision,
        "Gearbox",
        CadLengthUnit.Millimeter,
        CadProjectMode.Scratch,
        [
            new CadProjectEntity("assembly:root", null, CadEntityKind.Assembly, "Gearbox", true, false),
            new CadProjectEntity("part:gear", "assembly:root", CadEntityKind.Part, "GEAR 42 / LH", true, false, "spur_gear"),
        ],
        [new CadProjectOperationRecord("operation:1", "spur_gear", "Create spur gear",
            new DateTimeOffset(2026, 8, 10, 18, 0, 0, TimeSpan.Zero), CadOperationRecordState.Proposed)],
        [],
        dirty: false);
    var result = new CadOperationResult(
        request.RequestId,
        project,
        revision,
        revision,
        CadOperationStatus.Accepted,
        stale: false,
        "accepted",
        snapshot: snapshot);
    using (var wire = JsonDocument.Parse(CadWireJson.SerializeOperationResult(result)))
    {
        var root = wire.RootElement;
        Equal("accepted", root.GetProperty("status").GetString(), "wire result status");
        Equal("assembly", root.GetProperty("snapshot").GetProperty("entities")[0].GetProperty("kind").GetString(),
            "wire entity kind");
        Equal(JsonValueKind.Null,
            root.GetProperty("snapshot").GetProperty("entities")[0].GetProperty("parentId").ValueKind,
            "wire root parent is explicit null");
        Equal("proposed", root.GetProperty("snapshot").GetProperty("operations")[0].GetProperty("state").GetString(),
            "wire operation state");
    }

    var verificationRequest = new CadVerificationRequest(
        CadRequestId.New(), session, project, revision,
        [CadVerificationCheck.ValidSolids, CadVerificationCheck.ExportReadiness]);
    using (var wire = JsonDocument.Parse(CadWireJson.SerializeVerificationRequest(verificationRequest)))
        Equal("valid-solids", wire.RootElement.GetProperty("checks")[0].GetString(), "wire verification check");
    var verificationResult = new CadVerificationResult(
        verificationRequest.RequestId,
        project,
        revision,
        CadVerificationStatus.Passed,
        stale: false,
        [],
        new DateTimeOffset(2026, 8, 10, 18, 1, 0, TimeSpan.Zero));
    using (var wire = JsonDocument.Parse(CadWireJson.SerializeVerificationResult(verificationResult)))
        Equal("passed", wire.RootElement.GetProperty("status").GetString(), "wire verification status");

    ThrowsCode(() => new CadProjectSnapshot(
        session, project, revision, "Cycle", CadLengthUnit.Millimeter, CadProjectMode.Scratch,
        [
            new CadProjectEntity("entity:a", "entity:b", CadEntityKind.Part, "A", true, false),
            new CadProjectEntity("entity:b", "entity:a", CadEntityKind.Part, "B", true, false),
        ], [], [], false), "entity_cycle");
    ThrowsCode(() => new CadOperationRequest(
        CadRequestId.New(), CadSessionHandle.New(), CadProjectHandle.New(), new CadRevision(0),
        CadOperationMode.Suggest, "gear",
        [
            new CadOperationInput("module", new CadNumberInputValue(1)),
            new CadOperationInput("module", new CadNumberInputValue(2)),
        ]), "duplicate_item");
    return Task.CompletedTask;
});

await suite.RunAsync("GLB preview receipt is opaque, bounded, and geometry-specific", () =>
{
    var preview = new CadPreviewDescriptor(
        CadPreviewHandle.New(),
        CadArtifactHandle.New(),
        CadProjectHandle.New(),
        new CadRevision(4),
        CadPreviewFormat.Glb,
        Hash('a'),
        4_096,
        CadLengthUnit.Millimeter,
        new CadBounds(new CadVector3(-10, -5, 0), new CadVector3(10, 5, 20)),
        entityCount: 3);
    Equal("model/gltf-binary", preview.MediaType, "GLB media type");
    True(preview.Width is null && preview.Height is null, "GLB has no raster dimensions");
    Equal(3, preview.EntityCount, "entity count");
    Equal(Hash('a'), preview.ContentDigest, "content digest");
    using (var wire = JsonDocument.Parse(CadWireJson.SerializePreview(preview)))
    {
        Equal(preview.Preview.Value, wire.RootElement.GetProperty("previewId").GetString(), "wire preview handle");
        Equal(4L, wire.RootElement.GetProperty("revision").GetInt64(), "wire preview revision");
        Equal("millimeter", wire.RootElement.GetProperty("units").GetString(), "wire preview units");
        Equal(-10d, wire.RootElement.GetProperty("bounds").GetProperty("minimum").GetProperty("x").GetDouble(), "wire preview bounds");
    }
    ThrowsCode(() => new CadBounds(new CadVector3(1, 0, 0), new CadVector3(0, 0, 0)), "invalid_bounds");
    return Task.CompletedTask;
});

await suite.RunAsync("STEP package preserves row-major local-to-parent assembly truth", () =>
{
    var files = new[]
    {
        new CadPackageFile(CadPackageFileRole.AssemblyStep, new CadRelativePath("assembly/gearbox.step"), Hash('1'), 1_000, "model/step"),
        new CadPackageFile(CadPackageFileRole.PartStep, new CadRelativePath("parts/bolt.step"), Hash('2'), 250, "model/step"),
        new CadPackageFile(CadPackageFileRole.Bom, new CadRelativePath("bom/bom.json"), Hash('3'), 100, "application/json"),
    };
    var occurrences = new[]
    {
        new CadAssemblyOccurrence("gearbox_root", null, "gearbox_assy", "gearbox_entity", CadRigidTransform.Identity),
        new CadAssemblyOccurrence("bolt_left", "gearbox_root", "bolt_m8", "bolt_entity", Translate(-25, 10, 0)),
        new CadAssemblyOccurrence("bolt_right", "gearbox_root", "bolt_m8", "bolt_entity", Translate(25, 10, 0)),
    };
    var profile = new CadStepInterchangeProfile("inventor-neutral-ap214", CadStepSchema.Ap214, CadLengthUnit.Millimeter);
    var manifest = new CadStepPackageManifest(
        "gearbox_release",
        Hash('4'),
        new CadInterchangeProject("gearbox_project", "Industrial gearbox", new CadRevision(12), CadLengthUnit.Millimeter),
        profile,
        DateTimeOffset.UtcNow,
        new CadPackageProvenance("1.0.0", Source("geometry-runtime"), Hash('5'), Source("assembly-runtime")),
        files,
        [new CadBomRow("M8 x 1.25 / Grade 8", "M8 bolt", 2, "each", "bolt_entity")],
        occurrences,
        new CadPackageValidation(CadValidationStatus.Passed,
            ["valid-solids", "assembly-structure", "export-readiness"], []),
        [new CadAcceptanceResult("autodesk-inventor", "inventor-2020", CadAcceptanceStatus.NotRun)]);

    Equal(1, manifest.SchemaVersion, "schema version");
    Equal("AP214", manifest.Profile.StepApplicationProtocol, "STEP protocol");
    Equal("right", manifest.Profile.CoordinateSystem.Handedness, "handedness");
    Equal("z", manifest.Profile.CoordinateSystem.UpAxis, "up axis");
    Equal("row-major", manifest.Profile.CoordinateSystem.MatrixOrder, "matrix order");
    Equal("column", manifest.Profile.CoordinateSystem.VectorConvention, "vector convention");
    Equal("local-to-parent", manifest.Profile.CoordinateSystem.TransformMeaning, "transform meaning");
    Equal(-25d, manifest.Occurrences[1].Transform.Values[3], "translation X index");
    Equal(10d, manifest.Occurrences[1].Transform.Values[7], "translation Y index");
    Equal(0d, manifest.Occurrences[1].Transform.Values[11], "translation Z index");
    Equal(2m, manifest.Bom.Single().Quantity, "BOM quantity");
    Equal("not-run", manifest.Acceptance.Single().Status, "optional sibling acceptance");

    using (var wire = JsonDocument.Parse(CadWireJson.SerializeInterchange(manifest)))
    {
        var root = wire.RootElement;
        Equal(1, root.GetProperty("schemaVersion").GetInt32(), "wire interchange version");
        Equal(12L, root.GetProperty("project").GetProperty("revision").GetInt64(), "wire project revision");
        Equal("row-major", root.GetProperty("profile").GetProperty("coordinateSystem").GetProperty("matrixOrder").GetString(), "wire matrix order");
        var transform = root.GetProperty("occurrences")[1].GetProperty("transform");
        Equal(JsonValueKind.Null, root.GetProperty("occurrences")[0].GetProperty("parentOccurrenceId").ValueKind,
            "wire root occurrence parent is explicit null");
        Equal(16, transform.GetArrayLength(), "wire transform length");
        Equal(-25d, transform[3].GetDouble(), "wire translation X index");
        Equal(10d, transform[7].GetDouble(), "wire translation Y index");
        Equal(0d, transform[11].GetDouble(), "wire translation Z index");
        Equal("assembly-step", root.GetProperty("files")[0].GetProperty("role").GetString(), "wire file role");
        True(!root.TryGetProperty("schemaId", out _), "wire omits internal schema ID");
    }

    files[0] = new CadPackageFile(CadPackageFileRole.PartStep, new CadRelativePath("changed.step"), Hash('6'), 1, "model/step");
    Equal("assembly/gearbox.step", manifest.Files[0].RelativePath.Value, "defensive package copy");

    ThrowsCode(() => new CadCoordinateSystem(handedness: "left"), "unsupported_coordinate_system");
    ThrowsCode(() => new CadRigidTransform(
    [
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        25, 0, 0, 1,
    ]), "invalid_affine_transform");
    return Task.CompletedTask;
});

await suite.RunAsync("commercial draft is bounded, geometry-neutral, and wire-compatible", () =>
{
    var draft = CommercialDraft(CadCommercialDocumentKind.Invoice);
    Equal(1, draft.ContractVersion, "commercial version");
    Equal("invoice", draft.DocumentKind, "document kind");
    True(draft.DraftOnly, "invoice remains a draft");
    Equal("not-sent", draft.DeliveryState, "delivery state");
    Equal("not-posted", draft.AccountingState, "accounting state");
    Equal("not-paid", draft.PaymentState, "payment state");
    Equal(new DateOnly(2026, 2, 28), CadCommercialWireValue.ParseIsoDate("2026-02-28", "issueDate"), "actual ISO date");
    Equal(TimeSpan.Zero, CadCommercialWireValue.ParseUtcInstant("2026-08-10T20:00:00.123Z", "expiresAtUtc").Offset,
        "actual UTC timestamp");
    Equal(27_500L, draft.Totals.LineSubtotalMinorUnits, "line subtotal");
    Equal(2_750L, draft.Totals.MarkupMinorUnits, "markup");
    Equal(21_273L, draft.Totals.TaxableSubtotalMinorUnits, "taxable subtotal");
    Equal(1_542L, draft.Totals.TaxMinorUnits, "tax");
    Equal(31_292L, draft.Totals.TotalMinorUnits, "total");
    Equal(draft.CommercialDraftFingerprint, CommercialDraft(CadCommercialDocumentKind.Invoice).CommercialDraftFingerprint,
        "canonical draft fingerprint");
    True(draft.CommercialDraftFingerprint != CommercialDraft(CadCommercialDocumentKind.Invoice, freightMinorUnits: 501).CommercialDraftFingerprint,
        "commercial edit changes fingerprint");

    using (var wire = JsonDocument.Parse(CadCommercialWireJson.SerializeDraft(draft)))
    {
        var root = wire.RootElement;
        Equal(1, root.GetProperty("contractVersion").GetInt32(), "wire commercial version");
        Equal("invoice", root.GetProperty("documentKind").GetString(), "wire commercial kind");
        Equal("project:gearbox", root.GetProperty("projectId").GetString(), "wire project ID");
        Equal(12L, root.GetProperty("projectRevision").GetInt64(), "wire project revision");
        Equal("USD", root.GetProperty("currency").GetString(), "wire currency");
        Equal(10_000L, root.GetProperty("lines")[0].GetProperty("unitPriceMinorUnits").GetInt64(), "wire minor units");
        True(!root.TryGetProperty("draftOnly", out _), "wire matches stable renderer draft shape");
    }

    ThrowsCode(() => new CadPrinterHandle("prt_bad"), "invalid_handle");
    ThrowsCode(() => CadCommercialWireValue.ParseIsoDate("2026-02-30", "issueDate"), "invalid_iso_date");
    ThrowsCode(() => CadCommercialWireValue.ParseUtcInstant("2026-08-10T20:00:00-05:00", "expiresAtUtc"),
        "invalid_utc_timestamp");
    ThrowsCode(() => new CadCommercialLine(
        "line:bad", null, "part:bad", "Bad", 1, CadCommercialUnit.Each,
        CadCommercialLimits.MaximumMinorUnits + 1, true), "invalid_minor_units");
    ThrowsCode(() => new CadCommercialLine(
        "line:bad-unit", null, "PART 12 / RH", "Bad unit", 1, (CadCommercialUnit)999, 1, true),
        "unsupported_enum_value");
    ThrowsCode(() => new CadStepInterchangeProfile((CadStepSchema)999, CadLengthUnit.Millimeter),
        "unsupported_enum_value");
    ThrowsCode(() => _ = CommercialDraft(CadCommercialDocumentKind.Quote, discountMinorUnits: 1_000_000),
        "discount_exceeds_subtotal");
    True(CadCommercialSafetyBoundary.ForbiddenActions.Contains("send"), "send remains forbidden");
    True(CadCommercialSafetyBoundary.ForbiddenActions.Contains("pay"), "payment remains forbidden");
    return Task.CompletedTask;
});

await suite.RunAsync("BOM exports support CSV XLSX PDF through single-use reviewed commits", () =>
{
    var now = new DateTimeOffset(2026, 8, 10, 19, 0, 0, TimeSpan.Zero);
    foreach (var format in new[] { CadBomExportFormat.Csv, CadBomExportFormat.Xlsx, CadBomExportFormat.Pdf })
    {
        var request = new CadBomExportReviewRequest(
            new CadCommercialRequestId($"bom-review:{format}"),
            new CadBomIdentity("project:gearbox", new CadRevision(12), Hash('b')),
            format,
            CadExportDestinationHandle.New());
        var receipt = new CadBomExportReviewReceipt(
            request,
            CadBomExportReviewHandle.New(),
            [
                new CadBomExportFile(false, new CadRelativePath($"bom/gearbox.{request.FormatName}")),
                new CadBomExportFile(true, new CadRelativePath("bom/validation.json")),
            ],
            now,
            now.AddMinutes(10));
        var gate = new CadBomExportGate(receipt);

        using (var wire = JsonDocument.Parse(CadCommercialWireJson.SerializeBomReviewRequest(request)))
        {
            Equal(request.FormatName, wire.RootElement.GetProperty("format").GetString(), "wire BOM format");
            True(wire.RootElement.GetProperty("destinationHandle").GetString()!.StartsWith("cad-destination:", StringComparison.Ordinal),
                "wire opaque destination");
        }
        using (var wire = JsonDocument.Parse(CadCommercialWireJson.SerializeBomReviewResult(receipt)))
        {
            Equal("ready", wire.RootElement.GetProperty("status").GetString(), "wire BOM review status");
            Equal("bom", wire.RootElement.GetProperty("files")[0].GetProperty("role").GetString(), "wire BOM role");
        }

        var commit = new CadBomExportCommitRequest(
            new CadCommercialRequestId($"bom-commit:{format}"),
            receipt.Review,
            receipt.ExportFingerprint);
        gate.Authorize(commit, now.AddMinutes(1));
        Equal(CadCommercialFlowState.Consumed, gate.State, "BOM review consumed");
        ThrowsCommercial(() => gate.Authorize(commit, now.AddMinutes(2)), "bom_review_not_available");
    }

    var mismatchRequest = new CadBomExportReviewRequest(
        new CadCommercialRequestId("bom-review:mismatch"),
        new CadBomIdentity("project:gearbox", new CadRevision(12), Hash('b')),
        CadBomExportFormat.Pdf,
        CadExportDestinationHandle.New());
    ThrowsCode(() => new CadBomExportReviewReceipt(
        mismatchRequest,
        CadBomExportReviewHandle.New(),
        [new CadBomExportFile(false, new CadRelativePath("bom/gearbox.xlsx"))],
        now,
        now.AddMinutes(10)), "bom_export_file_mismatch");
    var mismatchReceipt = new CadBomExportReviewReceipt(
        mismatchRequest,
        CadBomExportReviewHandle.New(),
        [new CadBomExportFile(false, new CadRelativePath("bom/gearbox.pdf"))],
        now,
        now.AddMinutes(10));
    ThrowsCommercial(
        () => new CadBomExportGate(mismatchReceipt).Authorize(new CadBomExportCommitRequest(
            new CadCommercialRequestId("bom-commit:mismatch"), mismatchReceipt.Review, Hash('f')), now.AddMinutes(1)),
        "bom_review_binding_mismatch");
    return Task.CompletedTask;
});

await suite.RunAsync("quote and invoice output requires complete preview and explicit bound approval", () =>
{
    var now = new DateTimeOffset(2026, 8, 10, 20, 0, 0, TimeSpan.Zero);
    var request = new CadCommercialReviewRequest(
        new CadCommercialRequestId("commercial-review:1"),
        CommercialDraft(CadCommercialDocumentKind.Quote),
        CadCommercialOutputAction.Print(CadPrinterHandle.New(), 2));
    var binding = new CadCommercialReviewBinding(
        request,
        CadCommercialReviewHandle.New(),
        now,
        now.AddMinutes(10));
    var gate = new CadCommercialFlowGate(binding);
    var pageOne = new CadCommercialPreviewPage(1, CadDocumentPageHandle.New(), Hash('1'));
    var pageTwo = new CadCommercialPreviewPage(2, CadDocumentPageHandle.New(), Hash('2'));
    var approvalRequest = new CadCommercialApprovalRequest(
        new CadCommercialRequestId("commercial-approve:1"),
        binding.Review,
        Hash('d'),
        binding.ActionFingerprint,
        [pageOne.ContentDigest, pageTwo.ContentDigest]);

    ThrowsCommercial(
        () => gate.RecordExplicitHumanApproval(
            approvalRequest, CadCommercialApprovalHandle.New(), true, now.AddMinutes(1), now.AddMinutes(5)),
        "complete_preview_required");
    ThrowsCode(() => new CadRenderedCommercialReview(
        binding,
        Hash('d'),
        [pageOne, new CadCommercialPreviewPage(3, CadDocumentPageHandle.New(), Hash('3'))],
        now.AddMinutes(1)), "incomplete_page_preview");

    var rendered = new CadRenderedCommercialReview(binding, Hash('d'), [pageOne, pageTwo], now.AddMinutes(1));
    gate.AttachCompletePreview(rendered, now.AddMinutes(1));
    Equal(CadCommercialFlowState.Previewed, gate.State, "complete preview attached");
    ThrowsCommercial(
        () => gate.RecordExplicitHumanApproval(
            approvalRequest, CadCommercialApprovalHandle.New(), false, now.AddMinutes(2), now.AddMinutes(5)),
        "explicit_human_approval_required");
    var partialApproval = new CadCommercialApprovalRequest(
        new CadCommercialRequestId("commercial-approve:partial"),
        binding.Review,
        rendered.DocumentFingerprint,
        binding.ActionFingerprint,
        [pageOne.ContentDigest]);
    ThrowsCommercial(
        () => gate.RecordExplicitHumanApproval(
            partialApproval, CadCommercialApprovalHandle.New(), true, now.AddMinutes(2), now.AddMinutes(5)),
        "commercial_approval_binding_mismatch");

    var approval = gate.RecordExplicitHumanApproval(
        approvalRequest,
        CadCommercialApprovalHandle.New(),
        true,
        now.AddMinutes(2),
        now.AddMinutes(5));
    Equal(request.Draft.CommercialDraftFingerprint, approval.DraftFingerprint, "approval binds draft fingerprint");
    Equal(rendered.PageSetDigest, approval.PageSetDigest, "approval binds complete page set");
    Equal(rendered.DocumentFingerprint, approval.DocumentFingerprint, "approval binds document digest");

    using (var wire = JsonDocument.Parse(CadCommercialWireJson.SerializeReviewRequest(request)))
    {
        var action = wire.RootElement.GetProperty("action");
        Equal("print", action.GetProperty("kind").GetString(), "wire print action");
        Equal(2, action.GetProperty("copies").GetInt32(), "wire print copies");
        True(action.GetProperty("printerHandle").GetString()!.StartsWith("cad-printer:", StringComparison.Ordinal),
            "wire opaque printer");
    }
    using (var wire = JsonDocument.Parse(CadCommercialWireJson.SerializeReviewResult(rendered)))
    {
        var root = wire.RootElement;
        Equal("ready", root.GetProperty("status").GetString(), "wire commercial review status");
        Equal(2, root.GetProperty("pages").GetArrayLength(), "wire all pages");
        Equal(Hash('d'), root.GetProperty("documentFingerprint").GetString(), "wire document digest");
        Equal(31_292L, root.GetProperty("totals").GetProperty("totalMinorUnits").GetInt64(), "wire totals");
    }
    using (var wire = JsonDocument.Parse(CadCommercialWireJson.SerializeApprovalResult(approval)))
        True(wire.RootElement.GetProperty("approvalHandle").GetString()!.StartsWith("cad-commercial-approval:", StringComparison.Ordinal),
            "wire approval handle");

    var commit = new CadCommercialCommitRequest(
        new CadCommercialRequestId("commercial-commit:1"),
        approval.Approval,
        approval.DocumentFingerprint,
        approval.ActionFingerprint);
    gate.AuthorizeOutput(commit, now.AddMinutes(3));
    Equal(CadCommercialFlowState.Consumed, gate.State, "approval is single use");
    ThrowsCommercial(() => gate.AuthorizeOutput(commit, now.AddMinutes(4)), "human_approval_required");
    True(typeof(CadCommercialCommitRequest).GetConstructors().All(constructor =>
        constructor.GetParameters().All(parameter => parameter.ParameterType != typeof(CadCommercialReviewHandle))),
        "no review-handle-to-output constructor");
    return Task.CompletedTask;
});

await suite.RunAsync("unavailable broker never pretends a runtime exists", async () =>
{
    ICadRuntimeBroker broker = new UnavailableCadRuntimeBroker();
    var description = broker.Describe();
    Equal(1, description.ContractVersion, "description version");
    Equal("unavailable", description.Status, "description status");
    Equal("verified_runtime_bundles_missing", description.Reason, "description reason");
    True(description.ActiveBundles is null && description.Catalog is null, "no unverified runtime identity");
    using (var wire = JsonDocument.Parse(CadWireJson.SerializeDescription(description)))
    {
        Equal("unavailable", wire.RootElement.GetProperty("status").GetString(), "wire unavailable status");
        Equal("verified_runtime_bundles_missing", wire.RootElement.GetProperty("reason").GetString(), "wire unavailable reason");
        True(!wire.RootElement.TryGetProperty("catalog", out _), "wire omits unavailable catalog");
        True(!wire.RootElement.TryGetProperty("geometryBundleId", out _), "wire omits unavailable bundle identity");
    }

    var requestId = CadRequestId.New();
    var project = CadProjectHandle.New();
    var session = CadSessionHandle.New();
    var open = await broker.OpenProjectAsync(new CadOpenProjectRequest(requestId, CadWorkspaceHandle.New(), "Smoke project"));
    Equal("cad_runtime_unavailable", open.Error?.Code, "open unavailable");
    Equal("cad_runtime_unavailable", (await broker.GetCatalogAsync(requestId)).Error?.Code, "catalog unavailable");
    var execute = await broker.ExecuteAsync(new CadOperationRequest(
        requestId, session, project, new CadRevision(0), CadOperationMode.Suggest, "spur_gear"));
    Equal("cad_runtime_unavailable", execute.Error?.Code, "execute unavailable");
    var artifactRequest = new CadArtifactReadRequest(requestId, session, CadArtifactHandle.New(), 4_096);
    Equal("cad_runtime_unavailable", (await broker.GetArtifactReceiptAsync(artifactRequest)).Error?.Code,
        "artifact receipt unavailable");
    Equal("cad_runtime_unavailable", (await broker.OpenArtifactReadAsync(artifactRequest)).Error?.Code,
        "artifact read unavailable");
    var verify = await broker.VerifyAsync(new CadVerificationRequest(
        requestId, session, project, new CadRevision(0), [CadVerificationCheck.ValidSolids]));
    Equal("cad_runtime_unavailable", verify.Error?.Code, "verify unavailable");
    Equal("cad_runtime_unavailable", (await broker.CloseSessionAsync(new CadCloseSessionRequest(requestId, session))).Error?.Code,
        "close unavailable");

    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    Equal("cancelled", (await broker.GetCatalogAsync(requestId, cancelled.Token)).Error?.Code, "cancelled request");
});

return suite.Complete();

static CadCommercialDraft CommercialDraft(
    CadCommercialDocumentKind kind,
    long discountMinorUnits = 1_000,
    long freightMinorUnits = 500)
{
    var seller = new CadCommercialParty(
        "Photon Fabrication",
        "Scarlett",
        ["100 Light Way"],
        "Springfield",
        "IL",
        "62701",
        "US",
        "sales@example.test",
        "555-0100");
    var customer = new CadCommercialParty(
        "Gear Works",
        "Pat Builder",
        ["200 Industry Road"],
        "Peoria",
        "IL",
        "61602",
        "US",
        "buyer@example.test",
        "555-0200");
    return new CadCommercialDraft(
        kind,
        kind == CadCommercialDocumentKind.Invoice ? "INV-1001" : "Q-1001",
        new CadBomIdentity("project:gearbox", new CadRevision(12), Hash('b')),
        new DateOnly(2026, 8, 10),
        new DateOnly(2026, 9, 9),
        "USD",
        2,
        seller,
        customer,
        [
            new CadCommercialLine("line:gear", "bom:gear", "GEAR 42 / LH", "Spur gear", 2, CadCommercialUnit.Each, 10_000, true),
            new CadCommercialLine("line:setup", null, "labor:setup", "Assembly setup", 1.5m, CadCommercialUnit.Hour, 5_000, false),
        ],
        markupBasisPoints: 1_000,
        discountMinorUnits,
        freightMinorUnits,
        taxBasisPoints: 725,
        "Net 30",
        "Draft only.");
}

static CadSourceIdentity Source(string package) => new(package, "1.0.0", Hash('9'), "Apache-2.0");

static CadBundleIdentity Bundle(CadRuntimeRole role, string id, char hash) => new(
    role,
    id,
    "1.0.0",
    role == CadRuntimeRole.Geometry ? "geometry-provider" : "assembly-provider",
    new string(hash, 40),
    "win-x64",
    Hash(hash),
    DateTimeOffset.UtcNow);

static string Hash(char character) => new(character, 64);

static CadRigidTransform Translate(double x, double y, double z) => new(
[
    1, 0, 0, x,
    0, 1, 0, y,
    0, 0, 1, z,
    0, 0, 0, 1,
]);

static void True(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Equal<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{message}: expected {expected}, actual {actual}");
}

static void Throws<T>(Action action, string message) where T : Exception
{
    try
    {
        action();
        throw new InvalidOperationException($"{message}: no exception");
    }
    catch (T)
    {
    }
}

static void ThrowsCode(Action action, string code)
{
    try
    {
        action();
        throw new InvalidOperationException($"{code}: no exception");
    }
    catch (CadContractException exception)
    {
        Equal(code, exception.Code, "contract code");
    }
}

static void ThrowsState(Action action, string code)
{
    try
    {
        action();
        throw new InvalidOperationException($"{code}: no exception");
    }
    catch (CadStateTransitionException exception)
    {
        Equal(code, exception.Code, "state code");
    }
}

static void ThrowsCommercial(Action action, string code)
{
    try
    {
        action();
        throw new InvalidOperationException($"{code}: no exception");
    }
    catch (CadCommercialFlowException exception)
    {
        Equal(code, exception.Code, "commercial flow code");
    }
}

internal sealed class DelegatePathInspector(Func<string, CadPathInspection> inspect) : ICadPathInspector
{
    public CadPathInspection Inspect(string absolutePath) => inspect(absolutePath);
}

internal sealed class SmokeSuite
{
    private readonly Stopwatch _total = Stopwatch.StartNew();
    private int _failed;
    private int _passed;

    internal async Task RunAsync(string name, Func<Task> test)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await test();
            _passed++;
            Console.WriteLine($"PASS {name} ({stopwatch.Elapsed.TotalMilliseconds:F1} ms)");
        }
        catch (Exception exception)
        {
            _failed++;
            Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
        }
    }

    internal int Complete()
    {
        _total.Stop();
        Console.WriteLine($"PhotonCadRuntime smoke: {_passed}/{_passed + _failed} passed in {_total.Elapsed.TotalMilliseconds:F1} ms.");
        return _failed == 0 ? 0 : 1;
    }
}
