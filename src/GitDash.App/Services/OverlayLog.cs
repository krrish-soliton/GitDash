using System.Text;

namespace GitDash.App.Services;

/// <summary>
/// A small text log of where the island and the widgets are and how macOS treats them, written to
/// overlay-diagnostics.log in the app data folder. It exists to find out why an overlay moves or stays hidden on a
/// computer the developer cannot see; it holds no personal data and is capped in size.
/// </summary>
internal static class OverlayLog
{
    private const long MaxBytes = 256 * 1024;
    private static readonly object Gate = new();
    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
        "GitDash", "overlay-diagnostics.log");
    private static bool _started;

    public static string FilePath => Path;

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                if (!_started)
                {
                    _started = true;
                    File.WriteAllText(Path, $"GitDash overlay log, started {DateTime.Now:O}{Environment.NewLine}", Encoding.UTF8);
                }

                if (new FileInfo(Path).Length > MaxBytes) return;
                File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Diagnostics must never disturb the app.
        }
    }
}
