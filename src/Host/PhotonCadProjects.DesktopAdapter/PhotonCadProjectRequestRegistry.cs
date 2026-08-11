namespace PhotonCadProjects.DesktopAdapter;

internal sealed class PhotonCadProjectRequestRegistry
{
    private readonly TimeProvider _time;
    private readonly Dictionary<string, ReplayEntry> _entries = new(StringComparer.Ordinal);
    private readonly Queue<ReplayEntry> _order = new();

    internal PhotonCadProjectRequestRegistry(TimeProvider time) =>
        _time = time ?? throw new ArgumentNullException(nameof(time));

    internal bool TryReserve(string requestId, IReadOnlySet<string> active)
    {
        var now = _time.GetUtcNow();
        Prune(now);
        if (active.Contains(requestId) || _entries.ContainsKey(requestId)) return false;
        while (_entries.Count >= PhotonCadProjectDesktopProtocol.MaximumReplayEntries && _order.TryDequeue(out var oldest))
        {
            if (_entries.TryGetValue(oldest.RequestId, out var current) && ReferenceEquals(current, oldest))
                _entries.Remove(oldest.RequestId);
        }
        var entry = new ReplayEntry(requestId, now + PhotonCadProjectDesktopProtocol.ReplayTimeToLive);
        _entries.Add(requestId, entry);
        _order.Enqueue(entry);
        return true;
    }

    internal void Clear()
    {
        _entries.Clear();
        _order.Clear();
    }

    private void Prune(DateTimeOffset now)
    {
        while (_order.TryPeek(out var oldest) && now >= oldest.ExpiresAtUtc)
        {
            _order.Dequeue();
            if (_entries.TryGetValue(oldest.RequestId, out var current) && ReferenceEquals(current, oldest))
                _entries.Remove(oldest.RequestId);
        }
    }

    private sealed record ReplayEntry(string RequestId, DateTimeOffset ExpiresAtUtc);
}
