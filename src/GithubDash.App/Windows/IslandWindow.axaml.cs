using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GithubDash.App.Converters;
using GithubDash.Application.Abstractions;
using GithubDash.Presentation.ViewModels;

namespace GithubDash.App.Windows;

/// <summary>
/// A pill at the top of the screen in the style of a dynamic island: a small status strip that grows to show a
/// notification and, while the cursor is over it, the oldest wait. Locked, it ignores the mouse (clicks fall through)
/// until it expands; unlocked in the settings, it can be dragged to a new place.
/// </summary>
public partial class IslandWindow : FloatingWindow
{
    private const double CompactWidth = 230, CompactHeight = 36, WideWidth = 430, NotifyHeight = 92, HoverHeight = 74;
    private const double WindowPadX = 10, WindowPadY = 12;
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan Shrink = TimeSpan.FromMilliseconds(340);

    private readonly Action<ToastRequest?> _activated = _ => { };
    private readonly DispatcherTimer _timer = new();
    private ToastRequest? _current;
    private bool _hoverOpen, _sticky;

    // Required by the XAML loader and the previewer.
    public IslandWindow() => InitializeComponent();

    public IslandWindow(Action<ToastRequest?> activated)
    {
        InitializeComponent();
        _activated = activated;
        Width = CompactWidth + WindowPadX;
        Height = CompactHeight + WindowPadY;
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Dismiss();
        };
    }

    public bool IsShowingNotification => _current is not null;

    /// <summary>Raised when the notification ends here (closed, clicked or timed out).</summary>
    public event Action<ToastRequest>? Dismissed;

    /// <summary>Closes the notification because the same one was closed somewhere else (the corner toast).</summary>
    public void DismissFromOutside(ToastRequest request)
    {
        if (ReferenceEquals(_current, request)) Dismiss(fromOutside: true);
    }

    /// <summary>Mouse passes through unless click-through is off, it is being moved, or it shows a notification that must be closed by hand.</summary>
    protected override bool IgnoresMouse => IsLocked && ClickThroughEnabled && !(_current is not null && _sticky);

    /// <summary>Horizontal centre and top edge in screen pixels: what is remembered as the island's place.</summary>
    public (int CenterX, int Top) Anchor => (CurrentCenterX(), Position.Y);

    private int _centerX;
    private PixelPoint _lastPos;
    private bool _hasCenter;

    private double Scale => RenderScaling <= 0 ? 1 : RenderScaling;

    /// <summary>
    /// The island's own centre. It differs from the window's centre while the window is shifted to stay on screen, and
    /// is re-read whenever something else (a drag, <see cref="PlaceAt"/>) has moved the window since we last placed it.
    /// </summary>
    private int CurrentCenterX()
    {
        if (!_hasCenter || Position != _lastPos)
        {
            _centerX = Position.X + (int)Math.Round(Width * Scale / 2);
            _hasCenter = true;
            _lastPos = Position;
        }

        return _centerX;
    }

    /// <summary>
    /// Resizes around the island's centre but never past the edges of the screen it is on: when the wide form would
    /// stick out, the window slides back in, and the centre is remembered so the compact form returns to the same spot.
    /// </summary>
    private void Fit(double width, double height)
    {
        var centre = CurrentCenterX();
        int wpx = (int)Math.Round(width * Scale), hpx = (int)Math.Round(height * Scale);
        // The pill is centred in its window, so its visual centre is the window's centre right now (before the resize).
        var visualCentre = Position.X + (int)Math.Round(Width * Scale / 2);
        var left = centre - wpx / 2;
        var top = Position.Y;
        var screen = Screens.ScreenFromPoint(new PixelPoint(centre, top)) ?? Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is not null)
        {
            var area = screen.WorkingArea;
            left = Math.Clamp(left, area.X, Math.Max(area.X, area.Right - wpx));
            top = Math.Clamp(top, area.Y, Math.Max(area.Y, area.Bottom - hpx));
        }

        var target = new PixelPoint(left, top);
        // New size first, around where the pill is now (so it grows/shrinks in place), then slide to the on-screen spot.
        var start = new PixelPoint(visualCentre - wpx / 2, Position.Y);
        _slide?.Stop();
        _slide = null;
        Place(start, width, height);
        _lastPos = start;
        if (start == target) return;
        if (!Classes.Contains("motion"))
        {
            Position = target;
            _lastPos = target;
            return;
        }

        var begun = Environment.TickCount64;
        _slide = new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, (_, _) =>
        {
            var t = Math.Min(1, (Environment.TickCount64 - begun) / SlideMs);
            var eased = 1 - Math.Pow(1 - t, 3); // ease out
            var at = new PixelPoint(
                start.X + (int)Math.Round((target.X - start.X) * eased),
                start.Y + (int)Math.Round((target.Y - start.Y) * eased));
            Position = at;
            _lastPos = at;
            if (t < 1) return;
            _slide?.Stop();
            _slide = null;
        });
        _slide.Start();
    }

    private const double SlideMs = 240;
    private DispatcherTimer? _slide;

    protected override void OnClosed(EventArgs e)
    {
        _slide?.Stop();
        _slide = null;
        base.OnClosed(e);
    }

    /// <summary>Puts the island at a remembered spot (if still on a screen) or at the top centre of the main screen.</summary>
    public void PlaceAt(int? centerX, int? top)
    {
        var scale = RenderScaling <= 0 ? 1 : RenderScaling;
        var half = (int)(Width * scale / 2);
        if (centerX is { } cx && top is { } y && Screens.All.Any(s => s.WorkingArea.Contains(new PixelPoint(cx, y + 4))))
        {
            Position = new PixelPoint(cx - half, y);
            return;
        }

        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null) return;
        Position = new PixelPoint(screen.WorkingArea.X + screen.WorkingArea.Width / 2 - half, screen.WorkingArea.Y);
    }

    /// <summary>Shows a notification. With <paramref name="autoClose"/> off it stays until closed.</summary>
    public void Present(ToastRequest request, bool autoClose, bool animate)
    {
        _current = request;
        _sticky = !autoClose;
        RefreshClickThrough();
        TitleText.Text = request.Title;
        MessageText.Text = request.Message;
        var accent = ToneBrushConverter.Brush(request.Tone.ToString());
        IconBadge.Background = accent;
        Life.Background = accent;
        CloseButton.IsVisible = true;

        Life.Classes.Remove("run");
        Life.IsVisible = autoClose;
        Detail.IsVisible = true;
        Expand(WideWidth, NotifyHeight);
        if (autoClose && animate) Life.Classes.Add("run");

        _timer.Stop();
        if (autoClose)
        {
            _timer.Interval = Lifetime;
            _timer.Start();
        }
    }

    private void Dismiss(bool fromOutside = false)
    {
        var closed = _current;
        _current = null;
        _sticky = false;
        RefreshClickThrough();
        _timer.Stop();
        Life.IsVisible = false;
        Life.Classes.Remove("run");
        if (_hoverOpen && IsLocked) ShowHoverDetails();
        else Collapse();
        if (!fromOutside && closed is not null) Dismissed?.Invoke(closed);
    }

    protected override void OnHoverChanged(bool hovering)
    {
        // While being moved the island stays compact so it is easy to grab.
        if (!IsLocked) return;
        _hoverOpen = hovering;
        if (_current is null)
        {
            if (hovering) ShowHoverDetails();
            else Collapse();
        }

        RefreshClickThrough();
    }

    private void ShowHoverDetails()
    {
        if (DataContext is not ShellViewModel shell) return;
        var w = shell.Widget;
        var any = w.HasOldest || w.HasOldestReview;
        TitleText.Text = any ? "Oldest waiting" : "Nothing is waiting on you";
        MessageText.Text = any
            ? $"Assigned {(w.HasOldest ? w.OldestAge : "none")}   ·   Review {(w.HasOldestReview ? w.OldestReviewAge : "none")}"
            : $"{w.Total} open pull requests across your repositories";
        IconBadge.Background = ToneBrushConverter.Brush(any ? "Assigned" : "Merged");
        CloseButton.IsVisible = false;
        Detail.IsVisible = true;
        Expand(WideWidth, HoverHeight);
    }

    private void Collapse()
    {
        var pill = this.FindControl<Border>("Pill")!;
        pill.Width = CompactWidth;
        pill.Height = CompactHeight;
        Detail.IsVisible = false;
        // Let the pill finish shrinking before the window follows it.
        DispatcherTimer.RunOnce(() =>
        {
            if (_current is not null || _hoverOpen) return;
            Fit(CompactWidth + WindowPadX, CompactHeight + WindowPadY);
        }, Shrink);
    }

    private void Expand(double width, double height)
    {
        Fit(width + WindowPadX, height + WindowPadY);
        var pill = this.FindControl<Border>("Pill")!;
        pill.Width = width;
        pill.Height = height;
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsLocked) return; // dragging
        if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
        var request = _current;
        if (request is not null) Dismiss();
        _activated(request);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Dismiss();
}
