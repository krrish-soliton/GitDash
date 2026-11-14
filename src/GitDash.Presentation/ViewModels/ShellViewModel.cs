using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using GitDash.Application.Abstractions;
using GitDash.Application.Accounts;
using GitDash.Application.Monitoring;
using GitDash.Application.Settings;
using GitDash.Application.Timers;
using GitDash.Domain.Insights;
using GitDash.Presentation.Mvvm;

namespace GitDash.Presentation.ViewModels;

/// <summary>The three numbers shown by the floating widget and the tray icon.</summary>
/// <summary>One counter on the island or the widget.</summary>
public sealed record StatusItemViewModel(StatusKind Kind, string Label, int Count, string Accent, double ValueSize)
{
    public string Caption => Label.ToUpperInvariant();
}

public sealed class WidgetViewModel : ObservableObject
{
    private const int IslandPageSize = 2;
    private readonly Dictionary<StatusKind, int> _counts = [];
    private IReadOnlyList<StatusKind> _islandKinds = [StatusKind.Assigned, StatusKind.Approval];
    private IReadOnlyList<StatusKind> _widgetKinds = [StatusKind.Assigned, StatusKind.Approval, StatusKind.Open];
    private IReadOnlyList<StatusItemViewModel> _islandItems = [], _islandRow = [], _widgetItems = [];
    private int _page, _rotateSeconds = 4, _secondsOnPage;
    private bool _islandExpanded;
    private string _islandSignature = string.Empty, _widgetSignature = string.Empty;

    /// <summary>Every counter the island is set to show, in the chosen order.</summary>
    public IReadOnlyList<StatusItemViewModel> IslandItems { get => _islandItems; private set => Set(ref _islandItems, value); }

    /// <summary>What the island's compact row shows right now: one page of two while it rotates, everything while expanded.</summary>
    public IReadOnlyList<StatusItemViewModel> IslandRow { get => _islandRow; private set => Set(ref _islandRow, value); }

    /// <summary>The counters on the floating widget's card.</summary>
    public IReadOnlyList<StatusItemViewModel> WidgetItems { get => _widgetItems; private set => Set(ref _widgetItems, value); }
    public int WidgetColumns => Math.Clamp(_widgetItems.Count, 1, 4);

    /// <summary>More than two counters: the compact island row rotates through them.</summary>
    public bool IslandRotates => _islandItems.Count > IslandPageSize;

    /// <summary>The island is hovered open: show every counter at once.</summary>
    public bool IslandExpanded
    {
        get => _islandExpanded;
        set { if (Set(ref _islandExpanded, value)) RebuildIslandRow(); }
    }

    /// <summary>Applies the user's choice of counters, their order and the rotation interval.</summary>
    public void Configure(IReadOnlyList<StatusKind> island, IReadOnlyList<StatusKind> widget, int rotateSeconds)
    {
        var seconds = Math.Clamp(rotateSeconds, 2, 60);
        if (island.SequenceEqual(_islandKinds) && widget.SequenceEqual(_widgetKinds) && seconds == _rotateSeconds) return;
        _islandKinds = island;
        _widgetKinds = widget;
        _rotateSeconds = seconds;
        _page = 0;
        _secondsOnPage = 0;
        RebuildItems();
    }

    /// <summary>Called once a second by the island. True when the interval has passed and the next pair is due.</summary>
    public bool TickIsland()
    {
        if (!IslandRotates || _islandExpanded) return false;
        if (++_secondsOnPage < _rotateSeconds) return false;
        _secondsOnPage = 0;
        return true;
    }

    /// <summary>Shows the next pair of counters (wrapping around). True when the pair changed.</summary>
    public bool AdvanceIslandPage()
    {
        if (!IslandRotates) return false;
        var pages = (int)Math.Ceiling(_islandItems.Count / (double)IslandPageSize);
        _page = (_page + 1) % pages;
        RebuildIslandRow();
        return true;
    }

