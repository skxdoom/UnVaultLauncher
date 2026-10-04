using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using UnVault.Core.Manifests;

namespace UnVault.Core.Epic;

/// <summary>Launcher API calls that need a signed-in account.</summary>
public sealed partial class EpicAPIClient(HttpClient http, EpicAccount account)
{
    /// <summary>Everything the account owns that the launcher can install (engines, plugins, games).</summary>
    public Task<List<EpicAsset>> GetAssetsAsync(string platform = "Windows", string label = "Live", CancellationToken cancellationToken = default) =>
        GetJSONAsync(
            $"{EpicEndpoints.LauncherService}/launcher/api/public/assets/{platform}?label={label}",
            EpicJSONContext.Default.ListEpicAsset,
            cancellationToken);

    /// <summary>The current build of an app and where its manifest lives.</summary>
    public async Task<EpicBuildInfo> GetBuildInfoAsync(
        string catalogNamespace, string catalogItemID, string appName,
        string platform = "Windows", string label = "Live", CancellationToken cancellationToken = default)
    {
        string url = $"{EpicEndpoints.LauncherService}/launcher/api/public/assets/v2/platform/{platform}" +
                     $"/namespace/{catalogNamespace}/catalogItem/{catalogItemID}/app/{appName}/label/{label}";
        var response = await GetJSONAsync(url, EpicJSONContext.Default.EpicBuildInfoResponse, cancellationToken);
        return response.Elements.FirstOrDefault()
            ?? throw new EpicAPIException($"No build of {appName} for {platform}/{label}.");
    }

    /// <summary>
    /// Downloads and parses a build's manifest, trying each CDN in turn. Verified against the SHA-1 from
    /// the build info and cached under %LOCALAPPDATA%\UnVaultLauncher\manifests so repeat calls are instant.
    /// </summary>
    public async Task<DownloadedManifest> DownloadManifestAsync(EpicBuildInfo build, CancellationToken cancellationToken = default)
    {
        if (build.Manifests.Count == 0)
            throw new EpicAPIException($"Build {build.BuildVersion} lists no manifest locations.");

        byte[] data = await DownloadVerifiedAsync(
            build.Manifests.Select(m => m.GetDownloadURL()), build.Hash, $"{build.AppName}_{build.Hash}", cancellationToken);

        // Launcher manifest URLs may be signed, but chunks are fetched from the plain CloudDir.
        var sources = build.Manifests.Select(m => new ChunkSource(m.GetBaseURL())).Distinct().ToList();
        return new DownloadedManifest(await ParseAsync(data, cancellationToken), data, sources, build.Secrets ?? []);
    }

    /// <summary>Fetches the first URL that returns a file matching the expected SHA-1, using/filling the manifest cache.</summary>
    private async Task<byte[]> DownloadVerifiedAsync(IEnumerable<string> urls, string? expectedSHA1, string cacheName, CancellationToken cancellationToken)
    {
        string cachePath = Path.Combine(AppPaths.ManifestCacheDirectory, cacheName + ".manifest");
        bool cacheable = !string.IsNullOrEmpty(expectedSHA1);

        if (cacheable && File.Exists(cachePath))
        {
            byte[] cached = await File.ReadAllBytesAsync(cachePath, cancellationToken);
            if (await Task.Run(() => HashMatches(cached, expectedSHA1), cancellationToken))
                return cached;
        }

        var errors = new List<string>();
        foreach (string url in urls)
        {
            try
            {
                // CDN request: no auth header; signed URLs carry their own query parameters.
                byte[] data = await http.GetByteArrayAsync(url, cancellationToken);
                if (!await Task.Run(() => HashMatches(data, expectedSHA1), cancellationToken))
                {
                    errors.Add($"{StripQuery(url)}: hash mismatch");
                    continue;
                }

                if (cacheable)
                {
                    Directory.CreateDirectory(AppPaths.ManifestCacheDirectory);
                    await File.WriteAllBytesAsync(cachePath, data, cancellationToken);
                }
                return data;
            }
            catch (HttpRequestException ex)
            {
                errors.Add($"{StripQuery(url)}: {ex.Message}");
            }
        }

        throw new EpicAPIException("Couldn't download the manifest:\n  " + string.Join("\n  ", errors));
    }

    private Task<T> GetJSONAsync<T>(string url, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken, int attempts = 1) =>
        SendJSONAsync(() => new HttpRequestMessage(HttpMethod.Get, url), typeInfo, cancellationToken, attempts);

    /// <summary>
    /// Sends an authorized request and reads a JSON response. With attempts &gt; 1, retries 403s with
    /// exponential backoff — Fab's edge returns those sporadically for valid requests.
    /// </summary>
    private async Task<T> SendJSONAsync<T>(Func<HttpRequestMessage> createRequest, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken, int attempts = 1)
    {
        for (int attempt = 1; ; attempt++)
        {
            var session = await account.GetValidSessionAsync(cancellationToken);

            using var request = createRequest();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);

            using var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Forbidden && attempt < attempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(1 << (attempt - 1)), cancellationToken);
                continue;
            }
            await EpicAPIException.ThrowIfFailedAsync(response, cancellationToken);

            try
            {
                return await response.Content.ReadFromJsonAsync(typeInfo, cancellationToken)
                    ?? throw new EpicAPIException($"Empty response from {StripQuery(request.RequestUri?.ToString() ?? "")}.");
            }
            catch (JsonException ex)
            {
                // Epic changing a response shape should read as an API problem, not crash whoever asked.
                throw new EpicAPIException($"Unexpected response from {StripQuery(request.RequestUri?.ToString() ?? "")}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// An engine's manifest lists every file of the engine, so parsing it isn't instant, and callers are often the UI:
    /// it happens on the thread pool. Hashing too (see <see cref="DownloadVerifiedAsync"/>).
    /// </summary>
    private static Task<Manifest> ParseAsync(byte[] data, CancellationToken cancellationToken) =>
        Task.Run(() => Manifest.Parse(data), cancellationToken);

    private static bool HashMatches(byte[] data, string? expectedHex) =>
        string.IsNullOrEmpty(expectedHex) || Convert.ToHexString(SHA1.HashData(data)).Equals(expectedHex, StringComparison.OrdinalIgnoreCase);

    /// <summary>For messages: signed URLs' tokens are noise and shouldn't end up in logs.</summary>
    private static string StripQuery(string url)
    {
        int queryStart = url.IndexOf('?');
        return queryStart >= 0 ? url[..queryStart] : url;
    }
}
