namespace HermesDesktop.PhotonWorkspace;

internal sealed class PhotonWorkspaceContextBinding
{
    private readonly string sessionId;
    private readonly string principalId;
    private readonly string generationId;

    internal PhotonWorkspaceContextBinding(string sessionId, string principalId, string generationId)
    {
        this.sessionId = PhotonWorkspaceGuards.Identifier(sessionId, "workspace_session_invalid");
        this.principalId = PhotonWorkspaceGuards.Identifier(principalId, "workspace_principal_invalid");
        this.generationId = PhotonWorkspaceGuards.Identifier(generationId, "workspace_generation_invalid");
    }

    internal void Validate(PhotonWorkspaceRequestContext? context)
    {
        if (context is null || context.Version != PhotonWorkspaceContract.Version)
        {
            throw new PhotonWorkspaceException("workspace_protocol_version_rejected");
        }

        PhotonWorkspaceGuards.Identifier(context.RequestId, "workspace_request_invalid");
        var candidateSession = PhotonWorkspaceGuards.Identifier(context.SessionId, "workspace_session_invalid");
        var candidatePrincipal = PhotonWorkspaceGuards.Identifier(context.PrincipalId, "workspace_principal_invalid");
        var candidateGeneration = PhotonWorkspaceGuards.Identifier(context.GenerationId, "workspace_generation_invalid");
        if (!PhotonWorkspaceGuards.FixedEquals(sessionId, candidateSession))
        {
            throw new PhotonWorkspaceException("workspace_session_rejected");
        }

        if (!PhotonWorkspaceGuards.FixedEquals(principalId, candidatePrincipal))
        {
            throw new PhotonWorkspaceException("workspace_principal_rejected");
        }

        if (!PhotonWorkspaceGuards.FixedEquals(generationId, candidateGeneration))
        {
            throw new PhotonWorkspaceException("workspace_generation_rejected");
        }
    }
}

internal sealed class PhotonWorkspaceRequestRegistry
{
    private readonly object sync = new();
    private readonly Dictionary<string, DateTimeOffset> requests = new(StringComparer.Ordinal);
    private readonly TimeProvider clock;

    internal PhotonWorkspaceRequestRegistry(TimeProvider clock)
    {
        this.clock = clock;
    }

    internal void Reserve(string requestId)
    {
        lock (sync)
        {
            var now = clock.GetUtcNow();
            foreach (var expired in requests.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray())
            {
                requests.Remove(expired);
            }

            if (requests.ContainsKey(requestId))
            {
                throw new PhotonWorkspaceException("workspace_request_replayed");
            }

            if (requests.Count >= PhotonWorkspaceContract.MaxRecentRequests)
            {
                throw new PhotonWorkspaceException("workspace_request_registry_full");
            }

            requests.Add(requestId, now + PhotonWorkspaceContract.RequestLifetime);
        }
    }
}

internal sealed record PhotonWorkspaceSnapshotRecord(
    string FileHandle,
    string Revision,
    string RelativePath,
    PhotonWorkspaceFileEvidence Evidence,
    DateTimeOffset ExpiresUtc,
    long Sequence);

internal sealed class PhotonWorkspaceSnapshotRegistry
{
    private readonly object sync = new();
    private readonly Dictionary<string, PhotonWorkspaceSnapshotRecord> records = new(StringComparer.Ordinal);
    private readonly TimeProvider clock;
    private long sequence;

    internal PhotonWorkspaceSnapshotRegistry(TimeProvider clock)
    {
        this.clock = clock;
    }

