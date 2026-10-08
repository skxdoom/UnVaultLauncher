using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using UnVault.Core.Chunks;
using UnVault.Core.Epic;
using UnVault.Core.Fab;
using UnVault.Core.Install;
using UnVault.Core.Manifests;
using UnVault.Core.Util;

namespace UnVault.Core.Tests;

/// <summary>Builds .chunk files the way Epic's CDN serves them, for decoder and installer tests.</summary>
internal static class ChunkFactory
{
    public static (ChunkInfo Info, byte[] File) Create(EpicGUID guid, byte[] data, bool compress = true, byte[]? aesKey = null, EpicGUID secret = default)
    {
        byte[] sha1 = SHA1.HashData(data);
        byte[] payload = data;
        byte storedAs = 0;

        if (compress)
        {
            using var output = new MemoryStream();
            using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
                zlib.Write(data);
            payload = output.ToArray();
            storedAs |= 1;
        }

        byte[] tag = new byte[16];
        if (aesKey is not null)
        {
            var ciphertext = new byte[payload.Length];
            using var aes = new AesGcm(aesKey, 16);
            aes.Encrypt(sha1.AsSpan(0, 12), payload, ciphertext, tag);
            payload = ciphertext;
            storedAs |= 2;
        }

        const int headerSize = 98; // v4
        var file = new byte[headerSize + payload.Length];
        var span = file.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span[0..], ChunkDecoder.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 4);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)payload.Length);
        guid.ToBytes().CopyTo(span[16..]);
        BinaryPrimitives.WriteUInt64LittleEndian(span[32..], 0x1234);
        span[40] = storedAs;
        sha1.CopyTo(span[41..]);
        span[61] = 2; // hash type: SHA-1
        BinaryPrimitives.WriteUInt32LittleEndian(span[62..], (uint)data.Length);
        secret.ToBytes().CopyTo(span[66..]);
        tag.CopyTo(span[82..]);
        payload.CopyTo(span[headerSize..]);

        var info = new ChunkInfo { GUID = guid, Hash = 0x1234, SHA1 = sha1, GroupNum = 1, WindowSize = (uint)data.Length, FileSize = file.Length, SecretGUID = secret };
        return (info, file);
    }
}

public class ChunkDecoderTests
{
    [Fact]
    public void Decodes_compressed_chunk()
    {
        var data = RandomNumberGenerator.GetBytes(5000);
        var (info, file) = ChunkFactory.Create(new EpicGUID(1, 2, 3, 4), data);
        var destination = new byte[data.Length];

        int length = ChunkDecoder.Decode(file, info, destination);

        Assert.Equal(data, destination[..length]);
    }

    [Fact]
    public void Decodes_encrypted_chunk_with_key_from_secrets()
    {
        var data = RandomNumberGenerator.GetBytes(3000);
        var key = RandomNumberGenerator.GetBytes(32);
        var secret = new EpicGUID(0xAA, 0xBB, 0xCC, 0xDD);
        var (info, file) = ChunkFactory.Create(new EpicGUID(9, 9, 9, 9), data, aesKey: key, secret: secret);
        var secrets = new Dictionary<string, string> { [secret.ToString()] = Convert.ToHexString(key) };
        var destination = new byte[data.Length];

        int length = ChunkDecoder.Decode(file, info, destination, secrets);

        Assert.Equal(data, destination[..length]);
    }

    [Fact]
    public void Rejects_corrupted_data()
    {
        var (info, file) = ChunkFactory.Create(new EpicGUID(1, 2, 3, 4), RandomNumberGenerator.GetBytes(1000), compress: false);
        file[^1] ^= 0xFF;

        Assert.Throws<ChunkFormatException>(() => ChunkDecoder.Decode(file, info, new byte[1000]));
    }

    [Fact]
    public void Rejects_a_different_chunk()
    {
        var (_, file) = ChunkFactory.Create(new EpicGUID(1, 2, 3, 4), new byte[100]);
        var (otherInfo, _) = ChunkFactory.Create(new EpicGUID(5, 6, 7, 8), new byte[100]);

        Assert.Throws<ChunkFormatException>(() => ChunkDecoder.Decode(file, otherInfo, new byte[100]));
    }

