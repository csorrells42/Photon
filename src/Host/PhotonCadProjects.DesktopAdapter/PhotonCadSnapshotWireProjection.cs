using PhotonCadProjects.Codec;

namespace PhotonCadProjects.DesktopAdapter;

/// <summary>
/// One path-free wire projection for both committed runtime results and persisted project loads.
/// Occurrences remain explicit for transform-aware consumers and are also represented as bounded
/// pseudo-entities for the existing tree and preview-selection contract.
/// </summary>
public static class PhotonCadSnapshotWireProjection
{
    public static object Snapshot(PhotonCadProjectStateV1 state, int contractVersion)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (contractVersion != PhotonCadProjectContract.Version)
            throw new PhotonCadProjectException("invalid_contract_version", nameof(contractVersion));

        var entities = Entities(state);
        var occurrences = state.Occurrences.Select(Occurrence).ToArray();
        return new
        {
            contractVersion,
            sessionId = state.SessionId,
            projectId = state.ProjectId,
            revision = state.Revision,
            title = state.Title,
            units = Unit(state.Units),
            mode = "canonical",
            entities,
            occurrences,
            operations = state.Operations.Select(Operation).ToArray(),
            issues = state.Issues.Select(Issue).ToArray(),
            dirty = state.Dirty,
        };
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> Entities(PhotonCadProjectStateV1 state)
    {
        var projected = new List<IReadOnlyDictionary<string, object?>>(state.Entities.Count + state.Occurrences.Count);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sources = new Dictionary<string, PhotonCadEntityV1>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in state.Entities)
        {
            if (entity.Kind == PhotonCadEntityKindV1.Occurrence)
                throw new PhotonCadProjectException("invalid_projection_entity_kind", "entities");
            if (!ids.Add(entity.Id) || !sources.TryAdd(entity.Id, entity))
                throw new PhotonCadProjectException("duplicate_projection_entity", "entities");
            var value = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = entity.Id,
                ["parentId"] = entity.ParentId,
                ["kind"] = entity.Kind.ToString().ToLowerInvariant(),
                ["name"] = entity.Name,
                ["visible"] = entity.Visible,
                ["suppressed"] = entity.Suppressed,
            };
            if (entity.SourceCapabilityId is not null) value["sourceCapabilityId"] = entity.SourceCapabilityId;
            projected.Add(value);
        }

        foreach (var occurrence in state.Occurrences)
        {
            if (!ids.Add(occurrence.OccurrenceId)
                || !sources.TryGetValue(occurrence.SourceEntityId, out var source))
                throw new PhotonCadProjectException("invalid_projection_occurrence", "occurrences");
            var value = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = occurrence.OccurrenceId,
                ["parentId"] = occurrence.ParentOccurrenceId,
                ["kind"] = "occurrence",
                ["name"] = occurrence.PartNumber,
                ["visible"] = true,
                ["suppressed"] = false,
            };
            if (source.SourceCapabilityId is not null) value["sourceCapabilityId"] = source.SourceCapabilityId;
            projected.Add(value);
        }
        return projected;
    }

    private static object Occurrence(PhotonCadOccurrenceV1 occurrence) => new
    {
        occurrenceId = occurrence.OccurrenceId,
        parentOccurrenceId = occurrence.ParentOccurrenceId,
        partNumber = occurrence.PartNumber,
        sourceEntityId = occurrence.SourceEntityId,
        transform = occurrence.Transform.ToArray(),
    };

    private static object Operation(PhotonCadOperationV1 operation) => new
    {
        id = operation.Id,
        capabilityId = operation.CapabilityId,
        label = operation.Label,
        createdAtUtc = Utc(operation.CreatedAtUtc),
        state = operation.State.ToString().ToLowerInvariant(),
    };

    private static object Issue(PhotonCadIssueV1 issue) => new
    {
        code = issue.Code,
        severity = issue.Severity switch
        {
            PhotonCadIssueSeverityV1.Information => "info",
            PhotonCadIssueSeverityV1.Warning => "warning",
            PhotonCadIssueSeverityV1.Error => "error",
            _ => throw new PhotonCadProjectException("invalid_issue_severity", nameof(issue)),
        },
        message = issue.Message,
        entityIds = issue.EntityIds.ToArray(),
    };

    private static string Unit(PhotonCadProjectUnit unit) => unit switch
    {
        PhotonCadProjectUnit.Millimeter => "millimeter",
        PhotonCadProjectUnit.Inch => "inch",
        _ => throw new PhotonCadProjectException("invalid_project_unit", nameof(unit)),
    };

    private static string Utc(DateTimeOffset value) => value.ToUniversalTime()
        .ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);
}
