using System.Text.Json;
using UnVault.Core.EGL;
using UnVault.Core.Install;
using UnVault.Core.Util;

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

/// <summary>What a plugin's .uplugin file says about it: the name and version its author gave it, and its icon if it has one.</summary>
public sealed record PluginDescriptor(string? FriendlyName, string? VersionName, string? IconPath = null);

/// <summary>Fab plugins inside an engine. Nearly all live in Engine\Plugins\Marketplace\{ArtifactID}, whoever installed them.</summary>
public static class EnginePlugins
{
    public static string MarketplaceDirectory(string engineDir) => Path.Combine(engineDir, "Engine", "Plugins", "Marketplace");

    public static string MarketplaceFolder(string engineDir, string artifactID) =>
        Path.Combine(MarketplaceDirectory(engineDir), ArtifactFolderName(artifactID));

    /// <summary>
    /// An artifact ID used as a folder name: its Marketplace folder, its Vault Cache folder, its state in .unvault\plugins.
    /// It comes from Fab or EGL's files, and one like "..." would name the folder above, which removal would then delete.
    /// </summary>
    internal static string ArtifactFolderName(string artifactID) =>
        FileNames.IsPlain(artifactID) ? artifactID : throw new InstallException($"'{artifactID}' can't be used as a folder name.");

    /// <summary>
    /// True for a file inside a plugin's folder under Engine/Plugins. A plugin listing anything else (an engine file)
    /// would overwrite it when installed and delete it when removed.
    /// </summary>
    public static bool IsPluginFile(string filename)
    {
        string[] parts = filename.Split('/', '\\');
        return parts.Length >= 4
            && parts[0].Equals("Engine", StringComparison.OrdinalIgnoreCase)
            && parts[1].Equals("Plugins", StringComparison.OrdinalIgnoreCase)
            && parts.All(FileNames.IsPlain);
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
                found[entry.AppName] = new EnginePlugin(entry.AppName, PluginSource.EGL, folder, CanRemove: Directory.Exists(folder) && !SamePath(folder, MarketplaceDirectory(engineDir)), entry.AppVersion);
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

    /// <summary>
    /// Reads the .uplugin in a plugin's folder, or one folder down where some plugins keep it. Null when there's none or
    /// it can't be read: the engine is the judge of a plugin, this is only for showing it.
    /// </summary>
    public static PluginDescriptor? ReadDescriptor(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
                return null;
            string? path = Directory.EnumerateFiles(folder, "*.uplugin").FirstOrDefault()
                ?? Directory.EnumerateDirectories(folder).SelectMany(d => Directory.EnumerateFiles(d, "*.uplugin")).FirstOrDefault();
            if (path is null)
                return null;

            // The engine's own reader takes trailing commas and comments, so some plugins have them.
            using var json = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            // VersionName is what authors show ("1.2.0"); Version is only a number that goes up with each release.
            string? versionName = Text(root, "VersionName")
                ?? (root.TryGetProperty("Version", out var number) && number.ValueKind == JsonValueKind.Number ? number.GetRawText() : null);
            // Where the engine's Plugins window looks for it too
            string icon = Path.Combine(Path.GetDirectoryName(path)!, "Resources", "Icon128.png");
            return new PluginDescriptor(Text(root, "FriendlyName"), versionName, File.Exists(icon) ? icon : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement json, string name) =>
        json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { } text && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

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
