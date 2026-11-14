using GitDash.Application.Updates;

namespace GitDash.Application.Abstractions;

/// <summary>Where new versions are announced (the GitHub releases page).</summary>
public interface IReleaseSource
{
    /// <summary>The newest published (non-draft, non-pre-release) release, or null when there is none or it cannot be read.</summary>
    Task<ReleaseInfo?> GetLatestAsync(string? token, CancellationToken ct = default);
}

/// <summary>Knows how this operating system / install type applies an update.</summary>
public interface IUpdateInstaller
{
    /// <summary>False for portable, development and read-only installs: the user is told, but nothing is replaced.</summary>
    bool CanInstall { get; }

    /// <summary>Why <see cref="CanInstall"/> is false, in words for the user.</summary>
    string? CannotInstallReason { get; }

    /// <summary>The release file that fits this computer, or null.</summary>
    ReleaseAsset? Pick(ReleaseInfo release);

    /// <summary>Downloads the file to a temporary location and returns its path. Reports progress from 0 to 1.</summary>
    Task<string> DownloadAsync(ReleaseAsset asset, string? token, IProgress<double> progress, CancellationToken ct = default);

    /// <summary>
    /// Starts the installer detached from this process; it waits for the app to exit, replaces it and starts it again.
    /// The caller exits the app right afterwards.
    /// </summary>
    void StartInstall(string file);
}
