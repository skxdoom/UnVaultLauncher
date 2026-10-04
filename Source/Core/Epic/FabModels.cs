using System.Text.Json.Serialization;

namespace UnVault.Core.Epic;

/// <summary>One page of GET fab.com/e/accounts/{accountId}/ue/library.</summary>
internal sealed class FabLibraryPage
{
    [JsonPropertyName("cursors")] public FabCursors? Cursors { get; set; }
    [JsonPropertyName("results")] public List<FabLibraryItem>? Results { get; set; }
}

internal sealed class FabCursors
{
    [JsonPropertyName("next")] public string? Next { get; set; }
}

/// <summary>Something in the account's Fab library usable from Unreal Engine (plugin, asset pack, project).</summary>
public sealed class FabLibraryItem
{
    [JsonPropertyName("assetId")] public string AssetID { get; set; } = "";
    [JsonPropertyName("assetNamespace")] public string AssetNamespace { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("seller")] public string? Seller { get; set; }

    /// <summary>e.g. "asset-pack", "complete_project"; plugins have their own value.</summary>
    [JsonPropertyName("distributionMethod")] public string? DistributionMethod { get; set; }

    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("url")] public string? URL { get; set; }
    [JsonPropertyName("categories")] public List<FabCategory>? Categories { get; set; }
    [JsonPropertyName("images")] public List<FabImage>? Images { get; set; }

    /// <summary>A small preview image: the narrowest one at least 300 px wide (or the widest if all are smaller).</summary>
    [JsonIgnore]
    public string? ThumbnailURL =>
        (Images ?? []).Where(i => !string.IsNullOrEmpty(i.URL)).OrderBy(i => i.Width >= 300 ? i.Width : int.MaxValue - i.Width).FirstOrDefault()?.URL;

    /// <summary>One downloadable artifact per supported engine version (e.g. MyPlugin_5.4, MyPlugin_5.5).</summary>
    [JsonPropertyName("projectVersions")] public List<FabProjectVersion>? ProjectVersions { get; set; }

    [JsonIgnore]
    public IEnumerable<string> EngineVersions =>
        (ProjectVersions ?? []).SelectMany(v => v.EngineVersions ?? []).Distinct();
}

public sealed class FabImage
{
    [JsonPropertyName("url")] public string URL { get; set; } = "";
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
    [JsonPropertyName("uploadedDate")] public DateTimeOffset? UploadedDate { get; set; }
}

public sealed class FabCategory
{
    [JsonPropertyName("id")] public string ID { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

public sealed class FabProjectVersion
{
    [JsonPropertyName("artifactId")] public string ArtifactID { get; set; } = "";
    [JsonPropertyName("engineVersions")] public List<string>? EngineVersions { get; set; }
    [JsonPropertyName("targetPlatforms")] public List<string>? TargetPlatforms { get; set; }
    [JsonPropertyName("buildVersions")] public List<FabBuildVersion>? BuildVersions { get; set; }
}

public sealed class FabBuildVersion
{
    [JsonPropertyName("buildVersion")] public string BuildVersion { get; set; } = "";
    [JsonPropertyName("platform")] public string Platform { get; set; } = "";
}

internal sealed class FabArtifactManifestRequest
{
    [JsonPropertyName("item_id")] public string ItemID { get; set; } = "";
    [JsonPropertyName("namespace")] public string Namespace { get; set; } = "";
    [JsonPropertyName("platform")] public string Platform { get; set; } = "";
}

internal sealed class FabArtifactManifestResponse
{
    [JsonPropertyName("downloadInfo")] public List<FabDownloadInfo>? DownloadInfo { get; set; }
}

/// <summary>Where to get one Fab artifact's build manifest. The URLs are signed and expire.</summary>
public sealed class FabDownloadInfo
{
    [JsonPropertyName("artifactId")] public string ArtifactID { get; set; } = "";
    [JsonPropertyName("assetFormat")] public string? AssetFormat { get; set; }
    [JsonPropertyName("buildVersion")] public string BuildVersion { get; set; } = "";
    [JsonPropertyName("type")] public string? Type { get; set; }

    /// <summary>SHA-1 (hex) of the manifest file.</summary>
    [JsonPropertyName("manifestHash")] public string? ManifestHash { get; set; }

    [JsonPropertyName("distributionPoints")] public List<FabDistributionPoint>? DistributionPoints { get; set; }
}

public sealed class FabDistributionPoint
{
    [JsonPropertyName("manifestUrl")] public string ManifestURL { get; set; } = "";
    [JsonPropertyName("signatureExpiration")] public DateTimeOffset SignatureExpiration { get; set; }
}

// Fab sends some numbers as strings ("width": "640").
[JsonSourceGenerationOptions(NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(FabLibraryPage))]
[JsonSerializable(typeof(FabArtifactManifestRequest))]
[JsonSerializable(typeof(FabArtifactManifestResponse))]
[JsonSerializable(typeof(Fab.FabLibrarySnapshot))]
internal sealed partial class FabJSONContext : JsonSerializerContext;
