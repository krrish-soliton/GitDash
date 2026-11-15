using GitDash.Application.Abstractions;
using GitDash.Application.Accounts;
using GitDash.Application.Settings;
using GitDash.Domain.Actions;
using GitDash.Domain.PullRequests;

namespace GitDash.Application.Monitoring;

/// <summary>What GitHub Actions is doing across the monitored repositories.</summary>
public sealed record ActionsState(
    IReadOnlyList<WorkflowRun> Runs,
    IReadOnlyDictionary<string, int> RunsPerPullRequest,
    DateTimeOffset? UpdatedAt = null,
    string? Error = null)
{
    public static readonly ActionsState Empty = new([], new Dictionary<string, int>());

    public static string KeyOf(RepositoryRef repository, int number) => $"{repository.FullName.ToLowerInvariant()}#{number}";

    /// <summary>How many of the repository's recent runs belonged to this pull request.</summary>
    public int RunCount(RepositoryRef repository, int number) =>
        RunsPerPullRequest.GetValueOrDefault(KeyOf(repository, number));

    public IEnumerable<WorkflowRun> RunsFor(RepositoryRef repository, int number) =>
        Runs.Where(r => r.BelongsTo(repository, number));
}

/// <summary>
/// Polls workflow runs: every few seconds while the Actions panel is open (live progress bars), otherwise only
/// now and then, enough to keep the per-pull-request run counts and the tags on the cards current.
/// </summary>
public sealed class ActionsMonitor : IAsyncDisposable
{
    private static readonly TimeSpan LiveInterval = TimeSpan.FromSeconds(8), ActiveInterval = TimeSpan.FromSeconds(15),
        IdleInterval = TimeSpan.FromSeconds(60), RecentFailure = TimeSpan.FromMinutes(30);
    private const int KeepRuns = 40, MaxParallel = 3;

    private readonly SettingsService _settings;
    private readonly AccountService _accounts;
    private readonly IGitHubGateway _gitHub;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<Guid, string?> _tokens = [];
    private CancellationTokenSource _wake = new();
    private readonly HashSet<long> _failedSeen = [];
    private volatile bool _live;
    private bool _baselined;
    private Task? _loop;

    public ActionsMonitor(SettingsService settings, AccountService accounts, IGitHubGateway gitHub, TimeProvider time)
    {
        _settings = settings;
        _accounts = accounts;
        _gitHub = gitHub;
        _time = time;
        _settings.Changed += _ =>
        {
            lock (_tokens) _tokens.Clear();
            Wake();
        };
    }

    public ActionsState State { get; private set; } = ActionsState.Empty;

    /// <summary>Raised on a background thread whenever <see cref="State"/> is replaced.</summary>
    public event Action<ActionsState>? Changed;

    /// <summary>Raised on a background thread once for every run of a pull request that has just failed.</summary>
    public event Action<WorkflowRun>? RunFailed;

    public void Start() => _loop ??= Task.Run(() => RunAsync(_shutdown.Token));

    /// <summary>Poll quickly while somebody is watching the progress bars.</summary>
    public void SetLive(bool live)
    {
        if (_live == live) return;
        _live = live;
        if (live) Wake();
    }

    private void Wake()
    {
        try
        {
            _wake.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Replaced by the loop a moment ago; the next cycle starts anyway.
        }
    }

