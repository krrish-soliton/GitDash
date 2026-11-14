using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GitDash.Presentation.ViewModels;

namespace GitDash.App.Views;

public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Hook();
    }

    private DashboardViewModel? _page;

    private void Hook()
    {
        if (_page is not null) _page.PropertyChanged -= OnPageChanged;
        _page = DataContext as DashboardViewModel;
        if (_page is not null) _page.PropertyChanged += OnPageChanged;
    }

    /// <summary>When a book is picked on the shelf, scroll the list on the right to its entry.</summary>
    private void OnPageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DashboardViewModel.SelectedBook) || _page?.SelectedBook is not { } book) return;
        Dispatcher.UIThread.Post(() =>
        {
            var index = -1;
            for (var i = 0; i < _page.DeskBooks.Count; i++)
                if (_page.DeskBooks[i].Id == book.Id) { index = i; break; }
            if (index >= 0) DeskList.ContainerFromIndex(index)?.BringIntoView();
        }, DispatcherPriority.Background);
    }

    private void OnActivityTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: ActivityItemViewModel item } && _page is not null)
            _page.OpenUrlCommand.Execute(item.Url);
    }

    /// <summary>A click on a book (on the shelf or in the list) opens its details and highlights it.</summary>
    private void OnBookTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: DeskBookViewModel book } && _page is not null)
            _page.SelectBookCommand.Execute(book);
    }

    private void OnBackdropTapped(object? sender, TappedEventArgs e) => _page?.CloseBookCommand.Execute(null);

    private void OnPopupTapped(object? sender, TappedEventArgs e) => e.Handled = true;

    private void OnCloseBookClick(object? sender, RoutedEventArgs e) => _page?.CloseBookCommand.Execute(null);

    private void OnOpenBookClick(object? sender, RoutedEventArgs e)
    {
        if (_page?.SelectedBook is { } book) _page.OpenUrlCommand.Execute(book.Url);
    }
}
