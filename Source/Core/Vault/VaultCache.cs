using System.Text.Json;
using System.Text.Json.Serialization;
using UnVault.Core.Util;

namespace UnVault.Core.Vault;

/// <summary>
/// One downloaded Fab/Marketplace artifact in a vault cache, as EGL stores it:
/// {VaultCache}\{ArtifactID}\data\… (the files), manifest (binary), manifest.json, vault.json (this metadata).
/// </summary>
public sealed class VaultEntry
{
    [JsonPropertyName("assetId")] public string ArtifactID { get; set; } = "";

    /// <summary>Catalog item id.</summary>
    [JsonPropertyName("id")] public string ItemID { get; set; } = "";

    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("build")] public string Build { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("thumbnail")] public string? Thumbnail { get; set; }
    [JsonPropertyName("categories")] public string? Categories { get; set; }

    // Fields EGL writes; kept so entries we write look like EGL's own.
    [JsonPropertyName("path")] public string? StoredPath { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("addedDate")] public long AddedDate { get; set; }
    [JsonPropertyName("modifiedDate")] public long ModifiedDate { get; set; }
    [JsonPropertyName("createdDate")] public long CreatedDate { get; set; }
    [JsonPropertyName("flags")] public int Flags { get; set; } = 1;
    [JsonPropertyName("vaultJSONVersion")] public int VaultJSONVersion { get; set; } = 1;

    /// <summary>The artifact's folder in the cache (not from JSON: EGL's "path" can be stale after moving the cache).</summary>
    [JsonIgnore] public string Directory { get; set; } = "";

    [JsonIgnore] public string DataDirectory => Path.Combine(Directory, "data");
    [JsonIgnore] public string ManifestPath => Path.Combine(Directory, "manifest");

    /// <summary>Complete when EGL or we finished it: the manifest is written last, after all files.</summary>
    [JsonIgnore] public bool IsComplete => File.Exists(ManifestPath) && System.IO.Directory.Exists(DataDirectory);
}

public sealed record VaultSummary(int Count, long TotalBytes);

/// <summary>Reads what's in a vault cache folder (EGL-compatible layout).</summary>
public static class VaultCache
{
    public static IReadOnlyList<VaultEntry> Scan(string directory)
    {
        if (!System.IO.Directory.Exists(directory))
            return [];

        var entries = new List<VaultEntry>();
        foreach (string folder in System.IO.Directory.EnumerateDirectories(directory))
        {
            string metadata = Path.Combine(folder, "vault.json");
            if (!File.Exists(metadata))
                continue;
            try
            {
                if (JsonSerializer.Deserialize(File.ReadAllText(metadata), VaultJSONContext.Default.VaultEntry) is { } entry)
                {
                    entry.Directory = folder;
                    entries.Add(entry);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // A damaged or unreadable entry shouldn't hide the rest.
            }
        }
        return entries.OrderBy(e => e.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static VaultSummary Summarize(string directory)
    {
        var entries = Scan(directory);
        return new VaultSummary(entries.Count, entries.Sum(e => e.Size));
    }

    /// <summary>Finishes a downloaded entry the way EGL does: binary manifest plus vault.json next to data\.</summary>
    public static void WriteEntry(VaultEntry entry, byte[] rawManifest)
    {
        System.IO.Directory.CreateDirectory(entry.Directory);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        entry.StoredPath = Path.TrimEndingDirectorySeparator(entry.Directory) + Path.DirectorySeparatorChar;
        entry.Description ??= entry.Title;
        if (entry.CreatedDate == 0)
            entry.CreatedDate = now;
        entry.AddedDate = now;
        entry.ModifiedDate = now;

        AtomicFile.WriteAllBytes(entry.ManifestPath, rawManifest);
        AtomicFile.WriteAllText(Path.Combine(entry.Directory, "vault.json"), JsonSerializer.Serialize(entry, typeof(VaultEntry), WriteOptions));
    }

    // Indented, with '+' in build strings left readable, like EGL writes.
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = VaultJSONContext.Default,
    };
}

[JsonSerializable(typeof(VaultEntry))]
internal sealed partial class VaultJSONContext : JsonSerializerContext;
