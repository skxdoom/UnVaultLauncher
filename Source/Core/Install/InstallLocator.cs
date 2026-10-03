using System.Text.RegularExpressions;
using Unvault.Core.EGL;
using Unvault.Core.Epic;
using Unvault.Core.Manifests;
using Unvault.Core.Util;

namespace Unvault.Core.Install;

/// <summary>An existing install on disk, with everything needed to verify, repair or modify it.</summary>
public sealed record ExistingInstall(
    string Name,
    string Directory,
    Manifest Manifest,
    string ManifestPath,
    IReadOnlySet<string> InstallTags,
    IReadOnlyList<ChunkSource> Sources,
    string Origin,
    InstallRecord? Record = null,
    EGLItem? EGLItem = null,
    Regex? InstalledFilter = null)
{
    /// <summary>True when only EGL knows this install (we haven't adopted it with our own record yet).</summary>
    public bool IsEGLOnly => Record is null && EGLItem is not null;

    /// <summary>Files this install should have (respecting a partial --only install), optionally narrowed further.</summary>
    public IEnumerable<FileManifest> SelectFiles(Regex? only) => SelectFiles(InstallTags, only);

    public IEnumerable<FileManifest> SelectFiles(IReadOnlySet<string> tags, Regex? only = null) =>
        Manifest.SelectFiles(tags).Where(f =>
            (InstalledFilter is null || InstalledFilter.IsMatch(f.Filename)) && (only is null || only.IsMatch(f.Filename)));

    /// <summary>Our record for this install with new components, creating one when adopting an EGL install.</summary>
    public InstallRecord ToRecord(IReadOnlySet<string> tags) => new()
    {
        AppName = Record?.AppName ?? Manifest.Meta.AppName,
        BuildVersion = Manifest.Meta.BuildVersion,
        CatalogNamespace = Record?.CatalogNamespace ?? EGLItem?.CatalogNamespace ?? "",
        CatalogItemID = Record?.CatalogItemID ?? EGLItem?.CatalogItemID ?? "",
        InstallTags = [.. tags.Order(StringComparer.Ordinal)],
        InstallSize = SelectFiles(tags).Sum(f => f.FileSize),
        AdoptedFromEGL = Record?.AdoptedFromEGL ?? EGLItem is not null,
        FileFilter = Record?.FileFilter,
        BaseURLs = [.. Sources.Where(s => s.Query.Length == 0).Select(s => s.BaseURL)],
        InstalledAt = Record?.InstalledAt ?? DateTimeOffset.Now,
    };
}

/// <summary>Finds existing installs: ours (with a .unvault record) first, then the Epic Games Launcher's.</summary>
public static class InstallLocator
{
    /// <summary>
    /// Finds an install by folder or app name. Our own record wins (it knows the exact components);
    /// otherwise EGL's records are used, with components detected from the files on disk.
    /// </summary>
    public static ExistingInstall? Find(string? target, string? directory = null, string? manifestPath = null, IReadOnlyList<string>? baseURLs = null)
    {
        var extraSources = (baseURLs ?? []).Select(u => new ChunkSource(u.TrimEnd('/'))).ToList();

        if (manifestPath is not null)
        {
            string dir = directory ?? throw new ArgumentException("A manifest path needs an install folder.");
            var manifest = Manifest.Load(manifestPath);
            return new ExistingInstall(manifest.Meta.AppName, dir, manifest, manifestPath, manifest.DetectInstalledTags(dir), extraSources, "explicit manifest");
        }

        string? folder = directory ?? (target is not null && System.IO.Directory.Exists(target) ? target : null);
        if (folder is not null)
            return FromRecord(Path.GetFullPath(folder), extraSources, eglItem: null);
        if (target is null)
            return null;

        // Engines we installed are registered in LauncherInstalled.dat under their app name.
        foreach (var entry in EGLInstallations.ReadLauncherInstalled())
        {
            if (string.Equals(entry.AppName, target, StringComparison.OrdinalIgnoreCase)
                && FromRecord(entry.InstallLocation, extraSources, eglItem: null) is { } ours)
                return ours;
        }

        var item = EGLInstallations.ReadItems().FirstOrDefault(i =>
            string.Equals(i.AppName, target, StringComparison.OrdinalIgnoreCase)
            && System.IO.Directory.Exists(i.InstallLocation) && File.Exists(i.CompleteManifestPath));
        if (item is null)
            return null;

        var eglSources = extraSources.Count > 0 ? extraSources : item.BaseURLs.Select(u => new ChunkSource(u.TrimEnd('/'))).ToList();
        if (FromRecord(item.InstallLocation, eglSources, item) is { } adopted)
            return adopted;

        var eglManifest = Manifest.Load(item.CompleteManifestPath);
        return new ExistingInstall(item.AppName, item.InstallLocation, eglManifest, item.CompleteManifestPath,
            eglManifest.DetectInstalledTags(item.InstallLocation), eglSources, "Epic Games Launcher install", EGLItem: item);
    }

    private static ExistingInstall? FromRecord(string folder, List<ChunkSource> extraSources, EGLItem? eglItem)
    {
        if (InstallRecord.Load(folder) is not var (record, manifest))
            return null;

        var sources = extraSources.Count > 0 ? extraSources : record.BaseURLs.Select(u => new ChunkSource(u)).ToList();
        string origin = record.FileFilter is not null ? $"partial Unvault install: {record.FileFilter}"
            : eglItem is not null ? "Unvault (adopted from Epic Games Launcher)"
            : "Unvault install";
        return new ExistingInstall(record.AppName, folder, manifest, InstallRecord.ManifestPath(folder),
            record.InstallTags.ToHashSet(StringComparer.Ordinal), sources, origin, record, eglItem, ParseFilter(record.FileFilter));
    }

    public static Regex? ParseFilter(string? glob) => glob is null ? null : PathGlob.ToRegex(glob);

    /// <summary>Where a new engine goes by default: {engine install folder from settings}\{app name}.</summary>
    public static string DefaultInstallDirectory(string appName, AppSettings settings) =>
        Path.Combine(settings.ResolveEngineInstallRoot().Path, appName);
}
