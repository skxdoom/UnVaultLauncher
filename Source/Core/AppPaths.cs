namespace UnVault.Core;

public static class AppPaths
{
    private static readonly Lazy<string> Resolved =
        new(() => ResolveDataDirectory(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));

    private static string? _override;

    /// <summary>%LOCALAPPDATA%\UnVaultLauncher on Windows, ~/.local/share/UnVaultLauncher elsewhere.</summary>
    public static string DataDirectory => _override ?? Resolved.Value;

    /// <summary>
    /// Puts sign-in, settings and caches in another folder. Tests use one of their own, so they never read the user's
    /// session or settings, clean up their thumbnails, or move their old data folder.
    /// </summary>
    internal static void UseDataDirectory(string directory) => _override = directory;

    public static string ManifestCacheDirectory => Path.Combine(DataDirectory, "manifests");

    /// <summary>
    /// The data folder was called just "Unvault" before it was named after the product. Whatever is still in the
    /// old folder (sign-in, settings, caches) is moved over; where both have a file, the newer one is kept. Anything
    /// that can't be moved yet, e.g. because another copy of the launcher has it open, follows on a later start.
    /// </summary>
    internal static string ResolveDataDirectory(string root)
    {
        string current = Path.Combine(root, "UnVaultLauncher");
        string old = Path.Combine(root, "Unvault");
        if (!Directory.Exists(old))
            return current;
        try
        {
            if (Directory.Exists(current))
                MergeInto(old, current);
            else
                Directory.Move(old, current); // one rename, the usual case
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Partly moved at most; the rest is merged next time.
        }
        return current;
    }

    private static void MergeInto(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string directory in Directory.GetDirectories(from))
        {
            string target = Path.Combine(to, Path.GetFileName(directory));
            if (Directory.Exists(target))
                MergeInto(directory, target);
            else
                Directory.Move(directory, target);
        }
        foreach (string file in Directory.GetFiles(from))
        {
            string target = Path.Combine(to, Path.GetFileName(file));
            if (!File.Exists(target) || File.GetLastWriteTimeUtc(file) > File.GetLastWriteTimeUtc(target))
                File.Move(file, target, overwrite: true);
            else
                File.Delete(file); // the copy already in the new folder is newer
        }
        Directory.Delete(from);
    }
}
