using GitDash.Domain.Insights;
using GitDash.Domain.Notifications;
using GitDash.Domain.PullRequests;
using GitDash.Domain.Timers;
using static GitDash.Tests.TestData;

namespace GitDash.Tests;

public class PullRequestTests
{
    [Fact]
    public void Approvals_older_than_the_newest_commit_are_stale()
    {
        var t0 = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
        var approved = new Review("alice", ReviewState.Approved, t0, 0);
        var changes = new Review("bob", ReviewState.ChangesRequested, t0, 0);
        var mine = new Review(Me, ReviewState.Approved, t0.AddHours(5), 0);

        var stale = Pr("a", with: p => p with
        {
            Reviews = [approved, changes, mine],
            LastCommitAt = t0.AddHours(2),
        });
        Assert.True(stale.HasStaleApproval);
        Assert.Equal(["alice"], stale.StaleApprovers.ToArray());
        Assert.Equal(["bob"], stale.StaleChangeRequesters.ToArray());
        Assert.False(stale.ViewerReviewIsStale); // the viewer reviewed after that commit

        var fresh = Pr("b", with: p => p with { Reviews = [approved], LastCommitAt = t0.Subtract(TimeSpan.FromHours(1)) });
        Assert.False(fresh.HasStaleReview);
        Assert.False(Pr("c", with: p => p with { Reviews = [approved] }).HasStaleReview); // unknown commit time: never stale

        var viewerStale = Pr("d", with: p => p with { Reviews = [mine], LastCommitAt = t0.AddHours(9) });
        Assert.True(viewerStale.ViewerReviewIsStale);
    }

    [Fact]
    public void Viewer_matching_ignores_case()
    {
        var pr = Pr("a", with: p => p with { Assignees = ["KRISH"], RequestedReviewers = ["Krish"] });
        Assert.True(pr.IsAssignedToViewer);
        Assert.True(pr.NeedsViewerApproval);
    }

    [Fact]
    public void Reapproval_needs_a_prior_review_and_a_fresh_request()
    {
        var reviewed = new Review(Me, ReviewState.ChangesRequested, Now.AddDays(-1), 2);
        Assert.True(Pr("a", with: p => p with { RequestedReviewers = [Me], Reviews = [reviewed] }).IsReapproval);
        Assert.False(Pr("b", with: p => p with { RequestedReviewers = [Me] }).IsReapproval);
        Assert.False(Pr("c", with: p => p with { Reviews = [reviewed] }).IsReapproval);
    }

    [Fact]
    public void Arrived_at_is_the_later_of_assignment_and_review_request()
    {
        var pr = Pr("a", with: p => p with
        {
            AssignedToViewerAt = Now.AddHours(-5),
            ReviewRequestedFromViewerAt = Now.AddHours(-2),
        });
        Assert.Equal(Now.AddHours(-2), pr.ArrivedAt);
        Assert.Null(Pr("b").ArrivedAt);
    }

    [Fact]
    public void Verdict_uses_each_reviewers_latest_decision()
    {
        var pr = Pr("a", with: p => p with
        {
            Reviews =
            [
                new("leo", ReviewState.ChangesRequested, Now.AddHours(-9), 1),
                new("leo", ReviewState.Approved, Now.AddHours(-1), 0),
                new("sam", ReviewState.Commented, Now, 4),
            ],
        });
        Assert.Equal(ReviewState.Approved, pr.Verdict);
    }
}

public class ChangeDetectorTests
{
    private static readonly NotificationRules All = new();

    [Fact]
    public void First_snapshot_is_a_silent_baseline()
    {
        var current = Snapshot([Pr("a", with: p => p with { Assignees = [Me] })]);
        Assert.Empty(PullRequestChangeDetector.Detect(null, current, All, Now));
    }

    [Fact]
    public void New_assignment_and_review_request_are_reported_once()
    {
        var before = Snapshot([Pr("a")]);
        var after = Snapshot([Pr("a", with: p => p with { Assignees = [Me], RequestedReviewers = [Me] })]);

        var first = PullRequestChangeDetector.Detect(before, after, All, Now);
        Assert.Equal(2, first.Count);
        Assert.Contains(first, n => n.Kind == NotificationKind.AssignedToMe);
        Assert.Contains(first, n => n.Kind == NotificationKind.ReviewRequested);

        Assert.Empty(PullRequestChangeDetector.Detect(after, after, All, Now));
    }

