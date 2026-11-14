namespace GitDash.Domain.Timers;

public enum TimerKind
{
    Task,
    Pomodoro,
    PullRequestReview,
    Countdown,
}

/// <summary>A finished block of tracked time.</summary>
public sealed record TimerSession(
    Guid Id,
    TimerKind Kind,
    string Label,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    string? PullRequestUrl = null);
