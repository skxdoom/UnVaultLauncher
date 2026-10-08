using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using UnVault.Core.Epic;

namespace UnVault.Core.Tests;

/// <summary>Epic's API as a busy or failing service: what's asked again, how long it waits, and when it stops.</summary>
public sealed class EpicAPIClientTests : IDisposable
{
    private readonly string _sessionPath = Path.Combine(Path.GetTempPath(), $"unvault-session-{Guid.NewGuid():N}.dat");

    public void Dispose() => File.Delete(_sessionPath);

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData((HttpStatusCode)0)] // the connection failed
    public async Task Asks_again_when_Epic_is_busy_or_failing(HttpStatusCode trouble)
    {
        var epic = new FakeEpic((_, call) => call >= 2 ? JSON("[]")
            : trouble == 0 ? throw new HttpRequestException("The connection was reset.")
            : new HttpResponseMessage(trouble));

        var assets = await Client(epic).GetAssetsAsync();

        Assert.Empty(assets);
        Assert.Equal(3, epic.Calls);
    }

    [Fact]
    public async Task Waits_as_long_as_Epic_asks()
    {
        var epic = new FakeEpic((_, call) => call == 0 ? Busy(TimeSpan.FromSeconds(1)) : JSON("[]"));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await Client(epic).GetAssetsAsync();

        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(0.9), $"waited {clock.Elapsed}");
        Assert.Equal(2, epic.Calls);
    }

    [Fact]
    public async Task Fails_rather_than_wait_as_long_as_Epic_asks_when_that_is_very_long()
    {
        var epic = new FakeEpic((_, _) => Busy(TimeSpan.FromHours(1)));

        var error = await Assert.ThrowsAsync<EpicAPIException>(() => Client(epic).GetAssetsAsync());

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Equal(1, epic.Calls);
    }

    [Fact]
    public async Task Reports_a_service_that_keeps_failing()
    {
        var epic = new FakeEpic((_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var error = await Assert.ThrowsAsync<EpicAPIException>(() => Client(epic).GetAssetsAsync());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        Assert.Equal(4, epic.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)] // only Fab's edge gets another try on this
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Doesnt_ask_again_when_another_try_wont_help(HttpStatusCode refusal)
    {
        var epic = new FakeEpic((_, _) => new HttpResponseMessage(refusal));

        await Assert.ThrowsAsync<EpicAPIException>(() => Client(epic).GetAssetsAsync());

        Assert.Equal(1, epic.Calls);
    }

    [Fact]
    public async Task Reads_the_Fab_library_page_by_page_and_rides_out_its_stray_403s()
    {
        var epic = new FakeEpic((request, call) => call switch
        {
            0 => new HttpResponseMessage(HttpStatusCode.Forbidden), // Fab's edge, now and then, for a valid request
            1 => JSON("""{ "cursors": { "next": "page-2" }, "results": [] }"""),
            _ => JSON("""{ "cursors": { "next": null }, "results": [] }"""),
        });

        await Client(epic).GetFabLibraryAsync();

        Assert.Equal(3, epic.Calls);
        Assert.Contains("cursor=page-2", epic.Requests[2]);
    }

    [Fact]
    public async Task Stops_a_Fab_library_listing_that_goes_in_circles()
    {
        string[] next = ["page-2", "page-3", "page-2"];
        var epic = new FakeEpic((_, call) => JSON($$"""{ "cursors": { "next": "{{next[Math.Min(call, 2)]}}" }, "results": [] }"""));

        var error = await Assert.ThrowsAsync<EpicAPIException>(() => Client(epic).GetFabLibraryAsync());

        Assert.Contains("circles", error.Message);
        Assert.Equal(3, epic.Calls);
    }

    [Fact]
    public async Task A_manifest_server_that_doesnt_answer_hands_over_to_the_next()
    {
        string app = "TestApp" + Guid.NewGuid().ToString("N")[..8]; // not in the manifest cache yet
        byte[] manifest = Encoding.UTF8.GetBytes(JSONManifestBuilder.Build(app, "1.0", ("Engine/Readme.txt", 10)));
        var epic = new FakeEpic((request, _) => request.RequestUri!.Host == "one.test" ? null : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(manifest) });
        var http = new HttpClient(epic) { Timeout = TimeSpan.FromMilliseconds(300) };
        var build = new EpicBuildInfo
        {
            AppName = app,
            BuildVersion = "1.0",
            Hash = Convert.ToHexString(SHA1.HashData(manifest)),
            Manifests = [new EpicManifestLocation { URI = "https://one.test/CloudDir/build.manifest" }, new EpicManifestLocation { URI = "https://two.test/CloudDir/build.manifest" }],
        };

        var downloaded = await Client(epic, http).DownloadManifestAsync(build);

        Assert.Equal(["Engine/Readme.txt"], downloaded.Manifest.Files.Select(f => f.Filename));
        Assert.Equal(["one.test", "two.test"], epic.Requests.Select(r => new Uri(r).Host));
    }

    /// <summary>A client signed in with a session good for hours, so no token refresh goes out.</summary>
    private EpicAPIClient Client(FakeEpic epic, HttpClient? http = null)
    {
        var store = new SessionStore(_sessionPath);
        store.Save(new EpicAuthSession
        {
            AccessToken = "eg1~access",
            AccessExpiresAt = DateTimeOffset.UtcNow.AddHours(2),
            RefreshToken = "eg1~refresh",
            RefreshExpiresAt = DateTimeOffset.UtcNow.AddDays(20),
            AccountID = "account",
        });
        http ??= new HttpClient(epic);
        return new EpicAPIClient(http, new EpicAccount(new EpicAuthClient(http), store)) { RetryBaseDelay = TimeSpan.FromMilliseconds(1) };
    }

    private static HttpResponseMessage JSON(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Busy(TimeSpan retryAfter)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);
        return response;
    }

    /// <summary>Answers each request from <paramref name="respond"/> (given the request and how many came before it); null never answers.</summary>
    private sealed class FakeEpic(Func<HttpRequestMessage, int, HttpResponseMessage?> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public int Calls => Requests.Count;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int call;
            lock (Requests)
            {
                call = Requests.Count;
                Requests.Add(request.RequestUri!.ToString());
            }
            if (respond(request, call) is { } response)
                return response;
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}