    internal PhotonWorkspaceSnapshotRecord Issue(string relativePath, PhotonWorkspaceFileEvidence evidence)
    {
        lock (sync)
        {
            PruneExpired();
            if (records.Count >= PhotonWorkspaceContract.MaxLiveSnapshots)
            {
                var oldest = records.Values.MinBy(record => record.Sequence);
                if (oldest is not null)
                {
                    records.Remove(oldest.FileHandle);
                }
            }

            var record = new PhotonWorkspaceSnapshotRecord(
                PhotonWorkspaceGuards.OpaqueToken(),
                PhotonWorkspaceGuards.OpaqueToken(),
                relativePath,
                evidence with { Content = evidence.Content?.ToArray() },
                clock.GetUtcNow() + PhotonWorkspaceContract.SnapshotLifetime,
                ++sequence);
            records.Add(record.FileHandle, record);
            return record;
        }
    }

    internal PhotonWorkspaceSnapshotRecord Require(
        string relativePath,
        PhotonWorkspacePrecondition? precondition)
    {
        if (precondition is null)
        {
            throw new PhotonWorkspaceException("workspace_precondition_required");
        }


        var fileHandle = PhotonWorkspaceGuards.RequireOpaqueToken(precondition.FileHandle, "workspace_precondition_invalid");
        var revision = PhotonWorkspaceGuards.RequireOpaqueToken(precondition.Revision, "workspace_precondition_invalid");
        var digest = PhotonWorkspaceGuards.RequireSha256(precondition.Sha256, "workspace_precondition_invalid");

        lock (sync)
        {
            PruneExpired();
            if (!records.TryGetValue(fileHandle, out var record) ||
                !PhotonWorkspaceGuards.FixedEquals(record.Revision, revision) ||
                !PhotonWorkspaceGuards.FixedEquals(record.Evidence.Sha256, digest) ||
                !string.Equals(record.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new PhotonWorkspaceException("workspace_precondition_stale");
            }

            return record;
        }
    }

    internal void RevokePath(string relativePath)
    {
        lock (sync)
        {
            foreach (var key in records
                .Where(pair => pair.Value.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key)
                .ToArray())
            {
                records.Remove(key);
            }
        }
    }

    private void PruneExpired()
    {
        var now = clock.GetUtcNow();
        foreach (var expired in records.Where(pair => pair.Value.ExpiresUtc <= now).Select(pair => pair.Key).ToArray())
        {
            records.Remove(expired);
        }
    }
}

internal sealed record PhotonWorkspaceTrashRecord(
    string Receipt,
    string Revision,
    string OriginalRelativePath,
    string PayloadPath,
    string SidecarPath,
    PhotonWorkspaceFileEvidence Evidence);

internal sealed class PhotonWorkspaceTrashRegistry
{
    private readonly object sync = new();
    private readonly Dictionary<string, PhotonWorkspaceTrashRecord> records = new(StringComparer.Ordinal);

    internal void EnsureCapacity()
    {
        lock (sync)
        {
            if (records.Count >= PhotonWorkspaceContract.MaxTrashReceipts)
            {
                throw new PhotonWorkspaceException("workspace_trash_registry_full");
            }
        }
    }

    internal void Add(PhotonWorkspaceTrashRecord record)
    {
        lock (sync)
        {
            if (records.Count >= PhotonWorkspaceContract.MaxTrashReceipts)
            {
                throw new PhotonWorkspaceException("workspace_trash_registry_full");
            }

            records.Add(record.Receipt, record);
        }
    }

    internal PhotonWorkspaceTrashRecord Require(string? receipt, string? revision)
    {
        var candidateReceipt = PhotonWorkspaceGuards.RequireOpaqueToken(receipt, "workspace_trash_receipt_invalid");
        var candidateRevision = PhotonWorkspaceGuards.RequireOpaqueToken(revision, "workspace_trash_receipt_invalid");
        lock (sync)
        {
            if (!records.TryGetValue(candidateReceipt, out var record) ||
                !PhotonWorkspaceGuards.FixedEquals(record.Revision, candidateRevision))
            {
                throw new PhotonWorkspaceException("workspace_trash_receipt_stale");
            }

            return record;
        }
    }

    internal void Remove(string receipt)
    {
        lock (sync)
        {
            records.Remove(receipt);
        }
    }
}

internal sealed class PhotonWorkspaceLatestWins : IDisposable
{
    private readonly object sync = new();
    private readonly Dictionary<string, Pending> pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationToken lifetime;
    private int disposed;

