namespace UnVault.Core.Util;

/// <summary>Paths named by a manifest, kept inside the folder they're written to or deleted from.</summary>
public static class ContainedPath
{
    /// <summary>
    /// The full path of <paramref name="relative"/> inside <paramref name="root"/> (a full path), refusing one that would
    /// land elsewhere, such as a ".." or a full path in a manifest.
    /// </summary>
    public static string Resolve(string root, string relative, string folderName = "install folder")
    {
        string full = Path.GetFullPath(Path.Combine(root, relative));
        return IsInside(root, full) ? full : throw new IOException($"Manifest path escapes the {folderName}: {relative}");
    }

    /// <summary>
    /// True when <paramref name="path"/> is under <paramref name="root"/>, both full paths. The root may end in a
    /// separator, as a typed "E:\Engines\" or a drive root "E:\" does.
    /// </summary>
    public static bool IsInside(string root, string path)
    {
        string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return path.Length > prefix.Length && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
