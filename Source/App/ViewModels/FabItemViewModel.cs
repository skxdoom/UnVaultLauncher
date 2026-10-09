using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnVault.Core.Epic;
using UnVault.Core.Fab;
using UnVault.Core.Install;
using UnVault.Core.Vault;

namespace UnVault.App.ViewModels;

/// <summary>One engine version of a Fab item: its artifact, from the online library and/or the vault cache.</summary>
public sealed record FabVersion(string EngineAppName, string ArtifactID, FabProjectVersion? Library, VaultEntry? Vault)
{
    public bool IsDownloaded => Vault is { IsComplete: true };

    /// <summary>The Windows build Fab currently offers for this artifact.</summary>
    public string? LatestBuild => Library?.BuildVersions?.FirstOrDefault(b => b.Platform == "Windows")?.BuildVersion;

    /// <summary>Downloaded, but Fab has another (newer) build than the Vault Cache copy.</summary>
    public bool IsOutdated => IsDownloaded && IsOlder(Vault!.Build, LatestBuild);

    /// <summary>Fab's build is the current one, so any different build here counts as older.</summary>
    internal static bool IsOlder(string? have, string? latest) =>
        !string.IsNullOrEmpty(have) && !string.IsNullOrEmpty(latest) && !string.Equals(have, latest, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A plugin installed into an engine: where, in which folder, who put it there, and which build it is (if known).</summary>
public sealed record FabInstall(string EngineAppName, string EngineDirectory, string ArtifactID, PluginSource Source, string Folder, bool CanRemove,
    string? BuildVersion = null);

/// <summary>One row in the Fab library: a product with all its per-engine versions.</summary>
public partial class FabItemViewModel : ViewModelBase
{
    private readonly FabLibraryViewModel _owner;
    private readonly string? _thumbnailURL;
    private int _viewers;
    private Services.ThumbnailCache.Use? _thumbnailUse; // the picture held for the tiles showing this item

    public FabItemViewModel(FabLibraryViewModel owner, string key, string title, FabItemKind kind, FabLibraryItem? library,
        IReadOnlyList<FabVersion> versions, IReadOnlyList<FabInstall> installs, string? thumbnailURL)
    {
        _owner = owner;
        Key = key;
        Title = title;
        Kind = kind;
        Library = library;
        Versions = versions;
        Installs = installs;
        _thumbnailURL = thumbnailURL;
        SearchText = $"{title} {library?.Seller} {library?.Description} {string.Join(' ', (library?.Categories ?? []).Select(c => c.Name))}";

        // Versions can repeat an artifact (one per engine it supports); updates are per artifact.
        var latest = versions.Where(v => v.LatestBuild is not null).GroupBy(v => v.ArtifactID, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().LatestBuild, StringComparer.OrdinalIgnoreCase);
        OutdatedDownloads = [.. versions.Where(v => v.IsOutdated).GroupBy(v => v.ArtifactID, StringComparer.OrdinalIgnoreCase).Select(g => g.First())];
        // Only installs UnVault can bring up to date: its own, or EGL's in their own Marketplace folder.
        OutdatedInstalls = [.. installs.Where(i => i.CanRemove && FabVersion.IsOlder(i.BuildVersion, latest.GetValueOrDefault(i.ArtifactID)))];
    }

    /// <summary>Identifies the product across reloads; titles aren't unique (two sellers' "Garden Pack").</summary>
    public string Key { get; }

    public string Title { get; }
    public FabItemKind Kind { get; }
    public FabLibraryItem? Library { get; }
    public IReadOnlyList<FabVersion> Versions { get; }
    public IReadOnlyList<FabInstall> Installs { get; }
    public string SearchText { get; }

    public string KindLabel => FabKinds.Label(Kind);

    public string? SellerText => string.IsNullOrWhiteSpace(Library?.Seller) ? null : Library.Seller;

    /// <summary>The tile's line under the title: who made it, or what it is when that's unknown (a Vault Cache copy only).</summary>
    public string BylineText => SellerText ?? KindLabel;

    public string EngineVersionsText =>
        Versions.Count == 0 ? "No engine versions listed" : "UE " + CompactVersions(Versions.Select(v => v.EngineAppName));

    public bool IsDownloaded => Versions.Any(v => v.IsDownloaded);
    public string? DownloadedText => IsDownloaded ? "In Vault Cache for " + CompactVersions(Versions.Where(v => v.IsDownloaded).Select(v => v.EngineAppName)) : null;

    public bool IsInstalled => Installs.Count > 0;
    public string? InstalledText => IsInstalled ? "Installed in " + CompactVersions(Installs.Select(i => i.EngineAppName)) : null;

    /// <summary>Marks at the end of the tile's versions line, with their versions on hover; "Working…" in their place while busy.</summary>
    public bool ShowsInstalledMark => IsIdle && IsInstalled;
    public bool ShowsDownloadedMark => IsIdle && IsDownloaded;

    /// <summary>Only plugins go into engines; an installed item of unknown kind can still be removed.</summary>
    public bool ShowsRemove => Kind == FabItemKind.Plugin || IsInstalled;

    /// <summary>Vault Cache copies with a newer build on Fab (one per artifact).</summary>
    public IReadOnlyList<FabVersion> OutdatedDownloads { get; }

    /// <summary>Plugins in engines with a newer build on Fab.</summary>
    public IReadOnlyList<FabInstall> OutdatedInstalls { get; }

    public bool HasUpdate => OutdatedDownloads.Count > 0 || OutdatedInstalls.Count > 0;

    /// <summary>What an update would refresh, e.g. "Newer build on Fab for: installed in UE 5.7; Vault Cache copy for 5.6".</summary>
    public string? UpdateText
    {
        get
        {
            if (!HasUpdate)
                return null;
            var parts = new List<string>();
            if (OutdatedInstalls.Count > 0)
                parts.Add("installed in UE " + CompactVersions(OutdatedInstalls.Select(i => i.EngineAppName)));
            if (OutdatedDownloads.Count > 0)
                parts.Add("Vault Cache copy for " + CompactVersions(OutdatedDownloads.Select(v => v.EngineAppName)));
            return "Newer build on Fab: " + string.Join("; ", parts);
        }
    }

    public bool IsInLibrary => Library is not null;
    public bool HasFabPage => !string.IsNullOrEmpty(Library?.URL);

    /// <summary>What the tile's main button does for this kind of item.</summary>
    public string ActionText => Kind switch
    {
        FabItemKind.Plugin => "Install to Engine",
        FabItemKind.AssetPack => "Add to Project",
        FabItemKind.Project => "Create Project",
        _ => "Download",
    };

    /// <summary>The main button: Update while Fab has a newer build (as in the Epic Games Launcher), otherwise <see cref="ActionText"/>.</summary>
    public string PrimaryText => HasUpdate ? "Update" : ActionText;

    /// <summary>What the main button will do: what an update refreshes, or the action with the kind named, as the tile doesn't name it.</summary>
    public string PrimaryToolTip => UpdateText ?? Kind switch
    {
        FabItemKind.Plugin => "Install this plugin to Engine",
        FabItemKind.AssetPack => "Add this asset pack to Project",
        FabItemKind.Project => "Create Project",
        _ => "Download to Vault Cache",
    };

    [ObservableProperty] public partial Bitmap? Thumbnail { get; set; }

    /// <summary>An operation is working on this item's files; nothing else may start on them until it ends.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(ShowsInstalledMark), nameof(ShowsDownloadedMark))]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand), nameof(RemoveCommand))]
    public partial bool IsBusy { get; set; }

    public bool IsIdle => !IsBusy;

    /// <summary>A tile started showing this item (the grid only makes tiles for rows on screen): load its picture.</summary>
    internal async void ShowThumbnail()
    {
        if (++_viewers > 1 || _thumbnailURL is null)
            return;
        var use = _thumbnailUse = _owner.Owner.Services.Thumbnails.Get(_thumbnailURL, Key); // by item: Fab rotates the link
        try
        {
            var bitmap = await use.Picture;
            if (_thumbnailUse == use) // not scrolled away meanwhile
                Thumbnail = bitmap;
        }
        catch (Exception)
        {
            // async void: an escaping exception would end the app. A missing picture is just a placeholder.
        }
    }

    /// <summary>No tile shows this item any more: the picture goes back to the cache, which keeps recent ones for scrolling back.</summary>
    internal void HideThumbnail()
    {
        if (_viewers == 0 || --_viewers > 0)
            return;
        Thumbnail = null; // first, as the cache may free the picture once it's back
        if (_thumbnailUse is { } use)
        {
            _thumbnailUse = null;
            _owner.Owner.Services.Thumbnails.Release(use);
        }
    }

    public bool Matches(string search) =>
        search.Length == 0 || SearchText.Contains(search, StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private void Primary()
    {
        if (HasUpdate)
            _owner.OpenUpdate(this);
        else
            _owner.OpenAction(this);
    }

    /// <summary>The kind's own action, from the ⋯ menu while the main button is Update.</summary>
    [RelayCommand]
    private void Action() => _owner.OpenAction(this);

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void Download() => _owner.OpenDownload(this);

    [RelayCommand]
    private void OpenFabPage() => _owner.Owner.OpenURL(Library?.URL);

    public bool HasFolder => IsDownloaded || IsInstalled;

    /// <summary>Opens the downloaded copy in the Vault Cache; failing that, where the plugin is installed.</summary>
    [RelayCommand]
    private void ShowInFolder()
    {
        string? folder = Versions.FirstOrDefault(v => v.IsDownloaded)?.Vault?.Directory
            ?? Installs.FirstOrDefault(i => Directory.Exists(i.Folder))?.Folder;
        if (folder is not null)
            _owner.Owner.OpenURL(folder);
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void Remove() => _owner.OpenRemove(this);

    /// <summary>["UE_4.27","UE_5.3","UE_5.4","UE_5.5"] → "4.27, 5.3–5.5".</summary>
    internal static string CompactVersions(IEnumerable<string> engineAppNames)
    {
        var versions = engineAppNames.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(a => (Name: a.StartsWith("UE_", StringComparison.Ordinal) ? a[3..] : a, Version: EngineLibrary.ParseVersion(a)))
            .OrderBy(v => v.Version)
            .ToList();

        var parts = new List<string>();
        for (int i = 0; i < versions.Count; i++)
        {
            int start = i;
            while (i + 1 < versions.Count && Follows(versions[i + 1].Version, versions[i].Version))
                i++;
            parts.Add(i - start >= 2 ? $"{versions[start].Name}–{versions[i].Name}"
                : i > start ? $"{versions[start].Name}, {versions[i].Name}"
                : versions[start].Name);
        }
        return string.Join(", ", parts);
    }

    /// <summary>The engine release right after another: 5.7 after 5.6, and 5.0 after 4.27, as there was no 4.28.</summary>
    private static bool Follows(Version next, Version previous) =>
        next.Major == previous.Major ? next.Minor == previous.Minor + 1 : (previous.Major, previous.Minor, next.Major, next.Minor) == (4, 27, 5, 0);
}
