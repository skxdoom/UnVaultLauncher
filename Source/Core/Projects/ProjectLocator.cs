using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Unvault.Core.Projects;

/// <summary>An Unreal project on disk.</summary>
public sealed record UnrealProject(string Name, string ProjectFile, string EngineAssociation, DateTime? LastOpened)
{
    public string Directory => Path.GetDirectoryName(ProjectFile)!;

    /// <summary>"5.7" → "UE_5.7". Null for source builds, which associate by GUID.</summary>
    public string? EngineAppName => Version.TryParse(EngineAssociation, out _) ? "UE_" + EngineAssociation : null;
}

/// <summary>
/// Finds the user's projects without asking: the editor's "recently opened" list for every engine version,
/// plus the project folders (see <see cref="AppSettings.ResolveProjectFolders()"/>).
/// </summary>
public static partial class ProjectLocator
{
    public static string EditorConfigRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnrealEngine");

    public static IReadOnlyList<UnrealProject> FindProjects(IEnumerable<string> projectFolders) =>
        FindProjects(EditorConfigRoot, projectFolders);

    public static IReadOnlyList<UnrealProject> FindProjects(string editorConfigRoot, IEnumerable<string> projectFolders)
    {
        var lastOpened = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);

        // %LOCALAPPDATA%\UnrealEngine\{version}\Saved\Config\WindowsEditor\EditorSettings.ini
        if (System.IO.Directory.Exists(editorConfigRoot))
        {
            foreach (string versionFolder in System.IO.Directory.EnumerateDirectories(editorConfigRoot))
            {
                string ini = Path.Combine(versionFolder, "Saved", "Config", "WindowsEditor", "EditorSettings.ini");
                if (!File.Exists(ini))
                    continue;
                foreach (Match match in RecentProjectRegex().Matches(File.ReadAllText(ini)))
                {
                    string file = Path.GetFullPath(match.Groups["path"].Value.Replace('/', Path.DirectorySeparatorChar));
                    DateTime? opened = DateTime.TryParseExact(match.Groups["time"].Value, "yyyy.MM.dd-HH.mm.ss",
                        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ? time : null;
                    if (!lastOpened.TryGetValue(file, out var known) || known < opened)
                        lastOpened[file] = opened;
                }
            }
        }

        // Each subfolder of a project folder that holds a .uproject.
        foreach (string folder in projectFolders.Where(System.IO.Directory.Exists))
        {
            foreach (string sub in SafeEnumerate(() => System.IO.Directory.EnumerateDirectories(folder)))
            {
                foreach (string file in SafeEnumerate(() => System.IO.Directory.EnumerateFiles(sub, "*.uproject")))
                    lastOpened.TryAdd(Path.GetFullPath(file), null);
            }
        }

        return lastOpened
            .Select(kv => Read(kv.Key, kv.Value))
            .OfType<UnrealProject>()
            .OrderByDescending(p => p.LastOpened ?? DateTime.MinValue)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Reads a .uproject's engine association. Null if the file is gone or unreadable.</summary>
    public static UnrealProject? Read(string projectFile, DateTime? lastOpened = null)
    {
        if (!File.Exists(projectFile))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(projectFile),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            string association = doc.RootElement.TryGetProperty("EngineAssociation", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";
            return new UnrealProject(Path.GetFileNameWithoutExtension(projectFile), projectFile, association, lastOpened);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<string> SafeEnumerate(Func<IEnumerable<string>> enumerate)
    {
        try
        {
            return enumerate().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // RecentlyOpenedProjectFiles=(ProjectName="D:/Dev/UE5/Foo/Foo.uproject",LastOpenTime=2026.09.27-23.18.27)
    [GeneratedRegex("""RecentlyOpenedProjectFiles=\(ProjectName="(?<path>[^"]+)",LastOpenTime=(?<time>[0-9.\-]+)\)""")]
    private static partial Regex RecentProjectRegex();
}
