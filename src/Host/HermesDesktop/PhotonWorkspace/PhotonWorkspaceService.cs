using System.Security.Cryptography;
using System.Text;

namespace HermesDesktop.PhotonWorkspace;

public sealed class PhotonWorkspaceService : IAsyncDisposable
{
    private readonly PhotonWorkspacePathPolicy paths;
    private readonly PhotonWorkspaceNativeRoot native;
    private readonly PhotonWorkspaceContextBinding binding;
    private readonly PhotonWorkspaceRequestRegistry requests;
    private readonly PhotonWorkspaceSnapshotRegistry snapshots;
    private readonly PhotonWorkspaceTrashRegistry trashRecords = new();
    private readonly PhotonWorkspaceLatestWins latestWins;
    private readonly PhotonWorkspaceActivityTracker activity = new();
    private readonly PhotonWorkspaceTestHooks hooks;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim mutationGate = new(1, 1);
    private readonly SemaphoreSlim mutationAdmission = new(
        PhotonWorkspaceContract.MaxPendingMutations,
        PhotonWorkspaceContract.MaxPendingMutations);
    private readonly object disposalSync = new();
    private PhotonWorkspaceTrash? trash;
    private Task? disposal;
    private readonly string bindingTag;

    public PhotonWorkspaceService(
        string workspaceRoot,
        string sessionId,
        string principalId,
        string generationId)
        : this(workspaceRoot, sessionId, principalId, generationId, TimeProvider.System, new PhotonWorkspaceTestHooks())
    {
    }

    internal PhotonWorkspaceService(
        string workspaceRoot,
        string sessionId,
        string principalId,
        string generationId,
        TimeProvider clock,
        PhotonWorkspaceTestHooks hooks)
    {
        paths = new PhotonWorkspacePathPolicy(workspaceRoot);
        native = new PhotonWorkspaceNativeRoot(paths);
        binding = new PhotonWorkspaceContextBinding(sessionId, principalId, generationId);
        requests = new PhotonWorkspaceRequestRegistry(clock);
        snapshots = new PhotonWorkspaceSnapshotRegistry(clock);
        latestWins = new PhotonWorkspaceLatestWins(lifetime.Token);
        this.hooks = hooks;
        bindingTag = PhotonWorkspaceGuards.Sha256(
            Encoding.UTF8.GetBytes(sessionId + "\0" + principalId + "\0" + generationId));
    }

    public PhotonWorkspaceCapabilities Capabilities { get; } = new(
        PhotonWorkspaceContract.Version,
        List: true,
        Read: true,
        Create: true,
        Update: true,
        MoveToTrash: true,
        RestoreFromTrash: true,
        DestructiveDelete: false);

    public Task<PhotonWorkspaceListResult> ListAsync(
        PhotonWorkspaceListRequest request,
        CancellationToken cancellationToken = default)
    {
        return FixedErrorsAsync(async () =>
        {
            if (request is null) throw new PhotonWorkspaceException("workspace_request_invalid");
            using var operation = Begin(request.Context);
            var directory = paths.NormalizeDirectory(request.RelativeDirectory);
            if (request.Limit is < 1 or > PhotonWorkspaceContract.MaxListResults)
            {
                throw new PhotonWorkspaceException("workspace_list_limit_invalid");
            }

            var after = request.After is null ? null : paths.NormalizeFile(request.After);
            if (after is not null && directory.Length != 0 &&
                !after.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase))
            {
                throw new PhotonWorkspaceException("workspace_list_cursor_invalid");
            }

            requests.Reserve(request.Context.RequestId);
            using var latest = latestWins.Begin("list:" + directory + ":" + request.Recursive, cancellationToken);
            try
            {
                await InvokeBeforeIoAsync("list", latest.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                latest.ThrowIfCancellationRequested();
                throw;
            }
            latest.ThrowIfCancellationRequested();
            var result = await Task.Run(
                () => ListCore(directory, request.Recursive, after, request.Limit, latest),
                CancellationToken.None).ConfigureAwait(false);
            latest.ThrowIfCancellationRequested();
            return result;
        });
    }

