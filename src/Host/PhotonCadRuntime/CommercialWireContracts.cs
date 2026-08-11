using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotonCadRuntime;

public static class CadCommercialWireJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 32,
    };

    public static byte[] SerializeDraft(CadCommercialDraft draft) =>
        JsonSerializer.SerializeToUtf8Bytes(CadCommercialWireProjection.Draft(draft), Options);

    public static byte[] SerializeBomReviewRequest(CadBomExportReviewRequest request) =>
        JsonSerializer.SerializeToUtf8Bytes(CadCommercialWireProjection.BomReviewRequest(request), Options);

    public static byte[] SerializeBomReviewResult(CadBomExportReviewReceipt review) =>
        JsonSerializer.SerializeToUtf8Bytes(CadCommercialWireProjection.BomReviewResult(review), Options);

    public static byte[] SerializeBomCommitRequest(CadBomExportCommitRequest request) =>
        JsonSerializer.SerializeToUtf8Bytes(CadCommercialWireProjection.BomCommitRequest(request), Options);

    public static byte[] SerializeReviewRequest(CadCommercialReviewRequest request) =>
        JsonSerializer.SerializeToUtf8Bytes(CadCommercialWireProjection.ReviewRequest(request), Options);

    public static byte[] SerializeReviewResult(CadRenderedCommercialReview review) =>
        JsonSerializer.SerializeToUtf8Bytes(CadCommercialWireProjection.ReviewResult(review), Options);

    public static byte[] SerializeApprovalRequest(CadCommercialApprovalRequest request) =>
        JsonSerializer.SerializeToUtf8Bytes(CadCommercialWireProjection.ApprovalRequest(request), Options);

    public static byte[] SerializeApprovalResult(CadCommercialApprovalReceipt approval) =>
        JsonSerializer.SerializeToUtf8Bytes(CadCommercialWireProjection.ApprovalResult(approval), Options);

    public static byte[] SerializeCommitRequest(CadCommercialCommitRequest request) =>
        JsonSerializer.SerializeToUtf8Bytes(CadCommercialWireProjection.CommitRequest(request), Options);
}

public static class CadCommercialWireProjection
{
    public static CadWireCommercialDraft Draft(CadCommercialDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return new CadWireCommercialDraft(
            draft.ContractVersion,
            draft.DocumentKind,
            draft.DocumentNumber,
            draft.ProjectId,
            draft.ProjectRevision.Value,
            draft.BomDigest,
            draft.IssueDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            draft.DueDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            draft.Currency,
            draft.CurrencyScale,
            Party(draft.Seller),
            Party(draft.Customer),
            draft.Lines.Select(line => new CadWireCommercialLine(
                line.LineId,
                line.SourceBomRowId,
                line.PartNumber,
                line.Description,
                line.Quantity,
                CadCommercialNames.Unit(line.Unit),
                line.UnitPriceMinorUnits,
                line.Taxable)).ToArray(),
            draft.MarkupBasisPoints,
            draft.DiscountMinorUnits,
            draft.FreightMinorUnits,
            draft.TaxBasisPoints,
            draft.Terms,
            draft.Notes);
    }

    public static CadWireBomExportReviewRequest BomReviewRequest(CadBomExportReviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new CadWireBomExportReviewRequest(
            request.ContractVersion,
            request.RequestId.Value,
            request.Bom.ProjectId,
            request.Bom.Revision.Value,
            request.Bom.BomDigest,
            request.FormatName,
            request.Destination.Value);
    }

    public static CadWireBomExportReviewResult BomReviewResult(CadBomExportReviewReceipt review)
    {
        ArgumentNullException.ThrowIfNull(review);
        return new CadWireBomExportReviewResult(
            review.ContractVersion,
            review.Request.RequestId.Value,
            review.Request.Bom.ProjectId,
            review.Request.Bom.Revision.Value,
            "ready",
            "ready",
            review.Review.Value,
            review.ExportFingerprint,
            review.Files.Select(file => new CadWireCommercialFile(file.Role, file.RelativePath.Value)).ToArray());
    }

    public static CadWireBomExportCommitRequest BomCommitRequest(CadBomExportCommitRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new CadWireBomExportCommitRequest(
            request.ContractVersion,
            request.RequestId.Value,
            request.Review.Value,
            request.ExportFingerprint);
    }

    public static CadWireCommercialReviewRequest ReviewRequest(CadCommercialReviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new CadWireCommercialReviewRequest(
            request.ContractVersion,
            request.RequestId.Value,
            Draft(request.Draft),
            Action(request.Action));
    }

