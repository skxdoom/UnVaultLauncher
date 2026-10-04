using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace UnVault.Core.Manifests;

public sealed class ManifestFormatException(string message) : Exception(message);

/// <summary>
/// An Epic BuildPatchServices manifest: the full description of one build (engine version,
/// plugin, game) — which files it has, and which chunks to download to rebuild them.
/// </summary>
/// <remarks>
/// Binary layout, all little-endian:
/// <code>
/// Header: magic u32 (0x44BEC00C), header size u32, uncompressed size u32, compressed size u32,
///         SHA-1 of uncompressed body [20], stored-as u8 (1 = zlib, 2 = encrypted), version u32
/// Body:   Meta, ChunkDataList, FileManifestList, CustomFields
/// </code>
/// Each body section starts with its own size, so newer versions that append fields still parse.
/// </remarks>
public sealed class Manifest
{
    public const uint Magic = 0x44BEC00C;

    private const byte StoredCompressed = 0x1;
    private const byte StoredEncrypted = 0x2;

    private Dictionary<EpicGUID, ChunkInfo>? _chunksByGUID;

    /// <summary>Format version from the file header.</summary>
    public uint Version { get; init; }

    /// <summary>Build feature level (from Meta); selects the chunk directory layout and optional fields.</summary>
    public uint FeatureLevel => Meta.FeatureLevel;

    public required ManifestMeta Meta { get; init; }
    public required IReadOnlyList<ChunkInfo> Chunks { get; init; }
    public required IReadOnlyList<FileManifest> Files { get; init; }
    public required IReadOnlyDictionary<string, string> CustomFields { get; init; }

    public IReadOnlyDictionary<EpicGUID, ChunkInfo> ChunksByGUID =>
        _chunksByGUID ??= Chunks.ToDictionary(c => c.GUID);

    public static Manifest Load(string path) => Parse(File.ReadAllBytes(path));

    public static Manifest Parse(byte[] data)
    {
        var json = data.AsMemory();
        if (json.Span.StartsWith("﻿"u8))
            json = json[3..];
        if (json.Length >= 1 && json.Span[0] == (byte)'{')
            return JSONManifestParser.Parse(json);

        using var headerStream = new MemoryStream(data, writable: false);
        using var header = new BinaryReader(headerStream);

        if (header.ReadUInt32() != Magic)
            throw new ManifestFormatException("Not an Epic manifest (bad magic).");

        uint headerSize = header.ReadUInt32();
        uint sizeUncompressed = header.ReadUInt32();
        uint sizeCompressed = header.ReadUInt32();
        byte[] expectedSHA1 = header.ReadBytes(20);
        byte storedAs = header.ReadByte();
        uint version = header.ReadUInt32();

        // Feature level 22+ headers append a secret GUID and AES-GCM tag (header size 73); we seek past
        // them via headerSize below. "Encrypted" means the file names and launch info live in an
        // AES-GCM EncryptedData section, keyed by the build info's "secrets" — not implemented yet.
        if ((storedAs & StoredEncrypted) != 0)
            throw new ManifestFormatException($"This manifest (format v{version}) has encrypted file names; decrypting them isn't implemented yet.");

        byte[] body;
        if ((storedAs & StoredCompressed) != 0)
        {
            using var compressed = new MemoryStream(data, (int)headerSize, (int)sizeCompressed, writable: false);
            using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
            body = new byte[sizeUncompressed];
            zlib.ReadExactly(body);
        }
        else
        {
            body = data.AsSpan((int)headerSize, (int)sizeUncompressed).ToArray();
        }

        if (!SHA1.HashData(body).AsSpan().SequenceEqual(expectedSHA1))
            throw new ManifestFormatException("Manifest body hash mismatch (corrupt or truncated file).");

        using var bodyStream = new MemoryStream(body, writable: false);
        var reader = new SectionReader(bodyStream);

        var meta = reader.ReadMeta();
        var chunks = reader.ReadChunkDataList(meta.FeatureLevel);
        var files = reader.ReadFileManifestList();
        var customFields = reader.ReadCustomFields();

        return new Manifest
        {
            Version = version,
            Meta = meta,
            Chunks = chunks,
            Files = files,
            CustomFields = customFields,
        };
    }