    public Task<PhotonWorkspaceSnapshot> ReadAsync(
        PhotonWorkspaceReadRequest request,
        CancellationToken cancellationToken = default)
    {
        return FixedErrorsAsync(async () =>
        {
            if (request is null) throw new PhotonWorkspaceException("workspace_request_invalid");
            using var operation = Begin(request.Context);
            var relativePath = paths.NormalizeFile(request.RelativePath);
            requests.Reserve(request.Context.RequestId);
            using var latest = latestWins.Begin("read:" + relativePath, cancellationToken);
            try
            {
                await InvokeBeforeIoAsync("read", latest.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                latest.ThrowIfCancellationRequested();
                throw;
            }
            latest.ThrowIfCancellationRequested();
            var evidence = await Task.Run(() =>
            {
                using var file = native.OpenFile(relativePath, includeContent: true);
                return file.Evidence with { Content = file.Evidence.Content?.ToArray() };
            }, CancellationToken.None).ConfigureAwait(false);
            latest.ThrowIfCancellationRequested();
            var snapshot = snapshots.Issue(relativePath, evidence);
            return ToSnapshot(snapshot);
        });
    }

    public Task<PhotonWorkspaceMutationResult> CreateAsync(
        PhotonWorkspaceCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        return FixedErrorsAsync(async () =>
        {
            if (request is null) throw new PhotonWorkspaceException("workspace_request_invalid");
            using var operation = Begin(request.Context);
            var relativePath = paths.NormalizeFile(request.RelativePath);
            var content = PhotonWorkspaceGuards.Content(request.Content);
            requests.Reserve(request.Context.RequestId);
            return await WithMutationGateAsync(cancellationToken, async token =>
            {
                await InvokeBeforeIoAsync("create", token).ConfigureAwait(false);
                return await Task.Run(
                    () => CreateCoreAsync(relativePath, content, token),
                    CancellationToken.None).ConfigureAwait(false);
            }).ConfigureAwait(false);
        });
    }

    public Task<PhotonWorkspaceMutationResult> UpdateAsync(
        PhotonWorkspaceUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        return FixedErrorsAsync(async () =>
        {
            if (request is null) throw new PhotonWorkspaceException("workspace_request_invalid");
            using var operation = Begin(request.Context);
            var relativePath = paths.NormalizeFile(request.RelativePath);
            var content = PhotonWorkspaceGuards.Content(request.Content);
            var expected = snapshots.Require(relativePath, request.Precondition);
            requests.Reserve(request.Context.RequestId);
            return await WithMutationGateAsync(cancellationToken, async token =>
            {
                await InvokeBeforeIoAsync("update", token).ConfigureAwait(false);
                return await Task.Run(
                    () => UpdateCoreAsync(relativePath, content, expected, token),
                    CancellationToken.None).ConfigureAwait(false);
            }).ConfigureAwait(false);
        });
    }

    public Task<PhotonWorkspaceMoveToTrashResult> MoveToTrashAsync(
        PhotonWorkspaceMoveToTrashRequest request,
        CancellationToken cancellationToken = default)
    {
        return FixedErrorsAsync(async () =>
        {
            if (request is null) throw new PhotonWorkspaceException("workspace_request_invalid");
            using var operation = Begin(request.Context);
            var relativePath = paths.NormalizeFile(request.RelativePath);
            var expected = snapshots.Require(relativePath, request.Precondition);
            requests.Reserve(request.Context.RequestId);
            return await WithMutationGateAsync(cancellationToken, async token =>
            {
                await InvokeBeforeIoAsync("move_to_trash", token).ConfigureAwait(false);
                return await Task.Run(
                    () => MoveToTrashCoreAsync(relativePath, expected, token),
                    CancellationToken.None).ConfigureAwait(false);
            }).ConfigureAwait(false);
        });
    }

    public Task<PhotonWorkspaceRestoreResult> RestoreFromTrashAsync(
        PhotonWorkspaceRestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        return FixedErrorsAsync(async () =>
        {
            if (request is null) throw new PhotonWorkspaceException("workspace_request_invalid");
            using var operation = Begin(request.Context);
            var record = trashRecords.Require(request.Receipt, request.Revision);
            var relativePath = paths.NormalizeFile(record.OriginalRelativePath);
            requests.Reserve(request.Context.RequestId);
            return await WithMutationGateAsync(cancellationToken, async token =>
            {
                await InvokeBeforeIoAsync("restore_from_trash", token).ConfigureAwait(false);
                return await Task.Run(
                    () => RestoreCoreAsync(record, relativePath, token),
                    CancellationToken.None).ConfigureAwait(false);
            }).ConfigureAwait(false);
        });
    }

    public Task<PhotonWorkspaceDeleteResult> DeleteAsync(
        PhotonWorkspaceDeleteRequest request,
        CancellationToken cancellationToken = default)
    {
        return FixedErrorsAsync(() =>
        {
            if (request is null) throw new PhotonWorkspaceException("workspace_request_invalid");
            using var operation = Begin(request.Context);
            cancellationToken.ThrowIfCancellationRequested();
            _ = paths.NormalizeFile(request.RelativePath);
            if (request.Precondition is null)
            {
                throw new PhotonWorkspaceException("workspace_precondition_required");
            }

            _ = PhotonWorkspaceGuards.RequireOpaqueToken(request.Precondition.FileHandle, "workspace_precondition_invalid");
            _ = PhotonWorkspaceGuards.RequireOpaqueToken(request.Precondition.Revision, "workspace_precondition_invalid");
            _ = PhotonWorkspaceGuards.RequireSha256(request.Precondition.Sha256, "workspace_precondition_invalid");
            requests.Reserve(request.Context.RequestId);
            return Task.FromResult(new PhotonWorkspaceDeleteResult(
                Available: false,
                PhotonWorkspaceCommitStatus.NotCommitted,
                "destructive_delete_not_approved"));
        });
    }

    public ValueTask DisposeAsync()
    {
        lock (disposalSync)
        {
            disposal ??= DisposeCoreAsync();
            return new ValueTask(disposal);
        }
    }

    internal static PhotonWorkspaceSnapshot ToSnapshot(PhotonWorkspaceSnapshotRecord record)
    {
        if (record.Evidence.Content is null)
        {
            throw new PhotonWorkspaceException("workspace_committed_readback_required");
        }

        return new PhotonWorkspaceSnapshot(
            record.RelativePath,
            record.FileHandle,
            record.Revision,
            record.Evidence.Sha256,
            record.Evidence.ByteLength,
            record.Evidence.LastWriteUtc,
            record.Evidence.Content.ToArray());
    }

    private PhotonWorkspaceActivityTracker.Lease Begin(PhotonWorkspaceRequestContext context)
    {
        var operation = activity.Enter();
        try
        {
            binding.Validate(context);
            return operation;
        }
        catch
        {
            operation.Dispose();
            throw;
        }
    }

    private PhotonWorkspaceListResult ListCore(
        string directory,
        bool recursive,
        string? after,
        int limit,
        PhotonWorkspaceLatestWins.Lease latest)
    {
        var found = new List<PhotonWorkspaceEntry>();
        var pending = new Queue<string>();
        pending.Enqueue(directory);
        var omitted = 0;
        var scanned = 0;
        var scanLimitReached = false;

        while (pending.Count != 0)
        {
            latest.ThrowIfCancellationRequested();
            var currentRelative = pending.Dequeue();
            var sentinel = currentRelative.Length == 0 ? "scan-sentinel" : currentRelative + "/scan-sentinel";
            using var held = native.HoldParent(sentinel);
            var currentPath = currentRelative.Length == 0 ? paths.Root : paths.Resolve(currentRelative);
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateFileSystemEntries(currentPath, "*", SearchOption.TopDirectoryOnly);
                foreach (var child in children)
                {
                    latest.ThrowIfCancellationRequested();
                    if (++scanned > PhotonWorkspaceContract.MaxEnumeratedEntries)
                    {
                        scanLimitReached = true;
                        break;
                    }

                    FileAttributes attributes;
                    try
                    {
                        attributes = File.GetAttributes(child);
                    }
                    catch
                    {
                        omitted++;
                        continue;
                    }

                    if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device | FileAttributes.Offline)) != 0)
                    {
                        omitted++;
                        continue;
                    }

                    var discovered = Path.GetRelativePath(paths.Root, child).Replace(Path.DirectorySeparatorChar, '/');
                    if (!paths.TryNormalizeDiscovered(discovered, out var relativePath))
                    {
                        omitted++;
                        continue;
                    }

                    var directoryEntry = (attributes & FileAttributes.Directory) != 0;
                    long? length = null;
                    DateTime lastWrite;
                    try
                    {
                        if (directoryEntry)
                        {
                            var info = new DirectoryInfo(child);
                            lastWrite = info.LastWriteTimeUtc;
                        }
                        else
                        {
                            var info = new FileInfo(child);
                            length = info.Length;
                            lastWrite = info.LastWriteTimeUtc;
                        }
                    }
                    catch
                    {
                        omitted++;
                        continue;
                    }

                    found.Add(new PhotonWorkspaceEntry(
                        relativePath,
                        directoryEntry ? PhotonWorkspaceEntryKind.Directory : PhotonWorkspaceEntryKind.File,
                        length,
                        PhotonWorkspaceGuards.Utc(lastWrite)));
                    if (recursive && directoryEntry)
                    {
                        pending.Enqueue(relativePath);
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                omitted++;
            }
            catch (IOException)
            {
                omitted++;
            }

            if (scanLimitReached)
            {
                break;
            }

            if (!recursive)
            {
                break;
            }
        }

