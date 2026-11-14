using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using GitDash.Application.Abstractions;

namespace GitDash.Infrastructure.Security;

/// <summary>
/// Checks <c>gh auth status</c> and runs <c>gh auth login --web</c>. The CLI prints a one-time code and opens the
/// browser itself; GitDash picks the code out of its output so it can show it, and never sees the token it creates.
/// </summary>
public sealed partial class GitHubCliSetup : IGitHubCliSetup
{
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(15);
    public const string VerificationUrl = "https://github.com/login/device";

    [GeneratedRegex(@"\b([A-Z0-9]{4}-[A-Z0-9]{4})\b")]
    private static partial Regex CodePattern();

    /// <summary>The one-time code (like <c>ABCD-1234</c>) in a line of the CLI's output, or null.</summary>
    public static string? ParseOneTimeCode(string? line) =>
        line is null ? null : CodePattern().Match(line) is { Success: true } m ? m.Groups[1].Value : null;

    public async Task<GitHubCliState> GetStateAsync(CancellationToken ct = default)
    {
        foreach (var program in GitHubCliTokenSource.Candidates())
        {
            if (Path.IsPathRooted(program) && !File.Exists(program)) continue;
            try
            {
                using var process = Start(program, "auth", "status", "--hostname", "github.com");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(StatusTimeout);
                _ = process.StandardOutput.ReadToEndAsync(timeout.Token);
                _ = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                return process.ExitCode == 0 ? GitHubCliState.SignedIn : GitHubCliState.NotSignedIn;
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

        return GitHubCliState.NotInstalled;
    }

    public async Task LoginAsync(Action<DeviceCodePrompt> onCode, CancellationToken ct = default)
    {
        Process? process = null;
        foreach (var program in GitHubCliTokenSource.Candidates())
        {
            if (Path.IsPathRooted(program) && !File.Exists(program)) continue;
            try
            {
                process = Start(program, "auth", "login", "--hostname", "github.com", "--git-protocol", "https", "--web", "--skip-ssh-key");
                break;
            }
            catch (Win32Exception)
            {
                // Try the next location.
            }
        }

        if (process is null) throw new Application.Accounts.GitHubCliNotInstalledException();

        using (process)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(LoginTimeout);
            // Nobody can type into it: closing stdin makes any unexpected question fail instead of hang.
            process.StandardInput.Close();
            var announced = 0;
            void Look(string? line)
            {
                if (ParseOneTimeCode(line) is not { } code || Interlocked.Exchange(ref announced, 1) == 1) return;
                onCode(new DeviceCodePrompt(string.Empty, code, VerificationUrl, TimeSpan.FromSeconds(5), LoginTimeout));
            }

            process.OutputDataReceived += (_, e) => Look(e.Data);
            process.ErrorDataReceived += (_, e) => Look(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                if (ct.IsCancellationRequested) throw;
                throw new InvalidOperationException("The GitHub login took too long and was stopped. Try again.");
            }

            if (process.ExitCode != 0)
                throw new InvalidOperationException("GitHub CLI did not finish signing in. Try again, or run \"gh auth login\" in a terminal.");
        }
    }

    private static Process Start(string program, params string[] arguments)
    {
        var info = new ProcessStartInfo(program)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new Win32Exception("The process did not start.");
    }
}
