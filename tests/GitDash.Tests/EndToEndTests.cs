using GitDash.Application.Abstractions;
using GitDash.Application.Accounts;
using GitDash.Application.Monitoring;
using GitDash.Application.Settings;
using GitDash.Application.Timers;
using GitDash.Domain.Insights;
using GitDash.Infrastructure.Demo;
using GitDash.Infrastructure.Storage;
using GitDash.Infrastructure.Security;
using GitDash.Presentation.Mvvm;
using GitDash.Presentation.ViewModels;

namespace GitDash.Tests;

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

    public Task<IReadOnlyList<GitDash.Domain.Notifications.PullRequestNotification>> LoadActivityAsync(CancellationToken ct = default) =>
        Task.FromResult(ActivityJson is null ? [] : SnapshotSerializer.ReadActivity(ActivityJson));

    public Task SaveActivityAsync(IReadOnlyList<GitDash.Domain.Notifications.PullRequestNotification> items, CancellationToken ct = default)
    {
        ActivityJson = SnapshotSerializer.Write(items);
        return Task.CompletedTask;
    }
}

internal sealed class NoAlarm : IAlarm { public void Play() { } public void Preview(string soundId, string? customPath) { } }

internal sealed class CountingAlarm : IAlarm
{
    public int Plays { get; private set; }
    public void Play() => Plays++;
    public void Preview(string soundId, string? customPath) { }
}

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
        public List<string> Opened { get; } = [];

        public bool Open(string url)
        {
            Opened.Add(url);
            return true;
        }
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

    private sealed class FakeCli(string? token, string? problem = null) : IGitHubCliTokenSource
    {
        public Task<string> GetTokenAsync(CancellationToken ct = default) =>
            problem is null ? Task.FromResult(token!) : throw new InvalidOperationException(problem);
    }

    [Fact]
    public async Task Github_cli_login_adds_an_account_with_the_cli_method()
    {
        var settings = new SettingsService(new InMemorySettingsStore());
        await settings.LoadAsync();
        var secrets = new InMemorySecretStore();
        var accounts = new AccountService(
            settings, secrets, new DemoGitHubGateway(TimeProvider.System, 1), new DemoDeviceFlowAuthenticator(), new FakeCli("gho_cli_token"));

        var account = await accounts.AddFromGitHubCliAsync();

        Assert.Equal(AuthMethod.GitHubCli, account.Method);
        Assert.Equal("gho_cli_token", await accounts.GetTokenAsync(account.Id));
        Assert.Equal(1, settings.Current.Accounts.Count);
    }

    [Fact]
    public async Task Github_cli_problems_reach_the_user_and_store_nothing()
    {
        var settings = new SettingsService(new InMemorySettingsStore());
        await settings.LoadAsync();
        var accounts = new AccountService(
            settings, new InMemorySecretStore(), new DemoGitHubGateway(TimeProvider.System, 1), new DemoDeviceFlowAuthenticator(),
            new FakeCli(null, "GitHub CLI (gh) was not found."));

        string? message = null;
        try { await accounts.AddFromGitHubCliAsync(); }
        catch (InvalidOperationException ex) { message = ex.Message; }
        Assert.True(message is not null && message.Contains("not found"));
        Assert.Equal(0, settings.Current.Accounts.Count);

        var without = new AccountService(settings, new InMemorySecretStore(), new DemoGitHubGateway(TimeProvider.System, 1), new DemoDeviceFlowAuthenticator());
        await Assert.ThrowsAsync<InvalidOperationException>(() => without.AddFromGitHubCliAsync());
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
        var launcher = new NoLauncher();
        var shell = new ShellViewModel(
            f.Monitor, f.Settings, f.Accounts, f.History, f.Router, f.PersonActivity, launcher, new NoAlarm(), new InlineDispatcher(),
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

        // Clicking a book opens its details and marks it in the list; clicking it again (or Close) clears it.
        var pick = shell.Dashboard.DeskBooks[0];
        shell.Dashboard.SelectBookCommand.Execute(pick);
        Assert.True(shell.Dashboard.HasSelectedBook);
        Assert.Equal(pick.Id, shell.Dashboard.SelectedBook!.Id);
        Assert.Equal(1, shell.Dashboard.DeskBooks.Count(b => b.IsSelected));
        shell.Dashboard.CloseBookCommand.Execute(null);
        Assert.False(shell.Dashboard.HasSelectedBook);
        Assert.Equal(0, shell.Dashboard.DeskBooks.Count(b => b.IsSelected));

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

        // Filters narrow the lists; "All" brings everything back.
        var allPrs = shell.People.RecentPullRequests.Count;
        var merged = shell.People.PrFilters.First(c => c.Key == "Merged");
        shell.People.SetPrFilterCommand.Execute(merged);
        Assert.True(shell.People.RecentPullRequests.All(p => p.State == "Merged"));
        Assert.True(shell.People.RecentPullRequests.Count <= allPrs);
        shell.People.SetPrFilterCommand.Execute(shell.People.PrFilters[0]);
        Assert.Equal(allPrs, shell.People.RecentPullRequests.Count);
        var comments = shell.People.EventFilters.First(c => c.Key == "Comment");
        shell.People.SetEventFilterCommand.Execute(comments);
        Assert.True(shell.People.ActivityEvents.All(e => e.Kind == "Comment"));

        // The commit-count option loads three counts only when it is switched on.
        Assert.False(shell.People.HasCommitStats);
        shell.People.ShowCommitCounts = true;
        for (var i = 0; i < 100 && !shell.People.HasCommitStats; i++) await Task.Delay(20);
        Assert.Equal(3, shell.People.CommitStats.Count);
        shell.People.ShowCommitCounts = false;
        Assert.False(shell.People.HasCommitStats);

        // The desk pile labels each book with its pull request number.
        var worker = shell.People.Floors.SelectMany(f => f.People).First(p => p.Assigned + p.ToReview + p.Raised > 0);
        Assert.True(worker.DeskBooks.Any(b => b.Label.StartsWith("#")));

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
        Assert.Contains(my.ReviewEvents, e => e.Verdict == "Approved");
        Assert.Contains(my.ReviewEvents, e => e.Verdict == "Changes Requested");

        var reviewed = cards[1];
        my.StartReviewCommand.Execute(reviewed);
        Assert.False(my.IsDetailOpen);
        Assert.True(shell.Timers.IsReviewRunning);   // the timer starts ...
        Assert.Equal(reviewed.Url, launcher.Opened.Last()); // ... and the pull request page opens
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
        var timers = new TimersViewModel(f.Settings, f.History, f.Router, new NoAlarm(), new InlineDispatcher(), clock);

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

    [Fact]
    public async Task Several_task_timers_run_at_once_and_stop_buttons_pause_them()
    {
        var f = await CreateAsync();
        var timers = new TimersViewModel(f.Settings, f.History, f.Router, new NoAlarm(), new InlineDispatcher(), new ManualTime());

        timers.NewTimerName = "Write docs";
        timers.AddTaskTimerCommand.Execute(null);
        timers.NewTimerName = "Fix bug";
        timers.AddTaskTimerCommand.Execute(null);

        Assert.Equal(2, timers.MoreTimers.Count);
        Assert.True(timers.MoreTimers.All(t => t.IsRunning));
        Assert.Equal(2, timers.RunningTimers.Count);

        timers.MoreTimers[0].Line.StopCommand.Execute(null); // the stop button on the island / widget
        Assert.False(timers.MoreTimers[0].IsRunning);
        Assert.True(timers.MoreTimers[1].IsRunning);
        Assert.Single(timers.RunningTimers);

        timers.Dispose();
        await f.Monitor.DisposeAsync();
    }

    [Fact]
    public async Task Countdown_rings_saves_a_session_and_stops_at_zero()
    {
        var f = await CreateAsync();
        var clock = new ManualTime();
        var alarm = new CountingAlarm();
        var timers = new TimersViewModel(f.Settings, f.History, f.Router, alarm, new InlineDispatcher(), clock);

        timers.CountdownMinutes = 1;
        timers.AddCountdownCommand.Execute(null);
        var countdown = Assert.Single(timers.MoreTimers);
        Assert.Equal("01:00", countdown.Clock);

        clock.Advance(TimeSpan.FromSeconds(30));
        timers.Reformat(); // redraws and re-reads every timer
        Assert.Equal("00:30", countdown.Clock);
        Assert.Equal(0, alarm.Plays);

        clock.Advance(TimeSpan.FromSeconds(40));
        timers.Reformat();
        Assert.True(countdown.IsFinished);
        Assert.False(countdown.IsRunning);
        Assert.Equal(1, alarm.Plays);
        Assert.Empty(timers.RunningTimers);

        for (var i = 0; i < 50 && f.History.Sessions.Count == 0; i++) await Task.Delay(20);
        var session = Assert.Single(f.History.Sessions);
        Assert.Equal(GitDash.Domain.Timers.TimerKind.Countdown, session.Kind);
        Assert.Equal(TimeSpan.FromMinutes(1), session.Duration);

        timers.Dispose();
        await f.Monitor.DisposeAsync();
    }

    private static GitDash.Domain.PullRequests.PullRequest DeskPr(bool approved) => TestData.Pr("p1", with: p => p with
    {
        Assignees = [TestData.Me],
        Reviews = approved ? [new GitDash.Domain.PullRequests.Review(
            TestData.Me, GitDash.Domain.PullRequests.ReviewState.Approved, TestData.Now, 0)] : [],
    });

    [Fact]
    public async Task Review_timer_stops_by_itself_once_you_approve()
    {
        var f = await CreateAsync();
        var clock = new ManualTime();
        var timers = new TimersViewModel(f.Settings, f.History, f.Router, new NoAlarm(), new InlineDispatcher(), clock);
        timers.StartReviewFor(DeskPr(approved: false));
        Assert.True(timers.IsReviewRunning);
        clock.Advance(TimeSpan.FromMinutes(12));

        timers.Apply(GitDash.Application.Monitoring.MonitorState.Empty with { Open = [DeskPr(approved: true)] });

        Assert.False(timers.IsReviewRunning);
        for (var i = 0; i < 50 && f.History.Sessions.Count == 0; i++) await Task.Delay(20);
        Assert.Equal(GitDash.Domain.Timers.TimerKind.PullRequestReview, Assert.Single(f.History.Sessions).Kind);

        timers.Dispose();
        await f.Monitor.DisposeAsync();
    }

    [Fact]
    public async Task Starting_a_review_timer_on_an_approved_pull_request_asks_first_and_then_keeps_running()
    {
        var f = await CreateAsync();
        var timers = new TimersViewModel(f.Settings, f.History, f.Router, new NoAlarm(), new InlineDispatcher(), new ManualTime());
        var state = GitDash.Application.Monitoring.MonitorState.Empty with { Open = [DeskPr(approved: true)] };
        timers.Apply(state);
        timers.SelectedReviewTarget = timers.ReviewTargets.Single();

        timers.ToggleReviewCommand.Execute(null);
        Assert.True(timers.IsConfirmingApproved);
        Assert.False(timers.IsReviewRunning);

        timers.CancelApprovedStartCommand.Execute(null);
        Assert.False(timers.IsConfirmingApproved);
        Assert.False(timers.IsReviewRunning);

        timers.ToggleReviewCommand.Execute(null);
        timers.ConfirmApprovedStartCommand.Execute(null);
        Assert.True(timers.IsReviewRunning);

        timers.Apply(state); // still approved: confirmed on purpose, so it must not stop
        Assert.True(timers.IsReviewRunning);

        timers.Dispose();
        await f.Monitor.DisposeAsync();
    }

    [Fact]
    public async Task A_second_pull_request_gets_its_own_review_timer_while_the_first_is_running()
    {
        var f = await CreateAsync();
        var timers = new TimersViewModel(f.Settings, f.History, f.Router, new NoAlarm(), new InlineDispatcher(), new ManualTime());
        timers.StartReviewFor(DeskPr(approved: false));
        var other = TestData.Pr("p2", with: p => p with { Assignees = [TestData.Me] });
        timers.StartReviewFor(other);

        Assert.True(timers.IsReviewRunning);
        var extra = Assert.Single(timers.MoreTimers);
        Assert.True(extra.IsReview);
        Assert.True(extra.IsRunning);
        Assert.Equal(2, timers.RunningTimers.Count);

        timers.Dispose();
        await f.Monitor.DisposeAsync();
    }

    [Fact]
    public async Task Alarms_are_added_toggled_and_removed_in_the_settings()
    {
        var f = await CreateAsync();
        var timers = new TimersViewModel(f.Settings, f.History, f.Router, new NoAlarm(), new InlineDispatcher(), new ManualTime());

        timers.NewAlarmTime = new TimeSpan(7, 30, 0);
        timers.NewAlarmLabel = "Stand-up";
        timers.NewAlarmDaily = true;
        timers.AddAlarmCommand.Execute(null);
        for (var i = 0; i < 50 && timers.Alarms.Count == 0; i++) await Task.Delay(20);

        var alarm = Assert.Single(timers.Alarms);
        Assert.Equal("Stand-up", alarm.Label);
        Assert.Equal("Every day", alarm.Repeat);
        Assert.True(Assert.Single(f.Settings.Current.Alarms).Daily);

        alarm.IsEnabled = false;
        for (var i = 0; i < 50 && f.Settings.Current.Alarms[0].Enabled; i++) await Task.Delay(20);
        Assert.False(f.Settings.Current.Alarms[0].Enabled);

        timers.Alarms[0].RemoveCommand.Execute(null);
        for (var i = 0; i < 50 && f.Settings.Current.Alarms.Count > 0; i++) await Task.Delay(20);
        Assert.Empty(f.Settings.Current.Alarms);

        timers.Dispose();
        await f.Monitor.DisposeAsync();
    }

    [Fact]
    public async Task Changing_the_filter_always_announces_the_selected_card_so_a_single_card_is_centred()
    {
        var f = await CreateAsync();
        var my = new MyPullRequestsViewModel(f.Monitor, new NoLauncher(), new InlineDispatcher(), TimeProvider.System, _ => { });
        await f.Monitor.SyncAsync();
        my.Apply(f.Monitor.State);

        my.SelectedFilter = my.Filters.Single(x => x.Filter == PullRequestFilter.All);
        Assert.True(my.Cards.Count > 2);
        my.SelectedIndex = my.Cards.Count - 1;

        var raised = 0;
        my.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MyPullRequestsViewModel.SelectedIndex)) raised++; };
        var all = my.Cards.Count;
        my.SelectedFilter = my.Filters.Where(x => x.Filter != PullRequestFilter.All && x.Count > 0).OrderBy(x => x.Count).First();

        Assert.True(my.Cards.Count < all);
        Assert.Equal(0, my.SelectedIndex);
        Assert.True(raised > 0); // the cover flow is told, so the lone card moves to the centre

        await f.Monitor.DisposeAsync();
    }

    [Fact]
    public async Task Actions_panel_lists_runs_and_cards_get_run_counts_and_tags()
    {
        var f = await CreateAsync();
        var time = TimeProvider.System;
        var gateway = new GitDash.Infrastructure.Demo.DemoGitHubGateway(time, 1);
        var actions = new ActionsMonitor(f.Settings, f.Accounts, gateway, time);
        var vm = new ActionsViewModel(actions, new NoLauncher(), new InlineDispatcher(), time);
        var my = new MyPullRequestsViewModel(f.Monitor, new NoLauncher(), new InlineDispatcher(), time, _ => { }, vm);

        await f.Monitor.SyncAsync();
        await actions.RefreshAsync();
        my.Apply(f.Monitor.State);
        my.SelectedFilter = my.Filters.Single(x => x.Filter == PullRequestFilter.All); // the panel lists runs of the pull requests on the cards

        Assert.True(vm.HasRuns);
        Assert.Contains(vm.Runs, r => r.IsActive && r.Progress > 0 && r.Progress < 1);
        Assert.Contains(vm.Runs, r => r.Status == "Passed");
        Assert.Contains(vm.Runs, r => r.Status == "Failed");
        Assert.Equal(3, vm.ActiveCount); // one running demo run in each of the three repositories
        Assert.Equal("Actions · 3 running", vm.Header);

        // The demo runs belong to the first three pull requests of every repository.
        var card = my.Cards.First(c => c.PullRequest.Number == 90 + 2 * 31);
        Assert.NotEqual("—", card.PipelineRunsText);
        Assert.True(card.RunsActive);

        vm.ToggleCommand.Execute(null);
        Assert.True(vm.IsOpen);

        // "Only this card" narrows the panel to the pull request in front; its own tab lists the same runs.
        var all = vm.Runs.Count;
        my.SelectedIndex = my.Cards.IndexOf(card);
        vm.OnlyCurrentCard = true;
        Assert.True(vm.Runs.Count > 0 && vm.Runs.Count < all);
        Assert.Equal(vm.Runs.Count, vm.RunsFor(card.PullRequest.Repository, card.PullRequest.Number).Count);
        vm.OnlyCurrentCard = false;
        Assert.Equal(all, vm.Runs.Count);
        // A filter that leaves out the cards of those runs hides them.
        my.SelectedFilter = my.Filters.Single(x => x.Filter == PullRequestFilter.Own);
        Assert.True(vm.Runs.Count < all);

        vm.Dispose();
        await actions.DisposeAsync();
        await f.Monitor.DisposeAsync();
    }

    [Fact]
    public async Task Approve_and_hand_over_need_a_second_click_and_report_the_result()
    {
        var f = await CreateAsync();
        var my = new MyPullRequestsViewModel(f.Monitor, new NoLauncher(), new InlineDispatcher(), TimeProvider.System, _ => { });
        await f.Monitor.SyncAsync();
        my.Apply(f.Monitor.State);
        my.SelectedFilter = my.Filters.Single(x => x.Filter == PullRequestFilter.AssignedToMe);

        var card = my.Cards.First(c => c.CanApprove && c.CanHandOver);
        my.ApproveCommand.Execute(card);
        Assert.Equal("approve", card.Pending); // first click only asks
        Assert.Equal("Confirm approve", card.ApproveLabel);
        Assert.False(my.HasActionMessage);

        my.CancelPendingCommand.Execute(card);
        Assert.Null(card.Pending);

        my.ApproveCommand.Execute(card);
        my.ApproveCommand.Execute(card);
        for (var i = 0; i < 100 && !my.ActionMessage.StartsWith("Approved"); i++) await Task.Delay(20);
        Assert.Equal($"Approved {card.Number}", my.ActionMessage);
        Assert.False(my.IsActionError);

        my.HandOverCommand.Execute(card);
        my.HandOverCommand.Execute(card);
        for (var i = 0; i < 100 && !my.ActionMessage.Contains("handed"); i++) await Task.Delay(20);
        Assert.Contains("handed to " + card.Author, my.ActionMessage);

        // Assign author and remove me are separate actions, each with its own confirm click.
        var other = my.Cards.First(c => c.CanRemoveMe);
        my.RemoveMeCommand.Execute(other);
        Assert.Equal("Confirm remove me", other.RemoveMeLabel);
        my.RemoveMeCommand.Execute(other);
        for (var i = 0; i < 100 && !my.ActionMessage.Contains("removed as assignee"); i++) await Task.Delay(20);
        Assert.Contains("removed as assignee", my.ActionMessage);
        var assignable = my.Cards.FirstOrDefault(c => c.CanAssignAuthor);
        if (assignable is not null)
        {
            my.AssignAuthorCommand.Execute(assignable);
            my.AssignAuthorCommand.Execute(assignable);
            for (var i = 0; i < 100 && !my.ActionMessage.Contains("is now assigned"); i++) await Task.Delay(20);
            Assert.Contains("is now assigned", my.ActionMessage);
        }

        await f.Monitor.DisposeAsync();
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public async Task A_pull_request_url_opens_the_detail_screen_and_unknown_ones_fall_back()
    {
        var f = await CreateAsync();
        var time = TimeProvider.System;
        var my = new MyPullRequestsViewModel(f.Monitor, new NoLauncher(), new InlineDispatcher(), time, _ => { });
        await f.Monitor.SyncAsync();
        my.Apply(f.Monitor.State);

        var target = my.Cards.Last().PullRequest;
        Assert.True(my.ShowPullRequest(target.Url));
        Assert.True(my.IsDetailOpen);
        Assert.Equal(target.Url, my.DetailCard!.Url);
        Assert.False(my.ShowPullRequest("https://github.com/nobody/nothing/pull/1"));

        AvatarDirectory.Update(new Dictionary<string, string> { ["Anna-Becker"] = "https://avatars.githubusercontent.com/u/1" });
        Assert.Equal("https://avatars.githubusercontent.com/u/1", AvatarDirectory.UrlFor("anna-becker"));
        Assert.Null(AvatarDirectory.UrlFor("someone-else"));

        await f.Monitor.DisposeAsync();
    }

    [Fact]
    public void Island_rotates_pairs_when_more_than_two_counters_and_shows_all_when_expanded()
    {
        var widget = new WidgetViewModel();
        widget.Configure(
            [StatusKind.Error, StatusKind.Assigned, StatusKind.Raised, StatusKind.Approval, StatusKind.Comments],
            [StatusKind.Assigned, StatusKind.Open], rotateSeconds: 3);

        Assert.True(widget.IslandRotates);
        Assert.Equal([StatusKind.Error, StatusKind.Assigned], widget.IslandRow.Select(i => i.Kind)); // chosen order
        Assert.False(widget.TickIsland());
        Assert.False(widget.TickIsland());
        Assert.True(widget.TickIsland()); // interval reached: the next pair is due
        widget.AdvanceIslandPage();
        Assert.Equal([StatusKind.Raised, StatusKind.Approval], widget.IslandRow.Select(i => i.Kind));
        widget.AdvanceIslandPage();
        Assert.Equal([StatusKind.Comments], widget.IslandRow.Select(i => i.Kind));
        widget.AdvanceIslandPage();
        Assert.Equal(StatusKind.Error, widget.IslandRow[0].Kind); // wraps around

        widget.IslandExpanded = true;
        Assert.Equal(5, widget.IslandRow.Count);
        Assert.False(widget.TickIsland()); // no rotation while everything is shown

        widget.Configure([StatusKind.Approved], [StatusKind.Assigned], 3);
        Assert.False(widget.IslandRotates);
        Assert.Single(widget.IslandRow);
        Assert.Equal(1, widget.WidgetColumns);
    }

    [Fact]
    public async Task Counter_choice_and_order_are_saved_in_settings()
    {
        var f = await CreateAsync();
        var settings = new SettingsViewModel(f.Settings, f.Accounts, f.Router, new NoLauncher(), new InlineDispatcher());

        var island = settings.IslandLayout;
        Assert.Equal([StatusKind.Assigned, StatusKind.Approval], island.Items.Where(i => i.IsSelected).Select(i => i.Kind));

        island.Items.Single(i => i.Kind == StatusKind.Error).IsSelected = true;
        island.Items.Single(i => i.Kind == StatusKind.Error).MoveUpCommand!.Execute(null); // up from the first unselected slot
        await Task.Delay(50);

        var saved = f.Settings.Current.IslandStatuses;
        Assert.True(saved.Contains(StatusKind.Error));
        Assert.Equal(saved.Count, island.Items.Count(i => i.IsSelected));
        Assert.Equal(saved, island.Items.Where(i => i.IsSelected).Select(i => i.Kind));

        island.Items[0].MoveDownCommand!.Execute(null);
        await Task.Delay(50);
        Assert.Equal(island.Items.Where(i => i.IsSelected).Select(i => i.Kind), f.Settings.Current.IslandStatuses);

        settings.StatusRotateSeconds = 9;
        await Task.Delay(50);
        Assert.Equal(9, f.Settings.Current.StatusRotateSeconds);
        await f.Monitor.DisposeAsync();
    }
}
