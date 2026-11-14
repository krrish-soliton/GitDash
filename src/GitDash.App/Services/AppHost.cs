using Avalonia.Threading;
using GitDash.App.Controls;
using GitDash.Application.Abstractions;
using GitDash.Application.Accounts;
using GitDash.Application.Monitoring;
using GitDash.Application.Settings;
using GitDash.Application.Timers;
using GitDash.Infrastructure.Demo;
using GitDash.Infrastructure.GitHub;
using GitDash.Infrastructure.Notifications;
using GitDash.Infrastructure.Security;
using GitDash.Infrastructure.Storage;
using GitDash.Persistence.Sqlite;
using GitDash.Presentation.Mvvm;
using GitDash.Presentation.ViewModels;

namespace GitDash.App.Services;

public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}

/// <summary>
/// Composition root: the only place that knows which concrete class implements which port.
/// Hand-wired on purpose — the object graph is small and this keeps start-up fast and dependency-free.
/// </summary>
public sealed class AppHost : IDisposable
{
    private readonly HttpClient? _http;
    private readonly TimerHistory _history;
    private readonly ActionsMonitor _actions;
    private readonly WidgetController _widget;
    private readonly IslandController _island;
    private readonly TrayController _tray;

    private AppHost(bool demo, Action showMainWindow, Action quit)
    {
        var time = TimeProvider.System;
        var ui = new AvaloniaUiDispatcher();
        IUrlLauncher launcher = new GitHubUrlLauncher();

        ISettingsStore settingsStore;
        ITimerSessionStore sessionStore;
        ISecretStore secrets;
        IGitHubGateway gitHub;
        IDeviceFlowAuthenticator deviceFlow;
        IStateCache stateCache = new NullStateCache();

        if (demo)
        {
            settingsStore = new InMemorySettingsStore();
            sessionStore = new InMemoryTimerSessionStore();
            secrets = new InMemorySecretStore();
            gitHub = new DemoGitHubGateway(time);
            deviceFlow = new DemoDeviceFlowAuthenticator();
        }
        else
        {
            var paths = new AppPaths();
            _http = GitHubHttpClientFactory.Create();
            settingsStore = new JsonSettingsStore(paths);
            // Settings stay in a human-editable JSON file; everything the app learns lives in SQLite.
            var database = new SqliteDatabase(paths.DatabaseFile);
            sessionStore = new SqliteTimerSessionStore(database, new JsonTimerSessionStore(paths));
            stateCache = new SqliteStateCache(database);
            secrets = SecretStoreFactory.Create(paths);
            gitHub = new GitHubGateway(_http);
            AvatarImage.Loader = new AvatarDownloader(_http);
            deviceFlow = new DeviceFlowAuthenticator(_http, time);
        }

        MarkdownView.LinkOpener = launcher;
        Settings = new SettingsService(settingsStore);
        // Small local file; loading it synchronously keeps the first frame consistent with the saved settings.
        Settings.LoadAsync().GetAwaiter().GetResult();

        var accounts = new AccountService(Settings, secrets, gitHub, deviceFlow, new GitHubCliTokenSource());
        var toasts = new ToastPresenter(
            launcher, showMainWindow, () => Settings.Current.ReduceMotion, tone => Settings.Current.AutoCloses(tone));
        var notifications = new NotificationRouter(Settings, toasts, new NativeNotifier());
        Monitor = new PullRequestMonitor(Settings, accounts, gitHub, notifications, time, stateCache);
        _actions = new ActionsMonitor(Settings, accounts, gitHub, time);
        _history = new TimerHistory(sessionStore);

        if (demo)
        {
            var account = accounts.AddWithTokenAsync("demo", AuthMethod.PersonalAccessToken).GetAwaiter().GetResult();
            Settings.UpdateAsync(s => s with
            {
                Repositories =
                [
                    .. DemoGitHubGateway.Monitored.Select(r => new MonitoredRepository(account.Id, r.Owner, r.Name)),
                ],
            }).GetAwaiter().GetResult();
        }

        Shell = new ShellViewModel(Monitor, Settings, accounts, _history, notifications,
            new PersonActivityService(Settings, accounts, gitHub, time), launcher, new SystemAlarm(Settings), ui, time, _actions);
        _widget = new WidgetController(Shell, Settings, showMainWindow);
        _island = new IslandController(Shell, Settings, launcher, showMainWindow);
        toasts.Island = _island.TryShow;
        // Closing a notification on one surface closes the same notification on the other.
        toasts.Closed += _island.CloseFor;
        _island.Closed += toasts.CloseFor;
        _tray = new TrayController(Shell.Widget, Settings, showMainWindow, quit);
    }

    public SettingsService Settings { get; }
    public PullRequestMonitor Monitor { get; }
    public ShellViewModel Shell { get; }

    public static AppHost Create(bool demo, Action showMainWindow, Action quit) => new(demo, showMainWindow, quit);

    public void Start()
    {
        _ = _history.LoadAsync();
        _widget.Start();
        _island.Start();
        _tray.Start();
        Monitor.Start();
        _actions.Start();
    }

    public void Dispose()
    {
        _tray.Dispose();
        _island.Dispose();
        _widget.Dispose();
        Shell.Dispose();
        // Bounded wait so a slow network call can never keep the process alive after Quit.
        Monitor.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        _actions.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        _http?.Dispose();
    }
}
