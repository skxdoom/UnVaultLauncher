using System.Buffers;
using UnVault.Core.Chunks;
using UnVault.Core.Epic;

namespace UnVault.Core.Install;

public sealed class InstallException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Downloads an <see cref="InstallPlan"/>'s chunks in parallel and writes their data into place.</summary>
public sealed class Installer(HttpClient http)
{
    private const int MaxAttemptsPerChunk = 6;

    public int MaxParallelDownloads { get; init; } = 16;

    /// <summary>Wait before the first retry of a failed chunk; doubles with each further attempt.</summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Runs the plan into <paramref name="installDir"/>. Safe to cancel and call again with the same plan:
    /// finished chunks are skipped. Existing files are overwritten in place.
    /// </summary>
    /// <param name="stateDirectory">
    /// Where the resume journal lives. Default: {installDir}\.unvault. Give each kind of content its own folder
    /// when several share an install folder (plugins inside an engine), since a journal clears others in its folder.
    /// </param>
    public async Task InstallAsync(
        InstallPlan plan, string installDir, IReadOnlyList<ChunkSource> sources,
        IReadOnlyDictionary<string, string> secrets, InstallStatus status, CancellationToken cancellationToken,
        string? stateDirectory = null)
    {
        if (plan.Files.Count == 0)
            return; // e.g. an update in which no file changed
        if (sources.Count == 0)
            throw new InstallException("No download locations for this build.");

        using var journal = InstallJournal.Open(stateDirectory ?? InstallJournal.DirectoryFor(installDir), plan);

        status.DownloadTotal = plan.DownloadBytes;
        status.WriteTotal = plan.InstallBytes;
        status.ChunksTotal = plan.Chunks.Count;
        status.FilesTotal = plan.Files.Count;

        var pending = new List<PlannedChunk>();
        foreach (var chunk in plan.Chunks)
        {
            if (journal.Done.Contains(chunk.Info.GUID))
            {
                status.AddDownloaded(chunk.Info.FileSize);
                status.AddWritten(chunk.Writes.Sum(w => (long)w.Size));
                status.ChunkDone();
            }
            else
            {
                pending.Add(chunk);
            }
        }

        using var targets = new FileTargets(plan, installDir, journal.Done, status);
        uint featureLevel = plan.Manifest.FeatureLevel;
        int nextSource = 0;

        await Parallel.ForEachAsync(pending, new ParallelOptions { MaxDegreeOfParallelism = MaxParallelDownloads, CancellationToken = cancellationToken },
            async (chunk, token) =>
            {
                int firstSource = Interlocked.Increment(ref nextSource) % sources.Count;
                byte[] data = ArrayPool<byte>.Shared.Rent((int)Math.Max(chunk.Info.WindowSize, 1024 * 1024));
                try
                {
                    int length = await DownloadAndDecodeAsync(chunk, featureLevel, sources, firstSource, secrets, data, status, token);
                    foreach (var write in chunk.Writes)
                    {
                        if (write.ChunkOffset + write.Size > length)
                            throw new InstallException($"Chunk {chunk.Info.GUID} is {length} bytes, but a file needs bytes up to {write.ChunkOffset + write.Size}.");
                        targets.Write(write, data.AsSpan((int)write.ChunkOffset, (int)write.Size));
                        status.AddWritten(write.Size);
                    }
                    journal.MarkDone(chunk.Info.GUID);
                    status.ChunkDone();
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(data);
                }
            });

        targets.EnsureAllComplete();
        journal.Complete();
    }

    /// <summary>Fetches and decodes one chunk, rotating through CDNs and retrying with backoff.</summary>
    private async Task<int> DownloadAndDecodeAsync(
        PlannedChunk chunk, uint featureLevel, IReadOnlyList<ChunkSource> sources, int firstSource,
        IReadOnlyDictionary<string, string> secrets, byte[] destination, InstallStatus status, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (int attempt = 0; attempt < MaxAttemptsPerChunk; attempt++)
        {
            var source = sources[(firstSource + attempt) % sources.Count];
            try
            {
                byte[] file = await http.GetByteArrayAsync(source.GetChunkURL(chunk.Info, featureLevel), cancellationToken);
                int length = ChunkDecoder.Decode(file, chunk.Info, destination, secrets);
                status.AddDownloaded(file.Length);
                return length;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or ChunkFormatException
                                       || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                lastError = ex;
                status.Retried();
                await Task.Delay(RetryBaseDelay * (1 << attempt), cancellationToken);
            }
        }

        throw new InstallException($"Chunk {chunk.Info.GUID} failed after {MaxAttemptsPerChunk} attempts: {lastError?.Message}", lastError);
    }
}
