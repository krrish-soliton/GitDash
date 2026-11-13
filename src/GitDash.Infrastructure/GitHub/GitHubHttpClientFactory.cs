using System.Net;
using System.Net.Http.Headers;

namespace GitDash.Infrastructure.GitHub;

/// <summary>
/// Builds the one HttpClient the app uses. Hardened so a bearer token can only ever travel
/// over HTTPS to GitHub itself: no redirects, host allow-list, bounded response size.
/// </summary>
public static class GitHubHttpClientFactory
{
    public const string ApiHost = "api.github.com";
    public const string WebHost = "github.com";
    public const string AvatarHost = "avatars.githubusercontent.com";

    public static HttpClient Create()
    {
        var sockets = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 4,
            UseCookies = false,
        };

        var client = new HttpClient(new HostGuardHandler(sockets))
        {
            Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = 16 * 1024 * 1024,
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GitDash", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private sealed class HostGuardHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri;
            var isGitHub = uri is not null && (uri.Host == ApiHost || uri.Host == WebHost);
            // Profile pictures are public: allowed, but only for requests that carry no credentials.
            var isAvatar = uri is not null && uri.Host == AvatarHost && request.Headers.Authorization is null;
            if (uri is null || uri.Scheme != Uri.UriSchemeHttps || !(isGitHub || isAvatar))
            {
                throw new InvalidOperationException("Blocked a request to a host that is not GitHub.");
            }

            return base.SendAsync(request, ct);
        }
    }
}
