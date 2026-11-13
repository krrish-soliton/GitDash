using System.Collections.Concurrent;
using GitDash.Application.Abstractions;
using GitDash.Application.Accounts;
using GitDash.Application.Settings;
using GitDash.Domain.Insights;
using GitDash.Domain.PullRequests;

namespace GitDash.Application.Monitoring;

/// <summary>
/// Loads the full activity of one person in one monitored repository on demand (when a person is clicked),
/// with a short cache so reopening the same person is instant and does not spend API quota.
/// </summary>
public sealed class PersonActivityService(
    SettingsService settings, AccountService accounts, IGitHubGateway gateway, TimeProvider time)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(90);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, PersonActivity Value)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<PersonActivity> LoadAsync(
        string repositoryFullName, string login, bool forceRefresh, CancellationToken ct)
    {
        var key = $"{repositoryFullName}|{login}";
        var now = time.GetUtcNow();
        if (!forceRefresh && _cache.TryGetValue(key, out var hit) && now - hit.At < CacheFor) return hit.Value;

        var monitored = settings.Current.Repositories.FirstOrDefault(r => r.ToRef().FullName
            .Equals(repositoryFullName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("This repository is no longer monitored.");
        var token = await accounts.GetTokenAsync(monitored.AccountId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The account for this repository has no stored token.");

        var repository = monitored.ToRef();
        var activityTask = gateway.GetPersonActivityAsync(token, repository, login, ct);
        var runsTask = LoadRunsAsync(token, repository, login, now, ct);
        var activity = await activityTask.ConfigureAwait(false) with { PipelineRuns = await runsTask.ConfigureAwait(false) };
        _cache[key] = (time.GetUtcNow(), activity);
        return activity;
    }

    /// <summary>Pipeline counts are a nice-to-have: if Actions cannot be read, the rest of the dashboard still loads.</summary>
    private async Task<PipelineRunCounts> LoadRunsAsync(
        string token, RepositoryRef repository, string login, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            var utc = now.UtcDateTime;
            var monthStart = new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero);
            var weekStart = new DateTimeOffset(utc.Date, TimeSpan.Zero)
                .AddDays(-(((int)utc.DayOfWeek + 6) % 7)); // Monday 00:00 UTC
            return await gateway.GetPipelineRunCountsAsync(token, repository, login, weekStart, monthStart, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return PipelineRunCounts.Unknown;
        }
    }
}
