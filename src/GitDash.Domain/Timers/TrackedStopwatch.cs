namespace GitDash.Domain.Timers;

/// <summary>
/// A pausable stopwatch driven by timestamps supplied by the caller,
/// so it stays correct across sleep/resume and needs no background thread.
/// </summary>
public sealed class TrackedStopwatch
{
    private TimeSpan _accumulated;
    private DateTimeOffset? _runningSince;

    public bool IsRunning => _runningSince is not null;
    public DateTimeOffset? FirstStartedAt { get; private set; }

    public void Start(DateTimeOffset now)
    {
        if (IsRunning) return;
        FirstStartedAt ??= now;
        _runningSince = now;
    }

    public void Pause(DateTimeOffset now)
    {
        if (_runningSince is not { } since) return;
        _accumulated += now - since;
        _runningSince = null;
    }

    public TimeSpan Elapsed(DateTimeOffset now) =>
        _runningSince is { } since ? _accumulated + (now - since) : _accumulated;

    public void Reset()
    {
        _accumulated = TimeSpan.Zero;
        _runningSince = null;
        FirstStartedAt = null;
    }
}
