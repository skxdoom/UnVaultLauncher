using Unvault.Core.Manifests;

namespace Unvault.Core.Epic;

/// <summary>
/// A CDN directory (CloudDir) chunks can be fetched from. Fab's URLs are signed: the manifest URL's
/// query string must be appended to every chunk URL, or the CDN rejects the request.
/// </summary>
public sealed record ChunkSource(string BaseURL, string Query = "")
{
    public string GetChunkURL(ChunkInfo chunk, uint featureLevel) =>
        $"{BaseURL}/{chunk.GetPath(featureLevel)}" + (Query.Length > 0 ? "?" + Query : "");

    /// <summary>".../CloudDir/Name.manifest?f_token=…" → (".../CloudDir", "f_token=…").</summary>
    public static ChunkSource FromSignedManifestURL(string manifestURL)
    {
        int queryStart = manifestURL.IndexOf('?');
        string path = queryStart >= 0 ? manifestURL[..queryStart] : manifestURL;
        string query = queryStart >= 0 ? manifestURL[(queryStart + 1)..] : "";
        return new ChunkSource(path[..path.LastIndexOf('/')], query);
    }
}

/// <summary>A downloaded build manifest, where its chunks live, and the keys for any encrypted chunks.</summary>
public sealed record DownloadedManifest(
    Manifest Manifest,
    byte[] RawBytes,
    IReadOnlyList<ChunkSource> Sources,
    IReadOnlyDictionary<string, string> Secrets);
