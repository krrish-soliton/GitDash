using GitDash.Application.Abstractions;
using GitDash.Domain.Actions;
using GitDash.Domain.Insights;
using GitDash.Domain.PullRequests;

namespace GitDash.Infrastructure.Demo;

/// <summary>
/// Offline stand-in for GitHub used by <c>--demo</c>. Produces believable data that changes a little
/// on every sync so notifications, counters and animations can be seen without any account.
/// </summary>
public sealed class DemoGitHubGateway(TimeProvider time, int changeEveryProbes = 3) : IGitHubGateway
{
    public const string Viewer = "octo-dev";
    private static readonly string[] Team = ["maya-r", "leo-chen", "sam-ortiz", "priya-n", "tomas-k", "ines-b", "jon-park"];

    public static readonly RepositoryRef[] Monitored =
    [
        new("acme-labs", "orbit-api"),
        new("acme-labs", "orbit-web"),
        new(Viewer, "pocket-tools"),
    ];

    private readonly Dictionary<string, int> _syncCount = [];
    private readonly Dictionary<string, int> _probeCount = [];

    /// <summary>Pretends that a repository changes every few probes, so the demo visibly updates live.</summary>
    public Task<RepositoryProbe> ProbeAsync(string token, RepositoryRef repository, string? etag, CancellationToken ct)
    {
        int count;
        lock (_probeCount)
        {
            count = _probeCount.GetValueOrDefault(repository.FullName) + 1;
            _probeCount[repository.FullName] = count;
        }

        var version = count / Math.Max(1, changeEveryProbes);
        var fresh = $"demo-{version}";
        return Task.FromResult(new RepositoryProbe(fresh != etag, fresh));
    }

    public Task<GitHubUser> GetViewerAsync(string token, CancellationToken ct) =>
        Task.FromResult(new GitHubUser(Viewer, "Demo User"));

    public Task<GitHubProfile> GetProfileAsync(string token, CancellationToken ct) =>
        Task.FromResult(new GitHubProfile
        {
            Login = Viewer,
            Name = "Demo User",
            Bio = "Builds things with .NET. This profile is sample data.",
            Company = "Acme Labs",
            Location = "Chennai, India",
            Url = $"https://github.com/{Viewer}",
            JoinedAt = time.GetUtcNow().AddYears(-6),
            Followers = 128,
            Following = 54,
            Repositories = 37,
            OpenPullRequests = 4,
            MergedPullRequests = 412,
            TotalPullRequests = 451,
            ContributionsThisYear = 1284,
            Organizations = ["acme-labs", "open-widgets"],
        });

    public Task<IReadOnlyList<RepositoryInfo>> ListRepositoriesAsync(string token, CancellationToken ct)
    {
        IReadOnlyList<RepositoryInfo> repos =
        [
            new(Monitored[0], true, true, "Core services"),
            new(Monitored[1], true, true, "Web client"),
            new(new("acme-labs", "orbit-infra"), true, true, "Deployment and pipelines"),
            new(new("acme-labs", "design-tokens"), false, true, "Shared design tokens"),
            new(new("open-widgets", "charts"), false, true, "Charting components"),
            new(Monitored[2], false, false, "Small utilities"),
            new(new(Viewer, "dotfiles"), true, false, null),
        ];
        return Task.FromResult(repos);
    }

