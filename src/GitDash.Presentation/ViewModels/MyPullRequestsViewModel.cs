using System.Collections.ObjectModel;
using System.Windows.Input;
using GitDash.Application.Abstractions;
using GitDash.Application.Monitoring;
using GitDash.Domain.PullRequests;
using GitDash.Presentation.Mvvm;

namespace GitDash.Presentation.ViewModels;

public enum PullRequestFilter
{
    AssignedToMe,
    All,
    ReviewRequested,
    Own,
}

/// <summary>One filter chip above the cover flow, with a live count.</summary>
public sealed class FilterOptionViewModel(PullRequestFilter filter, string label) : ObservableObject
{
    private int _count;

    public PullRequestFilter Filter { get; } = filter;
    public string Label { get; } = label;
    public int Count { get => _count; set => Set(ref _count, value); }

    public bool Matches(PullRequest pr) => Filter switch
    {
        PullRequestFilter.AssignedToMe => pr.IsAssignedToViewer,
        PullRequestFilter.ReviewRequested => pr.NeedsViewerApproval,
        PullRequestFilter.Own => pr.IsAuthoredByViewer,
        _ => true,
    };
}

public sealed record TimelineItemViewModel(string Time, string Actor, string Text, string Kind, bool IsViewer);

/// <summary>All timeline entries of one calendar day, under a date heading.</summary>
public sealed record TimelineDayViewModel(string Header, IReadOnlyList<TimelineItemViewModel> Items);

public sealed record CommentViewModel(
    string Author, string Initials, string Accent, string When, string Source, string Body,
    bool IsMine, bool MentionsMe, bool IsAi = false)
{
    /// <summary>Body rendered as Markdown blocks.</summary>
    public IReadOnlyList<GitDash.Presentation.Markdown.MarkdownBlock> Blocks { get; } =
        GitDash.Presentation.Markdown.MarkdownParser.Parse(Body);
}

public sealed record ReviewEventViewModel(string When, string Actor, string Verdict, string Text, string Kind);

