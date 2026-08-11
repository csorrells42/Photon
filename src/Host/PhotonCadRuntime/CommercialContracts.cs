using System.Globalization;
using System.Text;

namespace PhotonCadRuntime;

public static class CadCommercialLimits
{
    public const long MaximumMinorUnits = 9_007_199_254_740_991L;
    public const decimal MaximumQuantity = 1_000_000_000m;
    public const int MaximumBasisPoints = 100_000;
    public const int MaximumCurrencyScale = 4;
    public const int MaximumCopies = 10;
    public const int MaximumAddressLines = 8;
    public const int MaximumPagePreviews = 2_000;
    public static readonly TimeSpan MaximumReviewLifetime = TimeSpan.FromMinutes(15);
}

public static class CadCommercialSafetyBoundary
{
    public const string Invariant =
        "A customer quote or invoice may be exported or printed only after every rendered page is shown and a human explicitly approves that exact document and output action.";

    public static IReadOnlyList<string> ForbiddenActions { get; } = Array.AsReadOnly(
        new[] { "email", "send", "post-to-accounting", "charge", "pay", "mark-paid" });
}

public enum CadBomExportFormat
{
    Xlsx,
    Csv,
    Pdf,
}

public enum CadCommercialDocumentKind
{
    Quote,
    Invoice,
}

public enum CadCommercialUnit
{
    Each,
    Length,
    Hour,
}

public enum CadCommercialOutputKind
{
    ExportPdf,
    Print,
}

public enum CadCommercialFlowState
{
    Reviewed,
    Previewed,
    Approved,
    Consumed,
    Discarded,
    Expired,
}

public sealed record CadBomExportReviewHandle
{
    public CadBomExportReviewHandle(string value) =>
        Value = CadCommercialHandle.Normalize(value, nameof(value), "cad-bom-review:");
    public string Value { get; }
    public static CadBomExportReviewHandle New() => new($"cad-bom-review:{Guid.NewGuid():N}");
    public override string ToString() => Value;
}

public sealed record CadCommercialReviewHandle
{
    public CadCommercialReviewHandle(string value) =>
        Value = CadCommercialHandle.Normalize(value, nameof(value), "cad-commercial-review:");
    public string Value { get; }
    public static CadCommercialReviewHandle New() => new($"cad-commercial-review:{Guid.NewGuid():N}");
    public override string ToString() => Value;
}

public sealed record CadCommercialApprovalHandle
{
    public CadCommercialApprovalHandle(string value) =>
        Value = CadCommercialHandle.Normalize(value, nameof(value), "cad-commercial-approval:");
    public string Value { get; }
    public static CadCommercialApprovalHandle New() => new($"cad-commercial-approval:{Guid.NewGuid():N}");
    public override string ToString() => Value;
}

public sealed record CadDocumentPageHandle
{
    public CadDocumentPageHandle(string value) =>
        Value = CadCommercialHandle.Normalize(value, nameof(value), "cad-document-page:");
    public string Value { get; }
    public static CadDocumentPageHandle New() => new($"cad-document-page:{Guid.NewGuid():N}");
    public override string ToString() => Value;
}

public sealed record CadExportDestinationHandle
{
    public CadExportDestinationHandle(string value) =>
        Value = CadCommercialHandle.Normalize(value, nameof(value), "cad-destination:");
    public string Value { get; }
    public static CadExportDestinationHandle New() => new($"cad-destination:{Guid.NewGuid():N}");
    public override string ToString() => Value;
}

public sealed record CadPrinterHandle
{
    public CadPrinterHandle(string value) =>
        Value = CadCommercialHandle.Normalize(value, nameof(value), "cad-printer:");
    public string Value { get; }
    public static CadPrinterHandle New() => new($"cad-printer:{Guid.NewGuid():N}");
    public override string ToString() => Value;
}

public sealed class CadBomIdentity
{
    public CadBomIdentity(string projectId, CadRevision revision, string bomDigest)
    {
        ProjectId = ContractGuards.Identifier(projectId, nameof(projectId));
        Revision = revision ?? throw new CadContractException("required", nameof(revision));
        BomDigest = ContractGuards.Sha256(bomDigest, nameof(bomDigest));
    }

    public CadBomIdentity(CadProjectHandle project, CadRevision revision, string bomDigest)
        : this(project?.Value ?? throw new CadContractException("required", nameof(project)), revision, bomDigest)
    {
    }