    internal PhotonWorkspaceLatestWins(CancellationToken lifetime)
    {
        this.lifetime = lifetime;
    }

    internal Lease Begin(string key, CancellationToken caller)
    {
        lock (sync)
        {
            if (disposed != 0)
            {
                throw new PhotonWorkspaceException("workspace_service_closed");
            }

            if (pending.Remove(key, out var prior))
            {
                Interlocked.Exchange(ref prior.Superseded, 1);
                prior.Source.Cancel();
            }
            else if (pending.Count >= PhotonWorkspaceContract.MaxPendingOperations)
            {
                throw new PhotonWorkspaceException("workspace_pending_limit");
            }

            var source = CancellationTokenSource.CreateLinkedTokenSource(caller, lifetime);
            var current = new Pending(source);
            pending.Add(key, current);
            return new Lease(this, key, current);
        }
    }

    public void Dispose()
    {
        Pending[] values;
        lock (sync)
        {
            if (disposed != 0)
            {
                return;
            }

            disposed = 1;
            values = pending.Values.ToArray();
            pending.Clear();
        }

        foreach (var value in values)
        {
            value.Source.Cancel();
        }
    }

    private void Complete(string key, Pending current)
    {
        lock (sync)
        {
            if (pending.TryGetValue(key, out var found) && ReferenceEquals(found, current))
            {
                pending.Remove(key);
            }
        }
    }

    internal sealed class Pending
    {
        internal Pending(CancellationTokenSource source) => Source = source;
        internal CancellationTokenSource Source { get; }
        internal int Superseded;
    }

    internal sealed class Lease : IDisposable
    {
        private readonly PhotonWorkspaceLatestWins owner;
        private readonly string key;
        private readonly Pending pending;
        private int disposed;

        internal Lease(PhotonWorkspaceLatestWins owner, string key, Pending pending)
        {
            this.owner = owner;
            this.key = key;
            this.pending = pending;
        }

        internal CancellationToken Token => pending.Source.Token;

        internal void ThrowIfCancellationRequested()
        {
            if (Volatile.Read(ref pending.Superseded) != 0)
            {
                throw new PhotonWorkspaceException("workspace_request_superseded");
            }

            pending.Source.Token.ThrowIfCancellationRequested();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            owner.Complete(key, pending);
            pending.Source.Dispose();
        }
    }
}

internal sealed class PhotonWorkspaceActivityTracker
{
    private readonly object sync = new();
    private int active;
    private bool closing;
    private TaskCompletionSource drained = Completed();

    internal Lease Enter()
    {
        lock (sync)
        {
            if (closing)
            {
                throw new PhotonWorkspaceException("workspace_service_closed");
            }

            if (active >= PhotonWorkspaceContract.MaxPendingOperations)
            {
                throw new PhotonWorkspaceException("workspace_pending_limit");
            }

            if (active++ == 0)
            {
                drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            return new Lease(this);
        }
    }

    internal Task CloseAndDrainAsync()
    {
        lock (sync)
        {
            closing = true;
            return drained.Task;
        }
    }

    private void Exit()
    {
        lock (sync)
        {
            active--;
            if (active == 0)
            {
                drained.TrySetResult();
            }
        }
    }

    private static TaskCompletionSource Completed()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }

    internal sealed class Lease : IDisposable
    {
        private PhotonWorkspaceActivityTracker? owner;
        internal Lease(PhotonWorkspaceActivityTracker owner) => this.owner = owner;
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Exit();
    }
}

internal sealed class PhotonWorkspaceTestHooks
{
    internal Func<string, CancellationToken, ValueTask>? BeforeIoAsync { get; init; }
    internal Func<string, CancellationToken, ValueTask>? BeforeCommitAsync { get; init; }
    internal Func<string, ValueTask>? AfterCommitAsync { get; init; }
}
