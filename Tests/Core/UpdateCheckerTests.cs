using System.Net;
using System.Text;

namespace UnVault.Core.Tests;

public class UpdateCheckerTests
{
    private const string Page = "https://github.com/skxdoom/UnVaultLauncher/releases/tag/v0.6.0";

    [Theory]
    [InlineData("v0.6.0", "0.5.0", "0.6.0")]
    [InlineData("v0.5.1", "0.5.0", "0.5.1")]
    [InlineData("v0.5.0", "0.5.0", null)] // this one
    [InlineData("v0.4.0", "0.5.0", null)] // a build newer than the latest release (a local one)
    [InlineData("nightly", "0.5.0", null)] // a tag that isn't a version says nothing
    public async Task Finds_a_newer_release(string latestTag, string current, string? expected)
    {
        var github = new FakeGitHub(_ => JSON($$"""{ "tag_name": "{{latestTag}}", "html_url": "{{Page}}", "draft": false, "prerelease": false }"""));

        var newer = await new UpdateChecker(new HttpClient(github)).FindNewerAsync(current);

        Assert.Equal(expected, newer?.Version);
        if (newer is not null)
            Assert.Equal(Page, newer.PageURL);
    }

    [Fact]
    public async Task Asks_GitHub_as_itself_not_as_the_Epic_Games_Launcher()
    {
        var github = new FakeGitHub(_ => JSON("""{ "tag_name": "v0.5.0" }"""));
        var http = new HttpClient(github);
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "UELauncher/0.0.0 EpicGamesLauncher/0.0.0"); // as the shared client has

        await new UpdateChecker(http).FindNewerAsync("0.5.0");

        var request = Assert.Single(github.Requests);
        Assert.Equal("https://api.github.com/repos/skxdoom/UnVaultLauncher/releases/latest", request.URL);
        Assert.Equal("UnVaultLauncher/0.5.0", request.UserAgent);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "{}")] // no release published yet
    [InlineData(HttpStatusCode.Forbidden, "{}")] // GitHub's limit for requests without an account
    [InlineData(HttpStatusCode.OK, "<html>not JSON</html>")]
    [InlineData(HttpStatusCode.OK, """{ "name": "no tag" }""")]
    public async Task A_failed_check_is_an_HTTP_error(HttpStatusCode status, string body)
    {
        var github = new FakeGitHub(_ => JSON(body, status));

        await Assert.ThrowsAsync<HttpRequestException>(() => new UpdateChecker(new HttpClient(github)).FindNewerAsync("0.5.0"));
    }

    private static HttpResponseMessage JSON(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class FakeGitHub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string URL, string UserAgent)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.ToString(), request.Headers.UserAgent.ToString()));
            return Task.FromResult(respond(request));
        }
    }
}
