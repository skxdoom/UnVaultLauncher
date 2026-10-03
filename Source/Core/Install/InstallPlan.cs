using System.Security.Cryptography;
using System.Text;
using Unvault.Core.Manifests;

namespace Unvault.Core.Install;

/// <summary>Copy <see cref="Size"/> bytes from a chunk at <see cref="ChunkOffset"/> into a file at <see cref="FileOffset"/>.</summary>
public readonly record struct ChunkWrite(int FileIndex, long FileOffset, uint ChunkOffset, uint Size);

/// <summary>A chunk to download once, and every place its data goes.</summary>
public sealed class PlannedChunk(ChunkInfo info)
{
    public ChunkInfo Info { get; } = info;
    public List<ChunkWrite> Writes { get; } = [];
}

/// <summary>
/// What to download and where each byte goes. Built chunk-first: every chunk is downloaded exactly
/// once and its slices are written straight into the target files, so no chunk cache or staging copy
/// is needed — whatever chunks are shared between files.
/// </summary>
public sealed class InstallPlan
{
    private InstallPlan(Manifest manifest, List<FileManifest> files, List<PlannedChunk> chunks, string id)
    {
        Manifest = manifest;
        Files = files;
        Chunks = chunks;
        ID = id;
        InstallBytes = files.Sum(f => f.FileSize);
        DownloadBytes = chunks.Sum(c => c.Info.FileSize);
    }

    public Manifest Manifest { get; }

    /// <summary>Files to write, sorted by path. <see cref="ChunkWrite.FileIndex"/> indexes this list.</summary>
    public IReadOnlyList<FileManifest> Files { get; }

    /// <summary>Chunks in order of first use, so files complete progressively.</summary>
    public IReadOnlyList<PlannedChunk> Chunks { get; }

    /// <summary>Identifies build + file selection; a resume journal only applies to the same ID.</summary>
    public string ID { get; }

    public long InstallBytes { get; }
    public long DownloadBytes { get; }

    public static InstallPlan Create(Manifest manifest, IEnumerable<FileManifest> files)
    {
        var sorted = files.OrderBy(f => f.Filename, StringComparer.OrdinalIgnoreCase).ToList();
        var byGUID = new Dictionary<EpicGUID, PlannedChunk>();
        var ordered = new List<PlannedChunk>();

        for (int fileIndex = 0; fileIndex < sorted.Count; fileIndex++)
        {
            long fileOffset = 0;
            foreach (var part in sorted[fileIndex].ChunkParts)
            {
                if (!byGUID.TryGetValue(part.GUID, out var planned))
                {
                    if (!manifest.ChunksByGUID.TryGetValue(part.GUID, out var info))
                        throw new ManifestFormatException($"{sorted[fileIndex].Filename} references unknown chunk {part.GUID}.");
                    planned = new PlannedChunk(info);
                    byGUID.Add(part.GUID, planned);
                    ordered.Add(planned);
                }
                planned.Writes.Add(new ChunkWrite(fileIndex, fileOffset, part.Offset, part.Size));
                fileOffset += part.Size;
            }
        }

        return new InstallPlan(manifest, sorted, ordered, ComputeID(manifest, sorted));
    }

    private static string ComputeID(Manifest manifest, List<FileManifest> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(Encoding.UTF8.GetBytes(manifest.Meta.AppName + "\n" + manifest.Meta.BuildVersion + "\n"));
        foreach (var file in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(file.Filename));
            hash.AppendData(file.SHA1);
        }
        return Convert.ToHexString(hash.GetHashAndReset())[..16];
    }
}
