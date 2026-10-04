using System.Text.Json.Serialization;
using Unvault.Core.Epic;
using Unvault.Core.Util;

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
    public static FabLibrarySnapshot? Load(string accountID, string? path = null) =>
        SnapshotFile.Load(path ?? DefaultPath, FabJSONContext.Default.FabLibrarySnapshot) is { } snapshot
        && string.Equals(snapshot.AccountID, accountID, StringComparison.OrdinalIgnoreCase)
            ? snapshot
            : null;

    public static void Save(FabLibrarySnapshot snapshot, string? path = null) =>
        SnapshotFile.Save(path ?? DefaultPath, snapshot, FabJSONContext.Default.FabLibrarySnapshot);

    public static void Delete(string? path = null) => SnapshotFile.Delete(path ?? DefaultPath);
}
