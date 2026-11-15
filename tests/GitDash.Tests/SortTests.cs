using GitDash.Domain.PullRequests;
using GitDash.Presentation.ViewModels;

namespace GitDash.Tests;

public class SortTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private static PullRequest Pr(int number, string author, int createdDaysAgo, int? assignedDaysAgo = null) => new()
    {
        Id = number.ToString(), AccountId = Guid.NewGuid(), ViewerLogin = "krish", Repository = new RepositoryRef("o", "r"),
        Number = number, Title = $"PR {number}", Url = $"https://github.com/o/r/pull/{number}", Author = author,
        CreatedAt = Now.AddDays(-createdDaysAgo), UpdatedAt = Now.AddDays(-number),
        Assignees = assignedDaysAgo is null ? [] : ["krish"],
        AssignedToViewerAt = assignedDaysAgo is { } d ? Now.AddDays(-d) : null,
    };

    private static IEnumerable<int> Numbers(PullRequestSort sort, params PullRequest[] prs) =>
        MyPullRequestsViewModel.Sorted(prs, PullRequestFilter.All, sort).Select(p => p.Number);

    [Fact]
    public void Sorts_by_created_time_in_both_directions()
    {
        var prs = new[] { Pr(1, "b", 5), Pr(2, "a", 1), Pr(3, "c", 9) };

        Assert.Equal([2, 1, 3], Numbers(PullRequestSort.CreatedNewest, prs));
        Assert.Equal([3, 1, 2], Numbers(PullRequestSort.CreatedOldest, prs));
    }

    [Fact]
    public void Sorts_by_author_name()
    {
        Assert.Equal([2, 3, 1], Numbers(PullRequestSort.Author, Pr(1, "Zed", 1), Pr(2, "amy", 1), Pr(3, "Bob", 1)));
    }

    [Fact]
    public void Assigned_sort_puts_pull_requests_never_assigned_to_me_last()
    {
        var prs = new[] { Pr(1, "a", 1), Pr(2, "a", 1, assignedDaysAgo: 4), Pr(3, "a", 1, assignedDaysAgo: 1) };

        Assert.Equal([3, 2, 1], Numbers(PullRequestSort.AssignedNewest, prs));
        Assert.Equal([2, 3, 1], Numbers(PullRequestSort.AssignedOldest, prs));
    }

    [Fact]
    public void Type_sort_lists_assigned_before_the_rest()
    {
        Assert.Equal([2, 1], Numbers(PullRequestSort.Type, Pr(1, "a", 1), Pr(2, "a", 1, assignedDaysAgo: 2)));
    }
}