    public string ProjectId { get; }
    public CadRevision Revision { get; }
    public string BomDigest { get; }
}

public sealed class CadCommercialParty
{
    public CadCommercialParty(
        string organization,
        string contactName,
        IEnumerable<string> addressLines,
        string city,
        string region,
        string postalCode,
        string countryCode,
        string email,
        string phone)
    {
        Organization = CadCommercialText.Normalize(organization, nameof(organization), 256, required: true);
        ContactName = CadCommercialText.Normalize(contactName, nameof(contactName), 256);
        AddressLines = ContractGuards.Copy(
            addressLines?.Select(line => CadCommercialText.Normalize(line, nameof(addressLines), 256, required: true)),
            nameof(addressLines),
            CadCommercialLimits.MaximumAddressLines);
        City = CadCommercialText.Normalize(city, nameof(city), 256);
        Region = CadCommercialText.Normalize(region, nameof(region), 256);
        PostalCode = CadCommercialText.Normalize(postalCode, nameof(postalCode), 32);
        CountryCode = NormalizeCountry(countryCode);
        Email = NormalizeEmail(email);
        Phone = CadCommercialText.Normalize(phone, nameof(phone), 64);
    }

    public string Organization { get; }
    public string ContactName { get; }
    public IReadOnlyList<string> AddressLines { get; }
    public string City { get; }
    public string Region { get; }
    public string PostalCode { get; }
    public string CountryCode { get; }
    public string Email { get; }
    public string Phone { get; }

    private static string NormalizeCountry(string value)
    {
        var normalized = CadCommercialText.Normalize(value, nameof(value), 2, required: true).ToUpperInvariant();
        if (normalized.Length != 2 || normalized.Any(character => !char.IsAsciiLetter(character)))
            throw new CadContractException("invalid_country", nameof(value));
        return normalized;
    }

    private static string NormalizeEmail(string value)
    {
        var normalized = CadCommercialText.Normalize(value, nameof(value), 320).ToLowerInvariant();
        if (normalized.Length == 0) return normalized;
        var separator = normalized.IndexOf('@');
        var finalDot = normalized.LastIndexOf('.');
        if (separator < 1 || separator != normalized.LastIndexOf('@') || separator > 128 ||
            finalDot <= separator + 1 || finalDot >= normalized.Length - 2 ||
            normalized.Any(char.IsWhiteSpace))
            throw new CadContractException("invalid_email", nameof(value));
        return normalized;
    }
}

public sealed class CadCommercialLine
{
    public CadCommercialLine(
        string lineId,
        string? sourceBomRowId,
        string partNumber,
        string description,
        decimal quantity,
        CadCommercialUnit unit,
        long unitPriceMinorUnits,
        bool taxable)
    {
        LineId = ContractGuards.Identifier(lineId, nameof(lineId));
        SourceBomRowId = sourceBomRowId is null
            ? null
            : ContractGuards.Identifier(sourceBomRowId, nameof(sourceBomRowId));
        PartNumber = CadCommercialText.Normalize(partNumber, nameof(partNumber), 256, required: true);
        Description = CadCommercialText.Normalize(
            description,
            nameof(description),
            CadContractLimits.DescriptionLength,
            required: true);
        Quantity = ContractGuards.Decimal(quantity, 0.000001m, CadCommercialLimits.MaximumQuantity, 6, nameof(quantity));
        Unit = ContractGuards.EnumValue(unit, nameof(unit));
        UnitPriceMinorUnits = CadCommercialMath.MinorUnits(unitPriceMinorUnits, nameof(unitPriceMinorUnits));
        Taxable = taxable;
        ExtendedPriceMinorUnits = CadCommercialMath.MultiplyToMinorUnits(Quantity, UnitPriceMinorUnits, "extendedPriceMinorUnits");
    }

    public string LineId { get; }
    public string? SourceBomRowId { get; }
    public string PartNumber { get; }
    public string Description { get; }
    public decimal Quantity { get; }
    public CadCommercialUnit Unit { get; }
    public long UnitPriceMinorUnits { get; }
    public bool Taxable { get; }
    public long ExtendedPriceMinorUnits { get; }
}

