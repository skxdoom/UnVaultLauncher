using System.Text.Json;
using System.Text.Json.Serialization;
using UnVault.Core.Manifests;

namespace UnVault.Core.Install;

/// <summary>
/// What we installed into a folder, kept in {install}\.unvault\ next to a copy of the manifest,
/// so verify/repair/modify work later without asking Epic again.
/// </summary>
public sealed class InstallRecord
{
    public string AppName { get; set; } = "";
    public string BuildVersion { get; set; } = "";
    public string CatalogNamespace { get; set; } = "";
    public string CatalogItemID { get; set; } = "";
    public List<string> InstallTags { get; set; } = [];

    /// <summary>Bytes on disk for the installed selection.</summary>
    public long InstallSize { get; set; }

    /// <summary>True when the files were installed by the Epic Games Launcher and UnVault took over managing them.</summary>
    public bool AdoptedFromEGL { get; set; }

    /// <summary>Set when only part of the build was installed (testing with --only).</summary>
    public string? FileFilter { get; set; }

    /// <summary>CloudDir URLs chunks came from; engine CloudDirs are stable, so repair can reuse them.</summary>
    public List<string> BaseURLs { get; set; } = [];

    public DateTimeOffset InstalledAt { get; set; }

    public static string RecordPath(string installDir) => Path.Combine(InstallJournal.DirectoryFor(installDir), "install.json");
    public static string ManifestPath(string installDir) => Path.Combine(InstallJournal.DirectoryFor(installDir), "install.manifest");

    public void Save(string installDir, byte[] rawManifest)
    {
        Directory.CreateDirectory(InstallJournal.DirectoryFor(installDir));
        File.WriteAllBytes(ManifestPath(installDir), rawManifest);
        File.WriteAllText(RecordPath(installDir), JsonSerializer.Serialize(this, InstallJSONContext.Default.InstallRecord));
    }

    /// <summary>Just the record, without parsing the manifest. Null if there's none or it's unreadable.</summary>
    public static InstallRecord? TryRead(string installDir)
    {
        try
        {
            string path = RecordPath(installDir);
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), InstallJSONContext.Default.InstallRecord) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static (InstallRecord Record, Manifest Manifest)? Load(string installDir)
    {
        string recordPath = RecordPath(installDir);
        string manifestPath = ManifestPath(installDir);
        if (!File.Exists(recordPath) || !File.Exists(manifestPath))
            return null;

        var record = JsonSerializer.Deserialize(File.ReadAllText(recordPath), InstallJSONContext.Default.InstallRecord);
        return record is null ? null : (record, Manifest.Load(manifestPath));
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(InstallRecord))]
internal sealed partial class InstallJSONContext : JsonSerializerContext;
