using GitDash.Domain.PullRequests;

namespace GitDash.Presentation.ViewModels;

/// <summary>The one state a pull request is shown in; decides the border / accent colour everywhere.</summary>
public enum PullRequestState
{
    /// <summary>Pipeline failed or merge conflict (red).</summary>
    Error,
    /// <summary>A review is out of date because of newer commits (blue).</summary>
    Stale,
    /// <summary>Approved (green).</summary>
    Approved,
    /// <summary>Changes requested (orange).</summary>
    Changes,
    /// <summary>Not reviewed yet (purple).</summary>
    Unreviewed,
}

/// <summary>A small coloured label such as "MERGE CONFLICT". Kind is one of: error, ok, assigned, changes, unreviewed, open, draft, info. Texts are in Pascal case.</summary>
public sealed record PullRequestTag(string Text, string Tip, string Kind)
{
    public bool IsError => Kind == "error";
    public bool IsOk => Kind == "ok";
    public bool IsAssigned => Kind == "assigned";
    public bool IsChanges => Kind == "changes";
    public bool IsUnreviewed => Kind == "unreviewed";
    public bool IsOpen => Kind == "open";
    public bool IsDraft => Kind == "draft";
}

/// <summary>Single source of truth for state colours and tags, shared by My PRs, Home and Dashboard.</summary>
public static class PullRequestStatus
{
    public static PullRequestState StateOf(PullRequest pr)
    {
        if (pr.HasError) return PullRequestState.Error;
        if (pr.HasStaleReview || pr.ViewerReviewIsStale) return PullRequestState.Stale;
        return pr.Verdict switch
        {
            ReviewState.Approved => PullRequestState.Approved,
            ReviewState.ChangesRequested => PullRequestState.Changes,
            _ => PullRequestState.Unreviewed,
        };
    }

    /// <summary>Same colours as the brushes in Resources.axaml.</summary>
    public static string ColorOf(PullRequestState state) => state switch
    {
        PullRequestState.Error => "#FB7185",
        PullRequestState.Stale => "#3B82F6",
        PullRequestState.Approved => "#34D399",
        PullRequestState.Changes => "#FB923C",
        _ => "#A855F7",
    };

    public static string ColorOf(PullRequest pr) => ColorOf(StateOf(pr));

    /// <summary>
    /// Tags in display order, identical on every page. With <paramref name="withState"/> the pull request's own
    /// Open or Draft state comes first (used in the details view).
    /// </summary>
    public static IReadOnlyList<PullRequestTag> TagsOf(PullRequest pr, bool withState = false)
    {
        var tags = new List<PullRequestTag>(5);
        if (withState)
        {
            tags.Add(pr.IsDraft
                ? new("Draft", "A draft pull request", "draft")
                : new("Open", "This pull request is open", "open"));
        }

        if (pr.PipelineFailed) tags.Add(new("Pipeline Failed", "The checks on the latest commit failed", "error"));
        if (pr.HasMergeConflict) tags.Add(new("Merge Conflict", "This branch has conflicts with its base branch", "error"));
        if (pr.IsAssignedToViewer) tags.Add(new("Assigned", "Assigned to you", "assigned"));
        tags.Add(pr.Verdict switch
        {
            ReviewState.Approved => new PullRequestTag("Approved", "Approved", "ok"),
            ReviewState.ChangesRequested => new PullRequestTag("Changes Requested", "A reviewer asked for changes", "changes"),
            _ => new PullRequestTag("Unreviewed", "Nobody has reviewed this yet", "unreviewed"),
        });
        if (pr.IsReapproval) tags.Add(new("Re-Approval", "You reviewed this before and were asked to look again", "info"));
        if (pr.HasStaleApproval)
            tags.Add(new("Stale Approval", $"Stale approval from {string.Join(", ", pr.StaleApprovers)}: new commits were pushed after they approved", "info"));
        if (pr.ViewerReviewIsStale) tags.Add(new("Updated Since Your Review", "New commits were pushed after your review", "info"));
        if (pr.StaleChangeRequesters.Count > 0)
            tags.Add(new("Stale Review", $"{string.Join(", ", pr.StaleChangeRequesters)} requested changes before the newest commits", "info"));
        return tags;
    }
}