public sealed class CadCommercialTotals
{
    internal CadCommercialTotals(
        long lineSubtotalMinorUnits,
        long markupMinorUnits,
        long discountMinorUnits,
        long freightMinorUnits,
        long taxableSubtotalMinorUnits,
        long taxMinorUnits,
        long totalMinorUnits)
    {
        LineSubtotalMinorUnits = lineSubtotalMinorUnits;
        MarkupMinorUnits = markupMinorUnits;
        DiscountMinorUnits = discountMinorUnits;
        FreightMinorUnits = freightMinorUnits;
        TaxableSubtotalMinorUnits = taxableSubtotalMinorUnits;
        TaxMinorUnits = taxMinorUnits;
        TotalMinorUnits = totalMinorUnits;
    }

    public long LineSubtotalMinorUnits { get; }
    public long MarkupMinorUnits { get; }
    public long DiscountMinorUnits { get; }
    public long FreightMinorUnits { get; }
    public long TaxableSubtotalMinorUnits { get; }
    public long TaxMinorUnits { get; }
    public long TotalMinorUnits { get; }
}

public sealed class CadCommercialDraft
{
    public CadCommercialDraft(
        CadCommercialDocumentKind documentKind,
        string documentNumber,
        CadBomIdentity bom,
        DateOnly issueDate,
        DateOnly? dueDate,
        string currency,
        int currencyScale,
        CadCommercialParty seller,
        CadCommercialParty customer,
        IEnumerable<CadCommercialLine> lines,
        int markupBasisPoints,
        long discountMinorUnits,
        long freightMinorUnits,
        int taxBasisPoints,
        string terms,
        string notes)
    {
        DocumentKindValue = ContractGuards.EnumValue(documentKind, nameof(documentKind));
        DocumentNumber = CadCommercialText.Normalize(documentNumber, nameof(documentNumber), 128, required: true);
        Bom = bom ?? throw new CadContractException("required", nameof(bom));
        IssueDate = issueDate;
        DueDate = dueDate;
        if (DueDate is not null && DueDate.Value < IssueDate)
            throw new CadContractException("due_date_before_issue_date", nameof(dueDate));
        Currency = NormalizeCurrency(currency);
        CurrencyScale = ContractGuards.Range(currencyScale, 0, CadCommercialLimits.MaximumCurrencyScale, nameof(currencyScale));
        Seller = seller ?? throw new CadContractException("required", nameof(seller));
        Customer = customer ?? throw new CadContractException("required", nameof(customer));
        Lines = ContractGuards.Copy(lines, nameof(lines), CadContractLimits.MaximumCommercialLines, requireAny: true);
        ContractGuards.RequireUnique(Lines.Select(line => line.LineId), nameof(lines), StringComparer.OrdinalIgnoreCase);
        MarkupBasisPoints = ContractGuards.Range(markupBasisPoints, 0, CadCommercialLimits.MaximumBasisPoints, nameof(markupBasisPoints));
        DiscountMinorUnits = CadCommercialMath.MinorUnits(discountMinorUnits, nameof(discountMinorUnits));
        FreightMinorUnits = CadCommercialMath.MinorUnits(freightMinorUnits, nameof(freightMinorUnits));
        TaxBasisPoints = ContractGuards.Range(taxBasisPoints, 0, CadCommercialLimits.MaximumBasisPoints, nameof(taxBasisPoints));
        Terms = CadCommercialText.Normalize(terms, nameof(terms), CadContractLimits.DescriptionLength);
        Notes = CadCommercialText.Normalize(notes, nameof(notes), CadContractLimits.DescriptionLength);
        Totals = CadCommercialMath.Totals(this);
        CommercialDraftFingerprint = CadCommercialFingerprint.ForDraft(this);
    }

    public int ContractVersion => CadContractVersions.Host;
    public CadCommercialDocumentKind DocumentKindValue { get; }
    public string DocumentKind => DocumentKindValue == CadCommercialDocumentKind.Quote ? "quote" : "invoice";
    public string DocumentNumber { get; }
    public CadBomIdentity Bom { get; }
    public string ProjectId => Bom.ProjectId;
    public CadRevision ProjectRevision => Bom.Revision;
    public string BomDigest => Bom.BomDigest;
    public DateOnly IssueDate { get; }
    public DateOnly? DueDate { get; }
    public string Currency { get; }
    public int CurrencyScale { get; }
    public CadCommercialParty Seller { get; }
    public CadCommercialParty Customer { get; }
    public IReadOnlyList<CadCommercialLine> Lines { get; }
    public int MarkupBasisPoints { get; }
    public long DiscountMinorUnits { get; }
    public long FreightMinorUnits { get; }
    public int TaxBasisPoints { get; }
    public string Terms { get; }
    public string Notes { get; }
    public CadCommercialTotals Totals { get; }
    public string CommercialDraftFingerprint { get; }
    public bool DraftOnly => true;
    public string DeliveryState => "not-sent";
    public string AccountingState => "not-posted";
    public string PaymentState => "not-paid";

