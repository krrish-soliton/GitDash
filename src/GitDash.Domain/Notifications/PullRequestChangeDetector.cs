using GitDash.Domain.PullRequests;

namespace GitDash.Domain.Notifications;

/// <summary>
/// Compares two snapshots of the same repository and works out what the user should be told.
/// Pure function: no I/O, no clock.
/// </summary>
public static class PullRequestChangeDetector
{
    public static IReadOnlyList<PullRequestNotification> Detect(
        RepositorySnapshot? previous,
        RepositorySnapshot current,
        NotificationRules rules,
        DateTimeOffset now)
    {
        // The first snapshot is the baseline; announcing everything already open would be noise.
        if (previous is null) return [];

        var before = previous.Open.ToDictionary(p => p.Id);
        var result = new List<PullRequestNotification>();

        foreach (var pr in current.Open)
        {
            before.TryGetValue(pr.Id, out var old);
            var label = $"{pr.Repository.Name} #{pr.Number}";

            if (rules.AssignedToMe && pr.IsAssignedToViewer && old is not { IsAssignedToViewer: true })
            {
                result.Add(new(NotificationKind.AssignedToMe, pr, $"Assigned to you · {label}", pr.Title, now));
            }

            if (rules.ReviewRequested && pr.NeedsViewerApproval && old is not { NeedsViewerApproval: true })
            {
                var title = pr.IsReapproval ? "Re-review requested" : "Review requested";
                result.Add(new(NotificationKind.ReviewRequested, pr, $"{title} · {label}", pr.Title, now));
            }

            if (rules.NewComment && old is not null && pr.IsViewerResponsible &&
                pr.TotalComments > old.TotalComments && !Login.Same(pr.LastCommentAuthor, pr.ViewerLogin))
            {
                var count = pr.TotalComments - old.TotalComments;
                var who = pr.LastCommentAuthor ?? "Someone";
                var text = count == 1 ? $"{who} commented" : $"{count} new comments, latest by {who}";
                result.Add(new(NotificationKind.NewComment, pr, $"{text} · {label}", pr.Title, now));
            }
        }

        if (rules.UnassignedFromMe)
        {
            // Still open, was assigned to the viewer, and no longer is: somebody took the assignment away.
            foreach (var pr in current.Open)
            {
                if (!before.TryGetValue(pr.Id, out var old) || !old.IsAssignedToViewer || pr.IsAssignedToViewer) continue;
                var others = pr.Assignees.Count == 0 ? "no one is assigned now" : $"now assigned to {string.Join(", ", pr.Assignees)}";
                result.Add(new(NotificationKind.UnassignedFromMe, pr,
                    $"Removed as assignee · {pr.Repository.Name} #{pr.Number}", $"{pr.Title} ({others})", now));
            }
        }

        if (rules.Merged)
        {
            foreach (var pr in current.RecentlyMerged)
            {
                if (!before.ContainsKey(pr.Id)) continue; // was not open last time we looked
                var by = pr.MergedBy is null ? string.Empty : $" by {pr.MergedBy}";
                result.Add(new(NotificationKind.Merged, pr,
                    $"Merged{by} · {pr.Repository.Name} #{pr.Number}", pr.Title, now));
            }
        }

        return result;
    }
}
