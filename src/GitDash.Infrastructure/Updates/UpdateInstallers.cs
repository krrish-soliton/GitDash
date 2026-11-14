using System.Diagnostics;
using System.Runtime.InteropServices;
using GitDash.Application.Abstractions;
using GitDash.Application.Updates;

namespace GitDash.Infrastructure.Updates;

/// <summary>Development builds, portable copies and unsupported systems: updates are announced, never applied.</summary>
public sealed class NoUpdateInstaller(string reason) : IUpdateInstaller
{
    public bool CanInstall => false;
    public string? CannotInstallReason => reason;
    public ReleaseAsset? Pick(ReleaseInfo release) => null;

    public Task<string> DownloadAsync(ReleaseAsset asset, string? token, IProgress<double> progress, CancellationToken ct = default) =>
        throw new NotSupportedException(reason);

    public void StartInstall(string file) => throw new NotSupportedException(reason);
}

/// <summary>Runs the Inno Setup installer silently over the existing installation, then starts the app again.</summary>
public sealed class WindowsUpdateInstaller : IUpdateInstaller
{
    public bool CanInstall => true;
    public string? CannotInstallReason => null;

    public ReleaseAsset? Pick(ReleaseInfo release) => release.Assets.FirstOrDefault(a =>
        a.Name.StartsWith("GitDash-Setup-", StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

    public Task<string> DownloadAsync(ReleaseAsset asset, string? token, IProgress<double> progress, CancellationToken ct = default) =>
        ReleaseDownloader.DownloadAsync(asset, token, progress, ct);

    public void StartInstall(string file)
    {
        // The installer closes the running app itself, replaces the files and (silent mode) launches the new version.
        Process.Start(new ProcessStartInfo(file, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS")
        {
            UseShellExecute = true,
        });
    }
}

/// <summary>Replaces GitDash.app from the downloaded disk image once the app has quit, then opens it again.</summary>
public sealed class MacUpdateInstaller(string bundlePath) : IUpdateInstaller
{
    public bool CanInstall => true;
    public string? CannotInstallReason => null;

    public ReleaseAsset? Pick(ReleaseInfo release)
    {
        var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
        return release.Assets.FirstOrDefault(a =>
            a.Name.EndsWith($"-{arch}.dmg", StringComparison.OrdinalIgnoreCase));
    }

    public Task<string> DownloadAsync(ReleaseAsset asset, string? token, IProgress<double> progress, CancellationToken ct = default) =>
        ReleaseDownloader.DownloadAsync(asset, token, progress, ct);

    public void StartInstall(string file)
    {
        var script = Path.Combine(Path.GetTempPath(), "GitDash-update", "install.sh");
        File.WriteAllText(script, Script);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        // nohup + background so the helper keeps running after this process quits.
        var start = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("nohup /bin/bash \"$0\" \"$1\" \"$2\" \"$3\" >/dev/null 2>&1 &");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        start.ArgumentList.Add(file);
        start.ArgumentList.Add(bundlePath);
        Process.Start(start)?.Dispose();
    }

    private const string Script = """
        #!/bin/bash
        # $1 = pid of GitDash, $2 = downloaded .dmg, $3 = path of GitDash.app
        PID="$1"; DMG="$2"; APP="$3"
        while kill -0 "$PID" 2>/dev/null; do sleep 0.5; done
        MNT="$(mktemp -d /tmp/gitdash-update.XXXXXX)"
        if hdiutil attach "$DMG" -nobrowse -readonly -mountpoint "$MNT" -quiet; then
          if [ -d "$MNT/GitDash.app" ]; then
            rm -rf "$APP.new" "$APP.old"
            if ditto "$MNT/GitDash.app" "$APP.new"; then
              if mv "$APP" "$APP.old"; then
                if mv "$APP.new" "$APP"; then rm -rf "$APP.old"; else mv "$APP.old" "$APP"; fi
              fi
            fi
            rm -rf "$APP.new"
            xattr -dr com.apple.quarantine "$APP" 2>/dev/null
          fi
          hdiutil detach "$MNT" -quiet -force
        fi
        rm -f "$DMG"
        open "$APP"
        """;
}

public static class UpdateInstallerFactory
{
    public static IUpdateInstaller Create()
    {
        if (OperatingSystem.IsWindows())
        {
            // The Inno Setup uninstaller sits beside the app only for installed copies; a portable zip has none.
            return File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"))
                ? new WindowsUpdateInstaller()
                : new NoUpdateInstaller("This is a portable copy of GitDash: download the installer from the release page.");
        }

        if (OperatingSystem.IsMacOS())
        {
            var bundle = BundlePath(AppContext.BaseDirectory);
            if (bundle is null) return new NoUpdateInstaller("GitDash is not running from an app bundle.");
            var parent = Path.GetDirectoryName(bundle)!;
            return CanWrite(parent)
                ? new MacUpdateInstaller(bundle)
                : new NoUpdateInstaller("GitDash cannot replace itself in this folder: drag the new version into Applications.");
        }

        return new NoUpdateInstaller("Automatic updates are available on Windows and macOS.");
    }

    /// <summary>"/Applications/GitDash.app/Contents/MacOS/" → "/Applications/GitDash.app".</summary>
    public static string? BundlePath(string baseDirectory)
    {
        var i = baseDirectory.IndexOf(".app/Contents/MacOS", StringComparison.Ordinal);
        return i < 0 ? null : baseDirectory[..(i + 4)];
    }

    private static bool CanWrite(string folder)
    {
        try
        {
            var probe = Path.Combine(folder, $".gitdash-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>The version this build was published as (the tag, stamped in by the release workflow).</summary>
public static class RunningVersion
{
    public static Domain.Updates.AppVersion Read()
    {
        var assembly = System.Reflection.Assembly.GetEntryAssembly() ?? typeof(RunningVersion).Assembly;
        var info = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
        if (Domain.Updates.AppVersion.TryParse(info, out var v)) return v;
        return assembly.GetName().Version is { } n ? new Domain.Updates.AppVersion(n.Major, n.Minor, Math.Max(0, n.Build)) : default;
    }
}
