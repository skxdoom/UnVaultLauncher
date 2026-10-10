using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Text;
using Avalonia.Headless.XUnit;
using UnVault.App.Services;

namespace UnVault.App.Tests;

public sealed class ThumbnailCacheTests : IDisposable
{
    /// <summary>A 1×1 PNG.</summary>
    internal static readonly byte[] TinyPNG = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    /// <summary>Each test gets its own picture folder, never the real one.</summary>
    private readonly string _folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"unvault-thumbs-{Guid.NewGuid():N}")).FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // The background clean-up may still be looking at it.
        }
    }

    private ThumbnailCache NewCache(HttpMessageHandler? handler = null) =>
        new(handler is null ? new HttpClient() : new HttpClient(handler), _folder);

    [AvaloniaFact]
    public async Task Loads_EGL_local_thumbnails_from_disk()
    {
        // In a download's own folder, as in a vault cache: files directly in the cache folder are old leftovers the
        // cache deletes when it starts, so a picture there could be gone before it's read.
        string file = Path.Combine(Directory.CreateDirectory(Path.Combine(_folder, "Download")).FullName, "Thumb.png");
        await File.WriteAllBytesAsync(file, TinyPNG);

        Assert.NotNull(await NewCache().Get("local://" + file).Picture);
    }

    /// <summary>
    /// Every thumbnail of a real vault cache (set UNVAULT_REAL_VAULT to its folder; skipped otherwise). Downloads
    /// the web ones, so it's opt-in. Guards against whatever odd values EGL wrote into vault.json files.
    /// </summary>
    [AvaloniaFact]
    public async Task All_thumbnails_of_a_real_vault_cache_load_or_give_a_placeholder()
    {
        string? vault = Environment.GetEnvironmentVariable("UNVAULT_REAL_VAULT");
        Assert.SkipUnless(vault is not null && Directory.Exists(vault), "Set UNVAULT_REAL_VAULT to a Vault Cache folder to run this.");

        var cache = NewCache();
        var urls = Core.Vault.VaultCache.Scan(vault).Select(e => e.Thumbnail).OfType<string>().Distinct().ToList();
        var results = await Task.WhenAll(urls.Select(url => cache.Get(url).Picture));

        Assert.NotEmpty(urls);
        Assert.True(results.Count(r => r is not null) > urls.Count / 2, $"Only {results.Count(r => r is not null)} of {urls.Count} thumbnails loaded.");
    }

    [Theory]
    [InlineData("https://media.fab.com/image_previews/gallery_images/81cf/bae6.jpg", "https://media.fab.com/cdn-cgi/image/width=240/image_previews/gallery_images/81cf/bae6.jpg")]
    [InlineData("https://cdn1.epicgames.com/ue/product/Featured/SampleItem_featured-894x488-0000.png", "https://cdn1.epicgames.com/ue/product/Featured/SampleItem_featured-894x488-0000.png?resize=1&w=240")]
    [InlineData("https://media.fab.com/cdn-cgi/image/width=640/a.jpg", "https://media.fab.com/cdn-cgi/image/width=640/a.jpg")]
    [InlineData("https://example.com/picture.png", "https://example.com/picture.png")]
    public void Asks_the_CDN_for_tile_sized_pictures(string url, string expected) =>
        Assert.Equal(expected, ThumbnailCache.Resized(new Uri(url), 240).AbsoluteUri);

    // Tiles are 236 wide; at 100 % 240 px is plenty, high-DPI screens get more pixels.
    [AvaloniaTheory]
    [InlineData(1.0, 240)]
    [InlineData(1.25, 320)]
    [InlineData(1.5, 400)]
    [InlineData(1.75, 480)]
    [InlineData(3.0, 480)]
    public void Picture_width_follows_the_display_scaling(double scaling, int width)
    {
        var cache = NewCache();
        cache.UseScaling(scaling);
        Assert.Equal(width, cache.Width);
    }

    /// <summary>Fab sends another "featured" picture for many items from one library fetch to the next.</summary>
    [AvaloniaFact]
    public async Task Pictures_are_kept_per_item_so_a_rotated_link_is_not_downloaded_again()
    {
        string perLinkFile = Path.Combine(_folder, "0A1B2C-400.thumb"); // how earlier builds kept them
        File.WriteAllText(perLinkFile, "old");
        var server = new FakePictureServer();

        Assert.NotNull(await NewCache(server).Get("https://media.fab.com/image_previews/gallery_images/a/one.jpg", "fab:item").Picture);
        // Next start: Fab now links another gallery picture of the same item.
        Assert.NotNull(await NewCache(server).Get("https://media.fab.com/image_previews/gallery_images/a/two.jpg", "fab:item").Picture);

        Assert.Equal(["https://media.fab.com/cdn-cgi/image/width=240/image_previews/gallery_images/a/one.jpg"], server.Requests);
        await WaitUntil(() => !File.Exists(perLinkFile));

        // A month on, it's fetched again, so a seller's new picture does show up.
        string kept = Directory.GetFiles(Path.Combine(_folder, "items")).Single();
        File.SetLastWriteTimeUtc(kept, DateTime.UtcNow - ThumbnailCache.MaxAge - TimeSpan.FromHours(1));
        Assert.NotNull(await NewCache(server).Get("https://media.fab.com/image_previews/gallery_images/a/two.jpg", "fab:item").Picture);
        Assert.Equal("https://media.fab.com/cdn-cgi/image/width=240/image_previews/gallery_images/a/two.jpg", server.Requests[^1]);
    }

    // Each of these used to be able to throw out of an async void and end the app (the 'local' scheme did).
    [AvaloniaTheory]
    [InlineData(@"local://Z:\nowhere\missing.png")]
    [InlineData("local://")]
    [InlineData("ftp://example.com/picture.png")]
    [InlineData("not a url at all")]
    [InlineData("")]
    public async Task Unusable_thumbnails_give_a_placeholder_instead_of_an_exception(string url) =>
        Assert.Null(await NewCache().Get(url).Picture);

    [AvaloniaFact]
    public async Task Pictures_are_only_fetched_over_https()
    {
        var server = new FakePictureServer();
        Assert.Null(await NewCache(server).Get("http://media.fab.com/image_previews/gallery_images/a/one.jpg").Picture);
        Assert.Empty(server.Requests);
    }

    /// <summary>A server sending more than any preview picture is cut off, whether or not it says how much it sends.</summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_download_bigger_than_any_preview_is_cut_off(bool saysHowBig)
    {
        var server = new FakePictureServer { SaysHowBig = saysHowBig };
        const string url = "https://example.com/picture.png";

        var fits = new ThumbnailCache(new HttpClient(server), _folder) { MaxDownloadBytes = TinyPNG.Length };
        Assert.NotNull(await fits.Get(url, "fits").Picture);
        var tooBig = new ThumbnailCache(new HttpClient(server), _folder) { MaxDownloadBytes = TinyPNG.Length - 1 };
        Assert.Null(await tooBig.Get(url, "too big").Picture);
    }

    /// <summary>Decoding can need the whole picture in memory, so the size a header claims is checked first.</summary>
    [AvaloniaFact]
    public async Task A_picture_claiming_a_huge_size_is_left_as_a_placeholder()
    {
        string folder = Directory.CreateDirectory(Path.Combine(_folder, "Download")).FullName;
        string huge = Path.Combine(folder, "Huge.png"), small = Path.Combine(folder, "Small.png");
        await File.WriteAllBytesAsync(huge, BlackAndWhitePNG(side: ThumbnailCache.MaxSide + 4000, rows: 1));
        await File.WriteAllBytesAsync(small, BlackAndWhitePNG(side: 64, rows: 64));

        var cache = NewCache();
        Assert.NotNull(await cache.Get("local://" + small).Picture); // the same kind of file, sized like a preview
        Assert.Null(await cache.Get("local://" + huge).Picture);
    }

    /// <summary>A square PNG with the first <paramref name="rows"/> rows of pixels; its header may claim more than the file holds.</summary>
    private static byte[] BlackAndWhitePNG(int side, int rows)
    {
        var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, side);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), side);
        header[8] = 1; // bits per pixel; the rest (greyscale, no interlacing) are zeros
        Chunk("IHDR", header);
        var pixels = new MemoryStream();
        using (var zlib = new ZLibStream(pixels, CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(new byte[rows * (1 + (side + 7) / 8)]); // each row: a filter byte, then the pixels
        Chunk("IDAT", pixels.ToArray());
        Chunk("IEND", []);
        return png.ToArray();

        void Chunk(string type, byte[] data)
        {
            byte[] typed = [.. Encoding.ASCII.GetBytes(type), .. data];
            var number = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
            png.Write(number);
            png.Write(typed);
            BinaryPrimitives.WriteUInt32BigEndian(number, CRC32(typed));
            png.Write(number);
        }
    }

    private static uint CRC32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }

    /// <summary>A decoded picture is native memory the GC doesn't see, so the cache frees the ones no tile shows itself.</summary>
    [AvaloniaFact]
    public async Task Pictures_no_tile_shows_are_kept_for_scrolling_back_then_freed()
    {
        string file = Path.Combine(Directory.CreateDirectory(Path.Combine(_folder, "Download")).FullName, "Thumb.png");
        await File.WriteAllBytesAsync(file, TinyPNG);
        var cache = NewCache();

        // Shown, then scrolled away: still in memory, and scrolling back gets the same picture without loading it again.
        var first = cache.Get("local://" + file, "item 0");
        var picture = await first.Picture;
        cache.Release(first);
        var again = cache.Get("local://" + file, "item 0");
        Assert.Same(picture, await again.Picture);

        // Scrolled away for good, with many more shown since: freed. Pictures still on screen never are.
        cache.Release(again);
        var shown = cache.Get("local://" + file, "on screen");
        var onScreen = await shown.Picture;
        for (int i = 1; i <= 100; i++)
        {
            var use = cache.Get("local://" + file, $"item {i}");
            await use.Picture;
            cache.Release(use);
        }
        Assert.ThrowsAny<Exception>(() => picture!.PixelSize); // disposed
        Assert.True(onScreen!.PixelSize.Width > 0);
        Assert.NotSame(picture, await cache.Get("local://" + file, "item 0").Picture); // loads afresh
    }

    [AvaloniaFact]
    public async Task A_picture_whose_tile_scrolled_away_before_it_loaded_is_not_downloaded()
    {
        var server = new FakePictureServer { Delay = TimeSpan.FromMilliseconds(200) };
        var cache = NewCache(server);

        // Six downloads at a time: the seventh waits, and its tile is gone by the time it could start.
        var shown = Enumerable.Range(0, 6).Select(i => cache.Get($"https://media.fab.com/image_previews/{i}.jpg", $"item {i}")).ToList();
        var scrolledPast = cache.Get("https://media.fab.com/image_previews/7.jpg", "item 7");
        cache.Release(scrolledPast);

        Assert.All(await Task.WhenAll(shown.Select(s => s.Picture)), Assert.NotNull);
        Assert.Null(await scrolledPast.Picture);
        Assert.DoesNotContain(server.Requests, r => r.EndsWith("/7.jpg", StringComparison.Ordinal));
    }

    /// <summary>
    /// Fab gives each picture's upload date: one uploaded after ours was saved is the creator's new picture, fetched even
    /// while the old one is on screen; an older one Fab swaps in changes nothing.
    /// </summary>
    [AvaloniaFact]
    public async Task A_picture_the_creator_changed_is_fetched_again()
    {
        var server = new FakePictureServer();
        var longAgo = DateTimeOffset.UtcNow - TimeSpan.FromDays(10);
        Assert.NotNull(await NewCache(server).Get("https://media.fab.com/image_previews/a/one.jpg", "fab:item", longAgo).Picture);
        string kept = Directory.GetFiles(Path.Combine(_folder, "items")).Single();
        File.SetLastWriteTimeUtc(kept, DateTime.UtcNow - TimeSpan.FromDays(2)); // saved two days ago

        // Next start: the picture comes from disk, and a tile shows it.
        var cache = NewCache(server);
        var shown = cache.Get("https://media.fab.com/image_previews/a/one.jpg", "fab:item", longAgo);
        var old = await shown.Picture;
        Assert.Single(server.Requests);

        // The library is refreshed: the creator uploaded a new picture yesterday.
        var yesterday = DateTimeOffset.UtcNow - TimeSpan.FromDays(1);
        var changed = await cache.Get("https://media.fab.com/image_previews/a/new.jpg", "fab:item", yesterday).Picture;
        Assert.NotNull(changed);
        Assert.NotSame(old, changed);
        Assert.Equal(2, server.Requests.Count);
        Assert.EndsWith("/new.jpg", server.Requests[^1], StringComparison.Ordinal);

        // The old one is freed once its tile is gone; the new one stays, whatever older picture Fab names next.
        cache.Release(shown);
        Assert.ThrowsAny<Exception>(() => old!.PixelSize);
        Assert.Same(changed, await cache.Get("https://media.fab.com/image_previews/a/two.jpg", "fab:item", longAgo).Picture);
        Assert.Same(changed, await cache.Get("https://media.fab.com/image_previews/a/new.jpg", "fab:item", yesterday).Picture);
        Assert.Equal(2, server.Requests.Count);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++)
            await Task.Delay(20);
        Assert.True(condition());
    }

    private sealed class FakePictureServer : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public TimeSpan Delay { get; init; }

        /// <summary>False sends no length, as a server streaming its answer does.</summary>
        public bool SaysHowBig { get; init; } = true;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
                Requests.Add(request.RequestUri!.AbsoluteUri);
            await Task.Delay(Delay, cancellationToken);
            var content = new ByteArrayContent(TinyPNG);
            if (!SaysHowBig)
                content.Headers.ContentLength = null;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }
}
