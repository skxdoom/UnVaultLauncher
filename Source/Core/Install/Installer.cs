using System.Buffers;
using System.Diagnostics;
using System.Net;
using UnVault.Core.Chunks;
using UnVault.Core.Epic;
using UnVault.Core.Manifests;

namespace UnVault.Core.Install;

public sealed class InstallException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Downloads an <see cref="InstallPlan"/>'s chunks in parallel and writes their data into place.</summary>
public sealed class Installer(HttpClient http)
{
    public int MaxParallelDownloads { get; init; } = 16;

    /// <summary>Wait before the first retry of a failed chunk; doubles with each further attempt, up to <see cref="MaxRetryDelay"/>.</summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a chunk may keep failing, on every download server, before the install stops: long enough to ride out
    /// a dropped Wi-Fi connection or a router restart.
    /// </summary>
    public TimeSpan GiveUpAfter { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>A chunk download that receives nothing for this long is dropped and tried again.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);

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
        var done = StillWritten(plan, installDir, journal.Done);

        status.DownloadTotal = plan.DownloadBytes;
        status.WriteTotal = plan.InstallBytes;
        status.ChunksTotal = plan.Chunks.Count;
        status.FilesTotal = plan.Files.Count;

        var pending = new List<PlannedChunk>();
        foreach (var chunk in plan.Chunks)
        {
            if (done.Contains(chunk.Info.GUID))
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

        using var targets = new FileTargets(plan, installDir, done, status);
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

    /// <summary>
    /// The journal's chunks whose files are all still there at full size. The journal only says the data was written:
    /// a file deleted or cut short since (a component removed and added again, a plugin folder deleted) has its
    /// chunks downloaded again instead of being taken as done.
    /// </summary>
    private static HashSet<EpicGUID> StillWritten(InstallPlan plan, string installDir, IReadOnlySet<EpicGUID> journaled)
    {
        var done = journaled.ToHashSet();
        if (done.Count == 0)
            return done;

        var intact = new bool?[plan.Files.Count];
        foreach (var chunk in plan.Chunks)
        {
            if (done.Contains(chunk.Info.GUID) && chunk.Writes.Any(w => (intact[w.FileIndex] ??= IsFullSize(plan.Files[w.FileIndex])) == false))
                done.Remove(chunk.Info.GUID);
        }
        return done;

        bool IsFullSize(FileManifest file)
        {
            var info = new FileInfo(Path.Combine(installDir, file.Filename));
            return info.Exists && info.Length == file.FileSize;
        }
    }

    /// <summary>
    /// Fetches and decodes one chunk, rotating through the download servers. Dropped connections, server errors, stalled
    /// downloads and damaged data are tried again with growing waits, until the chunk has failed for
    /// <see cref="GiveUpAfter"/>. A server refusing the chunk (expired signed links) isn't asked again.
    /// </summary>
    private async Task<int> DownloadAndDecodeAsync(
        PlannedChunk chunk, uint featureLevel, IReadOnlyList<ChunkSource> sources, int firstSource,
        IReadOnlyDictionary<string, string> secrets, byte[] destination, InstallStatus status, CancellationToken cancellationToken)
    {
        var failing = Stopwatch.StartNew();
        var refused = new HashSet<int>();
        Exception? lastError = null;
        for (int attempt = 0; ; attempt++)
        {
            int source = (firstSource + attempt) % sources.Count;
            if (refused.Contains(source))
                continue;
            try
            {
                var (file, length) = await FetchAsync(sources[source].GetChunkURL(chunk.Info, featureLevel), chunk.Info.FileSize, cancellationToken);
                try
                {
                    int decoded = ChunkDecoder.Decode(file, length, chunk.Info, destination, secrets);
                    status.AddDownloaded(length);
                    return decoded;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(file);
                }
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                // Waiting won't change a refusal; another server may still have the chunk.
                refused.Add(source);
                if (refused.Count == sources.Count)
                    throw new InstallException($"The download servers refused chunk {chunk.Info.GUID} ({(int)ex.StatusCode!.Value} {ex.StatusCode}). Their links may have expired: " +
                                               "run it again to get new ones. What's downloaded so far is kept.", ex);
                lastError = ex;
                continue;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or ChunkFormatException
                                       || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                lastError = ex; // the OperationCanceledException is a stalled download or a request timeout, not the user
            }

            // Every server gets asked at least once.
            if (attempt + 1 >= sources.Count && failing.Elapsed >= GiveUpAfter)
                throw new InstallException($"Chunk {chunk.Info.GUID} couldn't be downloaded: {lastError?.Message}", lastError);
            status.Retried();
            await Task.Delay(RetryDelay(attempt), cancellationToken);
        }
    }

    /// <summary>Doubling waits up to <see cref="MaxRetryDelay"/>, spread a little so parallel downloads don't all retry at once.</summary>
    private TimeSpan RetryDelay(int attempt)
    {
        double milliseconds = Math.Min(RetryBaseDelay.TotalMilliseconds * Math.Pow(2, Math.Min(attempt, 20)), MaxRetryDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(milliseconds * (0.8 + 0.4 * Random.Shared.NextDouble()));
    }

    /// <summary>
    /// Downloads a chunk file into a pooled buffer (the caller returns it). Instead of a limit on the whole download, which
    /// a big chunk on a slow line could exceed, a download is dropped once no data has arrived for <see cref="StallTimeout"/>.
    /// </summary>
    private async Task<(byte[] File, int Length)> FetchAsync(string url, long expectedSize, CancellationToken cancellationToken)
    {
        using var stalled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stalled.CancelAfter(StallTimeout);
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stalled.Token);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(stalled.Token);

        // The manifest says how big the file is; a server sending far more than that isn't sending this chunk.
        long limit = Math.Max(expectedSize, 64 * 1024) * 4;
        byte[] buffer = ArrayPool<byte>.Shared.Rent((int)Math.Clamp(expectedSize, 4096, limit));
        int length = 0;
        try
        {
            while (true)
            {
                if (length == buffer.Length)
                {
                    if (buffer.Length >= limit)
                        throw new ChunkFormatException("The server sent more data than the manifest lists for this chunk.");
                    byte[] larger = ArrayPool<byte>.Shared.Rent((int)Math.Min((long)buffer.Length * 2, limit));
                    buffer.AsSpan(0, length).CopyTo(larger);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = larger;
                }
                int read = await body.ReadAsync(buffer.AsMemory(length), stalled.Token);
                if (read == 0)
                    return (buffer, length);
                length += read;
                stalled.CancelAfter(StallTimeout); // still receiving
            }
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }
}