    [Fact]
    public void A_brand_new_pull_request_assigned_to_me_is_reported()
    {
        var after = Snapshot([Pr("new", with: p => p with { Assignees = [Me] })]);
        var result = PullRequestChangeDetector.Detect(Snapshot([]), after, All, Now);
        Assert.Single(result);
        Assert.Equal(NotificationKind.AssignedToMe, result[0].Kind);
    }

    [Fact]
    public void Comments_are_reported_only_for_my_pull_requests_and_not_for_my_own_comments()
    {
        var mine = Pr("mine", author: Me, with: p => p with { TotalComments = 1 });
        var other = Pr("other", with: p => p with { TotalComments = 1 });
        var before = Snapshot([mine, other]);

        var byColleague = Snapshot(
        [
            mine with { TotalComments = 3, LastCommentAuthor = "leo" },
            other with { TotalComments = 5, LastCommentAuthor = "leo" },
        ]);
        var result = PullRequestChangeDetector.Detect(before, byColleague, All, Now);
        Assert.Single(result);
        Assert.Equal("mine", result[0].PullRequest.Id);
        Assert.Contains("2 new comments", result[0].Title);

        var byMe = Snapshot([mine with { TotalComments = 2, LastCommentAuthor = Me }, other]);
        Assert.Empty(PullRequestChangeDetector.Detect(before, byMe, All, Now));
    }

    [Fact]
    public void Merge_is_reported_only_for_pull_requests_that_were_open_before()
    {
        var before = Snapshot([Pr("a")]);
        var after = Snapshot([], merged:
        [
            Pr("a", with: p => p with { State = PullRequestState.Merged, MergedBy = "leo" }),
            Pr("old", with: p => p with { State = PullRequestState.Merged }),
        ]);
        var result = PullRequestChangeDetector.Detect(before, after, All, Now);
        Assert.Single(result);
        Assert.Equal(NotificationKind.Merged, result[0].Kind);
        Assert.Contains("leo", result[0].Title);
    }

    [Fact]
    public void Disabled_rules_suppress_their_notifications()
    {
        var before = Snapshot([Pr("a")]);
        var after = Snapshot([Pr("a", with: p => p with { Assignees = [Me], RequestedReviewers = [Me] })]);
        var rules = new NotificationRules(AssignedToMe: false);
        var result = PullRequestChangeDetector.Detect(before, after, rules, Now);
        Assert.Single(result);
        Assert.Equal(NotificationKind.ReviewRequested, result[0].Kind);
    }
}

public class InsightTests
{
    private static void AssertStats(RepositoryFloor floor, string login, int raised, int assignedUnder)
    {
        var person = floor.People.Single(p => p.Login == login);
        Assert.Equal(raised, person.Raised);
        Assert.Equal(assignedUnder, person.AssignedUnder);
    }

    [Fact]
    public void Summary_counts_distinct_pull_requests()
    {
        var shared = Pr("a", with: p => p with { Assignees = [Me] });
        var summary = DashboardSummary.From(
        [
            shared,
            shared with { AccountId = Guid.NewGuid() }, // same PR seen through a second account
            Pr("b", with: p => p with { RequestedReviewers = [Me] }),
            Pr("c"),
        ]);
        Assert.Equal(new DashboardSummary(3, 1, 1), summary);
    }

    [Fact]
    public void Floors_count_raised_and_assigned_per_person()
    {
        var floors = PeopleInsights.Build(
        [
            Pr("a", author: "maya", with: p => p with { Assignees = ["leo"], RequestedReviewers = ["leo", "sam"] }),
            Pr("b", author: "maya", with: p => p with { IsDraft = true }),
            Pr("c", author: "leo", with: p => p with { RequestedReviewers = ["maya"] }),
        ], Now);

        var floor = Assert.Single(floors);
        Assert.Equal(3, floor.OpenCount);
        Assert.Equal(1, floor.DraftCount);
        AssertStats(floor, "maya", raised: 2, assignedUnder: 1);
        AssertStats(floor, "leo", raised: 1, assignedUnder: 1);
        AssertStats(floor, "sam", raised: 0, assignedUnder: 1);
        Assert.Equal(TimeSpan.FromDays(2), floor.OldestAge);
    }
}

