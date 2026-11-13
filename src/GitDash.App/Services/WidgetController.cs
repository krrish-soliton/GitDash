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
            _timer.Position = Place(_timer, s.TimerWidgetX, s.TimerWidgetY, 72 + 190);
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

    private Func<int, int, AppSettings> SaveFor(FloatingWindow window) =>
        (x, y) => settings.Current with { TimerWidgetX = x, TimerWidgetY = y };

    /// <summary>The counter widget is remembered as percentages of the free screen room, which keeps it on screen.</summary>
    private void RememberCounter()
    {
        if (_counter is null) return;
        var (x, y) = _counter.RememberedPercent();
        _ = settings.UpdateAsync(c => c with { WidgetXPercent = x, WidgetYPercent = y });
    }

    private void Remember(FloatingWindow window, Func<int, int, AppSettings> change)
    {
        var (x, y) = (window.RememberedPosition.X, window.RememberedPosition.Y);
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

    private static PixelPoint Place(FloatingWindow window, int? x, int? y, int defaultTop)
    {
        var screen = window.Screens.Primary ?? window.Screens.All.FirstOrDefault();
        if (screen is null) return new PixelPoint(x ?? 80, y ?? 80);

        // A remembered spot is only reused while it is still on a connected screen.
        if (x is { } sx && y is { } sy && window.Screens.All.Any(s => s.WorkingArea.Contains(new PixelPoint(sx + 20, sy + 20))))
            return new PixelPoint(sx, sy);

        var scale = screen.Scaling;
        return new PixelPoint(
            screen.WorkingArea.Right - (int)((window.Width + 24) * scale),
            screen.WorkingArea.Y + (int)(defaultTop * scale));
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
