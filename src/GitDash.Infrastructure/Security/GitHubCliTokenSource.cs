using System.ComponentModel;
using System.Diagnostics;
using GitDash.Application.Abstractions;

namespace GitDash.Infrastructure.Security;

/// <summary>
/// Reads the token of an existing GitHub CLI login by running <c>gh auth token</c>. Nothing is written back to the
/// CLI and the token is never logged. Apps started from the Start menu or Finder often have a short PATH, so the
/// usual install folders are tried as well.
/// </summary>
public sealed class GitHubCliTokenSource : IGitHubCliTokenSource
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public static IReadOnlyList<string> Candidates()
    {
        var list = new List<string> { OperatingSystem.IsWindows() ? "gh.exe" : "gh" };
        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[]
                     {
                         Environment.GetEnvironmentVariable("ProgramFiles"),
                         Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                         Environment.GetEnvironmentVariable("LOCALAPPDATA") is { } local ? Path.Combine(local, "Programs") : null,
                     })
            {
                if (!string.IsNullOrEmpty(root)) list.Add(Path.Combine(root, "GitHub CLI", "gh.exe"));
            }
        }
        else
        {
            list.AddRange(["/opt/homebrew/bin/gh", "/usr/local/bin/gh", "/usr/bin/gh", "/snap/bin/gh"]);
        }

        return list;
    }

    /// <summary>The first line of the CLI's output, if it looks like a token; otherwise null.</summary>
    public static string? ParseToken(string? output)
    {
        var line = output?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(line) || line.Length > 255 || line.Any(char.IsWhiteSpace)) return null;
        return line;
    }

    public async Task<string> GetTokenAsync(CancellationToken ct = default)
    {
        foreach (var program in Candidates())
        {
            if (Path.IsPathRooted(program) && !File.Exists(program)) continue;

            try
            {
                var info = new ProcessStartInfo(program)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    ArgumentList = { "auth", "token", "--hostname", "github.com" },
                };
                using var process = Process.Start(info) ?? throw new Win32Exception("The process did not start.");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(Timeout);
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                _ = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        "GitHub CLI is installed but not logged in to github.com. Run \"gh auth login\" in a terminal, then try again.");
                }

                return ParseToken(await output.ConfigureAwait(false))
                    ?? throw new InvalidOperationException("GitHub CLI did not return a token. Run \"gh auth login\" and try again.");
            }
            catch (Win32Exception)
            {
                // Not at this location: try the next one.
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new InvalidOperationException("GitHub CLI did not answer in time. Try again.");
            }
        }

        throw new InvalidOperationException(
            "GitHub CLI (gh) was not found. Install it from cli.github.com, run \"gh auth login\", then try again.");
    }
}
