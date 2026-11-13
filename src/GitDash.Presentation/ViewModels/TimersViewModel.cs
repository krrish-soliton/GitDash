using System.Collections.ObjectModel;
using System.Windows.Input;
using GitDash.Application.Abstractions;
using GitDash.Application.Monitoring;
using GitDash.Application.Settings;
using GitDash.Application.Timers;
using GitDash.Domain.PullRequests;
using GitDash.Domain.Timers;
using GitDash.Presentation.Mvvm;

namespace GitDash.Presentation.ViewModels;

public sealed record ReviewTargetViewModel(string Id, string Label, string Url);

public sealed record SessionViewModel(string Kind, string Label, string Duration, string When);

/// <summary>Task timer, Pomodoro timer and pull request review timer on one page.</summary>
public sealed class TimersViewModel : PageViewModel, IDisposable
{
    private readonly SettingsService _settings;
    private readonly TimerHistory _history;
    private readonly NotificationRouter _notifications;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;

    private readonly TrackedStopwatch _task = new();
    private readonly TrackedStopwatch _review = new();
    private readonly PomodoroTimer _pomodoro;
    private DateTimeOffset? _focusStartedAt;
    private ITimer? _ticker;
    private string _widgetLabel = "Timers idle", _widgetClock = "--:--";
    private bool _isAnyRunning;
    private readonly RunningTimerViewModel _pomodoroLine = new("#F472B6"), _reviewLine = new("#FBBF24"), _taskLine = new("#22D3EE");

    private string _taskName = string.Empty, _taskElapsed = "00:00", _reviewElapsed = "00:00", _reviewTotal = "00:00";
    private string _pomodoroPhase = "Focus", _pomodoroRemaining = "25:00", _pomodoroRound = string.Empty, _todayTotal = "0m";
    private double _pomodoroSweep;
    private bool _isTaskRunning, _isReviewRunning, _isPomodoroRunning, _hasHistory;
    private ReviewTargetViewModel? _selectedReviewTarget;

    public TimersViewModel(
        SettingsService settings, TimerHistory history, NotificationRouter notifications,
        IUiDispatcher ui, TimeProvider time)
        : base("Timers", "IconTimer")
    {
        _settings = settings;
        _history = history;
        _notifications = notifications;
        _ui = ui;
        _time = time;
        _pomodoro = new PomodoroTimer(settings.Current.Pomodoro);

        ToggleTaskCommand = new RelayCommand(ToggleTask);
        FinishTaskCommand = new RelayCommand(FinishTask);
        TogglePomodoroCommand = new RelayCommand(TogglePomodoro);
        SkipPomodoroCommand = new RelayCommand(() => CompletePhase(_pomodoro.Skip(), announce: false));
        ResetPomodoroCommand = new RelayCommand(ResetPomodoro);
        ToggleReviewCommand = new RelayCommand(ToggleReview);
        FinishReviewCommand = new RelayCommand(FinishReview);

        _history.Changed += OnHistoryChanged;
        _settings.Changed += OnSettingsChanged;
        Refresh();
        RefreshHistory();
    }

    // ----- Task timer -----
    public string TaskName { get => _taskName; set => Set(ref _taskName, value); }
    public string TaskElapsed { get => _taskElapsed; private set => Set(ref _taskElapsed, value); }
    public bool IsTaskRunning { get => _isTaskRunning; private set => Set(ref _isTaskRunning, value); }
    /// <summary>Headline for the floating timer widget: the most relevant running timer, or an idle hint.</summary>
    public string WidgetLabel { get => _widgetLabel; private set => Set(ref _widgetLabel, value); }
    public string WidgetClock { get => _widgetClock; private set => Set(ref _widgetClock, value); }
    public bool IsAnyRunning { get => _isAnyRunning; private set => Set(ref _isAnyRunning, value); }

    /// <summary>Every running timer (Pomodoro, review, task), used by the dashboard.</summary>
    public ObservableCollection<RunningTimerViewModel> RunningTimers { get; } = [];

    /// <summary>What the widget and the island list: all running timers, or just the most relevant one, by the setting.</summary>
    public ObservableCollection<RunningTimerViewModel> VisibleTimers { get; } = [];
    public ICommand ToggleTaskCommand { get; }
    public ICommand FinishTaskCommand { get; }

