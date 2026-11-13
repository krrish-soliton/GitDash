using System.Diagnostics;
using System.Runtime.InteropServices;
using GithubDash.Application.Abstractions;

namespace GithubDash.Infrastructure.Notifications;

/// <summary>Plays a short built-in system sound. Best effort: silence is an acceptable failure.</summary>
public sealed class SystemAlarm : IAlarm
{
    private const uint AsteriskSound = 0x40;

    public void Play()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                _ = MessageBeep(AsteriskSound);
            }
            else if (OperatingSystem.IsMacOS())
            {
                using var _ = Process.Start(new ProcessStartInfo("/usr/bin/afplay")
                {
                    ArgumentList = { "/System/Library/Sounds/Glass.aiff" },
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
            }
        }
        catch (Exception)
        {
            // No sound device, or the sound file is missing.
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MessageBeep(uint type);
}
