using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PhotonCadRuntime;

public sealed record CadCommercialRequestId
{
    public CadCommercialRequestId(string value) => Value = ContractGuards.Identifier(value, nameof(value));
    public string Value { get; }
    public override string ToString() => Value;
}

public sealed class CadBomExportReviewRequest
{
    public CadBomExportReviewRequest(
        CadCommercialRequestId requestId,
        CadBomIdentity bom,
        CadBomExportFormat format,
        CadExportDestinationHandle destination)
    {
        RequestId = requestId ?? throw new CadContractException("required", nameof(requestId));
        Bom = bom ?? throw new CadContractException("required", nameof(bom));
        Format = ContractGuards.EnumValue(format, nameof(format));
        Destination = destination ?? throw new CadContractException("required", nameof(destination));
    }

    public int ContractVersion => CadContractVersions.Host;
    public CadCommercialRequestId RequestId { get; }
    public CadBomIdentity Bom { get; }
    public CadBomExportFormat Format { get; }
    public string FormatName => CadCommercialNames.BomFormat(Format);
    public CadExportDestinationHandle Destination { get; }
}

public sealed class CadBomExportFile
{
    public CadBomExportFile(bool validationReport, CadRelativePath relativePath)
    {
        ValidationReport = validationReport;
        RelativePath = relativePath ?? throw new CadContractException("required", nameof(relativePath));
    }

    public bool ValidationReport { get; }
    public string Role => ValidationReport ? "validation-report" : "bom";
    public CadRelativePath RelativePath { get; }
}

public sealed class CadBomExportReviewReceipt
{
    public CadBomExportReviewReceipt(
        CadBomExportReviewRequest request,
        CadBomExportReviewHandle review,
        IEnumerable<CadBomExportFile> files,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        Request = request ?? throw new CadContractException("required", nameof(request));
        Review = review ?? throw new CadContractException("required", nameof(review));
        Files = ContractGuards.Copy(files, nameof(files), 4, requireAny: true);
        ContractGuards.RequireUnique(Files.Select(file => file.RelativePath.Value), nameof(files), StringComparer.OrdinalIgnoreCase);
        var bomFiles = Files.Where(file => !file.ValidationReport).ToArray();
        if (bomFiles.Length != 1 || !bomFiles[0].RelativePath.Value.EndsWith($".{Request.FormatName}", StringComparison.OrdinalIgnoreCase))
            throw new CadContractException("bom_export_file_mismatch", nameof(files));
        if (Files.Count(file => file.ValidationReport) > 1)
            throw new CadContractException("duplicate_validation_report", nameof(files));
        (IssuedAtUtc, ExpiresAtUtc) = CadReviewWindow.Normalize(issuedAtUtc, expiresAtUtc);
        ExportFingerprint = CadCommercialFingerprint.ForBomExport(Request);
    }

    public int ContractVersion => CadContractVersions.Host;
    public CadBomExportReviewRequest Request { get; }
    public CadBomExportReviewHandle Review { get; }
    public string ExportFingerprint { get; }
    public IReadOnlyList<CadBomExportFile> Files { get; }
    public DateTimeOffset IssuedAtUtc { get; }
    public DateTimeOffset ExpiresAtUtc { get; }
}

public sealed class CadBomExportCommitRequest
{
    public CadBomExportCommitRequest(
        CadCommercialRequestId requestId,
        CadBomExportReviewHandle review,
        string exportFingerprint)
    {
        RequestId = requestId ?? throw new CadContractException("required", nameof(requestId));
        Review = review ?? throw new CadContractException("required", nameof(review));
        ExportFingerprint = ContractGuards.Sha256(exportFingerprint, nameof(exportFingerprint));
    }

    public int ContractVersion => CadContractVersions.Host;
    public CadCommercialRequestId RequestId { get; }
    public CadBomExportReviewHandle Review { get; }
    public string ExportFingerprint { get; }
}

public sealed class CadCommercialReviewRequest
{
    public CadCommercialReviewRequest(
        CadCommercialRequestId requestId,
        CadCommercialDraft draft,
        CadCommercialOutputAction action)
    {
        RequestId = requestId ?? throw new CadContractException("required", nameof(requestId));
        Draft = draft ?? throw new CadContractException("required", nameof(draft));
        Action = action ?? throw new CadContractException("required", nameof(action));
    }

