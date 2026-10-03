using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Unvault.Core.Chunks;
using Unvault.Core.Epic;
using Unvault.Core.Install;
using Unvault.Core.Manifests;

namespace Unvault.Core.Tests;

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
            new Installer(new HttpClient(broken)) { MaxParallelDownloads = 1, RetryBaseDelay = TimeSpan.FromMilliseconds(1) }
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

    /// <summary>Serves chunk files by the GUID in their URL; can simulate dead hosts and missing chunks.</summary>
    private sealed class FakeCDN(Dictionary<EpicGUID, byte[]> chunks) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public HashSet<string> DownHosts { get; } = [];
        public HashSet<EpicGUID> Unavailable { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.ToString();
            lock (Requests)
                Requests.Add(url);

            if (DownHosts.Contains(request.RequestUri.Host))
                throw new HttpRequestException("Host unreachable");

            foreach (var (guid, file) in chunks)
            {
                if (url.Contains(guid.ToString()) && !Unavailable.Contains(guid))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(file) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
