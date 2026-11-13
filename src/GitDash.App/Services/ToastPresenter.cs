using Avalonia;
using Avalonia.Threading;
using GitDash.Application.Abstractions;
using GitDash.App.Windows;

namespace GitDash.App.Services;

/// <summary>Shows the app's own animated toasts, stacked in a screen corner, at most a few at a time.</summary>
public sealed class ToastPresenter(IUrlLauncher launcher, Action showMainWindow, Func<bool> reduceMotion, Func<ToastTone, bool> autoClose)
    : IToastPresenter
{
    private const int MaxVisible = 4;
    private const int MaxQueued = 20;
    private readonly List<ToastWindow> _open = [];
    private readonly Queue<ToastRequest> _queue = new();

    /// <summary>When set and it accepts the request (island switched on), no corner toast is shown.</summary>
    public Func<ToastRequest, bool>? Island { get; set; }

    /// <summary>Raised when a toast ends (closed, clicked or timed out), so the same notification closes on the island too.</summary>
    public event Action<ToastRequest>? Closed;

    /// <summary>Closes the toast (or drops it from the queue) for a notification that was closed on the island.</summary>
    public void CloseFor(ToastRequest request) => Dispatcher.UIThread.Post(() =>
    {
        foreach (var window in _open.Where(w => ReferenceEquals(w.Request, request)).ToList()) window.DismissFromOutside();
        if (_queue.Any(r => ReferenceEquals(r, request)))
        {
            var rest = _queue.Where(r => !ReferenceEquals(r, request)).ToList();
            _queue.Clear();
            foreach (var r in rest) _queue.Enqueue(r);
        }
    });

    public void Show(ToastRequest request) => Dispatcher.UIThread.Post(() =>
    {
        if (Island?.Invoke(request) == true) return;
        if (_open.Count >= MaxVisible)
        {
            if (_queue.Count < MaxQueued) _queue.Enqueue(request);
            return;
        }

        Open(request);
    });

    private void Open(ToastRequest request)
    {
        var window = new ToastWindow(request, !reduceMotion(), autoClose(request.Tone), OnDismissed);
        _open.Add(window);
        Restack();
        window.Show();
        Restack(); // screen information is only reliable once the window exists on a screen
    }

    private void OnDismissed(ToastWindow window, bool activated)
    {
        _open.Remove(window);
        if (window.ClosedLocally) Closed?.Invoke(window.Request);
        if (activated)
        {
            if (window.Request.Url is { } url) launcher.Open(url);
            else showMainWindow();
        }

        Restack();
        if (_queue.TryDequeue(out var next)) Open(next);
    }

    private void Restack()
    {
        for (var i = 0; i < _open.Count; i++)
        {
            var window = _open[i];
            var screen = window.Screens.Primary ?? window.Screens.All.FirstOrDefault();
            if (screen is null) continue;

            var scale = screen.Scaling;
            var area = screen.WorkingArea;
            var width = (int)(window.Width * scale);
            var height = (int)(window.Height * scale);
            var margin = (int)(16 * scale);
            var gap = (int)(10 * scale);
            var x = area.Right - width - margin;

            // macOS users expect notifications at the top right, Windows users at the bottom right.
            var y = OperatingSystem.IsMacOS()
                ? area.Y + margin + i * (height + gap)
                : area.Bottom - margin - height - i * (height + gap);
            window.Position = new PixelPoint(x, y);
        }
    }
}