    public int ContractVersion => CadContractVersions.Host;
    public CadCommercialRequestId RequestId { get; }
    public CadCommercialDraft Draft { get; }
    public CadCommercialOutputAction Action { get; }
}

public sealed class CadCommercialReviewBinding
{
    public CadCommercialReviewBinding(
        CadCommercialReviewRequest request,
        CadCommercialReviewHandle review,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        Request = request ?? throw new CadContractException("required", nameof(request));
        Review = review ?? throw new CadContractException("required", nameof(review));
        (IssuedAtUtc, ExpiresAtUtc) = CadReviewWindow.Normalize(issuedAtUtc, expiresAtUtc);
        DraftFingerprint = Request.Draft.CommercialDraftFingerprint;
        ActionFingerprint = CadCommercialFingerprint.ForAction(DraftFingerprint, Request.Action);
    }

    public int ContractVersion => CadContractVersions.Host;
    public CadCommercialReviewRequest Request { get; }
    public CadCommercialReviewHandle Review { get; }
    public string DraftFingerprint { get; }
    public string ActionFingerprint { get; }
    public DateTimeOffset IssuedAtUtc { get; }
    public DateTimeOffset ExpiresAtUtc { get; }
}

public sealed class CadCommercialPreviewPage
{
    public CadCommercialPreviewPage(int pageNumber, CadDocumentPageHandle preview, string contentDigest)
    {
        PageNumber = ContractGuards.Range(pageNumber, 1, CadCommercialLimits.MaximumPagePreviews, nameof(pageNumber));
        Preview = preview ?? throw new CadContractException("required", nameof(preview));
        ContentDigest = ContractGuards.Sha256(contentDigest, nameof(contentDigest));
    }

    public int PageNumber { get; }
    public CadDocumentPageHandle Preview { get; }
    public string ContentDigest { get; }
}

public sealed class CadRenderedCommercialReview
{
    public CadRenderedCommercialReview(
        CadCommercialReviewBinding binding,
        string documentFingerprint,
        IEnumerable<CadCommercialPreviewPage> pages,
        DateTimeOffset renderedAtUtc)
    {
        Binding = binding ?? throw new CadContractException("required", nameof(binding));
        DocumentFingerprint = ContractGuards.Sha256(documentFingerprint, nameof(documentFingerprint));
        Pages = ContractGuards.Copy(pages, nameof(pages), CadCommercialLimits.MaximumPagePreviews, requireAny: true);
        ContractGuards.RequireUnique(Pages.Select(page => page.Preview.Value), nameof(pages));
        for (var index = 0; index < Pages.Count; index++)
        {
            if (Pages[index].PageNumber != index + 1)
                throw new CadContractException("incomplete_page_preview", nameof(pages));
        }
        RenderedAtUtc = ContractGuards.Utc(renderedAtUtc, nameof(renderedAtUtc));
        if (RenderedAtUtc < Binding.IssuedAtUtc || RenderedAtUtc >= Binding.ExpiresAtUtc)
            throw new CadContractException("preview_outside_review_window", nameof(renderedAtUtc));
        PageSetDigest = CadCommercialFingerprint.ForPages(Binding, DocumentFingerprint, Pages);
    }

    public int ContractVersion => CadContractVersions.Host;
    public CadCommercialReviewBinding Binding { get; }
    public string DocumentFingerprint { get; }
    public IReadOnlyList<CadCommercialPreviewPage> Pages { get; }
    public CadCommercialTotals Totals => Binding.Request.Draft.Totals;
    public DateTimeOffset RenderedAtUtc { get; }
    public string PageSetDigest { get; }
    public bool Complete => true;
}

public sealed class CadCommercialApprovalRequest
{
    public CadCommercialApprovalRequest(
        CadCommercialRequestId requestId,
        CadCommercialReviewHandle review,
        string documentFingerprint,
        string actionFingerprint,
        IEnumerable<string> viewedPageDigests)
    {
        RequestId = requestId ?? throw new CadContractException("required", nameof(requestId));
        Review = review ?? throw new CadContractException("required", nameof(review));
        DocumentFingerprint = ContractGuards.Sha256(documentFingerprint, nameof(documentFingerprint));
        ActionFingerprint = ContractGuards.Sha256(actionFingerprint, nameof(actionFingerprint));
        ViewedPageDigests = ContractGuards.Copy(
            viewedPageDigests?.Select(digest => ContractGuards.Sha256(digest, nameof(viewedPageDigests))),
            nameof(viewedPageDigests),
            CadCommercialLimits.MaximumPagePreviews,
            requireAny: true);
    }

