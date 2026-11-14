using System.Text.Json;
using GitDash.Application.Abstractions;
using GitDash.Domain.Insights;
using GitDash.Domain.PullRequests;

namespace GitDash.Infrastructure.GitHub;

/// <summary>Turns GraphQL JSON into domain objects. Pure, so it can be unit-tested with canned JSON.</summary>
public static class PullRequestMapper
{
    private const int MaxSummaryLength = 420;
    private const int MaxTimelineTextLength = 240;
    private const int MaxCommentLength = 4000;
    private const int MaxDescriptionLength = 20000;
    private const string Ghost = "ghost"; // GitHub's name for deleted users

    public static RepositorySnapshot MapSnapshot(
        JsonElement data, Guid accountId, string viewerLogin, RepositoryRef repository)
    {
        var repo = data.Prop("repository");
        var open = repo.Nodes("open").Select(n => MapPullRequest(n, accountId, viewerLogin, repository)).ToList();
        var merged = repo.Nodes("merged").Select(n => MapPullRequest(n, accountId, viewerLogin, repository)).ToList();
        var total = Math.Max(repo.Prop("open").Int("totalCount"), open.Count);

        var avatars = new Dictionary<string, string>(Login.Comparer);
        foreach (var node in repo.Nodes("open"))
        {
            AddAvatar(avatars, node.Prop("author"));
            foreach (var assignee in node.Nodes("assignees")) AddAvatar(avatars, assignee);
            foreach (var request in node.Nodes("reviewRequests")) AddAvatar(avatars, request.Prop("requestedReviewer"));
        }

        return new RepositorySnapshot(accountId, repository, open, merged, total) { Avatars = avatars };
    }

    /// <summary>Maps the person-activity document. Comments and reviews are filtered to the person here.</summary>
    public static PersonActivity MapPersonActivity(JsonElement data, RepositoryRef repository, string login)
    {
        var repo = data.Prop("repository");
        var defaultBranch = repo.Prop("defaultBranchRef").StrOrNull("name");

        var pending = repo.Nodes("refs")
            .Where(r => !string.Equals(r.Str("name"), defaultBranch, StringComparison.Ordinal))
            .Where(r => Login.Same(r.Prop("target").Prop("author").Prop("user").StrOrNull("login"), login))
            .Where(r => r.Prop("mergedPullRequests").Int("totalCount") == 0)
            .Select(r => new PendingBranch(
                r.Str("name"), r.Prop("target").Date("committedDate"), r.Prop("openPullRequests").Int("totalCount") > 0))
            .Take(50)
            .ToList();

        var recent = data.Nodes("authored")
            .Where(n => n.Int("number") > 0)
            .Select(n => new PersonPullRequest(
                n.Int("number"), Shorten(n.Str("title"), 200), n.Str("url"),
                n.Date("mergedAt") is not null ? "MERGED" : n.Str("state"), n.Bool("isDraft"),
                n.Date("createdAt") ?? DateTimeOffset.MinValue, n.Date("mergedAt") ?? n.Date("closedAt")))
            .ToList();

        var events = new List<PersonEvent>();
        foreach (var pr in data.Nodes("touched"))
        {
            var number = pr.Int("number");
            var title = Shorten(pr.Str("title"), 200);
            foreach (var c in pr.Nodes("comments").Where(c => Login.Same(c.Prop("author").StrOrNull("login"), login)))
            {
                if (c.Date("createdAt") is not { } at) continue;
                events.Add(new PersonEvent(at, PersonEventKind.Comment, number, title, Shorten(c.Str("body"), 300),
                    c.Str("url", pr.Str("url"))));
            }

            foreach (var r in pr.Nodes("reviews").Where(r => Login.Same(r.Prop("author").StrOrNull("login"), login)))
            {
                if (r.Date("submittedAt") is not { } at) continue;
                var state = r.Str("state") switch
                {
                    "APPROVED" => "Approved",
                    "CHANGES_REQUESTED" => "Requested changes",
                    "COMMENTED" => "Reviewed",
                    var other => other.Length == 0 ? "Reviewed" : other,
                };
                var body = Shorten(r.Str("body"), 260);
                events.Add(new PersonEvent(at, PersonEventKind.Review, number, title,
                    body.Length == 0 ? state : $"{state}: {body}", r.Str("url", pr.Str("url"))));
            }
        }

        foreach (var pr in recent)
        {
            events.Add(new PersonEvent(pr.CreatedAt, PersonEventKind.Opened, pr.Number, pr.Title, "Opened the pull request", pr.Url));
            if (pr.State == "MERGED" && pr.ClosedAt is { } merged)
                events.Add(new PersonEvent(merged, PersonEventKind.Merged, pr.Number, pr.Title, "Pull request was merged", pr.Url));
        }

        return new PersonActivity
        {
            Login = login,
            Repository = repository.FullName,
            Raised = data.Prop("raised").Int("issueCount"),
            Open = data.Prop("open").Int("issueCount"),
            Merged = data.Prop("merged").Int("issueCount"),
            Closed = data.Prop("closed").Int("issueCount"),
            Draft = data.Prop("draft").Int("issueCount"),
            PullRequestsCommentedOn = data.Prop("commented").Int("issueCount"),
            PullRequestsReviewed = data.Prop("reviewed").Int("issueCount"),
            PendingBranches = pending,
            RecentPullRequests = recent,
            Events = [.. events.OrderByDescending(e => e.At).Take(60)],
        };
    }

