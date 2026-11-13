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

    public int CommitCount { get; init; }

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