    public int ContractVersion => CadContractVersions.Host;
    public CadCommercialRequestId RequestId { get; }
    public CadCommercialReviewHandle Review { get; }
    public string DocumentFingerprint { get; }
    public string ActionFingerprint { get; }
    public IReadOnlyList<string> ViewedPageDigests { get; }
}

public sealed class CadCommercialApprovalReceipt
{
    internal CadCommercialApprovalReceipt(
        CadCommercialApprovalRequest request,
        CadCommercialApprovalHandle approval,
        DateTimeOffset approvedAtUtc,
        DateTimeOffset expiresAtUtc,
        string draftFingerprint,
        string pageSetDigest)
    {
        Request = request;
        Approval = approval;
        ApprovedAtUtc = approvedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        DraftFingerprint = ContractGuards.Sha256(draftFingerprint, nameof(draftFingerprint));
        PageSetDigest = ContractGuards.Sha256(pageSetDigest, nameof(pageSetDigest));
    }

    public int ContractVersion => CadContractVersions.Host;
    public CadCommercialApprovalRequest Request { get; }
    public CadCommercialApprovalHandle Approval { get; }
    public string DocumentFingerprint => Request.DocumentFingerprint;
    public string ActionFingerprint => Request.ActionFingerprint;
    public string DraftFingerprint { get; }
    public string PageSetDigest { get; }
    public DateTimeOffset ApprovedAtUtc { get; }
    public DateTimeOffset ExpiresAtUtc { get; }
    public bool ExplicitHumanApproval => true;
}

public sealed class CadCommercialCommitRequest
{
    public CadCommercialCommitRequest(
        CadCommercialRequestId requestId,
        CadCommercialApprovalHandle approval,
        string documentFingerprint,
        string actionFingerprint)
    {
        RequestId = requestId ?? throw new CadContractException("required", nameof(requestId));
        Approval = approval ?? throw new CadContractException("required", nameof(approval));
        DocumentFingerprint = ContractGuards.Sha256(documentFingerprint, nameof(documentFingerprint));
        ActionFingerprint = ContractGuards.Sha256(actionFingerprint, nameof(actionFingerprint));
    }

    public int ContractVersion => CadContractVersions.Host;
    public CadCommercialRequestId RequestId { get; }
    public CadCommercialApprovalHandle Approval { get; }
    public string DocumentFingerprint { get; }
    public string ActionFingerprint { get; }
}

public sealed class CadCommercialFlowException : InvalidOperationException
{
    public CadCommercialFlowException(string code)
        : base("The requested commercial document transition is not permitted.") =>
        Code = ContractGuards.Identifier(code, nameof(code), 64);

    public string Code { get; }
}

public sealed class CadBomExportGate
{
    private readonly object _sync = new();
    private readonly CadBomExportReviewReceipt _review;
    private CadCommercialFlowState _state = CadCommercialFlowState.Reviewed;

    public CadBomExportGate(CadBomExportReviewReceipt review) =>
        _review = review ?? throw new CadContractException("required", nameof(review));

    public CadCommercialFlowState State
    {
        get { lock (_sync) return _state; }
    }

    public void Authorize(CadBomExportCommitRequest request, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_sync)
        {
            EnsureLive(nowUtc);
            if (_state != CadCommercialFlowState.Reviewed)
                throw new CadCommercialFlowException("bom_review_not_available");
            if (request.Review != _review.Review || request.ExportFingerprint != _review.ExportFingerprint)
                throw new CadCommercialFlowException("bom_review_binding_mismatch");
            _state = CadCommercialFlowState.Consumed;
        }
    }

    public void Discard()
    {
        lock (_sync)
        {
            if (_state is CadCommercialFlowState.Consumed or CadCommercialFlowState.Expired)
                throw new CadCommercialFlowException("bom_review_terminal");
            _state = CadCommercialFlowState.Discarded;
        }
    }

    private void EnsureLive(DateTimeOffset nowUtc)
    {
        var now = ContractGuards.Utc(nowUtc, nameof(nowUtc));
        if (now >= _review.ExpiresAtUtc)
        {
            _state = CadCommercialFlowState.Expired;
            throw new CadCommercialFlowException("bom_review_expired");
        }
    }
}

public sealed class CadCommercialFlowGate
{
    private readonly object _sync = new();
    private readonly CadCommercialReviewBinding _binding;
    private CadCommercialFlowState _state = CadCommercialFlowState.Reviewed;
    private CadRenderedCommercialReview? _rendered;
    private CadCommercialApprovalReceipt? _approval;