public class TimerTests
{
    [Fact]
    public void Stopwatch_accumulates_across_pauses()
    {
        var watch = new TrackedStopwatch();
        watch.Start(Now);
        watch.Pause(Now.AddMinutes(10));
        watch.Start(Now.AddMinutes(30));
        Assert.Equal(TimeSpan.FromMinutes(15), watch.Elapsed(Now.AddMinutes(35)));
        Assert.Equal(Now, watch.FirstStartedAt);
    }

    [Fact]
    public void Pomodoro_cycles_through_short_and_long_breaks()
    {
        var timer = new PomodoroTimer(new PomodoroSettings(25, 5, 15, CyclesBeforeLongBreak: 2));
        var t = Now;

        timer.Start(t);
        Assert.Null(timer.Tick(t.AddMinutes(24)));
        Assert.Equal(PomodoroPhase.Focus, timer.Tick(t.AddMinutes(25)));
        Assert.Equal(PomodoroPhase.ShortBreak, timer.Phase);
        Assert.False(timer.IsRunning);

        Assert.Equal(PomodoroPhase.ShortBreak, timer.Skip());
        Assert.Equal(PomodoroPhase.Focus, timer.Skip());
        Assert.Equal(PomodoroPhase.LongBreak, timer.Phase);
        Assert.Equal(2, timer.CompletedFocusCount);
        Assert.Equal(TimeSpan.FromMinutes(15), timer.Remaining(t));
    }
}


public class AlarmTests
{
    private static readonly AlarmEntry Daily = new(Guid.NewGuid(), "Stand-up", new TimeOnly(9, 30), Daily: true);
    private static readonly DateTime Day = new(2026, 11, 13, 0, 0, 0);

    [Fact]
    public void Rings_from_its_time_for_a_few_minutes_only()
    {
        Assert.False(Daily.IsDue(Day.AddHours(9).AddMinutes(29)));
        Assert.True(Daily.IsDue(Day.AddHours(9).AddMinutes(30)));
        Assert.True(Daily.IsDue(Day.AddHours(9).AddMinutes(34)));
        Assert.False(Daily.IsDue(Day.AddHours(9).AddMinutes(36))); // the app was closed at 9:30: stay silent
    }

    [Fact]
    public void Daily_alarm_waits_for_tomorrow_and_one_off_alarm_switches_off()
    {
        var ringing = Day.AddHours(9).AddMinutes(30);
        var after = Daily.Rang(ringing);
        Assert.False(after.IsDue(ringing.AddMinutes(1)));
        Assert.True(after.IsDue(ringing.AddDays(1)));

        var once = Daily with { Daily = false };
        var done = once.Rang(ringing);
        Assert.False(done.Enabled);
        Assert.False(done.IsDue(ringing.AddDays(1)));
    }

    [Fact]
    public void Disabled_alarm_stays_silent()
    {
        Assert.False((Daily with { Enabled = false }).IsDue(Day.AddHours(9).AddMinutes(31)));
    }
}

public class ViewerApprovalTests
{
    private static PullRequest With(params Review[] reviews) => TestData.Pr("a", with: p => p with
    {
        Reviews = reviews,
        LastCommitAt = TestData.Now.AddHours(-1),
    });

    [Fact]
    public void Approval_after_the_last_commit_counts()
    {
        Assert.True(With(new Review(TestData.Me, ReviewState.Approved, TestData.Now, 0)).ViewerHasApproved);
    }

    [Fact]
    public void Approval_before_the_last_commit_is_stale_and_does_not_count()
    {
        Assert.False(With(new Review(TestData.Me, ReviewState.Approved, TestData.Now.AddHours(-3), 0)).ViewerHasApproved);
    }

    [Fact]
    public void Someone_elses_approval_or_a_later_change_request_does_not_count()
    {
        Assert.False(With(new Review("maya", ReviewState.Approved, TestData.Now, 0)).ViewerHasApproved);
        Assert.False(With(
            new Review(TestData.Me, ReviewState.Approved, TestData.Now.AddMinutes(-10), 0),
            new Review(TestData.Me, ReviewState.ChangesRequested, TestData.Now, 0)).ViewerHasApproved);
    }
}

