using Avalonia.Threading;
using GithubDash.Application.Abstractions;
using GithubDash.Application.Settings;
using GithubDash.App.Windows;
using GithubDash.Presentation.ViewModels;

namespace GithubDash.App.Services;

/// <summary>
/// Owns the dynamic-island window while it is switched on and, when it is, takes over showing notifications
/// (one at a time, the rest queue) instead of the corner toasts.
/// </summary>
public sealed class IslandController(
    ShellViewModel shell, SettingsService settings, IUrlLauncher launcher, Action showMainWindow) : IDisposable
{
    private const int MaxQueued = 20;
    private readonly Queue<ToastRequest> _queue = new();
    private IslandWindow? _window;
    private ToastRequest? _showing;

    public bool IsActive => _window is not null;

    /// <summary>Raised when a notification on the island ends (closed, clicked or timed out).</summary>
    public event Action<ToastRequest>? Closed;

    /// <summary>Closes the notification (or drops it from the queue) because it was closed as a corner toast.</summary>
    public void CloseFor(ToastRequest request) => Dispatcher.UIThread.Post(() =>
    {
        if (_window is not null && ReferenceEquals(_showing, request)) _window.DismissFromOutside(request);
        if (_queue.Any(r => ReferenceEquals(r, request)))
        {
            var rest = _queue.Where(r => !ReferenceEquals(r, request)).ToList();
            _queue.Clear();
            foreach (var r in rest) _queue.Enqueue(r);
        }
    });

    public void Start()
    {
        settings.Changed += OnChanged;
        Update(settings.Current);
    }

    private void OnChanged(AppSettings s) => Dispatcher.UIThread.Post(() => Update(s));

    private void Update(AppSettings s)
    {
        if (s.ShowDynamicIsland && _window is null)
        {
            _window = new IslandWindow(OnActivated) { DataContext = shell };
            _window.Dismissed += request => Closed?.Invoke(request);
            _window.Show();
            _window.PlaceAt(s.IslandX, s.IslandY);
        }
        else if (!s.ShowDynamicIsland && _window is not null)
        {
            Remember();
            _window.Close();
            _window = null;
            _showing = null;
            _queue.Clear();
        }

        if (_window is not null)
        {
            _window.SetMotion(!s.ReduceMotion);
            _window.SetIdleOpacity(s.IslandOpacity);
            _window.SetClickThroughEnabled(s.ClickThroughOverlays);

            // Leaving "unlocked" is the moment the user has finished dragging: remember where it ended up.
            if (!_window.IsLocked && s.WidgetLocked) Remember();
            _window.SetLocked(s.WidgetLocked);
        }
    }

    private void Remember()
    {
        if (_window is null) return;
        var (x, y) = _window.Anchor;
        _ = settings.UpdateAsync(s => s with { IslandX = x, IslandY = y });
    }

    /// <summary>
    /// Shows the notification on the island. Returns true when that is all that should happen; false when the island is
    /// off or the user also wants the ordinary corner toast.
    /// </summary>
    public bool TryShow(ToastRequest request)
    {
        if (_window is null) return false;
        Dispatcher.UIThread.Post(() =>
        {
            if (_window is null) return;
            if (_showing is not null)
            {
                if (_queue.Count < MaxQueued) _queue.Enqueue(request);
                return;
            }

            Present(request);
        });
        return !settings.Current.ShowToastWithIsland;
    }

    private void Present(ToastRequest request)
    {
        _showing = request;
        _window!.Present(request, settings.Current.AutoCloses(request.Tone), !settings.Current.ReduceMotion);
        // Present() ends by itself when auto-close is on; poll so the next queued notification follows.
        var watcher = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
        watcher.Tick += (_, _) =>
        {
            if (_window is { IsShowingNotification: true }) return;
            watcher.Stop();
            _showing = null;
            if (_queue.TryDequeue(out var next) && _window is not null) Present(next);
        };
        watcher.Start();
    }

    private void OnActivated(ToastRequest? request)
    {
        if (request?.Url is { } url) launcher.Open(url);
        else showMainWindow();
    }

    public void Dispose()
    {
        settings.Changed -= OnChanged;
        _window?.Close();
        _window = null;
    }
}