    /// <summary>Reads the manifest body sections. Struct-of-arrays layout: each field is stored for all elements before the next field.</summary>
    private sealed class SectionReader(MemoryStream stream)
    {
        private readonly BinaryReader _r = new(stream, Encoding.Latin1);

        // Install tags and similar strings repeat across many files.
        private readonly Dictionary<string, string> _stringPool = new(StringComparer.Ordinal);

        public ManifestMeta ReadMeta()
        {
            long start = stream.Position;
            uint size = _r.ReadUInt32();
            byte dataVersion = _r.ReadByte();

            var featureLevel = _r.ReadUInt32();
            var isFileData = _r.ReadByte() == 1;
            var appId = _r.ReadUInt32();
            var appName = ReadFString();
            var buildVersion = ReadFString();
            var launchExe = ReadFString();
            var launchCommand = ReadFString();

            var prereqIds = new string[_r.ReadUInt32()];
            for (int i = 0; i < prereqIds.Length; i++)
                prereqIds[i] = ReadFString();

            var meta = new ManifestMeta
            {
                DataVersion = dataVersion,
                FeatureLevel = featureLevel,
                IsFileData = isFileData,
                AppID = appId,
                AppName = appName,
                BuildVersion = buildVersion,
                LaunchExe = launchExe,
                LaunchCommand = launchCommand,
                PrereqIDs = prereqIds,
                PrereqName = ReadFString(),
                PrereqPath = ReadFString(),
                PrereqArgs = ReadFString(),
                BuildID = dataVersion >= 1 ? ReadFString() : "",
                UninstallActionPath = dataVersion >= 2 ? ReadFString() : "",
                UninstallActionArgs = dataVersion >= 2 ? ReadFString() : "",
            };

            stream.Position = start + size;
            return meta;
        }

        public List<ChunkInfo> ReadChunkDataList(uint featureLevel)
        {
            long start = stream.Position;
            uint size = _r.ReadUInt32();
            _r.ReadByte(); // section version
            int count = checked((int)_r.ReadUInt32());

            var guids = new EpicGUID[count];
            var hashes = new ulong[count];
            var sha1s = new byte[count][];
            var groups = new byte[count];
            var windowSizes = new uint[count];
            var fileSizes = new long[count];
            var secretGUIDs = new EpicGUID[count];
            var encryptionTags = new byte[count][];

            for (int i = 0; i < count; i++) guids[i] = EpicGUID.Read(_r);
            for (int i = 0; i < count; i++) hashes[i] = _r.ReadUInt64();
            for (int i = 0; i < count; i++) sha1s[i] = _r.ReadBytes(20);
            for (int i = 0; i < count; i++) groups[i] = _r.ReadByte();
            for (int i = 0; i < count; i++) windowSizes[i] = _r.ReadUInt32();
            for (int i = 0; i < count; i++) fileSizes[i] = _r.ReadInt64();

            if (featureLevel >= 22)
            {
                for (int i = 0; i < count; i++) secretGUIDs[i] = EpicGUID.Read(_r);
                for (int i = 0; i < count; i++) _r.ReadUInt32(); // compressed window size
                for (int i = 0; i < count; i++) encryptionTags[i] = _r.ReadBytes(16);
            }

            var chunks = new List<ChunkInfo>(count);
            for (int i = 0; i < count; i++)
            {
                chunks.Add(new ChunkInfo
                {
                    GUID = guids[i],
                    Hash = hashes[i],
                    SHA1 = sha1s[i],
                    GroupNum = groups[i],
                    WindowSize = windowSizes[i],
                    FileSize = fileSizes[i],
                    SecretGUID = secretGUIDs[i],
                    EncryptionTag = encryptionTags[i] ?? [],
                });
            }

            stream.Position = start + size;
            return chunks;
        }

