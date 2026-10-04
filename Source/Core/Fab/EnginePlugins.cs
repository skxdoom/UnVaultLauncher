using UnVault.Core.EGL;
using UnVault.Core.Install;

namespace UnVault.Core.Fab;

/// <summary>How a Fab plugin got into an engine, which decides how it can be removed.</summary>
public enum PluginSource
{
    /// <summary>Installed by UnVault Launcher: its exact file list is kept.</summary>
    UnVault,

    /// <summary>Listed in EGL's LauncherInstalled.dat.</summary>
    EGL,

    /// <summary>Only the folder: copied in by hand or by another tool, or EGL lost track of it.</summary>
    Unlisted,
}

/// <param name="CanRemove">
/// Ours (we kept the file list), or its Engine\Plugins\Marketplace\{ArtifactID} folder exists. EGL puts a few
/// plugins elsewhere (a few of Epic's own go straight into Engine\Plugins\<Name>); without a file
/// list there's no telling their files from the engine's own.
/// </param>
/// <param name="BuildVersion">The installed build (as Fab lists it), when UnVault or EGL recorded one; unknown for a bare folder.</param>
public sealed record EnginePlugin(string ArtifactID, PluginSource Source, string Folder, bool CanRemove, string? BuildVersion = null);

/// <summary>Fab plugins inside an engine. Nearly all live in Engine\Plugins\Marketplace\{ArtifactID}, whoever installed them.</summary>
public static class EnginePlugins
{
    public static string MarketplaceDirectory(string engineDir) => Path.Combine(engineDir, "Engine", "Plugins", "Marketplace");

    public static string MarketplaceFolder(string engineDir, string artifactID)
    {
        if (artifactID.Length == 0 || artifactID is "." or ".." || artifactID.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InstallException($"'{artifactID}' isn't a plugin folder name.");
        return Path.Combine(MarketplaceDirectory(engineDir), artifactID);
    }

    /// <summary>The plugins UnVault, EGL or anything else put into this engine.</summary>
    /// <param name="launcherInstalled">EGL's LauncherInstalled.dat entries (all engines; filtered here).</param>
    public static IReadOnlyList<EnginePlugin> Find(string engineDir, IEnumerable<LauncherInstalledEntry> launcherInstalled)
    {
        var found = new Dictionary<string, EnginePlugin>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in PluginInstalls.List(engineDir))
            found[record.ArtifactID] = new EnginePlugin(record.ArtifactID, PluginSource.UnVault, SafeFolder(engineDir, record.ArtifactID), CanRemove: true, record.BuildVersion);

        foreach (var entry in launcherInstalled)
        {
            if (!EngineLibrary.IsEngineApp(entry.AppName) && SamePath(entry.InstallLocation, engineDir) && !found.ContainsKey(entry.AppName))
            {
                string folder = SafeFolder(engineDir, entry.AppName);
                found[entry.AppName] = new EnginePlugin(entry.AppName, PluginSource.EGL, folder, CanRemove: Directory.Exists(folder) && folder != MarketplaceDirectory(engineDir), entry.AppVersion);
            }
        }

        string marketplace = MarketplaceDirectory(engineDir);
        if (Directory.Exists(marketplace))
        {
            foreach (string folder in Directory.EnumerateDirectories(marketplace))
            {
                string name = Path.GetFileName(folder);
                if (!found.ContainsKey(name))
                    found[name] = new EnginePlugin(name, PluginSource.Unlisted, folder, CanRemove: true);
            }
        }

        return [.. found.Values];
    }

    /// <summary>Bytes in a plugin's folder (0 if it's gone).</summary>
    public static long FolderSize(string folder)
    {
        if (!Directory.Exists(folder))
            return 0;
        return new DirectoryInfo(folder).EnumerateFiles("*", AllFiles).Sum(f => f.Length);
    }

    /// <summary>Every file, hidden ones included, without following junctions out of the folder.</summary>
    internal static readonly EnumerationOptions AllFiles = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = false,
    };

    private static string SafeFolder(string engineDir, string artifactID)
    {
        try
        {
            return MarketplaceFolder(engineDir, artifactID);
        }
        catch (InstallException)
        {
            return MarketplaceDirectory(engineDir); // only shown; removal re-checks the name
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);
}
