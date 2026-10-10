using System.Text.Json;
using System.Text.Json.Serialization;
using UnVault.Core;
using UnVault.Core.Util;

namespace UnVault.App.Services;

public enum OperationOutcome { Completed, Paused, Failed }

/// <summary>A finished operation, as the Downloads tab's history lists it.</summary>
public sealed record OperationRecord(string Title, OperationOutcome Outcome, string? Message, DateTimeOffset FinishedAt);

/// <summary>
/// The Downloads tab's history, kept in %LOCALAPPDATA%\UnVaultLauncher\history.json, newest first. Only a record: what
/// it would take to run an operation again isn't kept, so an old one can't be retried (starting it anew resumes it).
/// </summary>
internal static class OperationHistory
{
    /// <summary>Plenty to look back on; older ones are dropped.</summary>
    public const int MaxEntries = 100;

    public static string FilePath => Path.Combine(AppPaths.DataDirectory, "history.json");

    /// <summary>The saved history; empty when there's none or it can't be read.</summary>
    public static List<OperationRecord> Load(string? path = null)
    {
        path ??= FilePath;
        try
        {
            return File.Exists(path)
                ? [.. (JsonSerializer.Deserialize(File.ReadAllText(path), HistoryJSONContext.Default.ListOperationRecord) ?? []).Take(MaxEntries)]
                : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Saves the newest <see cref="MaxEntries"/>. A history that can't be saved isn't worth stopping anything for.</summary>
    public static void Save(IEnumerable<OperationRecord> records, string? path = null)
    {
        path ??= FilePath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize([.. records.Take(MaxEntries)], HistoryJSONContext.Default.ListOperationRecord));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept for this session; the next change tries again.
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(List<OperationRecord>))]
internal sealed partial class HistoryJSONContext : JsonSerializerContext;