public class WaitingForTests
{
    private static PullRequest Pr(Func<PullRequest, PullRequest> with) => TestData.Pr("w", with: with);

    [Fact]
    public void Waits_for_the_requested_reviewers()
    {
        var pr = Pr(p => p with { RequestedReviewers = ["maya", "leo"], Assignees = ["sam"] });
        Assert.Equal(new[] { "maya", "leo" }, pr.WaitingFor.ToArray());
        Assert.Equal("to review", pr.WaitingForReason);
    }

    [Fact]
    public void Waits_for_the_author_after_changes_were_requested_or_once_approved()
    {
        var changes = Pr(p => p with { Author = "maya", Reviews = [new Review("leo", ReviewState.ChangesRequested, TestData.Now, 0)] });
        Assert.Equal(new[] { "maya" }, changes.WaitingFor.ToArray());
        Assert.Equal("to address the requested changes", changes.WaitingForReason);

        var approved = Pr(p => p with { Author = "maya", Reviews = [new Review("leo", ReviewState.Approved, TestData.Now, 0)] });
        Assert.Equal(new[] { "maya" }, approved.WaitingFor.ToArray());
        Assert.Equal("to merge", approved.WaitingForReason);
    }

    [Fact]
    public void Falls_back_to_the_assignees_and_is_empty_when_not_open()
    {
        Assert.Equal(new[] { "sam" }, Pr(p => p with { Assignees = ["sam"] }).WaitingFor.ToArray());
        Assert.Empty(Pr(p => p with { Assignees = ["sam"], State = PullRequestState.Merged }).WaitingFor);
    }
}

public class AiAuthorTests
{
    [Fact]
    public void Detects_ai_logins()
    {
        Assert.True(AiAuthors.IsAi("copilot-pull-request-reviewer"));
        Assert.True(AiAuthors.IsAi("coderabbitai[bot]"));
        Assert.True(AiAuthors.IsAi("Claude"));
        Assert.False(AiAuthors.IsAi("maya"));
        Assert.False(AiAuthors.IsAi(""));
    }
}

public class MarkdownTests
{
    [Fact]
    public void Parses_blocks_and_inlines()
    {
        var blocks = GitDash.Presentation.Markdown.MarkdownParser.Parse(
            "## Title\n\nSome **bold** and `code` with [link](https://github.com/x).\n\n- [x] done\n- [ ] todo\n- item\n1. first\n\n```\nvar a = 1;\n```\n> quote");
        var kinds = blocks.Select(b => b.Kind).ToArray();
        Assert.Equal(new[]
        {
            GitDash.Presentation.Markdown.BlockKind.Heading, GitDash.Presentation.Markdown.BlockKind.Paragraph,
            GitDash.Presentation.Markdown.BlockKind.Task, GitDash.Presentation.Markdown.BlockKind.Task,
            GitDash.Presentation.Markdown.BlockKind.Bullet, GitDash.Presentation.Markdown.BlockKind.Numbered,
            GitDash.Presentation.Markdown.BlockKind.Code, GitDash.Presentation.Markdown.BlockKind.Quote,
        }, kinds);
        Assert.True(blocks[2].Checked);
        Assert.False(blocks[3].Checked);
        Assert.Contains(blocks[1].Inlines, i => i.Kind == GitDash.Presentation.Markdown.InlineKind.Link && i.Url == "https://github.com/x");
        Assert.Equal("var a = 1;", blocks[6].PlainText);
    }

    [Fact]
    public void Empty_input_gives_no_blocks() =>
        Assert.Empty(GitDash.Presentation.Markdown.MarkdownParser.Parse("  "));
}

