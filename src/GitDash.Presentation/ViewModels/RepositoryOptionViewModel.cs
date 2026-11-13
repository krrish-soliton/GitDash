using GitDash.Presentation.Mvvm;

namespace GitDash.Presentation.ViewModels;

/// <summary>One checkbox in the repository filter.</summary>
public sealed class RepositoryOptionViewModel(string name, bool isAll, Action<RepositoryOptionViewModel> toggled) : ObservableObject
{
    private bool _isSelected;

    public string Name { get; } = name;

    /// <summary>The "All repositories" entry.</summary>
    public bool IsAll { get; } = isAll;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!Set(ref _isSelected, value)) return;
            toggled(this);
        }
    }

    /// <summary>Updates the checkbox without reporting it as a click.</summary>
    public void SetChecked(bool value) => Set(ref _isSelected, value, nameof(IsSelected));
}
