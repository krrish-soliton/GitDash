using System.Collections.ObjectModel;
using System.Windows.Input;
using GithubDash.Application.Abstractions;
using GithubDash.Application.Monitoring;
using GithubDash.Application.Settings;
using GithubDash.Presentation.Mvvm;

namespace GithubDash.Presentation.ViewModels;

/// <summary>One AI tool (Claude, ChatGPT, …) whose usage window the user tracks by hand.</summary>
public sealed class AiToolItemViewModel(AiToolSettings tool) : ObservableObject
{
    private bool _isAvailable = true;
    private string _status = "Available", _remaining = string.Empty;
    private double _progress = 1;

    public Guid Id { get; } = tool.Id;
    public string Name { get; } = tool.Name;
    public int WindowMinutes { get; } = tool.WindowMinutes;
    public string WindowLabel { get; } = $"{Humanize.Age(TimeSpan.FromMinutes(tool.WindowMinutes))} window";
    public string Initials { get; } = Humanize.Initials(tool.Name);
    public string Accent { get; } = Humanize.Accent(tool.Name);
    public DateTimeOffset? ResetsAt { get; } = tool.ResetsAt;

    public bool IsAvailable { get => _isAvailable; private set => Set(ref _isAvailable, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    /// <summary>Countdown such as "2h 14m 05s"; empty while the tool is available.</summary>
    public string Remaining { get => _remaining; private set => Set(ref _remaining, value); }
    /// <summary>1 = fully available, 0 = about to reset. Fills the ring in the UI while a limit is active.</summary>
    public double Progress { get => _progress; private set => Set(ref _progress, value); }

    public void Update(DateTimeOffset now)
    {
        if (ResetsAt is not { } resetsAt || resetsAt <= now)
        {
            IsAvailable = true;
            Status = "Available";
            Remaining = string.Empty;
            Progress = 1;
            return;
        }

        var left = resetsAt - now;
        IsAvailable = false;
        Status = "Limit reached";
        Remaining = Humanize.Clock(left);
        Progress = Math.Clamp(1 - left.TotalMinutes / Math.Max(1, WindowMinutes), 0, 1);
    }
}

/// <summary>
/// Tracks usage windows of AI tools. No vendor offers a read-only usage API for subscriptions, so the user marks
/// "limit reached" and the app counts down, then raises a toast with an alarm when the tool is usable again.
/// </summary>
public sealed class AiToolsViewModel : ObservableObject, IDisposable
{
    public static readonly IReadOnlyList<(string Name, int Minutes)> Presets =
    [
        ("Claude", 300), ("ChatGPT", 180), ("Gemini", 1440), ("GitHub Copilot", 43200), ("Cursor", 43200),
    ];

    private readonly SettingsService _settings;
    private readonly NotificationRouter _notifications;
    private readonly IAlarm _alarm;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private ITimer? _ticker;
    private string _newName = "Claude";
    private int _newWindowMinutes = 300;
    private bool _hasTools;

    public AiToolsViewModel(
        SettingsService settings, NotificationRouter notifications, IAlarm alarm, IUiDispatcher ui, TimeProvider time)
    {
        _settings = settings;
        _notifications = notifications;
        _alarm = alarm;
        _ui = ui;
        _time = time;

        AddCommand = new RelayCommand(Add);
        RemoveCommand = new RelayCommand<AiToolItemViewModel>(t => Mutate(t, tools => tools.Where(x => x.Id != t.Id)));
        LimitReachedCommand = new RelayCommand<AiToolItemViewModel>(t => SetReset(t, _time.GetUtcNow().AddMinutes(t.WindowMinutes)));
        ClearLimitCommand = new RelayCommand<AiToolItemViewModel>(t => SetReset(t, null));
        PresetCommand = new RelayCommand<string>(name =>
        {
            var preset = Presets.FirstOrDefault(p => p.Name == name);
            if (preset.Name is null) return;
            NewName = preset.Name;
            NewWindowMinutes = preset.Minutes;
        });

        _settings.Changed += _ => _ui.Post(Sync);
        Sync();
    }

    public ObservableCollection<AiToolItemViewModel> Tools { get; } = [];
    public IEnumerable<string> PresetNames => Presets.Select(p => p.Name);
    public bool HasTools { get => _hasTools; private set => Set(ref _hasTools, value); }
    public string NewName { get => _newName; set => Set(ref _newName, value); }
    public int NewWindowMinutes { get => _newWindowMinutes; set => Set(ref _newWindowMinutes, Math.Clamp(value, 1, 44640)); }

    public ICommand AddCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand LimitReachedCommand { get; }
    public ICommand ClearLimitCommand { get; }
    public ICommand PresetCommand { get; }

    private void Add()
    {
        var name = NewName.Trim();
        if (name.Length == 0 || Tools.Count >= 20) return;
        var window = NewWindowMinutes;
        _ = _settings.UpdateAsync(s => s with { AiTools = [.. s.AiTools, new AiToolSettings(Guid.NewGuid(), name, window)] });
    }

    private void SetReset(AiToolItemViewModel tool, DateTimeOffset? resetsAt) =>
        Mutate(tool, tools => tools.Select(x => x.Id == tool.Id ? x with { ResetsAt = resetsAt } : x));

    private void Mutate(AiToolItemViewModel tool, Func<IReadOnlyList<AiToolSettings>, IEnumerable<AiToolSettings>> change) =>
        _ = _settings.UpdateAsync(s => s with { AiTools = [.. change(s.AiTools)] });

    private void Sync()
    {
        var fresh = _settings.Current.AiTools;
        var now = _time.GetUtcNow();
        // Rebuild only when the configured tools changed; the countdown itself never touches settings.
        if (!Tools.Select(t => (t.Id, t.Name, t.WindowMinutes, t.ResetsAt))
                .SequenceEqual(fresh.Select(t => (t.Id, t.Name, t.WindowMinutes, t.ResetsAt))))
        {
            Tools.Clear();
            foreach (var tool in fresh) Tools.Add(new AiToolItemViewModel(tool));
        }

        HasTools = Tools.Count > 0;
        foreach (var t in Tools) t.Update(now);
        EnsureTicker();
    }

    /// <summary>The one-second ticker only runs while at least one limit is counting down.</summary>
    private void EnsureTicker()
    {
        var needed = _settings.Current.AiTools.Any(t => t.ResetsAt is not null);
        if (needed && _ticker is null)
            _ticker = _time.CreateTimer(_ => _ui.Post(Tick), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        else if (!needed && _ticker is not null)
        {
            _ticker.Dispose();
            _ticker = null;
        }
    }

    private void Tick()
    {
        var now = _time.GetUtcNow();
        var due = _settings.Current.AiTools.Where(t => t.ResetsAt is { } at && at <= now).ToList();
        foreach (var t in Tools) t.Update(now);
        if (due.Count == 0) return;

        // Clear first so the alarm can never fire twice for the same reset.
        var ids = due.Select(t => t.Id).ToHashSet();
        _ = _settings.UpdateAsync(s => s with
        {
            AiTools = [.. s.AiTools.Select(t => ids.Contains(t.Id) ? t with { ResetsAt = null } : t)],
        });
        foreach (var tool in due)
            _ = _notifications.PublishAsync(new ToastRequest(
                $"{tool.Name} is ready", "Your usage limit has reset — you can use it again.", ToastTone.Timer));
        _alarm.Play();
    }

    public void Dispose() => _ticker?.Dispose();
}
