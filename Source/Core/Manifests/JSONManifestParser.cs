using System.Text.Json;

namespace Unvault.Core.Manifests;

/// <summary>
/// Parser for the older JSON manifest format (used by e.g. UE 4.27). Numbers and hashes are stored
/// as "blobs": each byte written as three decimal digits, least significant byte first —
/// "224103000000" is the bytes [224, 103, 0, 0], i.e. the uint32 26592.
/// </summary>
/// <remarks>
/// An engine's manifest in this format lists every file and chunk of the engine, so it's large. It's read once, front
/// to back, straight into the manifest classes: no parsed document is kept (it would hold every token in memory), and
/// the chunk lists (separate GUID-keyed objects for hash, SHA-1, group and size) are joined through dictionaries,
/// since looking each chunk up in a parsed object by name means scanning the whole list every time.
/// </remarks>
internal static class JSONManifestParser
{
    /// <summary>JSON manifests don't record chunk sizes; every chunk is 1 MiB uncompressed.</summary>
    private const uint JSONChunkWindowSize = 1024 * 1024;

    public static Manifest Parse(ReadOnlyMemory<byte> data)
    {
        try
        {
            return Read(data.Span);
        }
        catch (JsonException ex)
        {
            throw new ManifestFormatException($"The manifest isn't valid JSON (cut off or damaged?): {ex.Message}");
        }
    }

    private static Manifest Read(ReadOnlySpan<byte> data)
    {
        var reader = new Utf8JsonReader(data, new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        Expect(ref reader, JsonTokenType.StartObject);

        uint version = 13;
        bool isFileData = false;
        uint appID = 0;
        string appName = "", buildVersion = "", launchExe = "", launchCommand = "", prereqName = "", prereqPath = "", prereqArgs = "";
        string[] prereqIDs = [];
        List<FileManifest>? files = null;
        List<(EpicGUID GUID, ulong Hash)>? hashes = null;
        Dictionary<EpicGUID, byte>? groups = null;
        var shas = new Dictionary<EpicGUID, byte[]>();
        var sizes = new Dictionary<EpicGUID, long>();
        var customFields = new Dictionary<string, string>(StringComparer.Ordinal);

        while (Next(ref reader) == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("ManifestFileVersion"u8))
                version = (uint)ReadBlobNumber(ref reader);
            else if (reader.ValueTextEquals("bIsFileData"u8))
                isFileData = ReadBool(ref reader);
            else if (reader.ValueTextEquals("AppID"u8))
                appID = (uint)ReadBlobNumber(ref reader);
            else if (reader.ValueTextEquals("AppNameString"u8))
                appName = ReadString(ref reader);
            else if (reader.ValueTextEquals("BuildVersionString"u8))
                buildVersion = ReadString(ref reader);
            else if (reader.ValueTextEquals("LaunchExeString"u8))
                launchExe = ReadString(ref reader);
            else if (reader.ValueTextEquals("LaunchCommand"u8))
                launchCommand = ReadString(ref reader);
            else if (reader.ValueTextEquals("PrereqIds"u8))
                prereqIDs = ReadStringArray(ref reader, pool: null);
            else if (reader.ValueTextEquals("PrereqName"u8))
                prereqName = ReadString(ref reader);
            else if (reader.ValueTextEquals("PrereqPath"u8))
                prereqPath = ReadString(ref reader);
            else if (reader.ValueTextEquals("PrereqArgs"u8))
                prereqArgs = ReadString(ref reader);
            else if (reader.ValueTextEquals("FileManifestList"u8))
                files = ReadFiles(ref reader);
            else if (reader.ValueTextEquals("ChunkHashList"u8))
            {
                hashes = [];
                Expect(ref reader, JsonTokenType.StartObject);
                while (NextChunk(ref reader, out var guid))
                    hashes.Add((guid, BlobToNumber(Value(ref reader))));
            }
            else if (reader.ValueTextEquals("ChunkShaList"u8))
            {
                Expect(ref reader, JsonTokenType.StartObject);
                while (NextChunk(ref reader, out var guid))
                    shas[guid] = Convert.FromHexString(reader.GetString() ?? "");
            }
            else if (reader.ValueTextEquals("DataGroupList"u8))
            {
                groups = [];
                Expect(ref reader, JsonTokenType.StartObject);
                while (NextChunk(ref reader, out var guid))
                    groups[guid] = (byte)BlobToNumber(Value(ref reader));
            }
            else if (reader.ValueTextEquals("ChunkFilesizeList"u8))
            {
                Expect(ref reader, JsonTokenType.StartObject);
                while (NextChunk(ref reader, out var guid))
                    sizes[guid] = (long)BlobToNumber(Value(ref reader));
            }
            else if (reader.ValueTextEquals("CustomFields"u8))
            {
                Expect(ref reader, JsonTokenType.StartObject);
                while (Next(ref reader) == JsonTokenType.PropertyName)
                {
                    string key = reader.GetString()!;
                    customFields[key] = ReadString(ref reader);
                }
            }
            else
            {
                reader.Skip(); // a field this parser doesn't use
            }
        }

        if (files is null || hashes is null || groups is null)
            throw new ManifestFormatException("The manifest has no file or chunk list.");

        var chunks = new List<ChunkInfo>(hashes.Count);
        foreach (var (guid, hash) in hashes)
        {
            chunks.Add(new ChunkInfo
            {
                GUID = guid,
                Hash = hash,
                SHA1 = shas.GetValueOrDefault(guid) ?? [],
                GroupNum = groups.TryGetValue(guid, out byte group) ? group : throw new ManifestFormatException($"Chunk {guid} has no data group."),
                WindowSize = JSONChunkWindowSize,
                FileSize = sizes.GetValueOrDefault(guid),
            });
        }

        return new Manifest
        {
            Version = version,
            Meta = new ManifestMeta
            {
                FeatureLevel = version,
                IsFileData = isFileData,
                AppID = appID,
                AppName = appName,
                BuildVersion = buildVersion,
                LaunchExe = launchExe,
                LaunchCommand = launchCommand,
                PrereqIDs = prereqIDs,
                PrereqName = prereqName,
                PrereqPath = prereqPath,
                PrereqArgs = prereqArgs,
            },
            Chunks = chunks,
            Files = files,
            CustomFields = customFields,
        };
    }

