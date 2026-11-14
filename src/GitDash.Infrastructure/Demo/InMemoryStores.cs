using GitDash.Application.Abstractions;
using GitDash.Application.Settings;
using GitDash.Domain.Timers;

namespace GitDash.Infrastructure.Demo;

public sealed class InMemorySettingsStore : ISettingsStore
{
    private AppSettings _settings = new();

    public Task<AppSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(_settings);

    public Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        _settings = settings;
        return Task.CompletedTask;
    }
}

public sealed class InMemoryTimerSessionStore : ITimerSessionStore
{
    private IReadOnlyList<TimerSession> _sessions = [];

    public Task<IReadOnlyList<TimerSession>> LoadAsync(CancellationToken ct = default) => Task.FromResult(_sessions);

    public Task SaveAsync(IReadOnlyList<TimerSession> sessions, CancellationToken ct = default)
    {
        _sessions = sessions;
        return Task.CompletedTask;
    }
}

/// <summary>Device flow is meaningless without GitHub; say so instead of failing obscurely.</summary>
public sealed class DemoDeviceFlowAuthenticator : IDeviceFlowAuthenticator
{
    public Task<DeviceCodePrompt> RequestCodeAsync(string clientId, CancellationToken ct) =>
        throw new InvalidOperationException("Browser sign-in is not available in demo mode.");

    public Task<string> WaitForTokenAsync(string clientId, DeviceCodePrompt prompt, CancellationToken ct) =>
        throw new InvalidOperationException("Browser sign-in is not available in demo mode.");
}

public sealed class InMemoryNotificationStore : INotificationStore
{
    private IReadOnlyList<GitDash.Application.Notifications.NotificationEntry> _entries = [];

    public Task<IReadOnlyList<GitDash.Application.Notifications.NotificationEntry>> LoadAsync(CancellationToken ct = default) =>
        Task.FromResult(_entries);

    public Task SaveAsync(IReadOnlyList<GitDash.Application.Notifications.NotificationEntry> entries, CancellationToken ct = default)
    {
        _entries = entries;
        return Task.CompletedTask;
    }
}