    private void RebuildItems()
    {
        var island = _islandKinds.Distinct().Select(k => Item(k, 20)).ToList();
        var dense = _widgetKinds.Distinct().Count() > 4;
        var widget = _widgetKinds.Distinct().Select(k => Item(k, dense ? 18 : _widgetKinds.Count > 3 ? 24 : 30)).ToList();

        var islandSig = string.Join('|', island.Select(i => $"{i.Kind}:{i.Count}"));
        if (islandSig != _islandSignature)
        {
            _islandSignature = islandSig;
            IslandItems = island;
            Raise(nameof(IslandRotates));
            var pages = Math.Max(1, (int)Math.Ceiling(island.Count / (double)IslandPageSize));
            if (_page >= pages) _page = 0;
            RebuildIslandRow();
        }

        var widgetSig = string.Join('|', widget.Select(i => $"{i.Kind}:{i.Count}:{i.ValueSize}"));
        if (widgetSig != _widgetSignature)
        {
            _widgetSignature = widgetSig;
            WidgetItems = widget;
            Raise(nameof(WidgetColumns));
        }
    }

    private StatusItemViewModel Item(StatusKind kind, double size) =>
        new(kind, StatusCounter.Label(kind), _counts.GetValueOrDefault(kind), StatusCounter.Accent(kind), size);

    private void RebuildIslandRow() =>
        IslandRow = _islandExpanded || !IslandRotates
            ? _islandItems
            : [.. _islandItems.Skip(_page * IslandPageSize).Take(IslandPageSize)];

    private int _assigned, _approval, _total;
    private DateTimeOffset? _oldestSince, _oldestReviewSince;
    private string _oldestAge = "none waiting", _oldestReviewAge = "none waiting";
    private string _oldestTitle = string.Empty, _oldestReviewTitle = string.Empty;

    public int Assigned { get => _assigned; private set => Set(ref _assigned, value); }
    public int Approval { get => _approval; private set => Set(ref _approval, value); }
    public int Total { get => _total; private set => Set(ref _total, value); }

    private int _unseen;
    private string _lastNotification = string.Empty;

    /// <summary>Notifications that arrived since the widget was last looked at (the bubble on the circle).</summary>
    public int Unseen { get => _unseen; private set { if (Set(ref _unseen, value)) Raise(nameof(HasUnseen)); } }
    public bool HasUnseen => _unseen > 0;
    public string LastNotification { get => _lastNotification; private set => Set(ref _lastNotification, value); }

    public void NotificationArrived(string title)
    {
        LastNotification = title;
        Unseen++;
    }

    public void MarkSeen() => Unseen = 0;

    /// <summary>Longest time any PR assigned to me has been waiting, e.g. "2d 4h".</summary>
    public string OldestAge { get => _oldestAge; private set => Set(ref _oldestAge, value); }
    public string OldestTitle { get => _oldestTitle; private set => Set(ref _oldestTitle, value); }
    public bool HasOldest => _oldestSince is not null;

    /// <summary>Longest time any PR waiting for my approval has been waiting.</summary>
    public string OldestReviewAge { get => _oldestReviewAge; private set => Set(ref _oldestReviewAge, value); }
    public string OldestReviewTitle { get => _oldestReviewTitle; private set => Set(ref _oldestReviewTitle, value); }
    public bool HasOldestReview => _oldestReviewSince is not null;

    public void Apply(MonitorState state, DateTimeOffset now)
    {
        Assigned = state.Summary.AssignedToMe;
        Approval = state.Summary.NeedsMyApproval;
        Total = state.Summary.TotalOpen;
        foreach (var kind in StatusCounter.All) _counts[kind] = StatusCounter.Count(kind, state.Open);
        RebuildItems();

        var assigned = state.Open.Where(p => p.IsAssignedToViewer)
            .OrderBy(p => p.AssignedToViewerAt ?? p.CreatedAt)
            .FirstOrDefault();
        _oldestSince = assigned is null ? null : assigned.AssignedToViewerAt ?? assigned.CreatedAt;
        OldestTitle = assigned is null ? string.Empty : $"#{assigned.Number} {assigned.Title}";

        var review = state.Open.Where(p => p.NeedsViewerApproval)
            .OrderBy(p => p.ReviewRequestedFromViewerAt ?? p.CreatedAt)
            .FirstOrDefault();
        _oldestReviewSince = review is null ? null : review.ReviewRequestedFromViewerAt ?? review.CreatedAt;
        OldestReviewTitle = review is null ? string.Empty : $"#{review.Number} {review.Title}";

        Raise(nameof(HasOldest));
        Raise(nameof(HasOldestReview));
        Tick(now);
    }