/// <summary>One cover-flow card.</summary>
public sealed class PullRequestCardViewModel(PullRequest pr, DateTimeOffset now, string? authorAvatarUrl = null)
    : ObservableObject
{
    private bool _isFront;

    /// <summary>The card at the front of the cover flow, with nothing covering it: it gets the 3D tilt.</summary>
    public bool IsFront { get => _isFront; set => Set(ref _isFront, value); }

    public PullRequest PullRequest { get; } = pr;
    public string Id => PullRequest.Id;
    public string Title => PullRequest.Title;
    public string Repository => PullRequest.Repository.FullName;
    public string Number => $"#{PullRequest.Number}";
    public string Author => PullRequest.Author;
    public string AuthorInitials => Humanize.Initials(PullRequest.Author);
    public string AuthorAccent => Humanize.Accent(PullRequest.Author);
    public string? AuthorAvatarUrl { get; } = authorAvatarUrl;
    public string Url => PullRequest.Url;
    public bool IsDraft => PullRequest.IsDraft;
    public bool IsReapproval => PullRequest.IsReapproval;

    /// <summary>An approval was given before the newest commit: it has to be given again.</summary>
    public bool HasStaleApproval => PullRequest.HasStaleApproval;
    public string StaleApprovalTip =>
        $"Stale approval from {string.Join(", ", PullRequest.StaleApprovers)}: new commits were pushed after they approved";

    /// <summary>The viewer reviewed earlier and the code has changed since.</summary>
    public bool IsUpdatedSinceMyReview => PullRequest.ViewerReviewIsStale;

    /// <summary>Somebody asked for changes and has not looked at the new commits yet.</summary>
    public bool HasStaleChangeRequest => PullRequest.StaleChangeRequesters.Count > 0;
    public string StaleChangeRequestTip =>
        $"{string.Join(", ", PullRequest.StaleChangeRequesters)} requested changes before the newest commits";
    public bool HasAnyReviewFlag => IsReapproval || HasStaleApproval || IsUpdatedSinceMyReview || HasStaleChangeRequest;
    /// <summary>Drives the glowing highlight on cards that are assigned to the viewer.</summary>
    public bool IsAssignedToMe => PullRequest.IsAssignedToViewer;

    public string Role => (PullRequest.IsAssignedToViewer, PullRequest.NeedsViewerApproval) switch
    {
        (true, true) => "Assigned · Review requested",
        (true, false) => "Assigned to you",
        (false, true) => PullRequest.IsReapproval ? "Back for re-approval" : "Review requested",
        _ => PullRequest.IsAuthoredByViewer ? "Raised by you" : "Not on your desk",
    };

    public string ArrivedLabel => PullRequest.IsOnViewersDesk ? "ON DESK SINCE" : "OPENED";
    public string Arrived { get; } = Humanize.Stamp(pr.IsOnViewersDesk ? pr.ArrivedAt ?? pr.CreatedAt : pr.CreatedAt);
    public string WaitingLabel => PullRequest.IsOnViewersDesk ? "WAITING" : "AGE";

    // Waiting time by status: how long it has been assigned to me, and how long my review has been awaited.
    public bool HasAssigned => PullRequest.IsAssignedToViewer;
    public string AssignedWaiting { get; } =
        pr.IsAssignedToViewer ? Humanize.Age(now - (pr.AssignedToViewerAt ?? pr.CreatedAt)) : string.Empty;
    public bool HasReview => PullRequest.NeedsViewerApproval;
    public string ReviewWaiting { get; } =
        pr.NeedsViewerApproval ? Humanize.Age(now - (pr.ReviewRequestedFromViewerAt ?? pr.CreatedAt)) : string.Empty;
    /// <summary>Neither assigned nor awaiting my review: just how old the pull request is.</summary>
    public bool HasNoStatusWait => !PullRequest.IsOnViewersDesk;

    /// <summary>Changes whenever something shown on the card changes (used to update only the cards that need it).</summary>
    public string Key { get; } = KeyOf(pr);

    public static string KeyOf(PullRequest p) =>
        $"{p.Id}:{p.UpdatedAt.Ticks}:{p.TotalComments}:{p.ViewerRank}:{p.IsReapproval}:{p.HasStaleReview}";
    public string Waiting { get; } =
        Humanize.Age(now - (pr.IsOnViewersDesk ? pr.ArrivedAt ?? pr.CreatedAt : pr.CreatedAt));
    public string Age { get; } = Humanize.Age(now - pr.CreatedAt);

    public int TotalComments => PullRequest.TotalComments;
    public int MyComments => PullRequest.CommentsByViewer;
    public int Commits => PullRequest.CommitCount;
    public string Changes => $"+{PullRequest.Additions}  −{PullRequest.Deletions}";
    public int Files => PullRequest.ChangedFiles;
    public string Summary => PullRequest.Summary.Length == 0 ? "No description provided." : PullRequest.Summary;

    public string Verdict => PullRequest.Verdict switch
    {
        ReviewState.Approved => "Approved",
        ReviewState.ChangesRequested => "Changes requested",
        _ => PullRequest.IsDraft ? "Draft" : "Awaiting review",
    };
}

public sealed class MyPullRequestsViewModel : PageViewModel
{
    private readonly PullRequestMonitor _monitor;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;

    private MonitorState _state = MonitorState.Empty;
    private FilterOptionViewModel _selectedFilter;
    private int _selectedIndex;
    private bool _isEmpty = true;
    private string _position = string.Empty, _signature = "-", _emptyText = string.Empty;

    // Detail popup
    private PullRequestCardViewModel? _detailCard;
    private PullRequestDetails? _details;
    private DateTimeOffset _detailsVersion;
    private CancellationTokenSource? _detailLoad;
    private bool _isDetailOpen, _isDetailLoading;
    private int _detailTab;
    private string _detailError = string.Empty, _description = string.Empty;
    private string _timelineHeader = "Timeline", _commentsHeader = "Comments", _reviewsHeader = "Approvals";
    private string _approvedBy = "—", _changesRequestedBy = "—", _waitingOn = "—";
    private bool _showCommits = true, _showComments = true, _showReviews = true, _showEvents = true;
    private IReadOnlyList<GitDash.Presentation.Markdown.MarkdownBlock> _descriptionBlocks = [];
    private bool _commentsByMe = true, _commentsByOthers = true, _commentsMentioningMe = true, _commentsFromAi = true;
    private bool _hasTimeline, _hasComments, _hasReviews;