public class SnapshotSerializerTests
{
    [Fact]
    public void Round_trip_keeps_data_and_case_insensitive_lookups()
    {
        var pr = TestData.Pr("1", with: p => p with
        {
            Assignees = ["krish"],
            WaitingSince = new Dictionary<string, DateTimeOffset> { ["Krish"] = DateTimeOffset.Parse("2026-11-10T08:00:00Z") },
        });
        var snapshot = new RepositorySnapshot(Guid.NewGuid(), new RepositoryRef("o", "r"), [pr], [], 1)
        {
            Avatars = new Dictionary<string, string> { ["Maya"] = "https://avatars.githubusercontent.com/u/1" },
        };

        var back = GitDash.Infrastructure.Storage.SnapshotSerializer.ReadSnapshot(
            GitDash.Infrastructure.Storage.SnapshotSerializer.Write(snapshot))!;

        Assert.Equal(pr.Title, back.Open[0].Title);
        Assert.Equal(snapshot.Repository, back.Repository);
        Assert.True(back.Open[0].WaitingSince.ContainsKey("KRISH"));
        Assert.True(back.Avatars.ContainsKey("maya"));
        Assert.True(back.Open[0].IsAssignedToViewer == pr.IsAssignedToViewer);
    }
}

public class Round4Tests
{
    [Fact]
    public void Notification_types_stay_or_close_independently()
    {
        var settings = new GitDash.Application.Settings.AppSettings
        {
            StickyTones = [GitDash.Application.Abstractions.ToastTone.Review],
        };
        Assert.False(settings.AutoCloses(GitDash.Application.Abstractions.ToastTone.Review));
        Assert.True(settings.AutoCloses(GitDash.Application.Abstractions.ToastTone.Comment));
        Assert.True(new GitDash.Application.Settings.AppSettings().ShowToastWithIsland);
    }

    [Fact]
    public void A_person_walks_in_only_once()
    {
        var registry = new GitDash.Presentation.ViewModels.EntranceRegistry();
        var person = new GitDash.Presentation.ViewModels.PersonViewModel(
            "ann", "o/r", "AN", "#fff", null, 1, 1, 1, "", "", false, 0) { Entrance = registry };
        Assert.True(person.ConsumeEntrance());
        Assert.False(person.ConsumeEntrance());
        Assert.False((person with { Raised = 2 }).ConsumeEntrance());
    }

    [Fact]
    public void Being_removed_as_assignee_is_reported_while_the_pr_stays_open()
    {
        var before = Snapshot([Pr("a", with: p => p with { Assignees = [Me] })]);
        var after = Snapshot([Pr("a", with: p => p with { Assignees = ["maya"] })]);
        var found = PullRequestChangeDetector.Detect(before, after, new NotificationRules(), Now);
        Assert.Contains(found, n => n.Kind == NotificationKind.UnassignedFromMe);
        Assert.Empty(PullRequestChangeDetector.Detect(before, after, new NotificationRules(UnassignedFromMe: false), Now));

        // A merged PR leaves the open list; that is not "removed as assignee".
        Assert.False(PullRequestChangeDetector.Detect(before, Snapshot([]), new NotificationRules(), Now)
            .Any(n => n.Kind == NotificationKind.UnassignedFromMe));
    }

    [Fact]
    public void Oldest_assigned_and_oldest_review_are_tracked_separately()
    {
        var assignedAt = Now.AddDays(-3);
        var reviewAt = Now.AddDays(-1);
        var pr = Pr("a", with: p => p with
        {
            Assignees = ["ann"],
            RequestedReviewers = ["ann"],
            AssignedSince = new Dictionary<string, DateTimeOffset> { ["ann"] = assignedAt },
            ReviewRequestedSince = new Dictionary<string, DateTimeOffset> { ["ann"] = reviewAt },
        });
        var person = PeopleInsights.Build([pr], Now).Single().People.Single(p => p.Login == "ann");
        Assert.Equal(assignedAt, person.OldestAssignedSince);
        Assert.Equal(reviewAt, person.OldestReviewSince);
    }
}

public class Round7Tests
{
    [Fact]
    public void Assigned_sorts_before_reviewer_before_others()
    {
        var other = TestData.Pr("other");
        var review = TestData.Pr("review", with: p => p with { RequestedReviewers = [TestData.Me] });
        var assigned = TestData.Pr("assigned", with: p => p with { Assignees = [TestData.Me] });
        var both = TestData.Pr("both", with: p => p with { Assignees = [TestData.Me], RequestedReviewers = [TestData.Me] });

        var ordered = new[] { other, review, assigned, both }.OrderBy(p => p.ViewerRank).Select(p => p.Id).ToList();

        Assert.Equal(0, assigned.ViewerRank);
        Assert.Equal(0, both.ViewerRank); // assigned wins when it is also a review request
        Assert.Equal(1, review.ViewerRank);
        Assert.Equal(2, other.ViewerRank);
        Assert.Equal(["assigned", "both", "review", "other"], ordered);
    }

