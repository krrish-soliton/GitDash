using GitDash.Domain.PullRequests;

namespace GitDash.Domain.Insights;

/// <summary>One person on a repository "floor".</summary>
/// <param name="Raised">Open pull requests they authored.</param>
/// <param name="Assigned">Open pull requests where they are the assignee.</param>
/// <param name="ToReview">Open pull requests where their review is requested.</param>
/// <param name="AssignedUnder">Distinct pull requests waiting on them in either role.</param>
/// <param name="OldestAssignedSince">Since when the longest-waiting PR assigned to them has been on their desk.</param>
/// <param name="OldestReviewSince">Since when the longest-waiting PR awaiting their approval has been on their desk.</param>
public sealed record PersonStats(
    string Login, int Raised, int Assigned, int ToReview, int AssignedUnder,
    DateTimeOffset? OldestAssignedSince, DateTimeOffset? OldestReviewSince)
{
    /// <summary>The pull request numbers behind the counts, so the desk can label each book.</summary>
    public IReadOnlyList<int> AssignedNumbers { get; init; } = [];
    public IReadOnlyList<int> ReviewNumbers { get; init; } = [];
    public IReadOnlyList<int> RaisedNumbers { get; init; } = [];
}

/// <summary>Open pull request statistics for one repository.</summary>
public sealed record RepositoryFloor(
    RepositoryRef Repository,
    IReadOnlyList<PersonStats> People,
    int OpenCount,
    int DraftCount,
    int AwaitingReviewCount,
    int ApprovedCount,
    int ChangesRequestedCount,
    TimeSpan AverageAge,
    TimeSpan OldestAge,
    TimeSpan? OldestAssignedWait,
    TimeSpan? OldestReviewWait);

public static class PeopleInsights
{
    public static IReadOnlyList<RepositoryFloor> Build(IEnumerable<PullRequest> open, DateTimeOffset now) =>
        open.GroupBy(p => p.Id).Select(g => g.First())
            .GroupBy(p => p.Repository.FullName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => BuildFloor(g.ToList(), now))
            .ToList();

    private sealed class Tally
    {
        public int Raised, Assigned, ToReview, Under;
        public readonly List<int> RaisedNumbers = [], AssignedNumbers = [], ReviewNumbers = [];
        public DateTimeOffset? OldestAssigned, OldestReview;
    }

    private static RepositoryFloor BuildFloor(List<PullRequest> prs, DateTimeOffset now)
    {
        var people = new Dictionary<string, Tally>(Login.Comparer);
        Tally Of(string login) => people.TryGetValue(login, out var t) ? t : people[login] = new Tally();

        foreach (var pr in prs)
        {
            Of(pr.Author).Raised++;
            Of(pr.Author).RaisedNumbers.Add(pr.Number);
            foreach (var login in pr.Assignees)
            {
                var tally = Of(login);
                tally.Assigned++;
                tally.AssignedNumbers.Add(pr.Number);
                var since = pr.AssignedSinceFor(login);
                if (tally.OldestAssigned is null || since < tally.OldestAssigned) tally.OldestAssigned = since;
            }

            foreach (var login in pr.RequestedReviewers)
            {
                var tally = Of(login);
                tally.ToReview++;
                tally.ReviewNumbers.Add(pr.Number);
                var since = pr.ReviewSinceFor(login);
                if (tally.OldestReview is null || since < tally.OldestReview) tally.OldestReview = since;
            }

            foreach (var login in pr.Assignees.Concat(pr.RequestedReviewers).Distinct(Login.Comparer)) Of(login).Under++;
        }

        var stats = people
            .Select(p => new PersonStats(
                p.Key, p.Value.Raised, p.Value.Assigned, p.Value.ToReview, p.Value.Under, p.Value.OldestAssigned, p.Value.OldestReview)
            {
                AssignedNumbers = p.Value.AssignedNumbers, ReviewNumbers = p.Value.ReviewNumbers, RaisedNumbers = p.Value.RaisedNumbers,
            })
            .OrderByDescending(p => p.Raised + p.AssignedUnder)
            .ThenBy(p => p.Login, Login.Comparer)
            .ToList();

        var ages = prs.Select(p => now - p.CreatedAt).ToList();
        var assignedWaits = prs.SelectMany(pr => pr.Assignees.Select(l => now - pr.AssignedSinceFor(l))).ToList();
        var reviewWaits = prs.SelectMany(pr => pr.RequestedReviewers.Select(l => now - pr.ReviewSinceFor(l))).ToList();
        return new RepositoryFloor(
            prs[0].Repository,
            stats,
            prs.Count,
            prs.Count(p => p.IsDraft),
            prs.Count(p => !p.IsDraft && p.Verdict is null),
            prs.Count(p => p.Verdict == ReviewState.Approved),
            prs.Count(p => p.Verdict == ReviewState.ChangesRequested),
            TimeSpan.FromTicks((long)ages.Average(a => a.Ticks)),
            ages.Max(),
            assignedWaits.Count == 0 ? null : assignedWaits.Max(),
            reviewWaits.Count == 0 ? null : reviewWaits.Max());
    }
}
