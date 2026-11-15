using System.Collections.ObjectModel;
using System.Windows.Input;
using GitDash.Application.Abstractions;
using GitDash.Application.Monitoring;
using GitDash.Application.Notifications;
using GitDash.Presentation.Mvvm;

namespace GitDash.Presentation.ViewModels;

/// <summary>The notifications of one day under a heading.</summary>
public sealed record NotificationDayViewModel(string Header, IReadOnlyList<NotificationItemViewModel> Items);

/// <summary>
/// The Notifications page: every notification that was kept (the last 50), read or not, with its full text and details,
/// even after the bell's popup was cleared of them.
/// </summary>
public sealed class NotificationsViewModel : PageViewModel
{
    private readonly NotificationHistory? _history;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private readonly Action<ToastRequest> _open;
    private bool _onlyUnread;

    public NotificationsViewModel(
        NotificationHistory? history, IUiDispatcher ui, TimeProvider time, Action<ToastRequest> open)
        : base("Notifications", "IconBell")
    {
        _history = history;
        _ui = ui;
        _time = time;
        _open = open;
        MarkAllReadCommand = new RelayCommand(() => _history?.MarkAllRead(), () => UnreadCount > 0);
        ClearCommand = new RelayCommand(() => _history?.Clear(), () => _history?.Entries.Count > 0);
        if (history is not null) history.Changed += () => _ui.Post(Refresh);
        Refresh();
    }

    public ObservableCollection<NotificationItemViewModel> Items { get; } = [];

    /// <summary>The same notifications grouped by day (newest first), which is what the page lists.</summary>
    public ObservableCollection<NotificationDayViewModel> Days { get; } = [];

    /// <summary>Show only the notifications that were not read yet.</summary>
    public bool OnlyUnread
    {
        get => _onlyUnread;
        set
        {
            if (Set(ref _onlyUnread, value)) Refresh();
        }
    }

    public int UnreadCount => _history?.UnreadCount ?? 0;
    public bool IsEmpty => Items.Count == 0;
    public string EmptyText => _onlyUnread ? "No unread notifications." : "Nothing yet. New pull request activity shows up here.";
    public string Summary
    {
        get
        {
            var total = _history?.Entries.Count ?? 0;
            return total == 0 ? "No notifications yet" : $"{total} kept · {UnreadCount} unread";
        }
    }

    public ICommand MarkAllReadCommand { get; }
    public ICommand ClearCommand { get; }

    public override void Apply(MonitorState state)
    {
        // The list follows the history on its own; nothing to take from the pull request data.
    }

    private void Refresh()
    {
        var entries = (_history?.Entries ?? []).Where(e => !_onlyUnread || !e.Read).ToList();
        if (entries.Count == Items.Count && entries.Select(e => e.Id).SequenceEqual(Items.Select(i => i.Id)))
        {
            for (var i = 0; i < entries.Count; i++) Items[i].Update(entries[i]);
        }
        else
        {
            var today = _time.GetLocalNow().DateTime;
            Items.Clear();
            foreach (var entry in entries)
            {
                Items.Add(new NotificationItemViewModel(
                    entry, NotificationCenterViewModel.WhenText(entry.At, today), OnOpen, id => _history?.MarkRead(id)));
            }
        }

        var now = _time.GetUtcNow();
        var groups = Items.GroupBy(i => Humanize.DayOf(i.At))
            .Select(g => new NotificationDayViewModel(Humanize.Day(g.First().At, now), [.. g])).ToList();
        var sameDays = groups.Count == Days.Count && groups.Zip(Days).All(p =>
            p.First.Header == p.Second.Header && p.First.Items.SequenceEqual(p.Second.Items));
        if (!sameDays)
        {
            Days.Clear();
            foreach (var day in groups) Days.Add(day);
        }

        Raise(nameof(UnreadCount));
        Raise(nameof(IsEmpty));
        Raise(nameof(EmptyText));
        Raise(nameof(Summary));
        ((RelayCommand)MarkAllReadCommand).RaiseCanExecuteChanged();
        ((RelayCommand)ClearCommand).RaiseCanExecuteChanged();
    }

    private void OnOpen(NotificationItemViewModel item)
    {
        _history?.MarkRead(item.Id);
        _open(item.Request);
    }
}
