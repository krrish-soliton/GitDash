using GithubDash.Domain.Insights;
using GithubDash.Domain.Notifications;
using GithubDash.Domain.PullRequests;

namespace GithubDash.Application.Monitoring;

/// <summary>Immutable view of everything the monitor currently knows.</summary>
public sealed record MonitorState
{
    public static readonly MonitorState Empty = new();

    public IReadOnlyList<PullRequest> Open { get; init; } = [];
    public DashboardSummary Summary { get; init; } = DashboardSummary.Empty;
    public IReadOnlyList<PullRequestNotification> RecentActivity { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];
    /// <summary>Profile picture URL per login.</summary>
    public IReadOnlyDictionary<string, string> Avatars { get; init; } = new Dictionary<string, string>();

    /// <summary>Goes up every time pull request data actually changed; lets the UI skip no-op updates.</summary>
    public int Revision { get; init; }

    /// <summary>Last time GitHub was asked for changes.</summary>
    public DateTimeOffset? LastCheckedAt { get; init; }

    /// <summary>Last time changed data was downloaded.</summary>
    public DateTimeOffset? LastSyncedAt { get; init; }
    public bool IsSyncing { get; init; }
    public int RepositoryCount { get; init; }
}
