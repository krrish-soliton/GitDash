using System.Collections.ObjectModel;
using System.Windows.Input;
using GitDash.Application.Abstractions;
using GitDash.Application.Monitoring;
using System.Globalization;
using GitDash.Domain.Insights;
using GitDash.Domain.PullRequests;
using GitDash.Presentation.Mvvm;

namespace GitDash.Presentation.ViewModels;

/// <summary>Remembers who has already walked into the office, so the entrance is only played once per person.</summary>
public sealed class EntranceRegistry
{
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True the first time a key is seen, false from then on.</summary>
    public bool TryEnter(string key) => _seen.Add(key);
}

/// <summary>One book lying in the pile on a desk: a hex colour for the type of PR waiting and a width in pixels.</summary>
public sealed record DeskBookItem(string Color, double Width);

/// <summary>One animated person standing on a floor.</summary>
public sealed record PersonViewModel(
    string Login, string Repository, string Initials, string Accent, string? AvatarUrl,
    int Raised, int Assigned, int AssignedUnder, string OldestAssigned, string OldestReview, bool IsViewer, int Variant)
{
    /// <summary>Shared by every person so records still compare equal; null in tests.</summary>
    public EntranceRegistry? Entrance { get; init; }

    /// <summary>True only the first time this person is drawn in this session: they walk in. Afterwards they are just there.</summary>
    public bool ConsumeEntrance() => Entrance?.TryEnter($"{Repository}|{Login}") ?? false;

    public bool HasRaised => Raised > 0;
    public bool HasAssigned => Assigned > 0;
    public bool HasWaiting => AssignedUnder > 0;
    public bool HasOldestAssigned => OldestAssigned.Length > 0;
    public bool HasOldestReview => OldestReview.Length > 0;
    /// <summary>Sits at the desk and types; people with nothing on their plate stand around instead.</summary>
    public bool IsWorking => Raised > 0 || Assigned > 0 || AssignedUnder > 0;
    public bool IsIdle => !IsWorking;
    /// <summary>Open pull requests where this person's review is requested.</summary>
    public int ToReview { get; init; }

    /// <summary>
    /// Books piled on the desk from top to bottom, one per waiting item and coloured by type: Assigned cyan, Review Requested amber,
    /// PR Raised violet. At most <see cref="MaxDeskBooks"/> are drawn; every type present keeps at least one.
    /// </summary>
    public IReadOnlyList<DeskBookItem> DeskBooks => BuildDeskBooks(Assigned, ToReview, Raised);

    public const int MaxDeskBooks = 7;
    public const string AssignedBookColor = "#22D3EE", ReviewBookColor = "#F59E0B", RaisedBookColor = "#A78BFA";
    private static readonly int[] BookWidths = [30, 26, 32, 28, 24, 31, 27];

    public static IReadOnlyList<DeskBookItem> BuildDeskBooks(int assigned, int toReview, int raised)
    {
        var counts = new[] { assigned, toReview, raised };
        var colors = new[] { AssignedBookColor, ReviewBookColor, RaisedBookColor };
        var take = new int[3];
        var left = MaxDeskBooks;
        while (left > 0)
        {
            var progressed = false;
            for (var t = 0; t < 3 && left > 0; t++)
            {
                if (take[t] >= counts[t]) continue;
                take[t]++; left--; progressed = true;
            }

            if (!progressed) break;
        }

        var books = new List<DeskBookItem>();
        for (var t = 0; t < 3; t++)
            for (var i = 0; i < take[t]; i++)
                books.Add(new DeskBookItem(colors[t], BookWidths[books.Count % BookWidths.Length]));
        return books;
    }

    public bool IsBusy => Raised + Assigned >= 3;
    /// <summary>Assigned first, then PR, then Waiting.</summary>
    public string StatusLine => $"Assigned {Assigned} · PR {Raised} · Waiting {AssignedUnder}";
    public string OldestAssignedLine => HasOldestAssigned ? $"Oldest Assigned {OldestAssigned}" : string.Empty;
    public string OldestReviewLine => HasOldestReview ? $"Oldest Review {OldestReview}" : string.Empty;
    public string Hint => string.Join(" · ", new[] { StatusLine, OldestAssignedLine, OldestReviewLine }.Where(x => x.Length > 0));
    public string DisplayName => IsViewer ? $"{Login} (you)" : Login;
}

