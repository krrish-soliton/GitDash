using System.Text.Json;
using GitDash.Domain.Insights;
using GitDash.Application.Settings;
using GitDash.Domain.PullRequests;
using GitDash.Infrastructure.GitHub;
using GitDash.Infrastructure.Security;
using GitDash.Infrastructure.Storage;
using static GitDash.Tests.TestData;

namespace GitDash.Tests;

public class PullRequestMapperTests
{
    private const string SnapshotJson = """
        {"repository":{
          "open":{"totalCount":61,"nodes":[{
            "id":"PR_1","number":42,"title":"Add  thing","url":"https://github.com/acme/api/pull/42",
            "isDraft":false,"state":"OPEN","createdAt":"2026-11-10T08:00:00Z","updatedAt":"2026-11-12T08:00:00Z",
            "mergedAt":null,"bodyText":"First line\n\n   second   line","additions":10,"deletions":2,"changedFiles":3,
            "author":null,"mergedBy":null,
            "assignees":{"nodes":[{"login":"Krish","avatarUrl":"https://avatars.githubusercontent.com/u/1?s=96"}]},
            "reviewRequests":{"nodes":[
              {"requestedReviewer":{"__typename":"User","login":"krish"}},
              {"requestedReviewer":{"__typename":"Team"}},
              {"requestedReviewer":null}]},
            "reviews":{"nodes":[
              {"author":{"login":"krish"},"state":"CHANGES_REQUESTED","submittedAt":"2026-11-11T09:00:00Z","comments":{"totalCount":3}},
              {"author":{"login":"leo"},"state":"APPROVED","submittedAt":"2026-11-11T10:00:00Z","comments":{"totalCount":0}}]},
            "comments":{"totalCount":4,"nodes":[
              {"author":{"login":"krish"},"createdAt":"2026-11-11T07:00:00Z"},
              {"author":{"login":"maya"},"createdAt":"2026-11-12T07:00:00Z"}]},
            "commits":{"totalCount":5},
            "timelineItems":{"nodes":[
              {"__typename":"AssignedEvent","createdAt":"2026-11-10T09:00:00Z","assignee":{"login":"krish"}},
              {"__typename":"ReviewRequestedEvent","createdAt":"2026-11-10T10:00:00Z","requestedReviewer":{"login":"krish"}},
              {"__typename":"ReviewRequestedEvent","createdAt":"2026-11-12T06:00:00Z","requestedReviewer":{"login":"krish"}},
              {"__typename":"ReviewRequestedEvent","createdAt":"2026-11-12T07:30:00Z","requestedReviewer":{"login":"leo"}}]}
          }]},
          "merged":{"nodes":[]}}}
        """;

    [Fact]
    public void Maps_a_pull_request_with_nulls_and_viewer_specific_fields()
    {
        using var doc = JsonDocument.Parse(SnapshotJson);
        var snapshot = PullRequestMapper.MapSnapshot(doc.RootElement, Account, Me, Repo);

        Assert.Equal(61, snapshot.OpenTotalCount);
        var pr = Assert.Single(snapshot.Open);
        Assert.Equal("ghost", pr.Author);
        Assert.Equal("First line second line", pr.Summary);
        Assert.Single(pr.RequestedReviewers);
        Assert.True(pr.IsAssignedToViewer);
        Assert.True(pr.IsReapproval);
        Assert.Equal(7, pr.TotalComments);     // 4 conversation + 3 inline
        Assert.Equal(4, pr.CommentsByViewer);  // 1 conversation + 3 inline
        Assert.Equal("maya", pr.LastCommentAuthor);
        Assert.Equal(5, pr.CommitCount);
        Assert.Equal(DateTimeOffset.Parse("2026-11-10T09:00:00Z"), pr.AssignedToViewerAt);
        Assert.Equal(DateTimeOffset.Parse("2026-11-12T06:00:00Z"), pr.ArrivedAt);
        Assert.Equal("https://avatars.githubusercontent.com/u/1?s=96", snapshot.Avatars["krish"]);
    }