    public Task<RepositorySnapshot> GetSnapshotAsync(
        string token, Guid accountId, string viewerLogin, RepositoryRef repository, CancellationToken ct)
    {
        int sync;
        lock (_syncCount)
        {
            sync = _syncCount.GetValueOrDefault(repository.FullName);
            _syncCount[repository.FullName] = sync + 1;
        }

        var now = time.GetUtcNow();
        var index = Array.FindIndex(Monitored, r => r.Matches(repository));
        var count = index switch { 0 => 7, 1 => 5, 2 => 2, _ => 3 };
        var open = new List<PullRequest>();
        var merged = new List<PullRequest>();

        for (var i = 0; i < count; i++)
        {
            var seed = (index + 2) * 31 + i * 7;
            var author = i % 4 == 3 ? viewerLogin : Team[seed % Team.Length];
            var reviewer = Team[(seed + 3) % Team.Length];
            var mine = i % 3 == 0 && !Login.Same(author, viewerLogin);
            var reapproval = mine && i % 2 == 0;
            var created = now - TimeSpan.FromHours(5 + seed % 190);
            var arrived = created + TimeSpan.FromHours(1 + seed % 4);
            var reviews = new List<Review>();
            if (reapproval) reviews.Add(new(viewerLogin, ReviewState.ChangesRequested, arrived + TimeSpan.FromHours(1), 3));
            if (i % 5 == 1) reviews.Add(new(reviewer, ReviewState.Approved, created + TimeSpan.FromHours(3), 1));

            // One of the viewer's own pull requests collects a new comment on every sync.
            var extraComments = index == 0 && i == 3 ? sync : 0;
            var pr = new PullRequest
            {
                Id = $"demo-{index}-{i}",
                AccountId = accountId,
                ViewerLogin = viewerLogin,
                Repository = repository,
                Number = 90 + seed,
                Title = Titles[seed % Titles.Length],
                Url = $"https://github.com/{repository.FullName}/pull/{90 + seed}",
                Author = author,
                IsDraft = i % 6 == 4,
                HasMergeConflict = seed % 11 == 3,
                PipelineFailed = seed % 7 == 2,
                CreatedAt = created,
                UpdatedAt = now - TimeSpan.FromMinutes(seed % 300),
                Assignees = mine && i == 0 ? [viewerLogin] : i % 2 == 0 ? [reviewer] : [],
                RequestedReviewers = mine ? [viewerLogin, reviewer] : [reviewer],
                Reviews = reviews,
                TotalComments = seed % 14 + extraComments + reviews.Sum(r => r.CommentCount),
                CommentsByViewer = reapproval ? 3 + seed % 3 : 0,
                LastCommentAuthor = Team[(seed + 1) % Team.Length],
                LastCommentAt = now - TimeSpan.FromMinutes(seed % 90),
                CommitCount = 1 + seed % 9,
                // Some approvals and the viewer's earlier review predate the newest commit, so they show as stale.
                LastCommitAt = reapproval ? arrived + TimeSpan.FromHours(3) : i % 10 == 1 ? created + TimeSpan.FromHours(5) : created + TimeSpan.FromHours(2),
                Additions = 20 + seed * 13 % 700,
                Deletions = seed * 5 % 240,
                ChangedFiles = 1 + seed % 17,
                Summary = Summaries[seed % Summaries.Length],
                AssignedToViewerAt = mine && i == 0 ? arrived : null,
                ReviewRequestedFromViewerAt = mine ? (reapproval ? now - TimeSpan.FromHours(2 + i) : arrived) : null,
            };

            // After a couple of syncs one pull request gets merged.
            if (index == 1 && i == 1 && sync >= 2)
            {
                merged.Add(pr with { State = PullRequestState.Merged, MergedAt = now, MergedBy = reviewer });
            }
            else
            {
                open.Add(pr);
            }
        }

        // And a brand-new one lands on the viewer's desk.
        if (index == 0 && sync >= 1)
        {
            open.Add(new PullRequest
            {
                Id = "demo-new",
                AccountId = accountId,
                ViewerLogin = viewerLogin,
                Repository = repository,
                Number = 412,
                Title = "Retry webhook deliveries with exponential backoff",
                Url = $"https://github.com/{repository.FullName}/pull/412",
                Author = "priya-n",
                CreatedAt = now - TimeSpan.FromMinutes(12),
                UpdatedAt = now,
                Assignees = [viewerLogin],
                RequestedReviewers = [viewerLogin],
                CommitCount = 3,
                Additions = 184,
                Deletions = 22,
                ChangedFiles = 6,
                Summary = "Adds a delivery queue with jittered exponential backoff and a dead-letter table.",
                AssignedToViewerAt = now - TimeSpan.FromMinutes(10),
                ReviewRequestedFromViewerAt = now - TimeSpan.FromMinutes(10),
            });
        }

        return Task.FromResult(new RepositorySnapshot(accountId, repository, open, merged, open.Count));
    }

