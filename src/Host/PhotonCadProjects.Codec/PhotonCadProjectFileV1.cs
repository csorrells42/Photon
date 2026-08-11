using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PhotonCadProjects.Codec;

public static class PhotonCadProjectFileV1
{
    public const int FormatVersion = 1;
    public const int ContractVersion = 1;
    public const int HeaderLength = 96;
    public const int BlobHeaderLength = 64;
    public const int MaximumEncodedBytes = 128 * 1024 * 1024;
    public const int MaximumManifestBytes = 64 * 1024 * 1024;
    public const int MaximumBlobCount = 10_002;
    public const int MaximumEntities = 10_000;
    public const int MaximumOperations = 5_000;
    public const int MaximumIssues = 2_000;
    public const int MaximumOccurrences = 50_000;
    public const int MaximumInputsPerOperation = 128;
    public const int MaximumTargetsPerOperation = 10_000;
    public const int MaximumEntityTreeDepth = 256;
    public const int MaximumJsonDepth = 32;

    internal static ReadOnlySpan<byte> Magic =>
    [
        0x50, 0x48, 0x4f, 0x54, 0x4f, 0x4e, 0x43, 0x41,
        0x44, 0x50, 0x52, 0x4a, 0x01, 0x00, 0x00, 0x00,
    ];

    internal static ReadOnlySpan<byte> LogicalDigestDomain => "photon.cad.project.logical/v1\0"u8;
}

public enum PhotonCadEntityKindV1
{
    Body,
    Part,
    Assembly,
    Occurrence,
    Drawing,
    Datum,
}

public enum PhotonCadOperationStateV1
{
    Proposed,
    Applied,
    Rejected,
}

public enum PhotonCadOperationModeV1
{
    Suggest,
    Scratch,
}

public enum PhotonCadIssueSeverityV1
{
    Information,
    Warning,
    Error,
}

public enum PhotonCadInputKindV1
{
    Number,
    Integer,
    Boolean,
    Text,
    Choice,
    Vector3,
    Entity,
    EntityList,
}

public enum PhotonCadArtifactKindV1 : byte
{
    Step = 1,
    Glb = 2,
}

public enum PhotonCadArtifactRoleV1
{
    AuthoritativeGeometry,
    ProjectPreview,
}

public enum PhotonCadBackendV1
{
    Geometry,
    Assembly,
}

public interface IPhotonCadProjectIdentityIssuerV1
{
    (string SessionId, string ProjectId) NewIdentity();
}

public sealed class CryptographicPhotonCadProjectIdentityIssuerV1 : IPhotonCadProjectIdentityIssuerV1
{
    public (string SessionId, string ProjectId) NewIdentity() =>
        ($"pcsid:{Token()}", $"pcpid:{Token()}");

    private static string Token() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');
}

