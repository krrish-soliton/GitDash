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
    private readonly IAlarm _alarm;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;

    private readonly TrackedStopwatch _task = new();
    private readonly TrackedStopwatch _review = new();
    private readonly PomodoroTimer _pomodoro;
    private DateTimeOffset? _focusStartedAt;
    private ITimer? _ticker;
    private string _widgetLabel = "Timers idle", _widgetClock = "--:--";
    private bool _isAnyRunning;
    private readonly RunningTimerViewModel _pomodoroLine, _reviewLine, _taskLine;
    private ITimer? _alarmTicker;
    private readonly HashSet<(Guid, DateOnly)> _rang = [];
    private HashSet<string> _approved = [];
    private bool _reviewStartedWhileApproved;
    private Action? _pendingApprovedStart;
    private string _confirmMessage = string.Empty, _newTimerName = string.Empty;
    private bool _isConfirmingApproved, _hasAlarms, _hasMoreTimers, _newAlarmDaily;
    private decimal? _countdownMinutes = 10;
    private TimeSpan? _newAlarmTime = new(8, 0, 0);
    private string _newAlarmLabel = string.Empty;

    private string _taskName = string.Empty, _taskElapsed = "00:00", _reviewElapsed = "00:00", _reviewTotal = "00:00";
    private string _pomodoroPhase = "Focus", _pomodoroRemaining = "25:00", _pomodoroRound = string.Empty, _todayTotal = "0m";
    private double _pomodoroSweep;
    private bool _isTaskRunning, _isReviewRunning, _isPomodoroRunning, _hasHistory;
    private ReviewTargetViewModel? _selectedReviewTarget;

    public TimersViewModel(
        SettingsService settings, TimerHistory history, NotificationRouter notifications, IAlarm alarm,
        IUiDispatcher ui, TimeProvider time)
        : base("Timers", "IconTimer")
    {
        _settings = settings;
        _history = history;
        _notifications = notifications;
        _alarm = alarm;
        _ui = ui;
        _time = time;
        _pomodoro = new PomodoroTimer(settings.Current.Pomodoro);

        // The stop button on the island and the widget pauses a timer without losing its time.
        _pomodoroLine = new RunningTimerViewModel("#F472B6", () => { if (_pomodoro.IsRunning) TogglePomodoro(); });
        _reviewLine = new RunningTimerViewModel("#FBBF24", () => { if (_review.IsRunning) ToggleReview(); });
        _taskLine = new RunningTimerViewModel("#22D3EE", () => { if (_task.IsRunning) ToggleTask(); });

        AddTaskTimerCommand = new RelayCommand(() => AddItem(TimerKind.Task, NewTimerName, null));
        AddCountdownCommand = new RelayCommand(AddCountdown);
        AddAlarmCommand = new RelayCommand(AddAlarm);
        ConfirmApprovedStartCommand = new RelayCommand(() => ResolveApprovedPrompt(start: true));
        CancelApprovedStartCommand = new RelayCommand(() => ResolveApprovedPrompt(start: false));
        ToggleTaskCommand = new RelayCommand(ToggleTask);
        FinishTaskCommand = new RelayCommand(FinishTask);
        TogglePomodoroCommand = new RelayCommand(TogglePomodoro);
        SkipPomodoroCommand = new RelayCommand(() => CompletePhase(_pomodoro.Skip(), announce: false));
        ResetPomodoroCommand = new RelayCommand(ResetPomodoro);
        ToggleReviewCommand = new RelayCommand(ToggleReview);
        FinishReviewCommand = new RelayCommand(() => FinishReview(null));

        _history.Changed += OnHistoryChanged;
        _settings.Changed += OnSettingsChanged;
        _alarmTicker = _time.CreateTimer(_ => _ui.Post(CheckAlarms), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        RebuildAlarms();
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

    // ----- More timers: any number of task timers, countdowns and review timers -----
    public ObservableCollection<TimerItemViewModel> MoreTimers { get; } = [];
    public bool HasMoreTimers { get => _hasMoreTimers; private set => Set(ref _hasMoreTimers, value); }
    public string NewTimerName { get => _newTimerName; set => Set(ref _newTimerName, value); }
    /// <summary>Length of the next countdown, in minutes.</summary>
    public decimal? CountdownMinutes { get => _countdownMinutes; set => Set(ref _countdownMinutes, value); }
    public ICommand AddTaskTimerCommand { get; }
    public ICommand AddCountdownCommand { get; }

    // ----- Alarms -----
    public ObservableCollection<AlarmItemViewModel> Alarms { get; } = [];
    public bool HasAlarms { get => _hasAlarms; private set => Set(ref _hasAlarms, value); }
    public TimeSpan? NewAlarmTime { get => _newAlarmTime; set => Set(ref _newAlarmTime, value); }
    public string NewAlarmLabel { get => _newAlarmLabel; set => Set(ref _newAlarmLabel, value); }
    public bool NewAlarmDaily { get => _newAlarmDaily; set => Set(ref _newAlarmDaily, value); }
    public ICommand AddAlarmCommand { get; }

    // ----- Starting a review timer on a pull request you already approved asks first -----
    public bool IsConfirmingApproved { get => _isConfirmingApproved; private set => Set(ref _isConfirmingApproved, value); }
    public string ConfirmMessage { get => _confirmMessage; private set => Set(ref _confirmMessage, value); }
    public ICommand ConfirmApprovedStartCommand { get; }
    public ICommand CancelApprovedStartCommand { get; }

    // ----- History -----
    public ObservableCollection<SessionViewModel> History { get; } = [];
    public string TodayTotal { get => _todayTotal; private set => Set(ref _todayTotal, value); }
    public bool HasHistory { get => _hasHistory; private set => Set(ref _hasHistory, value); }

    public override void Apply(MonitorState state)
    {
        _approved = [.. state.Open.Where(p => p.ViewerHasApproved).Select(p => p.Id)];
        StopApprovedReviews();

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
        var label = $"{pr.Repository.Name} #{pr.Number} · {pr.Title}";
        var startedApproved = pr.ViewerHasApproved;

        // The main review timer is busy with another pull request: time this one on its own timer.
        if (_review.IsRunning && _selectedReviewTarget?.Id != pr.Id)
        {
            var existing = MoreTimers.FirstOrDefault(t => t.PullRequestId == pr.Id);
            if (existing is not null)
            {
                if (!existing.IsRunning) ToggleItem(existing);
                return;
            }

            var item = AddItem(TimerKind.PullRequestReview, label, null, pr.Id, pr.Url);
            item.StartedWhileApproved = startedApproved;
            ToggleItem(item);
            return;
        }

        var target = ReviewTargets.FirstOrDefault(t => t.Id == pr.Id);
        if (target is null)
        {
            target = new ReviewTargetViewModel(pr.Id, label, pr.Url);
            ReviewTargets.Insert(0, target);
        }

        SelectedReviewTarget = target;
        if (!_review.IsRunning)
        {
            _reviewStartedWhileApproved = startedApproved;
            StartReviewNow();
        }
    }

    // ----- Extra timers -----
    private TimerItemViewModel AddItem(TimerKind kind, string name, TimeSpan? total, string? prId = null, string? prUrl = null)
    {
        var item = new TimerItemViewModel(kind, name.Trim(), total, prId, prUrl, ToggleItem, FinishItem, RemoveItem, StopItem);
        MoreTimers.Add(item);
        if (kind == TimerKind.Task) NewTimerName = string.Empty;
        HasMoreTimers = true;
        if (kind != TimerKind.PullRequestReview) ToggleItem(item); // a timer you just made starts right away
        else item.Tick(_time.GetUtcNow());
        return item;
    }

    private void AddCountdown()
    {
        var minutes = (int)Math.Clamp(CountdownMinutes ?? 10, 1, 24 * 60);
        var name = string.IsNullOrWhiteSpace(NewTimerName) ? $"{minutes} min countdown" : NewTimerName;
        AddItem(TimerKind.Countdown, name, TimeSpan.FromMinutes(minutes));
        NewTimerName = string.Empty;
    }

    private void ToggleItem(TimerItemViewModel item)
    {
        var now = _time.GetUtcNow();
        if (item.IsRunning) item.Pause(now);
        else item.Start(now);
        Refresh();
        Announce($"{item.KindLabel} timer {(item.IsRunning ? "started" : "paused")}", $"{item.Title} · {item.Clock}");
    }

    private void StopItem(TimerItemViewModel item)
    {
        if (item.IsRunning) ToggleItem(item);
    }

    private void FinishItem(TimerItemViewModel item) => FinishItem(item, null);

    private void FinishItem(TimerItemViewModel item, string? why)
    {
        var now = _time.GetUtcNow();
        item.Pause(now);
        if (!item.IsFinished && item.FirstStartedAt is { } started && item.Elapsed(now) > TimeSpan.Zero)
        {
            Save(new TimerSession(
                Guid.NewGuid(), item.Kind, item.Name, started, item.Elapsed(now), item.PullRequestUrl));
            Announce(why ?? $"{item.KindLabel} timer finished", $"{item.Title} · {Humanize.Clock(item.Elapsed(now))}");
        }

        RemoveItem(item);
    }

    private void RemoveItem(TimerItemViewModel item)
    {
        MoreTimers.Remove(item);
        HasMoreTimers = MoreTimers.Count > 0;
        Refresh();
    }

    /// <summary>A countdown ran out: save it, tell the user and ring.</summary>
    private void CountdownFinished(TimerItemViewModel item)
    {
        if (item.FirstStartedAt is { } started && item.Total is { } total)
            Save(new TimerSession(Guid.NewGuid(), TimerKind.Countdown, item.Name, started, total));
        _ = _notifications.PublishAsync(new ToastRequest("Countdown finished", item.Title, ToastTone.Timer));
        _alarm.Play();
    }

    // ----- Stop the review timer once the pull request has been approved -----
    private void StopApprovedReviews()
    {
        if (_review.IsRunning && !_reviewStartedWhileApproved
            && _selectedReviewTarget is { } target && _approved.Contains(target.Id))
        {
            FinishReview("Review timer stopped · you approved");
        }

        foreach (var item in MoreTimers
                     .Where(t => t.IsReview && t.IsRunning && !t.StartedWhileApproved
                                 && t.PullRequestId is { } id && _approved.Contains(id)).ToList())
        {
            FinishItem(item, "Review timer stopped · you approved");
        }
    }

    private void ResolveApprovedPrompt(bool start)
    {
        var action = _pendingApprovedStart;
        _pendingApprovedStart = null;
        IsConfirmingApproved = false;
        if (start) action?.Invoke();
    }

    // ----- Alarms -----
    private void AddAlarm()
    {
        if (NewAlarmTime is not { } at) return;
        var entry = new AlarmEntry(Guid.NewGuid(), NewAlarmLabel.Trim(), TimeOnly.FromTimeSpan(at), true, NewAlarmDaily);
        NewAlarmLabel = string.Empty;
        _ = _settings.UpdateAsync(s => s with { Alarms = [.. s.Alarms, entry] });
    }

    private void SetAlarmEnabled(AlarmEntry entry, bool enabled) =>
        _ = _settings.UpdateAsync(s => s with
        {
            Alarms = [.. s.Alarms.Select(a => a.Id == entry.Id ? a with { Enabled = enabled } : a)],
        });

    private void RemoveAlarm(AlarmEntry entry) =>
        _ = _settings.UpdateAsync(s => s with { Alarms = [.. s.Alarms.Where(a => a.Id != entry.Id)] });

    private void RebuildAlarms()
    {
        var wanted = _settings.Current.Alarms.OrderBy(a => a.At).ToList();
        Alarms.Clear();
        foreach (var alarm in wanted) Alarms.Add(new AlarmItemViewModel(alarm, SetAlarmEnabled, RemoveAlarm));
        HasAlarms = Alarms.Count > 0;
    }

    private void CheckAlarms()
    {
        var local = _time.GetLocalNow().DateTime;
        var today = DateOnly.FromDateTime(local);
        var due = _settings.Current.Alarms.Where(a => a.IsDue(local) && !_rang.Contains((a.Id, today))).ToList();
        if (due.Count == 0) return;

        foreach (var alarm in due) _rang.Add((alarm.Id, today));
        var ids = due.Select(a => a.Id).ToHashSet();
        _ = _settings.UpdateAsync(s => s with
        {
            Alarms = [.. s.Alarms.Select(a => ids.Contains(a.Id) ? a.Rang(local) : a)],
        });
        foreach (var alarm in due)
            _ = _notifications.PublishAsync(new ToastRequest(
                "Alarm", string.IsNullOrWhiteSpace(alarm.Label) ? alarm.At.ToString("t") : alarm.Label, ToastTone.Timer));
        _alarm.Play();
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
            _alarm.Play();
        }

        Refresh();
    }

    private void ToggleReview()
    {
        if (_selectedReviewTarget is not { } target) return;
        if (!_review.IsRunning && _approved.Contains(target.Id))
        {
            ConfirmMessage = $"You already approved {target.Label}. Start a review timer anyway? It will not stop by itself.";
            _pendingApprovedStart = () =>
            {
                _reviewStartedWhileApproved = true;
                StartReviewNow();
            };
            IsConfirmingApproved = true;
            return;
        }

        if (!_review.IsRunning) _reviewStartedWhileApproved = false;
        StartReviewNow();
    }

    /// <summary>Starts or pauses the main review timer without asking anything.</summary>
    private void StartReviewNow()
    {
        if (_selectedReviewTarget is null) return;
        var now = _time.GetUtcNow();
        if (_review.IsRunning) _review.Pause(now);
        else _review.Start(now);
        Refresh();
        Announce(_review.IsRunning ? "Review timer started" : "Review timer paused",
            $"{_selectedReviewTarget.Label} · {ReviewElapsed}");
    }

    private void FinishReview() => FinishReview(null);

    private void FinishReview(string? why)
    {
        var now = _time.GetUtcNow();
        _review.Pause(now);
        if (_selectedReviewTarget is { } target && _review.FirstStartedAt is { } started)
        {
            Save(new TimerSession(
                Guid.NewGuid(), TimerKind.PullRequestReview, target.Label, started, _review.Elapsed(now), target.Url));
            Announce(why ?? "Review timer finished", $"{target.Label} · {Humanize.Clock(_review.Elapsed(now))}");
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

        var finishedCountdowns = new List<TimerItemViewModel>();
        foreach (var item in MoreTimers)
        {
            if (item.Tick(now)) finishedCountdowns.Add(item);
        }

        foreach (var item in finishedCountdowns) CountdownFinished(item);

        var anyRunning = _task.IsRunning || _pomodoro.IsRunning || _review.IsRunning || MoreTimers.Any(t => t.IsRunning);
        IsAnyRunning = anyRunning;
        var extra = MoreTimers.FirstOrDefault(t => t.IsRunning);
        (WidgetLabel, WidgetClock) = (_pomodoro.IsRunning, _review.IsRunning, _task.IsRunning) switch
        {
            (true, _, _) => ($"Pomodoro · {PomodoroPhase}", PomodoroRemaining),
            (_, true, _) => ("PR review", ReviewElapsed),
            (_, _, true) => (string.IsNullOrWhiteSpace(TaskName) ? "Task" : TaskName.Trim(), TaskElapsed),
            _ when extra is not null => (extra.Title, extra.Clock),
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

    private void OnSettingsChanged(AppSettings _) => _ui.Post(() =>
    {
        UpdateRunningLines(_time.GetUtcNow());
        RebuildAlarms();
    });

    /// <summary>Updates the per-timer lines every tick and changes the lists only when a timer starts or stops.</summary>
    private void UpdateRunningLines(DateTimeOffset now)
    {
        _pomodoroLine.Update($"Pomodoro · {PomodoroPhase}", PomodoroRemaining, _pomodoro.Progress(now));
        _reviewLine.Update(_selectedReviewTarget is { } t ? $"PR review · {t.Label}" : "PR review", ReviewElapsed,
            RunningTimerViewModel.MinuteSweep(_review.Elapsed(now)));
        _taskLine.Update(TaskTitle, TaskElapsed, RunningTimerViewModel.MinuteSweep(_task.Elapsed(now)));

        var running = new List<RunningTimerViewModel>(3 + MoreTimers.Count);
        if (_pomodoro.IsRunning) running.Add(_pomodoroLine);
        if (_review.IsRunning) running.Add(_reviewLine);
        if (_task.IsRunning) running.Add(_taskLine);
        running.AddRange(MoreTimers.Where(t => t.IsRunning).Select(t => t.Line));
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
                TimerKind.Countdown => "Countdown",
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
        _alarmTicker?.Dispose();
    }
}
