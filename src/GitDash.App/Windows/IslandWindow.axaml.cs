using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitDash.App.Converters;
using GitDash.Application.Abstractions;
using GitDash.Presentation.ViewModels;

namespace GitDash.App.Windows;

/// <summary>
/// A pill at the top of the screen in the style of a dynamic island: a small status strip that grows to show a
/// notification and, while the cursor is over it, the oldest wait. Locked, it ignores the mouse (clicks fall through)
/// until it expands; unlocked in the settings, it can be dragged to a new place.
/// </summary>
/// <summary>One notification row on the island.</summary>
public sealed class IslandNote(ToastRequest request, bool autoClose)
{
    public ToastRequest Request => request;
    public bool AutoClose => autoClose;
    /// <summary>When the row closes by itself; set when it first becomes visible (null = stays until closed).</summary>
    public DateTime? Expires { get; set; }
    public string Title => request.Title;
    public string Message => request.Message;
    public IBrush Accent { get; } = ToneBrushConverter.Brush(request.Tone.ToString());
}

public partial class IslandWindow : FloatingWindow
{
    private const double CompactWidth = 230, CompactHeight = 36, WideWidth = 430, NoteHeight = 56, HoverHeight = 74;
    private const double CompactRowHeight = 34, ExpandSlack = 14;
    private const int MaxVisibleNotes = 4, MaxWaiting = 20;
    private const double WindowPadX = 10, WindowPadY = 12, TimerRowHeight = 38;
    private int _timerRows;
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan Shrink = TimeSpan.FromMilliseconds(340);

    private readonly Action<ToastRequest?> _activated = _ => { };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    // Notifications stack: the newest is on top, up to four are shown, the rest wait for room.
    private readonly System.Collections.ObjectModel.ObservableCollection<IslandNote> _notes = [];
    private readonly Queue<IslandNote> _waiting = new();
    private bool _hoverOpen;
    private readonly DispatcherTimer _rotate = new() { Interval = TimeSpan.FromSeconds(1) };
    private const double StatusRowHeight = 20, StatusPerRow = 3;

    // Required by the XAML loader and the previewer.
    public IslandWindow() => InitializeComponent();

    public IslandWindow(Action<ToastRequest?> activated)
    {
        InitializeComponent();
        _activated = activated;
        Width = CompactWidth + WindowPadX;
        Height = CompactHeight + WindowPadY;
        Notes.ItemsSource = _notes;
        _timer.Tick += (_, _) => CloseExpired();
        _rotate.Tick += (_, _) => RotateStatuses();
        _rotate.Start();
        DataContextChanged += (_, _) => AttachTimers();
    }

    private System.Collections.ObjectModel.ObservableCollection<RunningTimerViewModel>? _listed;

    private void AttachTimers()
    {
        if (_listed is not null) _listed.CollectionChanged -= OnTimersChanged;
        _listed = (DataContext as ShellViewModel)?.Timers.VisibleTimers;
        if (_listed is null) return;
        _listed.CollectionChanged += OnTimersChanged;
        OnTimersChanged(null, null);
    }