        var ordered = found
            .OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .Where(entry => after is null || ComparePaths(entry.RelativePath, after) > 0)
            .ToArray();
        var selected = ordered.Take(limit).ToArray();
        var hasMore = ordered.Length > selected.Length;
        return new PhotonWorkspaceListResult(
            selected,
            hasMore && selected.Length != 0 ? selected[^1].RelativePath : null,
            omitted,
            scanLimitReached);
    }

    private async Task<PhotonWorkspaceMutationResult> CreateCoreAsync(
        string relativePath,
        byte[] content,
        CancellationToken cancellationToken)
    {
        using var parent = native.HoldParent(relativePath);
        var targetPath = paths.Resolve(relativePath);
        native.RequireAbsent(targetPath);
        using var stage = native.WriteStage(parent, content, out var stagePath, out _);
        var committed = false;
        try
        {
            await InvokeBeforeCommitAsync("create", cancellationToken).ConfigureAwait(false);
            native.RequireAbsent(targetPath);
            cancellationToken.ThrowIfCancellationRequested();
            native.RenameExact(stage, parent.ParentHandle, Path.GetFileName(targetPath));
            committed = true;
            stage.Dispose();
            native.FlushDirectory(parent.ParentHandle);
            using var created = native.OpenFile(relativePath, includeContent: true, parent);
            if (created.Evidence.ByteLength != content.LongLength ||
                !PhotonWorkspaceGuards.FixedEquals(created.Evidence.Sha256, PhotonWorkspaceGuards.Sha256(content)))
            {
                return new PhotonWorkspaceMutationResult(PhotonWorkspaceCommitStatus.CommittedReadbackRequired, null);
            }

            snapshots.RevokePath(relativePath);
            var issued = snapshots.Issue(relativePath, created.Evidence);
            await InvokeAfterCommitAsync("create").ConfigureAwait(false);
            return new PhotonWorkspaceMutationResult(PhotonWorkspaceCommitStatus.Committed, ToSnapshot(issued));
        }
        catch when (committed)
        {
            return new PhotonWorkspaceMutationResult(PhotonWorkspaceCommitStatus.CommittedReadbackRequired, null);
        }
        finally
        {
            if (!committed)
            {
                native.TryDeleteOwnedInternal(stagePath);
            }
        }
    }

    private async Task<PhotonWorkspaceMutationResult> UpdateCoreAsync(
        string relativePath,
        byte[] content,
        PhotonWorkspaceSnapshotRecord expected,
        CancellationToken cancellationToken)
    {
        using var parent = native.HoldParent(relativePath);
        var targetPath = paths.Resolve(relativePath);
        using var current = native.OpenFile(relativePath, includeContent: false, parent);
        if (!PhotonWorkspaceNativeRoot.EvidenceMatches(current.Evidence, expected.Evidence))
        {
            throw new PhotonWorkspaceException("workspace_precondition_stale");
        }

        using var stage = native.WriteStage(parent, content, out var stagePath, out _);
        var rollbackPath = Path.Combine(parent.ParentPath, ".photon-workspace-rollback-" + PhotonWorkspaceGuards.OpaqueToken());
        var failedNewPath = Path.Combine(parent.ParentPath, ".photon-workspace-failed-" + PhotonWorkspaceGuards.OpaqueToken());
        var committed = false;
        try
        {
            await InvokeBeforeCommitAsync("update", cancellationToken).ConfigureAwait(false);
            if (!PhotonWorkspaceNativeRoot.EvidenceMatches(current.Evidence, expected.Evidence))
            {
                throw new PhotonWorkspaceException("workspace_precondition_stale");
            }

            cancellationToken.ThrowIfCancellationRequested();
            native.RequireAbsent(rollbackPath);
            current.Dispose();
            stage.Dispose();
            native.ReplaceWithRollback(targetPath, stagePath, rollbackPath);
            committed = true;
            native.FlushDirectory(parent.ParentHandle);

            using (var rollback = native.OpenInternalFile(rollbackPath, includeContent: false))
            {
                if (!PhotonWorkspaceNativeRoot.EvidenceMatches(rollback.Evidence, expected.Evidence))
                {
                    native.RequireAbsent(failedNewPath);
                    rollback.Dispose();
                    native.ReplaceWithRollback(targetPath, rollbackPath, failedNewPath);
                    committed = false;
                    native.FlushDirectory(parent.ParentHandle);
                    native.TryDeleteOwnedInternal(failedNewPath);
                    throw new PhotonWorkspaceException("workspace_target_conflict");
                }
            }

            using var updated = native.OpenFile(relativePath, includeContent: true, parent);
            if (updated.Evidence.ByteLength != content.LongLength ||
                !PhotonWorkspaceGuards.FixedEquals(updated.Evidence.Sha256, PhotonWorkspaceGuards.Sha256(content)))
            {
                return new PhotonWorkspaceMutationResult(PhotonWorkspaceCommitStatus.CommittedReadbackRequired, null);
            }

            native.TryDeleteOwnedInternal(rollbackPath);
            snapshots.RevokePath(relativePath);
            var issued = snapshots.Issue(relativePath, updated.Evidence);
            await InvokeAfterCommitAsync("update").ConfigureAwait(false);
            return new PhotonWorkspaceMutationResult(PhotonWorkspaceCommitStatus.Committed, ToSnapshot(issued));
        }
        catch when (committed)
        {
            return new PhotonWorkspaceMutationResult(PhotonWorkspaceCommitStatus.CommittedReadbackRequired, null);
        }
        finally
        {
            if (!committed)
            {
                native.TryDeleteOwnedInternal(stagePath);
                native.TryDeleteOwnedInternal(rollbackPath);
                native.TryDeleteOwnedInternal(failedNewPath);
            }
        }
    }

    private async Task<PhotonWorkspaceMoveToTrashResult> MoveToTrashCoreAsync(
        string relativePath,
        PhotonWorkspaceSnapshotRecord expected,
        CancellationToken cancellationToken)
    {
        using var parent = native.HoldParent(relativePath);
        using var source = native.OpenFile(relativePath, includeContent: false, parent);
        if (!PhotonWorkspaceNativeRoot.EvidenceMatches(source.Evidence, expected.Evidence))
        {
            throw new PhotonWorkspaceException("workspace_precondition_stale");
        }

        await InvokeBeforeCommitAsync("move_to_trash", cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var result = GetTrash().Move(relativePath, source, parent, cancellationToken);
        snapshots.RevokePath(relativePath);
        try
        {
            await InvokeAfterCommitAsync("move_to_trash").ConfigureAwait(false);
            return result;
        }
        catch
        {
            return result with { Status = PhotonWorkspaceCommitStatus.CommittedReadbackRequired };
        }
    }

    private async Task<PhotonWorkspaceRestoreResult> RestoreCoreAsync(
        PhotonWorkspaceTrashRecord record,
        string relativePath,
        CancellationToken cancellationToken)
    {
        using var parent = native.HoldParent(relativePath);
        await InvokeBeforeCommitAsync("restore_from_trash", cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var restored = GetTrash().Restore(record, relativePath, parent, cancellationToken, snapshots);
        try
        {
            await InvokeAfterCommitAsync("restore_from_trash").ConfigureAwait(false);
            return restored;
        }
        catch
        {
            return new PhotonWorkspaceRestoreResult(PhotonWorkspaceCommitStatus.CommittedReadbackRequired, restored.Snapshot);
        }
    }

    private PhotonWorkspaceTrash GetTrash()
    {
        return trash ??= new PhotonWorkspaceTrash(native, trashRecords, bindingTag);
    }

    private async Task DisposeCoreAsync()
    {
        lifetime.Cancel();
        latestWins.Dispose();
        await activity.CloseAndDrainAsync().ConfigureAwait(false);
        trash?.Dispose();
        native.Dispose();
        mutationGate.Dispose();
        mutationAdmission.Dispose();
        lifetime.Dispose();
    }

    private async Task<T> WithMutationGateAsync<T>(
        CancellationToken callerToken,
        Func<CancellationToken, Task<T>> action)
    {
        if (!mutationAdmission.Wait(0))
        {
            throw new PhotonWorkspaceException("workspace_pending_limit");
        }

        var enteredGate = false;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, lifetime.Token);
            await mutationGate.WaitAsync(linked.Token).ConfigureAwait(false);
            enteredGate = true;
            return await action(linked.Token).ConfigureAwait(false);
        }
        finally
        {
            if (enteredGate)
            {
                mutationGate.Release();
            }

            mutationAdmission.Release();
        }
    }

    private async ValueTask InvokeBeforeIoAsync(string operation, CancellationToken token)
    {
        if (hooks.BeforeIoAsync is not null)
        {
            await hooks.BeforeIoAsync(operation, token).ConfigureAwait(false);
        }
    }

    private async ValueTask InvokeBeforeCommitAsync(string operation, CancellationToken token)
    {
        if (hooks.BeforeCommitAsync is not null)
        {
            await hooks.BeforeCommitAsync(operation, token).ConfigureAwait(false);
        }
    }

    private async ValueTask InvokeAfterCommitAsync(string operation)
    {
        if (hooks.AfterCommitAsync is not null)
        {
            await hooks.AfterCommitAsync(operation).ConfigureAwait(false);
        }
    }

    private static int ComparePaths(string left, string right)
    {
        var insensitive = StringComparer.OrdinalIgnoreCase.Compare(left, right);
        return insensitive != 0 ? insensitive : StringComparer.Ordinal.Compare(left, right);
    }

    private static async Task<T> FixedErrorsAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (PhotonWorkspaceException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or CryptographicException)
        {
            throw new PhotonWorkspaceException("workspace_io_failed");
        }
    }
}
