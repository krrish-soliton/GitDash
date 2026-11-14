namespace GitDash.Domain.PullRequests;

public enum PullRequestState
{
    Open,
    Merged,
    Closed,
}

/// <summary>
/// A pull request as seen by one signed-in account (the "viewer").
/// Immutable snapshot; a new instance is produced on every sync.
/// </summary>
public sealed record PullRequest
{
    public required string Id { get; init; }
    public required Guid AccountId { get; init; }
    public required string ViewerLogin { get; init; }
    public required RepositoryRef Repository { get; init; }
    public required int Number { get; init; }
    public required string Title { get; init; }
    public required string Url { get; init; }
    public required string Author { get; init; }
    public PullRequestState State { get; init; } = PullRequestState.Open;
    public bool IsDraft { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? MergedAt { get; init; }
    public string? MergedBy { get; init; }

    public IReadOnlyList<string> Assignees { get; init; } = [];
    public IReadOnlyList<string> RequestedReviewers { get; init; } = [];
    public IReadOnlyList<Review> Reviews { get; init; } = [];

    /// <summary>Conversation comments plus inline review comments.</summary>
    public int TotalComments { get; init; }
    public int CommentsByViewer { get; init; }
    public string? LastCommentAuthor { get; init; }
    public DateTimeOffset? LastCommentAt { get; init; }

    /// <summary>Review conversations on the diff that nobody has resolved yet.</summary>
    public int UnresolvedThreads { get; init; }

    public int CommitCount { get; init; }

    /// <summary>GitHub reports that this branch cannot be merged cleanly into its base.</summary>
    public bool HasMergeConflict { get; init; }

    /// <summary>The checks (CI pipeline) on the newest commit failed or errored.</summary>
    public bool PipelineFailed { get; init; }

    /// <summary>Something blocks the merge on its own: a failed pipeline or a merge conflict.</summary>
    public bool HasError => HasMergeConflict || PipelineFailed;

    /// <summary>When the newest commit was made; reviews older than this were given on an earlier version of the code.</summary>
    public DateTimeOffset? LastCommitAt { get; init; }
    public int Additions { get; init; }
    public int Deletions { get; init; }
    public int ChangedFiles { get; init; }
    public string Summary { get; init; } = string.Empty;

    /// <summary>
    /// When the pull request most recently landed on each person's desk (assignment or review request),
    /// keyed by login. People without an entry have been waiting since the pull request was opened.
    /// </summary>
    public IReadOnlyDictionary<string, DateTimeOffset> WaitingSince { get; init; } =
        new Dictionary<string, DateTimeOffset>(Login.Comparer);

    public DateTimeOffset WaitingSinceFor(string login) =>
        WaitingSince.TryGetValue(login, out var since) ? since : CreatedAt;

    /// <summary>When each person was last assigned, keyed by login.</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> AssignedSince { get; init; } =
        new Dictionary<string, DateTimeOffset>(Login.Comparer);

    /// <summary>When each person was last asked to review, keyed by login.</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> ReviewRequestedSince { get; init; } =
        new Dictionary<string, DateTimeOffset>(Login.Comparer);

    /// <summary>How long this person has had the PR as assignee (falls back to when it was opened).</summary>
    public DateTimeOffset AssignedSinceFor(string login) =>
        AssignedSince.TryGetValue(login, out var at) ? at
        : Login.Same(login, ViewerLogin) && AssignedToViewerAt is { } viewerAt ? viewerAt
        : CreatedAt;

    /// <summary>How long this person has been asked to review it (falls back to when it was opened).</summary>
    public DateTimeOffset ReviewSinceFor(string login) =>
        ReviewRequestedSince.TryGetValue(login, out var at) ? at
        : Login.Same(login, ViewerLogin) && ReviewRequestedFromViewerAt is { } viewerAt ? viewerAt
        : CreatedAt;

    public DateTimeOffset? AssignedToViewerAt { get; init; }
    public DateTimeOffset? ReviewRequestedFromViewerAt { get; init; }

    public bool IsAuthoredByViewer => Login.Same(Author, ViewerLogin);
    public bool IsAssignedToViewer => Assignees.ContainsLogin(ViewerLogin);
    public bool NeedsViewerApproval => RequestedReviewers.ContainsLogin(ViewerLogin);
    public bool ViewerHasReviewed => Reviews.Any(r => Login.Same(r.Author, ViewerLogin) && r.State != ReviewState.Pending);

    /// <summary>Each reviewer's latest approval or change request (plain comments do not count).</summary>
    private IEnumerable<Review> LatestDecisions => Reviews
        .Where(r => r.State is ReviewState.Approved or ReviewState.ChangesRequested)
        .GroupBy(r => r.Author, Login.Comparer)
        .Select(g => g.MaxBy(r => r.SubmittedAt)!);

    private bool IsStale(Review review) => LastCommitAt is { } commit && review.SubmittedAt < commit;

    /// <summary>People whose approval predates the newest commit: the approved code is no longer what is in the PR.</summary>
    public IReadOnlyList<string> StaleApprovers =>
        [.. LatestDecisions.Where(r => r.State == ReviewState.Approved && IsStale(r)).Select(r => r.Author)];

    /// <summary>People who asked for changes and have not looked since new commits arrived.</summary>
    public IReadOnlyList<string> StaleChangeRequesters =>
        [.. LatestDecisions.Where(r => r.State == ReviewState.ChangesRequested && IsStale(r)).Select(r => r.Author)];

    /// <summary>At least one approval is out of date and needs to be given again.</summary>
    public bool HasStaleApproval => StaleApprovers.Count > 0;

    /// <summary>Any reviewer's decision is older than the newest commit.</summary>
    public bool HasStaleReview => HasStaleApproval || StaleChangeRequesters.Count > 0;

    /// <summary>The viewer reviewed, and the code changed after that: a fresh look is due.</summary>
    public bool ViewerReviewIsStale => LatestDecisions.Any(r => Login.Same(r.Author, ViewerLogin) && IsStale(r));

    /// <summary>The viewer's latest decision is an approval of the code as it is now (no newer commit since).</summary>
    public bool ViewerHasApproved => LatestDecisions.Any(r =>
        Login.Same(r.Author, ViewerLogin) && r.State == ReviewState.Approved && !IsStale(r));

    /// <summary>
    /// Who the pull request is waiting for right now: the requested reviewers; the author when changes were requested
    /// or it is approved and ready to merge; otherwise the assignees.
    /// </summary>
    public IReadOnlyList<string> WaitingFor
    {
        get
        {
            if (State != PullRequestState.Open) return [];
            if (Verdict == ReviewState.ChangesRequested && RequestedReviewers.Count == 0) return [Author];
            if (RequestedReviewers.Count > 0) return RequestedReviewers;
            if (Verdict == ReviewState.Approved) return [Author];
            return Assignees;
        }
    }

    /// <summary>Why <see cref="WaitingFor"/> is waiting, in a few words.</summary>
    public string WaitingForReason =>
        State != PullRequestState.Open ? string.Empty
        : Verdict == ReviewState.ChangesRequested && RequestedReviewers.Count == 0 ? "to address the requested changes"
        : RequestedReviewers.Count > 0 ? "to review"
        : Verdict == ReviewState.Approved ? "to merge"
        : Assignees.Count > 0 ? "assigned" : string.Empty;

    /// <summary>The viewer already reviewed once and has been asked to look again.</summary>
    public bool IsReapproval => NeedsViewerApproval && ViewerHasReviewed;

    /// <summary>PRs whose new comments the viewer cares about.</summary>
    public bool IsViewerResponsible =>
        IsAuthoredByViewer || IsAssignedToViewer || NeedsViewerApproval || ViewerHasReviewed;

    /// <summary>PRs that sit on the viewer's desk right now.</summary>
    public bool IsOnViewersDesk => IsAssignedToViewer || NeedsViewerApproval;

    /// <summary>Sort priority for every list: 0 = assigned to the viewer, 1 = viewer is a requested reviewer, 2 = everything else.</summary>
    public int ViewerRank => IsAssignedToViewer ? 0 : NeedsViewerApproval ? 1 : 2;

    /// <summary>When the PR most recently landed on the viewer's desk (assignment or review request).</summary>
    public DateTimeOffset? ArrivedAt => (AssignedToViewerAt, ReviewRequestedFromViewerAt) switch
    {
        (null, null) => null,
        ({ } a, null) => a,
        (null, { } r) => r,
        ({ } a, { } r) => a > r ? a : r,
    };

    /// <summary>Latest decisive review state per reviewer, ignoring plain comments.</summary>
    public ReviewState? Verdict
    {
        get
        {
            var latest = Reviews
                .Where(r => r.State is ReviewState.Approved or ReviewState.ChangesRequested)
                .GroupBy(r => r.Author, Login.Comparer)
                .Select(g => g.MaxBy(r => r.SubmittedAt)!.State)
                .ToList();
            if (latest.Count == 0) return null;
            return latest.Contains(ReviewState.ChangesRequested) ? ReviewState.ChangesRequested : ReviewState.Approved;
        }
    }
}
