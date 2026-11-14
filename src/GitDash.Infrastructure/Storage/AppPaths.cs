namespace GitDash.Infrastructure.Storage;

/// <summary>Per-user data folder: %APPDATA%\GitDash on Windows, ~/Library/Application Support/GitDash on macOS.</summary>
public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create);
        Root = root ?? Path.Combine(appData, "GitDash");
        if (root is null) MigrateLegacyData(Path.Combine(appData, LegacyName), Root);
        Directory.CreateDirectory(Root);
        Restrict(Root);
    }

    /// <summary>The application's former name; its data folder is moved to the new one on first start.</summary>
    internal const string LegacyName = "GithubDash";

    public string Root { get; }
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string NotificationsFile => Path.Combine(Root, "notifications.json");
    public string TimerSessionsFile => Path.Combine(Root, "timer-sessions.json");
    public string DatabaseFile => Path.Combine(Root, "gitdash.db");
    public string SecretsFolder => Path.Combine(Root, "secrets");

    /// <summary>Moves settings, tokens and the cache from the old folder (and the old database name) so nothing is lost by the rename.</summary>
    private static void MigrateLegacyData(string legacyRoot, string root)
    {
        try
        {
            if (!Directory.Exists(root) && Directory.Exists(legacyRoot)) Directory.Move(legacyRoot, root);
            if (!Directory.Exists(root)) return;
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var old = Path.Combine(root, LegacyName.ToLowerInvariant() + ".db" + suffix);
                var current = Path.Combine(root, "gitdash.db" + suffix);
                if (File.Exists(old) && !File.Exists(current)) File.Move(old, current);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Could not move it: start fresh rather than fail to start.
        }
    }

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