    public static CadWireCommercialReviewResult ReviewResult(CadRenderedCommercialReview review)
    {
        ArgumentNullException.ThrowIfNull(review);
        return new CadWireCommercialReviewResult(
            review.ContractVersion,
            review.Binding.Request.RequestId.Value,
            "ready",
            "ready",
            review.Binding.Review.Value,
            review.DocumentFingerprint,
            review.Binding.ActionFingerprint,
            Utc(review.Binding.ExpiresAtUtc),
            review.Pages.Select(page => new CadWireCommercialPreviewPage(
                page.PageNumber,
                page.Preview.Value,
                page.ContentDigest)).ToArray(),
            Totals(review.Totals));
    }

    public static CadWireCommercialApprovalRequest ApprovalRequest(CadCommercialApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new CadWireCommercialApprovalRequest(
            request.ContractVersion,
            request.RequestId.Value,
            request.Review.Value,
            request.DocumentFingerprint,
            request.ActionFingerprint,
            request.ViewedPageDigests.ToArray());
    }

    public static CadWireCommercialApprovalResult ApprovalResult(CadCommercialApprovalReceipt approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        return new CadWireCommercialApprovalResult(
            approval.ContractVersion,
            approval.Request.RequestId.Value,
            "approved",
            "approved",
            approval.Approval.Value,
            approval.DocumentFingerprint,
            approval.ActionFingerprint,
            Utc(approval.ExpiresAtUtc));
    }

    public static CadWireCommercialCommitRequest CommitRequest(CadCommercialCommitRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new CadWireCommercialCommitRequest(
            request.ContractVersion,
            request.RequestId.Value,
            request.Approval.Value,
            request.DocumentFingerprint,
            request.ActionFingerprint);
    }

    private static CadWireCommercialParty Party(CadCommercialParty party) => new(
        party.Organization,
        party.ContactName,
        party.AddressLines.ToArray(),
        party.City,
        party.Region,
        party.PostalCode,
        party.CountryCode,
        party.Email,
        party.Phone);

    private static object Action(CadCommercialOutputAction action) => action.KindValue switch
    {
        CadCommercialOutputKind.ExportPdf => new CadWireCommercialExportAction(
            action.Kind,
            action.Destination?.Value ?? throw new InvalidOperationException("Export destination is missing.")),
        CadCommercialOutputKind.Print => new CadWireCommercialPrintAction(
            action.Kind,
            action.Printer?.Value ?? throw new InvalidOperationException("Printer is missing."),
            action.Copies),
        _ => throw new InvalidOperationException("Unsupported commercial output action."),
    };

    private static CadWireCommercialTotals Totals(CadCommercialTotals totals) => new(
        totals.LineSubtotalMinorUnits,
        totals.MarkupMinorUnits,
        totals.DiscountMinorUnits,
        totals.FreightMinorUnits,
        totals.TaxableSubtotalMinorUnits,
        totals.TaxMinorUnits,
        totals.TotalMinorUnits);

    private static string Utc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record CadWireCommercialParty(
    [property: JsonPropertyName("organization")] string Organization,
    [property: JsonPropertyName("contactName")] string ContactName,
    [property: JsonPropertyName("addressLines")] IReadOnlyList<string> AddressLines,
    [property: JsonPropertyName("city")] string City,
    [property: JsonPropertyName("region")] string Region,
    [property: JsonPropertyName("postalCode")] string PostalCode,
    [property: JsonPropertyName("countryCode")] string CountryCode,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("phone")] string Phone);

public sealed record CadWireCommercialLine(
    [property: JsonPropertyName("lineId")] string LineId,
    [property: JsonPropertyName("sourceBomRowId")] string? SourceBomRowId,
    [property: JsonPropertyName("partNumber")] string PartNumber,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("quantity")] decimal Quantity,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("unitPriceMinorUnits")] long UnitPriceMinorUnits,
    [property: JsonPropertyName("taxable")] bool Taxable);

public sealed record CadWireCommercialDraft(
    [property: JsonPropertyName("contractVersion")] int ContractVersion,
    [property: JsonPropertyName("documentKind")] string DocumentKind,
    [property: JsonPropertyName("documentNumber")] string DocumentNumber,
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("projectRevision")] long ProjectRevision,
    [property: JsonPropertyName("bomDigest")] string BomDigest,
    [property: JsonPropertyName("issueDate")] string IssueDate,
    [property: JsonPropertyName("dueDate")] string? DueDate,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("currencyScale")] int CurrencyScale,
    [property: JsonPropertyName("seller")] CadWireCommercialParty Seller,
    [property: JsonPropertyName("customer")] CadWireCommercialParty Customer,
    [property: JsonPropertyName("lines")] IReadOnlyList<CadWireCommercialLine> Lines,
    [property: JsonPropertyName("markupBasisPoints")] int MarkupBasisPoints,
    [property: JsonPropertyName("discountMinorUnits")] long DiscountMinorUnits,
    [property: JsonPropertyName("freightMinorUnits")] long FreightMinorUnits,
    [property: JsonPropertyName("taxBasisPoints")] int TaxBasisPoints,
    [property: JsonPropertyName("terms")] string Terms,
    [property: JsonPropertyName("notes")] string Notes);

