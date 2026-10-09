using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnVault.Core.Fab;
using UnVault.Core.Install;
using UnVault.Core.Projects;
using UnVault.Core.Util;

namespace UnVault.App.ViewModels;

public enum FabActionMode { InstallPlugin, AddToProject, CreateProject, Download, RemovePlugin }

/// <summary>Something a Fab item can go into: an engine (plugins) or a project (asset packs).</summary>
public sealed class FabTargetViewModel(string title, string subtitle, string? badge, FabVersion? version, bool isAvailable, LocalEngine? engine, UnrealProject? project)
{
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
    public string? Badge { get; } = badge;

    /// <summary>Why the badge says what it does (its tooltip).</summary>
    public string? BadgeTip { get; init; }
    public FabVersion? Version { get; } = version;
    public bool IsAvailable { get; } = isAvailable;
    public LocalEngine? Engine { get; } = engine;
    public UnrealProject? Project { get; } = project;

    /// <summary>For removal: the plugin install this row stands for.</summary>
    public FabInstall? Install { get; init; }
}

public sealed record FabVersionOption(FabVersion Version, string Label);

/// <summary>
/// The dialog behind a Fab row's buttons: pick the engine (plugin) or project (asset pack) it goes into, the
/// version and folder for a new project / a plain download, or the engine to remove a plugin from.
/// </summary>
public partial class FabActionViewModel : ViewModelBase
{
    private readonly FabLibraryViewModel _library;
    private readonly FabItemViewModel _item;

    public FabActionViewModel(FabLibraryViewModel library, FabItemViewModel item, FabActionMode mode)
    {
        _library = library;
        _item = item;
        Mode = mode;
        Title = mode switch
        {
            FabActionMode.InstallPlugin => $"Install {item.Title}",
            FabActionMode.AddToProject => $"Add {item.Title} to a project",
            FabActionMode.CreateProject => $"Create a project from {item.Title}",
            FabActionMode.RemovePlugin => $"Remove {item.Title}",
            _ => $"Download {item.Title}",
        };
        ConfirmText = mode switch
        {
            FabActionMode.InstallPlugin => "Install",
            FabActionMode.AddToProject => "Add to Project",
            FabActionMode.CreateProject => "Create Project",
            FabActionMode.RemovePlugin => "Remove",
            _ => "Download",
        };
        Subtitle = $"{item.KindLabel}  ·  {(mode == FabActionMode.RemovePlugin ? item.InstalledText : item.EngineVersionsText)}";
    }

