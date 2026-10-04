using System.Collections.Concurrent;
using Microsoft.Win32.SafeHandles;
using UnVault.Core.Manifests;

namespace UnVault.Core.Install;

/// <summary>
/// The files an install writes into. Chunk slices arrive in any order from parallel downloads, so
/// writes are positional; a file's handle opens on its first write and closes when its last byte lands.
/// </summary>
internal sealed class FileTargets : IDisposable
{
    private readonly InstallPlan _plan;
    private readonly string _root;
    private readonly InstallStatus _status;
    private readonly long[] _remaining;
    private readonly SafeFileHandle?[] _handles;
    private readonly Lock[] _locks = Enumerable.Range(0, 64).Select(_ => new Lock()).ToArray();
    private readonly ConcurrentDictionary<string, bool> _createdDirectories = new(StringComparer.OrdinalIgnoreCase);

    public FileTargets(InstallPlan plan, string installDir, IReadOnlySet<EpicGUID> doneChunks, InstallStatus status)
    {
        _plan = plan;
        _root = Path.GetFullPath(installDir);
        _status = status;
        _remaining = plan.Files.Select(f => f.FileSize).ToArray();
        _handles = new SafeFileHandle?[plan.Files.Count];

        foreach (var chunk in plan.Chunks)
        {
            if (!doneChunks.Contains(chunk.Info.GUID))
                continue;
            foreach (var write in chunk.Writes)
                _remaining[write.FileIndex] -= write.Size;
        }

        for (int i = 0; i < plan.Files.Count; i++)
        {
            if (_remaining[i] != 0)
                continue;
            // Empty files never receive a chunk write; create them now. Others were finished by an earlier run.
            if (plan.Files[i].FileSize == 0)
                CreateEmpty(i);
            status.FileDone();
        }
    }

    public void Write(ChunkWrite write, ReadOnlySpan<byte> data)
    {
        int index = write.FileIndex;
        var handle = GetHandle(index);
        RandomAccess.Write(handle, data, write.FileOffset);

        // Only the writer that lands the last byte gets zero, so nobody else is still using the handle.
        if (Interlocked.Add(ref _remaining[index], -write.Size) == 0)
            Finish(index);
    }

    /// <summary>Throws if any file is still incomplete (a planning bug, or an interrupted run).</summary>
    public void EnsureAllComplete()
    {
        int incomplete = _remaining.Count(r => r != 0);
        if (incomplete > 0)
            throw new IOException($"{incomplete} files were not fully written.");
    }

    public void Dispose()
    {
        foreach (var handle in _handles)
            handle?.Dispose();
    }

    private SafeFileHandle GetHandle(int index)
    {
        var existing = Volatile.Read(ref _handles[index]);
        if (existing is not null)
            return existing;

        lock (_locks[index % _locks.Length])
        {
            if (_handles[index] is { } opened)
                return opened;

            string path = ResolvePath(index);
            PrepareForWrite(path);
            var handle = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
            long size = _plan.Files[index].FileSize;
            if (RandomAccess.GetLength(handle) != size)
                RandomAccess.SetLength(handle, size);

            Volatile.Write(ref _handles[index], handle);
            return handle;
        }
    }

    private void Finish(int index)
    {
        lock (_locks[index % _locks.Length])
        {
            _handles[index]?.Dispose();
            _handles[index] = null;
        }
        ApplyFlags(index);
        _status.FileDone();
    }

    private void CreateEmpty(int index)
    {
        string path = ResolvePath(index);
        PrepareForWrite(path);
        File.WriteAllBytes(path, []);
        ApplyFlags(index);
    }

    private void ApplyFlags(int index)
    {
        if ((_plan.Files[index].Flags & FileFlags.ReadOnly) != 0)
        {
            string path = ResolvePath(index);
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        }
    }

    /// <summary>Creates the folder and clears a read-only flag left by a previous install, so we can overwrite.</summary>
    private void PrepareForWrite(string path)
    {
        // Create before remembering: another writer must never see the folder as done while it doesn't exist yet.
        string directory = Path.GetDirectoryName(path)!;
        if (!_createdDirectories.ContainsKey(directory))
        {
            Directory.CreateDirectory(directory);
            _createdDirectories.TryAdd(directory, true);
        }

        if (File.Exists(path))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }

    /// <summary>Manifest path → absolute path, refusing anything that escapes the install folder.</summary>
    private string ResolvePath(int index)
    {
        string full = Path.GetFullPath(Path.Combine(_root, _plan.Files[index].Filename));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Manifest path escapes the install folder: {_plan.Files[index].Filename}");
        return full;
    }
}
