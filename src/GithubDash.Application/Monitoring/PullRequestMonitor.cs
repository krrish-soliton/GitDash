using GithubDash.Application.Abstractions;
using GithubDash.Application.Accounts;
using GithubDash.Application.Settings;
using GithubDash.Domain.Insights;
using GithubDash.Domain.Notifications;
using GithubDash.Domain.PullRequests;

namespace GithubDash.Application.Monitoring;

/// <summary>
/// Keeps pull request data live.
/// GitHub cannot push to a desktop app, so every few seconds each monitored repository is asked
/// "anything new?" with a conditional request. Unchanged repositories answer "not modified" (free of API
/// quota and a few hundred bytes); only a repository that really changed is downloaded again.
/// </summary>
public sealed class PullRequestMonitor : IAsyncDisposable
{
    private const int MaxParallelRequests = 4;
    private const int MaxRecentActivity = 40;
    private static readonly TimeSpan FullResyncInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RateLimitPause = TimeSpan.FromSeconds(90);

    private readonly SettingsService _settings;
    private readonly AccountService _accounts;
    private readonly IGitHubGateway _gitHub;
    private readonly NotificationRouter _notifications;
    private readonly TimeProvider _time;
    private readonly IStateCache _cache;

    private sealed class Tracked
    {
        public RepositorySnapshot? Snapshot;
        public string? ETag;
        public DateTimeOffset LastFullSync;

        /// <summary>Loaded from an old cache: replace it without notifying about what happened while the app was closed.</summary>
        public bool Silent;
    }

    private readonly Dictionary<(Guid, string), Tracked> _tracked = [];
    private readonly Dictionary<(Guid, string), string> _errors = [];
    private readonly Dictionary<Guid, string?> _tokens = [];
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private CancellationTokenSource _wake = new();
    private DateTimeOffset _pausedUntil;
    private volatile bool _forceFull;
    private Task? _loop;

    public PullRequestMonitor(
        SettingsService settings,
        AccountService accounts,
        IGitHubGateway gitHub,
        NotificationRouter notifications,
        TimeProvider time,
        IStateCache? cache = null)
    {
        _cache = cache ?? new NullStateCache();
        _settings = settings;
        _accounts = accounts;
        _gitHub = gitHub;
        _notifications = notifications;
        _time = time;
        _settings.Changed += OnSettingsChanged;
    }

    public MonitorState State { get; private set; } = MonitorState.Empty;

    /// <summary>Raised on a background thread whenever <see cref="State"/> is replaced.</summary>
    public event Action<MonitorState>? StateChanged;

    public void Start() => _loop ??= Task.Run(async () =>
    {
        await RestoreAsync(_shutdown.Token).ConfigureAwait(false);
        await RunAsync(_shutdown.Token).ConfigureAwait(false);
    });

    /// <summary>Cached data younger than this is the baseline for "what changed while I was away".</summary>
    private static readonly TimeSpan CacheBaselineAge = TimeSpan.FromHours(6);

    /// <summary>
    /// Cache-first start: show what was saved last time immediately, and resume the saved ETags so repositories that did
    /// not change since then cost no download. A broken cache is ignored; the normal sync fills everything in.
    /// </summary>
    public async Task RestoreAsync(CancellationToken ct = default)
    {
        try
        {
            var cached = await _cache.LoadRepositoriesAsync(ct).ConfigureAwait(false);
            var activity = await _cache.LoadActivityAsync(ct).ConfigureAwait(false);
            if (cached.Count == 0) return;

            await _syncGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var settings = _settings.Current;
                var now = _time.GetUtcNow();
                var restored = 0;
                foreach (var item in cached)
                {
                    var monitored = settings.Repositories.FirstOrDefault(r =>
                        r.AccountId == item.AccountId && r.ToRef().Matches(new RepositoryRef(item.Owner, item.Name)));
                    if (monitored is null || settings.Accounts.All(a => a.Id != item.AccountId)) continue;

                    var recent = now - item.SyncedAt < CacheBaselineAge;
                    _tracked[KeyOf(monitored)] = new Tracked
                    {
                        Snapshot = item.Snapshot,
                        ETag = recent ? item.ETag : null,
                        LastFullSync = recent ? item.SyncedAt : DateTimeOffset.MinValue,
                        Silent = !recent,
                    };
                    restored++;
                }

                if (restored == 0) return;
                var snapshots = _tracked.Values.Select(t => t.Snapshot).OfType<RepositorySnapshot>().ToList();
                var open = snapshots.SelectMany(s => s.Open).OrderByDescending(p => p.UpdatedAt).ToList();
                var avatars = new Dictionary<string, string>(Login.Comparer);
                foreach (var (login, url) in snapshots.SelectMany(sn => sn.Avatars)) avatars[login] = url;

                Publish(State with
                {
                    Open = open,
                    Summary = DashboardSummary.From(open),
                    Avatars = avatars,
                    RecentActivity = [.. activity.Take(MaxRecentActivity)],
                    Revision = State.Revision + 1,
                    LastSyncedAt = cached.Max(c => c.SyncedAt),
                    RepositoryCount = restored,
                });
            }
            finally
            {
                _syncGate.Release();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // The cache is an optimisation only.
        }
    }

