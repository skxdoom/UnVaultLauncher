namespace UnVault.Core.Util;

/// <summary>Checks names that come from Epic or another launcher's files before they become part of a path.</summary>
public static class FileNames
{
    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// True for one file or folder name that Windows takes as written. Windows drops trailing dots and spaces, so
    /// "..." or "Plugin." would name the parent folder or another one, and "CON" or "NUL.txt" name a device, not a file.
    /// </summary>
    public static bool IsPlain(string? name) =>
        !string.IsNullOrEmpty(name)
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && name[^1] is not ('.' or ' ')
        && !DeviceNames.Contains(Path.GetFileNameWithoutExtension(name).TrimEnd());
}
