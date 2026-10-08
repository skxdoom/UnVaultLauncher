namespace UnVault.Core.Install;

/// <summary>Live counters for an install or verify run. Safe to read from a UI thread while it runs.</summary>
public sealed class InstallStatus
{
    private long _downloadedBytes;
    private long _writtenBytes;
    private int _filesDone;
    private int _retries;

    public long DownloadTotal { get; internal set; }
    public long WriteTotal { get; internal set; }
    public int FilesTotal { get; internal set; }

    public long DownloadedBytes => Interlocked.Read(ref _downloadedBytes);
    public long WrittenBytes => Interlocked.Read(ref _writtenBytes);
    public int FilesDone => Volatile.Read(ref _filesDone);

    /// <summary>Failed chunk attempts that were retried (network errors, bad data, CDN failover).</summary>
    public int Retries => Volatile.Read(ref _retries);

    internal void AddDownloaded(long bytes) => Interlocked.Add(ref _downloadedBytes, bytes);
    internal void AddWritten(long bytes) => Interlocked.Add(ref _writtenBytes, bytes);
    internal void FileDone() => Interlocked.Increment(ref _filesDone);
    internal void Retried() => Interlocked.Increment(ref _retries);
}
