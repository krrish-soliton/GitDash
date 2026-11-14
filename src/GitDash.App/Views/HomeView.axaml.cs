using Avalonia.Controls;
using Avalonia.Input;
using GitDash.Presentation.ViewModels;

namespace GitDash.App.Views;

public partial class HomeView : UserControl
{
    public HomeView() => InitializeComponent();

    private void OnAssignedTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: AssignedPullRequestViewModel pr } && DataContext is HomeViewModel page)
            page.OpenPullRequestCommand.Execute(pr.Url);
    }
}
