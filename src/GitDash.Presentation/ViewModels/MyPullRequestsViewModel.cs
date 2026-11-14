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
    MergeConflict,
    PipelineFailed,
}

/// <summary>One author in the multi-select author filter.</summary>
public sealed class AuthorOptionViewModel(string login, string? avatarUrl, Action<AuthorOptionViewModel> changed) : ObservableObject
{
    private bool _isSelected;
    private int _count;
    public string Login { get; } = login;
    public string FirstName { get; } = Humanize.FirstName(login);
    public string Initials { get; } = Humanize.Initials(login);
    public string Accent { get; } = Humanize.Accent(login);
    public string? AvatarUrl { get; } = avatarUrl;
    public int Count { get => _count; set => Set(ref _count, value); }
    public bool IsSelected
    {
        get => _isSelected;
        set { if (Set(ref _isSelected, value)) changed(this); }
    }
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
        PullRequestFilter.MergeConflict => pr.HasMergeConflict,
        PullRequestFilter.PipelineFailed => pr.PipelineFailed,
        _ => true,
    };
}

public sealed record TimelineItemViewModel(string Time, string Actor, string Text, string Kind, bool IsViewer, string Label, string Tone);

/// <summary>The tag text and colour key of a timeline entry: people and reviewers added or removed, states, decisions.</summary>
public static class TimelineTags
{
    public static string LabelOf(TimelineEntryKind kind) => kind switch
    {
        TimelineEntryKind.Assigned or TimelineEntryKind.ReviewRequested => "Added",
        TimelineEntryKind.Unassigned or TimelineEntryKind.ReviewRequestRemoved => "Removed",
        TimelineEntryKind.ChangesRequested => "Changes Requested",
        TimelineEntryKind.ReviewDismissed => "Dismissed",
        TimelineEntryKind.ReadyForReview => "Ready For Review",
        TimelineEntryKind.ConvertedToDraft => "Draft",
        TimelineEntryKind.ForcePushed => "Force Pushed",
        TimelineEntryKind.Reopened => "Open",
        _ => kind.ToString(),
    };

    /// <summary>Colour key understood by the tone converter; Added and Open get colours of their own.</summary>
    public static string ToneOf(TimelineEntryKind kind) => kind switch
    {
        TimelineEntryKind.Assigned or TimelineEntryKind.ReviewRequested => "Added",
        TimelineEntryKind.Reopened => "Open",
        TimelineEntryKind.ConvertedToDraft => "Draft",
        _ => kind.ToString(),
    };
}

/// <summary>All timeline entries of one calendar day, under a date heading.</summary>
public sealed record TimelineDayViewModel(string Header, IReadOnlyList<TimelineItemViewModel> Items);

/// <summary>One emoji reaction chip under a comment, for example "👍 3".</summary>
public sealed record ReactionChipViewModel(string Emoji, int Count, IReadOnlyList<string>? Users = null)
{
    /// <summary>The emoji with the first names of who reacted ("👍 Anna, Ben +3"), or just the count when GitHub gave no names.</summary>
    public string Label
    {
        get
        {
            if (Users is not { Count: > 0 }) return $"{Emoji} {Count}";
            var names = Users.Select(Humanize.FirstName).Distinct().ToList();
            var shown = string.Join(", ", names.Take(3));
            var more = Count - Math.Min(3, names.Count);
            return more > 0 ? $"{Emoji} {shown} +{more}" : $"{Emoji} {shown}";
        }
    }

    /// <summary>Full logins of everyone who reacted, for the tooltip.</summary>
    public string Tip => Users is { Count: > 0 } ? string.Join(", ", Users) + (Count > Users.Count ? $" and {Count - Users.Count} more" : "") : $"{Count} reaction(s)";
}

