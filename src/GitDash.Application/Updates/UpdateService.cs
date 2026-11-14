using System.Security.Cryptography;
using GitDash.Application.Abstractions;
using GitDash.Application.Accounts;
using GitDash.Application.Settings;
using GitDash.Domain.Updates;

namespace GitDash.Application.Updates;

public enum UpdateStage
{
    Idle,
    Checking,
    UpToDate,
    /// <summary>A newer version exists but is not being installed (auto-update off, or this install cannot update itself).</summary>
    Available,
    Downloading,
    /// <summary>Downloaded; the app restarts when the countdown ends.</summary>
    Ready,
    Installing,
    Failed,
}

/// <summary>
/// Looks for a newer GitHub release now and then. With auto-update on it downloads the installer in the background,
/// tells the user, counts down a few seconds and hands over to the installer, which restarts the app.
/// </summary>
public sealed class UpdateService(
    SettingsService settings,
    AccountService accounts,
    IReleaseSource source,
    IUpdateInstaller installer,
    AppVersion current,
    TimeProvider time,
    Func<ToastRequest, Task> notify,
    Action exitApp)
{
    public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);
    public static readonly TimeSpan FirstCheckAfter = TimeSpan.FromSeconds(20);

    private readonly object _gate = new();
    private CancellationTokenSource? _countdown;
    private string? _file;
    private AppVersion? _notifiedAvailable, _notifiedUpdating;

    public AppVersion Current { get; } = current;
    public UpdateStage Stage { get; private set; }
    public ReleaseInfo? Release { get; private set; }
    public double Progress { get; private set; }
    public string Error { get; private set; } = string.Empty;
    public DateTimeOffset? LastChecked { get; private set; }

    /// <summary>Seconds until the restart while <see cref="Stage"/> is Ready.</summary>
    public int SecondsToRestart { get; private set; }

    /// <summary>Seconds shown before the app restarts to finish an update.</summary>
    public int RestartDelaySeconds { get; init; } = 10;

    public bool CanInstall => installer.CanInstall;
    public string? CannotInstallReason => installer.CannotInstallReason;

    public event Action? Changed;

    /// <summary>Background loop: a first look shortly after start-up, then every few hours, while auto-update is on.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(FirstCheckAfter, time, ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                if (settings.Current.AutoUpdate) await CheckAsync(manual: false, ct).ConfigureAwait(false);
                await Task.Delay(CheckEvery, time, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>Asks GitHub for the newest release. A manual check also reports "up to date" and errors.</summary>
    public async Task CheckAsync(bool manual, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (Stage is UpdateStage.Checking or UpdateStage.Downloading or UpdateStage.Ready or UpdateStage.Installing) return;
            Stage = UpdateStage.Checking;
            Error = string.Empty;
        }

        Changed?.Invoke();
        try
        {
            var token = await FirstTokenAsync(ct).ConfigureAwait(false);
            var release = await source.GetLatestAsync(token, ct).ConfigureAwait(false);
            LastChecked = time.GetUtcNow();
            if (release is null || !release.Version.IsNewerThan(Current))
            {
                Release = null;
                SetStage(UpdateStage.UpToDate);
                return;
            }

            Release = release;
            var asset = installer.CanInstall ? installer.Pick(release) : null;
            if (asset is null)
            {
                SetStage(UpdateStage.Available);
                await TellAvailableAsync(release, installer.CanInstall
                    ? "There is no installer for this computer on the release page."
                    : installer.CannotInstallReason ?? "Open the release page to update.").ConfigureAwait(false);
                return;
            }

            if (!settings.Current.AutoUpdate && !manual)
            {
                SetStage(UpdateStage.Available);
                await TellAvailableAsync(release, "Open GitDash and press Update now to install it.").ConfigureAwait(false);
                return;
            }

            await DownloadAsync(release, asset, token, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            SetStage(UpdateStage.Idle);
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
    }

    /// <summary>The user chose to install the version that is available (auto-update is off or was postponed).</summary>
    public async Task UpdateNowAsync(CancellationToken ct = default)
    {
        if (Stage == UpdateStage.Ready)
        {
            InstallNow();
            return;
        }

        if (Stage != UpdateStage.Available || Release is not { } release) return;
        if (!installer.CanInstall || installer.Pick(release) is not { } asset) return;
        try
        {
            await DownloadAsync(release, asset, await FirstTokenAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fail(ex.Message);
        }
    }

    /// <summary>Skips the countdown and restarts into the new version now.</summary>
    public void InstallNow()
    {
        if (Stage != UpdateStage.Ready || _file is null) return;
        _countdown?.Cancel();
        Install();
    }

    /// <summary>Cancels the countdown; the downloaded update stays available to install later.</summary>
    public void Postpone()
    {
        if (Stage != UpdateStage.Ready) return;
        _countdown?.Cancel();
        SecondsToRestart = 0;
        SetStage(UpdateStage.Available);
    }

    private async Task DownloadAsync(ReleaseInfo release, ReleaseAsset asset, string? token, CancellationToken ct)
    {
        Progress = 0;
        SetStage(UpdateStage.Downloading);
        if (_notifiedUpdating != release.Version)
        {
            _notifiedUpdating = release.Version;
            await SafeNotifyAsync(new ToastRequest(
                $"Updating GitDash to {release.Version}",
                $"Version {release.Version} was found on GitHub. It downloads in the background; GitDash restarts when it is ready.",
                ToastTone.Merged)).ConfigureAwait(false);
        }

        var progress = new Progress<double>(p =>
        {
            Progress = p;
            Changed?.Invoke();
        });
        var file = await installer.DownloadAsync(asset, token, progress, ct).ConfigureAwait(false);
        Verify(file, asset);
        _file = file;
        Progress = 1;
        await BeginCountdownAsync(release).ConfigureAwait(false);
    }

    private static void Verify(string file, ReleaseAsset asset)
    {
        if (string.IsNullOrEmpty(asset.Sha256)) return;
        using var stream = File.OpenRead(file);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(file);
            throw new InvalidOperationException("The downloaded update did not match its published checksum and was discarded.");
        }
    }

    private async Task BeginCountdownAsync(ReleaseInfo release)
    {
        var cts = _countdown = new CancellationTokenSource();
        SecondsToRestart = Math.Max(0, RestartDelaySeconds);
        SetStage(UpdateStage.Ready);
        await SafeNotifyAsync(new ToastRequest(
            $"GitDash {release.Version} is ready",
            $"Restarting in {SecondsToRestart} seconds to finish the update. Open GitDash to restart now or postpone.",
            ToastTone.Merged)).ConfigureAwait(false);

        try
        {
            while (SecondsToRestart > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), time, cts.Token).ConfigureAwait(false);
                SecondsToRestart--;
                Changed?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
            return; // postponed, or the user pressed "Restart now"
        }

        Install();
    }

    private void Install()
    {
        if (_file is null) return;
        SetStage(UpdateStage.Installing);
        try
        {
            installer.StartInstall(_file);
            exitApp();
        }
        catch (Exception ex)
        {
            Fail($"The update could not be started: {ex.Message}");
        }
    }

    private async Task TellAvailableAsync(ReleaseInfo release, string how)
    {
        if (_notifiedAvailable == release.Version) return;
        _notifiedAvailable = release.Version;
        await SafeNotifyAsync(new ToastRequest(
            $"GitDash {release.Version} is available", how, ToastTone.Merged, release.PageUrl)).ConfigureAwait(false);
    }

    private async Task SafeNotifyAsync(ToastRequest request)
    {
        try
        {
            await notify(request).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A notification that cannot be shown must never stop the update.
        }
    }

    private async Task<string?> FirstTokenAsync(CancellationToken ct)
    {
        foreach (var account in settings.Current.Accounts)
        {
            if (await accounts.GetTokenAsync(account.Id, ct).ConfigureAwait(false) is { Length: > 0 } token) return token;
        }

        return null;
    }

    private void Fail(string message)
    {
        Error = message;
        SetStage(UpdateStage.Failed);
    }

    private void SetStage(UpdateStage stage)
    {
        Stage = stage;
        Changed?.Invoke();
    }
}