/// <summary>One repository, drawn as one floor of the building.</summary>
public sealed record FloorViewModel(
    string Repository,
    string FloorLabel,
    IReadOnlyList<PersonViewModel> People,
    int OpenCount,
    int DraftCount,
    int AwaitingReviewCount,
    int ApprovedCount,
    int ChangesRequestedCount,
    string AverageAge,
    string OldestAssignedAge,
    string OldestReviewAge)
{
    // Records compare lists by reference; compare people by value so an unchanged floor is not redrawn.
    public bool Equals(FloorViewModel? other) =>
        other is not null && Repository == other.Repository && FloorLabel == other.FloorLabel &&
        OpenCount == other.OpenCount && DraftCount == other.DraftCount &&
        AwaitingReviewCount == other.AwaitingReviewCount && ApprovedCount == other.ApprovedCount &&
        ChangesRequestedCount == other.ChangesRequestedCount && AverageAge == other.AverageAge &&
        OldestAssignedAge == other.OldestAssignedAge && OldestReviewAge == other.OldestReviewAge && People.SequenceEqual(other.People);

    public override int GetHashCode() => HashCode.Combine(Repository, FloorLabel, OpenCount);
}

/// <summary>A pull request row in the person panel.</summary>
public sealed record PullRequestLinkViewModel(string Title, string Subtitle, string Url);

public sealed record PersonTileViewModel(string Label, string Value, string Tone);

/// <summary>A branch chip: green with a pull request, yellow without one, red without one for over two weeks.</summary>
public sealed record PendingBranchViewModel(string Name, string Tone, string Tip)
{
    public static PendingBranchViewModel From(PendingBranch branch, DateTimeOffset now)
    {
        var age = branch.LastCommitAt is { } at ? $"last commit {Humanize.Age(now - at)} ago" : "last commit unknown";
        return branch.HealthAt(now) switch
        {
            PendingBranchHealth.HasPullRequest => new(branch.Name, "Green", $"Has an open pull request · {age}"),
            PendingBranchHealth.Stale => new(branch.Name, "Red", $"No pull request for over two weeks · {age}"),
            _ => new(branch.Name, "Amber", $"No pull request yet · {age}"),
        };
    }
}

public sealed record PersonEventViewModel(string When, string Kind, string Title, string Text, string Url);

public sealed record PersonPullRequestViewModel(string Title, string Meta, string State, string Url);

public sealed class PeopleViewModel : PageViewModel
{
    private const int MaxPeoplePerFloor = 14;
    private readonly TimeProvider _time;
    private readonly EntranceRegistry _entrance = new();
    private MonitorState _state = MonitorState.Empty;
    private PersonViewModel? _selectedPerson;
    private int _peopleCount, _openCount;
    private bool _isEmpty = true, _isPersonOpen, _hasRaisedItems, _hasWaitingItems;
    private string _personSubtitle = string.Empty;
    private readonly PersonActivityService? _activityService;
    private CancellationTokenSource? _activityCts;
    private bool _isActivityLoading, _hasActivity, _hasPendingBranches, _hasEvents, _hasRecentPrs;
    private string _activityError = string.Empty;

    public PeopleViewModel(IUrlLauncher launcher, TimeProvider time, PersonActivityService? activityService = null)
        : base("People", "IconPeople")
    {
        _time = time;
        _activityService = activityService;
        RefreshActivityCommand = new RelayCommand(() => _ = LoadActivityAsync(forceRefresh: true));
        SelectPersonCommand = new RelayCommand<PersonViewModel>(Select);
        ClosePersonCommand = new RelayCommand(() => IsPersonOpen = false);
        OpenUrlCommand = new RelayCommand<string>(url => launcher.Open(url));
    }

