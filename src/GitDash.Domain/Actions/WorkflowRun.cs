using GitDash.Domain.PullRequests;

namespace GitDash.Domain.Actions;

public enum RunStatus
{
    Queued,
    InProgress,
    Completed,
}

/// <summary>One GitHub Actions workflow run.</summary>
public sealed record WorkflowRun(
    long Id,
    RepositoryRef Repository,
    string Name,
    string Branch,
    string Actor,
    RunStatus Status,
    string? Conclusion,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    string Url,
    IReadOnlyList<int> PullRequests,
    int StepsDone = 0,
    int StepsTotal = 0)
{
    public bool IsActive => Status != RunStatus.Completed;

    public bool Succeeded => Status == RunStatus.Completed && Conclusion == "success";

    public bool Failed => Status == RunStatus.Completed && Conclusion is "failure" or "timed_out" or "startup_failure";

    /// <summary>How far the run is, 0 to 1: finished steps out of all steps its jobs have. A finished run is complete.</summary>
    public double Progress => Status == RunStatus.Completed ? 1
        : StepsTotal <= 0 ? 0
        : Math.Clamp(StepsDone / (double)StepsTotal, 0, 0.99);

    public bool BelongsTo(RepositoryRef repository, int number) =>
        Repository.FullName.Equals(repository.FullName, StringComparison.OrdinalIgnoreCase) && PullRequests.Contains(number);
}
