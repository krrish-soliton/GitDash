using System.Text.Json;
using GitDash.Application.Abstractions;

namespace GitDash.Infrastructure.GitHub;

/// <summary>
/// GitHub OAuth device flow. Only the public client ID is needed, so the app ships no secret.
/// Scopes: "repo" to read pull requests in private repositories, "read:org" to list organization repositories.
/// </summary>
public sealed class DeviceFlowAuthenticator(HttpClient http, TimeProvider time) : IDeviceFlowAuthenticator
{
    private const string Scopes = "repo read:org";
    private static readonly Uri CodeEndpoint = new($"https://{GitHubHttpClientFactory.WebHost}/login/device/code");
    private static readonly Uri TokenEndpoint = new($"https://{GitHubHttpClientFactory.WebHost}/login/oauth/access_token");

    public async Task<DeviceCodePrompt> RequestCodeAsync(string clientId, CancellationToken ct)
    {
        using var doc = await PostAsync(CodeEndpoint, new() { ["client_id"] = clientId, ["scope"] = Scopes }, ct)
            .ConfigureAwait(false);
        var root = doc.RootElement;
        var deviceCode = root.Str("device_code");
        var verificationUri = root.Str("verification_uri");
        if (deviceCode.Length == 0 ||
            !Uri.TryCreate(verificationUri, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Host != GitHubHttpClientFactory.WebHost)
        {
            var reason = root.Str("error_description", "Check the OAuth App client ID and that device flow is enabled for it.");
            throw new GitHubAuthorizationException($"GitHub did not start the sign-in. {reason}");
        }

        return new DeviceCodePrompt(
            deviceCode,
            root.Str("user_code"),
            verificationUri,
            TimeSpan.FromSeconds(Math.Max(5, root.Int("interval"))),
            TimeSpan.FromSeconds(Math.Clamp(root.Int("expires_in"), 60, 1800)));
    }

    public async Task<string> WaitForTokenAsync(string clientId, DeviceCodePrompt prompt, CancellationToken ct)
    {
        var interval = prompt.PollInterval;
        var deadline = time.GetUtcNow() + prompt.ExpiresIn;

        while (time.GetUtcNow() < deadline)
        {
            await Task.Delay(interval, time, ct).ConfigureAwait(false);

            using var doc = await PostAsync(TokenEndpoint, new()
            {
                ["client_id"] = clientId,
                ["device_code"] = prompt.DeviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            }, ct).ConfigureAwait(false);

            var root = doc.RootElement;
            var token = root.Str("access_token");
            if (token.Length > 0) return token;

            switch (root.Str("error"))
            {
                case "authorization_pending":
                    break;
                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    break;
                case "access_denied":
                    throw new GitHubAuthorizationException("Sign-in was cancelled in the browser.");
                case "expired_token":
                    throw new GitHubAuthorizationException("The sign-in code expired. Start again.");
                default:
                    throw new GitHubAuthorizationException(
                        root.Str("error_description", "GitHub could not complete the sign-in."));
            }
        }

        throw new GitHubAuthorizationException("The sign-in code expired. Start again.");
    }

    private async Task<JsonDocument> PostAsync(Uri uri, Dictionary<string, string> form, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new FormUrlEncodedContent(form) };
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new GitHubApiException($"GitHub sign-in returned {(int)response.StatusCode}.");
        }

        return await JsonDocument
            .ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), default, ct)
            .ConfigureAwait(false);
    }
}
