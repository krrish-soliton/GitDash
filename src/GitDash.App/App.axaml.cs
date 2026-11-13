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