    [Fact]
    public void Card_border_state_is_exactly_one_colour_and_errors_win()
    {
        static GitDash.Presentation.ViewModels.PullRequestCardViewModel Card(PullRequest p) => new(p, TestData.Now);
        static int Count(GitDash.Presentation.ViewModels.PullRequestCardViewModel c) =>
            new[] { c.StateError, c.StateStale, c.StateApproved, c.StateChanges, c.StateUnreviewed }.Count(b => b);

        var plain = Card(TestData.Pr("plain"));
        var failing = Card(TestData.Pr("failing", with: p => p with { PipelineFailed = true }));
        var conflict = Card(TestData.Pr("conflict", with: p => p with { HasMergeConflict = true }));

        foreach (var c in new[] { plain, failing, conflict }) Assert.Equal(1, Count(c));
        Assert.True(plain.StateUnreviewed);   // purple
        Assert.True(failing.StateError);      // red
        Assert.True(conflict.StateError);
        Assert.False(failing.StateUnreviewed);
    }
}

public class Round9Tests
{
    [Fact]
    public void Tags_and_colour_come_from_one_shared_status()
    {
        var conflict = TestData.Pr("c", with: p => p with { HasMergeConflict = true, Assignees = [TestData.Me] });
        var plain = TestData.Pr("p");

        var tags = GitDash.Presentation.ViewModels.PullRequestStatus.TagsOf(conflict).Select(t => t.Text).ToList();

        Assert.True(tags.Contains("Merge Conflict"));
        Assert.True(tags.Contains("Assigned"));
        Assert.Equal("#FB7185", GitDash.Presentation.ViewModels.PullRequestStatus.ColorOf(conflict));
        Assert.Equal("#A855F7", GitDash.Presentation.ViewModels.PullRequestStatus.ColorOf(plain));
        Assert.Equal(["Unreviewed"], GitDash.Presentation.ViewModels.PullRequestStatus.TagsOf(plain).Select(t => t.Text).ToList());
    }

    [Fact]
    public void Error_filters_match_only_their_own_error()
    {
        var conflict = TestData.Pr("c", with: p => p with { HasMergeConflict = true });
        var failing = TestData.Pr("f", with: p => p with { PipelineFailed = true });
        var mergeFilter = new GitDash.Presentation.ViewModels.FilterOptionViewModel(GitDash.Presentation.ViewModels.PullRequestFilter.MergeConflict, "m");
        var pipeFilter = new GitDash.Presentation.ViewModels.FilterOptionViewModel(GitDash.Presentation.ViewModels.PullRequestFilter.PipelineFailed, "p");

        Assert.True(mergeFilter.Matches(conflict));
        Assert.False(mergeFilter.Matches(failing));
        Assert.True(pipeFilter.Matches(failing));
        Assert.False(pipeFilter.Matches(conflict));
    }
}

public class Round10Tests
{
    [Fact]
    public void Time_is_shown_in_the_chosen_style()
    {
        var at = new DateTimeOffset(2026, 11, 13, 14, 5, 0, TimeSpan.Zero);
        var previous = GitDash.Presentation.Mvvm.Humanize.Format;
        try
        {
            GitDash.Presentation.Mvvm.Humanize.Format = GitDash.Application.Settings.TimeFormat.Utc;
            Assert.Equal("14:05 UTC", GitDash.Presentation.Mvvm.Humanize.Time(at));

            GitDash.Presentation.Mvvm.Humanize.Format = GitDash.Application.Settings.TimeFormat.Hour24;
            var local = at.ToLocalTime();
            Assert.Equal(local.ToString("HH:mm"), GitDash.Presentation.Mvvm.Humanize.Time(at));

            GitDash.Presentation.Mvvm.Humanize.Format = GitDash.Application.Settings.TimeFormat.Hour12;
            Assert.True(GitDash.Presentation.Mvvm.Humanize.Time(at).EndsWith("M"));
        }
        finally
        {
            GitDash.Presentation.Mvvm.Humanize.Format = previous;
        }
    }