    [Fact]
    public void Maps_details_with_timeline_comments_and_description()
    {
        const string json = """
            {"repository":{"pullRequest":{"createdAt":"2026-11-10T08:00:00Z","body":"  Adds a cache.  ","author":{"login":"maya"},
              "timelineItems":{"nodes":[
                {"__typename":"MergedEvent","createdAt":"2026-11-12T08:00:00Z","actor":{"login":"leo"}},
                {"__typename":"PullRequestReview","submittedAt":"2026-11-11T08:00:00Z","author":{"login":"krish"},"state":"APPROVED","bodyText":"Nice","body":"Nice","comments":{"totalCount":2,"nodes":[
                  {"createdAt":"2026-11-11T07:50:00Z","path":"src/A.cs","body":"Rename this","author":{"login":"krish"}},
                  {"createdAt":"2026-11-11T07:55:00Z","path":"src/B.cs","body":"And this","author":{"login":"krish"}}]}},
                {"__typename":"IssueComment","createdAt":"2026-11-10T09:00:00Z","author":{"login":"leo"},"body":"ping @Krish please look"},
                {"__typename":"ReviewDismissedEvent","createdAt":"2026-11-11T09:00:00Z","actor":{"login":"maya"},"review":{"author":{"login":"sam"}}},
                {"__typename":"PullRequestCommit","commit":{"committedDate":"2026-11-10T08:30:00Z","messageHeadline":"Fix","author":{"name":"Maya R","user":null}}},
                {"__typename":"SomethingNew"}]}}}}
            """;
        using var doc = JsonDocument.Parse(json);
        var details = PullRequestMapper.MapDetails(doc.RootElement);

        Assert.Equal("Adds a cache.", details.Description);
        Assert.Equal(
            new[]
            {
                TimelineEntryKind.Opened, TimelineEntryKind.Commit, TimelineEntryKind.Comment,
                TimelineEntryKind.Approved, TimelineEntryKind.ReviewDismissed, TimelineEntryKind.Merged,
            },
            details.Timeline.Select(t => t.Kind).ToArray());
        Assert.Equal("Maya R", details.Timeline[1].Actor);
        Assert.Equal("Approved (2 inline comments): Nice", details.Timeline[3].Text);
        Assert.Equal(TimelineCategory.Reviews, details.Timeline[4].Category);

        Assert.Equal(4, details.Comments.Count);
        Assert.Equal(CommentSource.Conversation, details.Comments[0].Source);
        Assert.True(details.Comments[0].Mentions("krish"));
        Assert.False(details.Comments[0].Mentions("kris"));
        Assert.Equal("src/A.cs", details.Comments[1].Path);
        Assert.Equal(CommentSource.Review, details.Comments[3].Source);
    }
}

public class SecurityTests
{
    [Fact]
    public void Only_https_github_links_may_be_opened()
    {
        Assert.True(GitHubUrlLauncher.IsAllowed("https://github.com/acme/api/pull/1"));
        Assert.True(GitHubUrlLauncher.IsAllowed("https://gist.github.com/x"));
        Assert.False(GitHubUrlLauncher.IsAllowed("http://github.com/acme/api"));
        Assert.False(GitHubUrlLauncher.IsAllowed("https://github.com.evil.example/x"));
        Assert.False(GitHubUrlLauncher.IsAllowed("https://evilgithub.com/x"));
        Assert.False(GitHubUrlLauncher.IsAllowed("https://user:pw@github.com/x"));
        Assert.False(GitHubUrlLauncher.IsAllowed("file:///C:/Windows/System32/calc.exe"));
        Assert.False(GitHubUrlLauncher.IsAllowed("calc.exe"));
    }

