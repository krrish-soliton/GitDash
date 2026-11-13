using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using GithubDash.Presentation.ViewModels;

namespace GithubDash.App.Windows;

public partial class MainWindow : Window
{
    private ShellViewModel? _shell;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as ShellViewModel);
    }

    private void Attach(ShellViewModel? shell)
    {
        if (_shell is not null) _shell.PropertyChanged -= OnShellChanged;
        _shell = shell;
        if (_shell is not null) _shell.PropertyChanged += OnShellChanged;
        UpdateMotion();
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.ReduceMotion)) UpdateMotion();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty || change.Property == IsVisibleProperty) UpdateMotion();
    }

    /// <summary>
    /// All looping animations are styled under "Window.motion". Dropping the class while the window is
    /// hidden or minimised stops them completely, so the app idles at ~0% CPU in the tray.
    /// </summary>
    private void UpdateMotion() =>
        Classes.Set("motion", _shell is { ReduceMotion: false } && IsVisible && WindowState != WindowState.Minimized);
}
