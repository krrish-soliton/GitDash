using System.Windows.Input;
using GitDash.Domain.Timers;
using GitDash.Presentation.Mvvm;

namespace GitDash.Presentation.ViewModels;

/// <summary>One alarm in the list on the Timers page.</summary>
public sealed class AlarmItemViewModel(
    AlarmEntry entry, Action<AlarmEntry, bool> setEnabled, Action<AlarmEntry> remove) : ObservableObject
{
    private bool _isEnabled = entry.Enabled;

    public Guid Id => entry.Id;
    public string Label => string.IsNullOrWhiteSpace(entry.Label) ? "Alarm" : entry.Label;
    public string Time => entry.At.ToString("t");
    public string Repeat => entry.Daily ? "Every day" : "Once";

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (!Set(ref _isEnabled, value)) return;
            setEnabled(entry, value);
        }
    }

    public ICommand RemoveCommand { get; } = new RelayCommand(() => remove(entry));
}
