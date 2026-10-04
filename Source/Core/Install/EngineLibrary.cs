using UnVault.Core.EGL;

namespace UnVault.Core.Install;

public enum LocalInstallKind
{
    /// <summary>Installed by UnVault.</summary>
    UnVault,

    /// <summary>Installed by EGL, since managed by UnVault (has our record).</summary>
    AdoptedFromEGL,

    /// <summary>Installed by EGL; components are detected from files when needed.</summary>
    EGL,

    /// <summary>EGL lists it, but the folder is gone.</summary>
    StaleEGLRecord,
}

/// <summary>An engine install as listed in the library — cheap to build; <see cref="Load"/> reads the manifest.</summary>
public sealed record LocalEngine(string AppName, string Directory, string BuildVersion, long InstallSize, LocalInstallKind Kind)
{
    public bool Exists => Kind != LocalInstallKind.StaleEGLRecord;

    /// <summary>Loads the full install (manifest, components). Slower: parses the engine's whole manifest.</summary>
    public ExistingInstall? Load() => Kind switch
    {
        LocalInstallKind.UnVault or LocalInstallKind.AdoptedFromEGL => InstallLocator.Find(null, Directory),
        LocalInstallKind.EGL => InstallLocator.Find(AppName),
        _ => null,
    };
}

public static class EngineLibrary
{
    /// <summary>All engine installs on this machine, without parsing any manifest.</summary>
    public static IReadOnlyList<LocalEngine> ScanLocal()
    {
        var byFolder = new Dictionary<string, LocalEngine>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in EGLInstallations.ReadLauncherInstalled())
        {
            if (!IsEngineApp(entry.AppName) || !Directory.Exists(entry.InstallLocation))
                continue;
            if (InstallRecord.TryRead(entry.InstallLocation) is { FileFilter: null } record)
                byFolder[Normalize(entry.InstallLocation)] = FromRecord(record, entry.InstallLocation);
        }

        var stale = new List<LocalEngine>();
        foreach (var item in EGLInstallations.ReadItems().Where(i => i.IsEngine))
        {
            string key = Normalize(item.InstallLocation);
            if (!Directory.Exists(item.InstallLocation))
            {
                stale.Add(new LocalEngine(item.AppName, item.InstallLocation, item.AppVersionString, item.InstallSize, LocalInstallKind.StaleEGLRecord));
                continue;
            }

            // Our record wins: EGL may still list a folder UnVault has since installed into (a stale record, re-used folder).
            byFolder[key] = InstallRecord.TryRead(item.InstallLocation) is { FileFilter: null } record
                ? FromRecord(record, item.InstallLocation)
                : new LocalEngine(item.AppName, item.InstallLocation, item.AppVersionString, item.InstallSize, LocalInstallKind.EGL);
        }

        // A stale record only matters if nothing real exists for that version.
        var engines = byFolder.Values.ToList();
        engines.AddRange(stale.Where(s => !engines.Any(e => string.Equals(e.AppName, s.AppName, StringComparison.OrdinalIgnoreCase))));
        return engines.OrderByDescending(e => ParseVersion(e.AppName)).ToList();
    }

    private static LocalEngine FromRecord(InstallRecord record, string directory) =>
        new(record.AppName, directory, record.BuildVersion, record.InstallSize,
            record.AdoptedFromEGL ? LocalInstallKind.AdoptedFromEGL : LocalInstallKind.UnVault);

    public static bool IsEngineApp(string appName) => appName.StartsWith("UE_", StringComparison.Ordinal);

    /// <summary>"UE_5.8" → 5.8; anything unparsable (e.g. "UE_4.27Chaos") sorts last.</summary>
    public static Version ParseVersion(string appName) =>
        IsEngineApp(appName) && Version.TryParse(appName.AsSpan(3), out var version) ? version : new Version(0, 0);

    /// <summary>"5.8.3-58210709+++UE5+Release-5.8-Windows" → "5.8.3".</summary>
    public static string ShortBuildVersion(string buildVersion)
    {
        int cut = buildVersion.IndexOf('-');
        return cut > 0 ? buildVersion[..cut] : buildVersion;
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
