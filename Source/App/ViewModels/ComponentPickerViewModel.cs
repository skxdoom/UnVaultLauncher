using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Unvault.Core.Epic;
using Unvault.Core.Install;
using Unvault.Core.Manifests;
using Unvault.Core.Util;

namespace Unvault.App.ViewModels;

/// <summary>A checkbox in the component picker.</summary>
public partial class ComponentOptionViewModel(ComponentPickerViewModel picker, string tag, bool wasInstalled, string sizeText, string detailText) : ViewModelBase
{
    public string Tag { get; } = tag;
    public string DisplayName { get; } = ComponentNames.GetDisplayName(tag);
    public bool WasInstalled { get; } = wasInstalled;

    /// <summary>Disk impact on its own, e.g. "18.5 GB".</summary>
    public string SizeText { get; } = sizeText;

    /// <summary>e.g. "5.5 GB download" or "installed".</summary>
    public string DetailText { get; } = detailText;

    [ObservableProperty] public partial bool IsSelected { get; set; } = wasInstalled;

    partial void OnIsSelectedChanged(bool value) => picker.Recalculate();
}

/// <summary>An engine version offered in the install dialog.</summary>
public sealed record EngineVersionOption(EpicAsset Asset, string Label);

/// <summary>
/// Choose components for a new install, or change them on an existing one. Totals update live as
/// boxes are ticked, computed from the build manifest.
/// </summary>
public partial class ComponentPickerViewModel : ViewModelBase
{
    /// <summary>Preselected for new installs: small, commonly wanted. Platforms and symbols are opt-in.</summary>
    private static readonly string[] DefaultTags = ["starter_content", "templates", "engine_source"];

    private readonly MainViewModel _owner;
    private readonly EngineCardViewModel? _engine;
    private readonly Func<EpicAsset, CancellationToken, Task<InstallSource>> _fetch;
    private EngineVersionOption? _initialVersion;
    private string _defaultDirectory = "";
    private InstallSource? _source;
    private ExistingInstall? _install;
    private Manifest? _manifest;
    private int _recalculation;
    private int _versionRequest;

    private ComponentPickerViewModel(MainViewModel owner, EngineCardViewModel? engine, Func<EpicAsset, CancellationToken, Task<InstallSource>>? fetch)
    {
        _owner = owner;
        _engine = engine;
        _fetch = fetch ?? ((asset, cancellationToken) => InstallWorkflow.FetchAsync(owner.Services.API, asset, cancellationToken));
        IsModify = engine is not null;
        Title = engine is not null ? "Modify " + engine.Title : "Install Unreal Engine";
        Directory = engine?.Local?.Directory ?? "";
    }

    /// <summary>
    /// A new install: choose the version (from <paramref name="versions"/>), then its components.
    /// <paramref name="fetch"/> gets a version's manifest; tests pass a fake, the app asks Epic.
    /// </summary>
    public static ComponentPickerViewModel ForInstall(MainViewModel owner, IReadOnlyList<EpicAsset> versions, string? preselectAppName = null,
        Func<EpicAsset, CancellationToken, Task<InstallSource>>? fetch = null)
    {
        var picker = new ComponentPickerViewModel(owner, null, fetch);
        foreach (var asset in versions.OrderByDescending(a => EngineLibrary.ParseVersion(a.AppName)).ThenBy(a => a.AppName, StringComparer.OrdinalIgnoreCase))
            picker.VersionOptions.Add(new EngineVersionOption(asset, $"Unreal Engine {EngineLibrary.ShortBuildVersion(asset.BuildVersion)}"));
        picker._initialVersion = picker.VersionOptions.FirstOrDefault(o => string.Equals(o.Asset.AppName, preselectAppName, StringComparison.OrdinalIgnoreCase))
            ?? picker.VersionOptions.FirstOrDefault();
        return picker;
    }

    public static ComponentPickerViewModel ForModify(MainViewModel owner, EngineCardViewModel engine) => new(owner, engine, fetch: null);

    public string Title { get; }
    public bool IsModify { get; }
    public bool IsInstall => !IsModify;

    /// <summary>Install mode: engine versions the account can install and doesn't have yet, newest first.</summary>
    public ObservableCollection<EngineVersionOption> VersionOptions { get; } = [];

    [ObservableProperty] public partial EngineVersionOption? SelectedVersion { get; set; }

    partial void OnSelectedVersionChanged(EngineVersionOption? value)
    {
        if (value is not null)
            _ = LoadVersionAsync(value);
    }

