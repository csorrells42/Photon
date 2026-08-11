namespace PhotonCadArtifacts;

internal readonly record struct PhotonCadDeadline(
    long StartedTimestamp,
    TimeSpan Duration,
    DateTimeOffset DisplayExpiresAtUtc);

internal sealed class PhotonCadMonotonicClock
{
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private DateTimeOffset _lastUtc;
    private long _lastTimestamp;

    internal PhotonCadMonotonicClock(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lastUtc = _timeProvider.GetUtcNow().ToUniversalTime();
        _lastTimestamp = _timeProvider.GetTimestamp();
    }

    internal DateTimeOffset UtcNow()
    {
        lock (_sync)
        {
            var current = _timeProvider.GetUtcNow().ToUniversalTime();
            if (current > _lastUtc) _lastUtc = current;
            return _lastUtc;
        }
    }

    internal PhotonCadDeadline Start(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) throw new PhotonCadArtifactException("invalid_deadline");
        return new PhotonCadDeadline(Timestamp(), duration, UtcNow() + duration);
    }

    internal PhotonCadDeadline StartCapped(TimeSpan duration, params PhotonCadDeadline[] caps)
    {
        var remaining = duration;
        foreach (var cap in caps)
        {
            var capRemaining = Remaining(cap);
            if (capRemaining < remaining) remaining = capRemaining;
        }
        if (remaining <= TimeSpan.Zero) return new PhotonCadDeadline(Timestamp(), TimeSpan.Zero, UtcNow());
        return new PhotonCadDeadline(Timestamp(), remaining, UtcNow() + remaining);
    }

    internal TimeSpan Remaining(PhotonCadDeadline deadline)
    {
        var elapsed = _timeProvider.GetElapsedTime(deadline.StartedTimestamp, Timestamp());
        return elapsed >= deadline.Duration ? TimeSpan.Zero : deadline.Duration - elapsed;
    }

    internal bool IsExpired(PhotonCadDeadline deadline) => Remaining(deadline) <= TimeSpan.Zero;

    private long Timestamp()
    {
        lock (_sync)
        {
            var current = _timeProvider.GetTimestamp();
            if (current > _lastTimestamp) _lastTimestamp = current;
            return _lastTimestamp;
        }
    }
}