public sealed record CadWireCommercialTotals(
    [property: JsonPropertyName("lineSubtotalMinorUnits")] long LineSubtotalMinorUnits,
    [property: JsonPropertyName("markupMinorUnits")] long MarkupMinorUnits,
    [property: JsonPropertyName("discountMinorUnits")] long DiscountMinorUnits,
    [property: JsonPropertyName("freightMinorUnits")] long FreightMinorUnits,
    [property: JsonPropertyName("taxableSubtotalMinorUnits")] long TaxableSubtotalMinorUnits,
    [property: JsonPropertyName("taxMinorUnits")] long TaxMinorUnits,
    [property: JsonPropertyName("totalMinorUnits")] long TotalMinorUnits);

public sealed record CadWireCommercialExportAction(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("destinationHandle")] string DestinationHandle);

public sealed record CadWireCommercialPrintAction(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("printerHandle")] string PrinterHandle,
    [property: JsonPropertyName("copies")] int Copies);

public sealed record CadWireBomExportReviewRequest(
    [property: JsonPropertyName("contractVersion")] int ContractVersion,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("projectRevision")] long ProjectRevision,
    [property: JsonPropertyName("bomDigest")] string BomDigest,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("destinationHandle")] string DestinationHandle);

public sealed record CadWireCommercialFile(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("relativePath")] string RelativePath);

public sealed record CadWireBomExportReviewResult(
    [property: JsonPropertyName("contractVersion")] int ContractVersion,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("projectRevision")] long ProjectRevision,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("reviewHandle")] string ReviewHandle,
    [property: JsonPropertyName("exportFingerprint")] string ExportFingerprint,
    [property: JsonPropertyName("files")] IReadOnlyList<CadWireCommercialFile> Files);

public sealed record CadWireBomExportCommitRequest(
    [property: JsonPropertyName("contractVersion")] int ContractVersion,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("reviewHandle")] string ReviewHandle,
    [property: JsonPropertyName("exportFingerprint")] string ExportFingerprint);

public sealed record CadWireCommercialReviewRequest(
    [property: JsonPropertyName("contractVersion")] int ContractVersion,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("draft")] CadWireCommercialDraft Draft,
    [property: JsonPropertyName("action")] object Action);

public sealed record CadWireCommercialPreviewPage(
    [property: JsonPropertyName("pageNumber")] int PageNumber,
    [property: JsonPropertyName("previewHandle")] string PreviewHandle,
    [property: JsonPropertyName("contentDigest")] string ContentDigest);

public sealed record CadWireCommercialReviewResult(
    [property: JsonPropertyName("contractVersion")] int ContractVersion,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("reviewHandle")] string ReviewHandle,
    [property: JsonPropertyName("documentFingerprint")] string DocumentFingerprint,
    [property: JsonPropertyName("actionFingerprint")] string ActionFingerprint,
    [property: JsonPropertyName("expiresAtUtc")] string ExpiresAtUtc,
    [property: JsonPropertyName("pages")] IReadOnlyList<CadWireCommercialPreviewPage> Pages,
    [property: JsonPropertyName("totals")] CadWireCommercialTotals Totals);

public sealed record CadWireCommercialApprovalRequest(
    [property: JsonPropertyName("contractVersion")] int ContractVersion,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("reviewHandle")] string ReviewHandle,
    [property: JsonPropertyName("documentFingerprint")] string DocumentFingerprint,
    [property: JsonPropertyName("actionFingerprint")] string ActionFingerprint,
    [property: JsonPropertyName("viewedPageDigests")] IReadOnlyList<string> ViewedPageDigests);

public sealed record CadWireCommercialApprovalResult(
    [property: JsonPropertyName("contractVersion")] int ContractVersion,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("approvalHandle")] string ApprovalHandle,
    [property: JsonPropertyName("documentFingerprint")] string DocumentFingerprint,
    [property: JsonPropertyName("actionFingerprint")] string ActionFingerprint,
    [property: JsonPropertyName("expiresAtUtc")] string ExpiresAtUtc);

public sealed record CadWireCommercialCommitRequest(
    [property: JsonPropertyName("contractVersion")] int ContractVersion,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("approvalHandle")] string ApprovalHandle,
    [property: JsonPropertyName("documentFingerprint")] string DocumentFingerprint,
    [property: JsonPropertyName("actionFingerprint")] string ActionFingerprint);

public sealed record CadWireBomExportCommitResult(
    [property: JsonPropertyName("contractVersion")] int ContractVersion,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("exportFingerprint")] string? ExportFingerprint);

public sealed record CadWireCommercialCommitResult(
    [property: JsonPropertyName("contractVersion")] int ContractVersion,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("documentFingerprint")] string? DocumentFingerprint,
    [property: JsonPropertyName("actionFingerprint")] string? ActionFingerprint);
