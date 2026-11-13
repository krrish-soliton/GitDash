using System.Diagnostics;
using System.Text;
using GithubDash.Application.Abstractions;

namespace GithubDash.Infrastructure.Notifications;

/// <summary>
/// Shows a notification through the operating system's notification centre.
/// Titles and messages come from GitHub (untrusted), so they are never spliced into a command line or script:
/// Windows receives them through environment variables, macOS through argv.
/// </summary>
public sealed class NativeNotifier : INativeNotifier
{
    private const int MaxLength = 200;

    // Runs in Windows PowerShell 5.1, which can load the WinRT toast API without packaging the app.
    // The well-known PowerShell AppUserModelID is used because unpackaged apps have no ID of their own.
    private const string WindowsScript = """
        $ErrorActionPreference = 'Stop'
        [void][Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime]
        [void][Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime]
        $title = [System.Security.SecurityElement]::Escape($env:GITHUBDASH_TITLE)
        $body = [System.Security.SecurityElement]::Escape($env:GITHUBDASH_BODY)
        $xml = New-Object Windows.Data.Xml.Dom.XmlDocument
        $xml.LoadXml("<toast><visual><binding template='ToastGeneric'><text>$title</text><text>$body</text></binding></visual></toast>")
        $toast = New-Object Windows.UI.Notifications.ToastNotification $xml
        $appId = '{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe'
        [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($appId).Show($toast)
        """;

    private const string MacScript =
        "on run argv\n display notification (item 2 of argv) with title (item 1 of argv)\nend run";

    public async Task ShowAsync(string title, string message, CancellationToken ct = default)
    {
        title = Clean(title);
        message = Clean(message);

        ProcessStartInfo info;
        if (OperatingSystem.IsWindows())
        {
            // Absolute path so a rogue powershell.exe earlier on PATH cannot be picked up.
            var exe = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            info = new ProcessStartInfo(exe)
            {
                ArgumentList =
                {
                    "-NoProfile", "-NonInteractive", "-NoLogo", "-WindowStyle", "Hidden",
                    "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(WindowsScript)),
                },
            };
            info.Environment["GITHUBDASH_TITLE"] = title;
            info.Environment["GITHUBDASH_BODY"] = message;
        }
        else if (OperatingSystem.IsMacOS())
        {
            info = new ProcessStartInfo("/usr/bin/osascript") { ArgumentList = { "-e", MacScript, title, message } };
        }
        else
        {
            info = new ProcessStartInfo("notify-send") { ArgumentList = { "--app-name=GithubDash", "--", title, message } };
        }

        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardError = true;
        info.RedirectStandardOutput = true;

        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start the notifier.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"The system notifier exited with code {process.ExitCode}.");
        }
    }

    private static string Clean(string text)
    {
        var printable = new string([.. text.Where(c => !char.IsControl(c))]);
        return printable.Length <= MaxLength ? printable : printable[..MaxLength] + "…";
    }
}
