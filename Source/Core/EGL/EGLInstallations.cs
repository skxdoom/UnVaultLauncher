using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnVault.Core.Util;

namespace UnVault.Core.EGL;

/// <summary>
/// Reads what the Epic Games Launcher has installed on this machine, and maintains
/// LauncherInstalled.dat — the list UE tools use to find launcher-installed engines.
/// </summary>
public static class EGLInstallations
{
    public static string ProgramDataEpic =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic");

    public static string LauncherInstalledPath =>
        Path.Combine(ProgramDataEpic, "UnrealEngineLauncher", "LauncherInstalled.dat");

    public static string ItemsDirectory =>
        Path.Combine(ProgramDataEpic, "EpicGamesLauncher", "Data", "Manifests");

    public static IReadOnlyList<LauncherInstalledEntry> ReadLauncherInstalled() => ReadLauncherInstalledFrom(LauncherInstalledPath);

    /// <summary>Empty when the file is missing, damaged or locked: that shouldn't hide every installed engine.</summary>
    public static IReadOnlyList<LauncherInstalledEntry> ReadLauncherInstalledFrom(string path)
    {
        try
        {
            if (!File.Exists(path))
                return [];
            using var stream = File.OpenRead(path);
            var file = JsonSerializer.Deserialize(stream, EGLJSONContext.Default.LauncherInstalledFile);
            return file?.InstallationList ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static IReadOnlyList<EGLItem> ReadItems()
    {
        if (!Directory.Exists(ItemsDirectory))
            return [];

        var items = new List<EGLItem>();
        foreach (var path in Directory.EnumerateFiles(ItemsDirectory, "*.item"))
        {
            try
            {
                using var stream = File.OpenRead(path);
                if (JsonSerializer.Deserialize(stream, EGLJSONContext.Default.EGLItem) is { } item)
                    items.Add(item);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // A half-written, foreign or locked file shouldn't hide every other install.
            }
        }
        return items;
    }

    /// <summary>
    /// Adds an install to LauncherInstalled.dat, replacing an older entry for the same app in the same
    /// folder, and dropping entries for the same app whose folder no longer exists. UnrealVersionSelector
    /// and .uproject "EngineAssociation" then resolve e.g. "5.8" to this folder.
    /// </summary>
    public static void RegisterLauncherInstall(LauncherInstalledEntry entry, string? path = null)
    {
        path ??= LauncherInstalledPath;
        EditLauncherInstalled(path, list =>
        {
            RemoveWhere(list, existing =>
                SameApp(existing, entry.AppName) && (SameFolder(existing, entry.InstallLocation) || !Directory.Exists(Location(existing))));

            // Same field order as EGL writes.
            list.Add(new JsonObject
            {
                ["InstallLocation"] = entry.InstallLocation,
                ["NamespaceId"] = entry.NamespaceID,
                ["ItemId"] = entry.ItemID,
                ["ArtifactId"] = entry.ArtifactID,
                ["AppVersion"] = entry.AppVersion,
                ["AppName"] = entry.AppName,
            });
        });
    }

    /// <summary>Removes an app's entry for one folder. Returns whether anything was removed.</summary>
    public static bool UnregisterLauncherInstall(string appName, string installLocation, string? path = null)
    {
        path ??= LauncherInstalledPath;
        bool removed = false;
        EditLauncherInstalled(path, list =>
            removed = RemoveWhere(list, existing => SameApp(existing, appName) && SameFolder(existing, installLocation)) > 0);
        return removed;
    }

    /// <summary>
    /// Read-modify-write of LauncherInstalled.dat as a JSON tree, so fields we don't model survive.
    /// Written in EGL's style (tabs, CRLF, unescaped '+') via a temp file; the first edit keeps a backup.
    /// </summary>
    private static void EditLauncherInstalled(string path, Action<JsonArray> edit)
    {
        // Operations finishing together, or the app and the CLI, would each write over the other's change. The lock is
        // named, so it holds across processes.
        using var mutex = new Mutex(initiallyOwned: false, @"Local\UnVaultLauncher.LauncherInstalled");
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(30)))
                throw new IOException("Another edit of LauncherInstalled.dat didn't finish.");
        }
        catch (AbandonedMutexException)
        {
            // Its holder quit midway. The file is only ever replaced whole, so it's intact and the lock is ours now.
        }

        try
        {
            EditLocked(path, edit);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private static void EditLocked(string path, Action<JsonArray> edit)
    {
        JsonObject root = File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true }) as JsonObject ?? []
            : [];
        if (root["InstallationList"] is not JsonArray list)
            root["InstallationList"] = list = [];

        edit(list);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string backup = path + ".unvault-backup";
        if (File.Exists(path) && !File.Exists(backup))
            File.Copy(path, backup);

        string temp = AtomicFile.TemporaryFor(path);
        try
        {
            using (var stream = File.Create(temp))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
                   {
                       Indented = true,
                       IndentCharacter = '\t',
                       IndentSize = 1,
                       NewLine = "\r\n",
                       Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                   }))
            {
                root.WriteTo(writer);
            }

            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temp, path, overwrite: true);
                    break;
                }
                catch (IOException) when (attempt < 5)
                {
                    Thread.Sleep(100 * attempt); // EGL or a UE tool reading it at that moment
                }
            }
        }
        catch
        {
            AtomicFile.TryDelete(temp);
            throw;
        }
    }

    private static int RemoveWhere(JsonArray list, Func<JsonObject, bool> predicate)
    {
        int removed = 0;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (list[i] is JsonObject existing && predicate(existing))
            {
                list.RemoveAt(i);
                removed++;
            }
        }
        return removed;
    }

    private static string Location(JsonObject entry) => entry["InstallLocation"]?.GetValue<string>() ?? "";

    private static bool SameApp(JsonObject entry, string appName) =>
        string.Equals(entry["AppName"]?.GetValue<string>(), appName, StringComparison.OrdinalIgnoreCase);

    private static bool SameFolder(JsonObject entry, string folder) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Location(entry)), Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase);
}