    /// <summary>However a download got damaged, it's reported the same way, so the installer fetches it again.</summary>
    [Fact]
    public void Damage_of_any_kind_is_a_format_error()
    {
        var data = RandomNumberGenerator.GetBytes(5000);
        var (info, file) = ChunkFactory.Create(new EpicGUID(1, 2, 3, 4), data);
        var key = RandomNumberGenerator.GetBytes(32);
        var secret = new EpicGUID(0xAA, 0xBB, 0xCC, 0xDD);
        var (encryptedInfo, encrypted) = ChunkFactory.Create(new EpicGUID(9, 9, 9, 9), data, aesKey: key, secret: secret);
        var secrets = new Dictionary<string, string> { [secret.ToString()] = Convert.ToHexString(key) };

        byte[] Garbled(byte[] original)
        {
            byte[] copy = [.. original];
            copy.AsSpan(110, 40).Fill(0x5A);
            return copy;
        }

        Assert.Throws<ChunkFormatException>(() => ChunkDecoder.Decode(file[..50], info, new byte[5000])); // header cut off
        Assert.Throws<ChunkFormatException>(() => ChunkDecoder.Decode(Garbled(file), info, new byte[5000])); // won't inflate
        Assert.Throws<ChunkFormatException>(() => ChunkDecoder.Decode(Garbled(encrypted), encryptedInfo, new byte[5000], secrets)); // won't decrypt
    }
}

