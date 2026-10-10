using System.Text.Json;
using System.Text.Json.Serialization;
using UnVault.Core.EGL;

namespace UnVault.Core;

/// <summary>Where a setting's effective value came from, for showing "auto (from …)" in the UI.</summary>
public enum SettingSource { User, EpicGamesLauncher, ExistingInstalls, Default }

public readonly record struct ResolvedFolder(string Path, SettingSource Source);

public readonly record struct ResolvedFolders(IReadOnlyList<string> Paths, SettingSource Source)
{
    /// <summary>Where new projects go: the user's first folder; when automatic, the first one that exists.</summary>
    public string ForNewProjects => Source == SettingSource.User ? Paths[0] : Paths.FirstOrDefault(Directory.Exists) ?? Paths[0];
}

/// <summary>
/// User settings, saved as %LOCALAPPDATA%\UnVaultLauncher\settings.json. Folder settings left empty mean "automatic":
/// use what the Epic Games Launcher is configured with, so both launchers share folders.
/// </summary>
public sealed class AppSettings
{
    public const int DefaultParallelDownloads = 16;
    public const int MaxParallelDownloads = 64;

    /// <summary>Parent folder for new engines (UE_5.8 goes to {this}\UE_5.8). Null = automatic.</summary>
    public string? EngineInstallRoot { get; set; }

    /// <summary>Where Fab asset packs and plugins are downloaded and kept. Null = automatic.</summary>
    public string? VaultCacheDirectory { get; set; }

    /// <summary>Folders holding Unreal projects: searched for projects, and new ones go into the first. Null or empty = automatic.</summary>
    public List<string>? ProjectDirectories { get; set; }

    public int ParallelDownloads { get; set; } = DefaultParallelDownloads;

    /// <summary>Ask GitHub at startup whether a newer release is out.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Closing the window keeps the app running in the system tray, downloads included; Quit is in the tray icon's menu.</summary>
    public bool CloseToTray { get; set; }

    public static string FilePath => Path.Combine(AppPaths.DataDirectory, "settings.json");

    public static AppSettings Load(string? path = null)
    {
        path ??= FilePath;
        AppSettings settings;
        try
        {
            settings = File.Exists(path)
                ? JsonSerializer.Deserialize(File.ReadAllText(path), SettingsJSONContext.Default.AppSettings) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            settings = new AppSettings();
        }
        // The file can be edited by hand; 0 would stop every download.
        settings.ParallelDownloads = Math.Clamp(settings.ParallelDownloads, 1, MaxParallelDownloads);
        return settings;
    }

    public void Save(string? path = null)
    {
        path ??= FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, SettingsJSONContext.Default.AppSettings));
        File.Move(temp, path, overwrite: true);
    }

    public ResolvedFolder ResolveEngineInstallRoot() => ResolveEngineInstallRoot(EGLLauncherSettings.Read());

    public ResolvedFolder ResolveEngineInstallRoot(EGLLauncherSettings egl)
    {
        if (!string.IsNullOrWhiteSpace(EngineInstallRoot))
            return new(EngineInstallRoot, SettingSource.User);
        if (egl.DefaultAppInstallLocation is { } eglRoot && Directory.Exists(eglRoot))
            return new(eglRoot, SettingSource.EpicGamesLauncher);

        string? nextToExisting = EGLInstallations.ReadLauncherInstalled()
            .Where(e => e.AppName.StartsWith("UE_", StringComparison.Ordinal) && Directory.Exists(e.InstallLocation))
            .Select(e => Path.GetDirectoryName(e.InstallLocation))
            .FirstOrDefault(p => p is not null);
        if (nextToExisting is not null)
            return new(nextToExisting, SettingSource.ExistingInstalls);

        return new(Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", "Epic Games"), SettingSource.Default);
    }

    public ResolvedFolder ResolveVaultCache() => ResolveVaultCache(EGLLauncherSettings.Read());

    public ResolvedFolder ResolveVaultCache(EGLLauncherSettings egl)
    {
        if (!string.IsNullOrWhiteSpace(VaultCacheDirectory))
            return new(VaultCacheDirectory, SettingSource.User);
        if (egl.ActiveVaultCache is { } eglVault)
            return new(eglVault, SettingSource.EpicGamesLauncher);
        if (Directory.Exists(EGLDefaultVaultCache))
            return new(EGLDefaultVaultCache, SettingSource.EpicGamesLauncher);

        // Earlier versions defaulted to a folder of their own; downloads already there are kept in use.
        string ownFolder = Path.Combine(AppPaths.DataDirectory, "VaultCache");
        if (Directory.Exists(ownFolder))
            return new(ownFolder, SettingSource.Default);

        // EGL's default even before it exists, so an Epic Games Launcher installed later finds the same downloads.
        return new(EGLDefaultVaultCache, SettingSource.Default);
    }

    /// <summary>Where the Epic Games Launcher keeps Fab downloads unless told otherwise.</summary>
    public static string EGLDefaultVaultCache => Path.Combine(EGLInstallations.ProgramDataEpic, "EpicGamesLauncher", "VaultCache");

    public ResolvedFolders ResolveProjectFolders() => ResolveProjectFolders(EGLLauncherSettings.Read());

    /// <summary>The user's list; else the folders EGL created projects in; else Documents\Unreal Projects, as EGL does.</summary>
    public ResolvedFolders ResolveProjectFolders(EGLLauncherSettings egl)
    {
        var user = (ProjectDirectories ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (user.Count > 0)
            return new(user, SettingSource.User);
        if (egl.CreatedProjectPaths.Count > 0)
            return new(egl.CreatedProjectPaths, SettingSource.EpicGamesLauncher);
        return new([DefaultProjectFolder], SettingSource.Default);
    }

    public static string DefaultProjectFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Unreal Projects");
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJSONContext : JsonSerializerContext;
