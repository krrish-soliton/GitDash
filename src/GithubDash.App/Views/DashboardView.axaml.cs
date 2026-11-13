using Avalonia.Controls;
using Avalonia.Input;
using GithubDash.Presentation.ViewModels;

namespace GithubDash.App.Views;

public partial class DashboardView : UserControl
{
    public DashboardView() => InitializeComponent();

    private void OnActivityTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: ActivityItemViewModel item } &&
            DataContext is DashboardViewModel page)
        {
            page.OpenUrlCommand.Execute(item.Url);
        }
    }

    private void OnBookTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: DeskBookViewModel book } && DataContext is DashboardViewModel page)
            page.OpenUrlCommand.Execute(book.Url);
    }
}