    public ObservableCollection<FloorViewModel> Floors { get; } = [];

    public int PeopleCount { get => _peopleCount; private set => Set(ref _peopleCount, value); }
    public int OpenCount { get => _openCount; private set => Set(ref _openCount, value); }
    public bool IsEmpty { get => _isEmpty; private set => Set(ref _isEmpty, value); }

    // ----- Person panel: opened by clicking a figure -----
    public PersonViewModel? SelectedPerson { get => _selectedPerson; private set => Set(ref _selectedPerson, value); }
    public bool IsPersonOpen { get => _isPersonOpen; private set => Set(ref _isPersonOpen, value); }
    public string PersonSubtitle { get => _personSubtitle; private set => Set(ref _personSubtitle, value); }
    public ObservableCollection<PullRequestLinkViewModel> RaisedItems { get; } = [];
    public ObservableCollection<PullRequestLinkViewModel> WaitingItems { get; } = [];
    public bool HasRaisedItems { get => _hasRaisedItems; private set => Set(ref _hasRaisedItems, value); }
    public bool HasWaitingItems { get => _hasWaitingItems; private set => Set(ref _hasWaitingItems, value); }

    // ----- Person dashboard (loaded from GitHub when a person is opened) -----
    public ObservableCollection<PersonTileViewModel> ActivityTiles { get; } = [];

    /// <summary>The single small card: merge rate and pipeline runs this week, this month and in total.</summary>
    public ObservableCollection<PersonTileViewModel> SummaryStats { get; } = [];
    public ObservableCollection<PersonPullRequestViewModel> RecentPullRequests { get; } = [];
    public ObservableCollection<PersonEventViewModel> ActivityEvents { get; } = [];
    public ObservableCollection<PendingBranchViewModel> PendingBranches { get; } = [];

    /// <summary>Live counts for the open person: waiting on them, assigned to them, raised by them.</summary>
    public ObservableCollection<PersonTileViewModel> LiveTiles { get; } = [];
    public bool IsActivityLoading { get => _isActivityLoading; private set => Set(ref _isActivityLoading, value); }
    public bool HasActivity { get => _hasActivity; private set => Set(ref _hasActivity, value); }
    public bool HasPendingBranches { get => _hasPendingBranches; private set => Set(ref _hasPendingBranches, value); }
    public bool HasEvents { get => _hasEvents; private set => Set(ref _hasEvents, value); }
    public bool HasRecentPrs { get => _hasRecentPrs; private set => Set(ref _hasRecentPrs, value); }
    public string ActivityError { get => _activityError; private set => Set(ref _activityError, value); }
    public bool HasActivityError => ActivityError.Length > 0;
    public ICommand RefreshActivityCommand { get; }

    public ICommand SelectPersonCommand { get; }
    public ICommand ClosePersonCommand { get; }
    public ICommand OpenUrlCommand { get; }

    public override void Apply(MonitorState state)
    {
        _state = state;
        var floors = PeopleInsights.Build(state.Open, _time.GetUtcNow());
        var viewers = state.Open.Select(p => p.ViewerLogin).ToHashSet(Login.Comparer);

        // Top of the list is the top floor, like a building seen from the street.
        var models = floors
            .Select((floor, i) => ToViewModel(floor, i + 1, viewers, state.Avatars))
            .Reverse();
        DashboardViewModel.Replace(Floors, models);

        PeopleCount = floors.SelectMany(f => f.People).Select(p => p.Login).Distinct(Login.Comparer).Count();
        OpenCount = floors.Sum(f => f.OpenCount);
        IsEmpty = floors.Count == 0;

        if (IsPersonOpen && SelectedPerson is { } person) FillPersonPanel(person); // keep the open panel live
    }

    private void Select(PersonViewModel person)
    {
        SelectedPerson = person;
        FillPersonPanel(person);
        IsPersonOpen = true;
        ClearActivity();
        _ = LoadActivityAsync(forceRefresh: false);
    }

