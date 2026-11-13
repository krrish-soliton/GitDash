using Avalonia;

namespace GitDash.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // One instance per user: a second launch would double the API traffic and the notifications.
        using var single = new Mutex(initiallyOwned: true, "GitDash.SingleInstance", out var isFirst);
        if (!isFirst) return 0;

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the Avalonia previewer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
