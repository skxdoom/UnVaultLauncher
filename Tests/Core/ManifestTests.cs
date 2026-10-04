using System.Text;
using Unvault.Core.Manifests;

namespace Unvault.Core.Tests;

public class EpicGUIDTests
{
    [Fact]
    public void Parse_and_ToString_round_trip()
    {
        var guid = EpicGUID.Parse("87D126C240B6603F50960B8CF63A11D6");

        Assert.Equal(0x87D126C2u, guid.A);
        Assert.Equal(0xF63A11D6u, guid.D);
        Assert.Equal("87D126C240B6603F50960B8CF63A11D6", guid.ToString());
    }

    [Fact]
    public void Parse_rejects_wrong_length() =>
        Assert.Throws<FormatException>(() => EpicGUID.Parse("1234"));
}

public class JSONBlobTests
{
    [Theory]
    [InlineData("224103000000", 26592ul)]
    [InlineData("013000000000", 13ul)]
    [InlineData("014", 14ul)]
    [InlineData("000000000000", 0ul)]
    public void BlobToNumber_is_little_endian_decimal_bytes(string blob, ulong expected) =>
        Assert.Equal(expected, JSONManifestParser.BlobToNumber(Encoding.ASCII.GetBytes(blob)));

    [Fact]
    public void BlobToBytes_decodes_each_triplet() =>
        Assert.Equal(new byte[] { 27, 0, 255 }, JSONManifestParser.BlobToBytes("027000255"u8));

    [Theory]
    [InlineData("256")]
    [InlineData("02a")]
    public void Blob_with_a_bad_triplet_is_a_format_error(string blob) =>
        Assert.Throws<ManifestFormatException>(() => JSONManifestParser.BlobToBytes(Encoding.ASCII.GetBytes(blob)));

    [Fact]
    public void JSON_manifest_reads_every_field_in_any_order()
    {
        // Chunk lists before the file list, unknown fields, a prerequisite list and custom fields: all as EGL may write them.
        string json = """
            {
              "ChunkHashList": { "00000000000000000000000000000001": "016000000000000000" },
              "DataGroupList": { "00000000000000000000000000000001": "007" },
              "ChunkShaList": { "00000000000000000000000000000001": "0102030405060708090A0B0C0D0E0F1011121314" },
              "ChunkFilesizeList": { "00000000000000000000000000000001": "100001000000000000" },
              "SomethingNew": { "nested": [1, 2, { "deeper": true }] },
              "ManifestFileVersion": "013000000000",
              "AppNameString": "UE_4.27",
              "BuildVersionString": "4.27.2-1+++UE4+Release-4.27-Windows",
              "PrereqIds": [ "ABC" ],
              "FileManifestList": [
                {
                  "Filename": "Engine/Binaries/Win64/Editor.exe",
                  "FileHash": "001002003004005006007008009010011012013014015016017018019020",
                  "FileChunkParts": [ { "Guid": "00000000000000000000000000000001", "Offset": "016000000000", "Size": "032000000000" } ],
                  "InstallTags": [ "Win64", "Win64" ],
                  "bIsReadOnly": true,
                  "bIsUnixExecutable": false
                },
                { "Filename": "Readme.txt", "FileHash": "000000000000000000000000000000000000000000000000000000000000", "FileChunkParts": [] }
              ],
              "CustomFields": { "Key": "Value" }
            }
            """;

        var manifest = Manifest.Parse(Encoding.UTF8.GetBytes(json));

        Assert.Equal(("UE_4.27", "4.27.2-1+++UE4+Release-4.27-Windows", 13u), (manifest.Meta.AppName, manifest.Meta.BuildVersion, manifest.FeatureLevel));
        Assert.Equal(["ABC"], manifest.Meta.PrereqIDs);
        Assert.Equal("Value", manifest.CustomFields["Key"]);

        var chunk = Assert.Single(manifest.Chunks);
        Assert.Equal((16ul, (byte)7, 100L + 256), (chunk.Hash, chunk.GroupNum, chunk.FileSize));
        Assert.Equal(Enumerable.Range(1, 20).Select(i => (byte)i), chunk.SHA1);
        Assert.Equal(1024u * 1024, chunk.WindowSize);

        var exe = manifest.Files[0];
        Assert.Equal(new ChunkPart(EpicGUID.Parse("00000000000000000000000000000001"), 16, 32), Assert.Single(exe.ChunkParts));
        Assert.Equal((32L, FileFlags.ReadOnly), (exe.FileSize, exe.Flags));
        Assert.Equal(Enumerable.Range(1, 20).Select(i => (byte)i), exe.SHA1);
        Assert.Same(exe.InstallTags[0], exe.InstallTags[1]); // repeated tags share one string
        Assert.Equal(("Readme.txt", 0L), (manifest.Files[1].Filename, manifest.Files[1].FileSize));
    }

    [Fact]
    public void JSON_manifest_cut_off_midway_is_a_format_error() =>
        Assert.Throws<ManifestFormatException>(() => Manifest.Parse(Encoding.UTF8.GetBytes("""{ "AppNameString": "UE_4.27", "FileManifestList": [ { "Filename": "a" """)));
}

