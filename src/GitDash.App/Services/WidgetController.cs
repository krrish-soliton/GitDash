using Avalonia;
using Avalonia.Threading;
using GitDash.Application.Settings;
using GitDash.App.Windows;
using GitDash.Presentation.ViewModels;

namespace GitDash.App.Services;

/// <summary>
/// Creates the floating widgets (PR counters and timer) only while they are switched on, so they cost nothing when off.
/// Locked widgets are click-through; unlocking them in the settings makes them draggable, and their spot is remembered.
/// </summary>
public sealed class WidgetController(
    ShellViewModel shell, SettingsService settings, Action showMainWindow) : IDisposable
{
    private WidgetWindow? _counter;
    private TimerWidgetWindow? _timer;
    private DispatcherTimer? _clock;

    public void Start()
    {
        settings.Changed += OnSettingsChanged;
        shell.Timers.PropertyChanged += OnTimersChanged;
        Update(settings.Current);
    }

    private void OnSettingsChanged(AppSettings s) => Dispatcher.UIThread.Post(() => Update(s));

    /// <summary>The timer widget appears when a timer starts and goes away when the last one stops.</summary>
    private void OnTimersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TimersViewModel.IsAnyRunning))
            Dispatcher.UIThread.Post(() => Update(settings.Current));
    }

    private void Update(AppSettings s)
    {
        // Counter widget
        if (s.ShowFloatingWidget && _counter is null)
        {
            _counter = new WidgetWindow(
                showMainWindow, () => _ = settings.UpdateAsync(x => x with { ShowFloatingWidget = false }))
            {
                DataContext = shell.Widget,
            };
            _counter.Show();
            _counter.ApplyLayout(LayoutOf(s));
        }
        else if (!s.ShowFloatingWidget && _counter is not null)
        {
            RememberCounter();
            _counter.Close();
            _counter = null;
        }

        // Timer widget: only while at least one timer is running
        var showTimer = s.ShowTimerWidget && shell.Timers.IsAnyRunning;
        if (showTimer && _timer is null)
        {
            _timer = new TimerWidgetWindow(showMainWindow) { DataContext = shell.Timers };
            _timer.Show();
            _timer.Position = Place(_timer, s, 72 + 190);
        }
        else if (!showTimer && _timer is not null)
        {
            Remember(_timer, SaveFor(_timer));
            _timer.Close();
            _timer = null;
        }

        foreach (var window in new FloatingWindow?[] { _counter, _timer })
        {
            if (window is null) continue;
            window.SetMotion(!s.ReduceMotion);
            window.SetGlass(s.GlassEffect);
            window.SetIdleOpacity(s.WidgetOpacity);
            window.SetClickThroughEnabled(s.ClickThroughOverlays);

            // Leaving "unlocked" is the moment the user has finished dragging: remember where it ended up.
            if (!window.IsLocked && s.WidgetLocked)
            {
                if (window == _counter) RememberCounter();
                else Remember(window, SaveFor(window));
            }
            window.SetLocked(s.WidgetLocked);
        }

        _counter?.ApplyLayout(LayoutOf(s));

        UpdateClock();
    }

    private static WidgetWindow.WidgetLayout LayoutOf(AppSettings s) =>
        new(s.MiniWidget, s.WidgetXPercent, s.WidgetYPercent, s.MiniXPercent, s.MiniYPercent);

    private Func<double, double, AppSettings> SaveFor(FloatingWindow window) =>
        (x, y) => settings.Current with { TimerWidgetXPercent = x, TimerWidgetYPercent = y, TimerWidgetX = null, TimerWidgetY = null };

    /// <summary>The counter widget is remembered as percentages of the free screen room, which keeps it on screen.</summary>
    private void RememberCounter()
    {
        if (_counter is null) return;
        var (x, y) = _counter.RememberedPercent();
        _ = settings.UpdateAsync(c => c with { WidgetXPercent = x, WidgetYPercent = y });
    }

    /// <summary>Saved as percentages of the free room on the main screen, so another resolution keeps the same spot.</summary>
    private void Remember(FloatingWindow window, Func<double, double, AppSettings> change)
    {
        var area = (window.Screens.Primary ?? window.Screens.All.FirstOrDefault())?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        var scale = window.RenderScaling <= 0 ? 1 : window.RenderScaling;
        static double Pct(int value, int origin, double room) =>
            room <= 0 ? 0 : Math.Round(Math.Clamp((value - origin) * 100.0 / room, 0, 100), 2);
        var p = window.RememberedPosition;
        var x = Pct(p.X, area.X, area.Width - window.Width * scale);
        var y = Pct(p.Y, area.Y, area.Height - window.Height * scale);
        _ = settings.UpdateAsync(_ => change(x, y));
    }

    /// <summary>The counter widget shows live seconds, so it needs a one-second tick, but only while it is on screen.</summary>
    private void UpdateClock()
    {
        if (_counter is not null && _clock is null)
        {
            _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => shell.TickWidget());
            _clock.Start();
        }
        else if (_counter is null && _clock is not null)
        {
            _clock.Stop();
            _clock = null;
        }
    }

    private static PixelPoint Place(FloatingWindow window, AppSettings s, int defaultTop)
    {
        var screen = window.Screens.Primary ?? window.Screens.All.FirstOrDefault();
        if (screen is null) return new PixelPoint(s.TimerWidgetX ?? 80, s.TimerWidgetY ?? 80);

        var scale = screen.Scaling;
        var area = screen.WorkingArea;
        var w = (int)(window.Width * scale);
        var h = (int)(window.Height * scale);

        // Saved as a share of the free room on the screen: the same part of the screen at any resolution.
        if (s.TimerWidgetXPercent is { } px && s.TimerWidgetYPercent is { } py)
            return new PixelPoint(
                area.X + (int)Math.Round(Math.Clamp(px, 0, 100) / 100 * Math.Max(0, area.Width - w)),
                area.Y + (int)Math.Round(Math.Clamp(py, 0, 100) / 100 * Math.Max(0, area.Height - h)));

        // Older settings saved pixels: reuse them while they are still on a connected screen.
        if (s.TimerWidgetX is { } sx && s.TimerWidgetY is { } sy
            && window.Screens.All.Any(a => a.WorkingArea.Contains(new PixelPoint(sx + 20, sy + 20))))
            return new PixelPoint(sx, sy);

        return new PixelPoint(area.Right - (int)((window.Width + 24) * scale), area.Y + (int)(defaultTop * scale));
    }

    public void Dispose()
    {
        settings.Changed -= OnSettingsChanged;
        shell.Timers.PropertyChanged -= OnTimersChanged;
        _clock?.Stop();
        _counter?.Close();
        _timer?.Close();
        _counter = null;
        _timer = null;
    }
}
