using System.Text.Json;
using System.Text.Json.Serialization;
using UnVault.Core.Install;
using UnVault.Core.Manifests;
using UnVault.Core.Util;

namespace UnVault.Core.Fab;

/// <summary>A Fab plugin we installed into an engine, kept in {engine}\.unvault\plugins\{artifact}\ with its manifest.</summary>
public sealed class PluginRecord
{
    public string ArtifactID { get; set; } = "";
    public string Title { get; set; } = "";
    public string BuildVersion { get; set; } = "";
    public string AssetNamespace { get; set; } = "";
    public string AssetID { get; set; } = "";
    public long InstallSize { get; set; }
    public DateTimeOffset InstalledAt { get; set; }
}

public static class PluginInstalls
{
    public static string StateDirectory(string engineDir, string artifactID) =>
        Path.Combine(InstallJournal.DirectoryFor(engineDir), "plugins", EnginePlugins.ArtifactFolderName(artifactID));

    private static string RecordPath(string engineDir, string artifactID) => Path.Combine(StateDirectory(engineDir, artifactID), "plugin.json");
    private static string ManifestPath(string engineDir, string artifactID) => Path.Combine(StateDirectory(engineDir, artifactID), "install.manifest");

    /// <summary>
    /// The file list alone, kept as an install starts: an interrupted one then still says which folder and files are its,
    /// to continue or remove. The record follows once every file is there.
    /// </summary>
    public static void SaveManifest(string engineDir, string artifactID, byte[] rawManifest)
    {
        Directory.CreateDirectory(StateDirectory(engineDir, artifactID));
        AtomicFile.WriteAllBytes(ManifestPath(engineDir, artifactID), rawManifest);
    }

    /// <summary>The kept file list, also of an install that didn't finish; null when there's none.</summary>
    public static Manifest? LoadManifest(string engineDir, string artifactID)
    {
        string path = ManifestPath(engineDir, artifactID);
        return File.Exists(path) ? Manifest.Load(path) : null;
    }

    /// <summary>
    /// Installs into this engine that started but didn't finish: their state is there (the file list, or the resume
    /// journal of an install from before the list was kept first), but no record. Installing again continues them.
    /// </summary>
    public static IReadOnlyList<string> Unfinished(string engineDir)
    {
        string root = Path.Combine(InstallJournal.DirectoryFor(engineDir), "plugins");
        if (!Directory.Exists(root))
            return [];
        return [.. Directory.EnumerateDirectories(root)
            .Where(folder => !File.Exists(Path.Combine(folder, "plugin.json"))
                             && (File.Exists(Path.Combine(folder, "install.manifest")) || Directory.EnumerateFiles(folder, "*.journal").Any()))
            .Select(folder => Path.GetFileName(folder))];
    }

    public static void Save(string engineDir, PluginRecord record, byte[] rawManifest)
    {
        string directory = StateDirectory(engineDir, record.ArtifactID);
        Directory.CreateDirectory(directory);
        AtomicFile.WriteAllBytes(ManifestPath(engineDir, record.ArtifactID), rawManifest);
        AtomicFile.WriteAllText(RecordPath(engineDir, record.ArtifactID), JsonSerializer.Serialize(record, PluginJSONContext.Default.PluginRecord));
    }

    public static (PluginRecord Record, Manifest Manifest)? Load(string engineDir, string artifactID)
    {
        string recordPath = RecordPath(engineDir, artifactID);
        string manifestPath = ManifestPath(engineDir, artifactID);
        if (!File.Exists(recordPath) || !File.Exists(manifestPath))
            return null;
        var record = JsonSerializer.Deserialize(File.ReadAllText(recordPath), PluginJSONContext.Default.PluginRecord);
        return record is null ? null : (record, Manifest.Load(manifestPath));
    }

    /// <summary>Plugins UnVault installed into this engine (EGL-installed ones aren't listed here).</summary>
    public static IReadOnlyList<PluginRecord> List(string engineDir)
    {
        string root = Path.Combine(InstallJournal.DirectoryFor(engineDir), "plugins");
        if (!Directory.Exists(root))
            return [];

        var records = new List<PluginRecord>();
        foreach (string folder in Directory.EnumerateDirectories(root))
        {
            string path = Path.Combine(folder, "plugin.json");
            try
            {
                if (File.Exists(path) && JsonSerializer.Deserialize(File.ReadAllText(path), PluginJSONContext.Default.PluginRecord) is { } record)
                    records.Add(record);
            }
            catch (JsonException)
            {
                // Ignore a damaged record rather than hiding every plugin.
            }
        }
        return records;
    }

    public static void Delete(string engineDir, string artifactID)
    {
        string directory = StateDirectory(engineDir, artifactID);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(PluginRecord))]
internal sealed partial class PluginJSONContext : JsonSerializerContext;
