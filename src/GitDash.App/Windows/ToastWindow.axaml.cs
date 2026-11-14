using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitDash.Application.Abstractions;
using GitDash.App.Converters;

namespace GitDash.App.Windows;

/// <summary>One animated notification. Never takes keyboard focus away from what the user is doing.</summary>
public partial class ToastWindow : Window
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan ExitDuration = TimeSpan.FromMilliseconds(230);

    private readonly Action<ToastWindow, bool>? _onDismissed;
    private readonly bool _animate;
    private DispatcherTimer? _timer;
    private readonly bool _autoClose = true;
    private bool _dismissed;

    /// <summary>True when it ended here (close button, click or its own timer) rather than because the island closed the same notification.</summary>
    public bool ClosedLocally { get; private set; }

    // Required by the XAML loader and the previewer.
    public ToastWindow()
    {
        InitializeComponent();
        Request = new ToastRequest(string.Empty, string.Empty, ToastTone.Review);
    }

    public ToastWindow(ToastRequest request, bool animate, bool autoClose, Action<ToastWindow, bool> onDismissed)
    {
        InitializeComponent();
        Request = request;
        _animate = animate;
        _autoClose = autoClose;
        _onDismissed = onDismissed;

        TitleText.Text = request.Title;
        MessageText.Text = request.Message;
        var accent = ToneBrushConverter.Brush(request.Tone.ToString());
        IconBadge.Background = accent;
        Life.Background = accent;
        if (GitDash.Presentation.Mvvm.AvatarDirectory.UrlFor(request.Actor) is { } avatar)
        {
            AvatarPic.Url = avatar;
            AvatarBadge.IsVisible = true;
        }

        if (animate)
        {
            Card.Classes.Add("anim");
            IconBadge.Classes.Add("anim");
            if (autoClose) Life.Classes.Add("anim");
        }

        // Without auto-close there is no countdown to show: the toast stays until it is closed.
        Life.IsVisible = autoClose;
    }

    public ToastRequest Request { get; }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        GitDash.App.Services.NativeWindow.HideFromSwitcher(this);
        if (!_autoClose) return;
        _timer = new DispatcherTimer(Lifetime, DispatcherPriority.Background, (_, _) => Dismiss(false));
        _timer.Start();
    }

    private void OnCardPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
        Dismiss(activated: true);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Dismiss(activated: false);
    }

    /// <summary>Closes it because the same notification was closed somewhere else (the dynamic island).</summary>
    public void DismissFromOutside() => Dismiss(activated: false, fromOutside: true);

    private async void Dismiss(bool activated, bool fromOutside = false)
    {
        if (_dismissed) return;
        _dismissed = true;
        ClosedLocally = !fromOutside;
        _timer?.Stop();

        if (_animate)
        {
            Card.Classes.Add("closing");
            await Task.Delay(ExitDuration);
        }

        Close();
        _onDismissed?.Invoke(this, activated);
    }
}
