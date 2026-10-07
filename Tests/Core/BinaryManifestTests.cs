using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using UnVault.Core.Manifests;

namespace UnVault.Core.Tests;

/// <summary>Writes Epic's binary manifest format, the one engine builds come in, for test content.</summary>
internal static class BinaryManifestWriter
{
    /// <param name="newerFieldBytes">Bytes appended to every section and chunk part, as a newer format version would add fields.</param>
    /// <param name="storedAs">Overrides the header's storage flags (1 = zlib, 2 = encrypted).</param>
    public static byte[] Write(Manifest manifest, bool compress = true, byte fileListVersion = 2, int newerFieldBytes = 0, byte? storedAs = null)
    {
        byte[] body = Body(manifest, fileListVersion, newerFieldBytes);
        byte[] stored = compress ? Compress(body) : body;

        using var output = new MemoryStream();
        using var w = new BinaryWriter(output);
        w.Write(Manifest.Magic);
        w.Write(41u); // header size
        w.Write((uint)body.Length);
        w.Write((uint)stored.Length);
        w.Write(SHA1.HashData(body));
        w.Write(storedAs ?? (byte)(compress ? 1 : 0));
        w.Write(manifest.Version);
        w.Write(stored);
        return output.ToArray();
    }

    private static byte[] Body(Manifest m, byte fileListVersion, int newer)
    {
        using var body = new MemoryStream();
        using var w = new BinaryWriter(body);

        Section(w, newer, s =>
        {
            s.Write((byte)2); // data version: has BuildID and the uninstall action
            s.Write(m.Meta.FeatureLevel);
            s.Write((byte)(m.Meta.IsFileData ? 1 : 0));
            s.Write(m.Meta.AppID);
            FString(s, m.Meta.AppName);
            FString(s, m.Meta.BuildVersion);
            FString(s, m.Meta.LaunchExe);
            FString(s, m.Meta.LaunchCommand);
            s.Write((uint)m.Meta.PrereqIDs.Count);
            foreach (string id in m.Meta.PrereqIDs)
                FString(s, id);
            FString(s, m.Meta.PrereqName);
            FString(s, m.Meta.PrereqPath);
            FString(s, m.Meta.PrereqArgs);
            FString(s, m.Meta.BuildID);
            FString(s, m.Meta.UninstallActionPath);
            FString(s, m.Meta.UninstallActionArgs);
        });

        // Lists are stored field by field: every chunk's GUID, then every chunk's hash, and so on.
        Section(w, newer, s =>
        {
            s.Write((byte)0);
            s.Write((uint)m.Chunks.Count);
            foreach (var c in m.Chunks) s.Write(c.GUID.ToBytes());
            foreach (var c in m.Chunks) s.Write(c.Hash);
            foreach (var c in m.Chunks) s.Write(c.SHA1);
            foreach (var c in m.Chunks) s.Write(c.GroupNum);
            foreach (var c in m.Chunks) s.Write(c.WindowSize);
            foreach (var c in m.Chunks) s.Write(c.FileSize);
            if (m.Meta.FeatureLevel >= 22)
            {
                foreach (var c in m.Chunks) s.Write(c.SecretGUID.ToBytes());
                foreach (var _ in m.Chunks) s.Write(0u); // compressed window size
                foreach (var c in m.Chunks) s.Write(c.EncryptionTag);
            }
        });

        Section(w, newer, s =>
        {
            s.Write(fileListVersion);
            s.Write((uint)m.Files.Count);
            foreach (var f in m.Files) FString(s, f.Filename);
            foreach (var f in m.Files) FString(s, f.SymlinkTarget);
            foreach (var f in m.Files) s.Write(f.SHA1);
            foreach (var f in m.Files) s.Write((byte)f.Flags);
            foreach (var f in m.Files)
            {
                s.Write((uint)f.InstallTags.Count);
                foreach (string tag in f.InstallTags)
                    FString(s, tag);
            }
            foreach (var f in m.Files)
            {
                s.Write((uint)f.ChunkParts.Length);
                foreach (var part in f.ChunkParts)
                {
                    s.Write(28u + (uint)newer); // the part's own size: size, GUID, offset, size
                    s.Write(part.GUID.ToBytes());
                    s.Write(part.Offset);
                    s.Write(part.Size);
                    s.Write(new byte[newer]);
                }
            }
            if (fileListVersion >= 1)
            {
                foreach (var f in m.Files)
                {
                    s.Write(f.MD5 is null ? 0u : 1u);
                    if (f.MD5 is not null)
                        s.Write(f.MD5);
                }
                foreach (var f in m.Files) FString(s, f.MimeType);
            }
            if (fileListVersion >= 2)
            {
                foreach (var f in m.Files) s.Write(f.SHA256 ?? new byte[32]);
            }
        });

        Section(w, newer, s =>
        {
            s.Write((byte)0);
            s.Write((uint)m.CustomFields.Count);
            foreach (string key in m.CustomFields.Keys) FString(s, key);
            foreach (string value in m.CustomFields.Values) FString(s, value);
        });

        return body.ToArray();
    }