    private static List<FileManifest> ReadFiles(ref Utf8JsonReader reader)
    {
        var files = new List<FileManifest>();
        var tagPool = new Dictionary<string, string>(StringComparer.Ordinal); // a handful of tags, repeated across many files
        var parts = new List<ChunkPart>();

        Expect(ref reader, JsonTokenType.StartArray);
        while (Next(ref reader) == JsonTokenType.StartObject)
        {
            string filename = "", symlinkTarget = "";
            byte[] sha1 = [];
            var flags = FileFlags.None;
            string[] tags = [];
            parts.Clear();

            while (Next(ref reader) == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("Filename"u8))
                    filename = ReadString(ref reader);
                else if (reader.ValueTextEquals("FileHash"u8))
                    sha1 = BlobToBytes(ReadValue(ref reader));
                else if (reader.ValueTextEquals("FileChunkParts"u8))
                    ReadChunkParts(ref reader, parts);
                else if (reader.ValueTextEquals("InstallTags"u8))
                    tags = ReadStringArray(ref reader, tagPool);
                else if (reader.ValueTextEquals("SymlinkTarget"u8))
                    symlinkTarget = ReadString(ref reader);
                else if (reader.ValueTextEquals("bIsReadOnly"u8))
                    flags |= ReadBool(ref reader) ? FileFlags.ReadOnly : FileFlags.None;
                else if (reader.ValueTextEquals("bIsCompressed"u8))
                    flags |= ReadBool(ref reader) ? FileFlags.Compressed : FileFlags.None;
                else if (reader.ValueTextEquals("bIsUnixExecutable"u8))
                    flags |= ReadBool(ref reader) ? FileFlags.UnixExecutable : FileFlags.None;
                else
                    reader.Skip();
            }

            long fileSize = 0;
            foreach (var part in parts)
                fileSize += part.Size;

            files.Add(new FileManifest
            {
                Filename = filename,
                SymlinkTarget = symlinkTarget,
                SHA1 = sha1,
                Flags = flags,
                InstallTags = tags,
                ChunkParts = [.. parts],
                FileSize = fileSize,
            });
        }
        return files;
    }

    private static void ReadChunkParts(ref Utf8JsonReader reader, List<ChunkPart> parts)
    {
        Expect(ref reader, JsonTokenType.StartArray);
        while (Next(ref reader) == JsonTokenType.StartObject)
        {
            EpicGUID guid = default;
            uint offset = 0, size = 0;
            while (Next(ref reader) == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("Guid"u8))
                    guid = EpicGUID.Parse(ReadValue(ref reader));
                else if (reader.ValueTextEquals("Offset"u8))
                    offset = (uint)BlobToNumber(ReadValue(ref reader));
                else if (reader.ValueTextEquals("Size"u8))
                    size = (uint)BlobToNumber(ReadValue(ref reader));
                else
                    reader.Skip();
            }
            parts.Add(new ChunkPart(guid, offset, size));
        }
    }

    internal static ulong BlobToNumber(ReadOnlySpan<byte> blob)
    {
        ulong value = 0;
        for (int i = 0, shift = 0; i + 3 <= blob.Length && shift < 64; i += 3, shift += 8)
            value |= (ulong)Triplet(blob.Slice(i, 3)) << shift;
        return value;
    }

    internal static byte[] BlobToBytes(ReadOnlySpan<byte> blob)
    {
        var bytes = new byte[blob.Length / 3];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Triplet(blob.Slice(i * 3, 3));
        return bytes;
    }

    /// <summary>One byte of a blob: three decimal digits, "000" to "255".</summary>
    private static byte Triplet(ReadOnlySpan<byte> digits)
    {
        int value = 0;
        foreach (byte digit in digits)
        {
            if (digit is < (byte)'0' or > (byte)'9')
                throw new ManifestFormatException("A number in the manifest isn't written as decimal digits.");
            value = value * 10 + (digit - '0');
        }
        return value <= byte.MaxValue ? (byte)value : throw new ManifestFormatException("A number in the manifest is out of range.");
    }

    /// <summary>Moves to the next token, failing on a cut-off file.</summary>
    private static JsonTokenType Next(ref Utf8JsonReader reader) =>
        reader.Read() ? reader.TokenType : throw new ManifestFormatException("The manifest ends too early (cut off?).");

    private static void Expect(ref Utf8JsonReader reader, JsonTokenType expected)
    {
        if (Next(ref reader) != expected)
            throw new ManifestFormatException($"Unexpected {reader.TokenType} in the manifest where {expected} belongs.");
    }

    /// <summary>In a GUID-keyed chunk list: reads the next key and moves to its value; false at the end of the list.</summary>
    private static bool NextChunk(ref Utf8JsonReader reader, out EpicGUID guid)
    {
        guid = default;
        if (Next(ref reader) != JsonTokenType.PropertyName)
            return false;
        guid = EpicGUID.Parse(Value(ref reader));
        Next(ref reader);
        return true;
    }

    /// <summary>The current token's text as UTF-8 (unescaped, in the rare case it has escapes).</summary>
    private static ReadOnlySpan<byte> Value(ref Utf8JsonReader reader)
    {
        if (!reader.ValueIsEscaped)
            return reader.ValueSpan;
        var unescaped = new byte[reader.ValueSpan.Length];
        return unescaped.AsSpan(0, reader.CopyString(unescaped));
    }

    private static ReadOnlySpan<byte> ReadValue(ref Utf8JsonReader reader)
    {
        Next(ref reader);
        return Value(ref reader);
    }

    private static ulong ReadBlobNumber(ref Utf8JsonReader reader) => BlobToNumber(ReadValue(ref reader));

    private static string ReadString(ref Utf8JsonReader reader)
    {
        if (Next(ref reader) == JsonTokenType.String)
            return reader.GetString()!;
        reader.Skip(); // null, or something unexpected: treat as empty
        return "";
    }

    private static bool ReadBool(ref Utf8JsonReader reader)
    {
        var token = Next(ref reader);
        reader.Skip();
        return token == JsonTokenType.True;
    }

    private static string[] ReadStringArray(ref Utf8JsonReader reader, Dictionary<string, string>? pool)
    {
        if (Next(ref reader) != JsonTokenType.StartArray)
        {
            reader.Skip();
            return [];
        }

        var values = new List<string>();
        while (Next(ref reader) != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                reader.Skip(); // not a string: nothing to keep
                continue;
            }
            string value = reader.GetString()!;
            if (pool is not null)
            {
                if (!pool.TryGetValue(value, out var pooled))
                    pool[value] = pooled = value;
                value = pooled;
            }
            values.Add(value);
        }
        return [.. values];
    }
}
