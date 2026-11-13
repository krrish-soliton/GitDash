using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GitDash.Application.Abstractions;
using System.Text.RegularExpressions;
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
