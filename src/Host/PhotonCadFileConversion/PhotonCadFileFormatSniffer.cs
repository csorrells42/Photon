using System.Text;

namespace PhotonCadFileConversion;

internal static class PhotonCadFileFormatSniffer
{
    private static ReadOnlySpan<byte> StepMarker => "ISO-10303-21;"u8;
    private static ReadOnlySpan<byte> CompoundFileMarker => new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 };

    internal static PhotonCadExternalFormat Detect(ReadOnlySpan<byte> content)
    {
        if (content.StartsWith(StepMarker)) return PhotonCadExternalFormat.StepPart21;
        if (content.Length >= 12
            && content[0] == (byte)'g' && content[1] == (byte)'l' && content[2] == (byte)'T' && content[3] == (byte)'F')
            return PhotonCadExternalFormat.Glb;
        if (content.StartsWith(CompoundFileMarker)) return PhotonCadExternalFormat.OleCompoundDocument;
        return PhotonCadExternalFormat.Unknown;
    }

    internal static void RequireMatchingStepExtension(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        if (!StringComparer.OrdinalIgnoreCase.Equals(extension, ".step") && !StringComparer.OrdinalIgnoreCase.Equals(extension, ".stp"))
            throw PhotonCadFileConversionGuard.Failure("source_extension_mismatch", nameof(fileName));
    }

    internal static bool ContainsAssemblyConstructs(ReadOnlySpan<byte> bytes)
    {
        // STEP Part-21 keywords are ASCII and case-insensitive. This does not claim a normalized DAG;
        // it only records that the exact opaque exchange payload contains known assembly relationships.
        var text = Encoding.ASCII.GetString(bytes);
        return text.Contains("NEXT_ASSEMBLY_USAGE_OCCURRENCE", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ASSEMBLY_COMPONENT_USAGE", StringComparison.OrdinalIgnoreCase);
    }
}
