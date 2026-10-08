using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using UnVault.Core.Manifests;
using UnVault.Core.Util;

namespace UnVault.Core.Epic;

/// <summary>Launcher API calls that need a signed-in account.</summary>
public sealed partial class EpicAPIClient(HttpClient http, EpicAccount account)
{
    /// <summary>How many times a request is sent before a busy or failing Epic service is reported.</summary>
    private const int Tries = 4;

    /// <summary>Wait before sending a failed request again; doubles each time. Tests make it short.</summary>
    internal TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest "retry after" from Epic that's waited out; asked to wait longer, the request fails with Epic's message.</summary>
    internal TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromMinutes(1);
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
        bool cacheable = !string.IsNullOrEmpty(expectedSHA1) && FileNames.IsPlain(cacheName); // the name comes from Epic

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
                    await Task.Run(() => SaveToCache(cachePath, data)); // an engine's is big; keep the writing off the UI thread
                return data;
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                // A timeout too: the next server may answer.
                errors.Add($"{StripQuery(url)}: {(ex is TaskCanceledException ? "timed out" : ex.Message)}");
            }
        }

        throw new EpicAPIException("Couldn't download the manifest:\n  " + string.Join("\n  ", errors));
    }

    /// <summary>Only a speed-up for next time, so a failed write (another download writing the same file, a full disk) isn't one.</summary>
    private static void SaveToCache(string path, byte[] data)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteAllBytes(path, data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Downloaded again next time.
        }
    }

    private Task<T> GetJSONAsync<T>(string url, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken, bool retryForbidden = false) =>
        SendJSONAsync(() => new HttpRequestMessage(HttpMethod.Get, url), typeInfo, cancellationToken, retryForbidden);

    /// <summary>
    /// Sends an authorized request and reads a JSON response. A busy or failing service (429, 5xx) or a failed connection
    /// is tried again after a growing wait, or as long as Epic asks with "retry after". With <paramref name="retryForbidden"/>,
    /// so is a 403: Fab's edge returns those sporadically for valid requests.
    /// </summary>
    private async Task<T> SendJSONAsync<T>(Func<HttpRequestMessage> createRequest, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken, bool retryForbidden = false)
    {
        bool renewed = false;
        for (int attempt = 1; ; attempt++)
        {
            var session = await account.GetValidSessionAsync(cancellationToken);

            using var request = createRequest();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);

            HttpResponseMessage sent;
            try
            {
                sent = await http.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException) when (attempt < Tries)
            {
                // The connection failed (reset, a moment offline); a timeout isn't retried, as it already took long.
                await Task.Delay(RetryDelay(attempt), cancellationToken);
                continue;
            }

            using var response = sent;
            if (response.StatusCode == HttpStatusCode.Unauthorized && !renewed)
            {
                // The token was turned down before its time (sessions ended elsewhere, a password change): a new one, then
                // once more. If the session itself is over, that's a NotLoggedInException.
                renewed = true;
                await account.RenewAsync(session, cancellationToken);
                continue;
            }
            if (attempt < Tries && RetryWait(response, attempt, retryForbidden) is { } wait)
            {
                await Task.Delay(wait, cancellationToken);
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

    /// <summary>How long to wait before sending a failed request again; null when another try won't help.</summary>
    private TimeSpan? RetryWait(HttpResponseMessage response, int attempt, bool retryForbidden)
    {
        switch (response.StatusCode)
        {
            case HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable:
                var asked = response.Headers.RetryAfter is { } after ? after.Delta ?? after.Date - DateTimeOffset.UtcNow : null;
                if (asked > MaxRetryAfter)
                    return null;
                return asked > RetryDelay(attempt) ? asked : RetryDelay(attempt);
            case HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout:
                return RetryDelay(attempt);
            case HttpStatusCode.Forbidden when retryForbidden:
                return RetryDelay(attempt);
            default:
                return null;
        }
    }

    private TimeSpan RetryDelay(int attempt) => RetryBaseDelay * (1 << (attempt - 1));

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
