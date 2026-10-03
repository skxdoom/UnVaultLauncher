using System.Text.Json;
using System.Text.Json.Serialization;
using Unvault.Core.Epic;

namespace Unvault.Core.Fab;

/// <summary>The account's Fab library as last fetched, so it can be shown at once while a fresh copy loads.</summary>
public sealed class FabLibrarySnapshot
{
    [JsonPropertyName("accountId")] public string AccountID { get; set; } = "";
    [JsonPropertyName("fetchedAt")] public DateTimeOffset FetchedAt { get; set; }
    [JsonPropertyName("items")] public List<FabLibraryItem> Items { get; set; } = [];
}

/// <summary>
/// %LOCALAPPDATA%\UnvaultLauncher\fab-library.json: titles, versions and picture links only (nothing secret), kept for one
/// account at a time and deleted on sign-out.
/// </summary>
public static class FabLibraryCache
{
    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "fab-library.json");

    /// <summary>The saved library, if there is a readable one for this account.</summary>
    public static FabLibrarySnapshot? Load(string accountID, string? path = null)
    {
        try
        {
            using var stream = File.OpenRead(path ?? DefaultPath);
            var snapshot = JsonSerializer.Deserialize(stream, FabJSONContext.Default.FabLibrarySnapshot);
            return snapshot is not null && string.Equals(snapshot.AccountID, accountID, StringComparison.OrdinalIgnoreCase) ? snapshot : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null; // missing or damaged: just fetch it again
        }
    }

    public static void Save(FabLibrarySnapshot snapshot, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        using (var stream = File.Create(temporary))
            JsonSerializer.Serialize(stream, snapshot, FabJSONContext.Default.FabLibrarySnapshot);
        File.Move(temporary, path, overwrite: true);
    }

    public static void Delete(string? path = null)
    {
        try
        {
            File.Delete(path ?? DefaultPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not worth failing a sign-out over; it's only readable by the same account anyway.
        }
    }
}
