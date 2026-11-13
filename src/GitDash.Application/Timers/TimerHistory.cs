using GitDash.Application.Abstractions;
using GitDash.Domain.Timers;

namespace GitDash.Application.Timers;

/// <summary>Keeps finished timer sessions, newest first, capped so the file never grows without bound.</summary>
public sealed class TimerHistory(ITimerSessionStore store)
{
    private const int MaxSessions = 500;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<TimerSession> _sessions = [];

    public IReadOnlyList<TimerSession> Sessions => _sessions;

    public event Action? Changed;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        _sessions = [.. (await store.LoadAsync(ct).ConfigureAwait(false)).OrderByDescending(s => s.StartedAt)];
        Changed?.Invoke();
    }

    public async Task AddAsync(TimerSession session, CancellationToken ct = default)
    {
        if (session.Duration < TimeSpan.FromSeconds(1)) return;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _sessions = [session, .. _sessions.Take(MaxSessions - 1)];
            await store.SaveAsync(_sessions, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
    }

    public TimeSpan TotalFor(string pullRequestUrl) =>
        TimeSpan.FromTicks(_sessions
            .Where(s => s.Kind == TimerKind.PullRequestReview && s.PullRequestUrl == pullRequestUrl)
            .Sum(s => s.Duration.Ticks));
}