    /// <summary>A section: its size first (counting the size itself), its fields, then whatever a newer version appends.</summary>
    private static void Section(BinaryWriter w, int newer, Action<BinaryWriter> write)
    {
        using var content = new MemoryStream();
        using (var s = new BinaryWriter(content, Encoding.Latin1, leaveOpen: true))
            write(s);
        w.Write((uint)(4 + content.Length + newer));
        w.Write(content.ToArray());
        w.Write(new byte[newer]);
    }

    /// <summary>UE FString: the length counting a terminator; 8-bit text if every character fits, else UTF-16 with the length negated.</summary>
    private static void FString(BinaryWriter w, string value)
    {
        if (value.Length == 0)
        {
            w.Write(0);
        }
        else if (value.All(c => c < 128))
        {
            w.Write(value.Length + 1);
            w.Write(Encoding.ASCII.GetBytes(value));
            w.Write((byte)0);
        }
        else
        {
            w.Write(-(value.Length + 1));
            w.Write(Encoding.Unicode.GetBytes(value));
            w.Write((short)0);
        }
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal))
            zlib.Write(data);
        return output.ToArray();
    }
}

public class BinaryManifestTests
{
    private static readonly EpicGUID ChunkA = new(0xA, 1, 2, 3), ChunkB = new(0xB, 4, 5, 6);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Reads_every_section_compressed_or_not(bool compress)
    {
        var parsed = Manifest.Parse(BinaryManifestWriter.Write(SampleManifest(), compress));

        Assert.Equal(21u, parsed.Version);
        Assert.Equal(("TestEngine", "5.7.0-48201490+++Test+Release-5.7", 21u, "Engine/Binaries/Win64/TestEditor.exe", "build-17"),
            (parsed.Meta.AppName, parsed.Meta.BuildVersion, parsed.Meta.FeatureLevel, parsed.Meta.LaunchExe, parsed.Meta.BuildID));
        Assert.Equal(["prereq-1"], parsed.Meta.PrereqIDs);

        Assert.Equal([ChunkA, ChunkB], parsed.Chunks.Select(c => c.GUID));
        Assert.Equal((0x1122334455667788ul, (byte)7, 1048576u, 2048L), (parsed.Chunks[0].Hash, parsed.Chunks[0].GroupNum, parsed.Chunks[0].WindowSize, parsed.Chunks[0].FileSize));
        Assert.Equal(Enumerable.Repeat((byte)0xAB, 20), parsed.Chunks[1].SHA1);

        Assert.Equal(["Engine/Binaries/Win64/TestEditor.exe", "Engine/Content/Ünïcödé/名前.uasset", "Engine/Docs/Empty.txt"], parsed.Files.Select(f => f.Filename));
        var editor = parsed.Files[0];
        Assert.Equal((5000L, FileFlags.ReadOnly | FileFlags.UnixExecutable, "application/octet-stream"), (editor.FileSize, editor.Flags, editor.MimeType)); // size from its parts
        Assert.Equal([new ChunkPart(ChunkA, 0, 3000), new ChunkPart(ChunkB, 100, 2000)], editor.ChunkParts);
        Assert.Equal(["engine", "editor"], editor.InstallTags);
        Assert.Equal(Enumerable.Range(0, 20).Select(i => (byte)i), editor.SHA1);
        Assert.Equal(Enumerable.Repeat((byte)0x5A, 16), editor.MD5!);
        Assert.Null(parsed.Files[1].MD5);
        Assert.Equal(Enumerable.Repeat((byte)0x33, 32), editor.SHA256!);
        Assert.Equal((0L, 0), (parsed.Files[2].FileSize, parsed.Files[2].ChunkParts.Length));

        Assert.Equal("Release", parsed.CustomFields["BuildLabel"]);
    }

    [Fact]
    public void Skips_fields_a_newer_format_adds()
    {
        var parsed = Manifest.Parse(BinaryManifestWriter.Write(SampleManifest(), newerFieldBytes: 12));

        Assert.Equal("5.7.0-48201490+++Test+Release-5.7", parsed.Meta.BuildVersion);
        Assert.Equal([new ChunkPart(ChunkA, 0, 3000), new ChunkPart(ChunkB, 100, 2000)], parsed.Files[0].ChunkParts);
        Assert.Equal(3, parsed.Files.Count);
        Assert.Equal("Release", parsed.CustomFields["BuildLabel"]);
    }

