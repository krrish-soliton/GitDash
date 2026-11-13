using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using GitDash.Application.Settings;

namespace GitDash.App.Services;

/// <summary>
/// Opens the main window maximised on first start and afterwards where the user left it (position, size and
/// maximised or not). Changes are saved shortly after they stop, and once more when the window closes.
/// </summary>
public sealed class WindowPlacementKeeper
{
    private readonly Window _window;
    private readonly SettingsService _settings;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private WindowPlacement? _normal;

    public WindowPlacementKeeper(Window window, SettingsService settings)
    {
        _window = window;
        _settings = settings;
        _debounce.Tick += (_, _) => Save();
    }

    /// <summary>Applies the saved placement. Call before the window is shown.</summary>
    public void Restore()
    {
        var saved = _settings.Current.MainWindow;
        _normal = saved;
        if (saved is null)
        {
            _window.WindowState = WindowState.Maximized;
        }
        else
        {
            if (saved.Width >= _window.MinWidth && saved.Height >= _window.MinHeight)
            {
                _window.Width = saved.Width;
                _window.Height = saved.Height;
            }

            if (IsOnAScreen(saved))
            {
                _window.WindowStartupLocation = WindowStartupLocation.Manual;
                _window.Position = new PixelPoint(saved.X, saved.Y);
            }

            _window.WindowState = saved.Maximized ? WindowState.Maximized : WindowState.Normal;
        }

        _window.PositionChanged += (_, _) => Changed();
        _window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.BoundsProperty || e.Property == Window.WindowStateProperty) Changed();
        };
        _window.Closing += (_, _) => Save();
    }

    /// <summary>A window left on a monitor that was unplugged must not open out of reach.</summary>
    private bool IsOnAScreen(WindowPlacement p)
    {
        var screens = _window.Screens;
        if (screens is null) return true;
        var probe = new PixelPoint(p.X + 60, p.Y + 20);
        return screens.All.Any(s => s.WorkingArea.Contains(probe));
    }

    private void Changed()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void Save()
    {
        _debounce.Stop();
        if (_window.WindowState == WindowState.Minimized) return;
        var maximized = _window.WindowState == WindowState.Maximized;
        if (!maximized && _window.IsVisible)
            _normal = new WindowPlacement(_window.Position.X, _window.Position.Y, _window.Width, _window.Height, false);

        // Never seen un-maximised yet: remember a sensible size and a spot near the top-left of the screen.
        var keep = _normal ?? new WindowPlacement(80, 60, _window.Width, _window.Height, false);
        var placement = keep with { Maximized = maximized };
        if (placement == _settings.Current.MainWindow) return;
        _ = _settings.UpdateAsync(s => s with { MainWindow = placement });
    }
}
