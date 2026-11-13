using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using GitDash.Application.Settings;
using GitDash.Presentation.ViewModels;

namespace GitDash.App.Services;

/// <summary>
/// The OS-native counter: a notification-area icon on Windows and a menu-bar item on macOS,
/// with the three numbers in its tooltip and menu.
/// </summary>
public sealed class TrayController(
    WidgetViewModel counts, SettingsService settings, Action showMainWindow, Action quit) : IDisposable
{
    private TrayIcon? _icon;
    private NativeMenuItem? _assigned, _approval, _total;
    private NativeMenuItem? _move, _widget, _island, _mini;
    private int _iconCount = -1;

    public void Start()
    {
        _assigned = new NativeMenuItem { IsEnabled = false };
        _approval = new NativeMenuItem { IsEnabled = false };
        _total = new NativeMenuItem { IsEnabled = false };
        var open = new NativeMenuItem("Open GitDash");
        open.Click += (_, _) => showMainWindow();
        var exit = new NativeMenuItem("Quit");
        exit.Click += (_, _) => quit();

        _move = Toggle("Move widgets and island", s => s with { WidgetLocked = !s.WidgetLocked });
        _widget = Toggle("Show floating widget", s => s with { ShowFloatingWidget = !s.ShowFloatingWidget });
        _island = Toggle("Show dynamic island", s => s with { ShowDynamicIsland = !s.ShowDynamicIsland });
        _mini = Toggle("Mini widget", s => s with { MiniWidget = !s.MiniWidget });

        var menu = new NativeMenu();
        menu.Items.Add(_assigned);
        menu.Items.Add(_approval);
        menu.Items.Add(_total);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_widget);
        menu.Items.Add(_mini);
        menu.Items.Add(_island);
        menu.Items.Add(_move);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(open);
        menu.Items.Add(exit);

        _icon = new TrayIcon
        {
            Icon = IconFor(counts.Assigned),
            Menu = menu,
            IsVisible = settings.Current.ShowTrayIcon,
        };
        _icon.Clicked += (_, _) => showMainWindow();

        if (AvaloniaApplication.Current is { } app)
        {
            TrayIcon.SetIcons(app, new TrayIcons { _icon });
        }

        counts.PropertyChanged += OnCountsChanged;
        settings.Changed += OnSettingsChanged;
        Render();
        SyncToggles(settings.Current);
    }

    /// <summary>A check-mark menu item that flips one setting; the check itself follows the saved setting.</summary>
    private NativeMenuItem Toggle(string header, Func<AppSettings, AppSettings> flip)
    {
        var item = new NativeMenuItem(header) { ToggleType = NativeMenuItemToggleType.CheckBox };
        item.Click += (_, _) => _ = settings.UpdateAsync(flip);
        return item;
    }

    private void SyncToggles(AppSettings s)
    {
        if (_move is null) return;
        _move.IsChecked = !s.WidgetLocked;
        _widget!.IsChecked = s.ShowFloatingWidget;
        _island!.IsChecked = s.ShowDynamicIsland;
        _mini!.IsChecked = s.MiniWidget;
    }

    private void OnCountsChanged(object? sender, PropertyChangedEventArgs e) => Render();

    private void OnSettingsChanged(AppSettings s) => Dispatcher.UIThread.Post(() =>
    {
        if (_icon is not null) _icon.IsVisible = s.ShowTrayIcon;
        SyncToggles(s);
    });

    /// <summary>The app icon, with a red disc showing the number of pull requests assigned to me once there is one.</summary>
    private WindowIcon IconFor(int assigned)
    {
        _iconCount = assigned;
        using var stream = AssetLoader.Open(new Uri("avares://GitDash/Assets/app.ico"));
        if (assigned <= 0) return new WindowIcon(stream);
        try
        {
            using var baseIcon = new Bitmap(stream);
            const int size = 32;
            var target = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
            using (var ctx = target.CreateDrawingContext())
            {
                ctx.DrawImage(baseIcon, new Rect(baseIcon.Size), new Rect(0, 0, size, size));
                var label = Math.Min(assigned, 99).ToString(CultureInfo.InvariantCulture);
                const double radius = 11;
                var centre = new Point(size - radius, size - radius);
                ctx.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xE0, 0x30, 0x3B)), new Pen(Brushes.White, 1.5), centre, radius, radius);
                var text = new FormattedText(
                    label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold),
                    label.Length == 1 ? 15 : 12, Brushes.White);
                ctx.DrawText(text, new Point(centre.X - text.Width / 2, centre.Y - text.Height / 2));
            }

            return new WindowIcon(target);
        }
        catch (Exception)
        {
            // A drawing problem must never take the tray icon away: fall back to the plain icon.
            using var plain = AssetLoader.Open(new Uri("avares://GitDash/Assets/app.ico"));
            return new WindowIcon(plain);
        }
    }

    private void Render()
    {
        if (_icon is null) return;
        if (counts.Assigned != _iconCount) _icon.Icon = IconFor(counts.Assigned);
        _assigned!.Header = $"Assigned to me: {counts.Assigned}";
        _approval!.Header = $"Needs my approval: {counts.Approval}";
        _total!.Header = $"Open pull requests: {counts.Total}";
        _icon.ToolTipText =
            $"GitDash\nAssigned to me: {counts.Assigned}\nNeeds my approval: {counts.Approval}\nOpen: {counts.Total}";
    }

    public void Dispose()
    {
        counts.PropertyChanged -= OnCountsChanged;
        settings.Changed -= OnSettingsChanged;
        if (_icon is not null)
        {
            _icon.IsVisible = false;
            _icon.Dispose();
            _icon = null;
        }
    }
}
