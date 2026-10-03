using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Unvault.Core.Epic;
using Unvault.Core.Fab;
using Unvault.Core.Install;
using Unvault.Core.Vault;

namespace Unvault.App.ViewModels;

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
        // Only installs Unvault can bring up to date: its own, or EGL's in their own Marketplace folder.
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

    public string EngineVersionsText =>
        Versions.Count == 0 ? "No engine versions listed" : "Available for " + CompactVersions(Versions.Select(v => v.EngineAppName));

    public bool IsDownloaded => Versions.Any(v => v.IsDownloaded);
    public string? DownloadedText => IsDownloaded ? "Downloaded: " + CompactVersions(Versions.Where(v => v.IsDownloaded).Select(v => v.EngineAppName)) : null;

    /// <summary>Next to an "Installed" plate there's room for the word only; the versions are in the tooltip.</summary>
    public string? DownloadedPlateText => IsInstalled && IsDownloaded ? "Downloaded" : DownloadedText;

    public bool IsInstalled => Installs.Count > 0;
    public string? InstalledText => IsInstalled ? "Installed: " + CompactVersions(Installs.Select(i => i.EngineAppName)) : null;

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

    public string PrimaryText => Kind switch
    {
        FabItemKind.Plugin => "Install to Engine",
        FabItemKind.AssetPack => "Add to Project",
        FabItemKind.Project => "Create Project",
        _ => "Download",
    };

    [ObservableProperty] public partial Bitmap? Thumbnail { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; set; }

    public bool IsIdle => !IsBusy;

    /// <summary>A tile started showing this item (the grid only makes tiles for rows on screen): load its picture.</summary>
    internal async void ShowThumbnail()
    {
        if (++_viewers > 1 || _thumbnailURL is null)
            return;
        try
        {
            var bitmap = await _owner.Owner.Services.Thumbnails.GetAsync(_thumbnailURL, Key); // by item: Fab rotates the link
            if (_viewers > 0)
                Thumbnail = bitmap;
        }
        catch (Exception)
        {
            // async void: an escaping exception would end the app. A missing picture is just a placeholder.
        }
    }

    /// <summary>No tile shows this item any more. The cache keeps recent pictures, so scrolling back is instant.</summary>
    internal void HideThumbnail()
    {
        if (_viewers > 0 && --_viewers == 0)
            Thumbnail = null;
    }

    public bool Matches(string search) =>
        search.Length == 0 || SearchText.Contains(search, StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private void Primary() => _owner.OpenAction(this);

    [RelayCommand]
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

    [RelayCommand]
    private void Remove() => _owner.OpenRemove(this);

    [RelayCommand]
    private void Update() => _owner.OpenUpdate(this);

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
            while (i + 1 < versions.Count
                   && versions[i + 1].Version.Major == versions[i].Version.Major
                   && versions[i + 1].Version.Minor == versions[i].Version.Minor + 1)
                i++;
            parts.Add(i - start >= 2 ? $"{versions[start].Name}–{versions[i].Name}"
                : i > start ? $"{versions[start].Name}, {versions[i].Name}"
                : versions[start].Name);
        }
        return string.Join(", ", parts);
    }
}
