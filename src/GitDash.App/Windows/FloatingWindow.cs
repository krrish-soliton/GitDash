using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitDash.App.Services;

namespace GitDash.App.Windows;

/// <summary>
/// Shared behaviour of the always-on-top desktop widgets.
/// Locked: semi-transparent and click-through; it lights up (class "hover") while the cursor is over it.
/// Unlocked: fully opaque and draggable, so it can be moved from the application's settings.
/// </summary>
public abstract class FloatingWindow : Window
{
    private static readonly TimeSpan HoverPoll = TimeSpan.FromMilliseconds(60);
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
    protected virtual bool IgnoresMouse => _locked && _clickThrough && !_overButton;

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
        NativeWindow.ShowOnAllDesktops(this);
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

    private bool _overButton;

    /// <summary>The cursor is over a "hit" button right now, so the mouse must not pass through.</summary>
    protected bool OverHitButton => _overButton;

    /// <summary>
    /// Buttons marked with the style class "hit" (the stop buttons, close) stay clickable while the window is
    /// click-through: a click-through window gets no pointer events, so the polled cursor position decides when to
    /// let the mouse in, and only while it is exactly over such a button.
    /// </summary>
    private bool CursorIsOverHitButton(PixelPoint cursor)
    {
        var scale = RenderScaling <= 0 ? 1 : RenderScaling;
        foreach (var button in this.GetVisualDescendants().OfType<Button>())
        {
            if (!button.Classes.Contains("hit") || !button.IsVisible || button.Bounds.Width <= 0) continue;
            if (button.TranslatePoint(default, this) is not { } origin) continue;
            var topLeft = new PixelPoint(
                Position.X + (int)Math.Round(origin.X * scale), Position.Y + (int)Math.Round(origin.Y * scale));
            var size = new PixelSize((int)Math.Round(button.Bounds.Width * scale), (int)Math.Round(button.Bounds.Height * scale));
            if (new PixelRect(topLeft, size).Contains(cursor)) return true;
        }

        return false;
    }

    /// <summary>macOS: the cursor is asked of the window itself, in its own units, so there is no screen-coordinate guesswork.</summary>
    private void PollHoverOnMac()
    {
        if (NativeWindow.CursorInWindow(this) is not { } local) return;
        var width = double.IsNaN(Width) ? Bounds.Width : Width;
        var height = double.IsNaN(Height) ? Bounds.Height : Height;
        var inside = local.X >= 0 && local.Y >= 0 && local.X <= width && local.Y <= height;
        var overButton = false;
        if (inside)
        {
            foreach (var button in this.GetVisualDescendants().OfType<Button>())
            {
                if (!button.Classes.Contains("hit") || !button.IsVisible || button.Bounds.Width <= 0) continue;
                if (button.TranslatePoint(default, this) is not { } origin) continue;
                if (new Rect(origin, button.Bounds.Size).Contains(local)) { overButton = true; break; }
            }
        }

        if (overButton != _overButton)
        {
            _overButton = overButton;
            RefreshClickThrough();
        }

        SetHover(inside);
    }

    private void PollHover()
    {
        if (!IsVisible) return;
        if (OperatingSystem.IsMacOS()) { PollHoverOnMac(); return; }
        if (NativeWindow.CursorPosition(RenderScaling) is not { } cursor) return;
        var inside = HoverRect.Contains(cursor);
        var overButton = inside && CursorIsOverHitButton(cursor);
        if (overButton != _overButton)
        {
            _overButton = overButton;
            RefreshClickThrough();
        }

        SetHover(inside);
    }
}
