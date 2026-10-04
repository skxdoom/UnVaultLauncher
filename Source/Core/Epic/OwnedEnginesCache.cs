using System.Text.Json.Serialization;
using Unvault.Core.Util;

namespace Unvault.Core.Epic;

/// <summary>The engine versions the account could install, as Epic last listed them.</summary>
public sealed class OwnedEnginesSnapshot
{
    [JsonPropertyName("accountId")] public string AccountID { get; set; } = "";
    [JsonPropertyName("fetchedAt")] public DateTimeOffset FetchedAt { get; set; }
    [JsonPropertyName("engines")] public List<EpicAsset> Engines { get; set; } = [];
}

/// <summary>
/// %LOCALAPPDATA%\UnvaultLauncher\owned-engines.json. Epic is slow to list everything an account owns (every Fab
/// plugin build too, not only the engines), so the Engines tab starts from the last list: update badges and
/// installable versions show at once, then the fresh list replaces it. Versions and
/// build names only; kept for one account at a time and deleted on sign-out.
/// </summary>
public static class OwnedEnginesCache
{
    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "owned-engines.json");

    /// <summary>The saved list, if there is a readable one for this account.</summary>
    public static OwnedEnginesSnapshot? Load(string accountID, string? path = null) =>
        SnapshotFile.Load(path ?? DefaultPath, EpicJSONContext.Default.OwnedEnginesSnapshot) is { } snapshot
        && string.Equals(snapshot.AccountID, accountID, StringComparison.OrdinalIgnoreCase)
            ? snapshot
            : null;

    public static void Save(OwnedEnginesSnapshot snapshot, string? path = null) =>
        SnapshotFile.Save(path ?? DefaultPath, snapshot, EpicJSONContext.Default.OwnedEnginesSnapshot);

    public static void Delete(string? path = null) => SnapshotFile.Delete(path ?? DefaultPath);
}
