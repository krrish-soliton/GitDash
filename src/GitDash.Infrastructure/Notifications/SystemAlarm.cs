using System.Diagnostics;
using System.Runtime.InteropServices;
using GitDash.Application.Abstractions;
using GitDash.Application.Settings;

namespace GitDash.Infrastructure.Notifications;

/// <summary>
/// Plays the chosen notification sound: a built-in one (synthesised once into the temp folder) or the user's own file.
/// Best effort: silence is an acceptable failure.
/// </summary>
public sealed class SystemAlarm(SettingsService settings) : IAlarm
{
    private const string MciAlias = "gitdashsound";
    private static readonly object Gate = new();

    public void Play()
    {
        var s = settings.Current;
        if (s.SoundEnabled) Preview(s.SoundId, s.CustomSoundPath);
    }

    public void Preview(string soundId, string? customPath)
    {
        try
        {
            var file = ResolveFile(soundId, customPath);
            if (file is not null) PlayFile(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException
                                       or EntryPointNotFoundException or System.ComponentModel.Win32Exception)
        {
            // No sound device, or the sound file is missing.
        }
    }

    /// <summary>The user's file when chosen and still there, otherwise the built-in sound as a file.</summary>
    internal static string? ResolveFile(string soundId, string? customPath)
    {
        if (soundId == SoundCatalog.CustomId && !string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath))
            return customPath;

        var id = soundId == SoundCatalog.CustomId ? SoundCatalog.DefaultId : SoundCatalog.Normalise(soundId);
        var folder = Path.Combine(Path.GetTempPath(), "GitDash", "sounds");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, id + ".wav");
        if (!File.Exists(path)) File.WriteAllBytes(path, ToneSynth.Render(id));
        return path;
    }

    private static void PlayFile(string file)
    {
        if (OperatingSystem.IsWindows())
        {
            if (file.Contains('"')) return; // cannot be quoted for MCI
            lock (Gate)
            {
                _ = mciSendString($"close {MciAlias}", null, 0, 0);
                if (mciSendString($"open \"{file}\" alias {MciAlias}", null, 0, 0) == 0)
                    _ = mciSendString($"play {MciAlias}", null, 0, 0);
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            using var _ = Process.Start(new ProcessStartInfo("/usr/bin/afplay")
            {
                ArgumentList = { file },
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendString(string command, System.Text.StringBuilder? result, int length, nint callback);
}