    /// <summary>Re-formats both waits; called once a second while the widget is visible.</summary>
    public void Tick(DateTimeOffset now)
    {
        OldestAge = _oldestSince is { } a ? Humanize.Age(Clamp(now - a)) : "none waiting";
        OldestReviewAge = _oldestReviewSince is { } r ? Humanize.Age(Clamp(now - r)) : "none waiting";
    }

    private static TimeSpan Clamp(TimeSpan span) => span < TimeSpan.Zero ? TimeSpan.Zero : span;
}

/// <summary>Main window: navigation, sync status and the pages.</summary>
public sealed class ShellViewModel : ObservableObject, IDisposable
{
    private readonly PullRequestMonitor _monitor;
    private readonly SettingsService _settings;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private PageViewModel _currentPage;
    private readonly IUrlLauncher _launcher;
    private MonitorState _state = MonitorState.Empty;
    private int _updateQueued;
    private int _renderedRevision = -1;
    private bool _isSyncing, _hasError, _reduceMotion;
    private string _status = "Starting…", _error = string.Empty;

    public ShellViewModel(
        PullRequestMonitor monitor, SettingsService settings, AccountService accounts, TimerHistory history,
        NotificationRouter notifications, PersonActivityService personActivity, IUrlLauncher launcher, IAlarm alarm, IUiDispatcher ui, TimeProvider time,
        ActionsMonitor? actionsMonitor = null)
    {
        _time = time;
        _launcher = launcher;
        _monitor = monitor;
        _settings = settings;
        _ui = ui;
        _reduceMotion = settings.Current.ReduceMotion;
        Humanize.Format = settings.Current.TimeFormat;

        AiTools = new AiToolsViewModel(settings, notifications, alarm, ui, time);
        Settings = new SettingsViewModel(settings, accounts, notifications, launcher, ui) { AiTools = AiTools, Alarm = alarm };
        Home = new HomeViewModel(settings, accounts, launcher, ui, time);
        Timers = new TimersViewModel(settings, history, notifications, alarm, ui, time);
        Dashboard = new DashboardViewModel(launcher, time, () => Navigate(Settings));
        Dashboard.AttachTimers(Timers);
        People = new PeopleViewModel(launcher, time, personActivity);
        // "Review timer" on a card: start timing, then open the pull request on GitHub.
        Actions = actionsMonitor is null ? null : new ActionsViewModel(actionsMonitor, launcher, ui, time);
        MyPullRequests = new MyPullRequestsViewModel(monitor, launcher, ui, time, pr =>
        {
            Timers.StartReviewFor(pr);
            launcher.Open(pr.Url);
        }, Actions);
        Timers.Ticked += () => MyPullRequests.UpdateReviewClocks(Timers.ReviewClockFor);

        _selectedRepositories = new HashSet<string>(settings.Current.SelectedRepositories, StringComparer.OrdinalIgnoreCase);
        RefreshRepositoryOptions(MonitorState.Empty);
        About = new AboutViewModel(launcher);
        Home.PullRequestOpener = OpenPullRequest;
        Dashboard.PullRequestOpener = OpenPullRequest;
        People.PullRequestOpener = OpenPullRequest;
        Pages = [Home, Dashboard, People, MyPullRequests, Timers, Settings, About];
        _currentPage = Home;
        Home.IsSelected = true;

        NavigateCommand = new RelayCommand<PageViewModel>(Navigate);
        RefreshCommand = new RelayCommand(monitor.RequestRefresh);

        notifications.Published += request => ui.Post(() =>
        {
            Widget.NotificationArrived(request.Title);
            // Timers and alarms ring on their own; everything else follows the "sound with notifications" setting.
            if (request.Tone != ToastTone.Timer && settings.Current.SoundOnNotifications) alarm.Play();
        });
        _monitor.StateChanged += OnStateChanged;
        Widget.Configure(settings.Current.IslandStatuses, settings.Current.WidgetStatuses, settings.Current.StatusRotateSeconds);
        _settings.Changed += OnSettingsChanged;
    }

    public HomeViewModel Home { get; }
    public AiToolsViewModel AiTools { get; }
    public DashboardViewModel Dashboard { get; }
    public PeopleViewModel People { get; }
    public MyPullRequestsViewModel MyPullRequests { get; }
    public TimersViewModel Timers { get; }
    public ActionsViewModel? Actions { get; }
    public SettingsViewModel Settings { get; }
    public AboutViewModel About { get; }
    public WidgetViewModel Widget { get; } = new();
    public IReadOnlyList<PageViewModel> Pages { get; }

