namespace GithubDash.Domain.PullRequests;

public enum TimelineEntryKind
{
    Opened,
    Commit,
    Comment,
    Approved,
    ChangesRequested,
    Reviewed,
    ReviewDismissed,
    Assigned,
    Unassigned,
    ReviewRequested,
    ReviewRequestRemoved,
    ReadyForReview,
    ConvertedToDraft,
    ForcePushed,
    Merged,
    Closed,
    Reopened,
}

/// <summary>Coarse grouping used by the timeline filters.</summary>
public enum TimelineCategory
{
    Commits,
    Comments,
    Reviews,
    Events,
}

public sealed record TimelineEntry(DateTimeOffset At, TimelineEntryKind Kind, string Actor, string Text)
{
    public TimelineCategory Category => Kind switch
    {
        TimelineEntryKind.Commit or TimelineEntryKind.ForcePushed => TimelineCategory.Commits,
        TimelineEntryKind.Comment => TimelineCategory.Comments,
        TimelineEntryKind.Approved or TimelineEntryKind.ChangesRequested or TimelineEntryKind.Reviewed
            or TimelineEntryKind.ReviewDismissed or TimelineEntryKind.ReviewRequested
            or TimelineEntryKind.ReviewRequestRemoved => TimelineCategory.Reviews,
        _ => TimelineCategory.Events,
    };

    /// <summary>Entries that tell the approval story: requests, approvals, rejections, dismissals.</summary>
    public bool IsReviewDecision => Kind is TimelineEntryKind.Approved or TimelineEntryKind.ChangesRequested
        or TimelineEntryKind.ReviewDismissed or TimelineEntryKind.ReviewRequested
        or TimelineEntryKind.ReviewRequestRemoved or TimelineEntryKind.Reviewed;
}