    public Task<PersonActivity> GetPersonActivityAsync(
        string token, RepositoryRef repository, string login, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var seed = Math.Abs(login.GetHashCode()) % 7;
        PersonPullRequest Pr(int n, string title, string state, int daysAgo) => new(
            n, title, $"https://github.com/{repository.FullName}/pull/{n}", state, false,
            now.AddDays(-daysAgo), state == "OPEN" ? null : now.AddDays(-daysAgo + 1));

        PersonPullRequest[] recent =
        [
            Pr(140 + seed, "Add retry policy to the sync client", "OPEN", 1),
            Pr(131 + seed, "Fix flaky cache invalidation", "MERGED", 4),
            Pr(122 + seed, "Refactor settings loader", "MERGED", 9),
            Pr(118 + seed, "Experiment: parallel snapshot fetch", "CLOSED", 15),
        ];
        var events = new List<PersonEvent>
        {
            new(now.AddHours(-3), PersonEventKind.Comment, 150, "Speed up startup", "Could we lazy-load this instead?", recent[0].Url),
            new(now.AddHours(-9), PersonEventKind.Review, 151, "Update dependencies", "Approved: looks good to me", recent[0].Url),
            new(now.AddDays(-2), PersonEventKind.Review, 149, "Rework toast layout", "Requested changes: spacing is off on high-DPI", recent[0].Url),
            new(now.AddDays(-3), PersonEventKind.Comment, 147, "Add dark theme", "Nit: use the shared brush here", recent[0].Url),
        };
        events.AddRange(recent.Select(p => new PersonEvent(p.CreatedAt, PersonEventKind.Opened, p.Number, p.Title, "Opened the pull request", p.Url)));

        return Task.FromResult(new PersonActivity
        {
            Login = login,
            Repository = repository.FullName,
            Raised = 24 + seed * 3,
            Open = 2 + seed % 3,
            Merged = 18 + seed * 2,
            Closed = 3 + seed % 4,
            Draft = seed % 2,
            PullRequestsCommentedOn = 40 + seed * 5,
            PullRequestsReviewed = 31 + seed * 4,
            PendingBranches = seed % 3 == 0 ? Array.Empty<PendingBranch>() : new[]
            {
                new PendingBranch("feature/sync-retry", DateTimeOffset.UtcNow.AddDays(-2), true),
                new PendingBranch("spike/parallel-fetch", DateTimeOffset.UtcNow.AddDays(-5), false),
                new PendingBranch("old/experiment", DateTimeOffset.UtcNow.AddDays(-40), false),
            }[..(1 + seed % 3)],
            RecentPullRequests = recent,
            Events = [.. events.OrderByDescending(e => e.At)],
        });
    }

    public Task<IReadOnlyList<WorkflowRun>> GetWorkflowRunsAsync(string token, RepositoryRef repository, CancellationToken ct)
    {
        // A run that is always partway through (it loops once a minute), one that passed and one that failed.
        var now = time.GetUtcNow();
        var phase = now.Second / 60d;
        var total = 20;
        IReadOnlyList<WorkflowRun> runs =
        [
            new(900_001, repository, "CI build and test", "feature/timers", Team[0], RunStatus.InProgress, null,
                now.AddSeconds(-now.Second), now, $"https://github.com/{repository.FullName}/actions/runs/900001", [DemoNumber(repository, 0)],
                Math.Max(1, (int)(phase * total)), total),
            new(900_002, repository, "Lint", "feature/api-cache", Team[1], RunStatus.Completed, "success",
                now.AddMinutes(-9), now.AddMinutes(-8), $"https://github.com/{repository.FullName}/actions/runs/900002", [DemoNumber(repository, 1)]),
            new(900_003, repository, "CI build and test", "fix/null-check", Team[2], RunStatus.Completed, "failure",
                now.AddMinutes(-25), now.AddMinutes(-22), $"https://github.com/{repository.FullName}/actions/runs/900003", [DemoNumber(repository, 2)]),
        ];
        return Task.FromResult(runs);
    }

    public Task ApproveAsync(string token, RepositoryRef repository, int number, CancellationToken ct) => Task.CompletedTask;

