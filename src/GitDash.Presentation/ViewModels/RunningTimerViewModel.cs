using System.Windows.Input;
using GitDash.Presentation.Mvvm;

namespace GitDash.Presentation.ViewModels;

/// <summary>
/// One running timer as shown on the widget, the island and the dashboard: what it is, its clock and a slider.
/// The slider is the Pomodoro phase progress; for open-ended timers (task, review) it sweeps once per minute.
/// </summary>
public sealed class RunningTimerViewModel(string accent, Action? stop = null) : ObservableObject
{
    private string _label = string.Empty, _clock = "00:00";
    private double _progress;

    /// <summary>The stop button on the island and the widget: pauses this timer (nothing is lost; resume on the Timers page).</summary>
    public ICommand StopCommand { get; } = new RelayCommand(() => stop?.Invoke());

    /// <summary>Colour of the timer's slider, as #RRGGBB.</summary>
    public string Accent { get; } = accent;
    public string Label { get => _label; private set => Set(ref _label, value); }
    public string Clock { get => _clock; private set => Set(ref _clock, value); }
    /// <summary>0 to 1.</summary>
    public double Progress { get => _progress; private set => Set(ref _progress, value); }

    public void Update(string label, string clock, double progress)
    {
        Label = label;
        Clock = clock;
        Progress = Math.Clamp(progress, 0, 1);
    }

    /// <summary>Position of an open-ended timer within its current minute.</summary>
    public static double MinuteSweep(TimeSpan elapsed) => elapsed.TotalSeconds % 60 / 60;
}