    [Fact]
    public void Reads_an_older_file_list_without_hashes_and_mime_types()
    {
        var parsed = Manifest.Parse(BinaryManifestWriter.Write(SampleManifest(), fileListVersion: 0));

        Assert.Null(parsed.Files[0].MD5);
        Assert.Equal("", parsed.Files[0].MimeType);
        Assert.Null(parsed.Files[0].SHA256);
        Assert.Equal(5000L, parsed.Files[0].FileSize);
    }

    [Fact]
    public void Reads_the_chunk_fields_of_feature_level_22()
    {
        var parsed = Manifest.Parse(BinaryManifestWriter.Write(SampleManifest(featureLevel: 22)));

        Assert.Equal(new EpicGUID(0x5E, 0, 0, 1), parsed.Chunks[0].SecretGUID);
        Assert.Equal(Enumerable.Repeat((byte)0x7C, 16), parsed.Chunks[0].EncryptionTag);
        Assert.Equal(2, parsed.Files[0].ChunkParts.Length); // and the lists after it still line up
    }

    [Fact]
    public void Rejects_a_file_that_isnt_a_manifest()
    {
        byte[] data = BinaryManifestWriter.Write(SampleManifest());
        data[0] ^= 0xFF;

        Assert.Throws<ManifestFormatException>(() => Manifest.Parse(data));
        Assert.Throws<ManifestFormatException>(() => Manifest.Parse([0x0C, 0xC0]));
    }

    [Fact]
    public void Rejects_a_changed_body()
    {
        byte[] data = BinaryManifestWriter.Write(SampleManifest(), compress: false);
        data[^5] ^= 0xFF;

        var error = Assert.Throws<ManifestFormatException>(() => Manifest.Parse(data));
        Assert.Contains("hash mismatch", error.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Rejects_a_cut_off_file(bool compress)
    {
        byte[] data = BinaryManifestWriter.Write(SampleManifest(), compress);

        Assert.Throws<ManifestFormatException>(() => Manifest.Parse(data[..^20]));
        Assert.Throws<ManifestFormatException>(() => Manifest.Parse(data[..30])); // not even the whole header
    }

    [Fact]
    public void Rejects_a_damaged_compressed_body()
    {
        byte[] data = BinaryManifestWriter.Write(SampleManifest());
        data.AsSpan(45, 20).Fill(0xFF);

        Assert.Throws<ManifestFormatException>(() => Manifest.Parse(data));
    }

    [Fact]
    public void Says_so_when_file_names_are_encrypted() =>
        Assert.Contains("encrypted", Assert.Throws<ManifestFormatException>(() =>
            Manifest.Parse(BinaryManifestWriter.Write(SampleManifest(), storedAs: 2))).Message);

    /// <summary>A small engine-like build: a file over two chunks, one with a non-Latin name, an empty one.</summary>
    private static Manifest SampleManifest(uint featureLevel = 21) => new()
    {
        Version = 21,
        Meta = new ManifestMeta
        {
            FeatureLevel = featureLevel,
            IsFileData = true,
            AppName = "TestEngine",
            BuildVersion = "5.7.0-48201490+++Test+Release-5.7",
            LaunchExe = "Engine/Binaries/Win64/TestEditor.exe",
            PrereqIDs = ["prereq-1"],
            BuildID = "build-17",
        },
        Chunks =
        [
            new ChunkInfo
            {
                GUID = ChunkA, Hash = 0x1122334455667788, SHA1 = new byte[20], GroupNum = 7, WindowSize = 1048576, FileSize = 2048,
                SecretGUID = new EpicGUID(0x5E, 0, 0, 1), EncryptionTag = Enumerable.Repeat((byte)0x7C, 16).ToArray(),
            },
            new ChunkInfo
            {
                GUID = ChunkB, SHA1 = Enumerable.Repeat((byte)0xAB, 20).ToArray(), WindowSize = 1048576, FileSize = 4096,
                EncryptionTag = new byte[16],
            },
        ],
        Files =
        [
            new FileManifest
            {
                Filename = "Engine/Binaries/Win64/TestEditor.exe", SHA1 = Enumerable.Range(0, 20).Select(i => (byte)i).ToArray(),
                Flags = FileFlags.ReadOnly | FileFlags.UnixExecutable, InstallTags = ["engine", "editor"],
                ChunkParts = [new ChunkPart(ChunkA, 0, 3000), new ChunkPart(ChunkB, 100, 2000)],
                MD5 = Enumerable.Repeat((byte)0x5A, 16).ToArray(), MimeType = "application/octet-stream", SHA256 = Enumerable.Repeat((byte)0x33, 32).ToArray(),
            },
            new FileManifest
            {
                Filename = "Engine/Content/Ünïcödé/名前.uasset", SHA1 = new byte[20], ChunkParts = [new ChunkPart(ChunkB, 2100, 900)],
            },
            new FileManifest { Filename = "Engine/Docs/Empty.txt", SHA1 = new byte[20] },
        ],
        CustomFields = new Dictionary<string, string> { ["BuildLabel"] = "Release" },
    };
}
