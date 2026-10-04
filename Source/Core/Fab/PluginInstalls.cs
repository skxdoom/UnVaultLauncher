using System.Text.Json;
using System.Text.Json.Serialization;
using UnVault.Core.Install;
using UnVault.Core.Manifests;

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
        Path.Combine(InstallJournal.DirectoryFor(engineDir), "plugins", artifactID);

    private static string RecordPath(string engineDir, string artifactID) => Path.Combine(StateDirectory(engineDir, artifactID), "plugin.json");
    private static string ManifestPath(string engineDir, string artifactID) => Path.Combine(StateDirectory(engineDir, artifactID), "install.manifest");

    public static void Save(string engineDir, PluginRecord record, byte[] rawManifest)
    {
        string directory = StateDirectory(engineDir, record.ArtifactID);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(ManifestPath(engineDir, record.ArtifactID), rawManifest);
        File.WriteAllText(RecordPath(engineDir, record.ArtifactID), JsonSerializer.Serialize(record, PluginJSONContext.Default.PluginRecord));
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