    private static void AddAvatar(Dictionary<string, string> avatars, JsonElement? user)
    {
        var login = user.StrOrNull("login");
        var url = user.StrOrNull("avatarUrl");
        if (login is not null && url is not null) avatars[login] = url;
    }

    public static PullRequest MapPullRequest(
        JsonElement node, Guid accountId, string viewerLogin, RepositoryRef repository)
    {
        var reviews = node.Nodes("reviews")
            .Select(r => new Review(
                r.Prop("author").Str("login", Ghost),
                ParseReviewState(r.Str("state")),
                r.Date("submittedAt") ?? DateTimeOffset.MinValue,
                r.Prop("comments").Int("totalCount")))
            .ToList();

        var comments = node.Nodes("comments")
            .Select(c => (Author: c.Prop("author").Str("login", Ghost), At: c.Date("createdAt") ?? DateTimeOffset.MinValue))
            .ToList();

        // "Last comment" covers both conversation comments and reviews that carry inline comments.
        var lastComment = comments
            .Concat(reviews.Where(r => r.CommentCount > 0 || r.State == ReviewState.Commented)
                .Select(r => (r.Author, At: r.SubmittedAt)))
            .OrderByDescending(c => c.At)
            .Cast<(string Author, DateTimeOffset At)?>()
            .FirstOrDefault();

        DateTimeOffset? assignedAt = null, requestedAt = null;
        var waitingSince = new Dictionary<string, DateTimeOffset>(Login.Comparer);
        var assignedSince = new Dictionary<string, DateTimeOffset>(Login.Comparer);
        var reviewSince = new Dictionary<string, DateTimeOffset>(Login.Comparer);
        foreach (var e in node.Nodes("timelineItems"))
        {
            var at = e.Date("createdAt");
            var person = e.Prop("assignee").StrOrNull("login") ?? e.Prop("requestedReviewer").StrOrNull("login");
            if (person is not null && at is { } when &&
                (!waitingSince.TryGetValue(person, out var known) || when > known))
            {
                waitingSince[person] = when;
            }

            if (person is not null && at is { } happened)
            {
                var into = e.Str("__typename") == "AssignedEvent" ? assignedSince : reviewSince;
                if (!into.TryGetValue(person, out var seen) || happened > seen) into[person] = happened;
            }

            switch (e.Str("__typename"))
            {
                case "AssignedEvent" when Login.Same(e.Prop("assignee").StrOrNull("login"), viewerLogin):
                    assignedAt = Later(assignedAt, at);
                    break;
                case "ReviewRequestedEvent" when Login.Same(e.Prop("requestedReviewer").StrOrNull("login"), viewerLogin):
                    requestedAt = Later(requestedAt, at);
                    break;
            }
        }

        var reviewCommentTotal = reviews.Sum(r => r.CommentCount);
        return new PullRequest
        {
            Id = node.Str("id"),
            AccountId = accountId,
            ViewerLogin = viewerLogin,
            Repository = repository,
            Number = node.Int("number"),
            Title = node.Str("title"),
            Url = node.Str("url"),
            Author = node.Prop("author").Str("login", Ghost),
            State = node.Str("state") switch
            {
                "MERGED" => PullRequestState.Merged,
                "CLOSED" => PullRequestState.Closed,
                _ => PullRequestState.Open,
            },
            IsDraft = node.Bool("isDraft"),
            HasMergeConflict = node.Str("mergeable") == "CONFLICTING",
            PipelineFailed = node.Nodes("commits")
                .Select(n => n.Prop("commit").Prop("statusCheckRollup").StrOrNull("state"))
                .FirstOrDefault() is "FAILURE" or "ERROR",
            CreatedAt = node.Date("createdAt") ?? DateTimeOffset.MinValue,
            UpdatedAt = node.Date("updatedAt") ?? DateTimeOffset.MinValue,
            MergedAt = node.Date("mergedAt"),
            MergedBy = node.Prop("mergedBy").StrOrNull("login"),
            Assignees = [.. node.Nodes("assignees").Select(a => a.Str("login")).Where(l => l.Length > 0)],
            RequestedReviewers =
            [
                .. node.Nodes("reviewRequests")
                    .Select(r => r.Prop("requestedReviewer").StrOrNull("login"))
                    .OfType<string>(),
            ],
            Reviews = reviews,
            TotalComments = node.Prop("comments").Int("totalCount") + reviewCommentTotal,
            CommentsByViewer =
                comments.Count(c => Login.Same(c.Author, viewerLogin)) +
                reviews.Where(r => Login.Same(r.Author, viewerLogin)).Sum(r => r.CommentCount),
            LastCommentAuthor = lastComment?.Author,
            LastCommentAt = lastComment?.At,
            CommitCount = node.Prop("commits").Int("totalCount"),
            LastCommitAt = node.Nodes("commits").Select(n => n.Prop("commit").Date("committedDate")).FirstOrDefault(),
            Additions = node.Int("additions"),
            Deletions = node.Int("deletions"),
            ChangedFiles = node.Int("changedFiles"),
            Summary = Shorten(node.Str("bodyText"), MaxSummaryLength),
            WaitingSince = waitingSince,
            AssignedSince = assignedSince,
            ReviewRequestedSince = reviewSince,
            AssignedToViewerAt = assignedAt,
            ReviewRequestedFromViewerAt = requestedAt,
        };
    }

