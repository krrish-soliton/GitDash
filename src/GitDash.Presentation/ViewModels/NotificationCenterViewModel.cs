using System.Collections.ObjectModel;
using System.Windows.Input;
using GitDash.Application.Abstractions;
using GitDash.Application.Notifications;
using GitDash.Presentation.Mvvm;

namespace GitDash.Presentation.ViewModels;

/// <summary>One line of the notification list.</summary>
public sealed class NotificationItemViewModel(
    NotificationEntry entry, string whenText, Action<NotificationItemViewModel> open, Action<Guid> markRead)
    : ObservableObject
{
    public Guid Id => entry.Id;
    public string Title => entry.Title;
    public string Message => entry.Message;
    public string Tone => entry.Tone.ToString();
    public string? Url => entry.Url;
    public string WhenText => whenText;
    public bool IsUnread => !entry.Read;
    /// <summary>Picture of the person involved; null when there is none or it is not known yet.</summary>
    public string? AvatarUrl => AvatarDirectory.UrlFor(entry.Actor);
    public bool HasAvatar => AvatarUrl is not null;
    public ICommand OpenCommand => _open ??= new RelayCommand(() => open(this));
    public ICommand MarkReadCommand => _markRead ??= new RelayCommand(() => markRead(entry.Id), () => IsUnread);

    private RelayCommand? _open, _markRead;
}

/// <summary>The bell in the title bar: unread count, the last 50 notifications, mark as read.</summary>
public sealed class NotificationCenterViewModel : ObservableObject
{
    private readonly NotificationHistory? _history;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private readonly Action<string> _openUrl;
    private bool _isOpen;

    public NotificationCenterViewModel(
        NotificationHistory? history, IUiDispatcher ui, TimeProvider time, Action<string> openUrl)
    {
        _history = history;
        _ui = ui;
        _time = time;
        _openUrl = openUrl;
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
    public string Summary => HasUnread ? $"{UnreadCount} unread" : "All caught up";

    public ICommand ToggleCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand MarkAllReadCommand { get; }
    public ICommand ClearCommand { get; }

    private void Refresh()
    {
        Items.Clear();
        if (_history is not null)
            foreach (var entry in _history.Entries)
                Items.Add(new NotificationItemViewModel(entry, WhenText(entry.At), OnOpen, id => _history?.MarkRead(id)));

        Raise(nameof(UnreadCount));
        Raise(nameof(HasUnread));
        Raise(nameof(HasItems));
        Raise(nameof(IsEmpty));
        Raise(nameof(BadgeText));
        Raise(nameof(Summary));
        ((RelayCommand)MarkAllReadCommand).RaiseCanExecuteChanged();
        ((RelayCommand)ClearCommand).RaiseCanExecuteChanged();
    }

    /// <summary>Opening a notification marks it read and, when it is about a pull request, shows that pull request.</summary>
    private void OnOpen(NotificationItemViewModel item)
    {
        _history?.MarkRead(item.Id);
        if (!string.IsNullOrEmpty(item.Url))
        {
            IsOpen = false;
            _openUrl(item.Url);
        }
    }

    /// <summary>"Today 14:32", "Yesterday 09:05", "Mon 16:20", then "12 Nov 16:20".</summary>
    public string WhenText(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        var today = _time.GetLocalNow().Date;
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
