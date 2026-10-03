using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Unvault.Core;
using Unvault.Core.EGL;
using Unvault.Core.Util;
using Unvault.Core.Vault;

namespace Unvault.App.ViewModels;

/// <summary>One row of the project folder list in Settings.</summary>
public partial class ProjectFolderItem : ViewModelBase
{
    public ProjectFolderItem(string path, Action<ProjectFolderItem> remove, Action<ProjectFolderItem> makeDefault)
    {
        Path = path;
        RemoveCommand = new RelayCommand(() => remove(this));
        MakeDefaultCommand = new RelayCommand(() => makeDefault(this));
    }

    public string Path { get; }
    public IRelayCommand RemoveCommand { get; }
    public IRelayCommand MakeDefaultCommand { get; }

    /// <summary>The first folder: new projects are created here.</summary>
    [ObservableProperty] public partial bool IsDefault { get; set; }
}

/// <summary>The settings dialog. Empty folder fields mean "automatic" (follow the Epic Games Launcher's setup).</summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly MainViewModel _owner;
    private readonly EGLLauncherSettings _egl;
    private int _summaryRequest;

    public SettingsViewModel(MainViewModel owner, EGLLauncherSettings egl)
    {
        _owner = owner;
        _egl = egl;

        var automatic = new AppSettings();
        var engineRoot = automatic.ResolveEngineInstallRoot(egl);
        var vault = automatic.ResolveVaultCache(egl);
        EngineRootPlaceholder = $"Automatic: {engineRoot.Path}";
        EngineRootHint = Describe(engineRoot.Source, "New engines go into a subfolder here, e.g. UE_5.8.");
        VaultPlaceholder = $"Automatic: {vault.Path}";
        EGLVaultCache = egl.ActiveVaultCache;
        var projects = automatic.ResolveProjectFolders(egl);
        ProjectFoldersAutomatic = "Automatic: " + string.Join(", ", projects.Paths);
        ProjectFoldersHint = "When adding assets, the projects in these folders are offered (besides the ones the editor opened recently); new projects are created in the first one. "
            + (projects.Source == SettingSource.EpicGamesLauncher ? "Leave empty to use the Epic Games Launcher's project folders." : @"Leave empty for Documents\Unreal Projects.");

        var current = owner.Settings;
        EngineInstallRoot = current.EngineInstallRoot ?? "";
        VaultCacheDirectory = current.VaultCacheDirectory ?? "";
        ProjectFolders.CollectionChanged += (_, _) =>
        {
            for (int i = 0; i < ProjectFolders.Count; i++)
                ProjectFolders[i].IsDefault = i == 0;
            OnPropertyChanged(nameof(HasNoProjectFolders));
        };
        foreach (string folder in current.ProjectDirectories ?? [])
            AddProjectFolder(folder);
        ParallelDownloads = current.ParallelDownloads;
        UpdateVaultSummary();
    }

    public string EngineRootPlaceholder { get; }
    public string EngineRootHint { get; }
    public string VaultPlaceholder { get; }

    /// <summary>The VaultCache the Epic Games Launcher is set to, if any — offered as a one-click choice.</summary>
    public string? EGLVaultCache { get; }
    public bool HasEGLVaultCache => EGLVaultCache is not null;

    /// <summary>Folders searched for projects; the first is where new projects go. Empty = automatic.</summary>
    public ObservableCollection<ProjectFolderItem> ProjectFolders { get; } = [];
    public bool HasNoProjectFolders => ProjectFolders.Count == 0;
    public string ProjectFoldersAutomatic { get; }
    public string ProjectFoldersHint { get; }

    [ObservableProperty] public partial string EngineInstallRoot { get; set; } = "";
    [ObservableProperty] public partial string VaultCacheDirectory { get; set; } = "";
    [ObservableProperty] public partial decimal? ParallelDownloads { get; set; }
    [ObservableProperty] public partial string VaultSummary { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => Error is not null;

    partial void OnVaultCacheDirectoryChanged(string value) => UpdateVaultSummary();

    /// <summary>Counts what's already in the chosen cache, so pointing at EGL's shows its downloads are found.</summary>
    private void UpdateVaultSummary()
    {
        int request = Interlocked.Increment(ref _summaryRequest);
        var probe = new AppSettings { VaultCacheDirectory = string.IsNullOrWhiteSpace(VaultCacheDirectory) ? null : VaultCacheDirectory.Trim() };
        var folder = probe.ResolveVaultCache(_egl);

        _ = Task.Run(() =>
        {
            string text;
            if (!Directory.Exists(folder.Path))
                text = "This folder doesn't exist yet; it will be created on the first Fab download.";
            else
            {
                var summary = VaultCache.Summarize(folder.Path);
                text = summary.Count == 0
                    ? "No downloaded assets here yet."
                    : $"{summary.Count} downloaded {(summary.Count == 1 ? "asset" : "assets")} ({ByteSize.Format(summary.TotalBytes)}). Unvault reuses them instead of downloading again.";
            }
            string source = folder.Source == SettingSource.EpicGamesLauncher ? " Same folder the Epic Games Launcher uses." : "";
            Dispatcher.UIThread.Post(() =>
            {
                if (request == _summaryRequest)
                    VaultSummary = "Fab asset packs and plugins are downloaded and kept here. " + text + source;
            });
        });
    }

    [RelayCommand]
    private async Task BrowseEngineRootAsync()
    {
        if (await PickAsync("Folder for new engines", EngineInstallRoot) is { } picked)
            EngineInstallRoot = picked;
    }

    [RelayCommand]
    private async Task BrowseVaultCacheAsync()
    {
        if (await PickAsync("Vault Cache folder", VaultCacheDirectory) is { } picked)
            VaultCacheDirectory = picked;
    }

    [RelayCommand]
    private async Task AddProjectFolderAsync()
    {
        if (await PickAsync("Folder with Unreal projects", ProjectFolders.LastOrDefault()?.Path ?? "") is { } picked
            && !ProjectFolders.Any(f => string.Equals(Normalize(f.Path), Normalize(picked), StringComparison.OrdinalIgnoreCase)))
            AddProjectFolder(picked);
    }

    private void AddProjectFolder(string path) =>
        ProjectFolders.Add(new ProjectFolderItem(path, item => ProjectFolders.Remove(item), item => ProjectFolders.Move(ProjectFolders.IndexOf(item), 0)));

    [RelayCommand]
    private void UseEGLVaultCache()
    {
        if (EGLVaultCache is not null)
            VaultCacheDirectory = EGLVaultCache;
    }

    [RelayCommand]
    private void Cancel() => _owner.CloseDialog();

    [RelayCommand]
    private void Save()
    {
        string? engineRoot = Normalize(EngineInstallRoot);
        string? vault = Normalize(VaultCacheDirectory);
        var projectFolders = ProjectFolders.Select(f => Normalize(f.Path)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (string? folder in projectFolders.Prepend(vault).Prepend(engineRoot))
        {
            if (folder is not null && !Path.IsPathFullyQualified(folder))
            {
                Error = $"\"{folder}\" isn't a full folder path (like E:\\Epic Games).";
                return;
            }
        }

        var settings = _owner.Settings;
        settings.EngineInstallRoot = engineRoot;
        settings.VaultCacheDirectory = vault;
        settings.ProjectDirectories = projectFolders.Count == 0 ? null : projectFolders;
        settings.ParallelDownloads = (int)Math.Clamp(ParallelDownloads ?? AppSettings.DefaultParallelDownloads, 1, 64);
        try
        {
            settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = $"Couldn't save settings: {ex.Message}";
            return;
        }
        _owner.CloseDialog();
    }

    private async Task<string?> PickAsync(string title, string current) =>
        _owner.Interaction is null ? null : await _owner.Interaction.PickFolderAsync(title, string.IsNullOrWhiteSpace(current) ? null : current);

    private static string? Normalize(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : Path.TrimEndingDirectorySeparator(value.Trim());

    private static string Describe(SettingSource source, string purpose) => source switch
    {
        SettingSource.EpicGamesLauncher => $"{purpose} Leave empty to use the Epic Games Launcher's install folder.",
        SettingSource.ExistingInstalls => $"{purpose} Leave empty to install next to your existing engines.",
        _ => $"{purpose} Leave empty for the default.",
    };
}
