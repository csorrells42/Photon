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
            snapshot = PhotonCadSnapshotWireProjection.Snapshot(state, PhotonCadProjectContract.Version),
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

    private static object BomRow(PhotonCadBomRow row) => new
    {
        partNumber = row.PartNumber,
        description = row.Description,
        quantity = row.Quantity,
        unit = row.Unit == PhotonCadBomUnit.Each ? "each" : "length",
        sourceEntityId = row.SourceEntityId,
    };

    private static string Utc(DateTimeOffset value) => value.ToUniversalTime()
        .ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);
}
