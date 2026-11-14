using GitDash.Application.Abstractions;
using GitDash.Application.Accounts;
using GitDash.Application.Settings;
using GitDash.Infrastructure.Demo;
using GitDash.Infrastructure.Security;

namespace GitDash.Tests;

public class GitHubCliSetupTests
{
    [Fact]
    public void One_time_code_is_found_in_the_cli_output()
    {
        Assert.Equal("ABCD-1234", GitHubCliSetup.ParseOneTimeCode("! First copy your one-time code: ABCD-1234"));
        Assert.Equal("A1B2-C3D4", GitHubCliSetup.ParseOneTimeCode("code A1B2-C3D4 now"));
        Assert.True(GitHubCliSetup.ParseOneTimeCode("- Press Enter to open github.com in your browser...") is null);
        Assert.True(GitHubCliSetup.ParseOneTimeCode("abcd-1234") is null);
        Assert.True(GitHubCliSetup.ParseOneTimeCode(null) is null);
    }

    private sealed class Cli(string token) : IGitHubCliTokenSource
    {
        public Task<string> GetTokenAsync(CancellationToken ct = default) => Task.FromResult(token);
    }

    private sealed class Setup(GitHubCliState state) : IGitHubCliSetup
    {
        public bool LoggedIn { get; private set; }

        public Task<GitHubCliState> GetStateAsync(CancellationToken ct = default) => Task.FromResult(state);

        public Task LoginAsync(Action<DeviceCodePrompt> onCode, CancellationToken ct = default)
        {
            onCode(new DeviceCodePrompt(string.Empty, "ABCD-1234", "https://github.com/login/device", TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(15)));
            LoggedIn = true;
            return Task.CompletedTask;
        }
    }

    private static async Task<(AccountService Accounts, SettingsService Settings)> Build(IGitHubCliSetup setup)
    {
        var settings = new SettingsService(new InMemorySettingsStore());
        await settings.LoadAsync();
        var accounts = new AccountService(
            settings, new InMemorySecretStore(), new DemoGitHubGateway(TimeProvider.System, 1), new DemoDeviceFlowAuthenticator(),
            new Cli("gho_cli_token"), setup);
        return (accounts, settings);
    }

    [Fact]
    public async Task Not_signed_in_runs_the_cli_login_and_then_signs_in()
    {
        var setup = new Setup(GitHubCliState.NotSignedIn);
        var (accounts, settings) = await Build(setup);
        string? shown = null;

        var account = await accounts.SignInThroughGitHubCliAsync(p => shown = p.UserCode);

        Assert.True(setup.LoggedIn);
        Assert.Equal("ABCD-1234", shown);
        Assert.Equal(AuthMethod.GitHubCli, account.Method);
        Assert.Equal(1, settings.Current.Accounts.Count);
    }

    [Fact]
    public async Task Already_signed_in_skips_the_login()
    {
        var setup = new Setup(GitHubCliState.SignedIn);
        var (accounts, _) = await Build(setup);

        await accounts.SignInThroughGitHubCliAsync(_ => { });

        Assert.False(setup.LoggedIn);
    }

    [Fact]
    public async Task Missing_cli_is_reported_and_nothing_is_stored()
    {
        var (accounts, settings) = await Build(new Setup(GitHubCliState.NotInstalled));

        await Assert.ThrowsAsync<GitHubCliNotInstalledException>(() => accounts.SignInThroughGitHubCliAsync(_ => { }));
        Assert.Equal(0, settings.Current.Accounts.Count);
    }
}
