namespace GithubDash.Domain.PullRequests;

/// <summary>The state of one monitored repository at one point in time.</summary>
public sealed record RepositorySnapshot(
    Guid AccountId,
    RepositoryRef Repository,
    IReadOnlyList<PullRequest> Open,
    IReadOnlyList<PullRequest> RecentlyMerged,
    int OpenTotalCount)
{
    /// <summary>Profile picture URL per login, for everyone who appears in this snapshot.</summary>
    public IReadOnlyDictionary<string, string> Avatars { get; init; } =
        new Dictionary<string, string>(Login.Comparer);
}