    [Fact]
    public void Pascal_case_and_timeline_tags()
    {
        Assert.Equal("Changes Requested", GitDash.Presentation.Mvvm.Humanize.Pascal("CHANGES_REQUESTED"));
        Assert.Equal("Open", GitDash.Presentation.Mvvm.Humanize.Pascal("OPEN"));
        Assert.Equal("Added", GitDash.Presentation.ViewModels.TimelineTags.LabelOf(GitDash.Domain.PullRequests.TimelineEntryKind.ReviewRequested));
        Assert.Equal("Removed", GitDash.Presentation.ViewModels.TimelineTags.LabelOf(GitDash.Domain.PullRequests.TimelineEntryKind.Unassigned));
    }

    [Fact]
    public void Card_tags_show_changes_requested_and_unreviewed()
    {
        var plain = GitDash.Presentation.ViewModels.PullRequestStatus.TagsOf(TestData.Pr("p")).Select(t => t.Text).ToList();
        Assert.True(plain.Contains("Unreviewed"));
        var withState = GitDash.Presentation.ViewModels.PullRequestStatus.TagsOf(TestData.Pr("p"), withState: true).Select(t => t.Text).ToList();
        Assert.Equal("Open", withState[0]);
    }

    [Fact]
    public void Shelf_book_height_grows_with_pr_size()
    {
        var tiny = GitDash.Presentation.ViewModels.DashboardViewModel.BookHeightFor(1, 0, 1);
        var medium = GitDash.Presentation.ViewModels.DashboardViewModel.BookHeightFor(200, 50, 8);
        var huge = GitDash.Presentation.ViewModels.DashboardViewModel.BookHeightFor(50000, 20000, 400);
        Assert.True(tiny < medium);
        Assert.True(medium < huge);
        Assert.True(tiny >= GitDash.Presentation.ViewModels.DashboardViewModel.MinBookHeight);
        Assert.True(huge <= GitDash.Presentation.ViewModels.DashboardViewModel.MaxBookHeight);
    }

    [Fact]
    public void Desk_books_are_coloured_by_type_and_capped()
    {
        var books = GitDash.Presentation.ViewModels.PersonViewModel.BuildDeskBooks(2, 1, 1);
        Assert.Equal(4, books.Count);
        Assert.Equal(GitDash.Presentation.ViewModels.PersonViewModel.AssignedBookColor, books[0].Color);
        Assert.Equal(GitDash.Presentation.ViewModels.PersonViewModel.ReviewBookColor, books[2].Color);
        Assert.Equal(GitDash.Presentation.ViewModels.PersonViewModel.RaisedBookColor, books[3].Color);

        var capped = GitDash.Presentation.ViewModels.PersonViewModel.BuildDeskBooks(20, 20, 20);
        Assert.Equal(GitDash.Presentation.ViewModels.PersonViewModel.MaxDeskBooks, capped.Count);
        Assert.True(capped.Any(b => b.Color == GitDash.Presentation.ViewModels.PersonViewModel.RaisedBookColor));
        Assert.Equal(0, GitDash.Presentation.ViewModels.PersonViewModel.BuildDeskBooks(0, 0, 0).Count);
    }

    [Fact]
    public void Desk_books_are_ordered_assigned_then_review_then_raised()
    {
        var books = GitDash.Presentation.ViewModels.PersonViewModel.BuildDeskBooks(1, 2, 3);
        Assert.Equal(6, books.Count);
        var colors = books.Select(b => b.Color).ToList();
        var assigned = colors.IndexOf(GitDash.Presentation.ViewModels.PersonViewModel.AssignedBookColor);
        var review = colors.IndexOf(GitDash.Presentation.ViewModels.PersonViewModel.ReviewBookColor);
        var raised = colors.IndexOf(GitDash.Presentation.ViewModels.PersonViewModel.RaisedBookColor);
        Assert.True(assigned < review);
        Assert.True(review < raised);
    }

