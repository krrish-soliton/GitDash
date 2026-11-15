using GitDash.Application.Abstractions;
using GitDash.Application.Settings;
using GitDash.Domain.Notifications;

namespace GitDash.Application.Monitoring;

/// <summary>Sends a notification to the animated toasts or to the OS, depending on the user's choice.</summary>
public sealed class NotificationRouter(SettingsService settings, IToastPresenter toasts, INativeNotifier native)
{
    public Task PublishAsync(PullRequestNotification notification, CancellationToken ct = default) =>
        PublishAsync(new ToastRequest(
            notification.Title,
            notification.Message,
            notification.Kind switch
            {
                NotificationKind.AssignedToMe => ToastTone.Assigned,
                NotificationKind.ReviewRequested => ToastTone.Review,
                NotificationKind.NewComment => ToastTone.Comment,
                NotificationKind.UnassignedFromMe => ToastTone.Removed,
                _ => ToastTone.Merged,
            },
            notification.PullRequest.Url,
            ActorOf(notification),
            DetailOf(notification),
            ToastTarget.PullRequest), ct);

    private static string DetailOf(PullRequestNotification n)
    {
        var pr = n.PullRequest;
        // One "Label: value" per line; the notification history shows them as a small table.
        var lines = new List<string>
        {
            $"Repository: {pr.Repository.FullName}",
            $"Pull request: #{pr.Number} {pr.Title}",
            $"Author: {pr.Author}",
            $"Opened: {pr.CreatedAt.ToLocalTime():d MMM yyyy, HH:mm}",
            $"State: {(pr.State == PullRequestState.Open ? (pr.IsDraft ? "Draft" : "Open") : pr.State.ToString())}",
        };
        if (pr.Assignees.Count > 0) lines.Add("Assigned to: " + string.Join(", ", pr.Assignees));
        if (pr.RequestedReviewers.Count > 0) lines.Add("Review requested from: " + string.Join(", ", pr.RequestedReviewers));
        if (pr.MergedBy is not null) lines.Add("Merged by: " + pr.MergedBy);
        lines.Add($"Conversation: {pr.TotalComments} comments, {pr.UnresolvedThreads} unresolved");
        lines.Add($"Changes: {pr.CommitCount} commits, {pr.ChangedFiles} files, +{pr.Additions} -{pr.Deletions}");
        if (pr.HasMergeConflict || pr.PipelineFailed)
            lines.Add("Needs attention: " + string.Join(" and ", new[] { pr.HasMergeConflict ? "merge conflict" : null, pr.PipelineFailed ? "failing pipeline" : null }.OfType<string>()));
        return string.Join('\n', lines);
    }

    /// <summary>The person a notification is about, so their picture can be shown with it.</summary>
    private static string? ActorOf(PullRequestNotification n) => n.Kind switch
    {
        NotificationKind.NewComment => n.PullRequest.LastCommentAuthor,
        NotificationKind.Merged => n.PullRequest.MergedBy ?? n.PullRequest.Author,
        NotificationKind.UnassignedFromMe => n.PullRequest.Assignees.FirstOrDefault(),
        _ => n.PullRequest.Author,
    };

    /// <summary>Raised for every notification, whichever way it ends up being displayed (used for the widget badge).</summary>
    public event Action<ToastRequest>? Published;

    public async Task PublishAsync(ToastRequest request, CancellationToken ct = default)
    {
        Published?.Invoke(request);
        if (settings.Current.NotificationStyle == NotificationStyle.Native)
        {
            try
            {
                await native.ShowAsync(request.Title, request.Message, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Native delivery is best-effort; fall back so the event is not lost.
            }
        }

        toasts.Show(request);
    }
}
