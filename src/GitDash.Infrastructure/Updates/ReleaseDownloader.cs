using System.Net.Http.Headers;
using GitDash.Application.Abstractions;
using GitDash.Application.Updates;

namespace GitDash.Infrastructure.Updates;

/// <summary>
/// Downloads a release file into a private temporary folder. Public repositories use the plain download link; with a
/// token the API link is used so private releases work, and the token is never sent to the storage host it redirects to.
/// </summary>
internal static class ReleaseDownloader
{
    public static async Task<string> DownloadAsync(
        ReleaseAsset asset, string? token, IProgress<double> progress, CancellationToken ct)
    {
        var folder = Path.Combine(Path.GetTempPath(), "GitDash-update");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, Path.GetFileName(asset.Name));

        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GitDash", "updater"));

        var useApi = !string.IsNullOrWhiteSpace(token);
        var url = useApi ? asset.ApiUrl : asset.DownloadUrl;
        HttpResponseMessage? response = null;
        try
        {
            for (var hop = 0; hop < 5; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
                // Credentials only go to GitHub's own hosts, never to wherever a redirect points.
                if (useApi && GitHubReleaseSource.IsGitHubUrl(url))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                response?.Dispose();
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } next)
                {
                    var resolved = next.IsAbsoluteUri ? next : new Uri(new Uri(url), next);
                    if (resolved.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("The download redirected to an insecure address.");
                    url = resolved.ToString();
                    continue;
                }

                break;
            }

            response!.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? asset.Size;
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = File.Create(target);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (total > 0) progress.Report(Math.Min(1, (double)done / total));
            }

            return target;
        }
        catch
        {
            try { File.Delete(target); } catch (IOException) { }
            throw;
        }
        finally
        {
            response?.Dispose();
        }
    }
}