    public FabActionMode Mode { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public string ConfirmText { get; }

    public bool ShowsTargets => Mode is FabActionMode.InstallPlugin or FabActionMode.AddToProject or FabActionMode.RemovePlugin;
    public bool ShowsVersionChoice => Mode is FabActionMode.CreateProject or FabActionMode.Download;
    public bool ShowsProjectLocation => Mode == FabActionMode.CreateProject;
    public bool CanBrowseProject => Mode == FabActionMode.AddToProject;
    public bool ShowsProjectSearch => Mode == FabActionMode.AddToProject;
    public string TargetsHeading => Mode == FabActionMode.AddToProject ? "PROJECT" : "ENGINE";

    public ObservableCollection<FabTargetViewModel> Targets { get; } = [];

    /// <summary>Add to Project: every project found, by name; <see cref="Targets"/> lists the ones matching the search.</summary>
    private readonly List<FabTargetViewModel> _projects = [];

    /// <summary>Engines built from source on this PC, which projects name by ID instead of version.</summary>
    private IReadOnlyDictionary<string, CustomEngine> _customEngines = new Dictionary<string, CustomEngine>();

    /// <summary>Names as people read them: case doesn't matter, and "Project2" comes before "Project10".</summary>
    private static readonly StringComparer ByName =
        StringComparer.Create(CultureInfo.CurrentCulture, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);

    /// <summary>Narrows the project list by name (not folder: projects usually share one, like "Unreal Projects").</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearProjectSearchCommand))]
    public partial string ProjectSearch { get; set; } = "";

    partial void OnProjectSearchChanged(string value) => FilterProjects();
    public ObservableCollection<FabVersionOption> VersionChoices { get; } = [];

    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial string? EmptyText { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial FabTargetViewModel? SelectedTarget { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial FabVersionOption? SelectedVersion { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial string ProjectParent { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial string ProjectName { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    public partial string? Note { get; set; }

    public bool HasNote => Note is not null;

    partial void OnSelectedTargetChanged(FabTargetViewModel? value) =>
        Note = value?.Install is { } install ? DescribeRemoval(install)
            : value?.Version is { } version ? DescribeSource(version)
            : null;
    partial void OnSelectedVersionChanged(FabVersionOption? value) => Note = value is null ? null : DescribeSource(value.Version);

    private bool SignedIn => _library.Owner.IsSignedIn;

    /// <summary>A version can be used if it's already downloaded, or can be downloaded (signed in, in the online library).</summary>
    private bool CanGet(FabVersion version) => version.IsDownloaded || (SignedIn && version.Library is not null && _item.Library is not null);

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            switch (Mode)
            {
                case FabActionMode.InstallPlugin: LoadEngines(); break;
                case FabActionMode.AddToProject: await LoadProjectsAsync(); break;
                case FabActionMode.CreateProject: LoadVersions(onlyDownloadable: false); SetProjectDefaults(); break;
                case FabActionMode.Download: LoadVersions(onlyDownloadable: true); break;
                case FabActionMode.RemovePlugin: await LoadInstallsAsync(); break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            EmptyText = $"Couldn't look for targets: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void LoadEngines()
    {
        foreach (var engine in _library.Owner.LocalEngines.Where(e => e.Exists))
        {
            var version = BestVersion(_item.Versions.Where(v => SameEngine(v.EngineAppName, engine.AppName)));
            bool installed = _item.Installs.Any(i => SamePath(i.EngineDirectory, engine.Directory));
            string? badge = installed ? "Installed"
                : version is null ? "No version for this engine"
                : !CanGet(version) ? "Sign in to download"
                : null;
            Targets.Add(new FabTargetViewModel($"Unreal Engine {engine.AppName[3..]}", engine.Directory, badge, version,
                isAvailable: badge is null, engine, project: null));
        }
        SelectedTarget = Targets.FirstOrDefault(t => t.IsAvailable);
        EmptyText = Targets.Count == 0 ? "No installed engines found." : SelectedTarget is null ? "This plugin can't go into any of your installed engines." : null;
    }

    private async Task LoadInstallsAsync()
    {
        var installs = _item.Installs.OrderByDescending(i => EngineLibrary.ParseVersion(i.EngineAppName)).ToList();
        var sizes = await Task.Run(() => installs.Select(i => EnginePlugins.FolderSize(i.Folder)).ToList());
        for (int i = 0; i < installs.Count; i++)
        {
            var install = installs[i];
            // Removing works the same whoever installed it, so the note below says that; a badge only says why a row is off.
            string? badge = install.CanRemove ? null : "Remove with EGL";
            string where = !install.CanRemove ? "Installed by EGL outside Engine\\Plugins\\Marketplace"
                : sizes[i] > 0 ? $"{install.Folder}  ·  {ByteSize.Format(sizes[i])}"
                : install.Folder;
            Targets.Add(new FabTargetViewModel($"Unreal Engine {install.EngineAppName[3..]}", where,
                badge, version: null, isAvailable: install.CanRemove, engine: null, project: null) { Install = install });
        }
        SelectedTarget = Targets.FirstOrDefault(t => t.IsAvailable) ?? Targets.FirstOrDefault();
        EmptyText = Targets.Count == 0 ? "This plugin isn't in any of your engines." : null;
    }

    private async Task LoadProjectsAsync()
    {
        var (projects, customEngines) = await Task.Run(() =>
            (ProjectLocator.FindProjects(_library.Owner.Settings.ResolveProjectFolders().Paths), CustomEngines.Registered()));
        ShowProjects(projects, customEngines);
    }

    /// <summary>Lists the projects by name. Separate from loading so tests can supply projects and engines.</summary>
    internal void ShowProjects(IEnumerable<UnrealProject> projects, IReadOnlyDictionary<string, CustomEngine>? customEngines = null)
    {
        _customEngines = customEngines ?? new Dictionary<string, CustomEngine>();
        _projects.Clear();
        _projects.AddRange(projects.OrderBy(p => p.Name, ByName).Select(ProjectTarget));
        FilterProjects(); // nothing preselected: the project is the user's pick
    }

    private void FilterProjects()
    {
        string search = ProjectSearch.Trim();
        var selected = SelectedTarget;
        Targets.Clear(); // also clears the list's selection, restored below if that project is still listed
        foreach (var target in _projects.Where(t => t.Title.Contains(search, StringComparison.OrdinalIgnoreCase)))
            Targets.Add(target);
        SelectedTarget = selected is not null && Targets.Contains(selected) ? selected : null;
        EmptyText = _projects.Count == 0 ? "No projects found. Use \"Choose a .uproject…\" to pick one."
            : Targets.Count == 0 ? "No projects match the search."
            : null;
    }

    [RelayCommand(CanExecute = nameof(CanClearProjectSearch))]
    private void ClearProjectSearch() => ProjectSearch = "";

    private bool CanClearProjectSearch() => ProjectSearch.Length > 0;

    private FabTargetViewModel ProjectTarget(UnrealProject project)
    {
        // Launcher engines go by version ("5.7"); custom ones by an ID, looked up among the engines built on this PC.
        string? engine = project.EngineAppName;
        string engineName = $"UE {project.EngineAssociation}";
        if (engine is null)
        {
            var custom = CustomEngines.Find(_customEngines, project.EngineAssociation);
            engine = custom?.Version is { } customVersion ? "UE_" + customVersion : null;
            engineName = engine is null ? "Custom Engine" : $"Custom Engine {Short(engine)}";
            if (engine is null)
            {
                // No telling which version it could open, and assets saved by a newer engine don't open in an older one.
                string why = custom is null
                    ? "This project's custom engine isn't registered on this PC, so there's no telling which version of this item would open in it."
                    : $"Couldn't read the version of the custom engine in {custom.Directory}, so there's no telling which version of this item would open in it.";
                return new FabTargetViewModel(project.Name, $"{engineName}  ·  {project.Directory}",
                    custom is null ? "Engine version not installed" : "Engine version unknown", version: null, isAvailable: false, engine: null, project)
                {
                    BadgeTip = why,
                };
            }
        }

        // The version made for this engine, else the newest one made for an older engine: newer engines open older assets.
        var engineVersion = EngineLibrary.ParseVersion(engine);
        var usable = _item.Versions.Where(CanGet).ToList();
        var version = BestVersion(usable.Where(v => SameEngine(v.EngineAppName, engine)))
            ?? usable.Where(v => EngineLibrary.ParseVersion(v.EngineAppName) <= engineVersion)
                .OrderByDescending(v => EngineLibrary.ParseVersion(v.EngineAppName)).FirstOrDefault();

        string? badge = null, tip = null;
        if (version is not null && !SameEngine(version.EngineAppName, engine))
        {
            badge = $"Last available from UE {Short(version.EngineAppName)}";
            tip = $"This item has no {Short(engine)} version, so its {Short(version.EngineAppName)} version can be added.";
        }
        else if (version is null && !SignedIn && _item.Versions.Any(v => EngineLibrary.ParseVersion(v.EngineAppName) <= engineVersion))
        {
            badge = "Sign in to download";
            tip = "A version that fits this project isn't in your Vault Cache yet.";
        }
        else if (version is null)
        {
            // Every version is made for a newer engine, and an engine can't open assets saved by a newer one.
            string? oldest = _item.Versions.Select(v => v.EngineAppName).Where(EngineLibrary.IsEngineApp).MinBy(EngineLibrary.ParseVersion);
            badge = "Unsupported engine version";
            tip = oldest is null ? "This item is made for a newer engine version." : $"This item is made for a newer {Short(oldest)} engine version.";
        }

        return new FabTargetViewModel(project.Name, $"{engineName}  ·  {project.Directory}", badge, version,
            isAvailable: version is not null, engine: null, project)
        {
            BadgeTip = tip,
        };
    }

    private void LoadVersions(bool onlyDownloadable)
    {
        foreach (var version in _item.Versions
                     .Where(v => onlyDownloadable ? v.Library is not null && SignedIn : CanGet(v))
                     .GroupBy(v => v.ArtifactID).Select(g => BestVersion(g)!)
                     .OrderByDescending(v => EngineLibrary.ParseVersion(v.EngineAppName)))
        {
            string state = version.IsOutdated ? "  (update available)" : version.IsDownloaded ? "  (downloaded)" : "";
            VersionChoices.Add(new FabVersionOption(version, $"Unreal Engine {Short(version.EngineAppName)}{state}"));
        }
        SelectedVersion = Mode == FabActionMode.Download
            ? VersionChoices.FirstOrDefault(o => !o.Version.IsDownloaded || o.Version.IsOutdated) ?? VersionChoices.FirstOrDefault()
            : VersionChoices.FirstOrDefault();
        EmptyText = VersionChoices.Count == 0 ? (SignedIn ? "No downloadable versions." : "Sign in to download this item.") : null;
    }

    private void SetProjectDefaults()
    {
        ProjectParent = _library.Owner.Settings.ResolveProjectFolders().ForNewProjects;
        ProjectName = SafeFolderName(_item.Title);
    }

    [RelayCommand]
    private async Task BrowseProjectFileAsync()
    {
        if (_library.Owner.Interaction is null)
            return;
        string? file = await _library.Owner.Interaction.PickFileAsync("Choose a project", null, "Unreal project", "*.uproject");
        if (file is null || ProjectLocator.Read(file) is not { } project)
            return;
        // The project just picked goes first, whatever its name, and shows even if the search wouldn't find it.
        var target = ProjectTarget(project);
        _projects.Insert(0, target);
        if (ProjectSearch.Length > 0)
            ProjectSearch = "";
        else
            FilterProjects();
        SelectedTarget = target;
    }

    [RelayCommand]
    private async Task BrowseProjectParentAsync()
    {
        if (_library.Owner.Interaction is not null
            && await _library.Owner.Interaction.PickFolderAsync("Where to create the project", ProjectParent) is { } folder)
            ProjectParent = folder;
    }

    [RelayCommand]
    private void Cancel() => _library.Owner.CloseDialog();

    private bool CanConfirm() => Mode switch
    {
        FabActionMode.InstallPlugin or FabActionMode.AddToProject => SelectedTarget is { IsAvailable: true },
        FabActionMode.CreateProject => SelectedVersion is not null && !string.IsNullOrWhiteSpace(ProjectName) && Path.IsPathFullyQualified(ProjectParent),
        FabActionMode.RemovePlugin => SelectedTarget is { IsAvailable: true, Install: not null },
        _ => SelectedVersion is not null,
    };

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        _library.Owner.CloseDialog();
        switch (Mode)
        {
            case FabActionMode.InstallPlugin:
                _library.StartPluginInstall(_item, SelectedTarget!.Version!, SelectedTarget.Engine!);
                break;
            case FabActionMode.AddToProject:
                _library.StartAddToProject(_item, SelectedTarget!.Version!, SelectedTarget.Project!);
                break;
            case FabActionMode.CreateProject:
                _library.StartCreateProject(_item, SelectedVersion!.Version, Path.Combine(ProjectParent, ProjectName.Trim()));
                break;
            case FabActionMode.RemovePlugin:
                _library.StartRemove(_item.Title, _item.Key, SelectedTarget!.Install!);
                break;
            default:
                _library.StartDownload(_item, SelectedVersion!.Version);
                break;
        }
    }

    /// <summary>Null when there's nothing to add: a plugin that isn't downloaded yet just downloads into the engine.</summary>
    private string? DescribeSource(FabVersion version) =>
        version.IsOutdated
            ? Mode == FabActionMode.InstallPlugin
                ? "Your Vault Cache copy is an older build, so Fab's latest downloads straight into the engine."
                : "Your Vault Cache copy is an older build: it's updated first, downloading only the files that changed."
        : version.IsDownloaded ? $"Already in your Vault Cache ({Short(version.EngineAppName)} version): nothing to download."
        : Mode switch
        {
            FabActionMode.InstallPlugin => null,
            FabActionMode.AddToProject => "Downloads into your Vault Cache and adds it to the project.",
            FabActionMode.CreateProject => "Downloads into your Vault Cache and creates the project.",
            _ => "Downloads into your Vault Cache, ready to add to projects.",
        };

    private static string DescribeRemoval(FabInstall install) => !install.CanRemove
        ? "EGL put this plugin among the engine's own folders, so there's no telling which files are its. Remove it with the Epic Games Launcher."
        : install.Source switch
    {
        PluginSource.UnVault => "Deletes the plugin installed by UnVault Launcher.",
        PluginSource.EGL => "Deletes the plugin installed by the Epic Games Launcher.",
        _ => "Deletes the plugin copied in by hand or by another tool.",
    } + " Projects that use the plugin won't find it any more.";

    /// <summary>Prefer a downloaded copy (no network) over one that has to be fetched.</summary>
    private static FabVersion? BestVersion(IEnumerable<FabVersion> versions) =>
        versions.OrderByDescending(v => v.IsDownloaded).ThenByDescending(v => v.Library is not null).FirstOrDefault();

    private static bool SameEngine(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static bool SamePath(string a, string b) => string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);
    private static string Short(string engineAppName) => engineAppName.StartsWith("UE_", StringComparison.Ordinal) ? engineAppName[3..] : engineAppName;

    /// <summary>"Modular Warehouse  V. 2" → "ModularWarehouseV2": UE project names can't have spaces.</summary>
    private static string SafeFolderName(string title)
    {
        var name = new StringBuilder();
        foreach (char c in title)
        {
            if (char.IsAsciiLetterOrDigit(c))
                name.Append(c);
        }
        return name.Length == 0 ? "NewProject" : char.IsDigit(name[0]) ? "P" + name : name.ToString();
    }
}