    /// <summary>Each running timer adds one line under the status row, so the island grows a little to fit them.</summary>
    private void OnTimersChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs? e)
    {
        var rows = Math.Min(_listed?.Count ?? 0, 4);
        if (rows == _timerRows) return;
        _timerRows = rows;
        if (_notes.Count > 0) Expand(WideWidth, NotesHeight);
        else if (_hoverOpen && IsLocked) Expand(WideWidth, HoverHeight);
        else Collapse();
    }

    /// <summary>Hovered open with more counters than fit one line: the row wraps and the pill grows to match.</summary>
    private double StatusExtra
    {
        get
        {
            if (!_hoverOpen || DataContext is not ShellViewModel shell) return 0;
            var count = shell.Widget.IslandItems.Count;
            return count <= 2 ? 0 : (Math.Ceiling(count / StatusPerRow) - 1) * StatusRowHeight;
        }
    }

    private double TimerExtra => _timerRows * TimerRowHeight + StatusExtra;

    /// <summary>Every second: after the configured interval, fade the pair out, swap it and fade back in.</summary>
    private void RotateStatuses()
    {
        if (DataContext is not ShellViewModel shell) return;
        if (_hoverOpen || !shell.Widget.TickIsland()) return;
        StatusRow.Opacity = 0;
        DispatcherTimer.RunOnce(() =>
        {
            shell.Widget.AdvanceIslandPage();
            StatusRow.Opacity = 1;
        }, TimeSpan.FromMilliseconds(190));
    }

    public bool IsShowingNotification => _notes.Count > 0;

    // The counter row stays visible above the notes, so it needs its own height (34) on top of the notes.
    private double NotesHeight => CompactRowHeight + _notes.Count * NoteHeight + 8;

    /// <summary>Raised when the notification ends here (closed, clicked or timed out).</summary>
    public event Action<ToastRequest>? Dismissed;

    /// <summary>Closes the notification because the same one was closed somewhere else (the corner toast).</summary>
    public void DismissFromOutside(ToastRequest request)
    {
        if (_notes.FirstOrDefault(n => ReferenceEquals(n.Request, request)) is { } note) Remove(note, fromOutside: true);
        else if (_waiting.Any(n => ReferenceEquals(n.Request, request)))
        {
            var rest = _waiting.Where(n => !ReferenceEquals(n.Request, request)).ToList();
            _waiting.Clear();
            foreach (var n in rest) _waiting.Enqueue(n);
        }
    }

    /// <summary>Mouse passes through unless click-through is off, it is being moved, or it shows a notification that must be closed by hand.</summary>
    protected override bool IgnoresMouse =>
        IsLocked && ClickThroughEnabled && !OverHitButton && !_notes.Any(n => !n.AutoClose);

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
        if (_listed is not null) _listed.CollectionChanged -= OnTimersChanged;
        _rotate.Stop();
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

    /// <summary>
    /// Adds a notification on top of the ones already shown. With <paramref name="autoClose"/> off it stays until
    /// closed. <paramref name="animate"/> is kept for the caller's sake; rows simply appear and the pill grows.
    /// </summary>
    public void Present(ToastRequest request, bool autoClose, bool animate)
    {
        var note = new IslandNote(request, autoClose);
        if (_notes.Count >= MaxVisibleNotes)
        {
            if (_waiting.Count < MaxWaiting) _waiting.Enqueue(note);
            return;
        }

        Show(note);
    }

    private void Show(IslandNote note)
    {
        note.Expires = note.AutoClose ? DateTime.UtcNow + Lifetime : null;
        _notes.Insert(0, note);
        Notes.IsVisible = true;
        Detail.IsVisible = false;
        Expand(WideWidth, NotesHeight);
        RefreshClickThrough();
        if (!_timer.IsEnabled) _timer.Start();
    }

    private void CloseExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var note in _notes.Where(n => n.Expires is { } at && at <= now).ToList()) Remove(note, fromOutside: false);
        if (_notes.Count == 0) _timer.Stop();
    }

    private void Remove(IslandNote note, bool fromOutside)
    {
        if (!_notes.Remove(note)) return;
        while (_notes.Count < MaxVisibleNotes && _waiting.TryDequeue(out var next))
        {
            next.Expires = next.AutoClose ? DateTime.UtcNow + Lifetime : null;
            _notes.Add(next);
        }

        RefreshClickThrough();
        if (!fromOutside) Dismissed?.Invoke(note.Request);
        if (_notes.Count > 0)
        {
            Expand(WideWidth, NotesHeight);
            return;
        }

        _timer.Stop();
        Notes.IsVisible = false;
        if (_hoverOpen && IsLocked) ShowHoverDetails();
        else Collapse();
    }

    protected override void OnHoverChanged(bool hovering)
    {
        // While being moved the island stays compact so it is easy to grab.
        if (!IsLocked) return;
        _hoverOpen = hovering;
        if (DataContext is ShellViewModel hovered) hovered.Widget.IslandExpanded = hovering;
        if (_notes.Count == 0)
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
        Notes.IsVisible = false;
        Detail.IsVisible = true;
        Expand(WideWidth, HoverHeight);
    }

    private void Collapse()
    {
        var pill = this.FindControl<Border>("Pill")!;
        pill.Width = CompactWidth;
        pill.Height = CompactHeight + TimerExtra;
        Detail.IsVisible = false;
        // Let the pill finish shrinking before the window follows it.
        DispatcherTimer.RunOnce(() =>
        {
            if (_notes.Count > 0 || _hoverOpen) return;
            Fit(CompactWidth + WindowPadX, CompactHeight + TimerExtra + WindowPadY);
        }, Shrink);
    }

    private void Expand(double width, double height)
    {
        // A little extra at the bottom so the last row (a timer slider, a note) is never touching the edge.
        height += TimerExtra + ExpandSlack;
        Fit(width + WindowPadX, height + WindowPadY);
        var pill = this.FindControl<Border>("Pill")!;
        pill.Width = width;
        pill.Height = height;
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsLocked) return; // dragging
        if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
        if ((e.Source as StyledElement)?.DataContext is IslandNote note)
        {
            Remove(note, fromOutside: false);
            _activated(note.Request);
            return;
        }

        _activated(null);
    }

    private void OnNoteCloseClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as StyledElement)?.DataContext is IslandNote note) Remove(note, fromOutside: false);
    }
}
