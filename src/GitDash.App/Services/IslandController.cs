using Avalonia.Threading;
using GitDash.Application.Abstractions;
using GitDash.Application.Settings;
using GitDash.App.Windows;
using GitDash.Presentation.ViewModels;

namespace GitDash.App.Services;

/// <summary>
/// Owns the dynamic-island window while it is switched on and, when it is, takes over showing notifications
/// (stacked, newest on top) instead of the corner toasts.
/// </summary>
public sealed class IslandController(
    ShellViewModel shell, SettingsService settings, Action showMainWindow) : IDisposable
{
    private IslandWindow? _window;

    public bool IsActive => _window is not null;

    /// <summary>Raised when a notification on the island ends (closed, clicked or timed out).</summary>
    public event Action<ToastRequest>? Closed;

    /// <summary>Closes the notification because it was closed as a corner toast.</summary>
    public void CloseFor(ToastRequest request) => Dispatcher.UIThread.Post(() => _window?.DismissFromOutside(request));

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
        }

        if (_window is not null)
        {
            _window.SetMotion(!s.ReduceMotion);
            _window.SetGlass(s.GlassEffect);
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
        // Notifications stack on the island: a new one does not wait for the earlier ones to be closed.
        Dispatcher.UIThread.Post(() => _window?.Present(
            request, settings.Current.AutoCloses(request.Tone), !settings.Current.ReduceMotion));
        return !settings.Current.ShowToastWithIsland;
    }

    private void OnActivated(ToastRequest? request)
    {
        showMainWindow();
        if (request?.Url is { } url) shell.OpenPullRequest(url);
    }

    public void Dispose()
    {
        settings.Changed -= OnChanged;
        _window?.Close();
        _window = null;
    }
}