    [Fact]
    public async Task Http_client_refuses_hosts_other_than_github()
    {
        using var http = GitHubHttpClientFactory.Create();
        await Assert.ThrowsAsync<InvalidOperationException>(() => http.GetAsync("https://example.com/"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => http.GetAsync("http://api.github.com/"));
    }

    [Fact]
    public async Task Avatar_host_is_allowed_only_without_credentials()
    {
        Assert.True(AvatarDownloader.IsAvatarUrl("https://avatars.githubusercontent.com/u/1?s=96"));
        Assert.False(AvatarDownloader.IsAvatarUrl("http://avatars.githubusercontent.com/u/1"));
        Assert.False(AvatarDownloader.IsAvatarUrl("https://avatars.githubusercontent.com.evil.example/u/1"));
        Assert.Null(await new AvatarDownloader(GitHubHttpClientFactory.Create()).GetAsync("https://example.com/a.png"));

        using var http = GitHubHttpClientFactory.Create();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://avatars.githubusercontent.com/u/1");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "secret");
        await Assert.ThrowsAsync<InvalidOperationException>(() => http.SendAsync(request));
    }

    [Fact]
    public async Task Local_secret_store_round_trips_and_never_writes_plaintext()
    {
        var paths = new AppPaths(Directory.CreateTempSubdirectory("ghd").FullName);
        var store = new LocalKeyFileSecretStore(paths);
        const string token = "ghp_exampleTokenValue1234567890";

        await store.SaveAsync("account:a", token);
        Assert.Equal(token, await store.ReadAsync("account:a"));
        Assert.Null(await store.ReadAsync("account:b"));

        foreach (var file in Directory.GetFiles(paths.Root, "*", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain(token, await File.ReadAllTextAsync(file));
        }

        await store.DeleteAsync("account:a");
        Assert.Null(await store.ReadAsync("account:a"));
    }

    [Fact]
    public async Task Settings_round_trip_and_hostile_values_are_dropped()
    {
        var paths = new AppPaths(Directory.CreateTempSubdirectory("ghd").FullName);
        var store = new JsonSettingsStore(paths);
        var id = Guid.NewGuid();

        await store.SaveAsync(new AppSettings
        {
            Accounts = [new(id, "krish", "Krish", AuthMethod.DeviceFlow)],
            Repositories =
            [
                new(id, "acme", "api"),
                new(id, "ACME", "API"),                 // duplicate
                new(id, "acme", "../../etc/passwd"),    // not a repository name
                new(Guid.NewGuid(), "acme", "orphan"),  // unknown account
            ],
            LiveCheckSeconds = 1,
            NotificationStyle = NotificationStyle.Native,
        });

        var loaded = await store.LoadAsync();
        Assert.Single(loaded.Accounts);
        Assert.Single(loaded.Repositories);
        Assert.Equal(AppSettings.MinLiveCheckSeconds, loaded.LiveCheckSeconds);
        Assert.Equal(NotificationStyle.Native, loaded.NotificationStyle);
        Assert.DoesNotContain("token", (await File.ReadAllTextAsync(paths.SettingsFile)).ToLowerInvariant());
    }

    [Fact]
    public async Task Corrupt_settings_fall_back_to_defaults()
    {
        var paths = new AppPaths(Directory.CreateTempSubdirectory("ghd").FullName);
        await File.WriteAllTextAsync(paths.SettingsFile, "{ not json");
        var loaded = await new JsonSettingsStore(paths).LoadAsync();
        Assert.Empty(loaded.Accounts);
        Assert.True(File.Exists(paths.SettingsFile + ".corrupt"));
    }
}

public class PersonActivityMapperTests
{
    [Fact]
    public void Maps_totals_branches_and_only_the_persons_comments()
    {
        const string json = """
            {"raised":{"issueCount":10},"open":{"issueCount":2},"merged":{"issueCount":6},"closed":{"issueCount":2},
             "draft":{"issueCount":1},"commented":{"issueCount":7},"reviewed":{"issueCount":5},
             "authored":{"nodes":[{"number":9,"title":"Cache","url":"https://github.com/o/r/pull/9","state":"MERGED","isDraft":false,
               "createdAt":"2026-11-01T08:00:00Z","closedAt":"2026-11-02T08:00:00Z","mergedAt":"2026-11-02T08:00:00Z"}]},
             "touched":{"nodes":[{"number":12,"title":"Other","url":"https://github.com/o/r/pull/12",
               "comments":{"nodes":[{"author":{"login":"maya"},"createdAt":"2026-11-10T08:00:00Z","body":"mine","url":"u1"},
                                    {"author":{"login":"leo"},"createdAt":"2026-11-10T09:00:00Z","body":"not mine","url":"u2"}]},
               "reviews":{"nodes":[{"author":{"login":"Maya"},"state":"APPROVED","submittedAt":"2026-11-11T08:00:00Z","body":"","url":"u3"}]}}]},
             "repository":{"defaultBranchRef":{"name":"main"},"refs":{"nodes":[
               {"name":"main","target":{"author":{"user":{"login":"maya"}}},"mergedPullRequests":{"totalCount":0}},
               {"name":"wip","target":{"committedDate":"2026-10-01T08:00:00Z","author":{"user":{"login":"maya"}}},"mergedPullRequests":{"totalCount":0},"openPullRequests":{"totalCount":0}},
               {"name":"review","target":{"committedDate":"2026-11-10T08:00:00Z","author":{"user":{"login":"maya"}}},"mergedPullRequests":{"totalCount":0},"openPullRequests":{"totalCount":1}},
               {"name":"done","target":{"author":{"user":{"login":"maya"}}},"mergedPullRequests":{"totalCount":1}},
               {"name":"theirs","target":{"author":{"user":{"login":"leo"}}},"mergedPullRequests":{"totalCount":0}}]}}}
            """;
        using var doc = JsonDocument.Parse(json);
        var a = PullRequestMapper.MapPersonActivity(doc.RootElement, new RepositoryRef("o", "r"), "maya");

        Assert.Equal(10, a.Raised);
        Assert.Equal(6, a.Merged);
        Assert.Equal(7, a.PullRequestsCommentedOn);
        Assert.Equal(new[] { "wip", "review" }, a.PendingBranches.Select(b => b.Name).ToArray());
        var now = DateTimeOffset.Parse("2026-11-13T00:00:00Z");
        Assert.Equal(PendingBranchHealth.Stale, a.PendingBranches[0].HealthAt(now));
        Assert.Equal(PendingBranchHealth.HasPullRequest, a.PendingBranches[1].HealthAt(now));
        Assert.Equal(PendingBranchHealth.Waiting, (a.PendingBranches[0] with { LastCommitAt = now.AddDays(-3) }).HealthAt(now));
        Assert.Equal(1, a.CommentsInRecent);
        Assert.Equal(1, a.ReviewsInRecent);
        Assert.Equal("MERGED", a.RecentPullRequests.Single().State);
        Assert.Contains(a.Events, e => e.Kind == PersonEventKind.Merged);
        Assert.Equal(0.75, a.MergeRate);
    }
}

public class GitHubCliTokenSourceTests
{
    [Fact]
    public void Parses_the_first_line_and_rejects_anything_else()
    {
        Assert.Equal("gho_abc123", GitHubCliTokenSource.ParseToken("gho_abc123\n"));
        Assert.Equal("gho_abc123", GitHubCliTokenSource.ParseToken("  gho_abc123  \r\nsecond line"));
        Assert.True(GitHubCliTokenSource.ParseToken("") is null);
        Assert.True(GitHubCliTokenSource.ParseToken(null) is null);
        Assert.True(GitHubCliTokenSource.ParseToken("not a token because spaces") is null);
        Assert.True(GitHubCliTokenSource.ParseToken(new string('a', 300)) is null);
    }

    [Fact]
    public void Looks_on_the_path_first_and_in_the_usual_install_folders()
    {
        var candidates = GitHubCliTokenSource.Candidates();
        Assert.True(candidates.Count > 1);
        Assert.True(candidates[0].StartsWith("gh"));
    }

    [Fact]
    public void Token_page_link_is_allowed_by_the_launcher()
    {
        Assert.True(GitHubUrlLauncher.IsAllowed(GitDash.Presentation.ViewModels.SettingsViewModel.NewTokenUrl));
    }
}

public class SoundTests
{
    [Fact]
    public void Every_built_in_sound_renders_a_valid_wav()
    {
        foreach (var option in SoundCatalog.BuiltIn)
        {
            var wav = GitDash.Infrastructure.Notifications.ToneSynth.Render(option.Id);
            Assert.True(wav.Length > 2000, option.Id);
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
            Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
            Assert.Equal(wav.Length - 8, BitConverter.ToInt32(wav, 4));
            Assert.Contains(wav, b => b != 0); // not silence
        }
    }

    [Fact]
    public void Unknown_sound_ids_fall_back_to_the_default()
    {
        Assert.Equal(SoundCatalog.DefaultId, SoundCatalog.Normalise("nope"));
        Assert.Equal(SoundCatalog.DefaultId, SoundCatalog.Normalise(null));
        Assert.Equal(SoundCatalog.CustomId, SoundCatalog.Normalise("custom"));
        Assert.Equal("bell", SoundCatalog.Normalise("bell"));
    }
}

public class CommentReactionTests
{
    [Fact]
    public void Maps_reactions_and_keeps_code_conversations_together()
    {
        const string json = """
            {"repository":{"pullRequest":{"createdAt":"2026-11-10T08:00:00Z","body":"x","author":{"login":"maya"},
              "reviewThreads":{"nodes":[{"id":"T1","isResolved":false,"comments":{"nodes":[{"id":"c1"},{"id":"c2"}]}}]},
              "timelineItems":{"nodes":[
                {"__typename":"IssueComment","createdAt":"2026-11-10T09:00:00Z","author":{"login":"leo"},"body":"great","bodyText":"great",
                 "reactionGroups":[{"content":"ROCKET","reactors":{"totalCount":2,"nodes":[{"login":"maya"},{"login":"krish"}]}},{"content":"EYES","reactors":{"totalCount":0}}]},
                {"__typename":"PullRequestReview","submittedAt":"2026-11-11T08:00:00Z","author":{"login":"krish"},"state":"COMMENTED","bodyText":"","body":"","comments":{"totalCount":2,"nodes":[
                  {"id":"c1","createdAt":"2026-11-11T07:50:00Z","path":"src/A.cs","body":"Rename","author":{"login":"krish"},
                   "reactionGroups":[{"content":"THUMBS_UP","reactors":{"totalCount":3}},{"content":"HEART","reactors":{"totalCount":0}}]},
                  {"id":"c2","createdAt":"2026-11-11T07:55:00Z","path":"src/A.cs","body":"Done","author":{"login":"maya"}}]}}]}}}}
            """;
        using var doc = JsonDocument.Parse(json);
        var comments = PullRequestMapper.MapDetails(doc.RootElement).Comments;

        var conversation = comments.Single(c => c.Source == CommentSource.Conversation);
        var rocket = Assert.Single(conversation.ReactionList);
        Assert.Equal("🚀", rocket.Emoji);
        Assert.Equal(2, rocket.Count);
        Assert.Equal("maya", rocket.Users![0]);
        Assert.Equal("krish", rocket.Users[1]);
        Assert.Null(conversation.ThreadId);

        var inline = comments.Where(c => c.Source == CommentSource.Inline).OrderBy(c => c.At).ToList();
        Assert.Equal(new CommentReaction("👍", 3), Assert.Single(inline[0].ReactionList)); // zero counts are dropped
        Assert.Empty(inline[1].ReactionList);
        Assert.Equal("T1", inline[0].ThreadId);
        Assert.Equal("T1", inline[1].ThreadId);
    }
}

public class WorkflowRunMapperTests
{
    [Fact]
    public void Maps_runs_and_counts_finished_steps()
    {
        const string runs = """
            {"workflow_runs":[
              {"id":1,"name":"CI","head_branch":"feature/x","status":"in_progress","conclusion":null,"created_at":"2026-11-13T08:00:00Z",
               "run_started_at":"2026-11-13T08:00:05Z","updated_at":"2026-11-13T08:01:00Z","html_url":"https://github.com/a/b/actions/runs/1",
               "actor":{"login":"maya"},"pull_requests":[{"number":5},{"number":6}]},
              {"id":2,"name":"Lint","head_branch":"main","status":"completed","conclusion":"failure","created_at":"2026-11-13T07:00:00Z",
               "updated_at":"2026-11-13T07:02:00Z","html_url":"https://github.com/a/b/actions/runs/2","actor":{"login":"leo"},"pull_requests":[]},
              {"id":3,"name":"Deploy","head_branch":"main","status":"queued","created_at":"2026-11-13T09:00:00Z","updated_at":"2026-11-13T09:00:00Z",
               "html_url":"https://github.com/a/b/actions/runs/3","actor":{"login":"sam"},"pull_requests":[]}]}
            """;
        const string jobs = """
            {"jobs":[{"steps":[{"status":"completed"},{"status":"completed"}]},{"steps":[{"status":"in_progress"},{"status":"queued"}]}]}
            """;
        using var runDoc = JsonDocument.Parse(runs);
        using var jobDoc = JsonDocument.Parse(jobs);
        var repo = new RepositoryRef("a", "b");
        var mapped = WorkflowRunMapper.MapRuns(runDoc.RootElement, repo);

        Assert.Equal(3, mapped.Count);
        Assert.Equal(GitDash.Domain.Actions.RunStatus.InProgress, mapped[0].Status);
        Assert.Equal(new[] { 5, 6 }, mapped[0].PullRequests.ToArray());
        Assert.True(mapped[0].BelongsTo(repo, 6));
        Assert.True(mapped[1].Failed);
        Assert.Equal(GitDash.Domain.Actions.RunStatus.Queued, mapped[2].Status);
        Assert.Equal(1d, mapped[1].Progress);

        var withSteps = WorkflowRunMapper.WithSteps(mapped[0], jobDoc.RootElement);
        Assert.Equal(2, withSteps.StepsDone);
        Assert.Equal(4, withSteps.StepsTotal);
        Assert.Equal(0.5, withSteps.Progress);
    }
}