    // ----- Pomodoro -----
    public string PomodoroPhase { get => _pomodoroPhase; private set => Set(ref _pomodoroPhase, value); }
    public string PomodoroRemaining { get => _pomodoroRemaining; private set => Set(ref _pomodoroRemaining, value); }
    public string PomodoroRound { get => _pomodoroRound; private set => Set(ref _pomodoroRound, value); }
    /// <summary>Progress of the current phase as an arc angle, 0–360.</summary>
    public double PomodoroSweep { get => _pomodoroSweep; private set => Set(ref _pomodoroSweep, value); }
    public bool IsPomodoroRunning { get => _isPomodoroRunning; private set => Set(ref _isPomodoroRunning, value); }
    public ICommand TogglePomodoroCommand { get; }
    public ICommand SkipPomodoroCommand { get; }
    public ICommand ResetPomodoroCommand { get; }

    public decimal? FocusMinutes
    {
        get => _pomodoro.Settings.FocusMinutes;
        set => Configure(s => s with { FocusMinutes = Minutes(value, s.FocusMinutes, 180) });
    }

    public decimal? ShortBreakMinutes
    {
        get => _pomodoro.Settings.ShortBreakMinutes;
        set => Configure(s => s with { ShortBreakMinutes = Minutes(value, s.ShortBreakMinutes, 60) });
    }

    public decimal? LongBreakMinutes
    {
        get => _pomodoro.Settings.LongBreakMinutes;
        set => Configure(s => s with { LongBreakMinutes = Minutes(value, s.LongBreakMinutes, 120) });
    }

    public decimal? Cycles
    {
        get => _pomodoro.Settings.CyclesBeforeLongBreak;
        set => Configure(s => s with { CyclesBeforeLongBreak = Minutes(value, s.CyclesBeforeLongBreak, 12) });
    }

    // ----- Pull request review timer -----
    public ObservableCollection<ReviewTargetViewModel> ReviewTargets { get; } = [];

    public ReviewTargetViewModel? SelectedReviewTarget
    {
        get => _selectedReviewTarget;
        set
        {
            if (Equals(_selectedReviewTarget, value)) return;
            FinishReview(); // time already tracked belongs to the previous pull request
            _selectedReviewTarget = value;
            Raise();
            Refresh();
        }
    }

    public string ReviewElapsed { get => _reviewElapsed; private set => Set(ref _reviewElapsed, value); }
    public string ReviewTotal { get => _reviewTotal; private set => Set(ref _reviewTotal, value); }
    public bool IsReviewRunning { get => _isReviewRunning; private set => Set(ref _isReviewRunning, value); }
    public ICommand ToggleReviewCommand { get; }
    public ICommand FinishReviewCommand { get; }

    // ----- History -----
    public ObservableCollection<SessionViewModel> History { get; } = [];
    public string TodayTotal { get => _todayTotal; private set => Set(ref _todayTotal, value); }
    public bool HasHistory { get => _hasHistory; private set => Set(ref _hasHistory, value); }

    public override void Apply(MonitorState state)
    {
        var targets = state.Open
            .Where(p => p.IsOnViewersDesk)
            .GroupBy(p => p.Id).Select(g => g.First())
            .Select(p => new ReviewTargetViewModel(p.Id, $"{p.Repository.Name} #{p.Number} · {p.Title}", p.Url))
            .ToList();
        if (ReviewTargets.SequenceEqual(targets)) return;

        // Keep the current target even if it left the list (for example it was merged mid-review).
        var current = _selectedReviewTarget;
        if (current is not null && targets.All(t => t.Id != current.Id)) targets.Insert(0, current);

        ReviewTargets.Clear();
        foreach (var target in targets) ReviewTargets.Add(target);
        _selectedReviewTarget = current is null ? null : targets.First(t => t.Id == current.Id);
        Raise(nameof(SelectedReviewTarget));
    }

    /// <summary>Called from the My PRs page: pick the pull request and start timing straight away.</summary>
    public void StartReviewFor(PullRequest pr)
    {
        var target = ReviewTargets.FirstOrDefault(t => t.Id == pr.Id);
        if (target is null)
        {
            target = new ReviewTargetViewModel(pr.Id, $"{pr.Repository.Name} #{pr.Number} · {pr.Title}", pr.Url);
            ReviewTargets.Insert(0, target);
        }

        SelectedReviewTarget = target;
        if (!_review.IsRunning) ToggleReview();
    }

    /// <summary>Timer events are notifications too, so they also appear on the dynamic island.</summary>
    private void Announce(string title, string message) =>
        _ = _notifications.PublishAsync(new ToastRequest(title, message, ToastTone.Timer));

    private string TaskTitle => string.IsNullOrWhiteSpace(TaskName) ? "Task" : TaskName.Trim();

    private void ToggleTask()
    {
        var now = _time.GetUtcNow();
        if (_task.IsRunning) _task.Pause(now);
        else _task.Start(now);
        Refresh();
        Announce(_task.IsRunning ? "Task timer started" : "Task timer paused", $"{TaskTitle} · {TaskElapsed}");
    }

