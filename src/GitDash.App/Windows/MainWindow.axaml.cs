using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GitDash.Presentation.ViewModels;

namespace GitDash.App.Windows;

public partial class MainWindow : Window
{
    private ShellViewModel? _shell;
    private bool _sidebarHover;

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
        UpdateSidebar();
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.ReduceMotion)) UpdateMotion();
        if (e.PropertyName == nameof(ShellViewModel.SidebarPinned)) UpdateSidebar();
    }

    private const double SidebarOpen = 232, SidebarIcons = 78;

    /// <summary>
    /// Pinned: the sidebar is open and pushes the pages aside. Not pinned: it rests as an icon strip and, while the
    /// mouse is over it, opens over the pages without moving them.
    /// </summary>
    private void UpdateSidebar()
    {
        var pinned = _shell?.SidebarPinned ?? true;
        var open = pinned || _sidebarHover;
        if (this.FindControl<Panel>("SidebarHost") is { } host) host.Width = pinned ? SidebarOpen : SidebarIcons;
        if (this.FindControl<Border>("Sidebar") is { } bar)
        {
            bar.Width = open ? SidebarOpen : SidebarIcons;
            bar.Classes.Set("collapsed", !open);
            bar.Classes.Set("floating", !pinned && open);
        }

        if (this.FindControl<PathIcon>("CollapseIcon") is { } collapse) collapse.IsVisible = pinned;
        if (this.FindControl<PathIcon>("PinIcon") is { } pin) pin.IsVisible = !pinned;
    }

    private void OnSidebarEntered(object? sender, PointerEventArgs e)
    {
        _sidebarHover = true;
        UpdateSidebar();
    }

    private void OnSidebarExited(object? sender, PointerEventArgs e)
    {
        _sidebarHover = false;
        UpdateSidebar();
    }

    private void OnPinClick(object? sender, RoutedEventArgs e)
    {
        if (_shell is null) return;
        _shell.SidebarPinned = !_shell.SidebarPinned;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty || change.Property == IsVisibleProperty) UpdateMotion();
        if (change.Property == WindowStateProperty) UpdateChrome();
    }

    /// <summary>Square corners and the "restore" glyph while maximised; rounded corners and "maximise" otherwise.</summary>
    private void UpdateChrome()
    {
        var maximized = WindowState == WindowState.Maximized;
        if (this.FindControl<Border>("Frame") is { } frame)
        {
            frame.CornerRadius = new CornerRadius(maximized ? 0 : 14);
            frame.BorderThickness = new Thickness(maximized ? 0 : 1);
        }

        if (this.FindControl<PathIcon>("MaxIcon") is { } max) max.IsVisible = !maximized;
        if (this.FindControl<PathIcon>("RestoreIcon") is { } restore) restore.IsVisible = maximized;
    }

    private void OnTitlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
        BeginMoveDrag(e);
    }

    private void OnTitleDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
        ToggleMaximize();
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// All looping animations are styled under "Window.motion". Dropping the class while the window is
    /// hidden or minimised stops them completely, so the app idles at ~0% CPU in the tray.
    /// </summary>
    private void UpdateMotion() =>
        Classes.Set("motion", _shell is { ReduceMotion: false } && IsVisible && WindowState != WindowState.Minimized);
}