    [Fact]
    public void Pending_branch_colours_follow_pr_and_age()
    {
        var now = Now;
        var withPr = new GitDash.Domain.Insights.PendingBranch("a", now.AddDays(-30), true);
        var fresh = new GitDash.Domain.Insights.PendingBranch("b", now.AddDays(-3), false);
        var old = new GitDash.Domain.Insights.PendingBranch("c", now.AddDays(-15), false);
        Assert.Equal("Green", GitDash.Presentation.ViewModels.PendingBranchViewModel.From(withPr, now, "o/r").Tone);
        Assert.Equal("Amber", GitDash.Presentation.ViewModels.PendingBranchViewModel.From(fresh, now, "o/r").Tone);
        Assert.Equal("Red", GitDash.Presentation.ViewModels.PendingBranchViewModel.From(old, now, "o/r").Tone);
    }

    [Fact]
    public void Repository_option_reports_clicks_but_not_silent_updates()
    {
        var clicks = 0;
        var option = new GitDash.Presentation.ViewModels.RepositoryOptionViewModel("o/r", false, _ => clicks++);
        option.SetChecked(true);
        Assert.Equal(0, clicks);
        option.IsSelected = false;
        Assert.Equal(1, clicks);
    }

    [Fact]
    public void Window_placement_and_repository_selection_have_safe_defaults()
    {
        var settings = new GitDash.Application.Settings.AppSettings();
        Assert.True(settings.MainWindow is null);
        Assert.Equal(0, settings.SelectedRepositories.Count);
    }

    [Fact]
    public void Viewer_comes_first_then_biggest_stack()
    {
        var people = new[]
        {
            new GitDash.Domain.Insights.PersonStats("zed", 1, 0, 0, 0, null, null),
            new GitDash.Domain.Insights.PersonStats("amy", 2, 2, 2, 4, null, null),
            new GitDash.Domain.Insights.PersonStats("me", 0, 0, 0, 0, null, null),
            new GitDash.Domain.Insights.PersonStats("bob", 1, 1, 1, 2, null, null),
        };
        var order = GitDash.Presentation.ViewModels.PeopleViewModel
            .OrderPeople(people, new HashSet<string> { "me" }).Select(p => p.Login).ToList();
        Assert.Equal("me", order[0]);
        Assert.Equal("amy", order[1]);
        Assert.Equal("bob", order[2]);
        Assert.Equal("zed", order[3]);
    }

    [Fact]
    public void Branch_link_keeps_slashes_and_escapes_the_rest()
    {
        Assert.Equal("https://github.com/o/r/tree/feature/a%20b",
            GitDash.Presentation.ViewModels.PendingBranchViewModel.UrlFor("o/r", "feature/a b"));
    }

    [Fact]
    public void Glass_effect_is_on_by_default()
    {
        Assert.True(new GitDash.Application.Settings.AppSettings().GlassEffect);
    }

    private sealed class RecordingLauncher : GitDash.Application.Abstractions.IUrlLauncher
    {
        public List<string> Opened { get; } = [];
        public bool Open(string url) { Opened.Add(url); return true; }
    }

    [Fact]
    public void About_page_shows_the_author_and_opens_the_repository()
    {
        var launcher = new RecordingLauncher();
        var about = new GitDash.Presentation.ViewModels.AboutViewModel(launcher);
        Assert.Equal("Gowtham Krishnan", about.AuthorName);
        Assert.Equal("gowtham.krishnan@solitontech.com", about.Email);
        Assert.Equal("GK", about.AuthorInitials);
        about.OpenRepositoryCommand.Execute(null);
        Assert.Equal(GitDash.Presentation.ViewModels.AboutViewModel.RepositoryUrl, launcher.Opened[0]);
        Assert.True(GitDash.Infrastructure.Security.GitHubUrlLauncher.IsAllowed(GitDash.Presentation.ViewModels.AboutViewModel.RepositoryUrl));
    }

    [Fact]
    public void Version_text_drops_build_metadata()
    {
        Assert.Equal("dev", GitDash.Presentation.ViewModels.AboutViewModel.VersionOf(null));
        Assert.True(GitDash.Infrastructure.Security.GitHubUrlLauncher.IsAllowed(GitDash.Presentation.ViewModels.SettingsViewModel.NewOAuthAppUrl));
    }
}
