using GitDash.Application.Abstractions;
using GitDash.Application.Settings;
using GitDash.Domain.Notifications;

namespace GitDash.Application.Monitoring;

/// <summary>Sends a notification to the animated toasts or to the OS, depending on the user's choice.</summary>
public sealed class NotificationRouter(SettingsService settings, IToastPresenter toasts, INativeNotifier native)
{
    public Task PublishAsync(PullRequestNotification notification, CancellationToken ct = default) =>
        PublishAsync(new ToastRequest(
            notification.Title,
            notification.Message,
            notification.Kind switch
            {
                NotificationKind.AssignedToMe => ToastTone.Assigned,
                NotificationKind.ReviewRequested => ToastTone.Review,
                NotificationKind.NewComment => ToastTone.Comment,
                NotificationKind.UnassignedFromMe => ToastTone.Removed,
                _ => ToastTone.Merged,
            },
            notification.PullRequest.Url), ct);

    /// <summary>Raised for every notification, whichever way it ends up being displayed (used for the widget badge).</summary>
    public event Action<ToastRequest>? Published;

    public async Task PublishAsync(ToastRequest request, CancellationToken ct = default)
    {
        Published?.Invoke(request);
        if (settings.Current.NotificationStyle == NotificationStyle.Native)
        {
            try
            {
                await native.ShowAsync(request.Title, request.Message, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Native delivery is best-effort; fall back so the event is not lost.
            }
        }

        toasts.Show(request);
    }
}
