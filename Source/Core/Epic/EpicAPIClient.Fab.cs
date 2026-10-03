using System.Net.Http.Json;
using Unvault.Core.Manifests;

namespace Unvault.Core.Epic;

/// <summary>Fab (ex-Marketplace) library and downloads. Uses the same Epic launcher session; no fab.com cookies.</summary>
public sealed partial class EpicAPIClient
{
    /// <summary>Fab's edge sporadically answers valid requests with 403; this many tries smooths that over.</summary>
    private const int FabAttempts = 4;

    /// <summary>
    /// Pages are fetched one after another (each needs the previous cursor), and Fab pays ~0.5 s per request on
    /// top of ~9 ms per item. Measured on a 930-item library: 100 per page took 20 s over 23 pages, 1000 took 9 s.
    /// </summary>
    private const int FabLibraryPageSize = 1000;

    /// <summary>Everything in the account's Fab library that can be used from Unreal Engine.</summary>
    /// <param name="progress">Receives the number of entries read so far, after each page.</param>
    public async Task<List<FabLibraryItem>> GetFabLibraryAsync(IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        var session = await account.GetValidSessionAsync(cancellationToken);
        var items = new List<FabLibraryItem>();
        string? cursor = null;

        do
        {
            string url = $"{EpicEndpoints.FabService}/e/accounts/{session.AccountID}/ue/library?count={FabLibraryPageSize}";
            if (cursor is not null)
                url += "&cursor=" + Uri.EscapeDataString(cursor);

            var page = await GetJSONAsync(url, FabJSONContext.Default.FabLibraryPage, cancellationToken, FabAttempts);
            items.AddRange(page.Results ?? []);
            progress?.Report(items.Count);
            cursor = string.IsNullOrEmpty(page.Cursors?.Next) ? null : page.Cursors.Next;
        }
        while (cursor is not null);

        return MergeDuplicateEntries(items);
    }

    /// <summary>
    /// The library lists licenses, not products: an asset acquired twice (e.g. granted again after the move
    /// to Fab, or a re-listed plugin) comes back as two entries, sometimes with different pictures. Folds them into
    /// one per asset, keeping the freshest listing data and every artifact either copy offers.
    /// </summary>
    internal static List<FabLibraryItem> MergeDuplicateEntries(IEnumerable<FabLibraryItem> entries)
    {
        var merged = new List<FabLibraryItem>();
        var byAsset = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (entry.AssetID.Length == 0)
            {
                merged.Add(entry);
                continue;
            }

            string key = entry.AssetNamespace + "/" + entry.AssetID;
            if (!byAsset.TryGetValue(key, out int index))
            {
                byAsset[key] = merged.Count;
                merged.Add(entry);
                continue;
            }

            var (fresh, other) = NewestUpload(entry) > NewestUpload(merged[index]) ? (entry, merged[index]) : (merged[index], entry);
            var versions = fresh.ProjectVersions ??= [];
            foreach (var version in other.ProjectVersions ?? [])
            {
                var same = versions.FirstOrDefault(v => string.Equals(v.ArtifactID, version.ArtifactID, StringComparison.OrdinalIgnoreCase));
                if (same is null)
                    versions.Add(version);
                else
                    same.EngineVersions = [.. (same.EngineVersions ?? []).Union(version.EngineVersions ?? [], StringComparer.OrdinalIgnoreCase)];
            }
            merged[index] = fresh;
        }

        return merged;

        static DateTimeOffset NewestUpload(FabLibraryItem item) =>
            (item.Images ?? []).Select(i => i.UploadedDate ?? DateTimeOffset.MinValue).DefaultIfEmpty(DateTimeOffset.MinValue).Max();
    }

    /// <summary>Signed manifest locations for one artifact (one engine version of a Fab item).</summary>
    public async Task<FabDownloadInfo> GetFabDownloadInfoAsync(
        string artifactID, string assetNamespace, string assetID, string platform = "Windows", CancellationToken cancellationToken = default)
    {
        var body = new FabArtifactManifestRequest { ItemID = assetID, Namespace = assetNamespace, Platform = platform };
        var response = await SendJSONAsync(
            () => new HttpRequestMessage(HttpMethod.Post, $"{EpicEndpoints.FabService}/e/artifacts/{artifactID}/manifest")
            {
                Content = JsonContent.Create(body, FabJSONContext.Default.FabArtifactManifestRequest),
            },
            FabJSONContext.Default.FabArtifactManifestResponse,
            cancellationToken,
            FabAttempts);

        return response.DownloadInfo?.FirstOrDefault()
            ?? throw new EpicAPIException($"Fab returned no download info for {artifactID} ({platform}).");
    }

    /// <summary>Downloads a Fab artifact's manifest from whichever signed distribution point still works.</summary>
    public async Task<DownloadedManifest> DownloadFabManifestAsync(FabDownloadInfo info, CancellationToken cancellationToken = default)
    {
        var points = (info.DistributionPoints ?? [])
            .Where(p => p.SignatureExpiration > DateTimeOffset.UtcNow.AddMinutes(1))
            .ToList();
        if (points.Count == 0)
            throw new EpicAPIException($"All download links for {info.ArtifactID} have expired; request them again.");

        byte[] data = await DownloadVerifiedAsync(
            points.Select(p => p.ManifestURL), info.ManifestHash, $"{info.ArtifactID}_{info.ManifestHash}", cancellationToken);

        var sources = points.Select(p => ChunkSource.FromSignedManifestURL(p.ManifestURL)).ToList();
        return new DownloadedManifest(Manifest.Parse(data), data, sources, new Dictionary<string, string>());
    }
}
