using System.Text.Json;
using Microsoft.Win32;

namespace UnVault.Core.Projects;

/// <param name="ID">What a project's EngineAssociation holds for it, e.g. "{FA447359-45B2-76D4-BBBC-E1B6D0042DD2}".</param>
/// <param name="Version">"4.27", from the engine's Build.version; null if that can't be read.</param>
public sealed record CustomEngine(string ID, string Directory, string? Version);

/// <summary>
/// Engines built from source. Each is registered under HKCU\Software\Epic Games\Unreal Engine\Builds as its ID and
/// folder, and projects made with it name that ID instead of a version like "5.7".
/// </summary>
public static class CustomEngines
{
    private const string BuildsKey = @"Software\Epic Games\Unreal Engine\Builds";

    /// <summary>The custom engines registered on this PC, by ID.</summary>
    public static IReadOnlyDictionary<string, CustomEngine> Registered()
    {
        var engines = new Dictionary<string, CustomEngine>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows())
            return engines;

        using var key = Registry.CurrentUser.OpenSubKey(BuildsKey);
        foreach (string id in key?.GetValueNames() ?? [])
        {
            if (key!.GetValue(id) is not string folder || folder.Length == 0)
                continue;
            try
            {
                string directory = Path.GetFullPath(folder); // registered with forward slashes
                engines[Normalize(id)] = new CustomEngine(id, directory, ReadVersion(directory));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // A malformed entry: that engine just counts as not found.
            }
        }
        return engines;
    }

    /// <summary>The engine a project's EngineAssociation names, if it's one of <paramref name="engines"/>.</summary>
    public static CustomEngine? Find(IReadOnlyDictionary<string, CustomEngine> engines, string association) =>
        association.Length == 0 ? null : engines.GetValueOrDefault(Normalize(association));

    /// <summary>"4.27" from the engine's Engine\Build\Build.version; null if it's missing or unreadable.</summary>
    public static string? ReadVersion(string engineDirectory)
    {
        try
        {
            string file = Path.Combine(engineDirectory, "Engine", "Build", "Build.version");
            if (!File.Exists(file))
                return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            return root.TryGetProperty("MajorVersion", out var major) && root.TryGetProperty("MinorVersion", out var minor)
                ? $"{major.GetInt32()}.{minor.GetInt32()}"
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>IDs are written with and without braces: "{FA44…}" and "FA44…" are the same engine.</summary>
    private static string Normalize(string id) => id.Trim().Trim('{', '}');
}
