using System.Windows.Input;
using GitDash.Domain.Timers;
using GitDash.Presentation.Mvvm;

namespace GitDash.Presentation.ViewModels;

/// <summary>
/// One of the extra timers the user adds on the Timers page: a task timer, a countdown, or a pull request review timer.
/// Any number of each can run at the same time. The owner decides what start, finish and remove mean.
/// </summary>
public sealed class TimerItemViewModel : ObservableObject
{
    private readonly TrackedStopwatch _watch = new();
    private string _name, _clock = "00:00";
    private bool _isRunning, _isFinished;
    private double _progress;

    public TimerItemViewModel(
        TimerKind kind, string name, TimeSpan? total, string? pullRequestId, string? pullRequestUrl,
        Action<TimerItemViewModel> toggle, Action<TimerItemViewModel> finish,
        Action<TimerItemViewModel> remove, Action<TimerItemViewModel> stop)
    {
        Kind = kind;
        _name = name;
        Total = total;
        PullRequestId = pullRequestId;
        PullRequestUrl = pullRequestUrl;
        Accent = kind switch
        {
            TimerKind.Countdown => "#34D399",
            TimerKind.PullRequestReview => "#FBBF24",
            _ => "#22D3EE",
        };
        Line = new RunningTimerViewModel(Accent, () => stop(this));
        ToggleCommand = new RelayCommand(() => toggle(this));
        FinishCommand = new RelayCommand(() => finish(this));
        RemoveCommand = new RelayCommand(() => remove(this));
        _clock = Humanize.Clock(total ?? TimeSpan.Zero);
    }

    public TimerKind Kind { get; }
    public string Accent { get; }
    /// <summary>Set for a countdown: how long it runs. Null for open-ended timers.</summary>
    public TimeSpan? Total { get; }
    public string? PullRequestId { get; }
    public string? PullRequestUrl { get; }
    /// <summary>The review timer was started on a pull request that was already approved, so approval must not stop it.</summary>
    public bool StartedWhileApproved { get; set; }
    public RunningTimerViewModel Line { get; }

    public bool IsCountdown => Total is not null;
    public bool IsReview => Kind == TimerKind.PullRequestReview;

    public string KindLabel => Kind switch
    {
        TimerKind.Countdown => "Countdown",
        TimerKind.PullRequestReview => "Review",
        _ => "Task",
    };

    public string Name { get => _name; set => Set(ref _name, value); }
    public string Clock { get => _clock; private set => Set(ref _clock, value); }
    public bool IsRunning { get => _isRunning; private set => Set(ref _isRunning, value); }
    /// <summary>A countdown that reached zero and waits to be restarted or removed.</summary>
    public bool IsFinished { get => _isFinished; private set => Set(ref _isFinished, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }

    public ICommand ToggleCommand { get; }
    public ICommand FinishCommand { get; }
    public ICommand RemoveCommand { get; }

    public string Title => Kind switch
    {
        TimerKind.Countdown => string.IsNullOrWhiteSpace(Name) ? "Countdown" : Name.Trim(),
        TimerKind.PullRequestReview => $"PR review · {Name}",
        _ => string.IsNullOrWhiteSpace(Name) ? "Task" : Name.Trim(),
    };

    public DateTimeOffset? FirstStartedAt => _watch.FirstStartedAt;

    /// <summary>Time spent so far; a countdown never counts past its length.</summary>
    public TimeSpan Elapsed(DateTimeOffset now) =>
        Total is { } total ? TimeSpan.FromTicks(Math.Min(_watch.Elapsed(now).Ticks, total.Ticks)) : _watch.Elapsed(now);

    public void Start(DateTimeOffset now)
    {
        if (IsFinished) Restart();
        _watch.Start(now);
        Tick(now);
    }

    public void Pause(DateTimeOffset now)
    {
        _watch.Pause(now);
        Tick(now);
    }

    private void Restart()
    {
        _watch.Reset();
        IsFinished = false;
    }

    /// <summary>Refreshes the displayed clock and slider. Returns true on the tick a countdown reaches zero.</summary>
    public bool Tick(DateTimeOffset now)
    {
        var elapsed = Elapsed(now);
        var reachedZero = Total is { } total && _watch.IsRunning && _watch.Elapsed(now) >= total;
        if (reachedZero)
        {
            _watch.Pause(now);
            IsFinished = true;
        }

        IsRunning = _watch.IsRunning;
        Clock = Humanize.Clock(Total is { } t ? t - elapsed : elapsed);
        Progress = Total is { } whole && whole > TimeSpan.Zero
            ? elapsed / whole
            : RunningTimerViewModel.MinuteSweep(elapsed);
        Line.Update(Title, Clock, Progress);
        Raise(nameof(Title));
        return reachedZero;
    }
}
