using System.Text.Json.Serialization;

namespace Unvault.Core.EGL;

/// <summary>
/// One entry of C:\ProgramData\Epic\UnrealEngineLauncher\LauncherInstalled.dat. UE tools
/// (UnrealVersionSelector, the editor's plugin browser) read this file to find launcher installs.
/// </summary>
public sealed class LauncherInstalledEntry
{
    public string InstallLocation { get; set; } = "";
    [JsonPropertyName("NamespaceId")]
    public string NamespaceID { get; set; } = "";

    [JsonPropertyName("ItemId")]
    public string ItemID { get; set; } = "";

    [JsonPropertyName("ArtifactId")]
    public string ArtifactID { get; set; } = "";

    public string AppVersion { get; set; } = "";
    public string AppName { get; set; } = "";
}

public sealed class LauncherInstalledFile
{
    public List<LauncherInstalledEntry> InstallationList { get; set; } = [];
}

/// <summary>
/// The fields we use from EGL's per-install record
/// (C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests\{InstallationGuid}.item).
/// </summary>
public sealed class EGLItem
{
    public int FormatVersion { get; set; }
    public bool bIsIncompleteInstall { get; set; }
    public string LaunchExecutable { get; set; } = "";
    public string ManifestLocation { get; set; } = "";
    public string CompleteManifestPath { get; set; } = "";
    public string ManifestHash { get; set; } = "";
    public bool bIsFab { get; set; }
    public List<string> BaseURLs { get; set; } = [];
    public string BuildLabel { get; set; } = "";
    public List<string> AppCategories { get; set; } = [];
    public string DisplayName { get; set; } = "";
    [JsonPropertyName("InstallationGuid")]
    public string InstallationGUID { get; set; } = "";

    public string InstallLocation { get; set; } = "";
    public List<string> InstallTags { get; set; } = [];
    public List<string> InstallComponents { get; set; } = [];

    [JsonPropertyName("HostInstallationGuid")]
    public string HostInstallationGUID { get; set; } = "";

    public long InstallSize { get; set; }
    public string CatalogNamespace { get; set; } = "";

    [JsonPropertyName("CatalogItemId")]
    public string CatalogItemID { get; set; } = "";

    public string AppName { get; set; } = "";
    public string AppVersionString { get; set; } = "";

    [JsonIgnore]
    public bool IsEngine => AppCategories.Contains("engines");
}

[JsonSourceGenerationOptions(WriteIndented = true, ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(LauncherInstalledFile))]
[JsonSerializable(typeof(EGLItem))]
internal sealed partial class EGLJSONContext : JsonSerializerContext;