public sealed class InstallerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"unvault-install-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (!Directory.Exists(_dir))
            return;
        foreach (var file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Installs_files_that_share_and_split_chunks()
    {
        var (manifest, expected, chunkFiles) = BuildTestBuild();
        var handler = new FakeCDN(chunkFiles);
        var plan = InstallPlan.Create(manifest, manifest.Files);

        await new Installer(new HttpClient(handler)).InstallAsync(plan, _dir, [new ChunkSource("https://cdn.test/CloudDir")], new Dictionary<string, string>(), new InstallStatus(), default);

        foreach (var (name, content) in expected)
            Assert.Equal(content, File.ReadAllBytes(Path.Combine(_dir, name)));
        Assert.True((File.GetAttributes(Path.Combine(_dir, "Engine/ReadOnly.txt")) & FileAttributes.ReadOnly) != 0);
        Assert.Equal(2, handler.Requests.Distinct().Count()); // each chunk fetched once
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_dir, ".unvault"), "*.journal")); // journal removed on success
    }

    [Fact]
    public async Task Resumes_without_refetching_finished_chunks_and_fails_over_between_CDNs()
    {
        var (manifest, expected, chunkFiles) = BuildTestBuild();
        var plan = InstallPlan.Create(manifest, manifest.Files);

        // First run: chunk B is unavailable everywhere, so the install fails after A is done.
        var broken = new FakeCDN(chunkFiles) { Unavailable = { plan.Chunks[1].Info.GUID } };
        await Assert.ThrowsAsync<InstallException>(() =>
            new Installer(new HttpClient(broken)) { MaxParallelDownloads = 1, RetryBaseDelay = TimeSpan.FromMilliseconds(1), GiveUpAfter = TimeSpan.Zero }
                .InstallAsync(plan, _dir, [new ChunkSource("https://cdn.test/CloudDir")], new Dictionary<string, string>(), new InstallStatus(), default));

        // Second run: the first CDN is down entirely; the second works. Only chunk B should be fetched.
        var healthy = new FakeCDN(chunkFiles) { DownHosts = { "dead.test" } };
        await new Installer(new HttpClient(healthy)) { RetryBaseDelay = TimeSpan.FromMilliseconds(1) }.InstallAsync(plan, _dir,
            [new ChunkSource("https://dead.test/CloudDir"), new ChunkSource("https://cdn.test/CloudDir")],
            new Dictionary<string, string>(), new InstallStatus(), default);

        foreach (var (name, content) in expected)
            Assert.Equal(content, File.ReadAllBytes(Path.Combine(_dir, name)));
        Assert.DoesNotContain(healthy.Requests, r => r.Contains(plan.Chunks[0].Info.GUID.ToString()) && r.Contains("cdn.test"));
    }

    [Fact]
    public async Task Resuming_downloads_again_what_finished_chunks_wrote_into_files_deleted_since()
    {
        var (manifest, expected, chunkFiles) = BuildTestBuild();
        var plan = InstallPlan.Create(manifest, manifest.Files);
        ChunkSource[] sources = [new("https://cdn.test/CloudDir")];

        // Stops after chunk A (B is unavailable); then Big.bin, which A filled, is deleted, as unticking a component does.
        var broken = new FakeCDN(chunkFiles) { Unavailable = { plan.Chunks[1].Info.GUID } };
        await Assert.ThrowsAsync<InstallException>(() =>
            new Installer(new HttpClient(broken)) { MaxParallelDownloads = 1, RetryBaseDelay = TimeSpan.FromMilliseconds(1), GiveUpAfter = TimeSpan.Zero }
                .InstallAsync(plan, _dir, sources, new Dictionary<string, string>(), new InstallStatus(), default));
        File.Delete(Path.Combine(_dir, "Engine/Big.bin"));

        // Ticked again, same plan: the journal says A is done, but its file is gone, so A is fetched again.
        var cdn = new FakeCDN(chunkFiles);
        await new Installer(new HttpClient(cdn)).InstallAsync(plan, _dir, sources, new Dictionary<string, string>(), new InstallStatus(), default);

        foreach (var (name, content) in expected)
            Assert.Equal(content, File.ReadAllBytes(Path.Combine(_dir, name)));
        Assert.Contains(cdn.Requests, r => r.Contains(plan.Chunks[0].Info.GUID.ToString()));
    }

    [Fact]
    public void Install_record_files_are_replaced_whole_and_an_unchanged_manifest_is_left_alone()
    {
        byte[] manifest = Encoding.UTF8.GetBytes("manifest v1");
        new InstallRecord { AppName = "UE_5.7" }.Save(_dir, manifest);
        string manifestPath = InstallRecord.ManifestPath(_dir);
        var written = File.GetLastWriteTimeUtc(manifestPath);
        Thread.Sleep(20);

        // Changing components saves the same build again: the record changes, the manifest (the only copy) isn't rewritten.
        new InstallRecord { AppName = "UE_5.7", InstallTags = ["engine_source"] }.Save(_dir, manifest);
        Assert.Equal(written, File.GetLastWriteTimeUtc(manifestPath));
        Assert.Equal(["engine_source"], InstallRecord.TryRead(_dir)!.InstallTags);

        new InstallRecord { AppName = "UE_5.7" }.Save(_dir, Encoding.UTF8.GetBytes("manifest v2"));
        Assert.Equal("manifest v2", File.ReadAllText(manifestPath));
        Assert.Empty(Directory.EnumerateFiles(InstallJournal.DirectoryFor(_dir), "*.tmp"));
    }

    [Fact]
    public async Task Installs_into_and_cleans_a_folder_typed_with_a_trailing_separator()
    {
        var (manifest, expected, chunkFiles) = BuildTestBuild();
        string typed = _dir + Path.DirectorySeparatorChar; // as tab completion in a terminal leaves it

        await new Installer(new HttpClient(new FakeCDN(chunkFiles))).InstallAsync(InstallPlan.Create(manifest, manifest.Files), typed,
            [new ChunkSource("https://cdn.test/CloudDir")], new Dictionary<string, string>(), new InstallStatus(), default);
        foreach (var (name, content) in expected)
            Assert.Equal(content, File.ReadAllBytes(Path.Combine(_dir, name)));

        File.SetAttributes(Path.Combine(_dir, "Engine/ReadOnly.txt"), FileAttributes.Normal);
        var cleaned = InstallCleaner.DeleteFiles(typed, manifest.Files);
        Assert.Equal((4, 1), (cleaned.FilesDeleted, cleaned.FoldersRemoved)); // Engine\ goes, the install folder stays
        Assert.True(Directory.Exists(_dir));
    }

    [Theory]
    [InlineData(@"E:\", @"E:\UE_5.8\Engine", true)] // a drive root
    [InlineData(@"E:\Engines\UE_5.8\", @"E:\Engines\UE_5.8\Engine", true)]
    [InlineData(@"E:\Engines\UE_5.8", @"E:\Engines\UE_5.8\Engine", true)]
    [InlineData(@"E:\Engines\UE_5.8", @"E:\Engines\UE_5.8", false)] // the folder itself
    [InlineData(@"E:\Engines\UE_5", @"E:\Engines\UE_5.8\Engine", false)] // a folder whose name merely starts the same
    public void Knows_what_is_inside_a_folder(string root, string path, bool inside) =>
        Assert.Equal(inside, ContainedPath.IsInside(root, path));

    [Fact]
    public void Refuses_manifest_paths_that_leave_their_folder()
    {
        Assert.Throws<IOException>(() => ContainedPath.Resolve(@"E:\Engines\UE_5.8", @"..\UE_5.7\Engine\x.dll"));
        Assert.Throws<IOException>(() => ContainedPath.Resolve(@"E:\Engines\UE_5.8", @"C:\Windows\x.dll"));
        Assert.Equal(@"E:\Engine\x.dll", ContainedPath.Resolve(@"E:\", "Engine/x.dll"));
    }

    [Fact]
    public async Task A_component_change_that_failed_runs_again_and_resumes()
    {
        // Core is always there; "old" and "extra" are components. Extra spans two chunks, so a run can stop between them.
        byte[] core = RandomNumberGenerator.GetBytes(1000), old = RandomNumberGenerator.GetBytes(1000), extra = RandomNumberGenerator.GetBytes(4000);
        var (coreChunk, coreFile) = ChunkFactory.Create(new EpicGUID(0x10, 0, 0, 0), core);
        var (oldChunk, oldFile) = ChunkFactory.Create(new EpicGUID(0x20, 0, 0, 0), old);
        var (extraFirst, extraFirstFile) = ChunkFactory.Create(new EpicGUID(0x30, 0, 0, 0), extra[..2000]);
        var (extraSecond, extraSecondFile) = ChunkFactory.Create(new EpicGUID(0x31, 0, 0, 0), extra[2000..]);
        var chunkFiles = new Dictionary<EpicGUID, byte[]>
        {
            [coreChunk.GUID] = coreFile, [oldChunk.GUID] = oldFile, [extraFirst.GUID] = extraFirstFile, [extraSecond.GUID] = extraSecondFile,
        };
        FileManifest Entry(string name, byte[] content, string? tag, params ChunkPart[] parts) => new()
        {
            Filename = name, SHA1 = SHA1.HashData(content), FileSize = content.Length, ChunkParts = parts, InstallTags = tag is null ? [] : [tag],
        };
        var manifest = new Manifest
        {
            Version = 21,
            Meta = new ManifestMeta { FeatureLevel = 21, AppName = "UE_9.9", BuildVersion = "9.9.0-1" },
            Chunks = [coreChunk, oldChunk, extraFirst, extraSecond],
            Files =
            [
                Entry("Engine/Core.bin", core, null, new ChunkPart(coreChunk.GUID, 0, 1000)),
                Entry("Engine/Old.bin", old, "old", new ChunkPart(oldChunk.GUID, 0, 1000)),
                Entry("Engine/Extra.bin", extra, "extra", new ChunkPart(extraFirst.GUID, 0, 2000), new ChunkPart(extraSecond.GUID, 0, 2000)),
            ],
            CustomFields = new Dictionary<string, string>(),
        };
        ChunkSource[] sources = [new("https://cdn.test/CloudDir")];
        static HashSet<string> Tags(params string[] tags) => new(tags, StringComparer.Ordinal);

        await new Installer(new HttpClient(new FakeCDN(chunkFiles))).InstallAsync(InstallPlan.Create(manifest, manifest.SelectFiles(Tags("old"))), _dir,
            sources, new Dictionary<string, string>(), new InstallStatus(), default);
        string manifestPath = Path.Combine(_dir, "build.manifest");
        File.WriteAllBytes(manifestPath, [1, 2, 3]); // only copied into the record, never parsed here
        var install = new ExistingInstall("UE_9.9", _dir, manifest, manifestPath, Tags("old"), sources, "test");

        // Swapping "old" for "extra" stops after Extra's first chunk.
        var plan = InstallWorkflow.PlanModify(install, Tags("extra"));
        var broken = new FakeCDN(chunkFiles) { Unavailable = { extraSecond.GUID } };
        await Assert.ThrowsAsync<InstallException>(() => InstallWorkflow.ApplyModifyAsync(plan,
            new Installer(new HttpClient(broken)) { MaxParallelDownloads = 1, RetryBaseDelay = TimeSpan.FromMilliseconds(1), GiveUpAfter = TimeSpan.Zero }, new InstallStatus(), default));

        // "old" is gone from disk and from the record, and "extra" isn't claimed yet: the same change still has work to do.
        var record = InstallRecord.TryRead(_dir)!;
        Assert.Empty(record.InstallTags);
        Assert.False(File.Exists(Path.Combine(_dir, "Engine/Old.bin")));
        var again = InstallWorkflow.PlanModify(install with { InstallTags = record.InstallTags.ToHashSet(StringComparer.Ordinal) }, Tags("extra"));
        Assert.Equal(plan.ToAdd!.ID, again.ToAdd!.ID); // the same download, so its journal applies

        var cdn = new FakeCDN(chunkFiles);
        await InstallWorkflow.ApplyModifyAsync(again, new Installer(new HttpClient(cdn)), new InstallStatus(), default);

        Assert.Equal(["extra"], InstallRecord.TryRead(_dir)!.InstallTags);
        Assert.Equal(extra, File.ReadAllBytes(Path.Combine(_dir, "Engine/Extra.bin")));
        Assert.DoesNotContain(cdn.Requests, r => r.Contains(extraFirst.GUID.ToString())); // resumed
    }

    [Theory]
    [InlineData("server error")]
    [InlineData("busy")]
    [InlineData("dropped connection")]
    [InlineData("cut short")]
    [InlineData("garbled")]
    [InlineData("stalled")]
    public async Task Rides_out_trouble_from_the_download_server(string trouble)
    {
        var (manifest, expected, chunkFiles) = BuildTestBuild();
        // The first two tries of every chunk go wrong; the third works.
        var cdn = new FakeCDN(chunkFiles)
        {
            Trouble = (_, attempt, file) => attempt >= 2 ? null : trouble switch
            {
                "server error" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                "busy" => new HttpResponseMessage(HttpStatusCode.TooManyRequests),
                "dropped connection" => throw new HttpRequestException("The connection was reset."),
                "cut short" => Served(file[..(file.Length / 2)]),
                "garbled" => Served([.. file[..110], .. Enumerable.Repeat((byte)0x5A, file.Length - 110)]),
                _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream(file[..(file.Length / 2)])) },
            },
        };
        var status = new InstallStatus();

        await new Installer(new HttpClient(cdn)) { RetryBaseDelay = TimeSpan.FromMilliseconds(1), StallTimeout = TimeSpan.FromMilliseconds(200) }
            .InstallAsync(InstallPlan.Create(manifest, manifest.Files), _dir, [new ChunkSource("https://cdn.test/CloudDir")],
                new Dictionary<string, string>(), status, default);

        foreach (var (name, content) in expected)
            Assert.Equal(content, File.ReadAllBytes(Path.Combine(_dir, name)));
        Assert.Equal(4, status.Retries); // two chunks, two retries each
    }

    [Fact]
    public async Task Gives_up_on_a_chunk_that_keeps_failing_for_long_enough()
    {
        var (manifest, _, chunkFiles) = BuildTestBuild();
        var cdn = new FakeCDN(chunkFiles) { Trouble = (_, _, _) => new HttpResponseMessage(HttpStatusCode.BadGateway) };
        var giveUpAfter = TimeSpan.FromMilliseconds(300);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<InstallException>(() =>
            new Installer(new HttpClient(cdn)) { MaxParallelDownloads = 1, RetryBaseDelay = TimeSpan.FromMilliseconds(1), MaxRetryDelay = TimeSpan.FromMilliseconds(20), GiveUpAfter = giveUpAfter }
                .InstallAsync(InstallPlan.Create(manifest, manifest.Files), _dir, [new ChunkSource("https://cdn.test/CloudDir")],
                    new Dictionary<string, string>(), new InstallStatus(), default));

        Assert.True(clock.Elapsed >= giveUpAfter);
        Assert.True(cdn.Requests.Count > 3); // kept trying meanwhile
        Assert.Contains("502", error.Message);
    }

    [Fact]
    public async Task Stops_at_once_when_every_server_refuses_a_chunk()
    {
        var (manifest, _, chunkFiles) = BuildTestBuild();
        // Signed download links that have expired: asking again won't help.
        var cdn = new FakeCDN(chunkFiles) { Trouble = (_, _, _) => new HttpResponseMessage(HttpStatusCode.Forbidden) };
        var status = new InstallStatus();

        var error = await Assert.ThrowsAsync<InstallException>(() =>
            new Installer(new HttpClient(cdn)) { MaxParallelDownloads = 1, RetryBaseDelay = TimeSpan.FromMilliseconds(1) }
                .InstallAsync(InstallPlan.Create(manifest, manifest.Files), _dir,
                    [new ChunkSource("https://one.test/CloudDir"), new ChunkSource("https://two.test/CloudDir")],
                    new Dictionary<string, string>(), status, default));

        Assert.Contains("expired", error.Message);
        Assert.Equal(2, cdn.Requests.Count); // each server asked once
        Assert.Equal(0, status.Retries);
    }

    [Fact]
    public async Task A_server_refusing_a_chunk_hands_it_to_the_next()
    {
        var (manifest, expected, chunkFiles) = BuildTestBuild();
        var cdn = new FakeCDN(chunkFiles) { Trouble = (url, _, _) => url.Contains("one.test") ? new HttpResponseMessage(HttpStatusCode.Forbidden) : null };

        await new Installer(new HttpClient(cdn)).InstallAsync(InstallPlan.Create(manifest, manifest.Files), _dir,
            [new ChunkSource("https://one.test/CloudDir"), new ChunkSource("https://two.test/CloudDir")],
            new Dictionary<string, string>(), new InstallStatus(), default);

        foreach (var (name, content) in expected)
            Assert.Equal(content, File.ReadAllBytes(Path.Combine(_dir, name)));
    }

    [Fact]
    public async Task Cancelling_partway_then_running_again_fetches_only_what_is_missing()
    {
        var (manifest, expected, chunkFiles) = BuildTestBuild();
        var plan = InstallPlan.Create(manifest, manifest.Files);
        string second = plan.Chunks[1].Info.GUID.ToString();
        ChunkSource[] sources = [new("https://cdn.test/CloudDir")];

        // The user cancels while the second chunk is on its way.
        using var cancel = new CancellationTokenSource();
        var first = new FakeCDN(chunkFiles)
        {
            Trouble = (url, _, file) =>
            {
                if (!url.Contains(second))
                    return null;
                cancel.Cancel();
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream(file[..10])) };
            },
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Installer(new HttpClient(first)) { MaxParallelDownloads = 1 }
                .InstallAsync(plan, _dir, sources, new Dictionary<string, string>(), new InstallStatus(), cancel.Token));

        var again = new FakeCDN(chunkFiles);
        await new Installer(new HttpClient(again)).InstallAsync(plan, _dir, sources, new Dictionary<string, string>(), new InstallStatus(), default);

        foreach (var (name, content) in expected)
            Assert.Equal(content, File.ReadAllBytes(Path.Combine(_dir, name)));
        Assert.Contains(second, Assert.Single(again.Requests)); // the first chunk was already written
    }

    [Fact]
    public async Task Verifier_finds_missing_resized_and_changed_files()
    {
        var (manifest, _, chunkFiles) = BuildTestBuild();
        var plan = InstallPlan.Create(manifest, manifest.Files);
        await new Installer(new HttpClient(new FakeCDN(chunkFiles))).InstallAsync(plan, _dir, [new ChunkSource("https://cdn.test/CloudDir")], new Dictionary<string, string>(), new InstallStatus(), default);

        File.Delete(Path.Combine(_dir, "Engine/Small.txt"));
        File.WriteAllBytes(Path.Combine(_dir, "Engine/Empty.txt"), [1]);
        var big = Path.Combine(_dir, "Engine/Big.bin");
        var bytes = File.ReadAllBytes(big);
        bytes[100] ^= 0xFF;
        File.WriteAllBytes(big, bytes);

        var bad = await Verifier.FindBadFilesAsync(manifest.Files, _dir, new InstallStatus());

        Assert.Equal(
            [("Engine/Big.bin", FileProblem.WrongHash), ("Engine/Empty.txt", FileProblem.WrongSize), ("Engine/Small.txt", FileProblem.Missing)],
            bad.Select(b => (b.File.Filename, b.Problem)));
    }

    /// <summary>
    /// Two chunks, four files: Big.bin spans both chunks, Small.txt and ReadOnly.txt share the tail of
    /// chunk B with Big.bin's end, Empty.txt has no data.
    /// </summary>
    [Fact]
    public async Task An_update_downloads_only_changed_files_and_drops_removed_ones()
    {
        var (v1, expected, chunkFiles) = BuildTestBuild();
        var cdn = new FakeCDN(chunkFiles);
        var installer = new Installer(new HttpClient(cdn));
        ChunkSource[] sources = [new("https://cdn.test/CloudDir")];
        await installer.InstallAsync(InstallPlan.Create(v1, v1.Files), _dir, sources, new Dictionary<string, string>(), new InstallStatus(), default);
        var bigWritten = File.GetLastWriteTimeUtc(Path.Combine(_dir, "Engine/Big.bin"));

        // The next build: Small.txt changed and New.txt added (both in a new chunk), Empty.txt dropped, the rest as before.
        byte[] dataC = RandomNumberGenerator.GetBytes(1000);
        var (chunkC, fileC) = ChunkFactory.Create(new EpicGUID(0xC, 0, 0, 0), dataC);
        chunkFiles[chunkC.GUID] = fileC;
        FileManifest Changed(string name, byte[] content, uint offset) => new()
        {
            Filename = name, SHA1 = SHA1.HashData(content), FileSize = content.Length,
            ChunkParts = [new ChunkPart(chunkC.GUID, offset, (uint)content.Length)],
        };
        var v2 = new Manifest
        {
            Version = 21,
            Meta = new ManifestMeta { FeatureLevel = 21, AppName = "Test", BuildVersion = "2.0" },
            Chunks = [.. v1.Chunks, chunkC],
            Files = [v1.Files[0], Changed("Engine/Small.txt", dataC[..400], 0), v1.Files[2], Changed("Engine/New.txt", dataC[400..], 400)],
            CustomFields = new Dictionary<string, string>(),
        };

        var (write, delete) = FabWorkflow.Diff(v1, v2);
        Assert.Equal(["Engine/Small.txt", "Engine/New.txt"], write.Select(f => f.Filename));
        Assert.Equal(["Engine/Empty.txt"], delete.Select(f => f.Filename));

        cdn.Requests.Clear();
        InstallCleaner.DeleteFiles(_dir, delete);
        await installer.InstallAsync(InstallPlan.Create(v2, write), _dir, sources, new Dictionary<string, string>(), new InstallStatus(), default);

        Assert.All(cdn.Requests, url => Assert.Contains(chunkC.GUID.ToString(), url)); // only the new chunk was fetched
        Assert.Equal(dataC[..400], File.ReadAllBytes(Path.Combine(_dir, "Engine/Small.txt")));
        Assert.Equal(dataC[400..], File.ReadAllBytes(Path.Combine(_dir, "Engine/New.txt")));
        Assert.False(File.Exists(Path.Combine(_dir, "Engine/Empty.txt")));
        Assert.Equal(expected["Engine/Big.bin"], File.ReadAllBytes(Path.Combine(_dir, "Engine/Big.bin")));
        Assert.Equal(bigWritten, File.GetLastWriteTimeUtc(Path.Combine(_dir, "Engine/Big.bin"))); // untouched
    }

    [Fact]
    public void Diff_without_trustworthy_hashes_rewrites_the_file()
    {
        FileManifest Entry(string name, int size, byte[] sha1) => new() { Filename = name, FileSize = size, SHA1 = sha1 };
        Manifest Build(params FileManifest[] files) => new()
        {
            Version = 21, Meta = new ManifestMeta(), Chunks = [], Files = files, CustomFields = new Dictionary<string, string>(),
        };

        var (write, delete) = FabWorkflow.Diff(Build(Entry("a", 5, []), Entry("b", 5, new byte[20])), Build(Entry("a", 5, []), Entry("b", 6, new byte[20])));
        Assert.Equal(["a", "b"], write.Select(f => f.Filename)); // no hash to compare, and a size change
        Assert.Empty(delete);
        Assert.Equal(2, FabWorkflow.Diff(null, Build(Entry("a", 5, []), Entry("b", 5, []))).Write.Count);
    }

    private static (Manifest Manifest, Dictionary<string, byte[]> Expected, Dictionary<EpicGUID, byte[]> ChunkFiles) BuildTestBuild()
    {
        var dataA = RandomNumberGenerator.GetBytes(4096);
        var dataB = RandomNumberGenerator.GetBytes(4096);
        var (chunkA, fileA) = ChunkFactory.Create(new EpicGUID(0xA, 0, 0, 0), dataA);
        var (chunkB, fileB) = ChunkFactory.Create(new EpicGUID(0xB, 0, 0, 0), dataB);

        byte[] big = [.. dataA, .. dataB.AsSpan(0, 3000)];
        byte[] small = dataB[3000..3500];
        byte[] readOnly = dataB[3500..4096];

        FileManifest File(string name, byte[] content, ChunkPart[] parts, FileFlags flags = FileFlags.None) => new()
        {
            Filename = name,
            SHA1 = SHA1.HashData(content),
            ChunkParts = parts,
            FileSize = content.Length,
            Flags = flags,
        };

        var manifest = new Manifest
        {
            Version = 21,
            Meta = new ManifestMeta { FeatureLevel = 21, AppName = "Test", BuildVersion = "1.0" },
            Chunks = [chunkA, chunkB],
            Files =
            [
                File("Engine/Big.bin", big, [new ChunkPart(chunkA.GUID, 0, 4096), new ChunkPart(chunkB.GUID, 0, 3000)]),
                File("Engine/Small.txt", small, [new ChunkPart(chunkB.GUID, 3000, 500)]),
                File("Engine/ReadOnly.txt", readOnly, [new ChunkPart(chunkB.GUID, 3500, 596)], FileFlags.ReadOnly),
                File("Engine/Empty.txt", [], []),
            ],
            CustomFields = new Dictionary<string, string>(),
        };

        var expected = new Dictionary<string, byte[]>
        {
            ["Engine/Big.bin"] = big,
            ["Engine/Small.txt"] = small,
            ["Engine/ReadOnly.txt"] = readOnly,
            ["Engine/Empty.txt"] = [],
        };
        return (manifest, expected, new Dictionary<EpicGUID, byte[]> { [chunkA.GUID] = fileA, [chunkB.GUID] = fileB });
    }

    private static HttpResponseMessage Served(byte[] content) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };

    /// <summary>Serves chunk files by the GUID in their URL; can simulate dead hosts, missing chunks and misbehaving servers.</summary>
    private sealed class FakeCDN(Dictionary<EpicGUID, byte[]> chunks) : HttpMessageHandler
    {
        private readonly Dictionary<string, int> _attempts = [];

        public List<string> Requests { get; } = [];
        public HashSet<string> DownHosts { get; } = [];
        public HashSet<EpicGUID> Unavailable { get; } = [];

        /// <summary>Given the URL, how many times this chunk was asked for before, and its real file: a response in its place, or null for the real one.</summary>
        public Func<string, int, byte[], HttpResponseMessage?>? Trouble { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.ToString();
            lock (Requests)
                Requests.Add(url);

            if (DownHosts.Contains(request.RequestUri.Host))
                throw new HttpRequestException("Host unreachable");

            foreach (var (guid, file) in chunks)
            {
                if (!url.Contains(guid.ToString()) || Unavailable.Contains(guid))
                    continue;
                int attempt;
                lock (_attempts)
                {
                    attempt = _attempts.GetValueOrDefault(guid.ToString());
                    _attempts[guid.ToString()] = attempt + 1;
                }
                return Task.FromResult(Trouble?.Invoke(url, attempt, file) ?? Served(file));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    /// <summary>Sends the start of a file, then nothing more: a connection that went quiet without closing.</summary>
    private sealed class StallingStream(byte[] start) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < start.Length)
            {
                int count = Math.Min(buffer.Length, start.Length - _position);
                start.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
