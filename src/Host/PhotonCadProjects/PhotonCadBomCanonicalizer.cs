using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace PhotonCadProjects;

/// <summary>
/// Canonical BOM digest used by persistence, export, and commercial-document boundaries.
/// </summary>
public static class PhotonCadBomCanonicalizer
{
    private static readonly byte[] Header = "photon.cad.bom/v1"u8.ToArray();

    public static string Compute(PhotonCadProjectUnit units, IEnumerable<PhotonCadBomRow> rows)
    {
        _ = ProjectGuards.Enum(units, nameof(units));
        ArgumentNullException.ThrowIfNull(rows);
        var ordered = rows.ToArray();
        if (ordered.Length > PhotonCadProjectContract.MaximumBomRows || ordered.Any(row => row is null))
            throw new PhotonCadProjectException("invalid_bom_size", nameof(rows));
        Array.Sort(ordered, static (left, right) =>
        {
            var source = StringComparer.Ordinal.Compare(left.SourceEntityId, right.SourceEntityId);
            return source != 0 ? source : StringComparer.Ordinal.Compare(left.PartNumber, right.PartNumber);
        });
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (ordered.Any(row => !keys.Add($"{row.SourceEntityId}\0{row.PartNumber}")))
            throw new PhotonCadProjectException("duplicate_bom_row", nameof(rows));

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Header);
        hash.AppendData([0, UnitByte(units)]);
        AppendUInt32(hash, checked((uint)ordered.Length));
        Span<byte> quantity = stackalloc byte[sizeof(long)];
        foreach (var row in ordered)
        {
            AppendText(hash, row.SourceEntityId);
            AppendText(hash, row.PartNumber);
            AppendText(hash, row.Description);
            BinaryPrimitives.WriteInt64BigEndian(quantity, BitConverter.DoubleToInt64Bits(row.Quantity));
            hash.AppendData(quantity);
            hash.AppendData([BomUnitByte(row.Unit)]);
        }
        return $"sha256:{Convert.ToHexStringLower(hash.GetHashAndReset())}";
    }

    private static void AppendText(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value.Normalize(NormalizationForm.FormC));
        AppendUInt32(hash, checked((uint)bytes.Length));
        hash.AppendData(bytes);
    }

    private static void AppendUInt32(IncrementalHash hash, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static byte UnitByte(PhotonCadProjectUnit unit) => unit switch
    {
        PhotonCadProjectUnit.Millimeter => 0x01,
        PhotonCadProjectUnit.Inch => 0x02,
        _ => throw new PhotonCadProjectException("unsupported_enum", nameof(unit)),
    };

    private static byte BomUnitByte(PhotonCadBomUnit unit) => unit switch
    {
        PhotonCadBomUnit.Each => 0x01,
        PhotonCadBomUnit.Length => 0x02,
        _ => throw new PhotonCadProjectException("unsupported_enum", nameof(unit)),
    };
}