    public static GitHubProfile MapProfile(JsonElement data)
    {
        var viewer = data.Prop("viewer");
        return new GitHubProfile
        {
            Login = viewer.Str("login"),
            Name = viewer.StrOrNull("name"),
            AvatarUrl = viewer.StrOrNull("avatarUrl"),
            Bio = viewer.StrOrNull("bio"),
            Company = viewer.StrOrNull("company"),
            Location = viewer.StrOrNull("location"),
            Url = viewer.StrOrNull("url"),
            JoinedAt = viewer.Date("createdAt"),
            Followers = viewer.Prop("followers").Int("totalCount"),
            Following = viewer.Prop("following").Int("totalCount"),
            Repositories = viewer.Prop("repositories").Int("totalCount"),
            OpenPullRequests = viewer.Prop("openPullRequests").Int("totalCount"),
            MergedPullRequests = viewer.Prop("mergedPullRequests").Int("totalCount"),
            TotalPullRequests = viewer.Prop("allPullRequests").Int("totalCount"),
            ContributionsThisYear = viewer.Prop("contributionsCollection").Prop("contributionCalendar").Int("totalContributions"),
            Organizations = [.. viewer.Nodes("organizations").Select(o => o.Str("login")).Where(l => l.Length > 0)],
        };
    }