    public PageViewModel CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (Set(ref _currentPage, value))
            {
                Raise(nameof(SelectedPage));
                Raise(nameof(ShowRepositoryFilter));
            }
        }
    }

    // ----- Repository filter: any number of repositories, one choice that every list page follows -----
    public const string AllRepositories = "All repositories";

    /// <summary>"All repositories" followed by every repository that has open pull requests or is monitored.</summary>
    public ObservableCollection<RepositoryOptionViewModel> RepositoryOptions { get; } = [];

    /// <summary>The chosen repositories (owner/name). Empty means all of them, which is the default.</summary>
    private HashSet<string> _selectedRepositories = new(StringComparer.OrdinalIgnoreCase);

    private string _repositorySummary = AllRepositories;
    private bool _updatingOptions;

    /// <summary>Text on the filter button: "All repositories", the repository name, or "3 repositories".</summary>
    public string RepositorySummary { get => _repositorySummary; private set => Set(ref _repositorySummary, value); }

    /// <summary>The filter is shown on the pages that list pull requests.</summary>
    public bool ShowRepositoryFilter => CurrentPage is not (TimersViewModel or SettingsViewModel or AboutViewModel);

    private bool IsSelected(string repository) =>
        _selectedRepositories.Count == 0 || _selectedRepositories.Contains(repository);

    private MonitorState Filtered(MonitorState state)
    {
        if (_selectedRepositories.Count == 0) return state;
        bool Match(GitDash.Domain.PullRequests.PullRequest pr) => IsSelected(pr.Repository.FullName);

        var open = state.Open.Where(Match).ToList();
        return state with
        {
            Open = open,
            Summary = GitDash.Domain.Insights.DashboardSummary.From(open),
            RecentActivity = [.. state.RecentActivity.Where(n => Match(n.PullRequest))],
        };
    }

    /// <summary>
    /// Called when a checkbox in the filter changes. Ticking "All repositories" clears the individual choices;
    /// ticking a repository un-ticks "All"; clearing the last repository goes back to "All".
    /// </summary>
    private void OnRepositoryOptionToggled(RepositoryOptionViewModel option)
    {
        if (_updatingOptions) return;
        if (option.IsAll)
        {
            _selectedRepositories.Clear();
        }
        else if (option.IsSelected)
        {
            _selectedRepositories.Add(option.Name);
        }
        else
        {
            _selectedRepositories.Remove(option.Name);
        }

        SyncOptionChecks();
        SelectionChanged();
    }

    private void SyncOptionChecks()
    {
        _updatingOptions = true;
        try
        {
            foreach (var o in RepositoryOptions)
                o.SetChecked(o.IsAll ? _selectedRepositories.Count == 0 : _selectedRepositories.Contains(o.Name));
        }
        finally
        {
            _updatingOptions = false;
        }

        RepositorySummary = _selectedRepositories.Count switch
        {
            0 => AllRepositories,
            1 => _selectedRepositories.First(),
            var n => $"{n} repositories",
        };
    }

    private void SelectionChanged()
    {
        var keep = _selectedRepositories.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        _ = _settings.UpdateAsync(x => x with { SelectedRepositories = keep });
        if (!ReferenceEquals(CurrentPage, Timers)) CurrentPage.Apply(Filtered(_state));
    }

    private void RefreshRepositoryOptions(MonitorState state)
    {
        var names = state.Open.Select(p => p.Repository.FullName)
            .Concat(_settings.Current.Repositories.Select(r => $"{r.Owner}/{r.Name}"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // A repository that is no longer monitored drops out of the selection (without touching the saved choice).
        if (names.Count > 0) _selectedRepositories.RemoveWhere(r => !names.Contains(r, StringComparer.OrdinalIgnoreCase));

        var wanted = new List<string> { AllRepositories };
        wanted.AddRange(names);
        if (!RepositoryOptions.Select(o => o.Name).SequenceEqual(wanted))
        {
            RepositoryOptions.Clear();
            foreach (var name in wanted)
                RepositoryOptions.Add(new RepositoryOptionViewModel(name, name == AllRepositories, OnRepositoryOptionToggled));
        }

        SyncOptionChecks();
    }

    /// <summary>Two-way friendly alias of <see cref="CurrentPage"/> for the navigation list.</summary>
    public PageViewModel? SelectedPage
    {
        get => _currentPage;
        set
        {
            if (value is not null) Navigate(value);
        }
    }
    public bool IsSyncing { get => _isSyncing; private set => Set(ref _isSyncing, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Error { get => _error; private set => Set(ref _error, value); }
    public bool HasError { get => _hasError; private set => Set(ref _hasError, value); }
    public bool ReduceMotion { get => _reduceMotion; private set => Set(ref _reduceMotion, value); }

    /// <summary>True: the sidebar stays open. False: it shows only icons and opens while hovered.</summary>
    public bool SidebarPinned
    {
        get => _settings.Current.SidebarPinned;
        set
        {
            if (_settings.Current.SidebarPinned == value) return;
            _ = _settings.UpdateAsync(s => s with { SidebarPinned = value });
        }
    }

    public ICommand NavigateCommand { get; }
    public ICommand RefreshCommand { get; }

    /// <summary>Called once a second by the floating widget while it is on screen.</summary>
    public void TickWidget() => Widget.Tick(_time.GetUtcNow());

    /// <summary>
    /// Shows a pull request in the detailed review screen of the Pull requests page. Only when it is not part of the
    /// loaded list (for example another team's repository) does it fall back to the browser.
    /// </summary>
    public void OpenPullRequest(string url)
    {
        if (string.IsNullOrEmpty(url)) return;
        Navigate(MyPullRequests);
        if (!MyPullRequests.ShowPullRequest(url)) _launcher.Open(url);
    }

    public void Navigate(PageViewModel page)
    {
        if (ReferenceEquals(page, CurrentPage)) return;
        CurrentPage.IsSelected = false;
        page.IsSelected = true;
        page.Apply(Filtered(_state)); // bring the page up to date before it becomes visible
        CurrentPage = page;
    }

    private void OnSettingsChanged(AppSettings settings) => _ui.Post(() =>
    {
        ReduceMotion = settings.ReduceMotion;
        Widget.Configure(settings.IslandStatuses, settings.WidgetStatuses, settings.StatusRotateSeconds);
        Raise(nameof(SidebarPinned));
        if (Humanize.Format != settings.TimeFormat)
        {
            // Times are turned into text when a page is built, so rebuild them in the new style.
            Humanize.Format = settings.TimeFormat;
            Timers.Reformat();
            _renderedRevision = -1;
            Render(_state);
        }
    });

    private void OnStateChanged(MonitorState state)
    {
        // Several updates can arrive in a burst; only the newest one needs to reach the UI.
        _state = state;
        AvatarDirectory.Update(state.Avatars);
        if (Interlocked.Exchange(ref _updateQueued, 1) == 1) return;
        _ui.Post(() =>
        {
            Interlocked.Exchange(ref _updateQueued, 0);
            Render(_state);
        });
    }

    private void Render(MonitorState state)
    {
        IsSyncing = state.IsSyncing;
        HasError = state.Errors.Count > 0;
        Error = string.Join(Environment.NewLine, state.Errors.Take(3));
        Status = state switch
        {
            { LastCheckedAt: { } at } =>
                $"Live · {Humanize.Time(at, seconds: true)} · " +
                $"{state.RepositoryCount} {(state.RepositoryCount == 1 ? "repo" : "repos")}",
            { IsSyncing: true } => "Connecting…",
            { LastSyncedAt: { } saved } =>
                $"Saved data from {Humanize.Time(saved)} · updating…",
            _ => "Waiting for first sync",
        };

        // Heartbeats arrive every few seconds; pages are only touched when the data really changed.
        if (state.Revision == _renderedRevision) return;
        _renderedRevision = state.Revision;

        Widget.Apply(state, _time.GetUtcNow());
        Timers.Apply(state); // keeps the review picker current even when the page is hidden (cheap)
        RefreshRepositoryOptions(state);
        if (!ReferenceEquals(CurrentPage, Timers)) CurrentPage.Apply(Filtered(state));
    }

    public void Dispose()
    {
        _monitor.StateChanged -= OnStateChanged;
        _settings.Changed -= OnSettingsChanged;
        Timers.Dispose();
        Actions?.Dispose();
        AiTools.Dispose();
    }
}
