namespace GitDash.Domain.Timers;

/// <summary>
/// A wall-clock alarm: rings once at <see cref="At"/> (local time), or every day when <see cref="Daily"/> is set.
/// An alarm is only "due" for a few minutes after its time, so one that passed while the app was closed stays silent.
/// </summary>
public sealed record AlarmEntry(
    Guid Id, string Label, TimeOnly At, bool Enabled = true, bool Daily = false, DateOnly? LastRangOn = null)
{
    /// <summary>How long after its time an alarm can still ring (covers a late check, not a closed app).</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

    public bool IsDue(DateTime localNow)
    {
        if (!Enabled || LastRangOn == DateOnly.FromDateTime(localNow)) return false;
        var late = localNow.TimeOfDay - At.ToTimeSpan();
        return late >= TimeSpan.Zero && late < Grace;
    }

    /// <summary>The alarm after it rang: a daily one waits for tomorrow, a one-off switches itself off.</summary>
    public AlarmEntry Rang(DateTime localNow) =>
        Daily ? this with { LastRangOn = DateOnly.FromDateTime(localNow) } : this with { Enabled = false };
}
