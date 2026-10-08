using System.Net;
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

        Assert.NotNull(await NewCache().GetAsync("local://" + file));
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
        var results = await Task.WhenAll(urls.Select(url => cache.GetAsync(url)));

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

        Assert.NotNull(await NewCache(server).GetAsync("https://media.fab.com/image_previews/gallery_images/a/one.jpg", "fab:item"));
        // Next start: Fab now links another gallery picture of the same item.
        Assert.NotNull(await NewCache(server).GetAsync("https://media.fab.com/image_previews/gallery_images/a/two.jpg", "fab:item"));

        Assert.Equal(["https://media.fab.com/cdn-cgi/image/width=240/image_previews/gallery_images/a/one.jpg"], server.Requests);
        await WaitUntil(() => !File.Exists(perLinkFile));

        // A month on, it's fetched again, so a seller's new picture does show up.
        string kept = Directory.GetFiles(Path.Combine(_folder, "items")).Single();
        File.SetLastWriteTimeUtc(kept, DateTime.UtcNow - ThumbnailCache.MaxAge - TimeSpan.FromHours(1));
        Assert.NotNull(await NewCache(server).GetAsync("https://media.fab.com/image_previews/gallery_images/a/two.jpg", "fab:item"));
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
        Assert.Null(await NewCache().GetAsync(url));

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++)
            await Task.Delay(20);
        Assert.True(condition());
    }

    private sealed class FakePictureServer : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
                Requests.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(TinyPNG) });
        }
    }
}
