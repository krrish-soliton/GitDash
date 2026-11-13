using GithubDash.Application.Abstractions;
using GithubDash.Application.Accounts;
using GithubDash.Application.Monitoring;
using GithubDash.Application.Settings;
using GithubDash.Application.Timers;
using GithubDash.Infrastructure.Demo;
using GithubDash.Infrastructure.Storage;
using GithubDash.Infrastructure.Security;
using GithubDash.Presentation.Mvvm;
using GithubDash.Presentation.ViewModels;

namespace GithubDash.Tests;

/// <summary>Runs the real application services and view-models against the offline demo gateway.</summary>
/// <summary>Stores through the real JSON serializer, like the SQLite cache does.</summary>
internal sealed class JsonStateCache : IStateCache
{
    public readonly Dictionary<string, (string Json, string? ETag, DateTimeOffset At)> Rows = [];
    public string? ActivityJson;

    public Task<IReadOnlyList<CachedRepository>> LoadRepositoriesAsync(CancellationToken ct = default)
    {
        lock (Rows)
        {
            return Task.FromResult<IReadOnlyList<CachedRepository>>([.. Rows.Select(r =>
            {
                var snapshot = SnapshotSerializer.ReadSnapshot(r.Value.Json)!;
                return new CachedRepository(snapshot.AccountId, snapshot.Repository.Owner, snapshot.Repository.Name,
                    snapshot, r.Value.ETag, r.Value.At);
            })]);
        }
    }

    public Task SaveRepositoriesAsync(IReadOnlyList<CachedRepository> items, CancellationToken ct = default)
    {
        lock (Rows)
        {
            foreach (var i in items) Rows[$"{i.AccountId}|{i.Owner}/{i.Name}"] = (SnapshotSerializer.Write(i.Snapshot), i.ETag, i.SyncedAt);
        }

        return Task.CompletedTask;
    }

    public Task RemoveRepositoryAsync(Guid accountId, string owner, string name, CancellationToken ct = default)
    {
        lock (Rows) Rows.Remove($"{accountId}|{owner}/{name}");
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<GithubDash.Domain.Notifications.PullRequestNotification>> LoadActivityAsync(CancellationToken ct = default) =>
        Task.FromResult(ActivityJson is null ? [] : SnapshotSerializer.ReadActivity(ActivityJson));

    public Task SaveActivityAsync(IReadOnlyList<GithubDash.Domain.Notifications.PullRequestNotification> items, CancellationToken ct = default)
    {
        ActivityJson = SnapshotSerializer.Write(items);
        return Task.CompletedTask;
    }
}

internal sealed class NoAlarm : IAlarm { public void Play() { } }

public class EndToEndTests
{
    private sealed class RecordingToasts : IToastPresenter
    {
        public List<ToastRequest> Shown { get; } = [];
        public void Show(ToastRequest request) => Shown.Add(request);
    }

    private sealed class NoNative : INativeNotifier
    {
        public int Calls { get; private set; }

        public Task ShowAsync(string title, string message, CancellationToken ct = default)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private sealed class NoLauncher : IUrlLauncher
    {
        public bool Open(string url) => true;
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
    }

    private sealed record Fixture(
        SettingsService Settings, AccountService Accounts, PullRequestMonitor Monitor,
        NotificationRouter Router, RecordingToasts Toasts, NoNative Native, TimerHistory History,
        PersonActivityService PersonActivity);

