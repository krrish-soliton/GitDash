using System.Globalization;
using System.Windows.Input;
using GitDash.Application.Abstractions;
using GitDash.Application.Updates;
using GitDash.Presentation.Mvvm;

namespace GitDash.Presentation.ViewModels;

/// <summary>What the sidebar, the update banner and the Updates settings show about the auto-updater.</summary>
public sealed class UpdateViewModel : ObservableObject
{
    private readonly UpdateService? _service;
    private readonly IUrlLauncher _launcher;
    private readonly IUiDispatcher _ui;
    private readonly RelayCommand _check, _primary;

    public UpdateViewModel(UpdateService? service, IUrlLauncher launcher, IUiDispatcher ui)
    {
        _service = service;
        _launcher = launcher;
        _ui = ui;
        _check = new RelayCommand(() => _ = _service?.CheckAsync(manual: true), () => _service is not null && !IsBusy);
        _primary = new RelayCommand(RunPrimary, () => Stage == UpdateStage.Available);
        RestartNowCommand = new RelayCommand(() => _service?.InstallNow());
        LaterCommand = new RelayCommand(() => _service?.Postpone());
        OpenReleaseCommand = new RelayCommand(OpenRelease, () => _service?.Release is not null);
        if (service is not null) service.Changed += () => _ui.Post(Refresh);
    }

    public UpdateStage Stage => _service?.Stage ?? UpdateStage.Idle;
    public bool IsEnabled => _service is not null;

    /// <summary>"v1.2.3": shown at the bottom of the sidebar.</summary>
    public string VersionText => _service is null ? string.Empty : $"v{_service.Current}";
    public bool HasVersion => _service is not null;

    public ICommand CheckCommand => _check;
    public ICommand RestartNowCommand { get; }
    public ICommand LaterCommand { get; }
    public ICommand OpenReleaseCommand { get; }

    /// <summary>The sidebar line: install the available update, or open its release page when it cannot be installed here.</summary>
    public ICommand PrimaryActionCommand => _primary;

    public bool IsBusy => Stage is UpdateStage.Checking or UpdateStage.Downloading or UpdateStage.Ready or UpdateStage.Installing;

    // ----- Banner above the pages while an update is on its way -----
    public bool ShowBanner => Stage is UpdateStage.Downloading or UpdateStage.Ready or UpdateStage.Installing;
    public bool IsReady => Stage == UpdateStage.Ready;
    public bool IsDownloading => Stage == UpdateStage.Downloading;

    private string Target => _service?.Release?.Version.ToString() ?? string.Empty;

    public string BannerTitle => Stage switch
    {
        UpdateStage.Downloading => $"Updating GitDash to {Target}",
        UpdateStage.Ready => $"GitDash {Target} is ready",
        UpdateStage.Installing => "Installing the update",
        _ => string.Empty,
    };

    public string BannerDetail => Stage switch
    {
        UpdateStage.Downloading => $"Downloading in the background… {Percent}%",
        UpdateStage.Ready => $"GitDash restarts in {_service?.SecondsToRestart ?? 0} s to finish the update.",
        UpdateStage.Installing => "GitDash closes now and opens again in a moment.",
        _ => string.Empty,
    };

    public int Percent => (int)Math.Round(Math.Clamp(_service?.Progress ?? 0, 0, 1) * 100);
    public double ProgressValue => Stage == UpdateStage.Ready ? 100 : Percent;
    public bool IsIndeterminate => Stage == UpdateStage.Installing;

    // ----- Line under the version in the sidebar -----
    public string SidebarText => Stage switch
    {
        UpdateStage.Downloading => $"Updating… {Percent}%",
        UpdateStage.Ready => $"Restarting in {_service?.SecondsToRestart ?? 0} s",
        UpdateStage.Installing => "Installing…",
        UpdateStage.Available => $"{Target} available",
        UpdateStage.Failed => "Update failed",
        UpdateStage.Checking => "Checking…",
        UpdateStage.UpToDate => "Up to date",
        _ => string.Empty,
    };

    public bool HasSidebarText => SidebarText.Length > 0;
    public bool SidebarIsAction => Stage == UpdateStage.Available;

    // ----- Settings -----
    public string StatusText => Stage switch
    {
        UpdateStage.Checking => "Looking for a new version…",
        UpdateStage.Downloading => $"Downloading {Target}… {Percent}%",
        UpdateStage.Ready => $"{Target} is downloaded. GitDash restarts in {_service?.SecondsToRestart ?? 0} s.",
        UpdateStage.Installing => "Installing the update…",
        UpdateStage.Failed => _service?.Error ?? string.Empty,
        UpdateStage.Available when _service is { CanInstall: false } s => $"Version {Target} is available. {s.CannotInstallReason}",
        UpdateStage.Available => $"Version {Target} is available.",
        UpdateStage.UpToDate => $"You have the latest version{Checked}.",
        _ => _service is null ? "Updates are switched off in this mode." : "Not checked yet.",
    };

    private string Checked => _service?.LastChecked is { } at
        ? $" (checked {at.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)})"
        : string.Empty;

    public bool HasError => Stage == UpdateStage.Failed;
    public bool CanInstallUpdate => Stage == UpdateStage.Available && _service?.CanInstall == true;

    private void RunPrimary()
    {
        if (_service is null) return;
        if (_service.CanInstall) _ = _service.UpdateNowAsync();
        else OpenRelease();
    }

    private void OpenRelease()
    {
        if (_service?.Release?.PageUrl is { Length: > 0 } url) _launcher.Open(url);
    }

    private void Refresh()
    {
        foreach (var name in new[]
                 {
                     nameof(Stage), nameof(IsBusy), nameof(ShowBanner), nameof(IsReady), nameof(IsDownloading), nameof(BannerTitle),
                     nameof(BannerDetail), nameof(Percent), nameof(ProgressValue), nameof(IsIndeterminate), nameof(SidebarText),
                     nameof(HasSidebarText), nameof(SidebarIsAction), nameof(StatusText), nameof(HasError), nameof(CanInstallUpdate),
                 })
        {
            Raise(name);
        }

        _check.RaiseCanExecuteChanged();
        _primary.RaiseCanExecuteChanged();
    }
}
