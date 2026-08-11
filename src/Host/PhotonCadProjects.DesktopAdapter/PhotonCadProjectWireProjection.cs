using PhotonCadProjects.Codec;

namespace PhotonCadProjects.DesktopAdapter;

/// <summary>Strict path-free projection from authoritative codec/project objects into wire v1.</summary>
public sealed class PhotonCadProjectWireProjection
{
    private readonly PhotonCadCanonicalProjectCodecV1 _codec;

    public PhotonCadProjectWireProjection(PhotonCadCanonicalProjectCodecV1 codec) =>
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));

    public object Document(PhotonCadProjectDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var state = _codec.Inspect(document.Snapshot);
        return new
        {
            contractVersion = PhotonCadProjectContract.Version,
            workspaceHandle = document.WorkspaceHandle.Value,
            projectHandle = document.ProjectHandle.Value,
            displayName = document.DisplayName,
            snapshot = Snapshot(state),
            contentDigest = document.ContentDigest,
            lastSavedContentDigest = document.LastSavedContentDigest,
            bomDigest = document.BomDigest,
            bom = document.Bom.Select(BomRow).ToArray(),
            openedAtUtc = Utc(document.OpenedAtUtc),
            lastSavedRevision = document.LastSavedRevision,
        };
    }

    public object SaveReceipt(PhotonCadProjectSaveReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return new
        {
            receiptHandle = receipt.ReceiptHandle.Value,
            sourceProjectHandle = receipt.SourceProjectHandle.Value,
            projectHandle = receipt.ProjectHandle.Value,
            sessionId = receipt.SessionId,
            projectId = receipt.ProjectId,
            baseRevision = receipt.BaseRevision,
            savedRevision = receipt.SavedRevision,
            contentDigest = receipt.ContentDigest,
            savedAtUtc = Utc(receipt.SavedAtUtc),
            atomic = receipt.Atomic,
        };
    }

    public static object Reopen(PhotonCadProjectReopenMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return new
        {
            reopenHandle = metadata.ReopenHandle.Value,
            displayName = metadata.DisplayName,
            projectId = metadata.ProjectId,
            lastSavedRevision = metadata.LastSavedRevision,
            contentDigest = metadata.ContentDigest,
            closedAtUtc = Utc(metadata.ClosedAtUtc),
        };
    }

    private static object Snapshot(PhotonCadProjectStateV1 state) => new
    {
        contractVersion = PhotonCadProjectContract.Version,
        sessionId = state.SessionId,
        projectId = state.ProjectId,
        revision = state.Revision,
        title = state.Title,
        units = Unit(state.Units),
        mode = "canonical",
        entities = state.Entities.Select(Entity).ToArray(),
        operations = state.Operations.Select(Operation).ToArray(),
        issues = state.Issues.Select(Issue).ToArray(),
        dirty = state.Dirty,
    };

    private static object Entity(PhotonCadEntityV1 entity)
    {
        var value = new Dictionary<string, object?>
        {
            ["id"] = entity.Id,
            ["parentId"] = entity.ParentId,
            ["kind"] = entity.Kind.ToString().ToLowerInvariant(),
            ["name"] = entity.Name,
            ["visible"] = entity.Visible,
            ["suppressed"] = entity.Suppressed,
        };
        if (entity.SourceCapabilityId is not null) value["sourceCapabilityId"] = entity.SourceCapabilityId;
        return value;
    }

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

    private static object BomRow(PhotonCadBomRow row) => new
    {
        partNumber = row.PartNumber,
        description = row.Description,
        quantity = row.Quantity,
        unit = row.Unit == PhotonCadBomUnit.Each ? "each" : "length",
        sourceEntityId = row.SourceEntityId,
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
