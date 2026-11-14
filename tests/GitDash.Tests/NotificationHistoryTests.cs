using GitDash.Application.Abstractions;
using GitDash.Application.Notifications;
using GitDash.Infrastructure.Demo;
using GitDash.Presentation.Mvvm;
using GitDash.Presentation.ViewModels;

namespace GitDash.Tests;

public class NotificationHistoryTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Inline : IUiDispatcher
    {
        public void Post(Action action) => action();
    }

    private static ToastRequest Request(int n, string? actor = null) =>
        new($"Title {n}", $"Message {n}", ToastTone.Comment, $"https://github.com/o/r/pull/{n}", actor);

    [Fact]
    public void Keeps_only_the_newest_fifty_newest_first()
    {
        var history = new NotificationHistory(new InMemoryNotificationStore(), TimeProvider.System);
        for (var i = 1; i <= 60; i++) history.Record(Request(i));

        Assert.Equal(NotificationHistory.Capacity, history.Entries.Count);
        Assert.Equal("Title 60", history.Entries[0].Title);
        Assert.Equal("Title 11", history.Entries[^1].Title);
        Assert.Equal(50, history.UnreadCount);
    }

    [Fact]
    public void Entries_carry_their_time_and_can_be_marked_read()
    {
        var clock = new Clock(new DateTimeOffset(2026, 11, 14, 9, 30, 0, TimeSpan.Zero));
        var history = new NotificationHistory(new InMemoryNotificationStore(), clock);
        history.Record(Request(1));
        history.Record(Request(2));

        Assert.Equal(clock.Now, history.Entries[0].At);
        history.MarkRead(history.Entries[0].Id);
        Assert.Equal(1, history.UnreadCount);
        history.MarkAllRead();
        Assert.Equal(0, history.UnreadCount);
        history.Clear();
        Assert.Empty(history.Entries);
    }

    [Fact]
    public async Task History_survives_a_restart()
    {
        var store = new InMemoryNotificationStore();
        var first = new NotificationHistory(store, TimeProvider.System);
        first.Record(Request(1, "maya"));
        first.Record(Request(2));
        first.MarkRead(first.Entries[1].Id);
        await Task.Delay(100); // saving is fire-and-forget

        var second = new NotificationHistory(store, TimeProvider.System);
        await second.LoadAsync();

        Assert.Equal(2, second.Entries.Count);
        Assert.Equal("Title 2", second.Entries[0].Title);
        Assert.Equal("maya", second.Entries[1].Actor);
        Assert.Equal(1, second.UnreadCount);
    }

    [Fact]
    public void Bell_shows_unread_count_and_opening_an_item_marks_it_read()
    {
        var history = new NotificationHistory(new InMemoryNotificationStore(), TimeProvider.System);
        string? opened = null;
        var center = new NotificationCenterViewModel(history, new Inline(), TimeProvider.System, url => opened = url);

        Assert.True(center.IsEmpty);
        history.Record(Request(1));
        history.Record(Request(2));
        Assert.Equal(2, center.UnreadCount);
        Assert.Equal("2", center.BadgeText);
        Assert.Equal(2, center.Items.Count);

        center.IsOpen = true;
        center.Items[0].OpenCommand.Execute(null);
        Assert.Equal("https://github.com/o/r/pull/2", opened);
        Assert.False(center.IsOpen);
        Assert.Equal(1, center.UnreadCount);

        center.MarkAllReadCommand.Execute(null);
        Assert.False(center.HasUnread);
        foreach (var item in center.Items) Assert.False(item.IsUnread);
    }

    [Fact]
    public void Time_text_is_friendly()
    {
        var now = new DateTimeOffset(2026, 11, 14, 15, 0, 0, TimeSpan.Zero);
        var center = new NotificationCenterViewModel(null, new Inline(), new Clock(now), _ => { });
        var local = now.ToLocalTime();

        Assert.True(center.WhenText(now).StartsWith("Today ", StringComparison.Ordinal));
        Assert.True(center.WhenText(now.AddDays(-1)).StartsWith("Yesterday ", StringComparison.Ordinal));
        Assert.True(center.WhenText(now.AddDays(-30)).Contains(local.AddDays(-30).ToString("MMM"), StringComparison.Ordinal));
    }
}
