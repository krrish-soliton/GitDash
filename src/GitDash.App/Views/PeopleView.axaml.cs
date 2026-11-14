using Avalonia.Controls;
using Avalonia.Input;
using GitDash.Presentation.ViewModels;

namespace GitDash.App.Views;

public partial class PeopleView : UserControl
{
    public PeopleView()
    {
        InitializeComponent();
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Page?.ClosePersonCommand.Execute(null);
            e.Handled = true;
        };
    }

    private PeopleViewModel? Page => DataContext as PeopleViewModel;

    /// <summary>The first time a person is drawn they walk in; the animation is a class added just once.</summary>
    private void OnStationAttached(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (sender is Border { Child: Panel panel, DataContext: PersonViewModel person } && person.ConsumeEntrance())
            panel.Classes.Add("walkin");
    }

    private void OnPersonTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: PersonViewModel person }) Page?.SelectPersonCommand.Execute(person);
        Focus(); // so Esc closes the panel
    }

    private void OnPullRequestTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: PullRequestLinkViewModel link }) Page?.OpenPullRequestCommand.Execute(link.Url);
    }

    private void OnPersonPrTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: PersonPullRequestViewModel pr }) Page?.OpenPullRequestCommand.Execute(pr.Url);
    }

    private void OnBranchTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: PendingBranchViewModel branch }) Page?.OpenUrlCommand.Execute(branch.Url);
    }

    private void OnPersonEventTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: PersonEventViewModel ev }) Page?.OpenPullRequestCommand.Execute(ev.Url);
    }

    private void OnPrFilterTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: FilterChipViewModel chip }) Page?.SetPrFilterCommand.Execute(chip);
    }

    private void OnEventFilterTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: FilterChipViewModel chip }) Page?.SetEventFilterCommand.Execute(chip);
    }

    private void OnScrimTapped(object? sender, TappedEventArgs e) => Page?.ClosePersonCommand.Execute(null);
}
