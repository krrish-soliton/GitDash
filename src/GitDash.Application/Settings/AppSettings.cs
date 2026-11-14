using GitDash.Application.Abstractions;
using GitDash.Domain.Notifications;
using GitDash.Domain.PullRequests;
using GitDash.Domain.Timers;

namespace GitDash.Application.Settings;

public enum AuthMethod
{
    PersonalAccessToken,
    DeviceFlow,
    /// <summary>The token of the user's GitHub CLI login, copied when the account was added.</summary>
    GitHubCli,
}

public enum NotificationStyle
{
    /// <summary>Animated in-app toasts.</summary>
    Custom,
    /// <summary>Windows / macOS notification centre.</summary>
    Native,
}

public enum AppTheme
{
    Dark,
    Light,
    System,
}

/// <summary>How dates and times are shown. Everything is converted from UTC (as GitHub reports it).</summary>
public enum TimeFormat
{
    /// <summary>Local time in the operating system's regional format.</summary>
    System,
    /// <summary>Local time, 24-hour clock.</summary>
    Hour24,
    /// <summary>Local time, 12-hour clock with AM / PM.</summary>
    Hour12,
    /// <summary>UTC, 24-hour clock.</summary>
    Utc,
}

/// <summary>Where the main window was last: position and size when not maximised, and whether it was maximised.</summary>
public sealed record WindowPlacement(int X, int Y, double Width, double Height, bool Maximized);

public sealed record AccountSettings(Guid Id, string Login, string? Name, AuthMethod Method, string? AvatarUrl = null);

/// <summary>
/// An AI tool whose usage limit the user wants to keep an eye on. There is no official way to read a
/// subscription's remaining quota, so the user marks "limit reached" and the app counts down to the reset.
/// </summary>
public sealed record AiToolSettings(Guid Id, string Name, int WindowMinutes, DateTimeOffset? ResetsAt = null);

public sealed record MonitoredRepository(Guid AccountId, string Owner, string Name)
{
    public RepositoryRef ToRef() => new(Owner, Name);
}

/// <summary>Everything the user configures. Contains no secrets and is safe to store as plain JSON.</summary>
public sealed record AppSettings
{
    public const int MinLiveCheckSeconds = 5;
    public const int MaxLiveCheckSeconds = 300;

    public IReadOnlyList<AccountSettings> Accounts { get; init; } = [];
    public IReadOnlyList<MonitoredRepository> Repositories { get; init; } = [];
    public NotificationRules Notifications { get; init; } = new();
    public NotificationStyle NotificationStyle { get; init; } = NotificationStyle.Custom;
    /// <summary>How often each repository is asked "anything new?". Unchanged repositories cost no API quota.</summary>
    public int LiveCheckSeconds { get; init; } = 10;
    /// <summary>Last window placement; null on first start, when the window opens maximised.</summary>
    public WindowPlacement? MainWindow { get; init; }

    /// <summary>
    /// Repositories (owner/name) the list pages are limited to. Empty means "All repositories", which is the default.
    /// </summary>
    public IReadOnlyList<string> SelectedRepositories { get; init; } = [];
    public bool ReduceMotion { get; init; }
    public AppTheme Theme { get; init; } = AppTheme.Dark;

    /// <summary>Notification types that stay on screen until closed. Every other type disappears by itself.</summary>
    public IReadOnlyList<ToastTone> StickyTones { get; init; } = [];

    public bool AutoCloses(ToastTone tone) => !StickyTones.Contains(tone);

    /// <summary>With the dynamic island on: also show the ordinary corner toast, not only the island.</summary>
    public bool ShowToastWithIsland { get; init; } = true;

    /// <summary>Sidebar stays open (true) or collapses to icons and opens while the mouse is over it (false).</summary>
    public bool SidebarPinned { get; init; } = true;

    public bool ShowFloatingWidget { get; init; }

    /// <summary>Locked: the widget ignores the mouse (clicks go to whatever is underneath). Unlocked: it can be dragged.</summary>
    public bool WidgetLocked { get; init; } = true;

    /// <summary>The counter widget is a small circle that expands on hover. Off: it stays as the full card.</summary>
    public bool MiniWidget { get; init; } = true;

    /// <summary>Where the expanded (full) widget sits, as 0–100 % of the free room on the main screen, so it is always on screen.</summary>
    public double WidgetXPercent { get; init; } = 98;
    public double WidgetYPercent { get; init; } = 8;

    /// <summary>
    /// Where the mini circle sits inside the expanded widget, as 0–100 % of the room in it (0/0 = top-left corner,
    /// 100/50 = middle of the right edge). The card grows from the circle to the full widget.
    /// </summary>
    public double MiniXPercent { get; init; } = 100;
    public double MiniYPercent { get; init; } = 50;
    public int? TimerWidgetX { get; init; }
    public int? TimerWidgetY { get; init; }
    public bool ShowTimerWidget { get; init; }
    /// <summary>Show every running timer on the widget and the island (off: only the most relevant one).</summary>
    public bool ShowMultipleTimers { get; init; } = true;
    /// <summary>A dynamic-island style pill at the top of the screen that also shows the notifications.</summary>
    public bool ShowDynamicIsland { get; init; }

    /// <summary>How see-through the overlays are while the cursor is not over them (hovering makes them solid).</summary>
    public double WidgetOpacity { get; init; } = 0.65;
    public double IslandOpacity { get; init; } = 0.9;

    /// <summary>Glass gloss, glowing rim and sheen on the island and the widgets.</summary>
    public bool GlassEffect { get; init; } = true;

    /// <summary>Overlays let mouse clicks fall through to the windows underneath them.</summary>
    public bool ClickThroughOverlays { get; init; } = true;
    /// <summary>Centre (X) and top (Y) of the island in screen pixels; null = top centre of the main screen.</summary>
    public int? IslandX { get; init; }
    public int? IslandY { get; init; }
    public IReadOnlyList<AiToolSettings> AiTools { get; init; } = [];
    public bool ShowTrayIcon { get; init; } = true;
    /// <summary>Minimising the window sends it to the tray instead of the taskbar (needs the tray icon).</summary>
    public bool HideToTrayOnMinimize { get; init; }
    /// <summary>Closing the window keeps the app running in the tray (needs the tray icon); off quits the app.</summary>
    public bool HideToTrayOnClose { get; init; } = true;
    public TimeFormat TimeFormat { get; init; } = TimeFormat.System;
    public string OAuthClientId { get; init; } = string.Empty;
    public PomodoroSettings Pomodoro { get; init; } = new();

    /// <summary>Wall-clock alarms set on the Timers page.</summary>
    public IReadOnlyList<AlarmEntry> Alarms { get; init; } = [];

    /// <summary>Master switch for every sound (timers, alarms and notifications).</summary>
    public bool SoundEnabled { get; init; } = true;

    /// <summary>One of <see cref="SoundCatalog.BuiltIn"/>, or <see cref="SoundCatalog.CustomId"/> to use <see cref="CustomSoundPath"/>.</summary>
    public string SoundId { get; init; } = SoundCatalog.DefaultId;

    /// <summary>The user's own tone (wav, mp3, aiff, m4a). Never copied; if the file disappears the built-in sound is used.</summary>
    public string? CustomSoundPath { get; init; }

    /// <summary>Also play the sound for ordinary notifications, not only for timers and alarms.</summary>
    public bool SoundOnNotifications { get; init; } = true;

    public TimeSpan LiveCheckInterval =>
        TimeSpan.FromSeconds(Math.Clamp(LiveCheckSeconds, MinLiveCheckSeconds, MaxLiveCheckSeconds));
}
