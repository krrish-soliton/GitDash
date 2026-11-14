using System.Net;
using GitDash.Application.Updates;
using GitDash.Infrastructure.Updates;

namespace GitDash.Tests;

public class UpdateAccessTests
{
    private sealed class Stub(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
    }

    private static GitHubReleaseSource Source(HttpStatusCode status) =>
        new(new HttpClient(new Stub(status)), "o", "r");

    [Fact]
    public async Task Request_limit_without_a_sign_in_is_reported_not_called_up_to_date()
    {
        string? message = null;
        try { await Source(HttpStatusCode.Forbidden).GetLatestAsync(null); }
        catch (ReleaseAccessException ex) { message = ex.Message; }

        Assert.NotNull(message);
        Assert.True(message!.Contains("sign", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Too_many_requests_is_reported_too()
    {
        await Assert.ThrowsAsync<ReleaseAccessException>(() => Source(HttpStatusCode.TooManyRequests).GetLatestAsync(null));
    }

    [Fact]
    public async Task Refused_token_is_reported()
    {
        await Assert.ThrowsAsync<ReleaseAccessException>(() => Source(HttpStatusCode.Forbidden).GetLatestAsync("token"));
    }

    [Fact]
    public async Task No_release_yet_is_simply_nothing_new()
    {
        Assert.Null(await Source(HttpStatusCode.NotFound).GetLatestAsync(null));
        Assert.Null(await Source(HttpStatusCode.NotFound).GetLatestAsync("token"));
    }
}
