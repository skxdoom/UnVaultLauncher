using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;
using Unvault.Core;

namespace Unvault.App.Services;

/// <summary>
/// Preview images for the Fab tiles. Asks the image CDNs for copies sized to the tiles at the display's scaling
/// (a 2560 px original can be ~450 KB; a 240 px copy is ~10 KB), keeps them on disk per item, decodes off the UI
/// thread, and holds only the most recently shown pictures in memory.
/// Kept per item rather than per link because Fab sends a different gallery picture as "featured" from one library
/// fetch to the next (about half of them changed within a day): keyed by link, every refresh re-downloaded half the
/// pictures and left the old files behind. A picture is fetched again after <see cref="MaxAge"/>, so a seller's new
/// picture shows up eventually, and pictures not used for that long are deleted.
/// Best effort throughout: a picture that can't be had just leaves the placeholder.
/// </summary>
public sealed class ThumbnailCache
{
    /// <summary>Width of a tile's picture in layout units (FabRowView.TileWidth).</summary>
    private const double TileWidth = 236;

    /// <summary>A few screens' worth, so scrolling back doesn't flash placeholders.</summary>
    private const int MemoryCapacity = 200;

    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    /// <summary>EGL's vault.json uses this for pictures inside the download itself, e.g. local://E:\…\data\Preview.png.</summary>
    private const string LocalScheme = "local://";

    private readonly HttpClient _http;
    private readonly string _folder;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Task<Bitmap?>> _pending = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, Bitmap Bitmap)> _recent = new(); // most recently used first
    private readonly Dictionary<string, LinkedListNode<(string Key, Bitmap Bitmap)>> _recentByKey = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _downloads = new(6);

    /// <param name="folder">Where pictures are kept; normally %LOCALAPPDATA%\UnvaultLauncher\thumbnails.</param>
    public ThumbnailCache(HttpClient http, string? folder = null)
    {
        _http = http;
        _folder = folder ?? DefaultFolder;
        _ = Task.Run(CleanUp);
    }

    public static string DefaultFolder => Path.Combine(AppPaths.DataDirectory, "thumbnails");

    private string ItemsFolder => Path.Combine(_folder, "items");

    /// <summary>Pixel width fetched and decoded: the tile's width at the display's scaling, in steps of 80 (240 at 100 %, 320 at 125 %, 400 at 150 %, up to 480).</summary>
    public int Width { get; private set; } = 240;

    public void UseScaling(double scaling) => Width = Math.Clamp((int)Math.Ceiling(TileWidth * scaling / 80) * 80, 240, 480);

    /// <param name="key">What the picture belongs to (e.g. the Fab item), so a changing link doesn't mean a new download. Defaults to the link.</param>
    public Task<Bitmap?> GetAsync(string url, string? key = null)
    {
        int width = Width;
        string memoryKey = $"{key ?? url}|{width}";
        lock (_lock)
        {
            if (_recentByKey.TryGetValue(memoryKey, out var node))
            {
                _recent.Remove(node);
                _recent.AddFirst(node);
                return Task.FromResult<Bitmap?>(node.Value.Bitmap);
            }
            if (_failed.Contains(memoryKey))
                return Task.FromResult<Bitmap?>(null);
            if (!_pending.TryGetValue(memoryKey, out var pending))
                _pending[memoryKey] = pending = Task.Run(() => LoadAsync(url, key ?? url, width, memoryKey));
            return pending;
        }
    }

    /// <summary>The CDN's copy of a picture at this width, where the host makes one on request.</summary>
    internal static Uri Resized(Uri uri, int width) => uri.Host.ToLowerInvariant() switch
    {
        // Fab's pictures sit behind Cloudflare image resizing (scales down only).
        "media.fab.com" when !uri.AbsolutePath.StartsWith("/cdn-cgi/", StringComparison.Ordinal) =>
            new Uri($"https://media.fab.com/cdn-cgi/image/width={width}{uri.AbsolutePath}"),
        // Old Marketplace pictures: Epic's store image service.
        "cdn1.epicgames.com" when uri.Query.Length == 0 => new Uri($"{uri.GetLeftPart(UriPartial.Path)}?resize=1&w={width}"),
        _ => uri,
    };

    private async Task<Bitmap?> LoadAsync(string url, string key, int width, string memoryKey)
    {
        Bitmap? bitmap = null;
        try
        {
            string? file = url.StartsWith(LocalScheme, StringComparison.OrdinalIgnoreCase)
                ? url[LocalScheme.Length..]
                : Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
                    ? await DownloadAsync(uri, key, width)
                    : null;
            if (file is not null && File.Exists(file))
            {
                await using var stream = File.OpenRead(file);
                bitmap = Bitmap.DecodeToWidth(stream, width);
            }
        }
        catch (Exception)
        {
            // Deliberately broad: network, disk, unknown image formats… none of it is worth more than a placeholder.
        }

        lock (_lock)
        {
            _pending.Remove(memoryKey);
            if (bitmap is null)
            {
                _failed.Add(memoryKey);
            }
            else
            {
                _recentByKey[memoryKey] = _recent.AddFirst((memoryKey, bitmap));
                if (_recent.Count > MemoryCapacity)
                {
                    // Not disposed: a tile may still be drawing it. The GC frees it once nothing does.
                    _recentByKey.Remove(_recent.Last!.Value.Key);
                    _recent.RemoveLast();
                }
            }
        }
        return bitmap;
    }

    private async Task<string> DownloadAsync(Uri uri, string key, int width)
    {
        string file = Path.Combine(ItemsFolder, $"{Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))}-{width}.thumb");
        if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < MaxAge)
            return file;

        await _downloads.WaitAsync();
        try
        {
            var resized = Resized(uri, width);
            byte[] data;
            try
            {
                data = await _http.GetByteArrayAsync(resized);
            }
            catch (HttpRequestException) when (resized != uri)
            {
                data = await _http.GetByteArrayAsync(uri);
            }

            Directory.CreateDirectory(ItemsFolder);
            string temporary = file + ".tmp";
            await File.WriteAllBytesAsync(temporary, data);
            File.Move(temporary, file, overwrite: true);
            return file;
        }
        catch (Exception) when (File.Exists(file))
        {
            return file; // offline or the link is gone: an old picture beats none
        }
        finally
        {
            _downloads.Release();
        }
    }

    /// <summary>Drops pictures not refreshed for <see cref="MaxAge"/>, and the per-link files earlier builds kept directly in the folder.</summary>
    private void CleanUp()
    {
        try
        {
            if (Directory.Exists(_folder))
                foreach (string file in Directory.EnumerateFiles(_folder))
                    File.Delete(file);
            if (Directory.Exists(ItemsFolder))
                foreach (var file in new DirectoryInfo(ItemsFolder).EnumerateFiles())
                    if (DateTime.UtcNow - file.LastWriteTimeUtc >= MaxAge)
                        file.Delete();
        }
        catch (Exception)
        {
            // Only disk space; try again next start.
        }
    }
}