    private void ClearActivity()
    {
        HasActivity = false;
        ActivityError = string.Empty;
        Raise(nameof(HasActivityError));
        ActivityTiles.Clear();
        SummaryStats.Clear();
        RecentPullRequests.Clear();
        ActivityEvents.Clear();
        PendingBranches.Clear();
        HasPendingBranches = HasEvents = HasRecentPrs = false;
    }

    private async Task LoadActivityAsync(bool forceRefresh)
    {
        if (_activityService is null || SelectedPerson is not { } person) return;

        _activityCts?.Cancel();
        var cts = _activityCts = new CancellationTokenSource();
        IsActivityLoading = true;
        ActivityError = string.Empty;
        Raise(nameof(HasActivityError));
        try
        {
            var activity = await Task.Run(
                () => _activityService.LoadAsync(person.Repository, person.Login, forceRefresh, cts.Token), cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(person, SelectedPerson)) return;
            ShowActivity(activity);
        }
        catch (OperationCanceledException)
        {
            // A newer selection took over.
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException
                                       or GitHubApiException or GitHubAuthorizationException or GitHubRateLimitException)
        {
            if (!cts.IsCancellationRequested)
            {
                ActivityError = "Could not load the activity: " + ex.Message;
                Raise(nameof(HasActivityError));
            }
        }
        finally
        {
            if (ReferenceEquals(_activityCts, cts)) IsActivityLoading = false;
        }
    }

    private void ShowActivity(PersonActivity a)
    {
        var inv = CultureInfo.InvariantCulture;
        var now = _time.GetUtcNow();
        DashboardViewModel.Replace(ActivityTiles,
        [
            new("Pull requests raised", a.Raised.ToString("N0", inv), "Violet"),
            new("Open now", a.Open.ToString("N0", inv), "Cyan"),
            new("Merged", a.Merged.ToString("N0", inv), "Green"),
            new("Closed unmerged", a.Closed.ToString("N0", inv), "Red"),
            new("Drafts", a.Draft.ToString("N0", inv), "Muted"),
            new("PRs commented on", a.PullRequestsCommentedOn.ToString("N0", inv), "Cyan"),
            new("PRs reviewed", a.PullRequestsReviewed.ToString("N0", inv), "Amber"),
            new("Comments (recent)", a.CommentsInRecent.ToString("N0", inv), "Cyan"),
            new("Pending branches", a.PendingBranches.Count.ToString("N0", inv), a.PendingBranches.Count > 0 ? "Amber" : "Muted"),
            new("Branches without PR > 2 weeks", a.PendingBranches.Count(b => b.HealthAt(now) == PendingBranchHealth.Stale).ToString("N0", inv),
                a.PendingBranches.Any(b => b.HealthAt(now) == PendingBranchHealth.Stale) ? "Red" : "Muted"),
        ]);
        static string Runs(int? n) => n is { } v ? v.ToString("N0", CultureInfo.InvariantCulture) : "–";
        DashboardViewModel.Replace(SummaryStats,
        [
            new("Merge rate", a.Merged + a.Closed == 0 ? "–" : a.MergeRate.ToString("P0", inv), "Green"),
            new("Runs this week", Runs(a.PipelineRuns.Week), "Cyan"),
            new("Runs this month", Runs(a.PipelineRuns.Month), "Violet"),
            new("Total runs", Runs(a.PipelineRuns.Total), "Amber"),
        ]);
        DashboardViewModel.Replace(PendingBranches, a.PendingBranches.Select(b => PendingBranchViewModel.From(b, now)));
        HasPendingBranches = PendingBranches.Count > 0;
        DashboardViewModel.Replace(RecentPullRequests, a.RecentPullRequests.Select(p => new PersonPullRequestViewModel(
            p.Title,
            $"#{p.Number} · {(p.ClosedAt is { } closed ? Humanize.Ago(now - closed) : "opened " + Humanize.Ago(now - p.CreatedAt))}",
            p.IsDraft ? "Draft" : Humanize.Pascal(p.State), p.Url)));
        HasRecentPrs = RecentPullRequests.Count > 0;
        DashboardViewModel.Replace(ActivityEvents, a.Events.Take(30).Select(e => new PersonEventViewModel(
            Humanize.Ago(now - e.At), e.Kind.ToString(), $"#{e.Number} {e.PullRequestTitle}", e.Text, e.Url)));
        HasEvents = ActivityEvents.Count > 0;
        HasActivity = true;
    }