    private void FinishTask()
    {
        var now = _time.GetUtcNow();
        _task.Pause(now);
        if (_task.FirstStartedAt is { } started)
        {
            var label = string.IsNullOrWhiteSpace(TaskName) ? "Untitled task" : TaskName.Trim();
            Save(new TimerSession(Guid.NewGuid(), TimerKind.Task, label, started, _task.Elapsed(now)));
            Announce("Task timer finished", $"{label} · {Humanize.Clock(_task.Elapsed(now))}");
        }

        _task.Reset();
        Refresh();
    }

    private void TogglePomodoro()
    {
        var now = _time.GetUtcNow();
        if (_pomodoro.IsRunning)
        {
            _pomodoro.Pause(now);
        }
        else
        {
            if (_pomodoro.Phase == Domain.Timers.PomodoroPhase.Focus) _focusStartedAt ??= now;
            _pomodoro.Start(now);
        }

        Refresh();
        Announce(_pomodoro.IsRunning ? $"Pomodoro started · {PomodoroPhase}" : $"Pomodoro paused · {PomodoroPhase}",
            $"{PomodoroRemaining} remaining");
    }

    private void ResetPomodoro()
    {
        _pomodoro.Reset();
        _focusStartedAt = null;
        Refresh();
    }

    private void CompletePhase(PomodoroPhase finished, bool announce)
    {
        if (finished == Domain.Timers.PomodoroPhase.Focus)
        {
            if (announce && _focusStartedAt is { } started)
            {
                Save(new TimerSession(
                    Guid.NewGuid(), TimerKind.Pomodoro, "Focus session", started,
                    _pomodoro.Settings.DurationOf(finished)));
            }

            _focusStartedAt = null;
        }

        if (announce)
        {
            var (title, message) = finished == Domain.Timers.PomodoroPhase.Focus
                ? ("Focus session complete", "Nice work. Time for a break.")
                : ("Break is over", "Ready for the next focus session?");
            _ = _notifications.PublishAsync(new ToastRequest(title, message, ToastTone.Timer));
        }

        Refresh();
    }

    private void ToggleReview()
    {
        if (_selectedReviewTarget is null) return;
        var now = _time.GetUtcNow();
        if (_review.IsRunning) _review.Pause(now);
        else _review.Start(now);
        Refresh();
        Announce(_review.IsRunning ? "Review timer started" : "Review timer paused",
            $"{_selectedReviewTarget.Label} · {ReviewElapsed}");
    }

    private void FinishReview()
    {
        var now = _time.GetUtcNow();
        _review.Pause(now);
        if (_selectedReviewTarget is { } target && _review.FirstStartedAt is { } started)
        {
            Save(new TimerSession(
                Guid.NewGuid(), TimerKind.PullRequestReview, target.Label, started, _review.Elapsed(now), target.Url));
            Announce("Review timer finished", $"{target.Label} · {Humanize.Clock(_review.Elapsed(now))}");
        }

        _review.Reset();
        Refresh();
    }

    private void Configure(Func<PomodoroSettings, PomodoroSettings> change)
    {
        var updated = change(_pomodoro.Settings);
        if (updated == _pomodoro.Settings) return;
        _pomodoro.Configure(updated);
        _ = _settings.UpdateAsync(s => s with { Pomodoro = updated });
        Raise(nameof(FocusMinutes));
        Raise(nameof(ShortBreakMinutes));
        Raise(nameof(LongBreakMinutes));
        Raise(nameof(Cycles));
        Refresh();
    }

    private static int Minutes(decimal? value, int fallback, int max) =>
        value is { } v ? (int)Math.Clamp(v, 1, max) : fallback;

    private void Save(TimerSession session) => _ = _history.AddAsync(session);

