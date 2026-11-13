using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using GitDash.App.Services;

namespace GitDash.App.Windows;

/// <summary>
/// Shared behaviour of the always-on-top desktop widgets.
/// Locked: semi-transparent and click-through; it lights up (class "hover") while the cursor is over it.
/// Unlocked: fully opaque and draggable, so it can be moved from the application's settings.
/// </summary>
public abstract class FloatingWindow : Window
{
    private static readonly TimeSpan HoverPoll = TimeSpan.FromMilliseconds(120);
    private DispatcherTimer? _hoverTimer;
    private bool _locked = true;

    protected FloatingWindow()
    {
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Background = Avalonia.Media.Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        WindowStartupLocation = WindowStartupLocation.Manual;
        Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(180) }];
    }

    private double _idleOpacity = 1;
    private bool _clickThrough = true;

    /// <summary>How opaque the window is while the cursor is away; hovering (or being moved) makes it solid.</summary>
    public void SetIdleOpacity(double opacity)
    {
        _idleOpacity = Math.Clamp(opacity, 0.2, 1);
        ApplyOpacity();
    }

    /// <summary>The user's choice: when on, a locked window lets mouse clicks fall through to what is underneath.</summary>
    public void SetClickThroughEnabled(bool enabled)
    {
        _clickThrough = enabled;
        RefreshClickThrough();
    }

    protected bool ClickThroughEnabled => _clickThrough;

    private void ApplyOpacity() => Opacity = !_locked || _hovering ? 1 : _idleOpacity;

    public bool IsLocked => _locked;

    /// <summary>The place that is saved and restored for this window (its top-left corner unless overridden).</summary>
    public virtual PixelPoint RememberedPosition => Position;

    /// <summary>Entrance and looping animations are styled under the "motion" class, same as the main window.</summary>
    public void SetMotion(bool enabled) => Classes.Set("motion", enabled);

    /// <summary>The user's choice: glass gloss, rim glow and sheen on the island and the widgets (class "glass").</summary>
    public void SetGlass(bool enabled) => Classes.Set("glass", enabled);

    /// <summary>Whether the mouse should currently pass straight through the window. Locked windows do, by default.</summary>
    protected virtual bool IgnoresMouse => _locked && _clickThrough;

    /// <summary>Called when the cursor enters or leaves a locked window (always called with true when unlocked).</summary>
    protected virtual void OnHoverChanged(bool hovering) { }

    /// <summary>Re-applies <see cref="IgnoresMouse"/> after something it depends on has changed.</summary>
    protected void RefreshClickThrough() => NativeWindow.SetClickThrough(this, IgnoresMouse);

    private bool _hovering;

    private void SetHover(bool hovering)
    {
        if (_hovering == hovering) return;
        _hovering = hovering;
        Classes.Set("hover", hovering);
        ApplyOpacity();
        OnHoverChanged(hovering);
    }

    private PixelPoint _placed;
    private double _placedWidth = double.NaN;

    /// <summary>
    /// Resizes the window while keeping one point fixed on screen: 0 = left edge, 0.5 = centre, 1 = right edge.
    /// The new left edge is computed from the last place we put the window (not from the platform's possibly
    /// rounded or lagging value), and the two roundings cancel out, so going big and back never drifts.
    /// </summary>
    protected void ResizeAnchored(double width, double height, double anchor)
    {
        var scale = RenderScaling <= 0 ? 1 : RenderScaling;
        // Something else (the user dragging, PlaceAt) moved the window since our last call: start from there.
        if (double.IsNaN(_placedWidth) || Position != _placed)
        {
            _placed = Position;
            _placedWidth = Width;
        }

        var anchorX = _placed.X + (int)Math.Round(_placedWidth * scale * anchor);
        var left = anchorX - (int)Math.Round(width * scale * anchor);
        Width = width;
        Height = height;
        _placed = new PixelPoint(left, Position.Y);
        _placedWidth = width;
        Position = _placed;
    }

    /// <summary>Sets size and place in one step and records them as the window's own state.</summary>
    protected void Place(PixelPoint topLeft, double width, double height)
    {
        Width = width;
        Height = height;
        _placed = topLeft;
        _placedWidth = width;
        Position = topLeft;
    }

    private bool _applied;

    public void SetLocked(bool locked)
    {
        // Settings change often; only a real lock/unlock should reset the hover state (otherwise sliders make it flicker).
        if (_applied && _locked == locked)
        {
            RefreshClickThrough();
            return;
        }

        _applied = true;
        _locked = locked;
        Classes.Set("unlocked", !locked);
        Classes.Set("locked", locked);
        RefreshClickThrough();

        _hoverTimer?.Stop();
        _hoverTimer = null;
        _hovering = !locked;
        Classes.Set("hover", !locked);
        ApplyOpacity();
        OnHoverChanged(!locked);
        if (!locked) return;

        // A click-through window gets no pointer events, so the cursor is polled; the timer only exists while locked.
        _hoverTimer = new DispatcherTimer(HoverPoll, DispatcherPriority.Background, (_, _) => PollHover());
        _hoverTimer.Start();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // The native window exists only now, so the click-through flag has to be (re)applied here.
        NativeWindow.HideFromSwitcher(this);
        _applied = false;
        SetLocked(_locked);
    }

    protected override void OnClosed(EventArgs e)
    {
        _hoverTimer?.Stop();
        _hoverTimer = null;
        base.OnClosed(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!_locked && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    /// <summary>
    /// The screen rectangle that counts as "the cursor is over it". It comes from the size and place we set (which change
    /// together, immediately), not from the laid-out bounds (which lag a frame behind a resize and made hover flip).
    /// </summary>
    protected virtual PixelRect HoverRect
    {
        get
        {
            var scale = RenderScaling <= 0 ? 1 : RenderScaling;
            return new PixelRect(Position, new PixelSize((int)Math.Round(Width * scale), (int)Math.Round(Height * scale)));
        }
    }

    private void PollHover()
    {
        if (!IsVisible || NativeWindow.CursorPosition(RenderScaling) is not { } cursor) return;
        SetHover(HoverRect.Contains(cursor));
    }
}
