using System.Net;

namespace UnVault.Core.Epic;

public static class EpicHTTP
{
    /// <summary>One shared client for Epic's services and CDNs. Reuse it; don't create one per request.</summary>
    public static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 64, // the highest "parallel downloads" setting
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(100) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", EpicEndpoints.UserAgent);
        return http;
    }
}
