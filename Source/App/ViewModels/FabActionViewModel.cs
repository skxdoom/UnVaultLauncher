using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Unvault.Core.Fab;
using Unvault.Core.Install;
using Unvault.Core.Projects;
using Unvault.Core.Util;

namespace Unvault.App.ViewModels;

public enum FabActionMode { InstallPlugin, AddToProject, CreateProject, Download, RemovePlugin }

/// <summary>Something a Fab item can go into: an engine (plugins) or a project (asset packs).</summary>
public sealed class FabTargetViewModel(string title, string subtitle, string? badge, FabVersion? version, bool isAvailable, LocalEngine? engine, UnrealProject? project)
{
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
    public string? Badge { get; } = badge;
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
    public string TargetsHeading => Mode == FabActionMode.AddToProject ? "PROJECT" : "ENGINE";

    public ObservableCollection<FabTargetViewModel> Targets { get; } = [];
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
            string? badge = installed ? "installed"
                : version is null ? "no version for this engine"
                : !CanGet(version) ? "sign in to download"
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
            string? badge = !install.CanRemove ? "remove with EGL"
                : install.Source switch
                {
                    PluginSource.EGL => "installed by EGL",
                    PluginSource.Unlisted => "unregistered",
                    _ => null,
                };
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
        var projects = await Task.Run(() => ProjectLocator.FindProjects(_library.Owner.Settings.ResolveProjectFolders().Paths));
        foreach (var project in projects)
            Targets.Add(ProjectTarget(project));
        SelectedTarget = Targets.FirstOrDefault(t => t.IsAvailable);
        EmptyText = Targets.Count == 0 ? "No projects found. Use \"Choose a .uproject…\" to pick one." : null;
    }

    private FabTargetViewModel ProjectTarget(UnrealProject project)
    {
        string engine = project.EngineAppName ?? "";
        var usable = _item.Versions.Where(CanGet).ToList();
        FabVersion? version;
        string? badge = null;

        if (project.EngineAppName is null)
        {
            // Source builds associate by GUID; offer the newest version.
            version = usable.OrderByDescending(v => EngineLibrary.ParseVersion(v.EngineAppName)).FirstOrDefault();
            badge = version is null ? null : $"custom engine: uses the {Short(version.EngineAppName)} version";
        }
        else
        {
            version = BestVersion(usable.Where(v => SameEngine(v.EngineAppName, engine)))
                ?? usable.Where(v => EngineLibrary.ParseVersion(v.EngineAppName) <= EngineLibrary.ParseVersion(engine))
                    .OrderByDescending(v => EngineLibrary.ParseVersion(v.EngineAppName)).FirstOrDefault();
            if (version is not null && !SameEngine(version.EngineAppName, engine))
                badge = $"uses the {Short(version.EngineAppName)} version";
        }

        if (version is null)
            badge = _item.Versions.Any(v => EngineLibrary.ParseVersion(v.EngineAppName) <= EngineLibrary.ParseVersion(engine)) && !SignedIn
                ? "sign in to download" : "no compatible version";

        string opened = project.LastOpened is { } time ? $"  ·  opened {time.ToLocalTime():d}" : "";
        return new FabTargetViewModel(project.Name, $"UE {project.EngineAssociation}  ·  {project.Directory}{opened}", badge, version,
            isAvailable: version is not null, engine: null, project);
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
        var target = ProjectTarget(project);
        Targets.Insert(0, target);
        SelectedTarget = target;
        EmptyText = null;
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
                _library.StartRemove(_item, SelectedTarget!.Install!);
                break;
            default:
                _library.StartDownload(_item, SelectedVersion!.Version);
                break;
        }
    }

    private string DescribeSource(FabVersion version) =>
        version.IsOutdated
            ? Mode == FabActionMode.InstallPlugin
                ? "Your Vault Cache copy is an older build, so Fab's latest downloads straight into the engine."
                : "Your Vault Cache copy is an older build: it's updated first, downloading only the files that changed."
        : version.IsDownloaded ? $"Already in your Vault Cache ({Short(version.EngineAppName)} version): nothing to download."
        : Mode == FabActionMode.InstallPlugin ? "Downloads straight into the engine."
        : "Downloads into your Vault Cache first, so it can be reused later.";

    private static string DescribeRemoval(FabInstall install) => !install.CanRemove
        ? "EGL put this plugin among the engine's own folders, so there's no telling which files are its. Remove it with the Epic Games Launcher."
        : install.Source switch
    {
        PluginSource.Unvault => "Deletes the files Unvault installed.",
        PluginSource.EGL => "Installed by the Epic Games Launcher: deletes the plugin's folder and EGL's record of it.",
        _ => "Not registered with any launcher (copied in by hand or by another tool): deletes the plugin's folder.",
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