        public List<FileManifest> ReadFileManifestList()
        {
            long start = stream.Position;
            uint size = _r.ReadUInt32();
            byte version = _r.ReadByte();
            int count = checked((int)_r.ReadUInt32());

            var names = new string[count];
            var symlinks = new string[count];
            var sha1s = new byte[count][];
            var flags = new FileFlags[count];
            var tags = new string[count][];
            var parts = new ChunkPart[count][];
            var md5s = new byte[]?[count];
            var mimeTypes = new string[count];
            var sha256s = new byte[]?[count];

            for (int i = 0; i < count; i++) names[i] = ReadFString();
            for (int i = 0; i < count; i++) symlinks[i] = ReadFString();
            for (int i = 0; i < count; i++) sha1s[i] = _r.ReadBytes(20);
            for (int i = 0; i < count; i++) flags[i] = (FileFlags)_r.ReadByte();

            for (int i = 0; i < count; i++)
            {
                var fileTags = new string[_r.ReadUInt32()];
                for (int t = 0; t < fileTags.Length; t++)
                    fileTags[t] = Pool(ReadFString());
                tags[i] = fileTags;
            }

            for (int i = 0; i < count; i++)
            {
                var fileParts = new ChunkPart[_r.ReadUInt32()];
                for (int p = 0; p < fileParts.Length; p++)
                {
                    long partStart = stream.Position;
                    uint partSize = _r.ReadUInt32();
                    fileParts[p] = new ChunkPart(EpicGUID.Read(_r), _r.ReadUInt32(), _r.ReadUInt32());
                    stream.Position = partStart + partSize;
                }
                parts[i] = fileParts;
            }

            if (version >= 1)
            {
                for (int i = 0; i < count; i++)
                    md5s[i] = _r.ReadUInt32() != 0 ? _r.ReadBytes(16) : null;
                for (int i = 0; i < count; i++)
                    mimeTypes[i] = Pool(ReadFString());
            }

            if (version >= 2)
            {
                for (int i = 0; i < count; i++)
                    sha256s[i] = _r.ReadBytes(32);
            }

            var files = new List<FileManifest>(count);
            for (int i = 0; i < count; i++)
            {
                long fileSize = 0;
                foreach (var part in parts[i])
                    fileSize += part.Size;

                files.Add(new FileManifest
                {
                    Filename = names[i],
                    SymlinkTarget = symlinks[i],
                    SHA1 = sha1s[i],
                    Flags = flags[i],
                    InstallTags = tags[i],
                    ChunkParts = parts[i],
                    MD5 = md5s[i],
                    MimeType = mimeTypes[i] ?? "",
                    SHA256 = sha256s[i],
                    FileSize = fileSize,
                });
            }

            stream.Position = start + size;
            return files;
        }

        public Dictionary<string, string> ReadCustomFields()
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            if (stream.Position >= stream.Length)
                return fields;

            long start = stream.Position;
            uint size = _r.ReadUInt32();
            _r.ReadByte(); // section version
            int count = checked((int)_r.ReadUInt32());

            var keys = new string[count];
            for (int i = 0; i < count; i++) keys[i] = ReadFString();
            for (int i = 0; i < count; i++) fields[keys[i]] = ReadFString();

            stream.Position = start + size;
            return fields;
        }

        /// <summary>
        /// UE FString: int32 length including the null terminator.
        /// Positive = 8-bit chars, negative = UTF-16LE with -length code units.
        /// </summary>
        private string ReadFString()
        {
            int length = _r.ReadInt32();
            if (length == 0)
                return "";

            if (length > 0)
            {
                var bytes = _r.ReadBytes(length);
                return Encoding.Latin1.GetString(bytes, 0, length - 1);
            }

            int byteCount = checked(-length * 2);
            var utf16 = _r.ReadBytes(byteCount);
            return Encoding.Unicode.GetString(utf16, 0, byteCount - 2);
        }

        private string Pool(string value)
        {
            if (value.Length == 0)
                return "";
            if (_stringPool.TryGetValue(value, out var pooled))
                return pooled;
            _stringPool[value] = value;
            return value;
        }
    }
}
