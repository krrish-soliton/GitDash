using GitDash.Domain.Insights;
using GitDash.Domain.PullRequests;

namespace GitDash.Application.Abstractions;

public sealed record GitHubUser(string Login, string? Name, string? AvatarUrl = null);

/// <summary>Public profile of a signed-in account, for the home page.</summary>
public sealed record GitHubProfile
{
    public required string Login { get; init; }
    public string? Name { get; init; }
    public string? AvatarUrl { get; init; }
    public string? Bio { get; init; }
    public string? Company { get; init; }
    public string? Location { get; init; }
    public string? Url { get; init; }
    public DateTimeOffset? JoinedAt { get; init; }
    public int Followers { get; init; }
    public int Following { get; init; }
    public int Repositories { get; init; }
    public int OpenPullRequests { get; init; }
    public int MergedPullRequests { get; init; }
    public int TotalPullRequests { get; init; }
    public int ContributionsThisYear { get; init; }
    public IReadOnlyList<string> Organizations { get; init; } = [];
}

public sealed record RepositoryInfo(RepositoryRef Repository, bool IsPrivate, bool IsOrganization, string? Description);

/// <summary>Read-only port onto GitHub. The token is passed per call and never stored by the gateway.</summary>
public interface IGitHubGateway
{
    Task<GitHubUser> GetViewerAsync(string token, CancellationToken ct);

    Task<GitHubProfile> GetProfileAsync(string token, CancellationToken ct);

    /// <summary>Repositories the token can see: personal, collaborator and organization repositories.</summary>
    Task<IReadOnlyList<RepositoryInfo>> ListRepositoriesAsync(string token, CancellationToken ct);

    Task<RepositorySnapshot> GetSnapshotAsync(
        string token, Guid accountId, string viewerLogin, RepositoryRef repository, CancellationToken ct);

    /// <summary>Description, full timeline and all comments of one pull request.</summary>
    Task<PullRequestDetails> GetDetailsAsync(
        string token, RepositoryRef repository, int number, CancellationToken ct);

    /// <summary>Totals, recent pull requests, comments, reviews and stale branches of one person in one repository.</summary>
    Task<PersonActivity> GetPersonActivityAsync(
        string token, RepositoryRef repository, string login, CancellationToken ct);

    /// <summary>Workflow runs triggered by one person since the two given moments, and in total. Counts only; no run details.</summary>
    Task<PipelineRunCounts> GetPipelineRunCountsAsync(
        string token, RepositoryRef repository, string login, DateTimeOffset weekStart, DateTimeOffset monthStart,
        CancellationToken ct);

    /// <summary>
    /// Cheap "did anything change?" check for one repository. Pass the ETag from the previous probe;
    /// an unchanged repository answers "not modified", which does not use up API quota.
    /// </summary>
    Task<RepositoryProbe> ProbeAsync(string token, RepositoryRef repository, string? etag, CancellationToken ct);
}

public sealed record RepositoryProbe(bool Changed, string? ETag);

/// <summary>The token was rejected (expired, revoked or missing scopes).</summary>
public sealed class GitHubAuthorizationException(string message) : Exception(message);

/// <summary>GitHub answered, but with an error.</summary>
public sealed class GitHubApiException(string message) : Exception(message);

/// <summary>GitHub asked us to slow down.</summary>
public sealed class GitHubRateLimitException(string message) : Exception(message);
