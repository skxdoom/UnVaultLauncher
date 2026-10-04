using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using UnVault.Core.Manifests;

namespace UnVault.Core.Install;

public enum FileProblem { Missing, WrongSize, WrongHash }

public readonly record struct BadFile(FileManifest File, FileProblem Problem);

/// <summary>Checks installed files against the manifest's SHA-1s.</summary>
public static class Verifier
{
    /// <summary>
    /// Returns files that are missing, the wrong size or the wrong content. Progress goes to
    /// <paramref name="status"/> (WrittenBytes = bytes checked, FilesDone = files checked).
    /// </summary>
    public static async Task<IReadOnlyList<BadFile>> FindBadFilesAsync(
        IReadOnlyCollection<FileManifest> files, string installDir, InstallStatus status,
        int parallelism = 4, CancellationToken cancellationToken = default)
    {
        status.FilesTotal = files.Count;
        status.WriteTotal = files.Sum(f => f.FileSize);
        var bad = new ConcurrentBag<BadFile>();

        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
            async (file, token) =>
            {
                var problem = await CheckAsync(file, Path.Combine(installDir, file.Filename), status, token);
                if (problem is { } p)
                {
                    bad.Add(new BadFile(file, p));
                    status.AddWritten(file.FileSize); // keep the bar honest when a file is skipped early
                }
                status.FileDone();
            });

        return bad.OrderBy(b => b.File.Filename, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task<FileProblem?> CheckAsync(FileManifest file, string path, InstallStatus status, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            return FileProblem.Missing;
        if (info.Length != file.FileSize)
            return FileProblem.WrongSize;

        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.SequentialScan | FileOptions.Asynchronous);
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                sha1.AppendData(buffer, 0, read);
                status.AddWritten(read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return sha1.GetHashAndReset().AsSpan().SequenceEqual(file.SHA1) ? null : FileProblem.WrongHash;
    }
}
