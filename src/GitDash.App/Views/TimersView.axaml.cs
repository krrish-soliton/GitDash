using Avalonia.Controls;

namespace GitDash.App.Views;

public partial class TimersView : UserControl
{
    public TimersView() => InitializeComponent();

    /// <summary>Flyout content is not always given its button's data; make sure it edits the same timers.</summary>
    private void OnDurationsOpening(object? sender, EventArgs e)
    {
        if (sender is Flyout { Content: Control content, Target: { } target }) content.DataContext = target.DataContext;
    }
}
