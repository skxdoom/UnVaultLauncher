using System.Net;
using System.Text;
using UnVault.Core.Epic;

namespace UnVault.Core.Tests;

/// <summary>Sessions Epic ends early, runs out, or another process refreshes: the account follows along and never resurrects one.</summary>
public sealed class EpicAccountTests : IDisposable
{
    private readonly string _sessionPath = Path.Combine(Path.GetTempPath(), $"unvault-session-{Guid.NewGuid():N}.dat");

    public void Dispose() => File.Delete(_sessionPath);

    [Fact]
    public async Task A_token_Epic_turned_down_early_is_renewed_and_the_request_sent_again()
    {
        var epic = new FakeEpic(request => request.RequestUri!.ToString() == EpicEndpoints.TokenURL
            ? Tokens("eg1~renewed")
            : request.Headers.Authorization?.Parameter == "eg1~renewed" ? JSON("[]") : new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var (account, api) = SignedIn(epic, Session("eg1~revoked", validFor: TimeSpan.FromHours(2)));

        Assert.Empty(await api.GetAssetsAsync());

        Assert.Equal("eg1~renewed", account.Session?.AccessToken);
        Assert.Equal("eg1~renewed", new SessionStore(_sessionPath).Load()?.AccessToken); // kept for next time
        Assert.Equal(3, epic.Requests.Count); // turned down, renewed, sent again
    }

    [Fact]
    public async Task A_session_Epic_ended_signs_out_here_too()
    {
        // As after a password change: the access token is turned down, and so is the refresh token.
        var epic = new FakeEpic(request => request.RequestUri!.ToString() == EpicEndpoints.TokenURL
            ? JSON("""{ "errorCode": "errors.com.epicgames.account.auth_token.invalid_refresh_token", "errorMessage": "Sorry the refresh token 'eg1~refresh' is invalid" }""", HttpStatusCode.BadRequest)
            : new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var (account, api) = SignedIn(epic, Session("eg1~revoked", validFor: TimeSpan.FromHours(2)));

        var error = await Assert.ThrowsAsync<NotLoggedInException>(() => api.GetAssetsAsync());

        Assert.Contains("Sign in again", error.Message);
        Assert.False(account.IsLoggedIn);
        Assert.False(File.Exists(_sessionPath));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_busy_or_failing_Epic_doesnt_end_the_session(HttpStatusCode trouble)
    {
        var epic = new FakeEpic(_ => new HttpResponseMessage(trouble));
        var (account, _) = SignedIn(epic, Session("eg1~access", validFor: TimeSpan.FromMinutes(1))); // due for a refresh

        var error = await Assert.ThrowsAsync<EpicAPIException>(() => account.GetValidSessionAsync());

        Assert.IsNotType<NotLoggedInException>(error);
        Assert.True(account.IsLoggedIn);
        Assert.True(File.Exists(_sessionPath));
    }

    [Fact]
    public async Task Expiry_counts_from_how_long_the_tokens_last_not_from_Epics_clock()
    {
        // Epic's expiry times compared with a clock that's a day off would make the tokens look long gone.
        var epic = new FakeEpic(_ => JSON("""
            { "access_token": "eg1~access", "expires_in": 7200, "expires_at": "2001-01-01T00:00:00Z",
              "refresh_token": "eg1~refresh", "refresh_expires": 28800, "refresh_expires_at": "2001-01-01T00:00:00Z",
              "account_id": "account", "displayName": "Tester" }
            """));
        var account = new EpicAccount(new EpicAuthClient(new HttpClient(epic)), new SessionStore(_sessionPath));

        var session = await account.LoginWithAuthorizationCodeAsync("0123456789abcdef0123456789abcdef");

        Assert.InRange(session.AccessExpiresAt - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(119), TimeSpan.FromMinutes(121));
        Assert.InRange(session.RefreshExpiresAt - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(479), TimeSpan.FromMinutes(481));
        Assert.True(account.IsLoggedIn);
    }

    [Fact]
    public async Task Uses_tokens_the_CLI_refreshed_meanwhile_instead_of_refreshing_again()
    {
        var epic = new FakeEpic(_ => Tokens("eg1~mine"));
        var (account, _) = SignedIn(epic, Session("eg1~old", validFor: TimeSpan.FromMinutes(1)));
        new SessionStore(_sessionPath).Save(Session("eg1~theirs", validFor: TimeSpan.FromHours(2))); // the other process refreshed

        var session = await account.GetValidSessionAsync();

        Assert.Equal("eg1~theirs", session.AccessToken);
        Assert.Empty(epic.Requests);
    }

    [Fact]
    public async Task A_refresh_finishing_after_sign_out_doesnt_sign_back_in()
    {
        var refreshing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var epic = new FakeEpic(async request =>
        {
            if (request.RequestUri!.ToString() != EpicEndpoints.TokenURL)
                return new HttpResponseMessage(HttpStatusCode.NoContent); // the sign-out
            refreshing.SetResult();
            await answer.Task;
            return Tokens("eg1~refreshed");
        });
        var (account, _) = SignedIn(epic, Session("eg1~old", validFor: TimeSpan.FromMinutes(1)));

        var refresh = account.GetValidSessionAsync();
        await refreshing.Task;
        var signOut = account.LogoutAsync(); // while Epic is still answering the refresh
        answer.SetResult();
        await refresh;
        await signOut;

        Assert.False(account.IsLoggedIn);
        Assert.False(File.Exists(_sessionPath));
    }

    [Fact]
    public void Tokens_and_codes_quoted_in_Epic_errors_are_left_out()
    {
        string masked = EpicAPIException.MaskSecrets(
            "Sorry the refresh token 'eg1~eyJhbGciOi.payload-part.sig_nature' is invalid; code 0123456789abcdef0123456789abcdef was not found");

        Assert.DoesNotContain("eg1~", masked);
        Assert.DoesNotContain("0123456789abcdef", masked);
        Assert.StartsWith("Sorry the refresh token '…' is invalid; code … was not found", masked);
    }

    private (EpicAccount Account, EpicAPIClient API) SignedIn(FakeEpic epic, EpicAuthSession session)
    {
        var store = new SessionStore(_sessionPath);
        store.Save(session);
        var http = new HttpClient(epic);
        var account = new EpicAccount(new EpicAuthClient(http), store);
        return (account, new EpicAPIClient(http, account) { RetryBaseDelay = TimeSpan.FromMilliseconds(1) });
    }

    private static EpicAuthSession Session(string accessToken, TimeSpan validFor) => new()
    {
        AccessToken = accessToken,
        AccessExpiresAt = DateTimeOffset.UtcNow + validFor,
        RefreshToken = "eg1~refresh",
        RefreshExpiresAt = DateTimeOffset.UtcNow.AddDays(20),
        AccountID = "account",
    };

    private static HttpResponseMessage Tokens(string accessToken) => JSON($$"""
        { "access_token": "{{accessToken}}", "expires_in": 7200, "refresh_token": "eg1~refresh2", "refresh_expires": 28800,
          "account_id": "account", "displayName": "Tester" }
        """);

    private static HttpResponseMessage JSON(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class FakeEpic(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public FakeEpic(Func<HttpRequestMessage, HttpResponseMessage> respond) : this(request => Task.FromResult(respond(request))) { }

        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
                Requests.Add(request.RequestUri!.ToString());
            return respond(request);
        }
    }
}
