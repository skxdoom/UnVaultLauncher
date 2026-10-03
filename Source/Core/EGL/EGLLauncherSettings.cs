namespace Unvault.Core.EGL;

/// <summary>
/// The Epic Games Launcher's own settings that matter to us, read from its GameUserSettings.ini
/// ([Launcher] section; repeated keys form a list, as in all UE config files).
/// </summary>
public sealed record EGLLauncherSettings(
    IReadOnlyList<string> VaultCacheDirectories,
    string? DefaultAppInstallLocation,
    IReadOnlyList<string> CreatedProjectPaths)
{
    public static readonly EGLLauncherSettings Empty = new([], null, []);

    /// <summary>The VaultCache EGL uses: the last configured folder that exists (newer entries are appended).</summary>
    public string? ActiveVaultCache => VaultCacheDirectories.LastOrDefault(Directory.Exists);

    /// <summary>EGL writes this under WindowsEditor; older versions used Windows.</summary>
    public static IEnumerable<string> CandidatePaths
    {
        get
        {
            string config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EpicGamesLauncher", "Saved", "Config");
            yield return Path.Combine(config, "WindowsEditor", "GameUserSettings.ini");
            yield return Path.Combine(config, "Windows", "GameUserSettings.ini");
        }
    }

    public static EGLLauncherSettings Read() =>
        CandidatePaths.Select(ReadFrom).FirstOrDefault(s => s is not null) ?? Empty;

    /// <summary>Null when the file is missing or has none of these settings.</summary>
    public static EGLLauncherSettings? ReadFrom(string path)
    {
        if (!File.Exists(path))
            return null;

        var vaults = new List<string>();
        var projects = new List<string>();
        string? installLocation = null;
        bool inLauncher = false;

        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.StartsWith('['))
            {
                inLauncher = line.Equals("[Launcher]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inLauncher)
                continue;

            int equals = line.IndexOf('=');
            if (equals <= 0)
                continue;
            // UE config "+Key=" appends to an array, same as a repeated plain key here.
            string key = line[..equals].TrimStart('+');
            string value = line[(equals + 1)..].Trim();
            if (value.Length == 0)
                continue;

            switch (key)
            {
                case "VaultCacheDirectories": vaults.Add(NormalizeFolder(value)); break;
                case "DefaultAppInstallLocation": installLocation = NormalizeFolder(value); break;
                case "CreatedProjectPaths": projects.Add(NormalizeFolder(value)); break;
            }
        }

        return vaults.Count == 0 && installLocation is null && projects.Count == 0
            ? null
            : new EGLLauncherSettings(vaults, installLocation, projects);
    }

    /// <summary>"E:/Epic Games/VaultCache/" → "E:\Epic Games\VaultCache".</summary>
    private static string NormalizeFolder(string value)
    {
        string path = value.Replace('/', Path.DirectorySeparatorChar);
        // "E:" alone means the drive root, not the current folder on E:.
        if (path.Length == 2 && path[1] == ':')
            path += Path.DirectorySeparatorChar;
        return path.Length > 3 ? Path.TrimEndingDirectorySeparator(path) : path;
    }
}
