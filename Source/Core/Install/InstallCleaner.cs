using Unvault.Core.Manifests;

namespace Unvault.Core.Install;

/// <summary>Removes files of deselected components from an install, then any folders left empty.</summary>
public static class InstallCleaner
{
    public readonly record struct Result(int FilesDeleted, long BytesFreed, int FoldersRemoved);

    public static Result DeleteFiles(string installDir, IEnumerable<FileManifest> files, CancellationToken cancellationToken = default)
    {
        string root = Path.GetFullPath(installDir);
        var parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int deleted = 0;
        long freed = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = Path.GetFullPath(Path.Combine(root, file.Filename));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Manifest path escapes the install folder: {file.Filename}");

            var info = new FileInfo(path);
            if (!info.Exists)
                continue;
            if (info.IsReadOnly)
                info.IsReadOnly = false;

            freed += info.Length;
            info.Delete();
            deleted++;
            parents.Add(info.DirectoryName!);
        }

        // Deepest first, so a folder emptied by removing its subfolders goes too. Never above the install root.
        int folders = 0;
        foreach (string directory in parents.SelectMany(p => Ancestors(p, root)).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(d => d.Length))
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
                folders++;
            }
        }

        return new Result(deleted, freed, folders);
    }

    /// <summary>Deletes a folder and everything in it (read-only files too); a junction inside is unlinked, not followed.</summary>
    public static Result DeleteFolder(string folder)
    {
        var directory = new DirectoryInfo(folder);
        if (!directory.Exists)
            return default;

        int files = 0, folders = 1;
        long freed = 0;
        foreach (var file in directory.EnumerateFiles("*", Fab.EnginePlugins.AllFiles))
        {
            if (file.IsReadOnly)
                file.IsReadOnly = false;
            freed += file.Length;
            files++;
        }
        folders += directory.EnumerateDirectories("*", Fab.EnginePlugins.AllFiles).Count();

        directory.Delete(recursive: true);
        return new Result(files, freed, folders);
    }

    private static IEnumerable<string> Ancestors(string directory, string root)
    {
        for (string? current = directory;
             current is not null && current.Length > root.Length && current.StartsWith(root, StringComparison.OrdinalIgnoreCase);
             current = Path.GetDirectoryName(current))
        {
            yield return current;
        }
    }
}