    /// <summary>Recomputes all displayed values and starts or stops the one-second ticker as needed.</summary>
    private void Refresh()
    {
        var now = _time.GetUtcNow();

        if (_pomodoro.IsRunning && _pomodoro.Tick(now) is { } finished)
        {
            CompletePhase(finished, announce: true);
            return; // CompletePhase refreshes again with the new phase
        }

        TaskElapsed = Humanize.Clock(_task.Elapsed(now));
        IsTaskRunning = _task.IsRunning;

        PomodoroPhase = _pomodoro.Phase switch
        {
            Domain.Timers.PomodoroPhase.Focus => "Focus",
            Domain.Timers.PomodoroPhase.ShortBreak => "Short break",
            _ => "Long break",
        };
        PomodoroRemaining = Humanize.Clock(_pomodoro.Remaining(now));
        PomodoroSweep = _pomodoro.Progress(now) * 360d;
        IsPomodoroRunning = _pomodoro.IsRunning;
        var cycles = Math.Max(1, _pomodoro.Settings.CyclesBeforeLongBreak);
        PomodoroRound = $"Round {_pomodoro.CompletedFocusCount % cycles + 1} of {cycles} · {_pomodoro.CompletedFocusCount} done";

        var reviewNow = _review.Elapsed(now);
        ReviewElapsed = Humanize.Clock(reviewNow);
        IsReviewRunning = _review.IsRunning;
        var earlier = _selectedReviewTarget is { } t ? _history.TotalFor(t.Url) : TimeSpan.Zero;
        ReviewTotal = Humanize.Clock(earlier + reviewNow);

        var anyRunning = _task.IsRunning || _pomodoro.IsRunning || _review.IsRunning;
        IsAnyRunning = anyRunning;
        (WidgetLabel, WidgetClock) = (_pomodoro.IsRunning, _review.IsRunning, _task.IsRunning) switch
        {
            (true, _, _) => ($"Pomodoro · {PomodoroPhase}", PomodoroRemaining),
            (_, true, _) => ("PR review", ReviewElapsed),
            (_, _, true) => (string.IsNullOrWhiteSpace(TaskName) ? "Task" : TaskName.Trim(), TaskElapsed),
            _ => ("Timers idle", Humanize.Clock(_pomodoro.Remaining(now))),
        };
        UpdateRunningLines(now);
        if (anyRunning && _ticker is null)
        {
            _ticker = _time.CreateTimer(_ => _ui.Post(Refresh), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
        else if (!anyRunning && _ticker is not null)
        {
            _ticker.Dispose();
            _ticker = null;
        }
    }

    private void OnSettingsChanged(AppSettings _) => _ui.Post(() => UpdateRunningLines(_time.GetUtcNow()));

    /// <summary>Updates the per-timer lines every tick and changes the lists only when a timer starts or stops.</summary>
    private void UpdateRunningLines(DateTimeOffset now)
    {
        _pomodoroLine.Update($"Pomodoro · {PomodoroPhase}", PomodoroRemaining, _pomodoro.Progress(now));
        _reviewLine.Update(_selectedReviewTarget is { } t ? $"PR review · {t.Label}" : "PR review", ReviewElapsed,
            RunningTimerViewModel.MinuteSweep(_review.Elapsed(now)));
        _taskLine.Update(TaskTitle, TaskElapsed, RunningTimerViewModel.MinuteSweep(_task.Elapsed(now)));

        var running = new List<RunningTimerViewModel>(3);
        if (_pomodoro.IsRunning) running.Add(_pomodoroLine);
        if (_review.IsRunning) running.Add(_reviewLine);
        if (_task.IsRunning) running.Add(_taskLine);
        Sync(RunningTimers, running);
        Sync(VisibleTimers, _settings.Current.ShowMultipleTimers ? running : [.. running.Take(1)]);
    }

    private static void Sync(ObservableCollection<RunningTimerViewModel> target, IReadOnlyList<RunningTimerViewModel> wanted)
    {
        for (var i = 0; i < wanted.Count; i++)
        {
            var at = target.IndexOf(wanted[i]);
            if (at < 0) target.Insert(i, wanted[i]);
            else if (at != i) target.Move(at, i);
        }

        while (target.Count > wanted.Count) target.RemoveAt(target.Count - 1);
    }

    private void OnHistoryChanged() => _ui.Post(RefreshHistory);

    /// <summary>The time format changed: redraw the session times.</summary>
    public void Reformat() => RefreshHistory();

    private void RefreshHistory()
    {
        var now = _time.GetUtcNow();
        var today = Humanize.DayOf(now);
        DashboardViewModel.Replace(History, _history.Sessions.Take(25).Select(s => new SessionViewModel(
            s.Kind switch
            {
                TimerKind.Task => "Task",
                TimerKind.Pomodoro => "Pomodoro",
                _ => "Review",
            },
            s.Label,
            Humanize.Clock(s.Duration),
            Humanize.Stamp(s.StartedAt))));
        HasHistory = History.Count > 0;
        TodayTotal = Humanize.Age(TimeSpan.FromTicks(_history.Sessions
            .Where(s => Humanize.DayOf(s.StartedAt) == today)
            .Sum(s => s.Duration.Ticks))) is var total && total == "just now" ? "0m" : total;
        Refresh();
    }

    public void Dispose()
    {
        _history.Changed -= OnHistoryChanged;
        _settings.Changed -= OnSettingsChanged;
        _ticker?.Dispose();
    }
}
