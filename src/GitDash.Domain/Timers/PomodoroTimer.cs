namespace GitDash.Domain.Timers;

public enum PomodoroPhase
{
    Focus,
    ShortBreak,
    LongBreak,
}

public sealed record PomodoroSettings(
    int FocusMinutes = 25,
    int ShortBreakMinutes = 5,
    int LongBreakMinutes = 15,
    int CyclesBeforeLongBreak = 4)
{
    public TimeSpan DurationOf(PomodoroPhase phase) => TimeSpan.FromMinutes(Math.Max(1, phase switch
    {
        PomodoroPhase.Focus => FocusMinutes,
        PomodoroPhase.ShortBreak => ShortBreakMinutes,
        _ => LongBreakMinutes,
    }));
}

/// <summary>Pomodoro state machine: focus → short break ×N → long break → repeat.</summary>
public sealed class PomodoroTimer(PomodoroSettings settings)
{
    private readonly TrackedStopwatch _stopwatch = new();

    public PomodoroSettings Settings { get; private set; } = settings;
    public PomodoroPhase Phase { get; private set; } = PomodoroPhase.Focus;
    public int CompletedFocusCount { get; private set; }
    public bool IsRunning => _stopwatch.IsRunning;
    public TimeSpan PhaseDuration => Settings.DurationOf(Phase);

    public void Configure(PomodoroSettings settings) => Settings = settings;

    public void Start(DateTimeOffset now) => _stopwatch.Start(now);

    public void Pause(DateTimeOffset now) => _stopwatch.Pause(now);

    public void Reset()
    {
        _stopwatch.Reset();
        Phase = PomodoroPhase.Focus;
        CompletedFocusCount = 0;
    }

    public TimeSpan Remaining(DateTimeOffset now)
    {
        var left = PhaseDuration - _stopwatch.Elapsed(now);
        return left < TimeSpan.Zero ? TimeSpan.Zero : left;
    }

    /// <summary>0 at the start of a phase, 1 when it is over.</summary>
    public double Progress(DateTimeOffset now) =>
        Math.Clamp(_stopwatch.Elapsed(now) / PhaseDuration, 0d, 1d);

    /// <summary>
    /// Advances to the next phase if the current one has run out.
    /// Returns the phase that just finished, or null when nothing changed.
    /// The next phase starts paused so the user consciously begins it.
    /// </summary>
    public PomodoroPhase? Tick(DateTimeOffset now) =>
        Remaining(now) > TimeSpan.Zero ? null : Advance();

    /// <summary>Ends the current phase early.</summary>
    public PomodoroPhase Skip() => Advance();

    private PomodoroPhase Advance()
    {
        var finished = Phase;
        _stopwatch.Reset();
        if (finished == PomodoroPhase.Focus)
        {
            CompletedFocusCount++;
            Phase = CompletedFocusCount % Math.Max(1, Settings.CyclesBeforeLongBreak) == 0
                ? PomodoroPhase.LongBreak
                : PomodoroPhase.ShortBreak;
        }
        else
        {
            Phase = PomodoroPhase.Focus;
        }

        return finished;
    }
}