    public MyPullRequestsViewModel(
        PullRequestMonitor monitor, IUrlLauncher launcher, IUiDispatcher ui, TimeProvider time,
        Action<PullRequest> startReviewTimer)
        : base("My PRs", "IconCards")
    {
        _monitor = monitor;
        _ui = ui;
        _time = time;

        Filters =
        [
            new(PullRequestFilter.AssignedToMe, "Assigned to me"),
            new(PullRequestFilter.All, "All PRs"),
            new(PullRequestFilter.ReviewRequested, "Requested review"),
            new(PullRequestFilter.Own, "Own PRs"),
        ];
        _selectedFilter = Filters[0];

        NextCommand = new RelayCommand(() => SelectedIndex++);
        PreviousCommand = new RelayCommand(() => SelectedIndex--);
        ActivateCommand = new RelayCommand<PullRequestCardViewModel>(Activate);
        ShowDetailsCommand = new RelayCommand<PullRequestCardViewModel>(card =>
        {
            var index = Cards.IndexOf(card);
            if (index < 0) return;
            SelectedIndex = index;
            OpenDetails(card);
        });
        CloseDetailsCommand = new RelayCommand(CloseDetails);
        OpenInBrowserCommand = new RelayCommand<PullRequestCardViewModel>(c => launcher.Open(c.Url));
        StartReviewCommand = new RelayCommand<PullRequestCardViewModel>(c =>
        {
            CloseDetails();
            startReviewTimer(c.PullRequest);
        });
    }

    // ----- Filters and cards -----
    public IReadOnlyList<FilterOptionViewModel> Filters { get; }

