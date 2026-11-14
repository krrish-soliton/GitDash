using System.Text.Json;
using GitDash.Domain.Actions;
using GitDash.Domain.PullRequests;

namespace GitDash.Infrastructure.GitHub;

/// <summary>Turns GitHub's Actions REST answers into <see cref="WorkflowRun"/>s.</summary>
public static class WorkflowRunMapper
{
    public static IReadOnlyList<WorkflowRun> MapRuns(JsonElement root, RepositoryRef repository)
    {
        var runs = new List<WorkflowRun>();
        foreach (var run in root.Items("workflow_runs"))
        {
            var id = run.Prop("id") is { ValueKind: JsonValueKind.Number } n && n.TryGetInt64(out var value) ? value : 0;
            if (id == 0) continue;

            var created = run.Date("created_at") ?? DateTimeOffset.MinValue;
            runs.Add(new WorkflowRun(
                id,
                repository,
                run.Str("name", run.Str("display_title", "Workflow")),
                run.Str("head_branch"),
                run.Prop("actor").Str("login", "unknown"),
                run.Str("status") switch
                {
                    "completed" => RunStatus.Completed,
                    "in_progress" => RunStatus.InProgress,
                    _ => RunStatus.Queued, // queued, waiting, pending, requested
                },
                ((JsonElement?)run).StrOrNull("conclusion"),
                run.Date("run_started_at") ?? created,
                run.Date("updated_at") ?? created,
                run.Str("html_url"),
                [.. run.Items("pull_requests").Select(p => p.Int("number")).Where(num => num > 0)]));
        }

        return runs;
    }

    /// <summary>Counts the steps of all jobs of a run: finished steps out of all steps.</summary>
    public static WorkflowRun WithSteps(WorkflowRun run, JsonElement jobsRoot)
    {
        int done = 0, total = 0;
        foreach (var job in jobsRoot.Items("jobs"))
        {
            foreach (var step in job.Items("steps"))
            {
                total++;
                if (step.Str("status") == "completed") done++;
            }
        }

        return run with { StepsDone = done, StepsTotal = total };
    }
}