internal static class PhotonCadFileGuardsV1
{
    private static readonly Regex IdentifierPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex TokenPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9_.:+-]{0,255}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex RuntimeHandlePattern = new(
        "^(?:(?:wsp|prj|ses|art|prv|pkg|req)_[a-f0-9]{32}|cad-(?:workspace|project|reopen|save-receipt|storage-target|storage-stage|recovery|overwrite-grant|bom-review|commercial-review|commercial-approval|document-page|destination|printer):[A-Za-z0-9_-]{32,})$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static PhotonCadProjectException Failure(string code, string field) => new(code, field);

    internal static string Identifier(string? value, string field)
    {
        var safe = Text(value, field, 128, required: true);
        if (!IdentifierPattern.IsMatch(safe)) throw Failure("invalid_identifier", field);
        RejectRuntimeHandle(safe, field);
        return safe;
    }

    internal static string Token(string? value, string field, int maximum = 256)
    {
        var safe = Text(value, field, maximum, required: true);
        if (!TokenPattern.IsMatch(safe)) throw Failure("invalid_token", field);
        RejectRuntimeHandle(safe, field);
        return safe;
    }

    internal static string Digest(string? value, string field)
    {
        var safe = Text(value, field, 71, required: true).ToLowerInvariant();
        if (safe.StartsWith("sha256:", StringComparison.Ordinal)) safe = safe[7..];
        if (safe.Length != 64 || safe.Any(character => !char.IsAsciiHexDigit(character)))
            throw Failure("invalid_digest", field);
        return $"sha256:{safe}";
    }

    internal static string Text(string? value, string field, int maximum, bool required)
    {
        if (value is null) throw Failure("required", field);
        string normalized;
        try
        {
            normalized = value.Normalize(NormalizationForm.FormC);
            _ = StrictUtf8.GetByteCount(normalized);
        }
        catch (ArgumentException exception)
        {
            throw new PhotonCadProjectException("invalid_unicode", field, exception);
        }

        if (normalized.Length > maximum) throw Failure("too_long", field);
        if (required && string.IsNullOrWhiteSpace(normalized)) throw Failure("required", field);
        foreach (var rune in normalized.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate)
                throw Failure("unsafe_character", field);
        }
        return normalized;
    }

    internal static string DisplayName(string? value, string field, int maximum)
    {
        var safe = Text(value, field, maximum, required: true);
        if (safe.Contains('/') || safe.Contains('\\') || (safe.Length >= 2 && char.IsAsciiLetter(safe[0]) && safe[1] == ':'))
            throw Failure("path_like_display_name", field);
        return safe;
    }

    internal static DateTimeOffset Utc(DateTimeOffset value, string field)
    {
        if (value == DateTimeOffset.MinValue || value == DateTimeOffset.MaxValue) throw Failure("invalid_timestamp", field);
        return value.ToUniversalTime();
    }

    internal static long SafeInteger(long value, string field, long minimum = 0)
    {
        if (value < minimum || value > PhotonCadProjectContract.MaximumSafeInteger)
            throw Failure("invalid_safe_integer", field);
        return value;
    }

    internal static IReadOnlyList<T> Copy<T>(IEnumerable<T>? values, string field, int maximum)
    {
        if (values is null) throw Failure("required", field);
        var copy = values.ToArray();
        if (copy.Length > maximum || copy.Any(value => value is null)) throw Failure("invalid_collection_size", field);
        return Array.AsReadOnly(copy);
    }

    internal static double Finite(double value, string field, double maximumMagnitude = 1_000_000_000d)
    {
        if (!double.IsFinite(value) || Math.Abs(value) > maximumMagnitude) throw Failure("invalid_number", field);
        return value == 0d ? 0d : value;
    }

    internal static string F64Hex(double value, string field)
    {
        var finite = Finite(value, field);
        return BitConverter.DoubleToUInt64Bits(finite).ToString("x16", CultureInfo.InvariantCulture);
    }

    internal static double ParseF64Hex(string? value, string field)
    {
        if (value is null || value.Length != 16 || value.Any(character => !char.IsAsciiHexDigit(character) || char.IsUpper(character)))
            throw Failure("invalid_f64", field);
        if (!ulong.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var bits))
            throw Failure("invalid_f64", field);
        if (bits == 0x8000_0000_0000_0000UL) throw Failure("noncanonical_negative_zero", field);
        return Finite(BitConverter.UInt64BitsToDouble(bits), field);
    }

    internal static string Sha256(ReadOnlySpan<byte> value) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(value))}";

    internal static string RawDigestHex(string digest) => Digest(digest, nameof(digest))[7..];

    internal static bool FixedDigestEquals(string left, string right)
    {
        var leftBytes = Encoding.ASCII.GetBytes(Digest(left, nameof(left)));
        var rightBytes = Encoding.ASCII.GetBytes(Digest(right, nameof(right)));
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    internal static T EnumValue<T>(T value, string field) where T : struct, Enum
    {
        if (!Enum.IsDefined(value)) throw Failure("unsupported_enum", field);
        return value;
    }

    private static void RejectRuntimeHandle(string value, string field)
    {
        if (RuntimeHandlePattern.IsMatch(value)) throw Failure("runtime_handle_not_persistable", field);
    }
}
