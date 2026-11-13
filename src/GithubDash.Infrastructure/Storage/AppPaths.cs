namespace GithubDash.Infrastructure.Storage;

/// <summary>Per-user data folder: %APPDATA%\GithubDash on Windows, ~/Library/Application Support/GithubDash on macOS.</summary>
public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        Root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
            "GithubDash");
        Directory.CreateDirectory(Root);
        Restrict(Root);
    }

    public string Root { get; }
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string TimerSessionsFile => Path.Combine(Root, "timer-sessions.json");
    public string DatabaseFile => Path.Combine(Root, "githubdash.db");
    public string SecretsFolder => Path.Combine(Root, "secrets");

    /// <summary>On Unix, make a file or folder readable by the current user only.</summary>
    public static void Restrict(string path)
    {
        if (OperatingSystem.IsWindows()) return; // %APPDATA% is already per-user via ACLs.
        var mode = Directory.Exists(path)
            ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            : UnixFileMode.UserRead | UnixFileMode.UserWrite;
        File.SetUnixFileMode(path, mode);
    }

    /// <summary>Write-then-rename so a crash can never leave a half-written file.</summary>
    public static async Task WriteAtomicAsync(string path, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        var temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, content, ct).ConfigureAwait(false);
        Restrict(temp);
        File.Move(temp, path, overwrite: true);
    }
}