    private void FillPersonPanel(PersonViewModel person)
    {
        var now = _time.GetUtcNow();
        var onFloor = _state.Open
            .GroupBy(p => p.Id).Select(g => g.First())
            .Where(p => string.Equals(p.Repository.FullName, person.Repository, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.ViewerRank).ThenBy(p => p.CreatedAt)
            .ToList();

        PullRequestLinkViewModel Link(PullRequest p, string detail) =>
            new(p.Title, $"#{p.Number} · {detail} · open {Humanize.Age(now - p.CreatedAt)}", p.Url);

        DashboardViewModel.Replace(RaisedItems, onFloor
            .Where(p => Login.Same(p.Author, person.Login))
            .Select(p => Link(p, p.IsDraft ? "draft" : p.Verdict switch
            {
                ReviewState.Approved => "approved",
                ReviewState.ChangesRequested => "changes requested",
                _ => "awaiting review",
            })));
        DashboardViewModel.Replace(WaitingItems, onFloor
            .Where(p => p.Assignees.ContainsLogin(person.Login) || p.RequestedReviewers.ContainsLogin(person.Login))
            .Select(p => Link(p, (p.Assignees.ContainsLogin(person.Login), p.RequestedReviewers.ContainsLogin(person.Login)) switch
            {
                (true, true) => $"assignee and reviewer · by {p.Author}",
                (true, false) => $"assignee · by {p.Author}",
                _ => $"review requested · by {p.Author}",
            })));

        var assignedCount = onFloor.Count(p => p.Assignees.ContainsLogin(person.Login));
        DashboardViewModel.Replace(LiveTiles,
        [
            new("Waiting On Them", WaitingItems.Count.ToString("N0", CultureInfo.InvariantCulture), "Red"),
            new("Assigned", assignedCount.ToString("N0", CultureInfo.InvariantCulture), "Cyan"),
            new("Raised", RaisedItems.Count.ToString("N0", CultureInfo.InvariantCulture), "Violet"),
        ]);
        HasRaisedItems = RaisedItems.Count > 0;
        HasWaitingItems = WaitingItems.Count > 0;
        PersonSubtitle = $"{person.Repository} · {RaisedItems.Count} raised · {WaitingItems.Count} waiting on them";
    }

    private FloorViewModel ToViewModel(
        RepositoryFloor floor, int number, HashSet<string> viewers, IReadOnlyDictionary<string, string> avatars) => new(
        floor.Repository.FullName,
        $"Floor {number}",
        [
            .. floor.People.Take(MaxPeoplePerFloor).Select((p, i) => new PersonViewModel(
                p.Login, floor.Repository.FullName, Humanize.Initials(p.Login), Humanize.Accent(p.Login),
                avatars.GetValueOrDefault(p.Login), p.Raised, p.Assigned, p.AssignedUnder,
                p.OldestAssignedSince is { } since ? Humanize.Age(DateTimeOffset.UtcNow - since) : "",
                p.OldestReviewSince is { } reviewSince ? Humanize.Age(DateTimeOffset.UtcNow - reviewSince) : "",
                viewers.Contains(p.Login), i % 3) { Entrance = _entrance, ToReview = p.ToReview }),
        ],
        floor.OpenCount,
        floor.DraftCount,
        floor.AwaitingReviewCount,
        floor.ApprovedCount,
        floor.ChangesRequestedCount,
        Humanize.Age(floor.AverageAge),
        floor.OldestAssignedWait is { } oa ? Humanize.Age(oa) : "none",
        floor.OldestReviewWait is { } orw ? Humanize.Age(orw) : "none");
}
