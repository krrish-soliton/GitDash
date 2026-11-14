namespace GitDash.Application.Accounts;

/// <summary>The GitHub CLI (gh) is not on this computer; the message says how to get it.</summary>
public sealed class GitHubCliNotInstalledException() : InvalidOperationException(
    "GitHub CLI (gh) is not installed. Install it (the Windows installer can do it for you; on a Mac use \"brew install gh\" " +
    "or cli.github.com), then click the button again. GitDash will start the GitHub login for you.");