public sealed record CommentViewModel(
    string Author, string Initials, string Accent, string When, string Source, string Body,
    bool IsMine, bool MentionsMe, bool IsAi = false, bool IsResolved = false, bool IsUnresolved = false,
    IReadOnlyList<ReactionChipViewModel>? Reactions = null, bool IsReply = false)
{
    public IReadOnlyList<ReactionChipViewModel> ReactionChips => Reactions ?? [];
    public bool HasReactions => ReactionChips.Count > 0;
    /// <summary>The first message of a conversation shows where it was said; replies only follow it.</summary>
    public bool ShowsSource => !IsReply;

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
    // Review status of the pull request as a whole (drives the card border colour and the approved tag).
    public bool IsApproved => PullRequest.Verdict == ReviewState.Approved;
    public bool IsChangesRequested => PullRequest.Verdict == ReviewState.ChangesRequested;
    public bool IsUnreviewed => !IsApproved && !IsChangesRequested;

    // Errors that block the merge.
    public bool HasMergeConflict => PullRequest.HasMergeConflict;
    public bool PipelineFailed => PullRequest.PipelineFailed;
    public bool HasError => PullRequest.HasError;
    /// <summary>Tags shown on the card and in the details header (the same set as on every other page).</summary>
    public IReadOnlyList<PullRequestTag> Tags { get; } = PullRequestStatus.TagsOf(pr);
    public IReadOnlyList<PullRequestTag> DetailTags { get; } = PullRequestStatus.TagsOf(pr, withState: true);
    public bool HasTags => IsApproved || HasAssigned || HasError || IsReapproval || HasStaleApproval || IsUpdatedSinceMyReview || HasStaleChangeRequest;

    /// <summary>Any review (or the viewer's own) is out of date because new commits arrived.</summary>
    public bool IsStale => PullRequest.HasStaleReview || PullRequest.ViewerReviewIsStale;

    // Exactly one of these is true, which picks the single border colour. First match wins:
    // error (red), stale review (blue), approved (green), changes requested (orange), not reviewed (purple).
    public bool StateError => PullRequestStatus.StateOf(PullRequest) == PullRequestState.Error;
    public bool StateStale => PullRequestStatus.StateOf(PullRequest) == PullRequestState.Stale;
    public bool StateApproved => PullRequestStatus.StateOf(PullRequest) == PullRequestState.Approved;
    public bool StateChanges => PullRequestStatus.StateOf(PullRequest) == PullRequestState.Changes;
    public bool StateUnreviewed => PullRequestStatus.StateOf(PullRequest) == PullRequestState.Unreviewed;

    /// <summary>Neither assigned nor awaiting my review: just how old the pull request is.</summary>
    public bool HasNoStatusWait => !PullRequest.IsOnViewersDesk;

    /// <summary>Changes whenever something shown on the card changes (used to update only the cards that need it).</summary>
    public string Key { get; } = KeyOf(pr);

    public static string KeyOf(PullRequest p) =>
        $"{p.Id}:{p.UpdatedAt.Ticks}:{p.TotalComments}:{p.ViewerRank}:{p.IsReapproval}:{p.HasStaleReview}:{p.ViewerReviewIsStale}:{p.Verdict}:{p.HasMergeConflict}:{p.PipelineFailed}:{Humanize.Format}";
    public string Waiting { get; } =
        Humanize.Age(now - (pr.IsOnViewersDesk ? pr.ArrivedAt ?? pr.CreatedAt : pr.CreatedAt));
    public string Age { get; } = Humanize.Age(now - pr.CreatedAt);

    // ----- Who it is waiting for -----
    public bool HasWaitingFor => PullRequest.WaitingFor.Count > 0;
    public string WaitingForText => PullRequest.WaitingFor.Count == 0
        ? string.Empty
        : $"Waiting for {string.Join(", ", PullRequest.WaitingFor.Select(w => Login.Same(w, PullRequest.ViewerLogin) ? "you" : w))}" +
          (PullRequest.WaitingForReason.Length > 0 ? $" · {PullRequest.WaitingForReason}" : string.Empty);

    // ----- Pipeline runs (from GitHub Actions) -----
    private int? _pipelineRuns;
    private int _runsActive;
    private bool _lastRunFailed, _lastRunPassed;

    /// <summary>How many of the repository's recent workflow runs belonged to this pull request; null until Actions was read.</summary>
    public string PipelineRunsText => _pipelineRuns is { } n ? n.ToString() : "—";
    public bool RunsActive => _runsActive > 0;
    public string RunsActiveText => $"{_runsActive} running";
    public bool LastRunFailed => _lastRunFailed && _runsActive == 0;
    public bool LastRunPassed => _lastRunPassed && _runsActive == 0;
    public bool HasRunTag => RunsActive || LastRunFailed || LastRunPassed;

    public void ApplyActions(GitDash.Application.Monitoring.ActionsState state)
    {
        var runs = state.RunsFor(PullRequest.Repository, PullRequest.Number).ToList();
        var latest = runs.OrderByDescending(r => r.StartedAt).FirstOrDefault();
        _pipelineRuns = state.UpdatedAt is null ? null : state.RunCount(PullRequest.Repository, PullRequest.Number);
        _runsActive = runs.Count(r => r.IsActive);
        _lastRunFailed = latest?.Failed == true;
        _lastRunPassed = latest?.Succeeded == true;
        Raise(nameof(PipelineRunsText));
        Raise(nameof(RunsActive));
        Raise(nameof(RunsActiveText));
        Raise(nameof(LastRunFailed));
        Raise(nameof(LastRunPassed));
        Raise(nameof(HasRunTag));
    }

    // ----- Approve and hand over (two clicks: ask, then confirm) -----
    private string? _pending;
    private bool _isBusy;

    public bool CanApprove => PullRequest.State == GitDash.Domain.PullRequests.PullRequestState.Open && !PullRequest.IsAuthoredByViewer &&
                              !PullRequest.IsDraft && !PullRequest.ViewerHasApproved;
    public bool CanHandOver => PullRequest.State == GitDash.Domain.PullRequests.PullRequestState.Open && PullRequest.IsAssignedToViewer &&
                               !PullRequest.IsAuthoredByViewer;
    /// <summary>Put the author on the assignees (when they are not on them already).</summary>
    public bool CanAssignAuthor => PullRequest.State == GitDash.Domain.PullRequests.PullRequestState.Open &&
                                   !PullRequest.IsAuthoredByViewer && !PullRequest.Assignees.ContainsLogin(PullRequest.Author);
    /// <summary>Take myself off the assignees.</summary>
    public bool CanRemoveMe => PullRequest.State == GitDash.Domain.PullRequests.PullRequestState.Open && PullRequest.IsAssignedToViewer;
    public bool HasActions => CanApprove || CanAssignAuthor || CanRemoveMe;
    public bool ViewerApproved => PullRequest.ViewerHasApproved;

    /// <summary>"approve", "handover" or null: the action waiting for a second click.</summary>
    public string? Pending
    {
        get => _pending;
        set
        {
            if (!Set(ref _pending, value)) return;
            Raise(nameof(ApproveLabel));
            Raise(nameof(HandOverLabel));
            Raise(nameof(AssignAuthorLabel));
            Raise(nameof(RemoveMeLabel));
            Raise(nameof(IsConfirming));
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (!Set(ref _isBusy, value)) return;
            Raise(nameof(ApproveLabel));
            Raise(nameof(HandOverLabel));
            Raise(nameof(AssignAuthorLabel));
            Raise(nameof(RemoveMeLabel));
        }
    }

    public string AssignAuthorLabel => _isBusy ? "Working…" : _pending == "assign" ? "Confirm assign author" : "Assign author";
    public string RemoveMeLabel => _isBusy ? "Working…" : _pending == "removeme" ? "Confirm remove me" : "Remove me as assignee";
    public string AssignAuthorTip => $"Add {PullRequest.Author} to the assignees";

    // ----- Review timer button: a play icon and the time tracked on this pull request -----
    private string _reviewClock = "00:00";
    public string ReviewClock { get => _reviewClock; set => Set(ref _reviewClock, value); }

    public string ApproveLabel => _isBusy ? "Working…" : _pending == "approve" ? "Confirm approve" : "Approve";
    public string HandOverLabel => _isBusy ? "Working…" : _pending == "handover" ? "Confirm hand over" : "Hand to author";
    public bool IsConfirming => _pending is not null;
    public string HandOverTip => $"Remove you as assignee and assign {PullRequest.Author}";

    public int TotalComments => PullRequest.TotalComments;
    public int MyComments => PullRequest.CommentsByViewer;
    public int Commits => PullRequest.CommitCount;
    public string Changes => $"+{PullRequest.Additions}  −{PullRequest.Deletions}";
    public string AdditionsText => $"+{PullRequest.Additions:N0}";
    public string DeletionsText => $"−{PullRequest.Deletions:N0}";
    public string OpenText => $"Open {Age}";
    public int Files => PullRequest.ChangedFiles;
    public string Summary => PullRequest.Summary.Length == 0 ? "No description provided." : PullRequest.Summary;

    public string Verdict => PullRequest.Verdict switch
    {
        ReviewState.Approved => "Approved",
        ReviewState.ChangesRequested => "Changes Requested",
        _ => PullRequest.IsDraft ? "Draft" : "Awaiting Review",
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
    private readonly HashSet<string> _selectedAuthors = new(StringComparer.OrdinalIgnoreCase);
    private bool _syncingAuthors;
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
        Action<PullRequest> startReviewTimer, ActionsViewModel? actions = null)
        : base("Pull requests", "IconCards")
    {
        _monitor = monitor;
        _ui = ui;
        _time = time;
        Actions = actions;
        if (actions is not null) actions.Updated += OnActionsUpdated;

        ApproveCommand = new RelayCommand<PullRequestCardViewModel>(c =>
            Confirmed(c, "approve", () => _monitor.ApproveAsync(c.PullRequest), $"Approved {c.Number}"));
        HandOverCommand = new RelayCommand<PullRequestCardViewModel>(c =>
            Confirmed(c, "handover", () => _monitor.HandOverToAuthorAsync(c.PullRequest),
                $"{c.Number} handed to {c.Author}: you were removed as assignee"));
        AssignAuthorCommand = new RelayCommand<PullRequestCardViewModel>(c =>
            Confirmed(c, "assign", () => _monitor.AssignAuthorAsync(c.PullRequest), $"{c.Author} is now assigned to {c.Number}"));
        RemoveMeCommand = new RelayCommand<PullRequestCardViewModel>(c =>
            Confirmed(c, "removeme", () => _monitor.RemoveViewerAssigneeAsync(c.PullRequest), $"You were removed as assignee of {c.Number}"));
        CancelPendingCommand = new RelayCommand<PullRequestCardViewModel>(c => c.Pending = null);
        DismissActionMessageCommand = new RelayCommand(() => ActionMessage = string.Empty);

        Filters =
        [
            new(PullRequestFilter.AssignedToMe, "Assigned to me"),
            new(PullRequestFilter.All, "All PRs"),
            new(PullRequestFilter.ReviewRequested, "Requested review"),
            new(PullRequestFilter.Own, "Own PRs"),
            new(PullRequestFilter.MergeConflict, "Merge conflicts"),
            new(PullRequestFilter.PipelineFailed, "Pipeline failed"),
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
            if (c.PullRequest.ViewerHasApproved)
            {
                // Timing a review you have already approved is unusual: ask first.
                _pendingReview = c;
                ConfirmReviewMessage = $"You already approved #{c.PullRequest.Number} {c.PullRequest.Title}. Start a review timer anyway? It will not stop by itself.";
                IsConfirmingReview = true;
                return;
            }

            CloseDetails();
            startReviewTimer(c.PullRequest);
        });
        ConfirmReviewCommand = new RelayCommand(() =>
        {
            var card = _pendingReview;
            _pendingReview = null;
            IsConfirmingReview = false;
            if (card is null) return;
            CloseDetails();
            startReviewTimer(card.PullRequest);
        });
        CancelReviewCommand = new RelayCommand(() =>
        {
            _pendingReview = null;
            IsConfirmingReview = false;
        });
    }

    // ----- Approve / hand over -----
    private string _actionMessage = string.Empty;
    private bool _isActionError;

    public ActionsViewModel? Actions { get; }
    public bool HasActions => Actions is not null;
    public ICommand ApproveCommand { get; }
    public ICommand HandOverCommand { get; }
    public ICommand AssignAuthorCommand { get; }
    public ICommand RemoveMeCommand { get; }

    /// <summary>Refreshes the clock on every card's review-timer button (called on each timer tick).</summary>
    public void UpdateReviewClocks(Func<string, string?> clockFor)
    {
        foreach (var card in Cards) card.ReviewClock = clockFor(card.PullRequest.Id) ?? "00:00";
        if (DetailCard is { } open) open.ReviewClock = clockFor(open.PullRequest.Id) ?? "00:00";
    }
    public ICommand CancelPendingCommand { get; }
    public ICommand DismissActionMessageCommand { get; }

    /// <summary>Result of the last approve or hand over, shown above the cards.</summary>
    public string ActionMessage
    {
        get => _actionMessage;
        private set
        {
            if (Set(ref _actionMessage, value)) Raise(nameof(HasActionMessage));
        }
    }

    public bool HasActionMessage => _actionMessage.Length > 0;
    public bool IsActionError { get => _isActionError; private set => Set(ref _isActionError, value); }

    /// <summary>First click arms the action, second click runs it, so a stray click cannot approve a pull request.</summary>
    private void Confirmed(PullRequestCardViewModel card, string name, Func<Task> action, string done)
    {
        if (card.IsBusy) return;
        if (card.Pending != name)
        {
            card.Pending = name;
            return;
        }

        card.Pending = null;
        card.IsBusy = true;
        IsActionError = false;
        ActionMessage = "Working…";
        _ = RunAsync();

        async Task RunAsync()
        {
            try
            {
                await Task.Run(action).ConfigureAwait(false);
                _ui.Post(() => ActionMessage = done);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _ui.Post(() =>
                {
                    IsActionError = true;
                    ActionMessage = ex.Message;
                });
            }
            finally
            {
                _ui.Post(() => card.IsBusy = false);
            }
        }
    }

    // ----- Actions tab of the details popup: the workflow runs of this pull request -----
    public ObservableCollection<RunItemViewModel> DetailRuns { get; } = [];
    public bool HasDetailRuns => DetailRuns.Count > 0;
    public string ActionsHeader => DetailRuns.Count == 0 ? "Actions" : $"Actions ({DetailRuns.Count})";

    private void RefreshDetailRuns()
    {
        var runs = DetailCard is { } card && Actions is not null
            ? Actions.RunsFor(card.PullRequest.Repository, card.PullRequest.Number)
            : [];
        DashboardViewModel.Replace(DetailRuns, runs);
        Raise(nameof(HasDetailRuns));
        Raise(nameof(ActionsHeader));
    }

    private void OnActionsUpdated(GitDash.Application.Monitoring.ActionsState state)
    {
        RefreshDetailRuns();
        foreach (var card in Cards) card.ApplyActions(state);
    }

    private PullRequestCardViewModel? _pendingReview;
    private bool _isConfirmingReview;
    private string _confirmReviewMessage = string.Empty;

    /// <summary>Asks before a review timer is started on a pull request the viewer already approved.</summary>
    public bool IsConfirmingReview { get => _isConfirmingReview; private set => Set(ref _isConfirmingReview, value); }
    public string ConfirmReviewMessage { get => _confirmReviewMessage; private set => Set(ref _confirmReviewMessage, value); }
    public ICommand ConfirmReviewCommand { get; }
    public ICommand CancelReviewCommand { get; }

    // ----- Filters and cards -----
    public IReadOnlyList<FilterOptionViewModel> Filters { get; }

    public FilterOptionViewModel? SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (value is null || !Set(ref _selectedFilter!, value)) return;
            Rebuild(force: true, startAtFirst: true);
        }
    }

    public ObservableCollection<PullRequestCardViewModel> Cards { get; } = [];

    // ----- Author filter (multi-select) -----
    public ObservableCollection<AuthorOptionViewModel> AuthorOptions { get; } = [];
    public bool HasAuthorFilter => _selectedAuthors.Count > 0;
    public string AuthorSummary => _selectedAuthors.Count switch
    {
        0 => "All authors",
        1 => Humanize.FirstName(_selectedAuthors.First()),
        _ => $"{Humanize.FirstName(_selectedAuthors.First())} +{_selectedAuthors.Count - 1}",
    };
    public ICommand ClearAuthorsCommand => _clearAuthors ??= new RelayCommand(() =>
    {
        _selectedAuthors.Clear();
        _syncingAuthors = true;
        foreach (var option in AuthorOptions) option.IsSelected = false;
        _syncingAuthors = false;
        OnAuthorsChanged();
    });
    private ICommand? _clearAuthors;

    private void OnAuthorOptionChanged(AuthorOptionViewModel option)
    {
        if (_syncingAuthors) return;
        if (option.IsSelected) _selectedAuthors.Add(option.Login);
        else _selectedAuthors.Remove(option.Login);
        OnAuthorsChanged();
    }

    private void OnAuthorsChanged()
    {
        Raise(nameof(HasAuthorFilter));
        Raise(nameof(AuthorSummary));
        Rebuild(force: true, startAtFirst: true);
    }

    private void SyncAuthorOptions(IReadOnlyList<PullRequest> pool)
    {
        // Authors of the pull requests in the current view (before the author filter), most pull requests first.
        var groups = pool.GroupBy(p => p.Author, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
        var logins = groups.Select(g => g.Key).ToList();
        _syncingAuthors = true;
        try
        {
            if (!AuthorOptions.Select(o => o.Login).SequenceEqual(logins, StringComparer.OrdinalIgnoreCase))
            {
                AuthorOptions.Clear();
                foreach (var g in groups)
                {
                    AuthorOptions.Add(new AuthorOptionViewModel(g.Key, _state.Avatars.GetValueOrDefault(g.Key), OnAuthorOptionChanged)
                    {
                        IsSelected = _selectedAuthors.Contains(g.Key),
                    });
                }
            }

            foreach (var option in AuthorOptions) option.Count = groups.First(g => g.Key.Equals(option.Login, StringComparison.OrdinalIgnoreCase)).Count();
        }
        finally
        {
            _syncingAuthors = false;
        }
    }

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
                UpdateActionScope();
            }
        }
    }

    /// <summary>The Actions panel lists only runs of the pull requests on the cards (and optionally only the front one).</summary>
    private void UpdateActionScope()
    {
        if (Actions is null) return;
        var keys = Cards.Select(c => ActionsState.KeyOf(c.PullRequest.Repository, c.PullRequest.Number)).ToList();
        var current = Cards.ElementAtOrDefault(_selectedIndex) is { } front
            ? ActionsState.KeyOf(front.PullRequest.Repository, front.PullRequest.Number) : null;
        Actions.SetScope(keys, current);
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

    /// <summary>Resolved inline threads are shown while this is on.</summary>
    public bool ShowResolvedComments { get => _showResolvedComments; set { if (Set(ref _showResolvedComments, value)) RebuildComments(); } }

    /// <summary>Unresolved inline threads are shown while this is on.</summary>
    public bool ShowUnresolvedComments { get => _showUnresolvedComments; set { if (Set(ref _showUnresolvedComments, value)) RebuildComments(); } }
    private bool _showResolvedComments = true, _showUnresolvedComments = true;

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

    private void Rebuild(bool force, bool startAtFirst = false)
    {
        var all = _state.Open.GroupBy(p => p.Id).Select(g => g.First()).ToList();
        foreach (var option in Filters) option.Count = all.Count(option.Matches);

        var filter = _selectedFilter;
        SyncAuthorOptions([.. all.Where(filter.Matches)]);
        var matching = all.Where(filter.Matches).Where(p => _selectedAuthors.Count == 0 || _selectedAuthors.Contains(p.Author));
        // Assigned to me first, then review requested from me, then everything else.
        var ordered = filter.Filter is PullRequestFilter.AssignedToMe or PullRequestFilter.ReviewRequested
            ? matching.OrderBy(p => p.ViewerRank).ThenByDescending(p => p.IsReapproval).ThenBy(p => p.ArrivedAt ?? p.CreatedAt).ToList()
            : matching.OrderBy(p => p.ViewerRank).ThenByDescending(p => p.UpdatedAt).ToList();

        // Rebuild only when something visible changed; otherwise the cover flow would restart its animation.
        var signature = filter.Filter + "|" + string.Join(',', _selectedAuthors.Order()) + "|" + string.Join('|', ordered.Select(PullRequestCardViewModel.KeyOf));
        if (!force && signature == _signature) return;
        _signature = signature;

        var selectedId = startAtFirst ? null : Cards.ElementAtOrDefault(SelectedIndex)?.Id;
        var now = _time.GetUtcNow();
        Reconcile(ordered, now);

        var index = selectedId is null ? -1 : ordered.FindIndex(p => p.Id == selectedId);
        // Always announce the index: when it stays 0 across a filter change the cover flow would otherwise keep its
        // old position, and a single remaining card ended up parked at the side instead of in the centre.
        _selectedIndex = Math.Clamp(index >= 0 ? index : 0, 0, Math.Max(0, Cards.Count - 1));
        Raise(nameof(SelectedIndex));
        MarkFront(); // cards were added or replaced, so the front flag has to be set again
        IsEmpty = Cards.Count == 0;
        EmptyText = filter.Filter switch
        {
            PullRequestFilter.AssignedToMe => "Nothing is assigned to you right now.",
            PullRequestFilter.ReviewRequested => "Nobody is waiting for your review.",
            PullRequestFilter.Own => "You have no open pull requests.",
            PullRequestFilter.MergeConflict => "No pull requests have merge conflicts.",
            PullRequestFilter.PipelineFailed => "No pull requests have failing pipelines.",
            _ => "No open pull requests in the monitored repositories.",
        };
        UpdatePosition();
        UpdateActionScope();
    }

    /// <summary>
    /// Brings the card list in line with the new order by removing, moving, inserting and replacing only what differs.
    /// Emptying and refilling the list made every card vanish and reappear (a visible flicker) on each live update.
    /// </summary>
    private void Reconcile(List<PullRequest> ordered, DateTimeOffset now)
    {
        PullRequestCardViewModel NewCard(PullRequest pr)
        {
            var card = new PullRequestCardViewModel(pr, now, _state.Avatars.GetValueOrDefault(pr.Author));
            if (Actions is { State: { } actionsState }) card.ApplyActions(actionsState);
            return card;
        }

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
        RefreshDetailRuns();
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
            .GroupBy(e => Humanize.DayOf(e.At))
            .Select(g => new TimelineDayViewModel(
                Humanize.Day(g.First().At, now),
                [.. g.Select(e => new TimelineItemViewModel(
                    Humanize.Time(e.At), e.Actor, e.Text, e.Kind.ToString(), Login.Same(e.Actor, viewer),
                    TimelineTags.LabelOf(e.Kind), TimelineTags.ToneOf(e.Kind)))]));

        TimelineDays.Clear();
        foreach (var day in days) TimelineDays.Add(day);
        HasTimeline = TimelineDays.Count > 0;
        TimelineHeader = all.Count == 0 ? "Timeline" : $"Timeline ({all.Count})";
    }

    private void RebuildComments()
    {
        var viewer = DetailCard?.PullRequest.ViewerLogin ?? string.Empty;
        var all = _details?.Comments ?? [];

        // A chat: oldest first, and a code conversation stays together with its replies right under the first message.
        var ordered = all
            .Select((c, i) => (Comment: c, Key: c.ThreadId ?? "single:" + i))
            .GroupBy(x => x.Key)
            .Select(g => g.OrderBy(x => x.Comment.At).Select((x, n) => (x.Comment, IsReply: n > 0)).ToList())
            .OrderBy(g => g[0].Comment.At)
            .SelectMany(g => g)
            .ToList();

        Comments.Clear();
        foreach (var (c, isReply) in ordered)
        {
            var mine = Login.Same(c.Author, viewer);
            var ai = !mine && c.IsAi;
            var mentions = !mine && c.Mentions(viewer);
            // A comment is shown when it belongs to at least one ticked group. AI comments have their own switch
            // and are not counted as "other people".
            var others = !mine && !ai && CommentsByOthers;
            if (c.IsResolvedThread && !ShowResolvedComments) continue;
            if (c.IsUnresolvedThread && !ShowUnresolvedComments) continue;
            if (!((mine && CommentsByMe) || others || (ai && CommentsFromAi) || (mentions && CommentsMentioningMe))) continue;

            Comments.Add(new CommentViewModel(
                c.Author, Humanize.Initials(c.Author), Humanize.Accent(c.Author), Humanize.Stamp(c.At),
                c.Source switch
                {
                    CommentSource.Inline => c.Path is null ? "On code" : $"On {c.Path}",
                    CommentSource.Review => "Review summary",
                    _ => "Conversation",
                },
                c.Body, mine, mentions, ai, c.IsResolvedThread, c.IsUnresolvedThread,
                [.. c.ReactionList.Select(r => new ReactionChipViewModel(r.Emoji, r.Count, r.Users))], isReply));
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
                    TimelineEntryKind.Approved => "Approved",
                    TimelineEntryKind.ChangesRequested => "Changes Requested",
                    TimelineEntryKind.ReviewDismissed => "Review Dismissed",
                    TimelineEntryKind.ReviewRequested => "Review Requested",
                    TimelineEntryKind.ReviewRequestRemoved => "Request Removed",
                    _ => "Commented",
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
