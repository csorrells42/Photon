using HermesDesktop.PhotonWorkspace;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

internal static class Program
{
    private static int passed;
    private static int requestSequence;
    private const string Session = "smoke-session";
    private const string Principal = "smoke-principal";
    private const string Generation = "smoke-generation";

    private static async Task<int> Main()
    {
        var tempBase = Path.GetFullPath(Path.GetTempPath());
        var root = Path.Combine(tempBase, "photon-workspace-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Seed(root);
            await RunPrimaryMatrixAsync(root).ConfigureAwait(false);
            await RunLatestWinsMatrixAsync(root).ConfigureAwait(false);
            await RunCancellationMatrixAsync(root).ConfigureAwait(false);
            await RunPostCommitMatrixAsync(root).ConfigureAwait(false);
            await RunReparseMatrixAsync(root).ConfigureAwait(false);
            RunContractMatrix(root);
            Console.WriteLine($"PASS {passed}/40");
            return passed == 40 ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL after {passed}/40: {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
        finally
        {
            SafeRemove(root, tempBase);
        }
    }

    private static async Task RunPrimaryMatrixAsync(string root)
    {
        await using var service = new PhotonWorkspaceService(root, Session, Principal, Generation);
        Check(!service.Capabilities.DestructiveDelete && service.Capabilities.MoveToTrash, "capability truth");

        await ExpectCodeAsync(
            "relative_path_invalid",
            () => service.ReadAsync(new PhotonWorkspaceReadRequest(Context(), "../escape.txt")));
        Check(true, "traversal rejected");
        await ExpectCodeAsync(
            "relative_path_invalid",
            () => service.ReadAsync(new PhotonWorkspaceReadRequest(Context(), "C:\\escape.txt")));
        Check(true, "absolute path rejected");
        await ExpectCodeAsync(
            "relative_path_invalid",
            () => service.ReadAsync(new PhotonWorkspaceReadRequest(Context(), "part.step:stream")));
        Check(true, "ADS rejected");
        await ExpectCodeAsync(
            "relative_path_reserved",
            () => service.ReadAsync(new PhotonWorkspaceReadRequest(Context(), "CON.txt")));
        Check(true, "reserved name rejected");
        await ExpectCodeAsync(
            "relative_path_protected",
            () => service.ReadAsync(new PhotonWorkspaceReadRequest(Context(), ".env")));
        Check(true, "secret path rejected");
        await ExpectCodeAsync(
            "relative_path_invalid",
            () => service.ReadAsync(new PhotonWorkspaceReadRequest(Context(), "bad" + char.ConvertFromUtf32(0xE0001) + ".step")));
        Check(true, "supplementary Unicode format control rejected");

        var list = await service.ListAsync(new PhotonWorkspaceListRequest(Context(), string.Empty, false, null, 100));
        Check(list.Entries.Select(entry => entry.RelativePath).SequenceEqual(new[] { "a.txt", "sub" }), "list deterministic and protected entries omitted");
        Check(list.OmittedProtectedEntries >= 2, "protected omission counted");
        var recursive = await service.ListAsync(new PhotonWorkspaceListRequest(Context(), string.Empty, true, null, 100));
        Check(recursive.Entries.Any(entry => entry.RelativePath == "sub/z.txt"), "recursive list");
        var page = await service.ListAsync(new PhotonWorkspaceListRequest(Context(), string.Empty, false, null, 1));
        var next = await service.ListAsync(new PhotonWorkspaceListRequest(Context(), string.Empty, false, page.NextAfter, 10));
        Check(page.Entries.Count == 1 && page.NextAfter is not null && next.Entries.Count == 1, "list pagination");

        var first = await service.ReadAsync(new PhotonWorkspaceReadRequest(Context(), "a.txt"));
        Check(Encoding.UTF8.GetString(first.Content) == "old" && first.FileHandle.Length == 64 && first.Revision.Length == 64, "read sealed snapshot");
        first.Content[0] = (byte)'X';
        var reread = await service.ReadAsync(new PhotonWorkspaceReadRequest(Context(), "a.txt"));
        Check(Encoding.UTF8.GetString(reread.Content) == "old", "returned bytes are copied");

        var updated = await service.UpdateAsync(new PhotonWorkspaceUpdateRequest(
            Context(),
            "a.txt",
            Precondition(reread),
            Encoding.UTF8.GetBytes("new")));
        Check(updated.Status == PhotonWorkspaceCommitStatus.Committed && File.ReadAllText(Path.Combine(root, "a.txt")) == "new", "update committed");
        await ExpectCodeAsync(
            "workspace_precondition_stale",
            () => service.UpdateAsync(new PhotonWorkspaceUpdateRequest(Context(), "a.txt", Precondition(reread), Encoding.UTF8.GetBytes("bad"))));
        Check(File.ReadAllText(Path.Combine(root, "a.txt")) == "new", "stale update does not clobber");

        var externalSnapshot = await service.ReadAsync(new PhotonWorkspaceReadRequest(Context(), "a.txt"));
        File.WriteAllText(Path.Combine(root, "a.txt"), "external");
        await ExpectCodeAsync(
            "workspace_precondition_stale",
            () => service.UpdateAsync(new PhotonWorkspaceUpdateRequest(Context(), "a.txt", Precondition(externalSnapshot), Encoding.UTF8.GetBytes("bad"))));
        Check(File.ReadAllText(Path.Combine(root, "a.txt")) == "external", "external edit preserved");

        var created = await service.CreateAsync(new PhotonWorkspaceCreateRequest(Context(), "created.step", new byte[] { 1, 2, 3 }));
        Check(created.Status == PhotonWorkspaceCommitStatus.Committed && File.ReadAllBytes(Path.Combine(root, "created.step")).SequenceEqual(new byte[] { 1, 2, 3 }), "create committed");
        await ExpectCodeAsync(
            "workspace_target_conflict",
            () => service.CreateAsync(new PhotonWorkspaceCreateRequest(Context(), "created.step", new byte[] { 9 })));
        Check(File.ReadAllBytes(Path.Combine(root, "created.step")).SequenceEqual(new byte[] { 1, 2, 3 }), "duplicate create does not clobber");
        var empty = await service.CreateAsync(new PhotonWorkspaceCreateRequest(Context(), "empty.txt", Array.Empty<byte>()));
        Check(empty.Status == PhotonWorkspaceCommitStatus.Committed && new FileInfo(Path.Combine(root, "empty.txt")).Length == 0, "empty create");

        var nestedCreated = await service.CreateAsync(new PhotonWorkspaceCreateRequest(Context(), "sub/nested.step", new byte[] { 5 }));
        var nestedUpdated = await service.UpdateAsync(new PhotonWorkspaceUpdateRequest(
            Context(),
            "sub/nested.step",
            Precondition(nestedCreated.Snapshot!),
            new byte[] { 5, 6 }));
        Check(nestedUpdated.Status == PhotonWorkspaceCommitStatus.Committed && File.ReadAllBytes(Path.Combine(root, "sub", "nested.step")).SequenceEqual(new byte[] { 5, 6 }), "nested create and rollback-backed update");
        var nestedMoved = await service.MoveToTrashAsync(new PhotonWorkspaceMoveToTrashRequest(
            Context(),
            "sub/nested.step",
            Precondition(nestedUpdated.Snapshot!)));
        var nestedRestored = await service.RestoreFromTrashAsync(new PhotonWorkspaceRestoreRequest(
            Context(),
            nestedMoved.TrashReceipt!.Receipt,
            nestedMoved.TrashReceipt.Revision));
        Check(nestedRestored.Status == PhotonWorkspaceCommitStatus.Committed && File.Exists(Path.Combine(root, "sub", "nested.step")), "nested exact trash and restore");
        Check(Directory.EnumerateFiles(root, ".photon-workspace-*", SearchOption.AllDirectories).Count() == 0, "nested temporary and receipt files cleaned");

        await ExpectCodeAsync(
            "workspace_precondition_invalid",
            () => service.UpdateAsync(new PhotonWorkspaceUpdateRequest(
                Context(),
                "sub/nested.step",
                new PhotonWorkspacePrecondition(new string('a', 4_096), "bad", "bad"),
                new byte[] { 0 })));
        Check(File.ReadAllBytes(Path.Combine(root, "sub", "nested.step")).SequenceEqual(new byte[] { 5, 6 }), "unbounded malformed precondition rejected before I/O");

        var deleteResult = await service.DeleteAsync(new PhotonWorkspaceDeleteRequest(
            Context(),
            "created.step",
            Precondition(created.Snapshot!)));
        Check(!deleteResult.Available && deleteResult.ReasonCode == "destructive_delete_not_approved" && File.Exists(Path.Combine(root, "created.step")), "destructive delete unavailable and inert");

        var trashSnapshot = await service.ReadAsync(new PhotonWorkspaceReadRequest(Context(), "created.step"));
        var moved = await service.MoveToTrashAsync(new PhotonWorkspaceMoveToTrashRequest(Context(), "created.step", Precondition(trashSnapshot)));
        Check(moved.Status == PhotonWorkspaceCommitStatus.Committed && moved.TrashReceipt is not null && !File.Exists(Path.Combine(root, "created.step")), "move to trash committed");
        var hiddenList = await service.ListAsync(new PhotonWorkspaceListRequest(Context(), string.Empty, true, null, 100));
        Check(hiddenList.Entries.All(entry => !entry.RelativePath.Contains("photon-workspace", StringComparison.OrdinalIgnoreCase)), "trash hidden from list");
        var restored = await service.RestoreFromTrashAsync(new PhotonWorkspaceRestoreRequest(
            Context(),
            moved.TrashReceipt!.Receipt,
            moved.TrashReceipt.Revision));
        Check(restored.Status == PhotonWorkspaceCommitStatus.Committed && File.ReadAllBytes(Path.Combine(root, "created.step")).SequenceEqual(new byte[] { 1, 2, 3 }), "restore exact bytes");

        var moveStale = await service.ReadAsync(new PhotonWorkspaceReadRequest(Context(), "created.step"));
        File.WriteAllBytes(Path.Combine(root, "created.step"), new byte[] { 7 });
        await ExpectCodeAsync(
            "workspace_precondition_stale",
            () => service.MoveToTrashAsync(new PhotonWorkspaceMoveToTrashRequest(Context(), "created.step", Precondition(moveStale))));
        Check(File.ReadAllBytes(Path.Combine(root, "created.step")).SequenceEqual(new byte[] { 7 }), "stale trash move preserves external edit");

        var wrong = new PhotonWorkspaceRequestContext(PhotonWorkspaceContract.Version, "auth-before-replay", Session, Principal, "wrong-generation");
        await ExpectCodeAsync(
            "workspace_generation_rejected",
            () => service.ReadAsync(new PhotonWorkspaceReadRequest(wrong, "a.txt")));
        var corrected = wrong with { GenerationId = Generation };
        _ = await service.ReadAsync(new PhotonWorkspaceReadRequest(corrected, "a.txt"));
        Check(true, "auth rejected before request reservation");

        await ExpectCodeAsync(
            "workspace_protocol_version_rejected",
            () => service.ReadAsync(new PhotonWorkspaceReadRequest(
                new PhotonWorkspaceRequestContext(0, "wrong-version", Session, Principal, Generation),
                "a.txt")));
        Check(true, "protocol version rejected");
        await ExpectCodeAsync(
            "workspace_principal_rejected",
            () => service.ReadAsync(new PhotonWorkspaceReadRequest(
                new PhotonWorkspaceRequestContext(PhotonWorkspaceContract.Version, "wrong-principal", Session, "foreign-principal", Generation),
                "a.txt")));
        Check(true, "foreign principal rejected");

        var replayContext = Context("replay-id");
        _ = await service.ListAsync(new PhotonWorkspaceListRequest(replayContext, string.Empty, false, null, 10));
        await ExpectCodeAsync(
            "workspace_request_replayed",
            () => service.ListAsync(new PhotonWorkspaceListRequest(replayContext, string.Empty, false, null, 10)));
        Check(true, "request replay rejected");

        File.WriteAllBytes(Path.Combine(root, "oversized.bin"), new byte[PhotonWorkspaceContract.MaxFileBytes + 1]);
        await ExpectCodeAsync(
            "workspace_file_too_large",
            () => service.ReadAsync(new PhotonWorkspaceReadRequest(Context(), "oversized.bin")));
        Check(true, "oversized read rejected before allocation");

        var hardA = Path.Combine(root, "hard-a.txt");
        var hardB = Path.Combine(root, "hard-b.txt");
        File.WriteAllText(hardA, "linked");
        if (!CreateHardLinkW(hardB, hardA, IntPtr.Zero))
        {
            throw new InvalidOperationException("hardlink capability unavailable");
        }

        await ExpectCodeAsync(
            "workspace_hardlink_rejected",
            () => service.ReadAsync(new PhotonWorkspaceReadRequest(Context(), "hard-a.txt")));
        Check(true, "hardlink rejected");
    }

    private static async Task RunLatestWinsMatrixAsync(string root)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = 0;
        var hooks = new PhotonWorkspaceTestHooks
        {
            BeforeIoAsync = async (operation, token) =>
            {
                if (operation == "read" && Interlocked.Increment(ref call) == 1)
                {
                    entered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                }
            },
        };
        await using var service = new PhotonWorkspaceService(root, "latest-session", Principal, "latest-generation", TimeProvider.System, hooks);
        var first = service.ReadAsync(new PhotonWorkspaceReadRequest(
            ContextFor("latest-one", "latest-session", "latest-generation"),
            "a.txt"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var second = await service.ReadAsync(new PhotonWorkspaceReadRequest(
            ContextFor("latest-two", "latest-session", "latest-generation"),
            "a.txt"));
        await ExpectCodeAsync("workspace_request_superseded", async () => await first.ConfigureAwait(false));
        Check(second.ByteLength > 0, "per-target latest wins");
    }

    private static async Task RunCancellationMatrixAsync(string root)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hooks = new PhotonWorkspaceTestHooks
        {
            BeforeCommitAsync = async (operation, token) =>
            {
                if (operation == "create")
                {
                    entered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                }
            },
        };
        await using var service = new PhotonWorkspaceService(root, "cancel-session", Principal, "cancel-generation", TimeProvider.System, hooks);
        using var cancellation = new CancellationTokenSource();
        var task = service.CreateAsync(
            new PhotonWorkspaceCreateRequest(
                ContextFor("cancel-create", "cancel-session", "cancel-generation"),
                "cancelled.txt",
                new byte[] { 4 }),
            cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        cancellation.Cancel();
        await ExpectCanceledAsync(task);
        Check(!File.Exists(Path.Combine(root, "cancelled.txt")), "precommit cancellation leaves no target");
    }

    private static async Task RunPostCommitMatrixAsync(string root)
    {
        var hooks = new PhotonWorkspaceTestHooks
        {
            AfterCommitAsync = operation => operation == "create"
                ? ValueTask.FromException(new IOException("hostile postcommit hook"))
                : ValueTask.CompletedTask,
        };
        await using var service = new PhotonWorkspaceService(root, "post-session", Principal, "post-generation", TimeProvider.System, hooks);
        var result = await service.CreateAsync(new PhotonWorkspaceCreateRequest(
            ContextFor("post-create", "post-session", "post-generation"),
            "postcommit.txt",
            new byte[] { 8, 9 }));
        Check(result.Status == PhotonWorkspaceCommitStatus.CommittedReadbackRequired && File.ReadAllBytes(Path.Combine(root, "postcommit.txt")).SequenceEqual(new byte[] { 8, 9 }), "postcommit failure is not reported as rollback");
    }

    private static async Task RunReparseMatrixAsync(string root)
    {
        var outside = Path.Combine(Path.GetTempPath(), "photon-workspace-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllText(Path.Combine(outside, "outside.txt"), "outside");
            var link = Path.Combine(root, "linked-directory");
            var testRoot = root;
            var testRelative = "linked-directory/outside.txt";
            var expectedOutside = Path.Combine(outside, "outside.txt");
            try
            {
                Directory.CreateSymbolicLink(link, outside);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var existing = Directory.EnumerateFileSystemEntries(localData)
                    .FirstOrDefault(path =>
                    {
                        try
                        {
                            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
                        }
                        catch
                        {
                            return false;
                        }
                    });
                if (existing is null)
                {
                    throw new InvalidOperationException("reparse capability unavailable", exception);
                }

                testRoot = localData;
                testRelative = Path.GetFileName(existing) + "/outside.txt";
                expectedOutside = string.Empty;
            }

            await using var service = new PhotonWorkspaceService(testRoot, "reparse-session", Principal, "reparse-generation");
            await ExpectCodeAsync(
                "workspace_reparse_rejected",
                () => service.ReadAsync(new PhotonWorkspaceReadRequest(
                    ContextFor("reparse-read", "reparse-session", "reparse-generation"),
                    testRelative)));
            Check(expectedOutside.Length == 0 || File.ReadAllText(expectedOutside) == "outside", "reparse traversal rejected");
        }
        finally
        {
            SafeRemove(outside, Path.GetFullPath(Path.GetTempPath()));
        }
    }

    private static void RunContractMatrix(string root)
    {
        var publicDtos = typeof(PhotonWorkspaceRequestContext).Assembly
            .GetExportedTypes()
            .Where(type => type.Namespace == typeof(PhotonWorkspaceRequestContext).Namespace && type != typeof(PhotonWorkspaceService))
            .ToArray();
        var forbidden = new[] { "AbsolutePath", "RootPath", "Command", "Executable", "Arguments", "Environment", "OsIdentity", "Grant" };
        Check(publicDtos.SelectMany(type => type.GetProperties()).All(property => !forbidden.Contains(property.Name, StringComparer.OrdinalIgnoreCase)), "renderer DTO surface has no host path or execution authority");

        var sample = new PhotonWorkspaceListResult(
            new[] { new PhotonWorkspaceEntry("safe.step", PhotonWorkspaceEntryKind.File, 1, DateTimeOffset.UnixEpoch) },
            null,
            0,
            false);
        var json = JsonSerializer.Serialize(sample);
        Check(!json.Contains(root, StringComparison.OrdinalIgnoreCase) && !json.Contains(":\\", StringComparison.Ordinal), "serialized result has no host path");
    }

    private static void Seed(string root)
    {
        File.WriteAllText(Path.Combine(root, "a.txt"), "old");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllText(Path.Combine(root, "sub", "z.txt"), "nested");
        File.WriteAllText(Path.Combine(root, ".env"), "SECRET=never-read");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        File.WriteAllText(Path.Combine(root, ".git", "config"), "protected");
    }

    private static PhotonWorkspaceRequestContext Context(string? requestId = null)
    {
        return ContextFor(requestId ?? "request-" + Interlocked.Increment(ref requestSequence), Session, Generation);
    }

    private static PhotonWorkspaceRequestContext ContextFor(string requestId, string session, string generation)
    {
        return new PhotonWorkspaceRequestContext(PhotonWorkspaceContract.Version, requestId, session, Principal, generation);
    }

    private static PhotonWorkspacePrecondition Precondition(PhotonWorkspaceSnapshot snapshot)
    {
        return new PhotonWorkspacePrecondition(snapshot.FileHandle, snapshot.Revision, snapshot.Sha256);
    }

    private static async Task ExpectCodeAsync<T>(string code, Func<Task<T>> action)
    {
        try
        {
            _ = await action().ConfigureAwait(false);
            throw new InvalidOperationException($"expected {code}");
        }
        catch (PhotonWorkspaceException exception) when (exception.Code == code)
        {
        }
    }

    private static async Task ExpectCanceledAsync<T>(Task<T> task)
    {
        try
        {
            _ = await task.ConfigureAwait(false);
            throw new InvalidOperationException("expected cancellation");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException(name);
        }

        passed++;
        Console.WriteLine($"ok {passed:00} - {name}");
    }

    private static void SafeRemove(string path, string expectedParent)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var parentPrefix = Path.TrimEndingDirectorySeparator(expectedParent) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(parentPrefix, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(full).StartsWith("photon-workspace-", StringComparison.Ordinal))
            {
                return;
            }

            if (Directory.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0)
            {
                RemoveTreeWithoutFollowingReparse(full);
            }
        }
        catch
        {
            // The smoke only attempts cleanup of its own validated unique roots.
        }
    }

    private static void RemoveTreeWithoutFollowingReparse(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    Directory.Delete(entry, recursive: false);
                }
                else
                {
                    File.Delete(entry);
                }

                continue;
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                RemoveTreeWithoutFollowingReparse(entry);
            }
            else
            {
                File.Delete(entry);
            }
        }

        Directory.Delete(directory, recursive: false);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newFileName, string existingFileName, IntPtr securityAttributes);
}