    public CadCommercialFlowGate(CadCommercialReviewBinding binding) =>
        _binding = binding ?? throw new CadContractException("required", nameof(binding));

    public CadCommercialFlowState State
    {
        get { lock (_sync) return _state; }
    }

    public void AttachCompletePreview(CadRenderedCommercialReview rendered, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(rendered);
        lock (_sync)
        {
            EnsureLive(nowUtc);
            if (_state != CadCommercialFlowState.Reviewed)
                throw new CadCommercialFlowException("commercial_review_not_available");
            if (rendered.Binding.Review != _binding.Review ||
                rendered.Binding.Request.RequestId != _binding.Request.RequestId ||
                rendered.Binding.DraftFingerprint != _binding.DraftFingerprint ||
                rendered.Binding.ActionFingerprint != _binding.ActionFingerprint ||
                rendered.Binding.IssuedAtUtc != _binding.IssuedAtUtc ||
                rendered.Binding.ExpiresAtUtc != _binding.ExpiresAtUtc ||
                rendered.RenderedAtUtc > ContractGuards.Utc(nowUtc, nameof(nowUtc)) ||
                !rendered.Complete)
                throw new CadCommercialFlowException("commercial_preview_binding_mismatch");
            _rendered = rendered;
            _state = CadCommercialFlowState.Previewed;
        }
    }

    public CadCommercialApprovalReceipt RecordExplicitHumanApproval(
        CadCommercialApprovalRequest request,
        CadCommercialApprovalHandle approval,
        bool explicitHumanDecision,
        DateTimeOffset nowUtc,
        DateTimeOffset expiresAtUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(approval);
        lock (_sync)
        {
            EnsureLive(nowUtc);
            if (_state != CadCommercialFlowState.Previewed || _rendered is null)
                throw new CadCommercialFlowException("complete_preview_required");
            if (!explicitHumanDecision)
                throw new CadCommercialFlowException("explicit_human_approval_required");
            if (request.Review != _binding.Review ||
                request.DocumentFingerprint != _rendered.DocumentFingerprint ||
                request.ActionFingerprint != _binding.ActionFingerprint ||
                !request.ViewedPageDigests.SequenceEqual(_rendered.Pages.Select(page => page.ContentDigest)))
                throw new CadCommercialFlowException("commercial_approval_binding_mismatch");

            var now = ContractGuards.Utc(nowUtc, nameof(nowUtc));
            var expires = ContractGuards.Utc(expiresAtUtc, nameof(expiresAtUtc));
            if (expires <= now || expires > _binding.ExpiresAtUtc)
                throw new CadContractException("invalid_approval_window", nameof(expiresAtUtc));
            _approval = new CadCommercialApprovalReceipt(
                request,
                approval,
                now,
                expires,
                _binding.DraftFingerprint,
                _rendered.PageSetDigest);
            _state = CadCommercialFlowState.Approved;
            return _approval;
        }
    }

    public void AuthorizeOutput(CadCommercialCommitRequest request, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_sync)
        {
            EnsureLive(nowUtc);
            if (_state != CadCommercialFlowState.Approved || _approval is null)
                throw new CadCommercialFlowException("human_approval_required");
            var now = ContractGuards.Utc(nowUtc, nameof(nowUtc));
            if (now >= _approval.ExpiresAtUtc)
            {
                _approval = null;
                _state = CadCommercialFlowState.Expired;
                throw new CadCommercialFlowException("commercial_approval_expired");
            }
            if (request.Approval != _approval.Approval ||
                request.DocumentFingerprint != _approval.DocumentFingerprint ||
                request.ActionFingerprint != _approval.ActionFingerprint)
                throw new CadCommercialFlowException("commercial_commit_binding_mismatch");
            _state = CadCommercialFlowState.Consumed;
        }
    }

    public void Discard()
    {
        lock (_sync)
        {
            if (_state is CadCommercialFlowState.Consumed or CadCommercialFlowState.Expired)
                throw new CadCommercialFlowException("commercial_review_terminal");
            _rendered = null;
            _approval = null;
            _state = CadCommercialFlowState.Discarded;
        }
    }

    private void EnsureLive(DateTimeOffset nowUtc)
    {
        var now = ContractGuards.Utc(nowUtc, nameof(nowUtc));
        if (now >= _binding.ExpiresAtUtc)
        {
            _rendered = null;
            _approval = null;
            _state = CadCommercialFlowState.Expired;
            throw new CadCommercialFlowException("commercial_review_expired");
        }
    }
}

