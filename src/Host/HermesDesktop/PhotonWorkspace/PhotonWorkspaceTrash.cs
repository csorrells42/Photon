using Microsoft.Win32.SafeHandles;
using System.Text.Json;

namespace HermesDesktop.PhotonWorkspace;

internal sealed class PhotonWorkspaceTrash : IDisposable
{
    private readonly PhotonWorkspaceNativeRoot native;
    private readonly PhotonWorkspaceTrashRegistry registry;
    private readonly SafeFileHandle trashDirectoryHandle;
    private readonly string trashDirectoryPath;
    private readonly string bindingTag;
    private int disposed;

    internal PhotonWorkspaceTrash(
        PhotonWorkspaceNativeRoot native,
        PhotonWorkspaceTrashRegistry registry,
        string bindingTag)
    {
        this.native = native;
        this.registry = registry;
        this.bindingTag = bindingTag;
        trashDirectoryHandle = native.CreateHeldInternalDirectory(out trashDirectoryPath, out _);
    }

    internal PhotonWorkspaceMoveToTrashResult Move(
        string relativePath,
        PhotonWorkspaceVerifiedFile source,
        PhotonWorkspaceDirectoryLease sourceParent,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        registry.EnsureCapacity();
        var receipt = PhotonWorkspaceGuards.OpaqueToken();
        var revision = PhotonWorkspaceGuards.OpaqueToken();
        var payloadLeaf = receipt + ".payload";
        var sidecarLeaf = receipt + ".json";
        var payloadPath = Path.Combine(trashDirectoryPath, payloadLeaf);
        var sidecarPath = Path.Combine(trashDirectoryPath, sidecarLeaf);

        native.RequireAbsent(payloadPath);
        native.RequireAbsent(sidecarPath);
        WriteSidecar(
            sidecarPath,
            new PhotonWorkspaceTrashSidecar(
                1,
                receipt,
                revision,
                relativePath,
                source.Evidence.Sha256,
                source.Evidence.ByteLength,
                bindingTag,
                "current-generation"));

        var committed = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            native.RenameExact(source, trashDirectoryHandle, payloadLeaf);
            committed = true;
            source.Dispose();
            native.FlushDirectory(sourceParent.ParentHandle);
            native.FlushDirectory(trashDirectoryHandle);

            using var moved = native.OpenInternalFile(payloadPath, includeContent: false);
            if (!PhotonWorkspaceNativeRoot.EvidenceMatches(moved.Evidence, source.Evidence))
            {
                throw new PhotonWorkspaceException("workspace_committed_readback_required");
            }

            var record = new PhotonWorkspaceTrashRecord(
                receipt,
                revision,
                relativePath,
                payloadPath,
                sidecarPath,
                moved.Evidence);
            registry.Add(record);
            return new PhotonWorkspaceMoveToTrashResult(
                PhotonWorkspaceCommitStatus.Committed,
                new PhotonWorkspaceTrashReceipt(
                    receipt,
                    revision,
                    relativePath,
                    moved.Evidence.Sha256,
                    moved.Evidence.ByteLength,
                    "current-generation"));
        }
        catch when (!committed)
        {
            native.TryDeleteOwnedInternal(sidecarPath);
            throw;
        }
        catch
        {
            var record = new PhotonWorkspaceTrashRecord(
                receipt,
                revision,
                relativePath,
                payloadPath,
                sidecarPath,
                source.Evidence);
            try
            {
                registry.Add(record);
            }
            catch (PhotonWorkspaceException)
            {
                // Capacity was reserved under the service mutation gate. If an invariant
                // is broken, the durable sidecar remains the recovery authority.
            }

            return new PhotonWorkspaceMoveToTrashResult(
                PhotonWorkspaceCommitStatus.CommittedReadbackRequired,
                new PhotonWorkspaceTrashReceipt(
                    receipt,
                    revision,
                    relativePath,
                    source.Evidence.Sha256,
                    source.Evidence.ByteLength,
                    "current-generation"));
        }
    }

    internal PhotonWorkspaceRestoreResult Restore(
        PhotonWorkspaceTrashRecord record,
        string normalizedRelativePath,
        PhotonWorkspaceDirectoryLease destinationParent,
        CancellationToken cancellationToken,
        PhotonWorkspaceSnapshotRegistry snapshots)
    {
        ThrowIfDisposed();
        var destinationPath = Path.Combine(
            destinationParent.ParentPath,
            normalizedRelativePath.Split('/')[^1]);
        native.RequireAbsent(destinationPath);

        using var payload = native.OpenInternalFile(record.PayloadPath, includeContent: false);
        if (!PhotonWorkspaceNativeRoot.EvidenceMatches(payload.Evidence, record.Evidence))
        {
            throw new PhotonWorkspaceException("workspace_trash_receipt_stale");
        }

        var committed = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            native.RenameExact(payload, destinationParent.ParentHandle, normalizedRelativePath.Split('/')[^1]);
            committed = true;
            payload.Dispose();
            native.FlushDirectory(destinationParent.ParentHandle);
            native.FlushDirectory(trashDirectoryHandle);

            using var restored = native.OpenFile(normalizedRelativePath, includeContent: true, destinationParent);
            if (!PhotonWorkspaceNativeRoot.EvidenceMatches(restored.Evidence, record.Evidence))
            {
                return new PhotonWorkspaceRestoreResult(PhotonWorkspaceCommitStatus.CommittedReadbackRequired, null);
            }

            registry.Remove(record.Receipt);
            native.TryDeleteOwnedInternal(record.SidecarPath);
            snapshots.RevokePath(normalizedRelativePath);
            var snapshot = snapshots.Issue(normalizedRelativePath, restored.Evidence);
            return new PhotonWorkspaceRestoreResult(
                PhotonWorkspaceCommitStatus.Committed,
                PhotonWorkspaceService.ToSnapshot(snapshot));
        }
        catch when (committed)
        {
            return new PhotonWorkspaceRestoreResult(PhotonWorkspaceCommitStatus.CommittedReadbackRequired, null);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            trashDirectoryHandle.Dispose();
        }
    }

    private void WriteSidecar(string sidecarPath, PhotonWorkspaceTrashSidecar sidecar)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(sidecar);
        if (bytes.Length > 16 * 1024)
        {
            throw new PhotonWorkspaceException("workspace_internal_contract_invalid");
        }

        var created = false;
        try
        {
            using var stream = new FileStream(
                sidecarPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.WriteThrough);
            created = true;
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            native.FlushDirectory(trashDirectoryHandle);
        }
        catch (PhotonWorkspaceException)
        {
            throw;
        }
        catch
        {
            if (created)
            {
                native.TryDeleteOwnedInternal(sidecarPath);
            }

            throw new PhotonWorkspaceException("workspace_trash_unavailable");
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            throw new PhotonWorkspaceException("workspace_service_closed");
        }
    }

    private sealed record PhotonWorkspaceTrashSidecar(
        int Schema,
        string Receipt,
        string Revision,
        string OriginalRelativePath,
        string Sha256,
        long ByteLength,
        string BindingTag,
        string RecoveryScope);
}
