using GitDash.Application.Settings;
using GitDash.Domain.Notifications;
using GitDash.Domain.Timers;

namespace GitDash.Application.Abstractions;

/// <summary>OS-protected storage for access tokens. Secrets never go into the settings file.</summary>
public interface ISecretStore
{
    Task SaveAsync(string key, string secret, CancellationToken ct = default);
    Task<string?> ReadAsync(string key, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
}

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(AppSettings settings, CancellationToken ct = default);
}

/// <summary>Keeps the notification history across restarts.</summary>
public interface INotificationStore
{
    Task<IReadOnlyList<GitDash.Application.Notifications.NotificationEntry>> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(IReadOnlyList<GitDash.Application.Notifications.NotificationEntry> entries, CancellationToken ct = default);
}

public interface ITimerSessionStore
{
    Task<IReadOnlyList<TimerSession>> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(IReadOnlyList<TimerSession> sessions, CancellationToken ct = default);
}

public enum ToastTone
{
    Assigned,
    Review,
    Comment,
    Merged,
    Timer,
    Removed,
}

/// <summary>Something to tell the user, independent of how it will be displayed.</summary>
/// <param name="Actor">GitHub login of the person the notification is about (commenter, author, merger), if any.</param>
public sealed record ToastRequest(string Title, string Message, ToastTone Tone, string? Url = null, string? Actor = null);

/// <summary>The app's own animated toast windows.</summary>
public interface IToastPresenter
{
    void Show(ToastRequest request);
}

/// <summary>The operating system's notification centre.</summary>
public interface INativeNotifier
{
    Task ShowAsync(string title, string message, CancellationToken ct = default);
}

/// <summary>Reads the token of the user's existing GitHub CLI login (<c>gh auth token</c>).</summary>
public interface IGitHubCliTokenSource
{
    /// <summary>
    /// Returns the token. Throws <see cref="InvalidOperationException"/> with a message fit for the user when the
    /// GitHub CLI is not installed or not logged in.
    /// </summary>
    Task<string> GetTokenAsync(CancellationToken ct = default);
}

/// <summary>Opens links in the default browser, restricted to GitHub hosts.</summary>
public interface IUrlLauncher
{
    bool Open(string url);
}

/// <summary>Downloads profile pictures. Returns null when a picture is unavailable; never throws.</summary>
public interface IAvatarSource
{
    Task<byte[]?> GetAsync(string url, CancellationToken ct = default);
}

/// <summary>A short, unobtrusive sound for "time is up" moments and notifications.</summary>
public interface IAlarm
{
    /// <summary>Plays the sound chosen in the settings (nothing when sounds are switched off).</summary>
    void Play();

    /// <summary>Plays one sound so the user can hear it before choosing it; ignores the on/off switch.</summary>
    void Preview(string soundId, string? customPath);
}

/// <summary>One repository as last seen: enough to show the dashboard instantly and to resume change detection.</summary>
public sealed record CachedRepository(
    Guid AccountId, string Owner, string Name, Domain.PullRequests.RepositorySnapshot Snapshot,
    string? ETag, DateTimeOffset SyncedAt);

/// <summary>
/// Local copy of everything the monitor learned, so the app opens with data (cache-first) and a restart
/// does not have to re-download repositories that did not change (the ETag is kept too).
/// </summary>
public interface IStateCache
{
    Task<IReadOnlyList<CachedRepository>> LoadRepositoriesAsync(CancellationToken ct = default);
    Task SaveRepositoriesAsync(IReadOnlyList<CachedRepository> items, CancellationToken ct = default);
    Task RemoveRepositoryAsync(Guid accountId, string owner, string name, CancellationToken ct = default);
    Task<IReadOnlyList<Domain.Notifications.PullRequestNotification>> LoadActivityAsync(CancellationToken ct = default);
    Task SaveActivityAsync(
        IReadOnlyList<Domain.Notifications.PullRequestNotification> items, CancellationToken ct = default);
}

/// <summary>Cache that remembers nothing (tests, demo mode).</summary>
public sealed class NullStateCache : IStateCache
{
    public Task<IReadOnlyList<CachedRepository>> LoadRepositoriesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CachedRepository>>([]);

    public Task SaveRepositoriesAsync(IReadOnlyList<CachedRepository> items, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task RemoveRepositoryAsync(Guid accountId, string owner, string name, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<Domain.Notifications.PullRequestNotification>> LoadActivityAsync(
        CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Domain.Notifications.PullRequestNotification>>([]);

    public Task SaveActivityAsync(
        IReadOnlyList<Domain.Notifications.PullRequestNotification> items, CancellationToken ct = default) =>
        Task.CompletedTask;
}
