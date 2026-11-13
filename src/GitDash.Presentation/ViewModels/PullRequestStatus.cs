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

/// <summary>A small coloured label such as "MERGE CONFLICT". Kind is one of: error, ok, assigned, info.</summary>
public sealed record PullRequestTag(string Text, string Tip, string Kind)
{
    public bool IsError => Kind == "error";
    public bool IsOk => Kind == "ok";
    public bool IsAssigned => Kind == "assigned";
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

    /// <summary>Tags in display order, identical to the My PRs card header.</summary>
    public static IReadOnlyList<PullRequestTag> TagsOf(PullRequest pr)
    {
        var tags = new List<PullRequestTag>(4);
        if (pr.PipelineFailed) tags.Add(new("PIPELINE FAILED", "The checks on the latest commit failed", "error"));
        if (pr.HasMergeConflict) tags.Add(new("MERGE CONFLICT", "This branch has conflicts with its base branch", "error"));
        if (pr.IsAssignedToViewer) tags.Add(new("ASSIGNED", "Assigned to you", "assigned"));
        if (pr.Verdict == ReviewState.Approved) tags.Add(new("APPROVED", "Approved", "ok"));
        if (pr.IsReapproval) tags.Add(new("RE-APPROVAL", "You reviewed this before and were asked to look again", "info"));
        if (pr.HasStaleApproval)
            tags.Add(new("STALE APPROVAL", $"Stale approval from {string.Join(", ", pr.StaleApprovers)}: new commits were pushed after they approved", "info"));
        if (pr.ViewerReviewIsStale) tags.Add(new("UPDATED SINCE YOUR REVIEW", "New commits were pushed after your review", "info"));
        if (pr.StaleChangeRequesters.Count > 0)
            tags.Add(new("STALE REVIEW", $"{string.Join(", ", pr.StaleChangeRequesters)} requested changes before the newest commits", "info"));
        return tags;
    }
}
