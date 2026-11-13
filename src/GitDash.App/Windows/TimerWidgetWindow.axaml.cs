using System.Collections.Specialized;
using Avalonia.Input;
using GitDash.Presentation.ViewModels;

namespace GitDash.App.Windows;

/// <summary>Floating clock for the running Pomodoro, PR review and task timers (one slider each).</summary>
public partial class TimerWidgetWindow : FloatingWindow
{
    private const double BaseHeight = 30, RowHeight = 72;
    private readonly Action? _open;
    private TimersViewModel? _timers;

    // Required by the XAML loader and the previewer.
    public TimerWidgetWindow() => InitializeComponent();

    public TimerWidgetWindow(Action open)
    {
        InitializeComponent();
        _open = open;
        DataContextChanged += (_, _) => Attach();
    }

    private void Attach()
    {
        if (_timers is not null) _timers.VisibleTimers.CollectionChanged -= OnTimersChanged;
        _timers = DataContext as TimersViewModel;
        if (_timers is null) return;
        _timers.VisibleTimers.CollectionChanged += OnTimersChanged;
        FitToTimers();
    }

    private void OnTimersChanged(object? sender, NotifyCollectionChangedEventArgs e) => FitToTimers();

    /// <summary>One slot per listed timer; the widget grows downwards from where it sits.</summary>
    private void FitToTimers() => Height = BaseHeight + RowHeight * Math.Max(1, _timers?.VisibleTimers.Count ?? 1);

    protected override void OnClosed(EventArgs e)
    {
        if (_timers is not null) _timers.VisibleTimers.CollectionChanged -= OnTimersChanged;
        base.OnClosed(e);
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e) => _open?.Invoke();
}
