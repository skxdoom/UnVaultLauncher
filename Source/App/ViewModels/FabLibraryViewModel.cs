using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnVault.Core.EGL;
using UnVault.Core.Epic;
using UnVault.Core.Fab;
using UnVault.Core.Install;
using UnVault.Core.Projects;
using UnVault.Core.Util;
using UnVault.Core.Vault;

namespace UnVault.App.ViewModels;

/// <summary>One row of the Fab grid; <paramref name="Columns"/> is the page's, so a short last row keeps full-size tiles.</summary>
public sealed record FabItemRow(IReadOnlyList<FabItemViewModel> Items, int Columns);

/// <summary>
/// The Fab tab: the account's Fab library merged with what's already in the vault cache. Signed out, it still
/// shows (and can install) everything previously downloaded.
/// </summary>
public partial class FabLibraryViewModel : ViewModelBase
{
    private const string AllEngines = "All Engines";

    private List<FabItemViewModel> _all = [];
    private IReadOnlyList<FabLibraryItem>? _library;
    private DateTimeOffset? _libraryFetchedAt;
    private string? _libraryAccountID; // whose _library is
    private Task? _refreshing;
    private bool _rebuilding;
    private int _columns = 4;
    private CancellationTokenSource? _searchDelay;

    public FabLibraryViewModel(MainViewModel owner)
    {
        Owner = owner;
        owner.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsSignedIn))
                OnPropertyChanged(nameof(IsSignedOut));
        };
    }

    public MainViewModel Owner { get; }

    /// <summary>Raised when the user changes the search or a filter (not when the library reloads).</summary>
    public event EventHandler? FilterChanged;

    /// <summary>The items passing the search and filters.</summary>
    [ObservableProperty] public partial IReadOnlyList<FabItemViewModel> Items { get; private set; } = [];

    /// <summary><see cref="Items"/> cut into grid rows, so the view can create tiles only for rows on screen.</summary>
    [ObservableProperty] public partial IReadOnlyList<FabItemRow> Rows { get; private set; } = [];

    /// <summary>Tiles per row; the view sets it from its width.</summary>
    public int Columns
    {
        get => _columns;
        set
        {
            if (value < 1 || value == _columns)
                return;
            _columns = value;
            BuildRows();
        }
    }
    public IReadOnlyList<string> KindOptions { get; } = ["All Types", "Plugins", "Asset Packs", "Projects"];
    public ObservableCollection<string> EngineOptions { get; } = [AllEngines];

    public bool IsSignedOut => !Owner.IsSignedIn;
    public bool HasLoaded { get; private set; }

    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial int KindIndex { get; set; }
    [ObservableProperty] public partial string? EngineOption { get; set; } = AllEngines;
    [ObservableProperty] public partial bool DownloadedOnly { get; set; }
    [ObservableProperty] public partial bool UpdatesOnly { get; set; }

    /// <summary>How many items have a newer build on Fab (drives the dot on the Library tab).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdatesFilterText))]
    public partial int UpdateCount { get; private set; }

    public string UpdatesFilterText => UpdateCount > 0 ? $"Update Available ({UpdateCount:N0})" : "Update Available";
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial string? LoadingText { get; set; }
    [ObservableProperty] public partial string Summary { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => Error is not null;

    [ObservableProperty] public partial bool IsEmpty { get; set; }

    /// <summary>Shown when there's no tile: whether filters hide everything or there's nothing at all.</summary>
    [ObservableProperty] public partial string EmptyText { get; set; } = "";

    /// <summary>Filters once typing pauses rather than on every key.</summary>
    partial void OnSearchTextChanged(string value) => FilterSoon();

    private async void FilterSoon()
    {
        _searchDelay?.Cancel();
        var delay = _searchDelay = new CancellationTokenSource();
        try
        {
            await Task.Delay(100, delay.Token);
        }
        catch (OperationCanceledException)
        {
            return; // another key came first
        }
        OnFilterChanged();
    }

    /// <summary>Empties the search box and shows everything again at once (the × button, or Esc).</summary>
    [RelayCommand]
    private void ClearSearch()
    {
        if (SearchText.Length == 0)
            return;
        SearchText = "";
        _searchDelay?.Cancel();
        OnFilterChanged();
    }

    partial void OnKindIndexChanged(int value) => OnFilterChanged();
    partial void OnEngineOptionChanged(string? value) => OnFilterChanged();
    partial void OnDownloadedOnlyChanged(bool value) => OnFilterChanged();
    partial void OnUpdatesOnlyChanged(bool value) => OnFilterChanged();
    partial void OnIsLoadingChanged(bool value) => IsEmpty = !value && Items.Count == 0;

    private void OnFilterChanged()
    {
        if (_rebuilding)
            return;
        ApplyFilter();
        FilterChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Reloads the vault cache and installs; with <paramref name="includeLibrary"/>, also asks Fab for the library.</summary>
    public async Task LoadAsync(bool includeLibrary)
    {
        IsLoading = true;
        LoadingText = "Scanning downloads and installs…";
        try
        {
            string vaultDirectory = Owner.Settings.ResolveVaultCache().Path;
            var engines = Owner.LocalEngines;
            var finding = Task.Run(() => FindPluginInstalls(engines));
            IReadOnlyList<VaultEntry> vault;
            try
            {
                vault = await Task.Run(() => VaultCache.Scan(vaultDirectory));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                vault = [];
                Owner.Notice = $"Couldn't read the Vault Cache in {vaultDirectory}: {ex.Message}";
            }
            var installs = await finding;

            string? accountID = Owner.CurrentAccountID;
            if (accountID != _libraryAccountID)
            {
                // Signed out or into another account: nothing of the last one's may show, nor its fetch still under way.
                _library = null;
                _libraryFetchedAt = null;
                _libraryAccountID = accountID;
                _refreshing = null;
                Error = null;
            }

            if (accountID is not null && (includeLibrary || _library is null))
            {
                // Fab is slow to list a big library, so show the copy from last time meanwhile.
                if (_library is null && await Task.Run(() => FabLibraryCache.Load(accountID)) is { } saved && Owner.CurrentAccountID == accountID)
                {
                    _library = saved.Items;
                    _libraryFetchedAt = saved.FetchedAt;
                    SetItems(_library, vault, installs);
                    HasLoaded = true;
                }

                var refresh = _refreshing ??= RefreshLibraryAsync(accountID);
                try
                {
                    await refresh;
                }
                finally
                {
                    if (_refreshing == refresh)
                        _refreshing = null; // even after a failure, so the next load tries again
                }
                if (Owner.CurrentAccountID != accountID)
                    return; // the account changed meanwhile; its own load shows its library
            }

            SetItems(_library, vault, installs);
            HasLoaded = true;
        }
        finally
        {
            LoadingText = null;
            IsLoading = false;
        }
    }

    private async Task RefreshLibraryAsync(string accountID)
    {
        LoadingText = _library is null ? "Loading your library…" : "Updating…";
        try
        {
            var progress = new Progress<int>(read => LoadingText = $"{(_library is null ? "Loading" : "Updating")}… {read:N0} read");
            var library = await Owner.Services.API.GetFabLibraryAsync(progress);
            if (Owner.CurrentAccountID != accountID)
                return;
            _library = library;
            _libraryFetchedAt = DateTimeOffset.Now;
            Error = null;

            var snapshot = new FabLibrarySnapshot { AccountID = accountID, FetchedAt = _libraryFetchedAt.Value, Items = library };
            try
            {
                await Task.Run(() => FabLibraryCache.Save(snapshot));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Only costs the instant start next time.
            }
        }
        catch (NotLoggedInException ex)
        {
            Owner.SessionEnded(ex);
        }
        catch (Exception ex) when (ex is EpicAPIException or HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            // IOException and UnauthorizedAccessException: saving refreshed sign-in tokens failed.
            if (Owner.CurrentAccountID != accountID)
                return;
            Error = _library is null
                ? $"Couldn't load your library from Fab: {ex.Message}"
                : $"Couldn't update your library from Fab, so this is the list from {_libraryFetchedAt?.ToLocalTime():g}: {ex.Message}";
        }
    }

    /// <summary>Builds the rows: library items with their vault copies, then vault-only downloads grouped by product.</summary>
    internal void SetItems(IReadOnlyList<FabLibraryItem>? library, IReadOnlyList<VaultEntry> vault, IReadOnlyList<FabInstall> installs)
    {
        var vaultByArtifact = vault.GroupBy(v => v.ArtifactID, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<FabItemViewModel>();

        foreach (var item in (library ?? []).Where(i => !FabKinds.IsEngine(i)))
        {
            var versions = new List<FabVersion>();
            foreach (var projectVersion in item.ProjectVersions ?? [])
            {
                vaultByArtifact.TryGetValue(projectVersion.ArtifactID, out var downloaded);
                if (downloaded is not null)
                    used.Add(projectVersion.ArtifactID);
                foreach (string engine in projectVersion.EngineVersions ?? [])
                    versions.Add(new FabVersion(engine, projectVersion.ArtifactID, projectVersion, downloaded));
            }

            var kind = FabKinds.FromLibrary(item);
            if (kind == FabItemKind.Unknown && versions.FirstOrDefault(v => v.Vault is not null)?.Vault is { } sample)
                kind = FabKinds.FromVault(sample);
            string key = item.AssetID.Length > 0 ? $"fab:{item.AssetNamespace}/{item.AssetID}" : "fab:" + item.Title;
            items.Add(Row(key, item.Title, kind, item, versions, installs, item.ThumbnailURL));
        }

        // Downloads not (or no longer) in the library — or everything, when signed out.
        foreach (var group in vault.Where(v => !used.Contains(v.ArtifactID))
                     .GroupBy(v => v.ItemID.Length > 0 ? v.ItemID : v.Title, StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            var versions = group
                .Select(v => new FabVersion(FabKinds.EngineAppFromBuild(v.Version) ?? FabKinds.EngineAppFromBuild(v.Build) ?? "?", v.ArtifactID, null, v))
                .ToList();
            items.Add(Row("vault:" + group.Key, first.Title, FabKinds.FromVault(first), null, versions, installs, first.Thumbnail));
        }

        var busy = Owner.Operations.Where(o => o.IsRunning && o.FabItemKey is not null).Select(o => o.FabItemKey!).ToHashSet(StringComparer.Ordinal);
        foreach (var row in items)
            row.IsBusy = busy.Contains(row.Key);

        _all = items.OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase).ToList();

        // Rebuilding the options makes the combo box clear its selection; that's not the user changing the filter.
        _rebuilding = true;
        try
        {
            string? selectedEngine = EngineOption;
            EngineOptions.Clear();
            EngineOptions.Add(AllEngines);
            foreach (string engine in _all.SelectMany(i => i.Versions).Select(v => v.EngineAppName).Where(e => e.StartsWith("UE_", StringComparison.Ordinal))
                         .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(EngineLibrary.ParseVersion))
                EngineOptions.Add($"UE {engine[3..]}");
            EngineOption = selectedEngine is not null && EngineOptions.Contains(selectedEngine) ? selectedEngine : AllEngines;
        }
        finally
        {
            _rebuilding = false;
        }

        ApplyFilter();

        FabItemViewModel Row(string key, string title, FabItemKind kind, FabLibraryItem? libraryItem, List<FabVersion> versions, IReadOnlyList<FabInstall> allInstalls, string? thumbnail)
        {
            var artifacts = versions.Select(v => v.ArtifactID).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var itemInstalls = allInstalls.Where(i => artifacts.Contains(i.ArtifactID)).ToList();
            return new FabItemViewModel(this, key, title, kind, libraryItem, versions, itemInstalls, thumbnail);
        }
    }

    private void ApplyFilter()
    {
        var kind = KindIndex switch { 1 => FabItemKind.Plugin, 2 => FabItemKind.AssetPack, 3 => FabItemKind.Project, _ => (FabItemKind?)null };
        string? engine = EngineOption is { } option && option != AllEngines ? "UE_" + option[3..] : null;
        string search = SearchText.Trim();

        // Swapped in whole: adding a thousand items one by one meant a thousand change notifications.
        Items = _all.Where(item =>
                (kind is null || item.Kind == kind)
                && (engine is null || item.Versions.Any(v => string.Equals(v.EngineAppName, engine, StringComparison.OrdinalIgnoreCase)))
                && (!DownloadedOnly || item.IsDownloaded)
                && (!UpdatesOnly || item.HasUpdate)
                && item.Matches(search))
            .ToList();
        BuildRows();

        int downloaded = _all.Count(i => i.IsDownloaded);
        UpdateCount = _all.Count(i => i.HasUpdate);
        Summary = Items.Count == _all.Count ? $"{_all.Count:N0} items  ·  {downloaded:N0} downloaded" : $"{Items.Count:N0} of {_all.Count:N0} items";
        IsEmpty = !IsLoading && Items.Count == 0;
        EmptyText = _all.Count > 0 ? "Nothing matches. Try another search or filter."
            : HasError ? "" // the banner above says what went wrong
            : IsSignedOut ? "Your Vault Cache is empty."
            : "Your Fab library is empty.";
    }

    private void BuildRows() => Rows = Items.Chunk(_columns).Select(row => new FabItemRow(row, _columns)).ToList();

    /// <summary>Plugins in the installed engines, whoever put them there: UnVault, EGL, or something else.</summary>
    private static IReadOnlyList<FabInstall> FindPluginInstalls(IReadOnlyList<LocalEngine> engines)
    {
        var launcherInstalled = EGLInstallations.ReadLauncherInstalled();
        var installs = new List<FabInstall>();
        foreach (var engine in engines.Where(e => e.Exists))
        {
            try
            {
                installs.AddRange(EnginePlugins.Find(engine.Directory, launcherInstalled)
                    .Select(p => new FabInstall(engine.AppName, engine.Directory, p.ArtifactID, p.Source, p.Folder, p.CanRemove, p.BuildVersion)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable engine folder just shows no plugins.
            }
        }
        return installs;
    }

    internal void SetBusy(string? itemKey, bool busy)
    {
        if (itemKey is null)
            return;
        foreach (var item in _all.Where(i => i.Key == itemKey))
            item.IsBusy = busy;
    }

    internal async void OpenAction(FabItemViewModel item)
    {
        var mode = item.Kind switch
        {
            FabItemKind.Plugin => FabActionMode.InstallPlugin,
            FabItemKind.AssetPack => FabActionMode.AddToProject,
            FabItemKind.Project => FabActionMode.CreateProject,
            _ => FabActionMode.Download,
        };
        await OpenDialogAsync(item, mode);
    }

    internal async void OpenDownload(FabItemViewModel item) => await OpenDialogAsync(item, FabActionMode.Download);

    internal async void OpenRemove(FabItemViewModel item) => await OpenDialogAsync(item, FabActionMode.RemovePlugin);

    private async Task OpenDialogAsync(FabItemViewModel item, FabActionMode mode)
    {
        if (item.IsBusy)
            return; // two operations on the same files would collide
        var dialog = new FabActionViewModel(this, item, mode);
        Owner.Dialog = dialog;
        await dialog.LoadAsync();
    }

    internal void StartPluginInstall(FabItemViewModel item, FabVersion version, LocalEngine engine)
    {
        Owner.StartOperation(new OperationViewModel(Owner, $"Installing {item.Title} into UE {engine.AppName[3..]}", async (operation, status, cancellationToken) =>
        {
            if (version.IsDownloaded && !version.IsOutdated)
            {
                operation.SetPhase("Copying from Vault Cache");
                await FabWorkflow.InstallPluginFromVaultAsync(version.Vault!, engine.Directory, status, cancellationToken);
            }
            else
            {
                operation.SetPhase("Downloading");
                var artifact = Artifact(item, version);
                var source = await FabWorkflow.FetchAsync(Owner.Services.API, artifact, cancellationToken);
                await FabWorkflow.InstallPluginAsync(source, artifact, engine.Directory, Owner.CreateInstaller(), status, cancellationToken);
            }
            return $"Installed into {engine.Directory}.";
        }, engineAppName: engine.AppName, fabItemKey: item.Key));
    }

    internal void StartAddToProject(FabItemViewModel item, FabVersion version, UnrealProject project)
    {
        Owner.StartOperation(new OperationViewModel(Owner, $"Adding {item.Title} to {project.Name}", async (operation, status, cancellationToken) =>
        {
            var entry = await EnsureInVaultAsync(item, version, operation, status, cancellationToken);
            var copy = operation.BeginPhase("Copying into project");
            int files = await FabWorkflow.AddToProjectAsync(entry, project.Directory, copy, cancellationToken);
            return $"Added {files:N0} files to {project.Directory}\\Content.";
        }, fabItemKey: item.Key));
    }

    internal void StartCreateProject(FabItemViewModel item, FabVersion version, string targetDirectory)
    {
        Owner.StartOperation(new OperationViewModel(Owner, $"Creating project from {item.Title}", async (operation, status, cancellationToken) =>
        {
            var entry = await EnsureInVaultAsync(item, version, operation, status, cancellationToken);
            var copy = operation.BeginPhase("Copying project");
            string uproject = await FabWorkflow.CreateProjectAsync(entry, targetDirectory, copy, cancellationToken);
            return $"Created {uproject}.";
        }, fabItemKey: item.Key));
    }

    internal void StartDownload(FabItemViewModel item, FabVersion version)
    {
        Owner.StartOperation(new OperationViewModel(Owner, $"Downloading {item.Title} ({version.EngineAppName[3..]})", async (operation, status, cancellationToken) =>
        {
            var entry = await EnsureInVaultAsync(item, version, operation, status, cancellationToken);
            return $"In your Vault Cache: {entry.Directory} ({ByteSize.Format(entry.Size)}).";
        }, fabItemKey: item.Key));
    }

    /// <param name="itemKey">The library item it belongs to, kept busy meanwhile; null for a plugin the library doesn't list.</param>
    /// <returns>The removal, already started, for anything that shows its outcome (Installed Plugins).</returns>
    internal OperationViewModel StartRemove(string title, string? itemKey, FabInstall install)
    {
        string engine = install.EngineAppName[3..];
        var removal = new OperationViewModel(Owner, $"Removing {title} from UE {engine}", async (operation, _, _) =>
        {
            operation.SetPhase("Deleting files");
            var removed = await Task.Run(() => FabWorkflow.UninstallPlugin(install.EngineDirectory, install.ArtifactID));
            return $"Removed from UE {engine}; freed {ByteSize.Format(removed.BytesFreed)}.";
        }, engineAppName: install.EngineAppName, fabItemKey: itemKey);
        Owner.StartOperation(removal);
        return removal;
    }

    /// <summary>The library or Vault Cache item an artifact belongs to; null when it's in neither (or the library hasn't loaded).</summary>
    internal FabItemViewModel? FindItem(string artifactID) =>
        _all.FirstOrDefault(i => i.Versions.Any(v => string.Equals(v.ArtifactID, artifactID, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Uses the Vault Cache copy if it's current; otherwise downloads (or updates) it there first.</summary>
    private async Task<VaultEntry> EnsureInVaultAsync(FabItemViewModel item, FabVersion version, OperationViewModel operation, InstallStatus status, CancellationToken cancellationToken)
    {
        if (version.IsDownloaded && !version.IsOutdated)
            return version.Vault!;

        operation.SetPhase(version.IsOutdated ? "Updating Vault Cache copy" : "Downloading");
        var artifact = Artifact(item, version);
        var source = await FabWorkflow.FetchAsync(Owner.Services.API, artifact, cancellationToken);
        return await FabWorkflow.DownloadToVaultAsync(source, artifact, Owner.Settings.ResolveVaultCache().Path, Owner.CreateInstaller(), status, cancellationToken);
    }

    internal void OpenUpdate(FabItemViewModel item)
    {
        if (!item.HasUpdate || item.IsBusy)
            return;
        Owner.Dialog = new ConfirmViewModel(Owner, $"Update {item.Title}?", "Only files that changed are downloaded.",
            "Update", () => StartUpdate(item, item.OutdatedDownloads, item.OutdatedInstalls));
    }

    /// <summary>Brings outdated copies of an item up to Fab's build, one after another.</summary>
    internal void StartUpdate(FabItemViewModel item, IReadOnlyList<FabVersion> downloads, IReadOnlyList<FabInstall> installs)
    {
        Owner.StartOperation(new OperationViewModel(Owner, $"Updating {item.Title}", async (operation, _, cancellationToken) =>
        {
            foreach (var version in downloads)
            {
                var status = operation.BeginPhase($"Updating Vault Cache copy ({version.EngineAppName[3..]})");
                var artifact = Artifact(item, version);
                var source = await FabWorkflow.FetchAsync(Owner.Services.API, artifact, cancellationToken);
                await FabWorkflow.DownloadToVaultAsync(source, artifact, Owner.Settings.ResolveVaultCache().Path, Owner.CreateInstaller(), status, cancellationToken);
            }
            foreach (var install in installs)
            {
                string engine = install.EngineAppName[3..];
                var version = item.Versions.First(v => string.Equals(v.ArtifactID, install.ArtifactID, StringComparison.OrdinalIgnoreCase));
                var artifact = Artifact(item, version);
                var checking = operation.BeginPhase($"Checking UE {engine}");
                var source = await FabWorkflow.FetchAsync(Owner.Services.API, artifact, cancellationToken);
                var update = await FabWorkflow.PlanPluginUpdateAsync(source, artifact, install.EngineDirectory, checking, cancellationToken);
                var status = operation.BeginPhase($"Updating in UE {engine}");
                await FabWorkflow.ApplyPluginUpdateAsync(update, Owner.CreateInstaller(), status, cancellationToken);
            }
            int count = downloads.Count + installs.Count;
            return $"Updated {count} {(count == 1 ? "copy" : "copies")} to Fab's latest build.";
        }, fabItemKey: item.Key));
    }

    private static FabArtifact Artifact(FabItemViewModel item, FabVersion version) =>
        item.Library is not null && version.Library is not null
            ? new FabArtifact(item.Library, version.Library)
            : throw new InstallException("Sign in to download this item.");
}
