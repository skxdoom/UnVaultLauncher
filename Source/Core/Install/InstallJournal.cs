using Unvault.Core.Manifests;

namespace Unvault.Core.Install;

/// <summary>
/// Records finished chunks in {state folder}\{planID}.journal, one GUID per line (default state folder:
/// {install}\.unvault), so an interrupted install resumes where it stopped. A chunk is recorded only after all its slices are
/// written. After a crash the OS write cache still holds those writes; after a power cut, run verify.
/// </summary>
internal sealed class InstallJournal : IDisposable
{
    private const int FlushEvery = 64;

    private readonly string _path;
    private readonly StreamWriter _writer;
    private readonly Lock _lock = new();
    private int _unflushed;

    private InstallJournal(string path, HashSet<EpicGUID> done)
    {
        _path = path;
        Done = done;
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read));
    }

    /// <summary>Chunks completed by earlier runs of the same plan.</summary>
    public IReadOnlySet<EpicGUID> Done { get; }

    public static string DirectoryFor(string installDir) => Path.Combine(installDir, ".unvault");

    /// <summary>Opens (or starts) the journal for <paramref name="plan"/> in <paramref name="directory"/>.</summary>
    public static InstallJournal Open(string directory, InstallPlan plan)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, plan.ID + ".journal");

        var done = new HashSet<EpicGUID>();
        if (File.Exists(path))
        {
            foreach (string line in File.ReadLines(path))
            {
                // The last line may be cut off if the process was killed mid-write.
                if (line.Length == 32)
                    done.Add(EpicGUID.Parse(line));
            }
        }

        // Journals of other plans (different build or selection) can't be trusted for this one.
        foreach (string stale in Directory.EnumerateFiles(directory, "*.journal"))
        {
            if (!string.Equals(stale, path, StringComparison.OrdinalIgnoreCase))
                File.Delete(stale);
        }

        return new InstallJournal(path, done);
    }

    public void MarkDone(EpicGUID chunk)
    {
        lock (_lock)
        {
            _writer.WriteLine(chunk.ToString());
            if (++_unflushed >= FlushEvery)
            {
                _writer.Flush();
                _unflushed = 0;
            }
        }
    }

    /// <summary>The install finished: the journal is no longer needed.</summary>
    public void Complete()
    {
        lock (_lock)
        {
            _writer.Dispose();
            File.Delete(_path);
        }
    }

    public void Dispose()
    {
        lock (_lock)
            _writer.Dispose();
    }
}
