using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using GitDash.Application.Abstractions;
using GitDash.Application.Updates;
using GitDash.Domain.Updates;
using GitDash.Infrastructure.GitHub;

namespace GitDash.Infrastructure.Updates;

/// <summary>Reads the newest release of the app's repository from the GitHub REST API (read-only).</summary>
public sealed class GitHubReleaseSource(HttpClient http, string owner, string repository) : IReleaseSource
{
    public async Task<ReleaseInfo?> GetLatestAsync(string? token, CancellationToken ct = default)
    {
        var url = $"https://{GitHubHttpClientFactory.ApiHost}/repos/{owner}/{repository}/releases/latest";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        // Without a sign-in GitHub allows only 60 requests an hour per network address, which a shared office
        // network can use up. That must be reported, not mistaken for "no newer version".
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests && string.IsNullOrWhiteSpace(token))
            throw new ReleaseAccessException(
                "GitHub is limiting requests that are not signed in (often a shared network). Sign in in Settings, or try again later.");
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new ReleaseAccessException("GitHub refused the update check for your sign-in. Sign in again, or try again later.");
        // 404: no release has been published yet.
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        using var json = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
        return Parse(json.RootElement);
    }

    public static ReleaseInfo? Parse(JsonElement release)
    {
        if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
        if (release.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) return null;
        var tag = Text(release, "tag_name");
        if (!AppVersion.TryParse(tag, out var version)) return null;

        var assets = new List<ReleaseAsset>();
        if (release.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in list.EnumerateArray())
            {
                var name = Text(a, "name");
                var download = Text(a, "browser_download_url");
                var api = Text(a, "url");
                if (name.Length == 0 || !IsGitHubUrl(download) || !IsGitHubUrl(api)) continue;
                var digest = Text(a, "digest");
                assets.Add(new ReleaseAsset(
                    name, download, api, a.TryGetProperty("size", out var size) && size.TryGetInt64(out var n) ? n : 0,
                    digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest["sha256:".Length..] : null));
            }
        }

        return new ReleaseInfo(version, tag, Text(release, "body"), Text(release, "html_url"), assets);
    }

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? string.Empty : string.Empty;

    /// <summary>Only https links to GitHub itself are ever followed.</summary>
    internal static bool IsGitHubUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host.Equals(GitHubHttpClientFactory.WebHost, StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals(GitHubHttpClientFactory.ApiHost, StringComparison.OrdinalIgnoreCase));
}
