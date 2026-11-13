using System.Collections.Concurrent;
using GitDash.Application.Abstractions;

namespace GitDash.Infrastructure.GitHub;

/// <summary>
/// Downloads profile pictures from GitHub's avatar host. No token is ever sent, anything that is not an
/// https avatar URL is refused, and oversized responses are dropped.
/// </summary>
public sealed class AvatarDownloader(HttpClient http) : IAvatarSource
{
    private const int MaxBytes = 512 * 1024;
    private readonly ConcurrentDictionary<string, Task<byte[]?>> _inFlight = new();

    public static bool IsAvatarUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host == GitHubHttpClientFactory.AvatarHost &&
        uri.UserInfo.Length == 0;

    public Task<byte[]?> GetAsync(string url, CancellationToken ct = default)
    {
        if (!IsAvatarUrl(url)) return Task.FromResult<byte[]?>(null);
        // Many figures can show the same person; share one download between them.
        return _inFlight.GetOrAdd(url, DownloadAsync);
    }

    private async Task<byte[]?> DownloadAsync(string url)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes) return null;

            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            return bytes.Length is > 0 and <= MaxBytes ? bytes : null;
        }
        catch (Exception)
        {
            return null; // A missing face falls back to initials; it is never an error worth surfacing.
        }
        finally
        {
            _inFlight.TryRemove(url, out _);
        }
    }
}
