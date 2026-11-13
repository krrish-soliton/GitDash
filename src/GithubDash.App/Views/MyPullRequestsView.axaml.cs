using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GithubDash.Presentation.ViewModels;

namespace GithubDash.App.Views;

public partial class MyPullRequestsView : UserControl
{
    private DateTime _lastWheel;

    public MyPullRequestsView() => InitializeComponent();

    private MyPullRequestsViewModel? Page => DataContext as MyPullRequestsViewModel;

    private static PullRequestCardViewModel? CardOf(object? sender) =>
        (sender as Control)?.DataContext as PullRequestCardViewModel;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Focus(); // so the arrow keys work straight away
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Page is not { } page) return;
        switch (e.Key)
        {
            case Key.Left when !page.IsDetailOpen:
                page.PreviousCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Right when !page.IsDetailOpen:
                page.NextCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                page.CloseDetailsCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        // One card per notch; trackpads send many small events, so rate-limit.
        var now = DateTime.UtcNow;
        if (Page is not { IsDetailOpen: false } page || (now - _lastWheel).TotalMilliseconds < 140) return;
        _lastWheel = now;
        var delta = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? -e.Delta.X : e.Delta.Y;
        if (delta > 0) page.PreviousCommand.Execute(null);
        else if (delta < 0) page.NextCommand.Execute(null);
        e.Handled = true;
    }

    private void OnCardTapped(object? sender, TappedEventArgs e)
    {
        // Buttons on the card have their own handlers.
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
        if (CardOf(sender) is { } card) Page?.ActivateCommand.Execute(card);
        Focus();
    }

    private void OnDetailsClick(object? sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } card) Page?.ShowDetailsCommand.Execute(card);
    }

    private void OnStartReviewClick(object? sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } card) Page?.StartReviewCommand.Execute(card);
    }

    private void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } card) Page?.OpenInBrowserCommand.Execute(card);
    }

    // Buttons in the popup header act on the pull request the popup is showing.
    private void OnDetailReviewClick(object? sender, RoutedEventArgs e)
    {
        if (Page?.DetailCard is { } card) Page.StartReviewCommand.Execute(card);
    }

    private void OnDetailOpenClick(object? sender, RoutedEventArgs e)
    {
        if (Page?.DetailCard is { } card) Page.OpenInBrowserCommand.Execute(card);
    }

    private void OnScrimTapped(object? sender, TappedEventArgs e) => Page?.CloseDetailsCommand.Execute(null);

    // Give the keyboard back to the cover flow so ← → keep browsing cards after a filter is picked.
    private void OnFilterChanged(object? sender, SelectionChangedEventArgs e) => Focus();
}