public class ChunkPathTests
{
    [Theory]
    [InlineData(21u, "ChunksV4")]
    [InlineData(15u, "ChunksV4")]
    [InlineData(13u, "ChunksV3")]
    [InlineData(6u, "ChunksV3")]
    [InlineData(3u, "ChunksV2")]
    [InlineData(2u, "Chunks")]
    public void Chunk_dir_depends_on_manifest_version(uint version, string expected) =>
        Assert.Equal(expected, ChunkInfo.GetChunkDir(version));

    [Fact]
    public void Chunk_path_uses_group_hash_and_guid()
    {
        var chunk = new ChunkInfo { GUID = EpicGUID.Parse("87D126C240B6603F50960B8CF63A11D6"), Hash = 0xABC, GroupNum = 7 };

        Assert.Equal("ChunksV4/07/0000000000000ABC_87D126C240B6603F50960B8CF63A11D6.chunk", chunk.GetPath(21));
    }

    [Fact]
    public void ChunksV5_path_uses_url_safe_base64_of_little_endian_bytes()
    {
        var chunk = new ChunkInfo { GUID = new EpicGUID(1, 0, 0, 0), Hash = 0xABC, GroupNum = 7 };

        // hash LE = BC 0A 00 00 00 00 00 00; GUID LE = 01 00 … 00; no secret → "plain".
        Assert.Equal("ChunksV5/plain/07/vAoAAAAAAAA_AQAAAAAAAAAAAAAAAAAAAA.chunk", chunk.GetPath(22));
    }

    [Fact]
    public void ChunksV5_path_names_the_secret_when_encrypted()
    {
        var chunk = new ChunkInfo { GUID = new EpicGUID(1, 0, 0, 0), Hash = 0xABC, GroupNum = 7, SecretGUID = new EpicGUID(0xFFFFFFFF, 0, 0, 0) };

        Assert.StartsWith("ChunksV5/_____wAAAAAAAAAAAAAAAA/07/", chunk.GetPath(24));
    }
}

public class InstallSelectionTests
{
    private static readonly EpicGUID ChunkA = new(1, 0, 0, 0);
    private static readonly EpicGUID ChunkB = new(2, 0, 0, 0);
    private static readonly EpicGUID ChunkC = new(3, 0, 0, 0);

    private static readonly FileManifest Core = File("Engine/Binaries/Win64/UnrealEditor.exe", 100, [], ChunkA);
    private static readonly FileManifest Symbols = File("Engine/Binaries/Win64/UnrealEditor.pdb", 1000, ["editor_symbols"], ChunkB);
    private static readonly FileManifest LinuxBinary = File("Engine/Binaries/Linux/UnrealGame", 50, ["platform_Linux"], ChunkC);
    private static readonly FileManifest LinuxSymbols = File("Engine/Binaries/Linux/UnrealGame.pdb", 500, ["editor_symbols", "platform_Linux"], ChunkB);

    private static readonly Manifest TestManifest = new()
    {
        Version = 21,
        Meta = new ManifestMeta(),
        Chunks =
        [
            new ChunkInfo { GUID = ChunkA, FileSize = 10 },
            new ChunkInfo { GUID = ChunkB, FileSize = 20 },
            new ChunkInfo { GUID = ChunkC, FileSize = 30 },
        ],
        Files = [Core, Symbols, LinuxBinary, LinuxSymbols],
        CustomFields = new Dictionary<string, string>(),
    };

    [Fact]
    public void Untagged_files_are_always_installed() =>
        Assert.Equal([Core], TestManifest.SelectFiles(Tags()));

    [Fact]
    public void File_with_several_tags_is_installed_when_any_is_selected() =>
        Assert.Equal([Core, LinuxBinary, LinuxSymbols], TestManifest.SelectFiles(Tags("platform_Linux")));

    [Fact]
    public void MeasureSelection_counts_each_chunk_once()
    {
        var size = TestManifest.MeasureSelection(Tags("editor_symbols"));

        Assert.Equal(3, size.FileCount);
        Assert.Equal(1600, size.InstallBytes);
        Assert.Equal(2, size.ChunkCount);
        Assert.Equal(30, size.DownloadBytes);
    }

    [Fact]
    public void Removing_a_component_keeps_files_another_selected_component_needs()
    {
        var impacts = ComponentPlanner.Analyze(TestManifest, Tags("editor_symbols", "platform_Linux"));
        var symbols = impacts.Single(i => i.Tag == "editor_symbols");

        // LinuxSymbols stays because platform_Linux still selects it.
        Assert.True(symbols.Installed);
        Assert.Equal(1, symbols.FileCount);
        Assert.Equal(1000, symbols.DiskBytes);
    }

    [Fact]
    public void Adding_a_component_only_downloads_chunks_not_already_present()
    {
        var impacts = ComponentPlanner.Analyze(TestManifest, Tags("editor_symbols"));
        var linux = impacts.Single(i => i.Tag == "platform_Linux");

        // LinuxSymbols is already installed via editor_symbols; only LinuxBinary (chunk C) is new.
        Assert.False(linux.Installed);
        Assert.Equal(1, linux.FileCount);
        Assert.Equal(50, linux.DiskBytes);
        Assert.Equal(30, linux.DownloadBytes);
    }

    private static HashSet<string> Tags(params string[] tags) => new(tags, StringComparer.Ordinal);

    private static FileManifest File(string name, uint size, string[] tags, EpicGUID chunk) => new()
    {
        Filename = name,
        InstallTags = tags,
        ChunkParts = [new ChunkPart(chunk, 0, size)],
        FileSize = size,
    };
}
