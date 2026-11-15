using GitDash.Application.Abstractions;
using GitDash.Application.Monitoring;
using GitDash.Domain.Actions;
using GitDash.Domain.PullRequests;

namespace GitDash.Tests;

public class PipelineFailureTests
{
    private static readonly RepositoryRef Repo = new("o", "r");

    private static WorkflowRun Run(params int[] pulls) => new(
        1, Repo, "CI", "feature/x", "maya", RunStatus.Completed, "failure", DateTimeOffset.UtcNow.AddMinutes(-3),
        DateTimeOffset.UtcNow, "https://github.com/o/r/actions/runs/1", pulls);

    private static PullRequest Pr(int number, string author, params string[] assignees) => new()
    {
        Id = number.ToString(), AccountId = Guid.NewGuid(), ViewerLogin = "krish", Repository = Repo, Number = number,
        Title = "Fix it", Url = $"https://github.com/o/r/pull/{number}", Author = author, Assignees = assignees,
    };

    [Fact]
    public void Failed_run_of_my_pull_request_opens_its_actions_tab()
    {
        var request = PipelineFailureNotifier.RequestFor(Run(7), [Pr(7, "krish")]);

        Assert.NotNull(request);
        Assert.Equal(ToastTone.Failed, request.Tone);
        Assert.Equal(ToastTarget.PullRequestActions, request.Target);
        Assert.Equal("https://github.com/o/r/pull/7", request.Url);
        Assert.Contains("Workflow: CI", request.Detail);
    }

    [Fact]
    public void Assigned_pull_requests_count_too()
    {
        Assert.NotNull(PipelineFailureNotifier.RequestFor(Run(7), [Pr(7, "maya", "krish")]));
    }

    [Fact]
    public void Other_peoples_pull_requests_and_unknown_runs_are_ignored()
    {
        Assert.Null(PipelineFailureNotifier.RequestFor(Run(7), [Pr(7, "maya")]));
        Assert.Null(PipelineFailureNotifier.RequestFor(Run(8), [Pr(7, "krish")]));
        Assert.Null(PipelineFailureNotifier.RequestFor(Run(), [Pr(7, "krish")]));
    }
}
