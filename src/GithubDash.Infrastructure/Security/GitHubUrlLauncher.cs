using System.Diagnostics;
using GithubDash.Application.Abstractions;

namespace GithubDash.Infrastructure.Security;

/// <summary>
/// Opens links in the default browser. URLs originate from API responses, so they are validated first:
/// only absolute https links to github.com are ever handed to the shell.
/// </summary>
public sealed class GitHubUrlLauncher : IUrlLauncher
{
    public static bool IsAllowed(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.UserInfo.Length == 0 &&
        (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase));

    public bool Open(string url)
    {
        if (!IsAllowed(url)) return false;
        try
        {
            // AbsoluteUri is the normalised, escaped form — never the raw input string.
            var target = new Uri(url).AbsoluteUri;
            if (OperatingSystem.IsMacOS())
            {
                using var _ = Process.Start(new ProcessStartInfo("/usr/bin/open") { ArgumentList = { target } });
            }
            else if (OperatingSystem.IsWindows())
            {
                using var _ = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            else
            {
                using var _ = Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { target } });
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