    public ObservableCollection<ComponentOptionViewModel> ContentOptions { get; } = [];
    public ObservableCollection<ComponentOptionViewModel> PlatformOptions { get; } = [];
    public ObservableCollection<ComponentOptionViewModel> DebugOptions { get; } = [];

    public bool HasPlatformOptions => PlatformOptions.Count > 0;
    public bool HasDebugOptions => DebugOptions.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    public partial bool IsLoading { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    public partial string? Error { get; set; }

    public bool IsReady => !IsLoading && Error is null;

    [ObservableProperty] public partial string Directory { get; set; }
    [ObservableProperty] public partial string Subtitle { get; set; } = "Reading the build manifest…";
    [ObservableProperty] public partial string CoreSizeText { get; set; } = "";
    [ObservableProperty] public partial string SummaryText { get; set; } = "";
    [ObservableProperty] public partial string FreeSpaceText { get; set; } = "";
    /// <summary>Live problem with the current choice, e.g. not enough disk space.</summary>
    [ObservableProperty] public partial string? Warning { get; set; }

    /// <summary>Fixed remark about this install, e.g. that EGL installed it.</summary>
    [ObservableProperty] public partial string? Note { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial bool CanConfirm { get; set; }

    [ObservableProperty] public partial string ConfirmText { get; set; } = "Install";

    partial void OnDirectoryChanged(string value) => Recalculate();

    private IEnumerable<ComponentOptionViewModel> AllOptions => ContentOptions.Concat(PlatformOptions).Concat(DebugOptions);

    private HashSet<string> SelectedTags => AllOptions.Where(o => o.IsSelected).Select(o => o.Tag).ToHashSet(StringComparer.Ordinal);

    public async Task LoadAsync()
    {
        try
        {
            if (IsModify)
            {
                var install = await Task.Run(() => _engine!.Local!.Load()) ?? throw new InvalidOperationException("The install's manifest couldn't be read.");
                await ShowInstallAsync(install);
            }
            else if (_initialVersion is null)
            {
                IsLoading = false;
                Error = "You already have every engine version your account can install.";
            }
            else
            {
                SelectedVersion = _initialVersion; // loads it
            }
        }
        catch (Exception ex)
        {
            IsLoading = false;
            Error = ex.Message;
        }
    }

    /// <summary>Fetches a version's manifest and shows its components; a newer choice cancels out an older one.</summary>
    private async Task LoadVersionAsync(EngineVersionOption option)
    {
        int request = Interlocked.Increment(ref _versionRequest);
        IsLoading = true;
        Error = null;
        CanConfirm = false;
        SummaryText = "";
        Subtitle = "Reading the build manifest…";
        ClearOptions();

        // The folder follows the version (…\UE_5.8) unless the user chose their own.
        string newDefault = InstallLocator.DefaultInstallDirectory(option.Asset.AppName, _owner.Settings);
        if (Directory.Length == 0 || string.Equals(Directory, _defaultDirectory, StringComparison.OrdinalIgnoreCase))
            Directory = newDefault;
        _defaultDirectory = newDefault;

        try
        {
            var source = await _fetch(option.Asset, CancellationToken.None);
            if (request != _versionRequest)
                return;
            _source = source;
            await ShowManifestAsync(source.Manifest, new HashSet<string>(StringComparer.Ordinal), request);
        }
        catch (Exception ex)
        {
            if (request != _versionRequest)
                return;
            IsLoading = false;
            Error = ex.Message;
        }
    }

    private void ClearOptions()
    {
        ContentOptions.Clear();
        PlatformOptions.Clear();
        DebugOptions.Clear();
        OnPropertyChanged(nameof(HasPlatformOptions));
        OnPropertyChanged(nameof(HasDebugOptions));
    }

    /// <summary>Modify mode: shows an existing install's components. Separate from loading so tests can supply one.</summary>
    internal Task ShowInstallAsync(ExistingInstall install)
    {
        _install = install;
        if (install.IsEGLOnly)
            Note = "Installed by the Epic Games Launcher. After this change, let Unvault Launcher manage it: an EGL verify or update may bring removed components back.";
        return ShowManifestAsync(install.Manifest, install.InstallTags);
    }

    /// <summary>
    /// Fills the options from a manifest. Separate from loading so tests can supply a manifest directly.
    /// <paramref name="versionRequest"/>: the version load this belongs to; skipped if the user has since picked another.
    /// </summary>
    internal async Task ShowManifestAsync(Manifest manifest, IReadOnlySet<string> installed, int versionRequest = -1)
    {
        {
            var (core, impacts) = await Task.Run(() =>
                (manifest.MeasureSelection(new HashSet<string>()), ComponentPlanner.Analyze(manifest, installed)));
            if (versionRequest >= 0 && versionRequest != _versionRequest)
                return;

            _manifest = manifest;
            ClearOptions();
            Subtitle = manifest.Meta.BuildVersion;
            CoreSizeText = ByteSize.Format(core.InstallBytes);
            foreach (var impact in impacts.OrderBy(i => ComponentNames.GetDisplayName(i.Tag), StringComparer.OrdinalIgnoreCase))
            {
                string detail = impact.Installed ? "on disk" : $"{ByteSize.Format(impact.DownloadBytes)} download";
                var option = new ComponentOptionViewModel(this, impact.Tag, impact.Installed, ByteSize.Format(impact.DiskBytes), detail);
                if (!IsModify && DefaultTags.Contains(impact.Tag))
                    option.IsSelected = true;

                (ComponentNames.GetGroup(impact.Tag) switch
                {
                    ComponentGroup.TargetPlatform => PlatformOptions,
                    ComponentGroup.Debugging => DebugOptions,
                    _ => ContentOptions,
                }).Add(option);
            }

            OnPropertyChanged(nameof(HasPlatformOptions));
            OnPropertyChanged(nameof(HasDebugOptions));
            IsLoading = false;
            Recalculate();
        }
    }

    /// <summary>Recomputes totals in the background; only the latest request updates the screen.</summary>
    public void Recalculate()
    {
        if (IsLoading || Error is not null)
            return;

        int request = Interlocked.Increment(ref _recalculation);
        var tags = SelectedTags;
        string directory = Directory;
        CanConfirm = false;

        _ = Task.Run(() =>
        {
            try
            {
                ComputeTotals(request, tags, directory);
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (request == _recalculation)
                        Warning = $"Couldn't work out the sizes: {ex.Message}";
                });
            }
        });
    }

