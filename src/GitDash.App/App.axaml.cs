using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using GitDash.Application.Settings;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using GitDash.App.Services;
using GitDash.App.Windows;

namespace GitDash.App;

public partial class App : AvaloniaApplication
{
    private AppHost? _host;
    private bool _quitting;
    private bool _lastDockLogged = true;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The app keeps running in the tray when the window is closed.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var demo = desktop.Args?.Contains("--demo", StringComparer.OrdinalIgnoreCase) == true;
            var window = new MainWindow();
            var host = _host = AppHost.Create(demo, () => ShowMainWindow(window), () => Quit(desktop));
            window.DataContext = host.Shell;
            new WindowPlacementKeeper(window, host.Settings).Restore();
            ApplyTheme(host.Settings.Current.Theme);
            host.Settings.Changed += s => Dispatcher.UIThread.Post(() => ApplyTheme(s.Theme));
            desktop.MainWindow = window;

            window.Closing += (_, e) =>
            {
                if (_quitting) return;
                if (host.Settings.Current is { ShowTrayIcon: true, HideToTrayOnClose: true })
                {
                    e.Cancel = true;
                    window.Hide();
                }
                else
                {
                    Quit(desktop);
                }
            };
            // Minimising goes to the tray (and out of the taskbar) when asked for and a tray icon exists.
            window.PropertyChanged += (_, e) =>
            {
                if (e.Property != Window.WindowStateProperty || window.WindowState != WindowState.Minimized) return;
                if (host.Settings.Current is not { ShowTrayIcon: true, HideToTrayOnMinimize: true }) return;
                Dispatcher.UIThread.Post(window.Hide);
            };
            // macOS: with the option on, the Dock icon goes away while the window is hidden and returns when it is shown.
            // The Dock icon is also dropped while an overlay is on and "over full-screen apps" is chosen: macOS only lets
            // apps without a Dock icon put windows on top of full-screen apps.
            void UpdateDock()
            {
                var s = host.Settings.Current;
                var overlayOn = s.ShowDynamicIsland || s.ShowFloatingWidget || s.ShowTimerWidget;
                var overFullScreen = s is { OverlaysOverFullScreen: true, ShowTrayIcon: true } && overlayOn;
                var dockVisible = !overFullScreen && (window.IsVisible || !s.HideDockIconWhenHidden);
                if (OperatingSystem.IsMacOS() && dockVisible != _lastDockLogged)
                {
                    _lastDockLogged = dockVisible;
                    OverlayLog.Write($"dock icon visible={dockVisible} (overlayOn={overlayOn}, over-full-screen setting={s.OverlaysOverFullScreen}, " +
                                     $"tray icon={s.ShowTrayIcon}, window visible={window.IsVisible}, hide-while-hidden={s.HideDockIconWhenHidden}) {NativeWindow.Describe(window)}");
                }

                NativeWindow.SetDockIconVisible(dockVisible);
            }
            window.PropertyChanged += (_, e) => { if (e.Property == Avalonia.Visual.IsVisibleProperty) UpdateDock(); };
            host.Settings.Changed += _ => Dispatcher.UIThread.Post(UpdateDock);
            // The framework sets its own activation policy while the first windows open, which undid the call above:
            // look again once everything is up, and keep an eye on it while an overlay needs the Dock icon gone.
            window.Opened += (_, _) => Dispatcher.UIThread.Post(UpdateDock);
            if (OperatingSystem.IsMacOS())
            {
                foreach (var seconds in new[] { 1, 3 }) DispatcherTimer.RunOnce(UpdateDock, TimeSpan.FromSeconds(seconds));
                new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => UpdateDock()).Start();
            }
            desktop.Exit += (_, _) => host.Dispose();

            host.Start();

            var badge = new TaskbarBadge(window, host.Shell.Widget);
            badge.Start();
            desktop.Exit += (_, _) => badge.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ApplyTheme(AppTheme theme) => RequestedThemeVariant = theme switch
    {
        AppTheme.Light => ThemeVariant.Light,
        AppTheme.System => ThemeVariant.Default,
        _ => ThemeVariant.Dark,
    };

    private static void ShowMainWindow(Window window)
    {
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private void Quit(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (_quitting) return;
        _quitting = true;
        desktop.Shutdown();
    }
}
