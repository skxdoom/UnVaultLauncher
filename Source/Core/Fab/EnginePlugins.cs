using System.Text.Json;
using UnVault.Core.EGL;
using UnVault.Core.Install;
using UnVault.Core.Manifests;
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
/// Ours (we kept the file list), or it has a folder of its own in Engine\Plugins\Marketplace. EGL puts a few plugins
/// elsewhere (a few of Epic's own go straight into Engine\Plugins\<Name>); without a file list of our own there's no
/// telling their files from the engine's own.
/// </param>
/// <param name="BuildVersion">The installed build (as Fab lists it), when UnVault or EGL recorded one; unknown for a bare folder.</param>
public sealed record EnginePlugin(string ArtifactID, PluginSource Source, string Folder, bool CanRemove, string? BuildVersion = null);

/// <summary>What a plugin's .uplugin file says about it: the name and version its author gave it, and its icon if it has one.</summary>
public sealed record PluginDescriptor(string? FriendlyName, string? VersionName, string? IconPath = null);

/// <summary>Fab plugins inside an engine. Nearly all live in a folder of their own in Engine\Plugins\Marketplace, whoever installed them.</summary>
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
    /// <param name="eglItems">EGL's records of its installs (all engines), which say where it keeps each plugin's file list.</param>
    public static IReadOnlyList<EnginePlugin> Find(string engineDir, IEnumerable<LauncherInstalledEntry> launcherInstalled, IEnumerable<EGLItem>? eglItems = null)
    {
        var found = new Dictionary<string, EnginePlugin>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in PluginInstalls.List(engineDir))
        {
            string folder = FolderOf(engineDir, () => PluginInstalls.Load(engineDir, record.ArtifactID)?.Manifest) ?? SafeFolder(engineDir, record.ArtifactID);
            found[record.ArtifactID] = new EnginePlugin(record.ArtifactID, PluginSource.UnVault, folder, CanRemove: true, record.BuildVersion);
        }

        var items = (eglItems ?? []).Where(i => SamePath(i.InstallLocation, engineDir)).ToList();
        foreach (var entry in launcherInstalled)
        {
            if (!EngineLibrary.IsEngineApp(entry.AppName) && SamePath(entry.InstallLocation, engineDir) && !found.ContainsKey(entry.AppName))
            {
                var item = items.FirstOrDefault(i => string.Equals(i.AppName, entry.AppName, StringComparison.OrdinalIgnoreCase));
                string folder = (item is not null && ManifestPathOf(item) is { } path ? FolderOf(engineDir, () => Manifest.Load(path)) : null)
                    ?? SafeFolder(engineDir, entry.AppName);
                // Without a file list, only a plugin's own folder in Marketplace can be told apart from the engine's files.
                found[entry.AppName] = new EnginePlugin(entry.AppName, PluginSource.EGL, folder, CanRemove: Directory.Exists(folder) && IsMarketplaceFolder(engineDir, folder), entry.AppVersion);
            }
        }

        // Plugin folders none of the above accounts for. A plugin's folder can be named otherwise than its artifact.
        var claimed = found.Values.Select(p => Path.TrimEndingDirectorySeparator(p.Folder)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string marketplace = MarketplaceDirectory(engineDir);
        if (Directory.Exists(marketplace))
        {
            foreach (string folder in Directory.EnumerateDirectories(marketplace))
            {
                string name = Path.GetFileName(folder);
                if (!found.ContainsKey(name) && !claimed.Contains(folder))
                    found[name] = new EnginePlugin(name, PluginSource.Unlisted, folder, CanRemove: true);
            }
        }

        return [.. found.Values];
    }

    /// <summary>
    /// The plugin's own folder, from its file list: Engine\Plugins\Marketplace\{Name} for nearly all, Engine\Plugins\{Name}
    /// for a few of Epic's own (Bridge, Fab). Not always named after the artifact: Marketplace-era plugins often go by the
    /// plugin's name (MyPlugin_4.27 in Marketplace\MyPlugin). Null when the files aren't all in one plugin's folder.
    /// </summary>
    public static string? FolderOf(Manifest manifest, string engineDir)
    {
        string? folder = null;
        foreach (var file in manifest.Files)
        {
            if (!IsPluginFile(file.Filename))
                return null;
            string[] parts = file.Filename.Split('/', '\\');
            int length = parts[2].Equals("Marketplace", StringComparison.OrdinalIgnoreCase) ? 4 : 3;
            if (parts.Length <= length)
                return null; // a file in Engine\Plugins\Marketplace itself
            string own = string.Join('/', parts[..length]);
            if (folder is null)
                folder = own;
            else if (!folder.Equals(own, StringComparison.OrdinalIgnoreCase))
                return null; // more than one plugin's folder
        }
        return folder is null ? null : Path.Combine(engineDir, folder.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>A plugin's own folder in Engine\Plugins\Marketplace: the one kind removed whole when there's no file list.</summary>
    public static bool IsMarketplaceFolder(string engineDir, string folder)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return SamePath(Path.GetDirectoryName(full) ?? "", MarketplaceDirectory(engineDir)) && FileNames.IsPlain(Path.GetFileName(full));
    }

    /// <summary>A file list that can't be read just leaves the folder to be guessed from the artifact's name.</summary>
    private static string? FolderOf(string engineDir, Func<Manifest?> load)
    {
        try
        {
            return load() is { } manifest ? FolderOf(manifest, engineDir) : null;
        }
        catch (Exception ex) when (ex is ManifestFormatException or IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Where EGL keeps the file list of an install: its .item names it, or it's {InstallationGuid}.manifest in the install's .egstore.</summary>
    private static string? ManifestPathOf(EGLItem item) =>
        item.CompleteManifestPath.Length > 0 ? item.CompleteManifestPath
        : item.ManifestLocation.Length > 0 && FileNames.IsPlain(item.InstallationGUID) ? Path.Combine(item.ManifestLocation, item.InstallationGUID + ".manifest")
        : null;

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