    /// <summary>The emoji reactions on a comment: only the ones somebody used.</summary>
    internal static IReadOnlyList<CommentReaction> Reactions(JsonElement comment)
    {
        var list = new List<CommentReaction>();
        foreach (var group in comment.Items("reactionGroups"))
        {
            var count = group.Prop("reactors").Int("totalCount");
            if (count <= 0) continue;
            var users = group.Nodes("reactors").Select(n => n.Str("login")).Where(l => l.Length > 0).ToList();
            list.Add(new CommentReaction(group.Str("content") switch
            {
                "THUMBS_UP" => "👍",
                "THUMBS_DOWN" => "👎",
                "LAUGH" => "😄",
                "HOORAY" => "🎉",
                "CONFUSED" => "😕",
                "HEART" => "❤️",
                "ROCKET" => "🚀",
                "EYES" => "👀",
                _ => "•",
            }, count, users.Count == 0 ? null : users));
        }

        return list;
    }

    public static PullRequestDetails MapDetails(JsonElement data)
    {
        var pr = data.Prop("repository").Prop("pullRequest");
        var entries = new List<TimelineEntry>();
        var comments = new List<PullRequestComment>();
        // Which code comments belong to a conversation that has been resolved (true) or is still open (false).
        var resolution = new Dictionary<string, bool>(StringComparer.Ordinal);
        var threadOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var thread in pr.Nodes("reviewThreads"))
        {
            var resolved = thread.Bool("isResolved");
            var threadId = thread.Str("id");
            foreach (var c in thread.Nodes("comments"))
            {
                var id = c.Str("id");
                if (id.Length == 0) continue;
                resolution[id] = resolved;
                if (threadId.Length > 0) threadOf[id] = threadId;
            }
        }

        if (pr.Date("createdAt") is { } created)
        {
            entries.Add(new(created, TimelineEntryKind.Opened, pr.Prop("author").Str("login", Ghost), "Opened the pull request"));
        }

        foreach (var e in pr.Nodes("timelineItems"))
        {
            var actor = e.Prop("actor").Str("login", Ghost);
            var at = e.Date("createdAt");
            TimelineEntry? entry = null;
            switch (e.Str("__typename"))
            {
                case "PullRequestCommit" when e.Prop("commit") is { } c:
                    entry = new(
                        c.Date("committedDate") ?? DateTimeOffset.MinValue,
                        TimelineEntryKind.Commit,
                        c.Prop("author").Prop("user").StrOrNull("login") ?? c.Prop("author").Str("name", Ghost),
                        Shorten(c.Str("messageHeadline"), MaxTimelineTextLength));
                    break;

                case "IssueComment" when at is { } t:
                    var author = e.Prop("author").Str("login", Ghost);
                    entry = new(t, TimelineEntryKind.Comment, author, Shorten(e.Str("bodyText"), MaxTimelineTextLength));
                    comments.Add(new(t, author, Limit(e.Str("body")), CommentSource.Conversation, Reactions: Reactions(e)));
                    break;

                case "PullRequestReview" when e.Date("submittedAt") is { } t:
                    entry = MapReview(e, t);
                    var reviewer = e.Prop("author").Str("login", Ghost);
                    if (e.Str("body").Trim().Length > 0)
                    {
                        comments.Add(new(t, reviewer, Limit(e.Str("body")), CommentSource.Review));
                    }

                    foreach (var inline in e.Nodes("comments"))
                    {
                        comments.Add(new(
                            inline.Date("createdAt") ?? t,
                            inline.Prop("author").Str("login", reviewer),
                            Limit(inline.Str("body")),
                            CommentSource.Inline,
                            ((JsonElement?)inline).StrOrNull("path"),
                            resolution.TryGetValue(inline.Str("id"), out var isResolved) ? isResolved : null,
                            Reactions(inline),
                            threadOf.GetValueOrDefault(inline.Str("id"))));
                    }

                    break;

                case "AssignedEvent" when at is { } t:
                    entry = new(t, TimelineEntryKind.Assigned, actor, $"Assigned {e.Prop("assignee").Str("login", "someone")}");
                    break;
                case "UnassignedEvent" when at is { } t:
                    entry = new(t, TimelineEntryKind.Unassigned, actor, $"Unassigned {e.Prop("assignee").Str("login", "someone")}");
                    break;
                case "ReviewRequestedEvent" when at is { } t:
                    entry = new(t, TimelineEntryKind.ReviewRequested, actor, $"Requested a review from {Reviewer(e)}");
                    break;
                case "ReviewRequestRemovedEvent" when at is { } t:
                    entry = new(t, TimelineEntryKind.ReviewRequestRemoved, actor, $"Removed the review request for {Reviewer(e)}");
                    break;
                case "ReviewDismissedEvent" when at is { } t:
                    entry = new(t, TimelineEntryKind.ReviewDismissed, actor,
                        $"Dismissed the review by {e.Prop("review").Prop("author").Str("login", "someone")}");
                    break;
                case "ReadyForReviewEvent" when at is { } t:
                    entry = new(t, TimelineEntryKind.ReadyForReview, actor, "Marked ready for review");
                    break;
                case "ConvertToDraftEvent" when at is { } t:
                    entry = new(t, TimelineEntryKind.ConvertedToDraft, actor, "Converted to draft");
                    break;
                case "HeadRefForcePushedEvent" when at is { } t:
                    entry = new(t, TimelineEntryKind.ForcePushed, actor, "Force-pushed the branch");
                    break;
                case "MergedEvent" when at is { } t:
                    entry = new(t, TimelineEntryKind.Merged, actor, "Merged the pull request");
                    break;
                case "ClosedEvent" when at is { } t:
                    entry = new(t, TimelineEntryKind.Closed, actor, "Closed the pull request");
                    break;
                case "ReopenedEvent" when at is { } t:
                    entry = new(t, TimelineEntryKind.Reopened, actor, "Reopened the pull request");
                    break;
            }

            if (entry is not null) entries.Add(entry);
        }

