using GitDash.Domain.PullRequests;

namespace GitDash.Domain.Insights;

public sealed record DashboardSummary(int TotalOpen, int AssignedToMe, int NeedsMyApproval)
{
    public static readonly DashboardSummary Empty = new(0, 0, 0);

    /// <summary>
    /// The same repository can be watched through two accounts, so counts are taken over distinct PRs.
    /// </summary>
    public static DashboardSummary From(IEnumerable<PullRequest> open)
    {
        var all = open.ToList();
        return new DashboardSummary(
            all.Select(p => p.Id).Distinct().Count(),
            all.Where(p => p.IsAssignedToViewer).Select(p => p.Id).Distinct().Count(),
            all.Where(p => p.NeedsViewerApproval).Select(p => p.Id).Distinct().Count());
    }
}