    private void ComputeTotals(int request, HashSet<string> tags, string directory)
    {
        string summary, confirm, freeSpace;
        bool canConfirm;
        string? warning = null;
        long free = FreeBytes(directory);

        if (IsModify)
        {
            var plan = InstallWorkflow.PlanModify(_install!, tags);
            summary = plan.IsEmpty ? "No changes"
                : string.Join("  ·  ", new[]
                {
                    plan.ToRemove.Count > 0 ? $"Frees {ByteSize.Format(plan.BytesFreed)}" : null,
                    plan.ToAdd is { } add ? $"Downloads {ByteSize.Format(add.DownloadBytes)} (+{ByteSize.Format(add.InstallBytes)} on disk)" : null,
                }.Where(s => s is not null));
            confirm = plan.IsEmpty ? "Apply" : "Apply changes";
            canConfirm = !plan.IsEmpty;
            if (plan.ToAdd is { } adding && free >= 0 && free < adding.InstallBytes)
                warning = "Not enough free space for the added components.";
        }
        else
        {
            var size = _manifest!.MeasureSelection(tags);
            summary = $"Download {ByteSize.Format(size.DownloadBytes)}  ·  {ByteSize.Format(size.InstallBytes)} on disk";
            confirm = "Install";
            canConfirm = !string.IsNullOrWhiteSpace(directory);
            if (free >= 0 && free < size.InstallBytes)
                warning = "Not enough free space on that drive.";
        }

        freeSpace = free >= 0 ? $"{ByteSize.Format(free)} free on {Path.GetPathRoot(directory)}" : "";

        Dispatcher.UIThread.Post(() =>
        {
            if (request != _recalculation)
                return;
            SummaryText = summary;
            ConfirmText = confirm;
            FreeSpaceText = freeSpace;
            CanConfirm = canConfirm;
            Warning = warning;
        });
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (_owner.Interaction is null)
            return;
        string? parent = Path.GetDirectoryName(Directory);
        string? picked = await _owner.Interaction.PickFolderAsync("Choose where to install", parent);
        if (picked is not null)
            Directory = Path.Combine(picked, _engine?.AppName ?? SelectedVersion?.Asset.AppName ?? "UnrealEngine");
    }

    [RelayCommand]
    private void Cancel() => _owner.CloseDialog();

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        var tags = SelectedTags;
        _owner.CloseDialog();
        if (IsModify)
            _owner.StartModify(_engine!, InstallWorkflow.PlanModify(_install!, tags));
        else
            _owner.StartInstall(SelectedVersion!.Asset.AppName, "Unreal Engine " + EngineLibrary.ShortBuildVersion(_source!.Manifest.Meta.BuildVersion),
                _source, tags, Path.GetFullPath(Directory));
    }

    private static long FreeBytes(string directory)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(directory));
            return root is null ? -1 : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return -1;
        }
    }
}
