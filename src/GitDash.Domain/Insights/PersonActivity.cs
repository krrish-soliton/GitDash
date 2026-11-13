namespace GitDash.Domain.Insights;

/// <summary>A branch that has not been merged: with an open pull request, or without any pull request.</summary>
public sealed record PendingBranch(string Name, DateTimeOffset? LastCommitAt, bool HasPullRequest)
{
    /// <summary>A branch without a pull request for longer than this is considered neglected.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(14);

    public PendingBranchHealth HealthAt(DateTimeOffset now) =>
        HasPullRequest ? PendingBranchHealth.HasPullRequest
        : LastCommitAt is { } at && now - at > StaleAfter ? PendingBranchHealth.Stale
        : PendingBranchHealth.Waiting;
}

public enum PendingBranchHealth
{
    /// <summary>Has a pull request (green).</summary>
    HasPullRequest,
    /// <summary>No pull request yet (yellow).</summary>
    Waiting,
    /// <summary>No pull request and untouched for more than two weeks (red).</summary>
    Stale,
}

public enum PersonEventKind { Opened, Merged, Closed, Comment, Review }

/// <summary>One thing a person did on a pull request.</summary>
public sealed record PersonEvent(
    DateTimeOffset At, PersonEventKind Kind, int Number, string PullRequestTitle, string Text, string Url);

/// <summary>
/// How many GitHub Actions workflow runs a person triggered. A count is null when GitHub would not tell us
/// (for example Actions is disabled or the token cannot read it).
/// </summary>
public sealed record PipelineRunCounts(int? Week, int? Month, int? Total)
{
    public static PipelineRunCounts Unknown { get; } = new(null, null, null);
}

public sealed record PersonPullRequest(
    int Number, string Title, string Url, string State, bool IsDraft, DateTimeOffset CreatedAt, DateTimeOffset? ClosedAt);

/// <summary>
/// What one person has done in one repository: totals for their dashboard plus the recent pull requests,
/// comments and reviews behind them.
/// </summary>
public sealed record PersonActivity
{
    public required string Login { get; init; }
    public required string Repository { get; init; }

    public int Raised { get; init; }
    public int Open { get; init; }
    public int Merged { get; init; }
    public int Closed { get; init; }
    public int Draft { get; init; }

    /// <summary>Pull requests the person commented on (all time).</summary>
    public int PullRequestsCommentedOn { get; init; }

    /// <summary>Pull requests the person reviewed (all time).</summary>
    public int PullRequestsReviewed { get; init; }

    /// <summary>
    /// Branches whose newest commit is theirs and that were never merged through a pull request —
    /// work that is still waiting to go somewhere. Branches with an open pull request are included.
    /// </summary>
    public IReadOnlyList<PendingBranch> PendingBranches { get; init; } = [];

    /// <summary>Workflow runs triggered by the person: this week, this month and in total.</summary>
    public PipelineRunCounts PipelineRuns { get; init; } = PipelineRunCounts.Unknown;

    public IReadOnlyList<PersonPullRequest> RecentPullRequests { get; init; } = [];

    /// <summary>Comments and reviews found on recently active pull requests, newest first.</summary>
    public IReadOnlyList<PersonEvent> Events { get; init; } = [];

    public int CommentsInRecent => Events.Count(e => e.Kind == PersonEventKind.Comment);
    public int ReviewsInRecent => Events.Count(e => e.Kind == PersonEventKind.Review);

    /// <summary>Share of finished pull requests that were merged (not just closed).</summary>
    public double MergeRate => Merged + Closed == 0 ? 0 : (double)Merged / (Merged + Closed);
}