    public FilterOptionViewModel? SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (value is null || !Set(ref _selectedFilter!, value)) return;
            _selectedIndex = 0;
            Rebuild(force: true);
        }
    }

    public ObservableCollection<PullRequestCardViewModel> Cards { get; } = [];

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            var clamped = Cards.Count == 0 ? 0 : Math.Clamp(value, 0, Cards.Count - 1);
            if (Set(ref _selectedIndex, clamped))
            {
                UpdatePosition();
                MarkFront();
            }
        }
    }

    private void MarkFront()
    {
        for (var i = 0; i < Cards.Count; i++) Cards[i].IsFront = i == _selectedIndex && !_isDetailOpen;
    }

    public string Position { get => _position; private set => Set(ref _position, value); }
    public bool IsEmpty { get => _isEmpty; private set => Set(ref _isEmpty, value); }
    public string EmptyText { get => _emptyText; private set => Set(ref _emptyText, value); }

    public ICommand NextCommand { get; }
    public ICommand PreviousCommand { get; }
    /// <summary>Clicking a side card brings it to the front; clicking the front card opens its details.</summary>
    public ICommand ActivateCommand { get; }
    /// <summary>Brings the card to the front and opens its details in one step.</summary>
    public ICommand ShowDetailsCommand { get; }
    public ICommand CloseDetailsCommand { get; }
    public ICommand OpenInBrowserCommand { get; }
    public ICommand StartReviewCommand { get; }

    // ----- Detail popup -----
    public bool IsDetailOpen
    {
        get => _isDetailOpen;
        private set
        {
            if (Set(ref _isDetailOpen, value)) MarkFront();
        }
    }
    public bool IsDetailLoading { get => _isDetailLoading; private set => Set(ref _isDetailLoading, value); }
    public string DetailError { get => _detailError; private set => Set(ref _detailError, value); }
    public PullRequestCardViewModel? DetailCard { get => _detailCard; private set => Set(ref _detailCard, value); }

    /// <summary>0 timeline, 1 description, 2 comments, 3 approvals.</summary>
    public int DetailTab { get => _detailTab; set => Set(ref _detailTab, value); }

    public string TimelineHeader { get => _timelineHeader; private set => Set(ref _timelineHeader, value); }
    public string CommentsHeader { get => _commentsHeader; private set => Set(ref _commentsHeader, value); }
    public string ReviewsHeader { get => _reviewsHeader; private set => Set(ref _reviewsHeader, value); }

    public ObservableCollection<TimelineDayViewModel> TimelineDays { get; } = [];
    public bool HasTimeline { get => _hasTimeline; private set => Set(ref _hasTimeline, value); }
    public bool ShowCommits { get => _showCommits; set { if (Set(ref _showCommits, value)) RebuildTimeline(); } }
    public bool ShowComments { get => _showComments; set { if (Set(ref _showComments, value)) RebuildTimeline(); } }
    public bool ShowReviews { get => _showReviews; set { if (Set(ref _showReviews, value)) RebuildTimeline(); } }
    public bool ShowEvents { get => _showEvents; set { if (Set(ref _showEvents, value)) RebuildTimeline(); } }

    public string Description { get => _description; private set { if (Set(ref _description, value)) DescriptionBlocks = GitDash.Presentation.Markdown.MarkdownParser.Parse(value); } }
    public IReadOnlyList<GitDash.Presentation.Markdown.MarkdownBlock> DescriptionBlocks
    {
        get => _descriptionBlocks;
        private set => Set(ref _descriptionBlocks, value);
    }

    public ObservableCollection<CommentViewModel> Comments { get; } = [];
    public bool HasComments { get => _hasComments; private set => Set(ref _hasComments, value); }
    public bool CommentsByMe { get => _commentsByMe; set { if (Set(ref _commentsByMe, value)) RebuildComments(); } }
    public bool CommentsByOthers { get => _commentsByOthers; set { if (Set(ref _commentsByOthers, value)) RebuildComments(); } }
    public bool CommentsFromAi { get => _commentsFromAi; set { if (Set(ref _commentsFromAi, value)) RebuildComments(); } }
    public bool CommentsMentioningMe { get => _commentsMentioningMe; set { if (Set(ref _commentsMentioningMe, value)) RebuildComments(); } }

    public ObservableCollection<ReviewEventViewModel> ReviewEvents { get; } = [];
    public bool HasReviews { get => _hasReviews; private set => Set(ref _hasReviews, value); }
    public string ApprovedBy { get => _approvedBy; private set => Set(ref _approvedBy, value); }
    public string ChangesRequestedBy { get => _changesRequestedBy; private set => Set(ref _changesRequestedBy, value); }
    public string WaitingOn { get => _waitingOn; private set => Set(ref _waitingOn, value); }

    public override void Apply(MonitorState state)
    {
        _state = state;
        Rebuild(force: false);

        // Keep an open popup live: reload quietly when its pull request changed on GitHub.
        if (IsDetailOpen && DetailCard is { } open)
        {
            var current = state.Open.FirstOrDefault(p => p.Id == open.Id && p.AccountId == open.PullRequest.AccountId);
            if (current is not null && current.UpdatedAt != _detailsVersion)
            {
                var card = new PullRequestCardViewModel(current, _time.GetUtcNow(), open.AuthorAvatarUrl);
                DetailCard = card;
                _ = LoadDetailsAsync(card, quiet: true);
            }
        }
    }

    private void Rebuild(bool force)
    {
        var all = _state.Open.GroupBy(p => p.Id).Select(g => g.First()).ToList();
        foreach (var option in Filters) option.Count = all.Count(option.Matches);

        var filter = _selectedFilter;
        var matching = all.Where(filter.Matches);
        // Assigned to me first, then review requested from me, then everything else.
        var ordered = filter.Filter is PullRequestFilter.AssignedToMe or PullRequestFilter.ReviewRequested
            ? matching.OrderBy(p => p.ViewerRank).ThenByDescending(p => p.IsReapproval).ThenBy(p => p.ArrivedAt ?? p.CreatedAt).ToList()
            : matching.OrderBy(p => p.ViewerRank).ThenByDescending(p => p.UpdatedAt).ToList();

        // Rebuild only when something visible changed; otherwise the cover flow would restart its animation.
        var signature = filter.Filter + "|" + string.Join('|', ordered.Select(PullRequestCardViewModel.KeyOf));
        if (!force && signature == _signature) return;
        _signature = signature;

        var selectedId = Cards.ElementAtOrDefault(SelectedIndex)?.Id;
        var now = _time.GetUtcNow();
        Reconcile(ordered, now);

        var index = selectedId is null ? -1 : ordered.FindIndex(p => p.Id == selectedId);
        SelectedIndex = index >= 0 ? index : 0;
        MarkFront(); // cards were added or replaced, so the front flag has to be set again
        IsEmpty = Cards.Count == 0;
        EmptyText = filter.Filter switch
        {
            PullRequestFilter.AssignedToMe => "Nothing is assigned to you right now.",
            PullRequestFilter.ReviewRequested => "Nobody is waiting for your review.",
            PullRequestFilter.Own => "You have no open pull requests.",
            _ => "No open pull requests in the monitored repositories.",
        };
        UpdatePosition();
    }

    /// <summary>
    /// Brings the card list in line with the new order by removing, moving, inserting and replacing only what differs.
    /// Emptying and refilling the list made every card vanish and reappear (a visible flicker) on each live update.
    /// </summary>
    private void Reconcile(List<PullRequest> ordered, DateTimeOffset now)
    {
        PullRequestCardViewModel NewCard(PullRequest pr) => new(pr, now, _state.Avatars.GetValueOrDefault(pr.Author));

        var wanted = ordered.Select(p => p.Id).ToHashSet();
        for (var i = Cards.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(Cards[i].Id)) Cards.RemoveAt(i);
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            var pr = ordered[i];
            if (i >= Cards.Count || Cards[i].Id != pr.Id)
            {
                var from = -1;
                for (var j = i + 1; j < Cards.Count; j++)
                {
                    if (Cards[j].Id != pr.Id) continue;
                    from = j;
                    break;
                }

                if (from < 0)
                {
                    Cards.Insert(i, NewCard(pr));
                    continue;
                }

                Cards.Move(from, i);
            }

            if (Cards[i].Key != PullRequestCardViewModel.KeyOf(pr)) Cards[i] = NewCard(pr);
        }
    }

    private void UpdatePosition() =>
        Position = Cards.Count == 0 ? string.Empty : $"{SelectedIndex + 1} of {Cards.Count}";

    private void Activate(PullRequestCardViewModel card)
    {
        var index = Cards.IndexOf(card);
        if (index < 0) return;
        if (index != SelectedIndex)
        {
            SelectedIndex = index;
            return;
        }

        OpenDetails(card);
    }

    private void OpenDetails(PullRequestCardViewModel card)
    {
        DetailCard = card;
        _details = null;
        Description = string.Empty;
        DetailTab = 0;
        RebuildDetailViews();
        IsDetailOpen = true;
        _ = LoadDetailsAsync(card, quiet: false);
    }

    private async Task LoadDetailsAsync(PullRequestCardViewModel card, bool quiet)
    {
        _detailLoad?.Cancel();
        var cts = _detailLoad = new CancellationTokenSource();
        DetailError = string.Empty;
        if (!quiet) IsDetailLoading = true;
        try
        {
            var details = await Task.Run(() => _monitor.GetDetailsAsync(card.PullRequest, cts.Token), cts.Token);
            _ui.Post(() =>
            {
                if (cts.IsCancellationRequested) return;
                _details = details;
                _detailsVersion = card.PullRequest.UpdatedAt;
                Description = details.Description.Length == 0 ? "No description provided." : details.Description;
                RebuildDetailViews();
                IsDetailLoading = false;
            });
        }
        catch (OperationCanceledException)
        {
            // Superseded by another card or closed.
        }
        catch (Exception ex)
        {
            _ui.Post(() =>
            {
                if (cts.IsCancellationRequested) return;
                DetailError = ex.Message;
                IsDetailLoading = false;
            });
        }
    }

    private void CloseDetails()
    {
        _detailLoad?.Cancel();
        IsDetailOpen = false;
        IsDetailLoading = false;
    }

    private void RebuildDetailViews()
    {
        RebuildTimeline();
        RebuildComments();
        RebuildReviews();
    }

    private void RebuildTimeline()
    {
        var viewer = DetailCard?.PullRequest.ViewerLogin;
        var now = _time.GetUtcNow();
        var all = _details?.Timeline ?? [];
        var visible = all.Where(e => e.Category switch
        {
            TimelineCategory.Commits => ShowCommits,
            TimelineCategory.Comments => ShowComments,
            TimelineCategory.Reviews => ShowReviews,
            _ => ShowEvents,
        }).ToList();

        // Newest day first, and newest entry first within a day: what just happened is at the top.
        var days = visible
            .OrderByDescending(e => e.At)
            .GroupBy(e => e.At.ToLocalTime().Date)
            .Select(g => new TimelineDayViewModel(
                Humanize.Day(g.First().At, now),
                [.. g.Select(e => new TimelineItemViewModel(
                    Humanize.Time(e.At), e.Actor, e.Text, e.Kind.ToString(), Login.Same(e.Actor, viewer)))]));

        TimelineDays.Clear();
        foreach (var day in days) TimelineDays.Add(day);
        HasTimeline = TimelineDays.Count > 0;
        TimelineHeader = all.Count == 0 ? "Timeline" : $"Timeline ({all.Count})";
    }

    private void RebuildComments()
    {
        var viewer = DetailCard?.PullRequest.ViewerLogin ?? string.Empty;
        var all = _details?.Comments ?? [];

        Comments.Clear();
        foreach (var c in all.OrderByDescending(c => c.At))
        {
            var mine = Login.Same(c.Author, viewer);
            var ai = !mine && c.IsAi;
            var mentions = !mine && c.Mentions(viewer);
            // A comment is shown when it belongs to at least one ticked group. AI comments have their own switch
            // and are not counted as "other people".
            var others = !mine && !ai && CommentsByOthers;
            if (!((mine && CommentsByMe) || others || (ai && CommentsFromAi) || (mentions && CommentsMentioningMe))) continue;

            Comments.Add(new CommentViewModel(
                c.Author, Humanize.Initials(c.Author), Humanize.Accent(c.Author), Humanize.Stamp(c.At),
                c.Source switch
                {
                    CommentSource.Inline => c.Path is null ? "On code" : $"On {c.Path}",
                    CommentSource.Review => "Review summary",
                    _ => "Conversation",
                },
                c.Body, mine, mentions, ai));
        }

        HasComments = Comments.Count > 0;
        CommentsHeader = all.Count == 0 ? "Comments" : $"Comments ({all.Count})";
    }

    private void RebuildReviews()
    {
        var events = (_details?.Timeline ?? []).Where(e => e.IsReviewDecision).OrderByDescending(e => e.At).ToList();
        ReviewEvents.Clear();
        foreach (var e in events)
        {
            ReviewEvents.Add(new ReviewEventViewModel(
                Humanize.Stamp(e.At), e.Actor,
                e.Kind switch
                {
                    TimelineEntryKind.Approved => "APPROVED",
                    TimelineEntryKind.ChangesRequested => "CHANGES REQUESTED",
                    TimelineEntryKind.ReviewDismissed => "REVIEW DISMISSED",
                    TimelineEntryKind.ReviewRequested => "REVIEW REQUESTED",
                    TimelineEntryKind.ReviewRequestRemoved => "REQUEST REMOVED",
                    _ => "COMMENTED",
                },
                e.Text, e.Kind.ToString()));
        }

        HasReviews = ReviewEvents.Count > 0;
        var decisions = events.Count(e => e.Kind is TimelineEntryKind.Approved or TimelineEntryKind.ChangesRequested);
        ReviewsHeader = decisions == 0 ? "Approvals" : $"Approvals ({decisions})";

        // Current standing: each reviewer's latest decision.
        var pr = DetailCard?.PullRequest;
        var latest = (pr?.Reviews ?? [])
            .Where(r => r.State is ReviewState.Approved or ReviewState.ChangesRequested)
            .GroupBy(r => r.Author, Login.Comparer)
            .Select(g => g.MaxBy(r => r.SubmittedAt)!)
            .ToList();
        ApprovedBy = Names(latest.Where(r => r.State == ReviewState.Approved).Select(r => r.Author));
        ChangesRequestedBy = Names(latest.Where(r => r.State == ReviewState.ChangesRequested).Select(r => r.Author));
        WaitingOn = Names(pr?.RequestedReviewers ?? []);

        static string Names(IEnumerable<string> logins)
        {
            var text = string.Join(", ", logins);
            return text.Length == 0 ? "—" : text;
        }
    }
}