    private static string NormalizeCurrency(string value)
    {
        var normalized = CadCommercialText.Normalize(value, nameof(value), 3, required: true).ToUpperInvariant();
        if (normalized.Length != 3 || normalized.Any(character => !char.IsAsciiLetter(character)))
            throw new CadContractException("invalid_currency", nameof(value));
        return normalized;
    }
}

public sealed class CadCommercialOutputAction
{
    private CadCommercialOutputAction(
        CadCommercialOutputKind kind,
        CadExportDestinationHandle? destination,
        CadPrinterHandle? printer,
        int copies)
    {
        KindValue = ContractGuards.EnumValue(kind, nameof(kind));
        Destination = destination;
        Printer = printer;
        Copies = copies;
    }

    public CadCommercialOutputKind KindValue { get; }
    public string Kind => KindValue == CadCommercialOutputKind.ExportPdf ? "export-pdf" : "print";
    public CadExportDestinationHandle? Destination { get; }
    public CadPrinterHandle? Printer { get; }
    public int Copies { get; }

    public static CadCommercialOutputAction ExportPdf(CadExportDestinationHandle destination) =>
        new(CadCommercialOutputKind.ExportPdf,
            destination ?? throw new CadContractException("required", nameof(destination)),
            null,
            0);

    public static CadCommercialOutputAction Print(CadPrinterHandle printer, int copies) =>
        new(CadCommercialOutputKind.Print,
            null,
            printer ?? throw new CadContractException("required", nameof(printer)),
            ContractGuards.Range(copies, 1, CadCommercialLimits.MaximumCopies, nameof(copies)));
}

public static class CadCommercialWireValue
{
    public static DateOnly ParseIsoDate(string value, string field)
    {
        var normalized = CadCommercialText.Normalize(value, field, 10, required: true);
        if (!DateOnly.TryParseExact(
            normalized,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed))
            throw new CadContractException("invalid_iso_date", field);
        return parsed;
    }

    public static DateTimeOffset ParseUtcInstant(string value, string field)
    {
        var normalized = CadCommercialText.Normalize(value, field, 35, required: true);
        if (!normalized.EndsWith('Z') || !DateTimeOffset.TryParseExact(
            normalized,
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed))
            throw new CadContractException("invalid_utc_timestamp", field);
        return parsed;
    }
}

internal static class CadCommercialText
{
    internal static string Normalize(string? value, string field, int maximumLength, bool required = false)
    {
        if (value is null) throw new CadContractException("required", field);
        string normalized;
        try
        {
            normalized = value.Normalize(NormalizationForm.FormC).Trim();
        }
        catch (ArgumentException)
        {
            throw new CadContractException("invalid_unicode", field);
        }
        if (required && normalized.Length == 0) throw new CadContractException("required", field);
        if (normalized.Length > maximumLength) throw new CadContractException("too_long", field);
        foreach (var character in normalized)
        {
            if (char.IsControl(character) || char.GetUnicodeCategory(character) == UnicodeCategory.Format)
                throw new CadContractException("unsafe_character", field);
        }
        return normalized;
    }
}

internal static class CadCommercialHandle
{
    internal static string Normalize(string? value, string field, string prefix)
    {
        var normalized = CadCommercialText.Normalize(value, field, prefix.Length + 160, required: true);
        if (!normalized.StartsWith(prefix, StringComparison.Ordinal) ||
            normalized.Length < prefix.Length + 32 || normalized.Length > prefix.Length + 160)
            throw new CadContractException("invalid_handle", field);
        foreach (var character in normalized.AsSpan(prefix.Length))
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-')
                throw new CadContractException("invalid_handle", field);
        }
        return normalized;
    }
}
