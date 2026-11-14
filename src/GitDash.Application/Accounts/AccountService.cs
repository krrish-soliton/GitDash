using GitDash.Application.Abstractions;
using GitDash.Application.Settings;

namespace GitDash.Application.Accounts;

/// <summary>Adds and removes GitHub accounts. Tokens go to the secret store; settings only keep the login.</summary>
public sealed class AccountService(
    SettingsService settings,
    ISecretStore secrets,
    IGitHubGateway gitHub,
    IDeviceFlowAuthenticator deviceFlow,
    IGitHubCliTokenSource? gitHubCli = null,
    IGitHubCliSetup? gitHubCliSetup = null)
{
    private static string SecretKey(Guid accountId) => $"account:{accountId:N}";

    public Task<string?> GetTokenAsync(Guid accountId, CancellationToken ct = default) =>
        secrets.ReadAsync(SecretKey(accountId), ct);

    /// <summary>Validates the token against GitHub before anything is stored.</summary>
    public async Task<AccountSettings> AddWithTokenAsync(string token, AuthMethod method, CancellationToken ct = default)
    {
        token = token.Trim();
        if (token.Length == 0) throw new ArgumentException("The token is empty.", nameof(token));

        var viewer = await gitHub.GetViewerAsync(token, ct).ConfigureAwait(false);
        var existing = settings.Current.Accounts.FirstOrDefault(a =>
            string.Equals(a.Login, viewer.Login, StringComparison.OrdinalIgnoreCase));
        var account = new AccountSettings(
            existing?.Id ?? Guid.NewGuid(), viewer.Login, viewer.Name, method, viewer.AvatarUrl);

        await secrets.SaveAsync(SecretKey(account.Id), token, ct).ConfigureAwait(false);
        await settings.UpdateAsync(s => s with
        {
            Accounts = [.. s.Accounts.Where(a => a.Id != account.Id), account],
        }, ct).ConfigureAwait(false);
        return account;
    }

    /// <summary>
    /// Signs in with the token of the user's GitHub CLI login. The token is copied into the secret store, so
    /// running this again after <c>gh auth login</c> or <c>gh auth refresh</c> picks up the new token.
    /// </summary>
    public async Task<AccountSettings> AddFromGitHubCliAsync(CancellationToken ct = default)
    {
        if (gitHubCli is null) throw new InvalidOperationException("GitHub CLI sign-in is not available.");
        var token = await gitHubCli.GetTokenAsync(ct).ConfigureAwait(false);
        return await AddWithTokenAsync(token, AuthMethod.GitHubCli, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The one-click path: when the GitHub CLI is installed but not logged in, runs its browser login first (the code to
    /// type is passed to <paramref name="onCode"/>), then copies the token like <see cref="AddFromGitHubCliAsync"/>.
    /// </summary>
    public async Task<AccountSettings> SignInThroughGitHubCliAsync(Action<DeviceCodePrompt> onCode, CancellationToken ct = default)
    {
        if (gitHubCliSetup is not null)
        {
            switch (await gitHubCliSetup.GetStateAsync(ct).ConfigureAwait(false))
            {
                case GitHubCliState.NotInstalled:
                    throw new GitHubCliNotInstalledException();
                case GitHubCliState.NotSignedIn:
                    await gitHubCliSetup.LoginAsync(onCode, ct).ConfigureAwait(false);
                    break;
            }
        }

        return await AddFromGitHubCliAsync(ct).ConfigureAwait(false);
    }

    public Task<DeviceCodePrompt> BeginDeviceFlowAsync(CancellationToken ct = default)
    {
        var clientId = settings.Current.OAuthClientId.Trim();
        if (clientId.Length == 0)
        {
            throw new InvalidOperationException("Enter your GitHub OAuth App client ID first.");
        }

        return deviceFlow.RequestCodeAsync(clientId, ct);
    }

    public async Task<AccountSettings> CompleteDeviceFlowAsync(DeviceCodePrompt prompt, CancellationToken ct = default)
    {
        var token = await deviceFlow
            .WaitForTokenAsync(settings.Current.OAuthClientId.Trim(), prompt, ct).ConfigureAwait(false);
        return await AddWithTokenAsync(token, AuthMethod.DeviceFlow, ct).ConfigureAwait(false);
    }

    /// <summary>Removes the account, its token and every repository monitored through it.</summary>
    public async Task RemoveAsync(Guid accountId, CancellationToken ct = default)
    {
        await secrets.DeleteAsync(SecretKey(accountId), ct).ConfigureAwait(false);
        await settings.UpdateAsync(s => s with
        {
            Accounts = [.. s.Accounts.Where(a => a.Id != accountId)],
            Repositories = [.. s.Repositories.Where(r => r.AccountId != accountId)],
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Loads the account's public profile and remembers its picture for the account list.</summary>
    public async Task<GitHubProfile> GetProfileAsync(Guid accountId, CancellationToken ct = default)
    {
        var token = await GetTokenAsync(accountId, ct).ConfigureAwait(false)
            ?? throw new GitHubAuthorizationException("No token is stored for this account. Sign in again.");
        var profile = await gitHub.GetProfileAsync(token, ct).ConfigureAwait(false);

        var known = settings.Current.Accounts.FirstOrDefault(a => a.Id == accountId);
        if (known is not null && (known.AvatarUrl != profile.AvatarUrl || known.Name != profile.Name))
        {
            await settings.UpdateAsync(s => s with
            {
                Accounts =
                [
                    .. s.Accounts.Select(a => a.Id == accountId
                        ? a with { AvatarUrl = profile.AvatarUrl, Name = profile.Name ?? a.Name }
                        : a),
                ],
            }, ct).ConfigureAwait(false);
        }

        return profile;
    }

    public async Task<IReadOnlyList<RepositoryInfo>> ListRepositoriesAsync(Guid accountId, CancellationToken ct = default)
    {
        var token = await GetTokenAsync(accountId, ct).ConfigureAwait(false)
            ?? throw new GitHubAuthorizationException("No token is stored for this account. Sign in again.");
        return await gitHub.ListRepositoriesAsync(token, ct).ConfigureAwait(false);
    }
}
