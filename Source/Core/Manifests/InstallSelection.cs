namespace Unvault.Core.Manifests;

public readonly record struct InstallPlanSize(int FileCount, long InstallBytes, int ChunkCount, long DownloadBytes);

/// <summary>
/// Which files of a manifest get installed for a given set of optional components (install tags).
/// </summary>
public static class InstallSelection
{
    /// <summary>
    /// EGL's rule: untagged files are always installed; a tagged file is installed when ANY of its
    /// tags is selected. Verified byte-exact against an EGL-made UE 5.7 install (core + templates +
    /// engine_source). Note this means e.g. platform_Linux also pulls in Linux .pdb files that are
    /// tagged editor_symbols + platform_Linux.
    /// </summary>
    public static bool Includes(FileManifest file, IReadOnlySet<string> selectedTags)
    {
        if (file.InstallTags.Count == 0)
            return true;
        foreach (var tag in file.InstallTags)
        {
            if (selectedTags.Contains(tag))
                return true;
        }
        return false;
    }

    public static IEnumerable<FileManifest> SelectFiles(this Manifest manifest, IReadOnlySet<string> selectedTags) =>
        manifest.Files.Where(f => Includes(f, selectedTags));

    /// <summary>All optional components this build offers, sorted by name.</summary>
    public static IReadOnlyList<string> GetInstallTags(this Manifest manifest) =>
        manifest.Files.SelectMany(f => f.InstallTags).Where(t => t.Length > 0).Distinct().Order(StringComparer.Ordinal).ToList();

    /// <summary>Install size and download size (unique chunks) for a selection.</summary>
    public static InstallPlanSize MeasureSelection(this Manifest manifest, IReadOnlySet<string> selectedTags)
    {
        int fileCount = 0;
        long installBytes = 0;
        var chunkGUIDs = new HashSet<EpicGUID>();

        foreach (var file in manifest.SelectFiles(selectedTags))
        {
            fileCount++;
            installBytes += file.FileSize;
            foreach (var part in file.ChunkParts)
                chunkGUIDs.Add(part.GUID);
        }

        long downloadBytes = 0;
        foreach (var guid in chunkGUIDs)
            downloadBytes += manifest.ChunksByGUID[guid].FileSize;

        return new InstallPlanSize(fileCount, installBytes, chunkGUIDs.Count, downloadBytes);
    }

    /// <summary>
    /// Works out which optional components an existing install has, by checking whether files that
    /// belong only to that component exist on disk. Used to adopt installs made by EGL, whose
    /// records list components as opaque GUIDs rather than tag names.
    /// </summary>
    public static IReadOnlySet<string> DetectInstalledTags(this Manifest manifest, string installDir, int samplesPerTag = 40)
    {
        var installed = new HashSet<string>(StringComparer.Ordinal);
        var exclusiveByTag = manifest.Files
            .Where(f => f.InstallTags.Count == 1 && f.InstallTags[0].Length > 0)
            .GroupBy(f => f.InstallTags[0]);

        foreach (var group in exclusiveByTag)
        {
            var sample = group.Take(samplesPerTag).ToList();
            int present = sample.Count(f => File.Exists(Path.Combine(installDir, f.Filename)));
            if (present * 2 > sample.Count)
                installed.Add(group.Key);
        }

        return installed;
    }
}
