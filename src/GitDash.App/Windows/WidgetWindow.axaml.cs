using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GitDash.Presentation.ViewModels;

namespace GitDash.App.Windows;

/// <summary>
/// Counter widget. Mini mode: a small circle with the assigned count (and a bubble for new notifications) that opens
/// into the full card while the cursor is over it. Full mode: the card is simply always shown.
/// Click-through while locked, draggable while unlocked.
///
/// The full card has one fixed place on screen (saved as percentages, so it is always on screen); the circle sits at a
/// chosen spot inside that area. Opening grows the card from the circle's spot to the whole area (the window itself
/// switches size at once; the growth is the card's scale animation about the circle's centre). Nothing accumulates,
/// so the circle never creeps sideways between states.
/// </summary>
public partial class WidgetWindow : FloatingWindow
{
    private const double CircleSize = 92, CardWidth = 320, CardHeight = 178;
    private static readonly TimeSpan CollapseTime = TimeSpan.FromMilliseconds(380);

    private readonly Action? _open;
    private readonly Action? _hide;

    private WidgetLayout _layout = new(true, 98, 8, 100, 50);
    private bool _everApplied;
    private bool _expanded;       // the window currently has the card's size
    private int _circleOffsetX;   // where the circle sits inside the card area, in screen pixels
    private int _circleOffsetY;
    private int _generation;      // invalidates a pending delayed collapse

    // Required by the XAML loader and the previewer.
    public WidgetWindow() => InitializeComponent();

    public WidgetWindow(Action open, Action hide)
    {
        InitializeComponent();
        _open = open;
        _hide = hide;
    }

    /// <summary>Card place on screen (0–100 %), and the circle's place inside the card (0–100 %).</summary>
    public readonly record struct WidgetLayout(bool Mini, double XPercent, double YPercent, double MiniXPercent, double MiniYPercent);

    private double Scale => RenderScaling <= 0 ? 1 : RenderScaling;

    private int Px(double logical) => (int)Math.Round(logical * Scale);

    private PixelRect PrimaryArea() =>
        (Screens.Primary ?? Screens.All.FirstOrDefault())?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);

    /// <summary>Applies the user's settings: size and place of the widget in its resting state.</summary>
    public void ApplyLayout(WidgetLayout layout)
    {
        if (layout == _layout && _everApplied) return;
        _everApplied = true;
        _layout = layout;
        _generation++;
        Classes.Set("full", !layout.Mini);

        int cw = Px(CircleSize), kw = Px(CardWidth), kh = Px(CardHeight);
        var area = PrimaryArea();
        var card = new PixelPoint(
            area.X + (int)Math.Round(Math.Clamp(layout.XPercent, 0, 100) / 100 * Math.Max(0, area.Width - kw)),
            area.Y + (int)Math.Round(Math.Clamp(layout.YPercent, 0, 100) / 100 * Math.Max(0, area.Height - kh)));
        _circleOffsetX = (int)Math.Round(Math.Clamp(layout.MiniXPercent, 0, 100) / 100 * Math.Max(0, kw - cw));
        _circleOffsetY = (int)Math.Round(Math.Clamp(layout.MiniYPercent, 0, 100) / 100 * Math.Max(0, kh - cw));

        // Always start from the card (expanded) state, then collapse unless the card should stay open.
        _expanded = true;
        PositionParts();
        Place(card, CardWidth, CardHeight);
        if (layout.Mini && IsLocked && !Classes.Contains("hover")) Collapse();
    }

    /// <summary>The place to save: always the top-left of the full card.</summary>
    public override PixelPoint RememberedPosition =>
        _layout.Mini && !_expanded ? new PixelPoint(Position.X - _circleOffsetX, Position.Y - _circleOffsetY) : Position;

    /// <summary>The remembered place expressed as the 0–100 % the settings use (clamped, so it is always on screen).</summary>
    public (double X, double Y) RememberedPercent()
    {
        var p = RememberedPosition;
        var area = PrimaryArea();
        static double Pct(int value, int origin, int room) =>
            room <= 0 ? 0 : Math.Round(Math.Clamp((value - origin) * 100.0 / room, 0, 100), 1);
        return (Pct(p.X, area.X, area.Width - Px(CardWidth)), Pct(p.Y, area.Y, area.Height - Px(CardHeight)));
    }

    protected override void OnHoverChanged(bool hovering)
    {
        if (hovering) _generation++;

        if (!_layout.Mini)
        {
            if (!hovering) (DataContext as WidgetViewModel)?.MarkSeen();
            return;
        }

        if (hovering)
        {
            Expand();
            return;
        }

        // The notification was seen once the card has been shown and the cursor moves away.
        (DataContext as WidgetViewModel)?.MarkSeen();
        // Let the collapse animation play before the window shrinks around it.
        var generation = ++_generation;
        DispatcherTimer.RunOnce(() =>
        {
            if (generation != _generation || !_expanded || Classes.Contains("hover")) return;
            Collapse();
        }, CollapseTime);
    }

    /// <summary>The window becomes the whole card area; the circle stays where it is on screen and the card grows out of it.</summary>
    private void Expand()
    {
        if (_expanded) return;
        var card = new PixelPoint(Position.X - _circleOffsetX, Position.Y - _circleOffsetY);
        _expanded = true;
        // Parts first, then the window, so the circle is never drawn in the wrong place for a frame.
        PositionParts();
        Place(card, CardWidth, CardHeight);
    }

    private void Collapse()
    {
        // The circle goes back to exactly where it was: the offsets are the integers used when expanding.
        var home = new PixelPoint(Position.X + _circleOffsetX, Position.Y + _circleOffsetY);
        _expanded = false;
        PositionParts();
        Place(home, CircleSize, CircleSize);
    }

    /// <summary>
    /// Puts the circle at its place in the window (the card area while expanded, the origin while collapsed) and makes
    /// the card grow out of the circle's centre.
    /// </summary>
    private void PositionParts()
    {
        var dot = this.FindControl<Panel>("Dot");
        var card = this.FindControl<Panel>("Card");
        if (dot is null || card is null) return;

        var (x, y) = _expanded ? (_circleOffsetX / Scale, _circleOffsetY / Scale) : (0d, 0d);
        dot.Margin = new Thickness(x, y, 0, 0);
        // The card fills the window minus the 6 px root margin; the circle's centre in the card's own coordinates:
        card.RenderTransformOrigin = new RelativePoint(
            _circleOffsetX / Scale + CircleSize / 2 - 6, _circleOffsetY / Scale + CircleSize / 2 - 6, RelativeUnit.Absolute);
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e) => _open?.Invoke();

    private void OnOpenClick(object? sender, RoutedEventArgs e) => _open?.Invoke();

    private void OnHideClick(object? sender, RoutedEventArgs e) => _hide?.Invoke();
}
