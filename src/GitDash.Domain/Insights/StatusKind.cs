using GitDash.Domain.PullRequests;

namespace GitDash.Domain.Insights;

/// <summary>The counters the floating widget and the island can show, in whatever order the user picks.</summary>
public enum StatusKind
{
    /// <summary>Pull requests assigned to me.</summary>
    Assigned,
    /// <summary>Pull requests waiting for my approval.</summary>
    Approval,
    /// <summary>Pull requests I raised that are still open.</summary>
    Raised,
    /// <summary>Pull requests I raised that have been approved.</summary>
    Approved,
    /// <summary>Unresolved review conversations on the pull requests I raised or am assigned to.</summary>
    Comments,
    /// <summary>My approved pull requests with nothing in the way of merging.</summary>
    ReadyToMerge,
    /// <summary>My pull requests with a merge conflict or a failed pipeline.</summary>
    Error,
    /// <summary>Every open pull request.</summary>
    Open,
}

public static class StatusCounter
{
    public static IReadOnlyList<StatusKind> All { get; } = Enum.GetValues<StatusKind>();

    public static string Label(StatusKind kind) => kind switch
    {
        StatusKind.Assigned => "assigned",
        StatusKind.Approval => "to approve",
        StatusKind.Raised => "raised",
        StatusKind.Approved => "approved",
        StatusKind.Comments => "unresolved",
        StatusKind.ReadyToMerge => "ready to merge",
        StatusKind.Error => "with errors",
        _ => "open",
    };

    /// <summary>The name shown in settings.</summary>
    public static string Title(StatusKind kind) => kind switch
    {
        StatusKind.Assigned => "Assigned to me",
        StatusKind.Approval => "Waiting for my approval",
        StatusKind.Raised => "Raised by me",
        StatusKind.Approved => "My PRs approved",
        StatusKind.Comments => "Comments (unresolved only)",
        StatusKind.ReadyToMerge => "Ready to merge",
        StatusKind.Error => "Errors (conflict or failed pipeline)",
        _ => "All open pull requests",
    };

    public static string Accent(StatusKind kind) => kind switch
    {
        StatusKind.Assigned => "#22D3EE",
        StatusKind.Approval => "#FBBF24",
        StatusKind.Raised => "#A78BFA",
        StatusKind.Approved => "#34D399",
        StatusKind.Comments => "#F472B6",
        StatusKind.ReadyToMerge => "#4ADE80",
        StatusKind.Error => "#F87171",
        _ => "#E8EAF2",
    };

    public static int Count(StatusKind kind, IEnumerable<PullRequest> open)
    {
        var prs = open.Where(p => p.State == PullRequestState.Open);
        return kind switch
        {
            StatusKind.Assigned => prs.Count(p => p.IsAssignedToViewer),
            StatusKind.Approval => prs.Count(p => p.NeedsViewerApproval),
            StatusKind.Raised => prs.Count(p => p.IsAuthoredByViewer),
            StatusKind.Approved => prs.Count(p => p.IsAuthoredByViewer && p.Verdict == ReviewState.Approved),
            StatusKind.Comments => prs.Where(p => p.IsAuthoredByViewer || p.IsAssignedToViewer).Sum(p => p.UnresolvedThreads),
            StatusKind.ReadyToMerge => prs.Count(IsReadyToMerge),
            StatusKind.Error => prs.Count(p => (p.IsAuthoredByViewer || p.IsAssignedToViewer) && p.HasError),
            _ => prs.Count(),
        };
    }

    private static bool IsReadyToMerge(PullRequest p) =>
        p.IsAuthoredByViewer && !p.IsDraft && !p.HasError && p.Verdict == ReviewState.Approved && !p.HasStaleApproval;
}
