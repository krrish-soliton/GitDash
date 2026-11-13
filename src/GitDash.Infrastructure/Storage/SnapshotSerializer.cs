using System.Text.Json;
using System.Text.Json.Serialization;
using GitDash.Domain.Notifications;
using GitDash.Domain.PullRequests;
using GitDash.Domain.Timers;

namespace GitDash.Infrastructure.Storage;

/// <summary>
/// JSON form of snapshots and activity, used by the SQLite cache. Case-insensitive dictionaries are rebuilt after
/// reading because System.Text.Json does not keep the comparer.
/// </summary>
public static class SnapshotSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        IgnoreReadOnlyProperties = true, // computed members such as IsAssignedToViewer are not data
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Write(RepositorySnapshot snapshot) => JsonSerializer.Serialize(snapshot, Options);

    public static RepositorySnapshot? ReadSnapshot(string json)
    {
        var snapshot = JsonSerializer.Deserialize<RepositorySnapshot>(json, Options);
        if (snapshot is null) return null;
        return snapshot with
        {
            Open = [.. snapshot.Open.Select(Fix)],
            RecentlyMerged = [.. snapshot.RecentlyMerged.Select(Fix)],
            Avatars = new Dictionary<string, string>(snapshot.Avatars, Login.Comparer),
        };
    }

    public static string Write(IReadOnlyList<PullRequestNotification> activity) =>
        JsonSerializer.Serialize(activity, Options);

    public static IReadOnlyList<PullRequestNotification> ReadActivity(string json) =>
        (JsonSerializer.Deserialize<List<PullRequestNotification>>(json, Options) ?? [])
        .Select(n => n with { PullRequest = Fix(n.PullRequest) })
        .ToList();

    public static string Write(IReadOnlyList<TimerSession> sessions) => JsonSerializer.Serialize(sessions, Options);

    public static IReadOnlyList<TimerSession> ReadSessions(string json) =>
        JsonSerializer.Deserialize<List<TimerSession>>(json, Options) ?? [];

    private static PullRequest Fix(PullRequest pr) =>
        pr with
        {
            WaitingSince = new Dictionary<string, DateTimeOffset>(pr.WaitingSince, Login.Comparer),
            AssignedSince = new Dictionary<string, DateTimeOffset>(pr.AssignedSince, Login.Comparer),
            ReviewRequestedSince = new Dictionary<string, DateTimeOffset>(pr.ReviewRequestedSince, Login.Comparer),
        };
}
