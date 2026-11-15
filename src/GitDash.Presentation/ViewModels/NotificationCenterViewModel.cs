using System.Collections.ObjectModel;
using System.Windows.Input;
using GitDash.Application.Abstractions;
using GitDash.Application.Notifications;
using GitDash.Presentation.Mvvm;

namespace GitDash.Presentation.ViewModels;

/// <summary>One "Label: value" line of a notification's details.</summary>
public sealed record NotificationDetailRow(string Label, string Value);

/// <summary>One line of the notification list.</summary>
public sealed class NotificationItemViewModel(
    NotificationEntry entry, string whenText, Action<NotificationItemViewModel> open, Action<Guid> markRead)
    : ObservableObject
{
    private NotificationEntry _entry = entry;

    public Guid Id => _entry.Id;
    public string Title => _entry.Title;
    public string Message => _entry.Message;
    public string Tone => _entry.Tone.ToString();
    public string? Url => _entry.Url;
    public string WhenText => whenText;
    public ToastRequest Request => _entry.ToRequest();

    // ----- What the notification history page shows on top of the short popup row -----
    public string Detail => _entry.Detail ?? string.Empty;
    public bool HasDetail => DetailRows.Count > 0;
    public DateTimeOffset At => _entry.At;

    /// <summary>The clock time on the timeline rail.</summary>
    public string Time => Humanize.Time(_entry.At);

    /// <summary>The details table is folded away by default so the cards stay small.</summary>
    public bool IsExpanded { get => _expanded; private set { if (Set(ref _expanded, value)) { Raise(nameof(ShowDetails)); Raise(nameof(DetailsText)); } } }
    public bool ShowDetails => _expanded && HasDetail;
    public string DetailsText => _expanded ? "Hide details" : "Details";
    public ICommand ToggleDetailsCommand => _toggle ??= new RelayCommand(() => IsExpanded = !IsExpanded);

    /// <summary>The detail lines split at the first colon ("Repository: o/r"); a line without a label keeps just its text.</summary>
    public IReadOnlyList<NotificationDetailRow> DetailRows { get; } = ParseDetail(entry.Detail);

    public static IReadOnlyList<NotificationDetailRow> ParseDetail(string? detail) =>
        string.IsNullOrWhiteSpace(detail)
            ? []
            : [.. detail.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(line =>
            {
                var colon = line.IndexOf(": ", StringComparison.Ordinal);
                return colon is > 0 and <= 28
                    ? new NotificationDetailRow(line[..colon], line[(colon + 2)..])
                    : new NotificationDetailRow(string.Empty, line);
            })];
    /// <summary>The full date and time it arrived.</summary>
    public string Stamp => Humanize.Stamp(_entry.At);
    public string Category => _entry.Tone switch
    {
        ToastTone.Assigned => "Assigned to you",
        ToastTone.Review => "Review requested",
        ToastTone.Comment => "New comment",
        ToastTone.Merged => "Merged / update",
        ToastTone.Removed => "Removed",
        ToastTone.Failed => "Pipeline failed",
        _ => "Timer",
    };
    public string ActionText => _entry.Target switch
    {
        ToastTarget.PullRequestActions => "Open its runs",
        ToastTarget.Timers => "Open timers",
        ToastTarget.Settings => "Open settings",
        _ when !string.IsNullOrEmpty(_entry.Url) => "Open pull request",
        _ => "Open GitDash",
    };
    public string ActorName => _entry.Actor ?? string.Empty;
    public bool HasActor => !string.IsNullOrEmpty(_entry.Actor);
    public bool IsUnread => !_entry.Read;
    /// <summary>Picture of the person involved; null when there is none or it is not known yet.</summary>
    public string? AvatarUrl => AvatarDirectory.UrlFor(_entry.Actor);
    public bool HasAvatar => AvatarUrl is not null;
    public ICommand OpenCommand => _open ??= new RelayCommand(() => open(this));
    public ICommand MarkReadCommand => _markRead ??= new RelayCommand(() => markRead(_entry.Id), () => IsUnread);

    /// <summary>Takes the newest state of the same notification (read or not, picture) so the row can stay where it is.</summary>
    public void Update(NotificationEntry entry)
    {
        _entry = entry;
        Raise(nameof(IsUnread));
        Raise(nameof(AvatarUrl));
        Raise(nameof(HasAvatar));
        _markRead?.RaiseCanExecuteChanged();
    }

    private RelayCommand? _open, _markRead, _toggle;
    private bool _expanded;
}