        return new PullRequestDetails(
            Limit(pr.Str("body"), MaxDescriptionLength),
            [.. entries.OrderBy(x => x.At)],
            [.. comments.OrderBy(c => c.At)]);
    }

    /// <summary>Caps text that comes from GitHub so one enormous comment cannot bloat memory or the UI.</summary>
    private static string Limit(string text, int max = MaxCommentLength)
    {
        text = text.Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }

    private static TimelineEntry MapReview(JsonElement e, DateTimeOffset at)
    {
        var author = e.Prop("author").Str("login", Ghost);
        var inline = e.Prop("comments").Int("totalCount");
        var body = Shorten(e.Str("bodyText"), MaxTimelineTextLength);
        var suffix = inline > 0 ? $" ({inline} inline comment{(inline == 1 ? "" : "s")})" : string.Empty;
        return ParseReviewState(e.Str("state")) switch
        {
            ReviewState.Approved => new(at, TimelineEntryKind.Approved, author, Join("Approved", body, suffix)),
            ReviewState.ChangesRequested => new(at, TimelineEntryKind.ChangesRequested, author, Join("Requested changes", body, suffix)),
            _ => new(at, TimelineEntryKind.Reviewed, author, Join("Reviewed", body, suffix)),
        };

        static string Join(string verb, string body, string suffix) =>
            body.Length == 0 ? verb + suffix : $"{verb}{suffix}: {body}";
    }

    private static string Reviewer(JsonElement e)
    {
        var reviewer = e.Prop("requestedReviewer");
        return reviewer.StrOrNull("login") ?? reviewer.StrOrNull("name") ?? "someone";
    }

    private static ReviewState ParseReviewState(string state) => state switch
    {
        "APPROVED" => ReviewState.Approved,
        "CHANGES_REQUESTED" => ReviewState.ChangesRequested,
        "COMMENTED" => ReviewState.Commented,
        "DISMISSED" => ReviewState.Dismissed,
        _ => ReviewState.Pending,
    };

    private static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : a > b ? a : b;

    private static string Shorten(string text, int max)
    {
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= max ? collapsed : collapsed[..(max - 1)].TrimEnd() + "…";
    }
}
