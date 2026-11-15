using GitDash.Application.Abstractions;
using GitDash.Domain.Actions;
using GitDash.Domain.PullRequests;

namespace GitDash.Application.Monitoring;

/// <summary>
/// Tells the user when a workflow run fails for one of their pull requests (authored by them or assigned to them).
/// Clicking the notification opens that pull request on its Actions tab.
/// </summary>
public sealed class PipelineFailureNotifier
{
    private readonly PullRequestMonitor _pulls;
    private readonly NotificationRouter _router;

    public PipelineFailureNotifier(ActionsMonitor actions, PullRequestMonitor pulls, NotificationRouter router)
    {
        _pulls = pulls;
        _router = router;
        actions.RunFailed += OnFailed;
    }

    private void OnFailed(WorkflowRun run)
    {
        if (RequestFor(run, _pulls.State.Open) is { } request) _ = _router.PublishAsync(request);
    }

    /// <summary>The notification for a failed run, or null when it is not about one of the viewer's pull requests.</summary>
    public static ToastRequest? RequestFor(WorkflowRun run, IReadOnlyList<PullRequest> open)
    {
        var pr = open.FirstOrDefault(p =>
            p.Repository.FullName.Equals(run.Repository.FullName, StringComparison.OrdinalIgnoreCase) &&
            run.PullRequests.Contains(p.Number) && (p.IsAuthoredByViewer || p.IsAssignedToViewer));
        if (pr is null) return null;

        var detail = string.Join('\n',
            $"Workflow: {run.Name}",
            $"Repository: {pr.Repository.FullName}",
            $"Pull request: #{pr.Number} {pr.Title}",
            $"Branch: {run.Branch}",
            $"Run by: {run.Actor}",
            $"Finished: {run.UpdatedAt.ToLocalTime():d MMM yyyy, HH:mm}",
            $"Result: {(string.IsNullOrEmpty(run.Conclusion) ? "failed" : run.Conclusion.Replace('_', ' '))}",
            $"Run: {run.Url}");
        return new ToastRequest(
            $"Pipeline failed: {run.Name}", $"{pr.Title} (#{pr.Number}) in {pr.Repository.Name}", ToastTone.Failed,
            pr.Url, run.Actor, detail, ToastTarget.PullRequestActions);
    }
}