/// <summary>The bell in the title bar: unread count and the latest unread notifications (the full history has its own page).</summary>
public sealed class NotificationCenterViewModel : ObservableObject
{
    private readonly NotificationHistory? _history;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private readonly Action<ToastRequest> _open;
    private readonly Action _showAll;
    private bool _isOpen;

    /// <summary>The popup lists at most this many notifications, the newest unread ones.</summary>
    public const int PopupLimit = 10;

    public NotificationCenterViewModel(
        NotificationHistory? history, IUiDispatcher ui, TimeProvider time, Action<ToastRequest> open, Action showAll)
    {
        _history = history;
        _ui = ui;
        _time = time;
        _open = open;
        _showAll = showAll;
        ShowAllCommand = new RelayCommand(() =>
        {
            IsOpen = false;
            _showAll();
        });
        ToggleCommand = new RelayCommand(() => IsOpen = !IsOpen);
        CloseCommand = new RelayCommand(() => IsOpen = false);
        MarkAllReadCommand = new RelayCommand(() => _history?.MarkAllRead(), () => HasUnread);
        ClearCommand = new RelayCommand(() => _history?.Clear(), () => HasItems);
        if (history is not null)
        {
            history.Changed += () => _ui.Post(Refresh);
            AvatarDirectory.Changed += () => _ui.Post(Refresh);
        }

        Refresh();
    }

    public ObservableCollection<NotificationItemViewModel> Items { get; } = [];

    public bool IsOpen { get => _isOpen; set { if (Set(ref _isOpen, value) && value) Refresh(); } }
    public int UnreadCount => _history?.UnreadCount ?? 0;
    public bool HasUnread => UnreadCount > 0;
    public bool HasItems => Items.Count > 0;
    public bool IsEmpty => !HasItems;
    /// <summary>"9+" keeps the badge small.</summary>
    public string BadgeText => UnreadCount > 9 ? "9+" : UnreadCount.ToString();
    public string Summary => HasUnread
        ? UnreadCount > PopupLimit ? $"{UnreadCount} unread · showing the latest {PopupLimit}" : $"{UnreadCount} unread"
        : "All caught up";
    public ICommand ShowAllCommand { get; }

    public ICommand ToggleCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand MarkAllReadCommand { get; }
    public ICommand ClearCommand { get; }

    private void Refresh()
    {
        var entries = _history?.Entries.Where(e => !e.Read).Take(PopupLimit).ToList() ?? [];
        if (entries.Count == Items.Count && entries.Select(e => e.Id).SequenceEqual(Items.Select(i => i.Id)))
        {
            // Same list, only read marks or pictures changed: update the rows in place so the list neither jumps nor resizes.
            for (var i = 0; i < entries.Count; i++) Items[i].Update(entries[i]);
        }
        else
        {
            Items.Clear();
            foreach (var entry in entries)
                Items.Add(new NotificationItemViewModel(entry, WhenText(entry.At), OnOpen, id => _history?.MarkRead(id)));
        }

        Raise(nameof(UnreadCount));
        Raise(nameof(HasUnread));
        Raise(nameof(HasItems));
        Raise(nameof(IsEmpty));
        Raise(nameof(BadgeText));
        Raise(nameof(Summary));
        ((RelayCommand)MarkAllReadCommand).RaiseCanExecuteChanged();
        ((RelayCommand)ClearCommand).RaiseCanExecuteChanged();
    }

    /// <summary>Opening a notification marks it read and takes the user to what it is about, inside the app.</summary>
    private void OnOpen(NotificationItemViewModel item)
    {
        _history?.MarkRead(item.Id);
        IsOpen = false;
        _open(item.Request);
    }

    public string WhenText(DateTimeOffset at) => WhenText(at, _time.GetLocalNow().DateTime);

    /// <summary>"Today 14:32", "Yesterday 09:05", "Mon 16:20", then "12 Nov 16:20".</summary>
    public static string WhenText(DateTimeOffset at, DateTime localToday)
    {
        var local = at.ToLocalTime();
        var today = localToday.Date;
        var clock = local.ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture);
        var days = (today - local.Date).Days;
        return days switch
        {
            <= 0 => $"Today {clock}",
            1 => $"Yesterday {clock}",
            < 7 => $"{local:ddd} {clock}",
            _ => $"{local:d MMM} {clock}",
        };
    }
}
