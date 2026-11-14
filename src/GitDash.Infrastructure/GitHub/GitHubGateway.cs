using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GitDash.Application.Abstractions;
using System.Text.RegularExpressions;
using GitDash.Domain.Actions;
using GitDash.Domain.Insights;
using GitDash.Domain.PullRequests;

namespace GitDash.Infrastructure.GitHub;

/// <summary>
/// GitHub access over GraphQL (pull requests) and REST (repository listing).
/// Error messages are built from status codes and GitHub's own text — never from request headers,
/// so a token cannot leak into the UI or a log.
/// </summary>
public sealed partial class GitHubGateway(HttpClient http) : IGitHubGateway
{
    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$")]
    private static partial Regex LoginPattern();

    private const int MaxRepositoryPages = 20;
    private static readonly Uri GraphQlEndpoint = new($"https://{GitHubHttpClientFactory.ApiHost}/graphql");

    public async Task<GitHubUser> GetViewerAsync(string token, CancellationToken ct)
    {
        using var doc = await QueryAsync(token, GitHubQueries.Viewer, null, ct).ConfigureAwait(false);
        var viewer = doc.RootElement.Prop("data").Prop("viewer");
        var login = viewer.Str("login");
        if (login.Length == 0) throw new GitHubApiException("GitHub did not return the signed-in user.");
        return new GitHubUser(login, viewer.StrOrNull("name"), viewer.StrOrNull("avatarUrl"));
    }

    public async Task<GitHubProfile> GetProfileAsync(string token, CancellationToken ct)
    {
        using var doc = await QueryAsync(token, GitHubQueries.Profile, null, ct).ConfigureAwait(false);
        var data = doc.RootElement.Prop("data") ?? throw new GitHubApiException("Empty response from GitHub.");
        var profile = PullRequestMapper.MapProfile(data);
        if (profile.Login.Length == 0) throw new GitHubApiException("GitHub did not return the signed-in user.");
        return profile;
    }