internal static class CadCommercialMath
{
    internal static long MinorUnits(long value, string field)
    {
        if (value < 0 || value > CadCommercialLimits.MaximumMinorUnits)
            throw new CadContractException("invalid_minor_units", field);
        return value;
    }

    internal static long MultiplyToMinorUnits(decimal quantity, long unitPrice, string field) =>
        RoundedMinorUnits(quantity * unitPrice, field);

    internal static CadCommercialTotals Totals(CadCommercialDraft draft)
    {
        try
        {
            var subtotal = draft.Lines.Aggregate(0L, (current, line) => checked(current + line.ExtendedPriceMinorUnits));
            var taxableLines = draft.Lines.Where(line => line.Taxable)
                .Aggregate(0L, (current, line) => checked(current + line.ExtendedPriceMinorUnits));
            var markup = Rate(subtotal, draft.MarkupBasisPoints, "markupMinorUnits");
            var grossAfterMarkup = checked(subtotal + markup);
            if (draft.DiscountMinorUnits > grossAfterMarkup)
                throw new CadContractException("discount_exceeds_subtotal", nameof(draft.DiscountMinorUnits));

            var taxableMarkup = Rate(taxableLines, draft.MarkupBasisPoints, "taxableMarkupMinorUnits");
            var taxableBeforeDiscount = checked(taxableLines + taxableMarkup);
            var taxableDiscount = grossAfterMarkup == 0
                ? 0
                : RoundedMinorUnits(
                    ((decimal)draft.DiscountMinorUnits * taxableBeforeDiscount) / grossAfterMarkup,
                    "taxableDiscountMinorUnits");
            var taxableSubtotal = Math.Max(0, checked(taxableBeforeDiscount - taxableDiscount));
            var tax = Rate(taxableSubtotal, draft.TaxBasisPoints, "taxMinorUnits");
            var total = checked(grossAfterMarkup - draft.DiscountMinorUnits + draft.FreightMinorUnits + tax);
            MinorUnits(total, "totalMinorUnits");
            return new CadCommercialTotals(
                subtotal,
                markup,
                draft.DiscountMinorUnits,
                draft.FreightMinorUnits,
                taxableSubtotal,
                tax,
                total);
        }
        catch (OverflowException)
        {
            throw new CadContractException("invalid_minor_units", "totalMinorUnits");
        }
    }

    private static long Rate(long value, int basisPoints, string field) =>
        RoundedMinorUnits(((decimal)value * basisPoints) / 10_000m, field);

    private static long RoundedMinorUnits(decimal value, string field)
    {
        decimal rounded;
        try
        {
            rounded = decimal.Round(value, 0, MidpointRounding.AwayFromZero);
        }
        catch (OverflowException)
        {
            throw new CadContractException("invalid_minor_units", field);
        }
        if (rounded < 0 || rounded > CadCommercialLimits.MaximumMinorUnits)
            throw new CadContractException("invalid_minor_units", field);
        return decimal.ToInt64(rounded);
    }
}

internal static class CadReviewWindow
{
    internal static (DateTimeOffset IssuedAtUtc, DateTimeOffset ExpiresAtUtc) Normalize(
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        var issued = ContractGuards.Utc(issuedAtUtc, nameof(issuedAtUtc));
        var expires = ContractGuards.Utc(expiresAtUtc, nameof(expiresAtUtc));
        var lifetime = expires - issued;
        if (lifetime <= TimeSpan.Zero || lifetime > CadCommercialLimits.MaximumReviewLifetime)
            throw new CadContractException("invalid_review_window", nameof(expiresAtUtc));
        return (issued, expires);
    }
}

internal static class CadCommercialNames
{
    internal static string BomFormat(CadBomExportFormat format) => format switch
    {
        CadBomExportFormat.Xlsx => "xlsx",
        CadBomExportFormat.Csv => "csv",
        CadBomExportFormat.Pdf => "pdf",
        _ => throw new InvalidOperationException("Unsupported BOM export format."),
    };

    internal static string Unit(CadCommercialUnit unit) => unit switch
    {
        CadCommercialUnit.Each => "each",
        CadCommercialUnit.Length => "length",
        CadCommercialUnit.Hour => "hour",
        _ => throw new InvalidOperationException("Unsupported commercial unit."),
    };
}

