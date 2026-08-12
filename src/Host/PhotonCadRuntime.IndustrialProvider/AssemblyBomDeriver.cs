using PhotonCadProjects;
using PhotonCadProjects.Codec;
using PhotonCadProjects.RuntimeSync;

namespace PhotonCadRuntime.IndustrialProvider;

internal static class AssemblyBomDeriver
{
    internal static IReadOnlyList<PhotonCadBomRow> Derive(
        IReadOnlyDictionary<string, AssemblyPartMetadata> parts,
        IReadOnlyList<PhotonCadOccurrenceV1> occurrences)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var result = new List<PhotonCadBomRow>();
        foreach (var group in occurrences
            .GroupBy(value => value.SourceEntityId, StringComparer.Ordinal)
            .OrderBy(value => value.Key, StringComparer.Ordinal))
        {
            if (!parts.TryGetValue(group.Key, out var template))
                throw AssemblyGuards.Failure("assembly_bom_source_missing");
            var partNumbers = group.Select(value => value.PartNumber).Distinct(StringComparer.Ordinal).ToArray();
            if (partNumbers.Length != 1 || !StringComparer.Ordinal.Equals(partNumbers[0], template.PartNumber))
                throw AssemblyGuards.Failure("assembly_bom_part_number_mismatch");
            result.Add(new PhotonCadBomRow(
                template.PartNumber,
                template.Description,
                group.Count(),
                template.Unit,
                group.Key));
        }
        return result;
    }
}

internal sealed record AssemblyPartMetadata(
    string PartNumber,
    string Description,
    PhotonCadBomUnit Unit);
