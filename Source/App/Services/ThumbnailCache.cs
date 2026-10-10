using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;
using SkiaSharp;
using UnVault.Core;

namespace UnVault.App.Services;

/// <summary>
/// Preview images for the Fab tiles. Asks the image CDNs for copies sized to the tiles at the display's scaling
/// (the originals are many times bigger than a tile shows), keeps them on disk per item, decodes off the UI
/// thread, and holds in memory only the pictures on screen plus the most recently shown others.
/// Kept per item rather than per link because Fab sends a different gallery picture as "featured" for many items from
/// one library fetch to the next: keyed by link, every refresh re-downloaded those pictures and left the old files behind.
/// Fab gives each picture's upload date, so a picture uploaded after ours was saved (the creator changed it) is fetched
/// again, while an older one Fab swaps in isn't. Any picture is fetched again after <see cref="MaxAge"/>, and pictures not
/// used for that long are deleted.
/// Best effort throughout: a picture that can't be had just leaves the placeholder.
/// </summary>
/// <remarks>
/// A tile takes a picture with <see cref="Get"/> and gives it back with <see cref="Release"/>. A decoded picture lives in
/// native memory the garbage collector doesn't see, so leaving dropped ones to it let a long scroll pile them up; the
/// cache frees them itself once no tile shows them. A picture still loading when every tile that wanted it has scrolled
/// away isn't downloaded or decoded at all.
/// </remarks>
public sealed class ThumbnailCache
{
    /// <summary>Width of a tile's picture in layout units (FabRowView.TileWidth).</summary>
    private const double TileWidth = 236;

    /// <summary>Pictures kept after their tiles scrolled away: a couple of screens' worth, so scrolling back doesn't flash placeholders.</summary>
    private const int UnusedCapacity = 64;

    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    /// <summary>
    /// EGL's vault.json uses this for pictures inside the download itself, e.g. local://E:\…\data\Preview.png. Read as
    /// given: VaultEntry.Thumbnail only passes ones in the entry's own folder.
    /// </summary>
    private const string LocalScheme = "local://";

    /// <summary>Far more than any preview picture; a server sending more is cut off rather than let fill memory.</summary>
    internal long MaxDownloadBytes { get; init; } = 16 * 1024 * 1024;

    /// <summary>
    /// No preview picture is this big on a side. Decoding can need the whole picture in memory before it's scaled down,
    /// so a small file claiming a huge one is left as a placeholder.
    /// </summary>
    internal const int MaxSide = 8192;

    private readonly HttpClient _http;
    private readonly string _folder;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _unused = new(); // loaded, shown by no tile; most recently shown first
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _downloads = new(6);
    private readonly SemaphoreSlim _decodes = new(Math.Clamp(Environment.ProcessorCount / 2, 1, 4)); // each holds a whole decoded picture meanwhile

    /// <summary>One picture at one width, and how many tiles show it (or wait for it).</summary>
    internal sealed class Entry(string key)
    {
        public string Key { get; } = key;
        public Task<Bitmap?> Loading { get; set; } = Task.FromResult<Bitmap?>(null);
        public Bitmap? Bitmap { get; set; }

        /// <summary>When the picture's file was saved, to tell whether the source has a newer one.</summary>
        public DateTime SavedUtc { get; set; }

        public int Users { get; set; }
        public LinkedListNode<Entry>? UnusedNode { get; set; }
    }

    /// <summary>A tile's hold on a picture; give it back with <see cref="Release"/> once the tile stops showing it.</summary>
    public sealed class Use
    {
        internal Use(Entry? entry, Task<Bitmap?> picture)
        {
            Entry = entry;
            Picture = picture;
        }

        internal Entry? Entry { get; set; }

        /// <summary>The picture, or null for a placeholder.</summary>
        public Task<Bitmap?> Picture { get; }
    }

    /// <param name="folder">Where pictures are kept; normally %LOCALAPPDATA%\UnVaultLauncher\thumbnails.</param>
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

    /// <summary>Takes a picture for a tile: from memory, or loaded from disk or the web.</summary>
    /// <param name="key">What the picture belongs to (e.g. the Fab item), so a changing link doesn't mean a new download. Defaults to the link.</param>
    /// <param name="uploaded">When the source says the picture was uploaded; one uploaded after ours was saved is the creator's new picture.</param>
    public Use Get(string url, string? key = null, DateTimeOffset? uploaded = null)
    {
        int width = Width;
        string memoryKey = $"{key ?? url}|{width}";
        lock (_lock)
        {
            if (_failed.Contains(memoryKey))
                return new Use(null, Task.FromResult<Bitmap?>(null));
            if (_entries.TryGetValue(memoryKey, out var entry) && entry.Bitmap is not null && IsNewer(uploaded, entry.SavedUtc))
            {
                Forget(entry);
                entry = null;
            }
            if (entry is null)
            {
                _entries[memoryKey] = entry = new Entry(memoryKey);
                entry.Loading = Task.Run(() => LoadAsync(entry, url, key ?? url, width, uploaded));
            }
            if (entry.UnusedNode is not null)
            {
                _unused.Remove(entry.UnusedNode);
                entry.UnusedNode = null;
            }
            entry.Users++;
            return new Use(entry, entry.Loading);
        }
    }

    /// <summary>The tile no longer shows the picture. Kept a while for scrolling back; the oldest unused ones are freed.</summary>
    public void Release(Use use)
    {
        lock (_lock)
        {
            if (use.Entry is not { } entry)
                return;
            use.Entry = null; // a second release changes nothing
            if (--entry.Users == 0 && entry.Bitmap is not null)
            {
                if (IsCurrent(entry))
                    KeepUnused(entry);
                else
                    entry.Bitmap.Dispose(); // a newer picture replaced it meanwhile
            }
        }
    }