    private async Task RunAsync(CancellationToken shutdown)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), _time, shutdown).ConfigureAwait(false); // pull requests load first
            while (!shutdown.IsCancellationRequested)
            {
                var wake = new CancellationTokenSource();
                Interlocked.Exchange(ref _wake, wake).Dispose();
                try
                {
                    await RefreshAsync(shutdown).ConfigureAwait(false);
                }
                catch (Exception ex) when (!shutdown.IsCancellationRequested)
                {
                    // Any failure, a request timing out included (that arrives as a cancellation), must not end the polling:
                    // it used to stop the live updates until the app was restarted.
                    Publish(State with { Error = ex.Message });
                }

                using var wait = CancellationTokenSource.CreateLinkedTokenSource(shutdown, wake.Token);
                try
                {
                    await Task.Delay(
                        _live ? LiveInterval : State.Runs.Any(r => r.IsActive) ? ActiveInterval : IdleInterval,
                        _time, wait.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Woken early or shutting down.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>One poll of every monitored repository. Public so tests can run it directly.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var settings = _settings.Current;
        var repositories = settings.Repositories
            .Where(r => settings.Accounts.Any(a => a.Id == r.AccountId))
            .GroupBy(r => r.ToRef().FullName.ToLowerInvariant())
            .Select(g => g.First())
            .ToList();
        if (repositories.Count == 0)
        {
            if (State.Runs.Count > 0) Publish(ActionsState.Empty);
            return;
        }

        var all = new List<WorkflowRun>();
        string? error = null;
        await Parallel.ForEachAsync(
            repositories, new ParallelOptions { MaxDegreeOfParallelism = MaxParallel, CancellationToken = ct },
            async (repo, token) =>
            {
                try
                {
                    var secret = await TokenAsync(repo.AccountId, token).ConfigureAwait(false);
                    if (secret is null) return;
                    var runs = await _gitHub.GetWorkflowRunsAsync(secret, repo.ToRef(), token).ConfigureAwait(false);
                    lock (all) all.AddRange(runs);
                }
                catch (Exception ex) when (ex is GitHubApiException or GitHubAuthorizationException or GitHubRateLimitException
                                               or HttpRequestException)
                {
                    error = ex.Message;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    error = "GitHub did not answer in time";
                }
            }).ConfigureAwait(false);

        var perPr = new Dictionary<string, int>();
        foreach (var run in all)
        {
            foreach (var number in run.PullRequests.Distinct())
            {
                var key = ActionsState.KeyOf(run.Repository, number);
                perPr[key] = perPr.GetValueOrDefault(key) + 1;
            }
        }

        // Running ones first, then the newest finished ones.
        var shown = all
            .OrderByDescending(r => r.IsActive)
            .ThenByDescending(r => r.IsActive ? r.StartedAt : r.UpdatedAt)
            .Take(KeepRuns)
            .ToList();

        AnnounceFailures(all);
        var fresh = new ActionsState(shown, perPr, _time.GetUtcNow(), error);
        if (!SameContent(State, fresh)) Publish(fresh);
    }

    /// <summary>Tells about runs that failed since the last poll. The first poll only learns what already failed.</summary>
    private void AnnounceFailures(IReadOnlyList<WorkflowRun> runs)
    {
        var now = _time.GetUtcNow();
        foreach (var run in runs.Where(r => r.Failed))
        {
            var isNew = _failedSeen.Add(run.Id);
            if (isNew && _baselined && run.PullRequests.Count > 0 && now - run.UpdatedAt <= RecentFailure) RunFailed?.Invoke(run);
        }

        _baselined = true;
    }

    private static bool SameContent(ActionsState a, ActionsState b) =>
        a.Error == b.Error && a.Runs.Count == b.Runs.Count && a.Runs.SequenceEqual(b.Runs) &&
        a.RunsPerPullRequest.Count == b.RunsPerPullRequest.Count &&
        a.RunsPerPullRequest.All(p => b.RunsPerPullRequest.GetValueOrDefault(p.Key, -1) == p.Value);

    private async Task<string?> TokenAsync(Guid accountId, CancellationToken ct)
    {
        lock (_tokens)
        {
            if (_tokens.TryGetValue(accountId, out var cached) && cached is not null) return cached;
        }

        var token = await _accounts.GetTokenAsync(accountId, ct).ConfigureAwait(false);
        lock (_tokens) _tokens[accountId] = token;
        return token;
    }

    private void Publish(ActionsState state)
    {
        State = state;
        Changed?.Invoke(state);
    }

    public async ValueTask DisposeAsync()
    {
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
    }
}
