using PhotonCadProjects;
using PhotonCadProjects.Codec;
using PhotonCadProjects.RuntimeSync;

namespace PhotonCadRuntime.IndustrialProvider;

internal static class AssemblyBomDeriver
{
    internal static IReadOnlyList<PhotonCadBomRow> Derive(
        IReadOnlyList<PhotonCadProviderBaseBomRow> baseBom,
        IReadOnlyList<PhotonCadOccurrenceV1> occurrences)
    {
        var templates = baseBom
            .GroupBy(row => row.SourceEntityId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var rows = group.ToArray();
                    if (rows.Length != 1) throw AssemblyGuards.Failure("assembly_bom_source_ambiguous");
                    return rows[0];
                },
                StringComparer.Ordinal);
        var result = new List<PhotonCadBomRow>();
        foreach (var group in occurrences
            .GroupBy(value => value.SourceEntityId, StringComparer.Ordinal)
            .OrderBy(value => value.Key, StringComparer.Ordinal))
        {
            if (!templates.TryGetValue(group.Key, out var template))
                throw AssemblyGuards.Failure("assembly_bom_source_missing");
            var partNumbers = group.Select(value => value.PartNumber).Distinct(StringComparer.Ordinal).ToArray();
            if (partNumbers.Length != 1 || !StringComparer.Ordinal.Equals(partNumbers[0], template.PartNumber))
                throw AssemblyGuards.Failure("assembly_bom_part_number_mismatch");
            result.Add(new PhotonCadBomRow(
                template.PartNumber,
                template.Description,
                group.Count(),
                template.Unit,
                template.SourceEntityId));
        }
        return result;
    }
}