    /// <summary>Whether the picture was uploaded after our copy was saved.</summary>
    private static bool IsNewer(DateTimeOffset? uploaded, DateTime savedUtc) => uploaded?.UtcDateTime > savedUtc;

    /// <summary>Under the lock: whether this is the picture new tiles get for its key.</summary>
    private bool IsCurrent(Entry entry) => _entries.TryGetValue(entry.Key, out var current) && current == entry;

    /// <summary>Under the lock: a newer picture takes this one's place. Freed now if no tile shows it, else once the last one gives it back.</summary>
    private void Forget(Entry entry)
    {
        _entries.Remove(entry.Key);
        if (entry.UnusedNode is not null)
        {
            _unused.Remove(entry.UnusedNode);
            entry.UnusedNode = null;
            entry.Bitmap?.Dispose();
        }
    }

    /// <summary>Under the lock: no tile shows the picture; it's kept as the most recent unused one, the oldest beyond the limit are freed.</summary>
    private void KeepUnused(Entry entry)
    {
        entry.UnusedNode = _unused.AddFirst(entry);
        while (_unused.Count > UnusedCapacity)
        {
            var oldest = _unused.Last!.Value;
            _unused.RemoveLast();
            _entries.Remove(oldest.Key);
            // No tile shows it any more; one drawn in the last frame keeps the pixels it needs until then.
            oldest.Bitmap?.Dispose();
        }
    }

    /// <summary>
    /// Under the lock: whether a tile still wants the picture. If none does, it's forgotten instead of downloaded or decoded,
    /// and the next tile to want it loads it afresh.
    /// </summary>
    private bool StillWanted(Entry entry)
    {
        lock (_lock)
        {
            if (entry.Users > 0)
                return true;
            if (IsCurrent(entry))
                _entries.Remove(entry.Key);
            return false;
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

    private async Task<Bitmap?> LoadAsync(Entry entry, string url, string key, int width, DateTimeOffset? uploaded)
    {
        Bitmap? bitmap = null;
        DateTime saved = default;
        try
        {
            // Over https only, so nobody on the network can see or swap the pictures; .NET doesn't follow a redirect to http.
            string? file = url.StartsWith(LocalScheme, StringComparison.OrdinalIgnoreCase)
                ? url[LocalScheme.Length..]
                : Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
                    ? await DownloadAsync(uri, key, width, uploaded, entry)
                    : null;
            if (file is not null && File.Exists(file))
            {
                await _decodes.WaitAsync();
                try
                {
                    if (!StillWanted(entry))
                        return null;
                    if (IsPreviewSized(file))
                    {
                        saved = File.GetLastWriteTimeUtc(file);
                        await using var stream = File.OpenRead(file);
                        bitmap = Bitmap.DecodeToWidth(stream, width);
                    }
                }
                finally
                {
                    _decodes.Release();
                }
            }
        }
        catch (Exception)
        {
            // Deliberately broad: network, disk, unknown image formats… none of it is worth more than a placeholder.
        }

        lock (_lock)
        {
            if (!IsCurrent(entry))
            {
                bitmap?.Dispose(); // forgotten meanwhile: nobody can be handed it
                return null;
            }
            if (bitmap is null)
            {
                _entries.Remove(entry.Key);
                _failed.Add(entry.Key);
            }
            else
            {
                entry.Bitmap = bitmap;
                entry.SavedUtc = saved;
                if (entry.Users == 0)
                    KeepUnused(entry); // its tiles scrolled away while it loaded
            }
        }
        return bitmap;
    }

    /// <returns>The picture's file, or null when no tile wants it any more by the time a download could start.</returns>
    private async Task<string?> DownloadAsync(Uri uri, string key, int width, DateTimeOffset? uploaded, Entry entry)
    {
        string file = Path.Combine(ItemsFolder, $"{Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))}-{width}.thumb");
        var saved = File.GetLastWriteTimeUtc(file); // long ago for a missing file
        if (DateTime.UtcNow - saved < MaxAge && !IsNewer(uploaded, saved))
            return file;

        await _downloads.WaitAsync();
        try
        {
            if (!StillWanted(entry))
                return null;
            var resized = Resized(uri, width);
            byte[] data;
            try
            {
                data = await FetchAsync(resized);
            }
            catch (HttpRequestException) when (resized != uri)
            {
                data = await FetchAsync(uri);
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

    /// <summary>The picture's bytes, up to <see cref="MaxDownloadBytes"/>; a server may send no length, or the wrong one.</summary>
    private async Task<byte[]> FetchAsync(Uri uri)
    {
        // The client's time limit covers a whole request only when it reads the body itself; this one is read here.
        using var timeout = new CancellationTokenSource(_http.Timeout);
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxDownloadBytes)
            throw new InvalidDataException($"Too big for a preview picture: {uri}");

        await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var data = new MemoryStream();
        byte[] buffer = new byte[81920];
        for (int read; (read = await body.ReadAsync(buffer, timeout.Token)) > 0;)
        {
            if (data.Length + read > MaxDownloadBytes)
                throw new InvalidDataException($"Too big for a preview picture: {uri}");
            data.Write(buffer, 0, read);
        }
        return data.ToArray();
    }

    /// <summary>Whether the picture's header gives a size a preview can have; reads only the header.</summary>
    private static bool IsPreviewSized(string file)
    {
        using var codec = SKCodec.Create(file);
        return codec is { Info: { Width: <= MaxSide, Height: <= MaxSide } };
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
