using GitDash.Domain.PullRequests;

namespace GitDash.Tests;

internal static class TestData
{
    public const string Me = "krish";
    public static readonly DateTimeOffset Now = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);
    public static readonly RepositoryRef Repo = new("acme", "api");
    public static readonly Guid Account = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static PullRequest Pr(string id, string author = "maya", Func<PullRequest, PullRequest>? with = null)
    {
        var pr = new PullRequest
        {
            Id = id,
            AccountId = Account,
            ViewerLogin = Me,
            Repository = Repo,
            Number = Math.Abs(id.GetHashCode() % 1000),
            Title = $"Title {id}",
            Url = $"https://github.com/acme/api/pull/{id}",
            Author = author,
            CreatedAt = Now.AddDays(-2),
            UpdatedAt = Now,
        };
        return with is null ? pr : with(pr);
    }

    public static RepositorySnapshot Snapshot(IEnumerable<PullRequest> open, IEnumerable<PullRequest>? merged = null)
    {
        var list = open.ToList();
        return new RepositorySnapshot(Account, Repo, list, merged?.ToList() ?? [], list.Count);
    }
}
