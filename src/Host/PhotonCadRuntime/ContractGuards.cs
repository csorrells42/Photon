using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace PhotonCadRuntime;

public static class CadContractLimits
{
    public const int IdentifierLength = 128;
    public const int OpaqueHandleLength = 100;
    public const int DisplayNameLength = 256;
    public const int DescriptionLength = 2_048;
    public const int MessageLength = 512;
    public const int RelativePathLength = 1_024;
    public const int MaximumCatalogEntries = 2_000;
    public const int MaximumArtifactsPerOperation = 128;
    public const int MaximumBundleArtifacts = 4_096;
    public const int MaximumPackageComponents = 10_000;
    public const int MaximumOccurrences = 50_000;
    public const int MaximumBomItems = 20_000;
    public const int MaximumCommercialLines = 20_000;
    public const int MaximumRenderedPages = 500;
    public const int MaximumChecksums = 20_000;
    public const int MaximumValidationResults = 4_096;
    public const int MaximumValidationFindings = 4_096;
    public const long MaximumArtifactBytes = 8L * 1024 * 1024 * 1024;
    public const long MaximumBrokeredArtifactBytes = 512L * 1024 * 1024;
    public const long MaximumPreviewBytes = 256L * 1024 * 1024;
    public const long MaximumBundleBytes = 32L * 1024 * 1024 * 1024;
}

public sealed class CadContractException : ArgumentException
{
    public CadContractException(string code, string field)
        : base($"CAD contract field '{field}' is invalid.", field)
    {
        Code = ContractGuards.Identifier(code, nameof(code), 64);
        Field = ContractGuards.FieldName(field);
    }

    public string Code { get; }
    public string Field { get; }
}

internal static class ContractGuards
{
    internal static string FieldName(string field)
    {
        if (string.IsNullOrWhiteSpace(field) || field.Length > 128)
            return "field";
        return field.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
            ? field
            : "field";
    }

    internal static string RequiredText(string? value, string field, int maximumLength)
    {
        if (value is null)
            throw new CadContractException("required", field);

        string normalized;
        try
        {
            normalized = value.Normalize(NormalizationForm.FormC).Trim();
        }
        catch (ArgumentException)
        {
            throw new CadContractException("invalid_unicode", field);
        }

        if (normalized.Length == 0)
            throw new CadContractException("required", field);
        if (normalized.Length > maximumLength)
            throw new CadContractException("too_long", field);

        foreach (var character in normalized)
        {
            var category = char.GetUnicodeCategory(character);
            if (char.IsControl(character) || category == UnicodeCategory.Format)
                throw new CadContractException("unsafe_character", field);
        }

        return normalized;
    }

    internal static string? OptionalText(string? value, string field, int maximumLength) =>
        string.IsNullOrWhiteSpace(value) ? null : RequiredText(value, field, maximumLength);

    internal static string Identifier(string? value, string field, int maximumLength = CadContractLimits.IdentifierLength)
    {
        var normalized = RequiredText(value, field, maximumLength);
        if (!char.IsAsciiLetterOrDigit(normalized[0]))
            throw new CadContractException("invalid_identifier", field);

        foreach (var character in normalized)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-' and not '.' and not ':')
                throw new CadContractException("invalid_identifier", field);
        }

        return normalized;
    }

    internal static string Revision(string? value, string field)
    {
        var normalized = RequiredText(value, field, CadContractLimits.IdentifierLength);
        foreach (var character in normalized)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-' and not '.' and not ':')
                throw new CadContractException("invalid_revision", field);
        }

        return normalized;
    }

    internal static string OpaqueHandle(string? value, string field, string prefix)
    {
        var normalized = RequiredText(value, field, CadContractLimits.OpaqueHandleLength);
        var expectedPrefix = $"{prefix}_";
        if (!normalized.StartsWith(expectedPrefix, StringComparison.Ordinal) ||
            normalized.Length < expectedPrefix.Length + 16)
            throw new CadContractException("invalid_handle", field);

        foreach (var character in normalized.AsSpan(expectedPrefix.Length))
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-')
                throw new CadContractException("invalid_handle", field);
        }

        return normalized;
    }

    internal static string Sha256(string? value, string field)
    {
        var normalized = RequiredText(value, field, 71).ToLowerInvariant();
        if (normalized.StartsWith("sha256:", StringComparison.Ordinal))
            normalized = normalized[7..];
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
            throw new CadContractException("invalid_sha256", field);
        return normalized;
    }

    internal static DateTimeOffset Utc(DateTimeOffset value, string field)
    {
        if (value == DateTimeOffset.MinValue || value == DateTimeOffset.MaxValue)
            throw new CadContractException("invalid_timestamp", field);
        return value.ToUniversalTime();
    }

    internal static int Range(int value, int minimum, int maximum, string field)
    {
        if (value < minimum || value > maximum)
            throw new CadContractException("out_of_range", field);
        return value;
    }

    internal static long ByteLength(long value, string field)
    {
        if (value <= 0 || value > CadContractLimits.MaximumArtifactBytes)
            throw new CadContractException("invalid_byte_length", field);
        return value;
    }

    internal static double Finite(double value, string field, double absoluteMaximum = 1e12)
    {
        if (!double.IsFinite(value) || Math.Abs(value) > absoluteMaximum)
            throw new CadContractException("invalid_number", field);
        return value;
    }

    internal static decimal Decimal(
        decimal value,
        decimal minimum,
        decimal maximum,
        int maximumScale,
        string field)
    {
        if (value < minimum || value > maximum)
            throw new CadContractException("out_of_range", field);

        var scale = (decimal.GetBits(value)[3] >> 16) & 0x7f;
        if (scale > maximumScale)
            throw new CadContractException("excessive_decimal_scale", field);
        return value;
    }

    internal static TEnum EnumValue<TEnum>(TEnum value, string field) where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
            throw new CadContractException("unsupported_enum_value", field);
        return value;
    }

    internal static ReadOnlyCollection<T> Copy<T>(IEnumerable<T>? values, string field, int maximumCount, bool requireAny = false)
    {
        if (values is null)
            throw new CadContractException("required", field);
        var copy = values.ToArray();
        if (copy.Length > maximumCount || (requireAny && copy.Length == 0))
            throw new CadContractException("invalid_collection_size", field);
        if (copy.Any(value => value is null))
            throw new CadContractException("null_collection_item", field);
        return Array.AsReadOnly(copy);
    }

    internal static void RequireUnique(IEnumerable<string> values, string field, StringComparer? comparer = null)
    {
        var seen = new HashSet<string>(comparer ?? StringComparer.Ordinal);
        if (values.Any(value => !seen.Add(value)))
            throw new CadContractException("duplicate_item", field);
    }
}
