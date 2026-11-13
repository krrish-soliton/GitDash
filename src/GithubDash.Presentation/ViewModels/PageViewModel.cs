using GithubDash.Application.Monitoring;
using GithubDash.Presentation.Mvvm;

namespace GithubDash.Presentation.ViewModels;

/// <summary>
/// Base for the main pages. Only the visible page receives <see cref="Apply"/>,
/// so hidden pages cost nothing while data keeps syncing.
/// </summary>
public abstract class PageViewModel(string title, string icon) : ObservableObject
{
    private bool _isSelected;

    public string Title { get; } = title;

    /// <summary>Key of a geometry resource in the UI layer.</summary>
    public string Icon { get; } = icon;

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public abstract void Apply(MonitorState state);
}