internal static class CadCommercialFingerprint
{
    internal static string ForDraft(CadCommercialDraft draft)
    {
        using var builder = new FingerprintBuilder("photon.cad.commercial-draft/v1");
        builder.Add(draft.DocumentKind);
        builder.Add(draft.DocumentNumber);
        builder.Add(draft.ProjectId);
        builder.Add(draft.ProjectRevision.Value);
        builder.Add(draft.BomDigest);
        builder.Add(draft.IssueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        builder.Add(draft.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty);
        builder.Add(draft.Currency);
        builder.Add(draft.CurrencyScale);
        AddParty(builder, draft.Seller);
        AddParty(builder, draft.Customer);
        builder.Add(draft.Lines.Count);
        foreach (var line in draft.Lines)
        {
            builder.Add(line.LineId);
            builder.Add(line.SourceBomRowId ?? string.Empty);
            builder.Add(line.PartNumber);
            builder.Add(line.Description);
            builder.Add(line.Quantity);
            builder.Add(CadCommercialNames.Unit(line.Unit));
            builder.Add(line.UnitPriceMinorUnits);
            builder.Add(line.Taxable ? "true" : "false");
        }
        builder.Add(draft.MarkupBasisPoints);
        builder.Add(draft.DiscountMinorUnits);
        builder.Add(draft.FreightMinorUnits);
        builder.Add(draft.TaxBasisPoints);
        builder.Add(draft.Terms);
        builder.Add(draft.Notes);
        return builder.Complete();
    }

    internal static string ForBomExport(CadBomExportReviewRequest request)
    {
        using var builder = new FingerprintBuilder("photon.cad.bom-export/v1");
        builder.Add(request.Bom.ProjectId);
        builder.Add(request.Bom.Revision.Value);
        builder.Add(request.Bom.BomDigest);
        builder.Add(request.FormatName);
        builder.Add(request.Destination.Value);
        return builder.Complete();
    }

    internal static string ForAction(string documentFingerprint, CadCommercialOutputAction action)
    {
        using var builder = new FingerprintBuilder("photon.cad.commercial-action/v1");
        builder.Add(documentFingerprint);
        builder.Add(action.Kind);
        builder.Add(action.Destination?.Value ?? string.Empty);
        builder.Add(action.Printer?.Value ?? string.Empty);
        builder.Add(action.Copies);
        return builder.Complete();
    }

    internal static string ForPages(
        CadCommercialReviewBinding binding,
        string documentFingerprint,
        IEnumerable<CadCommercialPreviewPage> pages)
    {
        using var builder = new FingerprintBuilder("photon.cad.commercial-pages/v1");
        builder.Add(binding.DraftFingerprint);
        builder.Add(documentFingerprint);
        builder.Add(binding.ActionFingerprint);
        foreach (var page in pages)
        {
            builder.Add(page.PageNumber);
            builder.Add(page.Preview.Value);
            builder.Add(page.ContentDigest);
        }
        return builder.Complete();
    }

    private static void AddParty(FingerprintBuilder builder, CadCommercialParty party)
    {
        builder.Add(party.Organization);
        builder.Add(party.ContactName);
        builder.Add(party.AddressLines.Count);
        foreach (var line in party.AddressLines) builder.Add(line);
        builder.Add(party.City);
        builder.Add(party.Region);
        builder.Add(party.PostalCode);
        builder.Add(party.CountryCode);
        builder.Add(party.Email);
        builder.Add(party.Phone);
    }

    private sealed class FingerprintBuilder : IDisposable
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private bool _completed;

        internal FingerprintBuilder(string domain) => Add(domain);

        internal void Add(string value)
        {
            if (_completed) throw new InvalidOperationException("Fingerprint is complete.");
            var bytes = Encoding.UTF8.GetBytes(value);
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            _hash.AppendData(length);
            _hash.AppendData(bytes);
        }

        internal void Add(long value) => Add(value.ToString(CultureInfo.InvariantCulture));
        internal void Add(int value) => Add(value.ToString(CultureInfo.InvariantCulture));
        internal void Add(decimal value) => Add(value.ToString("G29", CultureInfo.InvariantCulture));

        internal string Complete()
        {
            if (_completed) throw new InvalidOperationException("Fingerprint is complete.");
            _completed = true;
            return Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
        }

        public void Dispose() => _hash.Dispose();
    }
}