    private static async Task<Fixture> CreateAsync(int changeEveryProbes = 1)
    {
        var time = TimeProvider.System;
        var gateway = new DemoGitHubGateway(time, changeEveryProbes);
        var settings = new SettingsService(new InMemorySettingsStore());
        await settings.LoadAsync();
        var accounts = new AccountService(settings, new InMemorySecretStore(), gateway, new DemoDeviceFlowAuthenticator());
        var toasts = new RecordingToasts();
        var native = new NoNative();
        var router = new NotificationRouter(settings, toasts, native);
        var monitor = new PullRequestMonitor(settings, accounts, gateway, router, time);

        var account = await accounts.AddWithTokenAsync("demo-token", AuthMethod.PersonalAccessToken);
        await settings.UpdateAsync(s => s with
        {
            Repositories = [.. DemoGitHubGateway.Monitored.Select(r => new MonitoredRepository(account.Id, r.Owner, r.Name))],
        });
        return new(settings, accounts, monitor, router, toasts, native, new TimerHistory(new InMemoryTimerSessionStore()),
            new PersonActivityService(settings, accounts, gateway, time));
    }

    [Fact]
    public async Task Cache_restores_state_before_any_network_sync()
    {
        var f = await CreateAsync();
        var cache = new JsonStateCache();
        var time = TimeProvider.System;
        var first = new PullRequestMonitor(f.Settings, f.Accounts, new DemoGitHubGateway(time, 1), f.Router, time, cache);
        await first.SyncAsync();
        for (var i = 0; i < 100 && cache.Rows.Count < 3; i++) await Task.Delay(20);
        Assert.Equal(3, cache.Rows.Count);

        // A fresh monitor (app restart) shows the saved data without contacting GitHub.
        var second = new PullRequestMonitor(f.Settings, f.Accounts, new DemoGitHubGateway(time, 1), f.Router, time, cache);
        await second.RestoreAsync();
        Assert.Equal(first.State.Summary.TotalOpen, second.State.Summary.TotalOpen);
        Assert.Equal(first.State.Open.Count, second.State.Open.Count);
        Assert.NotNull(second.State.LastSyncedAt);
        Assert.Equal(first.State.Open[0].Title, second.State.Open[0].Title);

        await first.DisposeAsync();
        await second.DisposeAsync();
        await f.Monitor.DisposeAsync();
    }

    [Fact]
    public async Task Monitor_builds_state_and_notifies_only_after_the_baseline()
    {
        var f = await CreateAsync();

        await f.Monitor.SyncAsync();
        Assert.Empty(f.Toasts.Shown);
        Assert.True(f.Monitor.State.Summary.TotalOpen > 0);
        Assert.Equal(3, f.Monitor.State.RepositoryCount);
        Assert.Empty(f.Monitor.State.Errors);

        await f.Monitor.SyncAsync();
        Assert.Contains(f.Toasts.Shown, t => t.Tone == ToastTone.Assigned);
        Assert.Contains(f.Toasts.Shown, t => t.Tone == ToastTone.Review);
        Assert.Contains(f.Toasts.Shown, t => t.Tone == ToastTone.Comment);

        await f.Monitor.SyncAsync();
        Assert.Contains(f.Toasts.Shown, t => t.Tone == ToastTone.Merged);
        Assert.True(f.Monitor.State.RecentActivity.Count >= 4);
        await f.Monitor.DisposeAsync();
    }

    [Fact]
    public async Task Unchanged_repositories_are_not_downloaded_again()
    {
        var f = await CreateAsync(changeEveryProbes: 1000);

        await f.Monitor.SyncAsync();
        var revision = f.Monitor.State.Revision;
        var firstCheck = f.Monitor.State.LastCheckedAt;
        Assert.True(revision > 0);

        // The demo data would raise notifications on a second download; none means nothing was downloaded.
        await Task.Delay(20);
        await f.Monitor.SyncAsync();
        await f.Monitor.SyncAsync();
        Assert.Equal(revision, f.Monitor.State.Revision);
        Assert.Empty(f.Toasts.Shown);
        Assert.True(f.Monitor.State.LastCheckedAt > firstCheck);
        await f.Monitor.DisposeAsync();
    }