    private void Persist(IReadOnlyList<CachedRepository> repositories, IReadOnlyList<MonitoredRepository> removed, MonitorState? activityFrom)
    {
        // Write-behind: the UI never waits for the disk.
        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var gone in removed)
                    await _cache.RemoveRepositoryAsync(gone.AccountId, gone.Owner, gone.Name).ConfigureAwait(false);
                if (repositories.Count > 0) await _cache.SaveRepositoriesAsync(repositories).ConfigureAwait(false);
                if (activityFrom is not null) await _cache.SaveActivityAsync(activityFrom.RecentActivity).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best effort.
            }
        });
    }

    /// <summary>Downloads everything again right now, changed or not.</summary>
    public void RequestRefresh()
    {
        _forceFull = true;
        Wake();
    }

    private void OnSettingsChanged(AppSettings _)
    {
        lock (_tokens) _tokens.Clear(); // accounts may have been added or removed
        Wake();
    }

    private void Wake()
    {
        try
        {
            _wake.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A cycle is already starting.
        }
    }

    private async Task RunAsync(CancellationToken shutdown)
    {
        while (!shutdown.IsCancellationRequested)
        {
            var wake = new CancellationTokenSource();
            Interlocked.Exchange(ref _wake, wake).Dispose();

            await SyncAsync(shutdown).ConfigureAwait(false);

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(shutdown, wake.Token);
            try
            {
                await Task.Delay(_settings.Current.LiveCheckInterval, _time, wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Woken early or shutting down; the loop condition decides which.
            }
        }
    }

    /// <summary>One live-check cycle. Public so tests (and "sync now") can run it directly.</summary>
    public async Task SyncAsync(CancellationToken ct = default)
    {
        await _syncGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var settings = _settings.Current;
            var now = _time.GetUtcNow();
            var force = _forceFull;
            _forceFull = false;

            if (now < _pausedUntil && !force) return;

            var wanted = settings.Repositories
                .Where(r => settings.Accounts.Any(a => a.Id == r.AccountId))
                .ToList();
            var wantedKeys = wanted.Select(KeyOf).ToHashSet();
            var removed = false;
            var droppedRepositories = new List<MonitoredRepository>();
            foreach (var stale in _tracked.Keys.Where(k => !wantedKeys.Contains(k)).ToList())
            {
                _tracked.Remove(stale);
                _errors.Remove(stale);
                removed = true;
                var parts = stale.Item2.Split('/', 2);
                if (parts.Length == 2) droppedRepositories.Add(new MonitoredRepository(stale.Item1, parts[0], parts[1]));
            }

            // Show the spinner only when a real download is certain (first run or manual refresh).
            if (force || wanted.Any(r => !_tracked.ContainsKey(KeyOf(r))))
            {
                Publish(State with { IsSyncing = true });
            }

            var fresh = new Dictionary<(Guid, string), RepositorySnapshot>();
            var errorsBefore = _errors.Count;
            var rateLimited = false;

            await Parallel.ForEachAsync(
                wanted,
                new ParallelOptions { MaxDegreeOfParallelism = MaxParallelRequests, CancellationToken = ct },
                async (repo, token) =>
                {
                    var key = KeyOf(repo);
                    var account = settings.Accounts.First(a => a.Id == repo.AccountId);
                    Tracked tracked;
                    lock (_tracked)
                    {
                        if (!_tracked.TryGetValue(key, out tracked!)) _tracked[key] = tracked = new Tracked();
                    }

                    try
                    {
                        var secret = await GetTokenAsync(account.Id, token).ConfigureAwait(false)
                            ?? throw new GitHubAuthorizationException("Not signed in.");

                        // Probe first, download second: a change that lands in between is caught by the next probe.
                        var probe = await _gitHub.ProbeAsync(secret, repo.ToRef(), tracked.ETag, token)
                            .ConfigureAwait(false);
                        tracked.ETag = probe.ETag;

                        var due = force || tracked.Snapshot is null || probe.Changed ||
                                  now - tracked.LastFullSync >= FullResyncInterval;
                        if (due)
                        {
                            var snapshot = await _gitHub
                                .GetSnapshotAsync(secret, account.Id, account.Login, repo.ToRef(), token)
                                .ConfigureAwait(false);
                            tracked.LastFullSync = now;
                            lock (fresh) fresh[key] = snapshot;
                        }

                        lock (_errors) _errors.Remove(key);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        if (ex is GitHubRateLimitException) rateLimited = true;
                        // Messages come from our own gateway and never contain the token.
                        lock (_errors) _errors[key] = $"{repo.ToRef().FullName} ({account.Login}): {ex.Message}";
                    }
                }).ConfigureAwait(false);

            if (rateLimited) _pausedUntil = now + RateLimitPause;

            var notifications = new List<PullRequestNotification>();
            var dataChanged = removed;
            foreach (var (key, snapshot) in fresh)
            {
                var tracked = _tracked[key];
                if (!tracked.Silent)
                {
                    notifications.AddRange(
                        PullRequestChangeDetector.Detect(tracked.Snapshot, snapshot, settings.Notifications, now));
                }

                tracked.Silent = false;
                dataChanged |= !SameContent(tracked.Snapshot, snapshot);
                tracked.Snapshot = snapshot;
            }

            // The same event can be seen through two accounts; report it once.
            var unique = notifications.DistinctBy(n => (n.Kind, n.PullRequest.Id)).ToList();

            if (dataChanged || unique.Count > 0 || State.RepositoryCount != wanted.Count)
            {
                var snapshots = _tracked.Values.Select(t => t.Snapshot).OfType<RepositorySnapshot>().ToList();
                var open = snapshots.SelectMany(s => s.Open).OrderByDescending(p => p.UpdatedAt).ToList();
                var avatars = new Dictionary<string, string>(Login.Comparer);
                foreach (var (login, url) in snapshots.SelectMany(s => s.Avatars)) avatars[login] = url;

                Publish(State with
                {
                    Open = open,
                    Summary = DashboardSummary.From(open),
                    Avatars = avatars,
                    RecentActivity =
                    [
                        .. unique.AsEnumerable().Reverse(),
                        .. State.RecentActivity.Take(MaxRecentActivity - Math.Min(unique.Count, MaxRecentActivity)),
                    ],
                    Errors = [.. _errors.Values],
                    Revision = State.Revision + 1,
                    LastSyncedAt = now,
                    LastCheckedAt = now,
                    IsSyncing = false,
                    RepositoryCount = wanted.Count,
                });
            }
            else
            {
                // Nothing changed: a light heartbeat so the UI can show that the app is still watching.
                Publish(State with
                {
                    Errors = _errors.Count == 0 && errorsBefore == 0 ? State.Errors : [.. _errors.Values],
                    LastCheckedAt = now,
                    IsSyncing = false,
                });
            }

            if (fresh.Count > 0 || droppedRepositories.Count > 0)
            {
                var saved = fresh.Select(kv => new CachedRepository(
                    kv.Value.AccountId, kv.Value.Repository.Owner, kv.Value.Repository.Name, kv.Value,
                    _tracked.TryGetValue(kv.Key, out var t) ? t.ETag : null, now)).ToList();
                Persist(saved, droppedRepositories, unique.Count > 0 ? State : null);
            }

            foreach (var notification in unique)
            {
                await _notifications.PublishAsync(notification, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            Publish(State with { IsSyncing = false, Errors = [ex.Message] });
        }
        finally
        {
            _syncGate.Release();
        }
    }

    public async Task<PullRequestDetails> GetDetailsAsync(PullRequest pr, CancellationToken ct = default)
    {
        var token = await GetTokenAsync(pr.AccountId, ct).ConfigureAwait(false)
            ?? throw new GitHubAuthorizationException("Not signed in.");
        return await _gitHub.GetDetailsAsync(token, pr.Repository, pr.Number, ct).ConfigureAwait(false);
    }

    private static (Guid, string) KeyOf(MonitoredRepository repo) =>
        (repo.AccountId, repo.ToRef().FullName.ToLowerInvariant());

    /// <summary>Tokens are read from the OS keystore once and then kept in memory until accounts change.</summary>
    private async Task<string?> GetTokenAsync(Guid accountId, CancellationToken ct)
    {
        lock (_tokens)
        {
            if (_tokens.TryGetValue(accountId, out var cached) && cached is not null) return cached;
        }

        var token = await _accounts.GetTokenAsync(accountId, ct).ConfigureAwait(false);
        lock (_tokens) _tokens[accountId] = token;
        return token;
    }

    /// <summary>A periodic safety resync often returns exactly what we already have; do not redraw for that.</summary>
    private static bool SameContent(RepositorySnapshot? a, RepositorySnapshot b) =>
        a is not null && a.Open.Count == b.Open.Count &&
        a.Open.Zip(b.Open).All(p =>
            p.First.Id == p.Second.Id && p.First.UpdatedAt == p.Second.UpdatedAt &&
            p.First.TotalComments == p.Second.TotalComments);

    private void Publish(MonitorState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }

    public async ValueTask DisposeAsync()
    {
        _settings.Changed -= OnSettingsChanged;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        _shutdown.Dispose();
        _syncGate.Dispose();
    }
}
