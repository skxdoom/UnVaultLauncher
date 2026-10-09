using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnVault.Core;
using UnVault.Core.EGL;
using UnVault.Core.Util;
using UnVault.Core.Vault;

namespace UnVault.App.ViewModels;

/// <summary>One row of the project folder list in Settings.</summary>
public partial class ProjectFolderItem : ViewModelBase
{
    private readonly Action<ProjectFolderItem> _madeDefault;

    public ProjectFolderItem(string path, Action<ProjectFolderItem> remove, Action<ProjectFolderItem> madeDefault)
    {
        Path = path;
        _madeDefault = madeDefault;
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public string Path { get; }
    public IRelayCommand RemoveCommand { get; }

    /// <summary>New projects are created here (the row's radio button); exactly one folder is the default.</summary>
    [ObservableProperty] public partial bool IsDefault { get; set; }

    partial void OnIsDefaultChanged(bool value)
    {
        if (value)
            _madeDefault(this);
    }
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
        EngineRootPlaceholder = Localized.Format(Strings.AutomaticPath, engineRoot.Path);
        EngineRootHint = Describe(engineRoot.Source, Strings.EngineRootPurpose);
        VaultPlaceholder = Localized.Format(Strings.AutomaticPath, vault.Path);
        var projects = automatic.ResolveProjectFolders(egl);
        ProjectFoldersAutomatic = Localized.Format(Strings.AutomaticPath, string.Join(", ", projects.Paths));
        ProjectFoldersHint = projects.Source == SettingSource.EpicGamesLauncher ? Strings.ProjectFoldersHintEGL : Strings.ProjectFoldersHintDocuments;

        var current = owner.Settings;
        EngineInstallRoot = current.EngineInstallRoot ?? "";
        VaultCacheDirectory = current.VaultCacheDirectory ?? "";
        ProjectFolders.CollectionChanged += (_, _) =>
        {
            // Saved with the default first; when it's removed, the first one left takes over.
            if (ProjectFolders.Count > 0 && !ProjectFolders.Any(f => f.IsDefault))
                ProjectFolders[0].IsDefault = true;
            OnPropertyChanged(nameof(HasNoProjectFolders));
        };
        foreach (string folder in current.ProjectDirectories ?? [])
            AddProjectFolder(folder);
        ParallelDownloads = current.ParallelDownloads;
        CheckForUpdates = current.CheckForUpdates;
        UpdateVaultSummary();
    }

    public string EngineRootPlaceholder { get; }
    public string EngineRootHint { get; }
    public string VaultPlaceholder { get; }

    /// <summary>Folders searched for projects; the first is where new projects go. Empty = automatic.</summary>
    public ObservableCollection<ProjectFolderItem> ProjectFolders { get; } = [];
    public bool HasNoProjectFolders => ProjectFolders.Count == 0;
    public string ProjectFoldersAutomatic { get; }
    public string ProjectFoldersHint { get; }

    [ObservableProperty] public partial string EngineInstallRoot { get; set; } = "";
    [ObservableProperty] public partial string VaultCacheDirectory { get; set; } = "";
    [ObservableProperty] public partial decimal? ParallelDownloads { get; set; }
    [ObservableProperty] public partial bool CheckForUpdates { get; set; }
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
                text = Strings.VaultFolderMissing;
            else
            {
                var summary = VaultCache.Summarize(folder.Path);
                text = summary.Count == 0
                    ? Strings.VaultNoAssets
                    : Localized.Plural(nameof(Strings.VaultAssets_Other), summary.Count, ByteSize.Format(summary.TotalBytes));
            }
            string source = folder.Source == SettingSource.EpicGamesLauncher ? " " + Strings.VaultSameAsEGL : "";
            Dispatcher.UIThread.Post(() =>
            {
                if (request == _summaryRequest)
                    VaultSummary = Strings.VaultPurpose + " " + text + source;
            });
        });
    }

    [RelayCommand]
    private async Task BrowseEngineRootAsync()
    {
        if (await PickAsync(Strings.PickEngineFolder, EngineInstallRoot) is { } picked)
            EngineInstallRoot = picked;
    }

    [RelayCommand]
    private async Task BrowseVaultCacheAsync()
    {
        if (await PickAsync(Strings.PickVaultFolder, VaultCacheDirectory) is { } picked)
            VaultCacheDirectory = picked;
    }

    [RelayCommand]
    private async Task AddProjectFolderAsync()
    {
        if (await PickAsync(Strings.PickProjectFolder, ProjectFolders.LastOrDefault()?.Path ?? "") is { } picked
            && !ProjectFolders.Any(f => string.Equals(Normalize(f.Path), Normalize(picked), StringComparison.OrdinalIgnoreCase)))
            AddProjectFolder(picked);
    }

    private void AddProjectFolder(string path) =>
        ProjectFolders.Add(new ProjectFolderItem(path, item => ProjectFolders.Remove(item), item =>
        {
            foreach (var other in ProjectFolders.Where(f => f != item))
                other.IsDefault = false;
        }));

    [RelayCommand]
    private void Cancel() => _owner.CloseDialog();

    [RelayCommand]
    private void Save()
    {
        string? engineRoot = Normalize(EngineInstallRoot);
        string? vault = Normalize(VaultCacheDirectory);
        // The default goes first, as that's where AppSettings creates new projects; the rest keep their order.
        var projectFolders = ProjectFolders.OrderByDescending(f => f.IsDefault).Select(f => Normalize(f.Path)).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (string? folder in projectFolders.Prepend(vault).Prepend(engineRoot))
        {
            if (folder is not null && !Path.IsPathFullyQualified(folder))
            {
                Error = Localized.Format(Strings.NotFullPath, folder);
                return;
            }
        }

        var settings = _owner.Settings;
        settings.EngineInstallRoot = engineRoot;
        settings.VaultCacheDirectory = vault;
        settings.ProjectDirectories = projectFolders.Count == 0 ? null : projectFolders;
        settings.ParallelDownloads = (int)Math.Clamp(ParallelDownloads ?? AppSettings.DefaultParallelDownloads, 1, AppSettings.MaxParallelDownloads);
        settings.CheckForUpdates = CheckForUpdates;
        try
        {
            settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = Localized.Format(Strings.SettingsSaveFailed, ex.Message);
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
        SettingSource.EpicGamesLauncher => $"{purpose} {Strings.LeaveEmptyEGLInstallFolder}",
        SettingSource.ExistingInstalls => $"{purpose} {Strings.LeaveEmptyExistingEngines}",
        _ => $"{purpose} {Strings.LeaveEmptyDefault}",
    };
}
