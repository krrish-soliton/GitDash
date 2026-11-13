using Avalonia.Input;

namespace GitDash.App.Windows;

/// <summary>Floating clock for the running Pomodoro, PR review or task timer.</summary>
public partial class TimerWidgetWindow : FloatingWindow
{
    private readonly Action? _open;

    // Required by the XAML loader and the previewer.
    public TimerWidgetWindow() => InitializeComponent();

    public TimerWidgetWindow(Action open)
    {
        InitializeComponent();
        _open = open;
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e) => _open?.Invoke();
}