    public async Task<IReadOnlyList<RepositoryInfo>> ListRepositoriesAsync(string token, CancellationToken ct)
    {
        var result = new List<RepositoryInfo>();
        for (var page = 1; page <= MaxRepositoryPages; page++)
        {
            var uri = new Uri(
                $"https://{GitHubHttpClientFactory.ApiHost}/user/repos?per_page=100&page={page}" +
                "&sort=full_name&affiliation=owner,collaborator,organization_member");
            using var request = Authorized(HttpMethod.Get, uri, token);
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, ct).ConfigureAwait(false);

            using var doc = await JsonDocument
                .ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), default, ct)
                .ConfigureAwait(false);
            var count = 0;
            foreach (var repo in doc.RootElement.EnumerateArray())
            {
                count++;
                if (repo.Bool("archived") || repo.Bool("disabled")) continue;
                var owner = repo.Prop("owner");
                var name = repo.Str("name");
                var ownerLogin = owner.Str("login");
                if (name.Length == 0 || ownerLogin.Length == 0) continue;
                result.Add(new RepositoryInfo(
                    new RepositoryRef(ownerLogin, name),
                    repo.Bool("private"),
                    owner.Str("type") == "Organization",
                    ((JsonElement?)repo).StrOrNull("description")));
            }

            if (count < 100) break;
        }

        return result;
    }

    public async Task<RepositorySnapshot> GetSnapshotAsync(
        string token, Guid accountId, string viewerLogin, RepositoryRef repository, CancellationToken ct)
    {
        using var doc = await QueryAsync(
            token, GitHubQueries.Snapshot, new { owner = repository.Owner, name = repository.Name }, ct)
            .ConfigureAwait(false);
        var data = doc.RootElement.Prop("data") ?? throw new GitHubApiException("Empty response from GitHub.");
        if (data.Prop("repository") is null)
        {
            throw new GitHubApiException("Repository not found, or this account can no longer see it.");
        }

        return PullRequestMapper.MapSnapshot(data, accountId, viewerLogin, repository);
    }

    public async Task<PersonActivity> GetPersonActivityAsync(
        string token, RepositoryRef repository, string login, CancellationToken ct)
    {
        // Bots and odd logins cannot be searched safely; show an empty dashboard instead of risking a malformed query.
        if (!LoginPattern().IsMatch(login))
            return new PersonActivity { Login = login, Repository = repository.FullName };

        var scope = $"repo:{repository.Owner}/{repository.Name} is:pr";
        using var doc = await QueryAsync(token, GitHubQueries.PersonActivity, new
        {
            owner = repository.Owner,
            name = repository.Name,
            raised = $"{scope} author:{login}",
            open = $"{scope} author:{login} is:open",
            merged = $"{scope} author:{login} is:merged",
            closed = $"{scope} author:{login} is:closed -is:merged",
            draft = $"{scope} author:{login} is:open draft:true",
            commented = $"{scope} commenter:{login} sort:updated-desc",
            reviewed = $"{scope} reviewed-by:{login}",
            recent = $"{scope} author:{login} sort:updated-desc",
        }, ct).ConfigureAwait(false);
        var data = doc.RootElement.Prop("data") ?? throw new GitHubApiException("Empty response from GitHub.");
        return PullRequestMapper.MapPersonActivity(data, repository, login);
    }

    public async Task<PipelineRunCounts> GetPipelineRunCountsAsync(
        string token, RepositoryRef repository, string login, DateTimeOffset weekStart, DateTimeOffset monthStart,
        CancellationToken ct)
    {
        if (!LoginPattern().IsMatch(login)) return PipelineRunCounts.Unknown;

        // Each call asks for one run only: the answer we want is the "total_count" field.
        var week = RunCountAsync(token, repository, login, weekStart, ct);
        var month = RunCountAsync(token, repository, login, monthStart, ct);
        var total = RunCountAsync(token, repository, login, null, ct);
        return new PipelineRunCounts(await week.ConfigureAwait(false), await month.ConfigureAwait(false),
            await total.ConfigureAwait(false));
    }

    private async Task<int?> RunCountAsync(
        string token, RepositoryRef repository, string login, DateTimeOffset? since, CancellationToken ct)
    {
        var created = since is { } s ? "&created=" + Uri.EscapeDataString(">=" + s.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")) : string.Empty;
        var uri = new Uri(
            $"https://{GitHubHttpClientFactory.ApiHost}/repos/{Uri.EscapeDataString(repository.Owner)}/" +
            $"{Uri.EscapeDataString(repository.Name)}/actions/runs?actor={Uri.EscapeDataString(login)}&per_page=1{created}");
        using var request = Authorized(HttpMethod.Get, uri, token);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        // No Actions access (or Actions turned off) is normal; the card shows a dash instead of failing.
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Unauthorized) return null;
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        using var doc = await JsonDocument
            .ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), default, ct)
            .ConfigureAwait(false);
        return doc.RootElement.Int("total_count");
    }

    public async Task<CommitCounts> GetCommitCountsAsync(
        string token, RepositoryRef repository, string login, DateTimeOffset weekStart, DateTimeOffset monthStart,
        CancellationToken ct)
    {
        if (!LoginPattern().IsMatch(login)) return CommitCounts.Unknown;
        var week = CommitCountAsync(token, repository, login, weekStart, ct);
        var month = CommitCountAsync(token, repository, login, monthStart, ct);
        var total = CommitCountAsync(token, repository, login, null, ct);
        return new CommitCounts(await week.ConfigureAwait(false), await month.ConfigureAwait(false),
            await total.ConfigureAwait(false));
    }

    /// <summary>Asks for one commit per page: the number of the last page (from the Link header) is then the count.</summary>
    private async Task<int?> CommitCountAsync(
        string token, RepositoryRef repository, string login, DateTimeOffset? since, CancellationToken ct)
    {
        var from = since is { } s ? "&since=" + Uri.EscapeDataString(s.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")) : string.Empty;
        var uri = new Uri(
            $"https://{GitHubHttpClientFactory.ApiHost}/repos/{Uri.EscapeDataString(repository.Owner)}/" +
            $"{Uri.EscapeDataString(repository.Name)}/commits?author={Uri.EscapeDataString(login)}&per_page=1{from}");
        using var request = Authorized(HttpMethod.Get, uri, token);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Conflict)
            return null; // no access, or an empty repository
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var link = response.Headers.TryGetValues("Link", out var values) ? string.Join(",", values) : string.Empty;
        var count = LastPageOf(link);
        if (count is not null) return count;
        using var doc = await JsonDocument
            .ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), default, ct)
            .ConfigureAwait(false);
        return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0;
    }

    /// <summary>The page number of rel="last" in a Link header, or null when there is only one page.</summary>
    public static int? LastPageOf(string link)
    {
        foreach (var part in link.Split(','))
        {
            if (!part.Contains("rel=\"last\"", StringComparison.Ordinal)) continue;
            var at = part.IndexOf("page=", StringComparison.Ordinal);
            while (at >= 0)
            {
                var start = at + 5;
                var end = start;
                while (end < part.Length && char.IsDigit(part[end])) end++;
                var preceded = at > 0 && (part[at - 1] == '?' || part[at - 1] == '&');
                if (preceded && end > start && int.TryParse(part.AsSpan(start, end - start), out var page)) return page;
                at = part.IndexOf("page=", at + 5, StringComparison.Ordinal);
            }
        }

        return null;
    }

    public async Task<PullRequestDetails> GetDetailsAsync(
        string token, RepositoryRef repository, int number, CancellationToken ct)
    {
        using var doc = await QueryAsync(
            token, GitHubQueries.Details, new { owner = repository.Owner, name = repository.Name, number }, ct)
            .ConfigureAwait(false);
        var data = doc.RootElement.Prop("data") ?? throw new GitHubApiException("Empty response from GitHub.");
        if (data.Prop("repository").Prop("pullRequest") is null)
        {
            throw new GitHubApiException("Pull request not found, or this account can no longer see it.");
        }

        return PullRequestMapper.MapDetails(data);
    }

    private const int MaxJobLookups = 8;

    public async Task<IReadOnlyList<WorkflowRun>> GetWorkflowRunsAsync(
        string token, RepositoryRef repository, CancellationToken ct)
    {
        var baseUri = $"https://{GitHubHttpClientFactory.ApiHost}/repos/{Uri.EscapeDataString(repository.Owner)}/" +
                      $"{Uri.EscapeDataString(repository.Name)}/actions/runs";
        IReadOnlyList<WorkflowRun> runs;
        using (var request = Authorized(HttpMethod.Get, new Uri(baseUri + "?per_page=100"), token))
        using (var response = await http.SendAsync(request, ct).ConfigureAwait(false))
        {
            // No Actions access (or Actions turned off) is normal: there is simply nothing to show.
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Unauthorized) return [];
            await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
            using var doc = await JsonDocument
                .ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), default, ct)
                .ConfigureAwait(false);
            runs = WorkflowRunMapper.MapRuns(doc.RootElement, repository);
        }

        // Steps are only looked up for runs that are still going: that is what the progress bar needs.
        var result = new List<WorkflowRun>(runs.Count);
        var lookups = 0;
        foreach (var run in runs)
        {
            if (run.Status != RunStatus.InProgress || lookups >= MaxJobLookups)
            {
                result.Add(run);
                continue;
            }

            lookups++;
            using var request = Authorized(HttpMethod.Get, new Uri($"{baseUri}/{run.Id}/jobs?per_page=100"), token);
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                result.Add(run);
                continue;
            }

            using var jobs = await JsonDocument
                .ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), default, ct)
                .ConfigureAwait(false);
            result.Add(WorkflowRunMapper.WithSteps(run, jobs.RootElement));
        }

        return result;
    }

    public async Task ApproveAsync(string token, RepositoryRef repository, int number, CancellationToken ct)
    {
        var uri = new Uri(
            $"https://{GitHubHttpClientFactory.ApiHost}/repos/{Uri.EscapeDataString(repository.Owner)}/" +
            $"{Uri.EscapeDataString(repository.Name)}/pulls/{number}/reviews");
        using var request = Authorized(HttpMethod.Post, uri, token);
        request.Content = JsonContent.Create(new { @event = "APPROVE" });
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    public async Task SwapAssigneeAsync(
        string token, RepositoryRef repository, int number, string remove, string add, CancellationToken ct)
    {
        var uri = new Uri(
            $"https://{GitHubHttpClientFactory.ApiHost}/repos/{Uri.EscapeDataString(repository.Owner)}/" +
            $"{Uri.EscapeDataString(repository.Name)}/issues/{number}/assignees");

        if (add.Length > 0)
        {
            using var addRequest = Authorized(HttpMethod.Post, uri, token);
            addRequest.Content = JsonContent.Create(new { assignees = new[] { add } });
            using var response = await http.SendAsync(addRequest, ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, ct).ConfigureAwait(false);

            // GitHub answers 201 even when it silently skips someone who cannot be assigned: check before removing anyone.
            using var doc = await JsonDocument
                .ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), default, ct)
                .ConfigureAwait(false);
            if (!doc.RootElement.Items("assignees").Any(a => a.Str("login").Equals(add, StringComparison.OrdinalIgnoreCase)))
            {
                throw new GitHubApiException(remove.Length > 0
                    ? $"{add} cannot be assigned in this repository, so you were not removed."
                    : $"{add} cannot be assigned in this repository.");
            }
        }

        if (remove.Length == 0) return;
        using var removeRequest = Authorized(HttpMethod.Delete, uri, token);
        removeRequest.Content = JsonContent.Create(new { assignees = new[] { remove } });
        using var removed = await http.SendAsync(removeRequest, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(removed, ct).ConfigureAwait(false);
    }

    public async Task<RepositoryProbe> ProbeAsync(
        string token, RepositoryRef repository, string? etag, CancellationToken ct)
    {
        // The most recently updated pull request in any state. Its ETag changes whenever any pull request
        // in the repository is opened, commented on, reviewed, assigned, pushed to, merged or closed.
        var uri = new Uri(
            $"https://{GitHubHttpClientFactory.ApiHost}/repos/{Uri.EscapeDataString(repository.Owner)}/" +
            $"{Uri.EscapeDataString(repository.Name)}/pulls?state=all&sort=updated&direction=desc&per_page=1");
        using var request = Authorized(HttpMethod.Get, uri, token);
        if (!string.IsNullOrEmpty(etag)) request.Headers.TryAddWithoutValidation("If-None-Match", etag);

        using var response = await http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified) return new RepositoryProbe(false, etag);

        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var fresh = response.Headers.ETag?.ToString();
        return new RepositoryProbe(fresh is null || fresh != etag, fresh);
    }

    private async Task<JsonDocument> QueryAsync(string token, string query, object? variables, CancellationToken ct)
    {
        using var request = Authorized(HttpMethod.Post, GraphQlEndpoint, token);
        request.Content = JsonContent.Create(new { query, variables });
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);

        var doc = await JsonDocument
            .ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), default, ct)
            .ConfigureAwait(false);
        try
        {
            // GraphQL reports problems with HTTP 200 plus an "errors" array. Partial data is still useful,
            // so only fail when there is no data at all.
            if (doc.RootElement.Prop("errors") is { ValueKind: JsonValueKind.Array } errors &&
                errors.GetArrayLength() > 0 && !HasData(doc.RootElement))
            {
                var first = errors[0];
                var type = first.Str("type");
                var message = first.Str("message", "GitHub rejected the query.");
                throw type is "FORBIDDEN" or "INSUFFICIENT_SCOPES"
                    ? new GitHubAuthorizationException(message)
                    : new GitHubApiException(message);
            }

            return doc;
        }
        catch
        {
            doc.Dispose();
            throw;
        }
    }

    private static bool HasData(JsonElement root) =>
        root.Prop("data") is { ValueKind: JsonValueKind.Object } data &&
        data.EnumerateObject().Any(p => p.Value.ValueKind != JsonValueKind.Null);

    private static HttpRequestMessage Authorized(HttpMethod method, Uri uri, string token)
    {
        if (token.Any(char.IsControl) || token.Any(char.IsWhiteSpace))
        {
            throw new GitHubAuthorizationException("The stored token is malformed. Sign in again.");
        }

        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        var detail = await ReadMessageAsync(response, ct).ConfigureAwait(false);
        var limited = response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) &&
                      remaining.FirstOrDefault() == "0";

        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new GitHubAuthorizationException(
                "GitHub rejected the token (expired or revoked). Sign in again."),
            HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
                when limited || response.Headers.Contains("retry-after") => new GitHubRateLimitException(
                    "GitHub asked the app to slow down. Live checks pause briefly and resume on their own."),
            HttpStatusCode.Forbidden => new GitHubAuthorizationException(
                $"GitHub denied access{detail}. The token may lack permissions, or the organization requires SSO/approval."),
            _ => new GitHubApiException($"GitHub returned {(int)response.StatusCode}{detail}."),
        };
    }

    private static async Task<string> ReadMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var doc = await JsonDocument
                .ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), default, ct)
                .ConfigureAwait(false);
            var message = doc.RootElement.Str("message");
            return message.Length == 0 ? string.Empty : $" ({message})";
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return string.Empty;
        }
    }
}
