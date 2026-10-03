using System.Text.Json;

namespace Unvault.Core.Manifests;

/// <summary>
/// Parser for the older JSON manifest format (used by e.g. UE 4.27). Numbers and hashes are stored
/// as "blobs": each byte written as three decimal digits, least significant byte first —
/// "224103000000" is the bytes [224, 103, 0, 0], i.e. the uint32 26592.
/// </summary>
internal static class JSONManifestParser
{
    /// <summary>JSON manifests don't record chunk sizes; every chunk is 1 MiB uncompressed.</summary>
    private const uint JSONChunkWindowSize = 1024 * 1024;

    public static Manifest Parse(ReadOnlyMemory<byte> data)
    {
        using var doc = JsonDocument.Parse(data, new JsonDocumentOptions { AllowTrailingCommas = true });
        var root = doc.RootElement;

        uint version = (uint)BlobToNumber(GetString(root, "ManifestFileVersion", "013000000000"));

        var meta = new ManifestMeta
        {
            FeatureLevel = version,
            IsFileData = root.TryGetProperty("bIsFileData", out var isFileData) && isFileData.GetBoolean(),
            AppID = (uint)BlobToNumber(GetString(root, "AppID", "000000000000")),
            AppName = GetString(root, "AppNameString"),
            BuildVersion = GetString(root, "BuildVersionString"),
            LaunchExe = GetString(root, "LaunchExeString"),
            LaunchCommand = GetString(root, "LaunchCommand"),
            PrereqIDs = GetStringArray(root, "PrereqIDs"),
            PrereqName = GetString(root, "PrereqName"),
            PrereqPath = GetString(root, "PrereqPath"),
            PrereqArgs = GetString(root, "PrereqArgs"),
        };

        return new Manifest
        {
            Version = version,
            Meta = meta,
            Chunks = ReadChunks(root),
            Files = ReadFiles(root),
            CustomFields = root.TryGetProperty("CustomFields", out var custom)
                ? custom.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal),
        };
    }

    private static List<ChunkInfo> ReadChunks(JsonElement root)
    {
        var hashes = root.GetProperty("ChunkHashList");
        var groups = root.GetProperty("DataGroupList");
        var sizes = root.GetProperty("ChunkFilesizeList");
        bool hasSHA = root.TryGetProperty("ChunkShaList", out var shas);

        var chunks = new List<ChunkInfo>();
        foreach (var entry in hashes.EnumerateObject())
        {
            string guid = entry.Name;
            chunks.Add(new ChunkInfo
            {
                GUID = EpicGUID.Parse(guid),
                Hash = BlobToNumber(entry.Value.GetString() ?? ""),
                SHA1 = hasSHA && shas.TryGetProperty(guid, out var sha) ? Convert.FromHexString(sha.GetString() ?? "") : [],
                GroupNum = groups.TryGetProperty(guid, out var group)
                    ? (byte)BlobToNumber(group.GetString() ?? "")
                    : throw new ManifestFormatException($"Chunk {guid} has no data group."),
                WindowSize = JSONChunkWindowSize,
                FileSize = sizes.TryGetProperty(guid, out var size) ? (long)BlobToNumber(size.GetString() ?? "") : 0,
            });
        }
        return chunks;
    }

    private static List<FileManifest> ReadFiles(JsonElement root)
    {
        var tagPool = new Dictionary<string, string>(StringComparer.Ordinal);
        var files = new List<FileManifest>();

        foreach (var file in root.GetProperty("FileManifestList").EnumerateArray())
        {
            var flags = FileFlags.None;
            if (GetBool(file, "bIsReadOnly")) flags |= FileFlags.ReadOnly;
            if (GetBool(file, "bIsCompressed")) flags |= FileFlags.Compressed;
            if (GetBool(file, "bIsUnixExecutable")) flags |= FileFlags.UnixExecutable;

            var parts = new List<ChunkPart>();
            if (file.TryGetProperty("FileChunkParts", out var partArray))
            {
                foreach (var part in partArray.EnumerateArray())
                {
                    parts.Add(new ChunkPart(
                        EpicGUID.Parse(GetString(part, "Guid")),
                        (uint)BlobToNumber(GetString(part, "Offset")),
                        (uint)BlobToNumber(GetString(part, "Size"))));
                }
            }

            var tags = GetStringArray(file, "InstallTags");
            for (int i = 0; i < tags.Length; i++)
            {
                if (!tagPool.TryGetValue(tags[i], out var pooled))
                    tagPool[tags[i]] = pooled = tags[i];
                tags[i] = pooled;
            }

            files.Add(new FileManifest
            {
                Filename = GetString(file, "Filename"),
                SymlinkTarget = GetString(file, "SymlinkTarget"),
                SHA1 = BlobToBytes(GetString(file, "FileHash")),
                Flags = flags,
                InstallTags = tags,
                ChunkParts = [.. parts],
                FileSize = parts.Sum(p => (long)p.Size),
            });
        }
        return files;
    }

    internal static ulong BlobToNumber(string blob)
    {
        ulong value = 0;
        int shift = 0;
        for (int i = 0; i + 3 <= blob.Length; i += 3, shift += 8)
            value |= (ulong)int.Parse(blob.AsSpan(i, 3)) << shift;
        return value;
    }

    internal static byte[] BlobToBytes(string blob)
    {
        var bytes = new byte[blob.Length / 3];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)int.Parse(blob.AsSpan(i * 3, 3));
        return bytes;
    }

    private static string GetString(JsonElement element, string name, string fallback = "") =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : fallback;

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string[] GetStringArray(JsonElement element, string name) =>
        element.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(v => v.GetString() ?? "").ToArray()
            : [];
}
