namespace Unvault.Core.Manifests;

/// <summary>What toggling one optional component would change on disk.</summary>
public readonly record struct ComponentImpact(
    string Tag,
    bool Installed,
    int FileCount,
    long DiskBytes,
    long DownloadBytes);

public static class ComponentPlanner
{
    /// <summary>
    /// For each optional component: if installed, how much removing it frees; if not, how much
    /// adding it costs (disk, and download of chunks not already needed by installed files).
    /// Shared files (tagged with several components) only count when the toggle really adds/removes them.
    /// </summary>
    public static IReadOnlyList<ComponentImpact> Analyze(Manifest manifest, IReadOnlySet<string> installedTags)
    {
        var currentFiles = manifest.SelectFiles(installedTags).ToHashSet();
        var currentChunks = currentFiles.SelectMany(f => f.ChunkParts).Select(p => p.GUID).ToHashSet();

        var impacts = new List<ComponentImpact>();
        foreach (var tag in manifest.GetInstallTags())
        {
            bool installed = installedTags.Contains(tag);
            var toggled = new HashSet<string>(installedTags, StringComparer.Ordinal);
            if (installed) toggled.Remove(tag); else toggled.Add(tag);

            var changed = installed
                ? currentFiles.Where(f => !InstallSelection.Includes(f, toggled)).ToList()
                : manifest.SelectFiles(toggled).Where(f => !currentFiles.Contains(f)).ToList();

            long download = 0;
            if (!installed)
            {
                var newChunks = changed.SelectMany(f => f.ChunkParts).Select(p => p.GUID)
                    .Where(g => !currentChunks.Contains(g)).ToHashSet();
                download = newChunks.Sum(g => manifest.ChunksByGUID[g].FileSize);
            }

            impacts.Add(new ComponentImpact(tag, installed, changed.Count, changed.Sum(f => f.FileSize), download));
        }

        return impacts;
    }
}
