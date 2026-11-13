using Avalonia.Controls;
using Avalonia.Input;
using GithubDash.Presentation.ViewModels;

namespace GithubDash.App.Views;

public partial class HomeView : UserControl
{
    public HomeView() => InitializeComponent();

    private void OnAssignedTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: AssignedPullRequestViewModel pr } && DataContext is HomeViewModel page)
            page.OpenUrlCommand.Execute(pr.Url);
    }
}
