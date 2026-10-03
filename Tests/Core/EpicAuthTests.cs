using Unvault.Core.Epic;

namespace Unvault.Core.Tests;

public class EpicAuthTests
{
    private const string Code = "0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData(Code)]
    [InlineData("  \"" + Code + "\"  ")]
    [InlineData("{\"warning\":\"Do not share this code\",\"redirectUrl\":\"https://localhost/launcher/authorized?code=" + Code + "\",\"authorizationCode\":\"" + Code + "\",\"exchangeCode\":null,\"sid\":null}")]
    [InlineData("\"authorizationCode\":\"" + Code + "\",\"exchangeCode\":null")]
    [InlineData("https://localhost/launcher/authorized?code=" + Code)]
    [InlineData("{\"redirectUrl\":\"https://localhost/launcher/authorized?code=" + Code + "\",\"authorizationCode\":null,\"sid\":null}")]
    public void ExtractAuthorizationCode_accepts_what_users_paste(string pasted) =>
        Assert.Equal(Code, EpicAuthClient.ExtractAuthorizationCode(pasted));

    [Fact]
    public void Error_messages_never_contain_tokens()
    {
        string token = "eg1~" + new string('a', 900);
        var logout = new Uri($"https://account-public-service-prod03.ol.epicgames.com/account/api/oauth/sessions/kill/{token}");
        var signed = new Uri("https://cdn.example.com/CloudDir/x.manifest?f_token=secret");

        Assert.Equal("https://account-public-service-prod03.ol.epicgames.com/account/api/oauth/sessions/kill/…", EpicAPIException.DescribeEndpoint(logout));
        Assert.Equal("https://cdn.example.com/CloudDir/x.manifest", EpicAPIException.DescribeEndpoint(signed));
    }

    [Fact]
    public void Signed_Fab_manifest_URL_keeps_its_token_for_chunks()
    {
        var source = ChunkSource.FromSignedManifestURL("https://egdownload.fastly-edge.com/Builds/Rocket/Automated/X/CloudDir/Name.manifest?f_token=abc");
        var chunk = new Manifests.ChunkInfo { GUID = new Manifests.EpicGUID(1, 2, 3, 4), Hash = 1, GroupNum = 5 };

        Assert.Equal("https://egdownload.fastly-edge.com/Builds/Rocket/Automated/X/CloudDir", source.BaseURL);
        Assert.Equal(
            "https://egdownload.fastly-edge.com/Builds/Rocket/Automated/X/CloudDir/ChunksV4/05/0000000000000001_00000001000000020000000300000004.chunk?f_token=abc",
            source.GetChunkURL(chunk, 21));
    }

    [Fact]
    public void Manifest_location_base_URL_is_the_CloudDir()
    {
        var location = new EpicManifestLocation { URI = "https://cdn.example.com/Builds/UE5/Releases/CloudDir/abc.manifest" };

        Assert.Equal("https://cdn.example.com/Builds/UE5/Releases/CloudDir", location.GetBaseURL());
    }

    [Fact]
    public void Manifest_download_URL_appends_signed_query_params()
    {
        var location = new EpicManifestLocation
        {
            URI = "https://cdn.example.com/x.manifest",
            QueryParams = [new EpicQueryParam { Name = "f_token", Value = "a b" }, new EpicQueryParam { Name = "sig", Value = "1" }],
        };

        Assert.Equal("https://cdn.example.com/x.manifest?f_token=a%20b&sig=1", location.GetDownloadURL());
    }

    [Fact]
    public void Session_store_round_trips_and_survives_garbage()
    {
        string path = Path.Combine(Path.GetTempPath(), $"unvault-session-{Guid.NewGuid():N}.dat");
        var store = new SessionStore(path);
        var session = new EpicAuthSession
        {
            AccessToken = "eg1~access",
            AccessExpiresAt = DateTimeOffset.UtcNow.AddHours(2),
            RefreshToken = "eg1~refresh",
            RefreshExpiresAt = DateTimeOffset.UtcNow.AddDays(20),
            AccountID = "acc",
            DisplayName = "Tester",
        };

        try
        {
            store.Save(session);
            Assert.Equal(session, store.Load());
            if (OperatingSystem.IsWindows())
                Assert.Equal(-1, File.ReadAllBytes(path).AsSpan().IndexOf("eg1~refresh"u8)); // DPAPI-encrypted, not plaintext

            File.WriteAllText(path, "not a session");
            Assert.Null(store.Load());
        }
        finally
        {
            store.Delete();
        }
    }
}
