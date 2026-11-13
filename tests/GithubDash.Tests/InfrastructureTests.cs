using System.Text.Json;
using GithubDash.Domain.Insights;
using GithubDash.Application.Settings;
using GithubDash.Domain.PullRequests;
using GithubDash.Infrastructure.GitHub;
using GithubDash.Infrastructure.Security;
using GithubDash.Infrastructure.Storage;
using static GithubDash.Tests.TestData;

namespace GithubDash.Tests;

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
               {"name":"main","target":{"author":{"user":{"login":"maya"}}},"associatedPullRequests":{"totalCount":0}},
               {"name":"wip","target":{"author":{"user":{"login":"maya"}}},"associatedPullRequests":{"totalCount":0}},
               {"name":"done","target":{"author":{"user":{"login":"maya"}}},"associatedPullRequests":{"totalCount":1}},
               {"name":"theirs","target":{"author":{"user":{"login":"leo"}}},"associatedPullRequests":{"totalCount":0}}]}}}
            """;
        using var doc = JsonDocument.Parse(json);
        var a = PullRequestMapper.MapPersonActivity(doc.RootElement, new RepositoryRef("o", "r"), "maya");

        Assert.Equal(10, a.Raised);
        Assert.Equal(6, a.Merged);
        Assert.Equal(7, a.PullRequestsCommentedOn);
        Assert.Equal(new[] { "wip" }, a.PendingBranches.ToArray());
        Assert.Equal(1, a.CommentsInRecent);
        Assert.Equal(1, a.ReviewsInRecent);
        Assert.Equal("MERGED", a.RecentPullRequests.Single().State);
        Assert.Contains(a.Events, e => e.Kind == PersonEventKind.Merged);
        Assert.Equal(0.75, a.MergeRate);
    }
}