    public Task SwapAssigneeAsync(
        string token, RepositoryRef repository, int number, string remove, string add, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<PipelineRunCounts> GetPipelineRunCountsAsync(
        string token, RepositoryRef repository, string login, DateTimeOffset weekStart, DateTimeOffset monthStart,
        CancellationToken ct)
    {
        var seed = Math.Abs(login.GetHashCode()) % 7;
        return Task.FromResult(new PipelineRunCounts(6 + seed * 2, 24 + seed * 9, 310 + seed * 85));
    }

    private static int DemoNumber(RepositoryRef repository, int i)
    {
        var index = Array.FindIndex(Monitored, r => r.Matches(repository));
        return 90 + (index + 2) * 31 + i * 7;
    }

    public Task<PullRequestDetails> GetDetailsAsync(
        string token, RepositoryRef repository, int number, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var author = Team[number % Team.Length];
        IReadOnlyList<TimelineEntry> entries =
        [
            new(now.AddHours(-52), TimelineEntryKind.Opened, author, "Opened the pull request"),
            new(now.AddHours(-52), TimelineEntryKind.Commit, author, "Add first implementation"),
            new(now.AddHours(-51), TimelineEntryKind.ReviewRequested, author, $"Requested a review from {Viewer}"),
            new(now.AddHours(-47), TimelineEntryKind.Comment, "leo-chen", "Could we cover the timeout path with a test as well?"),
            new(now.AddHours(-30), TimelineEntryKind.ChangesRequested, Viewer, "Requested changes (3 inline comments): naming and a missing null check"),
            new(now.AddHours(-26), TimelineEntryKind.Commit, author, "Address review comments"),
            new(now.AddHours(-26), TimelineEntryKind.ForcePushed, author, "Force-pushed the branch"),
            new(now.AddHours(-25), TimelineEntryKind.Comment, author, "Thanks — both fixed, and the timeout test is in."),
            new(now.AddHours(-3), TimelineEntryKind.ReviewRequested, author, $"Requested a review from {Viewer}"),
            new(now.AddHours(-1), TimelineEntryKind.Approved, "sam-ortiz", "Approved"),
        ];
        IReadOnlyList<PullRequestComment> comments =
        [
            new(now.AddHours(-47), "leo-chen", "Could we cover the **timeout path** with a test as well?", CommentSource.Conversation),
            new(now.AddHours(-46), "copilot-pull-request-reviewer", "### Summary\nThe change looks safe. Two suggestions:\n\n- Consider `ConfigureAwait(false)` in `RefreshAsync`.\n- The cache key ignores the tenant region.", CommentSource.Review),
            new(now.AddHours(-30), Viewer, "Naming and a missing null check — details inline.", CommentSource.Review),
            new(now.AddHours(-30), Viewer, "This can be `null` when the tenant has no configuration yet.", CommentSource.Inline, "src/Config/TenantCache.cs"),
            new(now.AddHours(-30), Viewer, "Consider `TryGetTenant` to match the rest of the class:\n\n```csharp\nif (!cache.TryGetTenant(id, out var tenant)) return NotFound();\n```", CommentSource.Inline, "src/Config/TenantCache.cs"),
            new(now.AddHours(-25), author, $"Thanks @{Viewer} — both fixed, and the timeout test is in. Could you take another look?", CommentSource.Conversation),
            new(now.AddHours(-1), "sam-ortiz", "Looks good to me.", CommentSource.Review),
        ];
        var description =
            "## What\n" + Summaries[number % Summaries.Length] + "\n\n" +
            "## Why\nRequested by the *platform team* after the last incident review. See [the write-up](https://github.com/acme-labs/orbit-api/issues/1).\n\n" +
            "## How to test\n1. Run the integration suite.\n2. Exercise the new path with the sample tenant.\n\n" +
            "- [x] Unit tests\n- [ ] Load test\n\n> Note: no schema changes.\n\n```bash\ndotnet test --filter Category=Integration\n```";
        return Task.FromResult(new PullRequestDetails(description, entries, comments));
    }

    private static readonly string[] Titles =
    [
        "Cache tenant configuration lookups",
        "Fix race in session refresh",
        "Move report export to a background job",
        "Add audit trail for role changes",
        "Upgrade to .NET 10 and trim dependencies",
        "Paginate the activity feed",
        "Validate webhook signatures",
        "Dark theme for the settings screens",
        "Reduce allocations in the JSON pipeline",
        "Add health checks for downstream services",
        "Refactor device registration flow",
    ];

    private static readonly string[] Summaries =
    [
        "Introduces a small in-memory cache with a five minute sliding expiry and invalidation on update.",
        "Guards the refresh path with a semaphore so two requests can no longer rotate the same token.",
        "Splits the long-running export out of the request path. Progress is reported through the existing job table.",
        "Records who changed what and when. No behaviour change for existing endpoints.",
        "Mostly mechanical. A couple of analyzers needed fixes; see the commit list for details.",
    ];
}