    [Fact]
    public async Task Native_style_routes_to_the_operating_system()
    {
        var f = await CreateAsync();
        await f.Settings.UpdateAsync(s => s with { NotificationStyle = NotificationStyle.Native });
        await f.Router.PublishAsync(new ToastRequest("t", "m", ToastTone.Timer));
        Assert.Equal(1, f.Native.Calls);
        Assert.Empty(f.Toasts.Shown);
        await f.Monitor.DisposeAsync();
    }

    [Fact]
    public async Task Removing_an_account_removes_its_token_and_repositories()
    {
        var f = await CreateAsync();
        var id = f.Settings.Current.Accounts[0].Id;
        await f.Accounts.RemoveAsync(id);
        Assert.Empty(f.Settings.Current.Accounts);
        Assert.Empty(f.Settings.Current.Repositories);
        Assert.Null(await f.Accounts.GetTokenAsync(id));
        await f.Monitor.DisposeAsync();
    }

    [Fact]
    public async Task View_models_render_synced_state()
    {
        var f = await CreateAsync();
        var shell = new ShellViewModel(
            f.Monitor, f.Settings, f.Accounts, f.History, f.Router, f.PersonActivity, new NoLauncher(), new NoAlarm(), new InlineDispatcher(),
            TimeProvider.System);

        await f.Monitor.SyncAsync();
        Assert.Same(shell.Home, shell.CurrentPage);
        shell.Navigate(shell.Dashboard);
        Assert.Equal(f.Monitor.State.Summary.TotalOpen, shell.Dashboard.TotalOpen);
        Assert.False(shell.Dashboard.IsEmpty);
        Assert.Equal(3, shell.Dashboard.Repositories.Count);
        Assert.Equal(shell.Dashboard.TotalOpen, shell.Widget.Total);
        Assert.True(shell.Widget.HasOldest);
        Assert.NotEqual("none waiting", shell.Widget.OldestAge);
        Assert.NotEmpty(shell.Dashboard.DeskBooks);
        Assert.Contains(shell.Dashboard.DeskBooks, b => b.IsAssigned);

        shell.Navigate(shell.People);
        Assert.Equal(3, shell.People.Floors.Count);
        Assert.Equal("Floor 3", shell.People.Floors[0].FloorLabel);
        Assert.Contains(shell.People.Floors.SelectMany(x => x.People), p => p.IsViewer);
        var someone = shell.People.Floors[0].People.First(p => p.Raised > 0);
        shell.People.SelectPersonCommand.Execute(someone);
        Assert.True(shell.People.IsPersonOpen);
        Assert.Equal(someone.Raised, shell.People.RaisedItems.Count);
        Assert.Equal(someone.AssignedUnder, shell.People.WaitingItems.Count);
        for (var i = 0; i < 100 && !shell.People.HasActivity; i++) await Task.Delay(20);
        Assert.True(shell.People.HasActivity, shell.People.ActivityError);
        Assert.Contains(shell.People.ActivityTiles, t => t.Label == "Merged");
        Assert.NotEmpty(shell.People.ActivityEvents);
        Assert.NotEmpty(shell.People.RecentPullRequests);

        shell.Navigate(shell.MyPullRequests);
        var my = shell.MyPullRequests;
        Assert.Equal(PullRequestFilter.AssignedToMe, my.SelectedFilter!.Filter); // default filter
        Assert.True(my.Cards.Count > 1);
        Assert.True(my.Cards.All(c => c.PullRequest.IsAssignedToViewer));
        Assert.Equal(my.Cards.Count, my.Filters[0].Count);

        my.SelectedFilter = my.Filters.Single(x => x.Filter == PullRequestFilter.All);
        Assert.Equal(shell.Dashboard.TotalOpen, my.Cards.Count);
        my.SelectedFilter = my.Filters.Single(x => x.Filter == PullRequestFilter.Own);
        Assert.True(my.Cards.Count > 0 && my.Cards.All(c => c.PullRequest.IsAuthoredByViewer));
        my.SelectedFilter = my.Filters.Single(x => x.Filter == PullRequestFilter.ReviewRequested);
        Assert.True(my.Cards.All(c => c.PullRequest.NeedsViewerApproval));

        var cards = my.Cards;
        Assert.True(cards.Count > 1);
        Assert.Equal($"1 of {cards.Count}", my.Position);
        Assert.True(cards[0].IsReapproval); // re-approvals come first

        my.ActivateCommand.Execute(cards[1]);
        Assert.Equal(1, my.SelectedIndex);
        Assert.False(my.IsDetailOpen);
        my.ActivateCommand.Execute(cards[1]);
        Assert.True(my.IsDetailOpen);
        for (var i = 0; i < 50 && my.IsDetailLoading; i++) await Task.Delay(20);

        // Timeline: grouped by day, newest first, filterable by category.
        Assert.True(my.TimelineDays.Count >= 2);
        var total = my.TimelineDays.Sum(d => d.Items.Count);
        my.ShowCommits = false;
        my.ShowEvents = false;
        Assert.True(my.TimelineDays.Sum(d => d.Items.Count) < total);
        Assert.True(my.TimelineDays.SelectMany(d => d.Items).All(i => i.Kind != "Commit" && i.Kind != "Opened"));

        // Comments: multi-select groups.
        Assert.Equal(7, my.Comments.Count);
        Assert.Contains(my.Comments, c => c.IsAi);
        my.CommentsByOthers = false;
        Assert.Equal(5, my.Comments.Count); // three of mine, the one that mentions me and the AI review
        my.CommentsFromAi = false;
        Assert.Equal(4, my.Comments.Count);
        my.CommentsMentioningMe = false;
        Assert.True(my.Comments.Count == 3 && my.Comments.All(c => c.IsMine));
        my.CommentsByMe = false;
        Assert.Empty(my.Comments);

        Assert.Contains("How to test", my.Description);
        Assert.Contains(my.ReviewEvents, e => e.Verdict == "APPROVED");
        Assert.Contains(my.ReviewEvents, e => e.Verdict == "CHANGES REQUESTED");

        var reviewed = cards[1];
        my.StartReviewCommand.Execute(reviewed);
        Assert.False(my.IsDetailOpen);
        Assert.Same(shell.Timers, shell.CurrentPage);
        Assert.True(shell.Timers.IsReviewRunning);
        Assert.Equal(reviewed.Id, shell.Timers.SelectedReviewTarget?.Id);

        shell.Navigate(shell.Settings);
        Assert.Single(shell.Settings.Accounts);
        for (var i = 0; i < 50 && shell.Settings.Repositories.Count == 0; i++) await Task.Delay(20);
        Assert.Equal(3, shell.Settings.Repositories.Count(r => r.IsSelected));
        shell.Settings.Repositories.First(r => r.IsSelected).IsSelected = false;
        for (var i = 0; i < 50 && f.Settings.Current.Repositories.Count != 2; i++) await Task.Delay(20);
        Assert.Equal(2, f.Settings.Current.Repositories.Count);

        shell.Dispose();
        await f.Monitor.DisposeAsync();
    }

    [Fact]
    public async Task Finished_task_timer_is_saved_to_history()
    {
        var f = await CreateAsync();
        var clock = new ManualTime();
        var timers = new TimersViewModel(f.Settings, f.History, f.Router, new InlineDispatcher(), clock);

        timers.TaskName = "Write report";
        timers.ToggleTaskCommand.Execute(null);
        clock.Advance(TimeSpan.FromMinutes(90));
        timers.FinishTaskCommand.Execute(null);

        for (var i = 0; i < 50 && f.History.Sessions.Count == 0; i++) await Task.Delay(20);
        var session = Assert.Single(f.History.Sessions);
        Assert.Equal("Write report", session.Label);
        Assert.Equal(TimeSpan.FromMinutes(90), session.Duration);
        Assert.Equal("00:00", timers.TaskElapsed);
        timers.Dispose();
        await f.Monitor.DisposeAsync();
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
