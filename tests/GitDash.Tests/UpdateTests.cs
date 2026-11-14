using System.Security.Cryptography;
using System.Text.Json;
using GitDash.Application.Abstractions;
using GitDash.Application.Accounts;
using GitDash.Application.Settings;
using GitDash.Application.Updates;
using GitDash.Domain.Updates;
using GitDash.Infrastructure.Demo;
using GitDash.Infrastructure.Security;
using GitDash.Infrastructure.Updates;
using GitDash.Presentation.Mvvm;
using GitDash.Presentation.ViewModels;

namespace GitDash.Tests;

public class UpdateTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.2", true)]
    [InlineData("1.10.0", "1.9.9", true)]
    [InlineData("v2.0.0", "1.99.99", true)]
    [InlineData("1.0.1", "1.0.1", false)]
    [InlineData("1.0.0", "1.0.1", false)]
    [InlineData("1.0.1", "1.0.1-beta.2", true)]   // a release beats its own pre-releases
    [InlineData("1.0.1-beta.10", "1.0.1-beta.2", true)]
    [InlineData("1.0.1-rc.1", "1.0.1-beta.9", true)]
    [InlineData("1.1.0", "1.1.0+abc123", false)]  // build metadata is ignored
    public void Versions_compare_like_semver(string a, string b, bool aIsNewer) =>
        Assert.Equal(aIsNewer, AppVersion.Parse(a).IsNewerThan(AppVersion.Parse(b)));

    [Fact]
    public void Odd_version_text_is_rejected_or_padded()
    {
        Assert.False(AppVersion.TryParse("latest", out _));
        Assert.False(AppVersion.TryParse("", out _));
        Assert.True(AppVersion.TryParse("1.2", out var v));
        Assert.Equal("1.2.0", v.ToString());
        Assert.Equal("1.2.3-rc.1", AppVersion.Parse("v1.2.3-rc.1+sha").ToString());
    }

    [Fact]
    public void Release_json_is_read_and_only_github_links_survive()
    {
        using var json = JsonDocument.Parse("""
            {
              "tag_name": "v1.4.0", "body": "Notes", "html_url": "https://github.com/o/r/releases/tag/v1.4.0",
              "draft": false, "prerelease": false,
              "assets": [
                { "name": "GitDash-Setup-1.4.0.exe", "browser_download_url": "https://github.com/o/r/releases/download/v1.4.0/GitDash-Setup-1.4.0.exe",
                  "url": "https://api.github.com/repos/o/r/releases/assets/1", "size": 1234, "digest": "sha256:ABCDEF" },
                { "name": "evil.exe", "browser_download_url": "https://evil.example/evil.exe", "url": "https://api.github.com/repos/o/r/releases/assets/2", "size": 1 },
                { "name": "plain.exe", "browser_download_url": "http://github.com/x.exe", "url": "https://api.github.com/repos/o/r/releases/assets/3", "size": 1 }
              ]
            }
            """);
        var release = GitHubReleaseSource.Parse(json.RootElement)!;
        Assert.Equal("1.4.0", release.Version.ToString());
        var asset = Assert.Single(release.Assets);
        Assert.Equal("ABCDEF", asset.Sha256);

        using var draft = JsonDocument.Parse("""{ "tag_name": "v9.0.0", "draft": true, "assets": [] }""");
        Assert.Null(GitHubReleaseSource.Parse(draft.RootElement));
        using var pre = JsonDocument.Parse("""{ "tag_name": "v9.0.0", "prerelease": true, "assets": [] }""");
        Assert.Null(GitHubReleaseSource.Parse(pre.RootElement));
    }

    [Fact]
    public void Mac_bundle_is_found_from_the_executable_folder()
    {
        Assert.Equal("/Applications/GitDash.app", UpdateInstallerFactory.BundlePath("/Applications/GitDash.app/Contents/MacOS/"));
        Assert.Null(UpdateInstallerFactory.BundlePath("/Users/me/src/GitDash/bin/Debug/"));
    }

    // ----- the service -----

    private sealed class FakeSource(ReleaseInfo? release) : IReleaseSource
    {
        public Task<ReleaseInfo?> GetLatestAsync(string? token, CancellationToken ct = default) => Task.FromResult(release);
    }

    private sealed class FakeInstaller(bool canInstall, string content = "installer", bool hasAsset = true) : IUpdateInstaller
    {
        public bool CanInstall => canInstall;
        public string? CannotInstallReason => canInstall ? null : "Portable copy.";
        public string? Started { get; private set; }
        public ReleaseAsset? Pick(ReleaseInfo release) => hasAsset ? release.Assets.FirstOrDefault() : null;

        public Task<string> DownloadAsync(ReleaseAsset asset, string? token, IProgress<double> progress, CancellationToken ct = default)
        {
            var file = Path.Combine(Path.GetTempPath(), $"gitdash-test-{Guid.NewGuid():N}.bin");
            File.WriteAllText(file, content);
            progress.Report(1);
            return Task.FromResult(file);
        }

        public void StartInstall(string file) => Started = file;
    }

    private static ReleaseInfo Release(string version, string sha = "") => new(
        AppVersion.Parse(version), "v" + version, "notes", "https://github.com/o/r/releases/tag/v" + version,
        [new ReleaseAsset("GitDash-Setup.exe", "https://github.com/o/r/x", "https://api.github.com/x", 9,
            sha.Length == 0 ? Convert.ToHexString(SHA256.HashData("installer"u8.ToArray())) : sha)]);

    private static async Task<(UpdateService Service, List<ToastRequest> Toasts, Func<int> Exits, SettingsService Settings)> BuildAsync(
        ReleaseInfo? release, IUpdateInstaller installer, bool autoUpdate = true, int delaySeconds = 0, string current = "1.0.1")
    {
        var settings = new SettingsService(new InMemorySettingsStore());
        await settings.LoadAsync();
        await settings.UpdateAsync(s => s with { AutoUpdate = autoUpdate });
        var accounts = new AccountService(settings, new InMemorySecretStore(), new DemoGitHubGateway(TimeProvider.System), new DemoDeviceFlowAuthenticator());
        var toasts = new List<ToastRequest>();
        var exits = 0;
        var service = new UpdateService(settings, accounts, new FakeSource(release), installer, AppVersion.Parse(current),
            TimeProvider.System, t => { toasts.Add(t); return Task.CompletedTask; }, () => exits++)
        {
            RestartDelaySeconds = delaySeconds,
        };
        return (service, toasts, () => exits, settings);
    }

    [Fact]
    public async Task A_newer_release_is_downloaded_announced_and_handed_to_the_installer()
    {
        var installer = new FakeInstaller(true);
        var (service, toasts, exits, _) = await BuildAsync(Release("1.1.0"), installer);

        await service.CheckAsync(manual: false);

        Assert.Equal(UpdateStage.Installing, service.Stage);
        Assert.NotNull(installer.Started);
        Assert.Equal(1, exits());
        Assert.Contains(toasts, t => t.Title.Contains("Updating GitDash to 1.1.0"));
        Assert.Contains(toasts, t => t.Title.Contains("1.1.0 is ready"));
    }

    [Fact]
    public async Task Nothing_happens_when_already_current_or_ahead()
    {
        var installer = new FakeInstaller(true);
        var (service, toasts, exits, _) = await BuildAsync(Release("1.0.1"), installer);
        await service.CheckAsync(manual: false);
        Assert.Equal(UpdateStage.UpToDate, service.Stage);
        Assert.Empty(toasts);
        Assert.Equal(0, exits());

        var (ahead, _, _, _) = await BuildAsync(Release("1.0.0"), installer);
        await ahead.CheckAsync(manual: false);
        Assert.Equal(UpdateStage.UpToDate, ahead.Stage);
    }

    [Fact]
    public async Task A_corrupt_download_is_discarded_and_never_installed()
    {
        var installer = new FakeInstaller(true);
        var (service, _, exits, _) = await BuildAsync(Release("1.1.0", sha: new string('0', 64)), installer);
        await service.CheckAsync(manual: false);

        Assert.Equal(UpdateStage.Failed, service.Stage);
        Assert.Contains("checksum", service.Error);
        Assert.Null(installer.Started);
        Assert.Equal(0, exits());
    }

    [Fact]
    public async Task With_auto_update_off_the_user_is_told_and_installs_on_request()
    {
        var installer = new FakeInstaller(true);
        var (service, toasts, exits, _) = await BuildAsync(Release("1.1.0"), installer, autoUpdate: false);
        await service.CheckAsync(manual: false);

        Assert.Equal(UpdateStage.Available, service.Stage);
        Assert.Null(installer.Started);
        Assert.Single(toasts);

        await service.CheckAsync(manual: false); // looking again does not repeat the announcement
        Assert.Single(toasts);

        await service.UpdateNowAsync();
        Assert.Equal(UpdateStage.Installing, service.Stage);
        Assert.Equal(1, exits());
    }

    [Fact]
    public async Task Portable_or_unsupported_installs_only_announce_the_release()
    {
        var (service, toasts, exits, _) = await BuildAsync(Release("1.1.0"), new FakeInstaller(canInstall: false));
        await service.CheckAsync(manual: false);

        Assert.Equal(UpdateStage.Available, service.Stage);
        Assert.False(service.CanInstall);
        Assert.Equal(0, exits());
        Assert.Equal("https://github.com/o/r/releases/tag/v1.1.0", Assert.Single(toasts).Url);
    }

    [Fact]
    public async Task The_countdown_can_be_postponed_or_skipped()
    {
        var installer = new FakeInstaller(true);
        var (service, _, exits, _) = await BuildAsync(Release("1.1.0"), installer, delaySeconds: 30);
        var check = service.CheckAsync(manual: false);
        await WaitForAsync(() => service.Stage == UpdateStage.Ready);
        Assert.Equal(30, service.SecondsToRestart);

        service.Postpone();
        await check;
        Assert.Equal(UpdateStage.Available, service.Stage);
        Assert.Equal(0, exits());

        var (second, _, exits2, _) = await BuildAsync(Release("1.1.0"), new FakeInstaller(true), delaySeconds: 30);
        var check2 = second.CheckAsync(manual: false);
        await WaitForAsync(() => second.Stage == UpdateStage.Ready);
        second.InstallNow();
        await check2;
        Assert.Equal(1, exits2());
    }

    [Fact]
    public async Task The_view_model_shows_version_banner_and_progress()
    {
        var installer = new FakeInstaller(true);
        var (service, _, _, _) = await BuildAsync(Release("1.1.0"), installer, delaySeconds: 30);
        var vm = new UpdateViewModel(service, new NoUrls(), new Inline());
        Assert.Equal("v1.0.1", vm.VersionText);
        Assert.False(vm.ShowBanner);

        var check = service.CheckAsync(manual: false);
        await WaitForAsync(() => service.Stage == UpdateStage.Ready);
        Assert.True(vm.ShowBanner);
        Assert.True(vm.IsReady);
        Assert.Contains("GitDash 1.1.0 is ready", vm.BannerTitle);
        Assert.Contains("30", vm.BannerDetail);

        vm.LaterCommand.Execute(null);
        await check;
        Assert.False(vm.ShowBanner);
        Assert.True(vm.SidebarIsAction);
        Assert.Equal("1.1.0 available", vm.SidebarText);

        Assert.Equal("", new UpdateViewModel(null, new NoUrls(), new Inline()).VersionText);
    }

    private sealed class Inline : IUiDispatcher
    {
        public void Post(Action action) => action();
    }

    private sealed class NoUrls : IUrlLauncher
    {
        public bool Open(string url) => true;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "Timed out waiting for the update state.");
    }
}
