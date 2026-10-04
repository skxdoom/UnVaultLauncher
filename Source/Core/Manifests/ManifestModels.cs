using System.Buffers.Binary;
using System.Buffers.Text;

namespace UnVault.Core.Manifests;

/// <summary>Build-level metadata from the manifest's Meta section.</summary>
public sealed class ManifestMeta
{
    public byte DataVersion { get; init; }
    public uint FeatureLevel { get; init; }
    public bool IsFileData { get; init; }
    public uint AppID { get; init; }
    public string AppName { get; init; } = "";
    public string BuildVersion { get; init; } = "";
    public string LaunchExe { get; init; } = "";
    public string LaunchCommand { get; init; } = "";
    public IReadOnlyList<string> PrereqIDs { get; init; } = [];
    public string PrereqName { get; init; } = "";
    public string PrereqPath { get; init; } = "";
    public string PrereqArgs { get; init; } = "";
    public string BuildID { get; init; } = "";
    public string UninstallActionPath { get; init; } = "";
    public string UninstallActionArgs { get; init; } = "";
}

/// <summary>
/// One downloadable chunk. Chunks are ~1 MiB blocks of file data shared between files;
/// files are rebuilt by copying slices (<see cref="ChunkPart"/>) out of them.
/// </summary>
public sealed class ChunkInfo
{
    public EpicGUID GUID { get; init; }

    /// <summary>Rolling hash, also part of the chunk's CDN file name.</summary>
    public ulong Hash { get; init; }

    public byte[] SHA1 { get; init; } = [];
    public byte GroupNum { get; init; }

    /// <summary>Uncompressed size of the chunk data.</summary>
    public uint WindowSize { get; init; }

    /// <summary>Size of the .chunk file on the CDN (compressed, with header).</summary>
    public long FileSize { get; init; }

    /// <summary>Feature level 22+: which AES key (from the build info's secrets) encrypts this chunk. Empty = not encrypted.</summary>
    public EpicGUID SecretGUID { get; init; }

    /// <summary>Feature level 22+: AES-GCM tag of the encrypted chunk data.</summary>
    public byte[] EncryptionTag { get; init; } = [];

    /// <summary>Path relative to the build's CloudDir base URL.</summary>
    public string GetPath(uint featureLevel)
    {
        if (featureLevel < 22)
            return $"{GetChunkDir(featureLevel)}/{GroupNum:D2}/{Hash:X16}_{GUID}.chunk";

        // ChunksV5: URL-safe base64 (no padding) of the little-endian bytes.
        Span<byte> hashBytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(hashBytes, Hash);
        string secret = SecretGUID.IsEmpty ? "plain" : Base64Url.EncodeToString(SecretGUID.ToBytes());
        return $"ChunksV5/{secret}/{GroupNum:D2}/{Base64Url.EncodeToString(hashBytes)}_{Base64Url.EncodeToString(GUID.ToBytes())}.chunk";
    }

    public static string GetChunkDir(uint featureLevel) => featureLevel switch
    {
        >= 22 => "ChunksV5",
        >= 15 => "ChunksV4",
        >= 6 => "ChunksV3",
        >= 3 => "ChunksV2",
        _ => "Chunks",
    };
}

/// <summary>A slice of a chunk that makes up part of a file, in file order.</summary>
public readonly record struct ChunkPart(EpicGUID GUID, uint Offset, uint Size);

[Flags]
public enum FileFlags : byte
{
    None = 0,
    ReadOnly = 1,
    Compressed = 2,
    UnixExecutable = 4,
}

public sealed class FileManifest
{
    /// <summary>Path relative to the install directory, with forward slashes.</summary>
    public string Filename { get; init; } = "";

    public string SymlinkTarget { get; init; } = "";
    public byte[] SHA1 { get; init; } = [];
    public FileFlags Flags { get; init; }

    /// <summary>Optional-component tags. Untagged files are always installed.</summary>
    public IReadOnlyList<string> InstallTags { get; init; } = [];

    public ChunkPart[] ChunkParts { get; init; } = [];
    public byte[]? MD5 { get; init; }
    public string MimeType { get; init; } = "";
    public byte[]? SHA256 { get; init; }

    public long FileSize { get; init; }
}
