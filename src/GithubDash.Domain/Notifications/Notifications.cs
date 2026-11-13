using GithubDash.Domain.PullRequests;

namespace GithubDash.Domain.Notifications;

public enum NotificationKind
{
    AssignedToMe,
    ReviewRequested,
    NewComment,
    Merged,
    UnassignedFromMe,
}

/// <summary>Which events the user wants to hear about.</summary>
public sealed record NotificationRules(
    bool AssignedToMe = true,
    bool ReviewRequested = true,
    bool NewComment = true,
    bool Merged = true,
    bool UnassignedFromMe = true)
{
    public bool Allows(NotificationKind kind) => kind switch
    {
        NotificationKind.AssignedToMe => AssignedToMe,
        NotificationKind.ReviewRequested => ReviewRequested,
        NotificationKind.NewComment => NewComment,
        NotificationKind.Merged => Merged,
        NotificationKind.UnassignedFromMe => UnassignedFromMe,
        _ => false,
    };
}

public sealed record PullRequestNotification(
    NotificationKind Kind,
    PullRequest PullRequest,
    string Title,
    string Message,
    DateTimeOffset At);
