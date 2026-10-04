using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace UnVault.Core.Util;

/// <summary>
/// A JSON file that keeps the last copy of something slow to fetch (e.g. the Fab library), so it can be shown at
/// once next time. Losing it only costs the instant start, so a missing or damaged file just reads as nothing.
/// </summary>
public static class SnapshotFile
{
    public static T? Load<T>(string path, JsonTypeInfo<T> typeInfo) where T : class
    {
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, typeInfo);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Writes beside the file first, so a crash midway leaves the previous copy intact.</summary>
    public static void Save<T>(string path, T value, JsonTypeInfo<T> typeInfo)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        using (var stream = File.Create(temporary))
            JsonSerializer.Serialize(stream, value, typeInfo);
        File.Move(temporary, path, overwrite: true);
    }

    public static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not worth failing a sign-out over; it's only readable by the same Windows user anyway.
        }
    }
}
