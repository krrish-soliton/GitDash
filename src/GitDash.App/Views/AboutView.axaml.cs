using Avalonia.Controls;
using Avalonia.Interactivity;
using GitDash.Presentation.ViewModels;

namespace GitDash.App.Views;

public partial class AboutView : UserControl
{
    public AboutView() => InitializeComponent();

    private async void OnCopyEmailClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AboutViewModel about) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        await clipboard.SetTextAsync(about.Email);
        if (sender is Button button) button.Content = "Copied";
    }
}
