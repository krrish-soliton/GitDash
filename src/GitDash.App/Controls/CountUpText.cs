using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace GitDash.App.Controls;

/// <summary>
/// A number that rolls to its new value. The timer exists only for the ~0.4 s of the roll,
/// so an idle dashboard does no work at all.
/// </summary>
public sealed class CountUpText : TextBlock
{
    public static readonly StyledProperty<int> ValueProperty =
        AvaloniaProperty.Register<CountUpText, int>(nameof(Value));

    public static readonly StyledProperty<bool> AnimateProperty =
        AvaloniaProperty.Register<CountUpText, bool>(nameof(Animate), true);

    private const int Steps = 14;
    private DispatcherTimer? _timer;
    private int _from, _step;
    private double _shown;

    public CountUpText() => Text = "0";

    public int Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public bool Animate
    {
        get => GetValue(AnimateProperty);
        set => SetValue(AnimateProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ValueProperty) return;

        if (!Animate || !IsEffectivelyVisible)
        {
            Stop();
            Show(Value);
            return;
        }

        _from = (int)Math.Round(_shown);
        _step = 0;
        _timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Render, OnTick);
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _step++;
        var t = Math.Min(1d, (double)_step / Steps);
        var eased = 1 - Math.Pow(1 - t, 3);
        Show(_from + (Value - _from) * eased);
        if (_step >= Steps) Stop();
    }

    private void Show(double value)
    {
        _shown = value;
        Text = ((int)Math.Round(value)).ToString();
    }

    private void Stop() => _timer?.Stop();

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Stop();
        Show(Value);
        base.OnDetachedFromVisualTree(e);
    }
}
