using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Unvault.App.Services;
using Unvault.Core;
using Unvault.Core.EGL;
using Unvault.Core.Epic;
using Unvault.Core.Fab;
using Unvault.Core.Install;
using Unvault.Core.Util;

namespace Unvault.App.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private List<EngineCardViewModel> _allEngines = [];
    private IReadOnlyList<LocalEngine> _local = [];
    private IReadOnlyList<EpicAsset> _owned = [];

    public MainViewModel(AppServices services)
    {
        Services = services;
        Fab = new FabLibraryViewModel(this);
        Fab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FabLibraryViewModel.UpdateCount))
            {
                OnPropertyChanged(nameof(HasLibraryUpdates));
                OnPropertyChanged(nameof(LibraryUpdatesText));
            }
        };
        Operations.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasOperations));
        UpdateAccount();
    }

    public AppServices Services { get; }
    public IUserInteraction? Interaction { get; set; }

    /// <summary>Shared with the CLI: %LOCALAPPDATA%\UnvaultLauncher\settings.json.</summary>
    public AppSettings Settings { get; } = AppSettings.Load();

    public FabLibraryViewModel Fab { get; }

    /// <summary>Shows the dot on the Library tab.</summary>
    public bool HasLibraryUpdates => Fab.UpdateCount > 0;

    public string? LibraryUpdatesText => Fab.UpdateCount switch
    {
        0 => null,
        1 => "1 item has an update on Fab",
        var n => $"{n:N0} items have updates on Fab",
    };

    /// <summary>Engine installs found by the last refresh (also used by the Fab tab to pick plugin targets).</summary>
    public IReadOnlyList<LocalEngine> LocalEngines => _local;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnginesTab))]
    public partial bool IsFabTab { get; set; }

    public bool IsEnginesTab => !IsFabTab;

    /// <summary>Engines shown (older versions filtered unless asked for).</summary>
    public ObservableCollection<EngineCardViewModel> Engines { get; } = [];

    public ObservableCollection<OperationViewModel> Operations { get; } = [];
    public bool HasOperations => Operations.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignedOut))]
    public partial bool IsSignedIn { get; set; }

    public bool IsSignedOut => !IsSignedIn;

    [ObservableProperty] public partial string DisplayName { get; set; } = "";

    [ObservableProperty] public partial bool IsLoading { get; set; }

    /// <summary>What a refresh is doing right now, shown next to the page title; null when idle.</summary>
    [ObservableProperty] public partial string? LoadingText { get; set; }

    /// <summary>A problem worth telling the user about (failed sign-in, Epic unreachable…).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string? Notice { get; set; }

    public bool HasNotice => Notice is not null;

    /// <summary>True when no engine is installed yet (the page then invites installing one).</summary>
    [ObservableProperty] public partial bool HasNoEngines { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDialog))]
    public partial ViewModelBase? Dialog { get; set; }

    public bool HasDialog => Dialog is not null;

    partial void OnDialogChanged(ViewModelBase? oldValue, ViewModelBase? newValue)
    {
        // These read build manifests (an engine's is large); give that memory back once they're closed.
        if (oldValue is ComponentPickerViewModel or FabActionViewModel)
            MemoryRelief.Release();
    }

    /// <summary>Shown next to the name in the header, e.g. "v0.1.0".</summary>
    public string VersionText { get; } = "v" + ProductInfo.Version;

    /// <summary>Engine versions the account owns but hasn't installed (stale EGL records count as not installed).</summary>
    public IReadOnlyList<EpicAsset> InstallableEngines =>
        _owned.Where(o => !_local.Any(l => l.Exists && string.Equals(l.AppName, o.AppName, StringComparison.OrdinalIgnoreCase))).ToList();

    /// <summary>First load: engines, then (signed in) the library in the background, so the Library tab shows pending updates before it's opened.</summary>
    public async Task StartAsync()
    {
        await RefreshAsync();
        if (IsSignedIn && !Fab.HasLoaded && !Fab.IsLoading)
            await Fab.LoadAsync(includeLibrary: true);
    }

    /// <summary>Rescans local installs and, when signed in, asks Epic which engines the account can install.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        await RefreshCoreAsync(includeOwned: !IsFabTab);
        if (IsFabTab)
            await Fab.LoadAsync(includeLibrary: true);
    }

    [RelayCommand]
    private void ShowEngines() => IsFabTab = false;

    /// <summary>
    /// Switches at once; the first visit starts loading the library, which shows its own progress. Not awaited: an async
    /// command can't run again until it finishes, so the tab button stayed disabled for the whole first load.
    /// </summary>
    [RelayCommand]
    private void ShowFab()
    {
        IsFabTab = true;
        if (!Fab.HasLoaded && !Fab.IsLoading)
            _ = Fab.LoadAsync(includeLibrary: true);
    }

    private async Task RefreshCoreAsync(bool includeOwned)
    {
        IsLoading = true;
        LoadingText = "Looking for installed engines…";
        try
        {
            _local = await Task.Run(EngineLibrary.ScanLocal);
            string? accountID = Services.Account.IsLoggedIn ? Services.Account.Session?.AccountID : null;
            if (accountID is null)
                _owned = [];
            else if (_owned.Count == 0 && await Task.Run(() => OwnedEnginesCache.Load(accountID)) is { } saved)
                _owned = saved.Engines; // Epic's list from last time, so update badges and installable versions are there at once

            // The installed engines show right away; the local scan is quick, Epic's list is not.
            SetEngines(_local, _owned);

            if (includeOwned && accountID is not null)
            {
                int installed = _local.Where(l => l.Exists).Select(l => l.AppName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                LoadingText = $"{installed:N0} installed  ·  checking Epic for versions and updates…";
                try
                {
                    _owned = (await Services.API.GetAssetsAsync()).Where(a => a.IsEngine).ToList();
                    Notice = null;
                    await SaveOwnedEnginesAsync(accountID);
                }
                catch (NotLoggedInException)
                {
                    _owned = [];
                    UpdateAccount();
                }
                catch (Exception ex) when (ex is EpicAPIException or HttpRequestException or TaskCanceledException)
                {
                    Notice = $"Couldn't reach Epic: {ex.Message}";
                }
                SetEngines(_local, _owned);
            }
        }
        finally
        {
            LoadingText = null;
            IsLoading = false;
        }
    }

    private async Task SaveOwnedEnginesAsync(string accountID)
    {
        var snapshot = new OwnedEnginesSnapshot { AccountID = accountID, FetchedAt = DateTimeOffset.Now, Engines = [.. _owned] };
        try
        {
            await Task.Run(() => OwnedEnginesCache.Save(snapshot));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only costs the instant start next time.
        }
    }

    /// <summary>
    /// Rebuilds the engine cards: one per engine on this machine (plus EGL records whose files are gone), newest
    /// major.minor first. Versions the account could install are offered through the + (install engine) button instead.
    /// </summary>
    internal void SetEngines(IEnumerable<LocalEngine> local, IEnumerable<EpicAsset> owned)
    {
        var localList = local.ToList();
        var ownedList = owned.ToList();
        _local = localList;
        _owned = ownedList;
        var busy = Operations.Where(o => o.IsRunning && o.EngineAppName is not null).Select(o => o.EngineAppName!).ToHashSet(StringComparer.OrdinalIgnoreCase);

        _allEngines = localList.Select(l => l.AppName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(app => new EngineCardViewModel(this, app,
                localList.FirstOrDefault(l => Same(l.AppName, app) && l.Exists) ?? localList.First(l => Same(l.AppName, app)),
                ownedList.FirstOrDefault(o => Same(o.AppName, app)))
            {
                IsBusy = busy.Contains(app),
            })
            .OrderByDescending(e => e.Version.Major)
            .ThenByDescending(e => e.Version.Minor)
            .ThenBy(e => e.AppName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Engines.Clear();
        foreach (var engine in _allEngines)
            Engines.Add(engine);
        HasNoEngines = Engines.Count == 0;

        static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    internal void SetBusy(OperationViewModel operation, bool busy)
    {
        Fab.SetBusy(operation.FabItemKey, busy);
        if (operation.EngineAppName is not { } appName)
            return;
        foreach (var engine in _allEngines.Where(e => string.Equals(e.AppName, appName, StringComparison.OrdinalIgnoreCase)))
            engine.IsBusy = busy;
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (Interaction is null)
            return;
        string? pasted = await Interaction.SignInAsync();
        if (string.IsNullOrWhiteSpace(pasted))
            return;

        try
        {
            await Services.Account.LoginWithAuthorizationCodeAsync(EpicAuthClient.ExtractAuthorizationCode(pasted));
            Notice = null;
        }
        catch (Exception ex) when (ex is EpicAPIException or HttpRequestException)
        {
            Notice = $"Sign-in failed: {ex.Message}";
            return;
        }

        UpdateAccount();
        await RefreshAfterAccountChangeAsync();
    }

    [RelayCommand]
    private async Task SignOutAsync()
    {
        try
        {
            await Services.Account.LogoutAsync();
        }
        catch (Exception ex) when (ex is EpicAPIException or HttpRequestException)
        {
            // The local session is deleted regardless; Epic will expire the tokens on its own.
        }
        FabLibraryCache.Delete();
        OwnedEnginesCache.Delete();
        if (Interaction is not null)
            await Interaction.ForgetSignInAsync();

        UpdateAccount();
        await RefreshAfterAccountChangeAsync();
    }

    private async Task RefreshAfterAccountChangeAsync()
    {
        await RefreshCoreAsync(includeOwned: true);
        if (Fab.HasLoaded || IsSignedIn)
            await Fab.LoadAsync(includeLibrary: true);
    }

    private void UpdateAccount()
    {
        IsSignedIn = Services.Account.IsLoggedIn;
        DisplayName = IsSignedIn ? Services.Account.Session?.DisplayName ?? "" : "";
    }

    [RelayCommand]
    private void DismissNotice() => Notice = null;

    [RelayCommand]
    private Task InstallEngineAsync() => OpenInstallAsync(null);

    /// <summary>Opens the install dialog, optionally with a version preselected (e.g. from a stale EGL record's card).</summary>
    internal async Task OpenInstallAsync(string? appName)
    {
        if (!IsSignedIn)
        {
            // Epic only tells signed-in accounts which versions they have and where the files are.
            await SignInAsync();
            if (!IsSignedIn)
                return;
        }
        if (_owned.Count == 0)
            await RefreshCoreAsync(includeOwned: true);

        var picker = ComponentPickerViewModel.ForInstall(this, InstallableEngines, appName);
        Dialog = picker;
        await picker.LoadAsync();
    }

    internal async Task OpenModifyAsync(EngineCardViewModel engine)
    {
        var picker = ComponentPickerViewModel.ForModify(this, engine);
        Dialog = picker;
        await picker.LoadAsync();
    }

    internal void CloseDialog() => Dialog = null;

    [RelayCommand]
    private void OpenSettings() => Dialog = new SettingsViewModel(this, EGLLauncherSettings.Read());

    internal Installer CreateInstaller() => new(Services.HTTP) { MaxParallelDownloads = Settings.ParallelDownloads };

    internal void StartInstall(string appName, string title, InstallSource source, IReadOnlySet<string> tags, string directory)
    {
        StartOperation(new OperationViewModel(this, $"Installing {title}", async (operation, status, cancellationToken) =>
        {
            var plan = InstallWorkflow.PlanInstall(source, tags);
            operation.SetPhase("Downloading");
            await InstallWorkflow.InstallAsync(source, plan, tags, null, directory, CreateInstaller(), status, register: true, cancellationToken);
            return $"Installed to {directory}. Projects for {appName[3..]} now open with it.";
        }, appName));
    }

    internal void StartModify(EngineCardViewModel engine, ModifyPlan plan)
    {
        StartOperation(new OperationViewModel(this, $"Changing components of {engine.Title}", async (operation, status, cancellationToken) =>
        {
            operation.SetPhase(plan.ToAdd is null ? "Removing files" : "Downloading");
            var cleaned = await InstallWorkflow.ApplyModifyAsync(plan, CreateInstaller(), status, cancellationToken);
            var parts = new List<string>();
            if (cleaned.FilesDeleted > 0)
                parts.Add($"freed {ByteSize.Format(cleaned.BytesFreed)}");
            if (plan.ToAdd is { } added)
                parts.Add($"added {added.Files.Count:N0} files");
            return parts.Count > 0 ? "Done: " + string.Join(", ", parts) + "." : "Done.";
        }, engine.AppName));
    }

    internal void StartVerify(EngineCardViewModel engine)
    {
        if (engine.Local is null)
            return;
        var local = engine.Local;
        StartOperation(new OperationViewModel(this, $"Verifying {engine.Title}", async (operation, status, cancellationToken) =>
        {
            operation.SetPhase("Reading manifest");
            var install = local.Load() ?? throw new InvalidOperationException("The install's manifest couldn't be read.");
            var files = install.SelectFiles(null).ToList();

            operation.SetPhase("Verifying");
            var bad = await Verifier.FindBadFilesAsync(files, install.Directory, status, cancellationToken: cancellationToken);
            if (bad.Count == 0)
                return $"All {files.Count:N0} files are OK.";

            operation.OfferFollowUp($"Repair {bad.Count:N0} files", () => StartRepair(engine, install, bad));
            return $"{bad.Count:N0} of {files.Count:N0} files are missing or damaged.";
        }, engine.AppName));
    }

    private void StartRepair(EngineCardViewModel engine, ExistingInstall install, IReadOnlyList<BadFile> bad)
    {
        StartOperation(new OperationViewModel(this, $"Repairing {engine.Title}", async (operation, status, cancellationToken) =>
        {
            if (install.Sources.Count == 0)
                throw new InstallException("No download location is known for this install.");
            operation.SetPhase("Downloading");
            var plan = InstallPlan.Create(install.Manifest, bad.Select(b => b.File));
            await CreateInstaller().InstallAsync(plan, install.Directory, install.Sources, new Dictionary<string, string>(), status, cancellationToken);
            return $"Repaired {plan.Files.Count:N0} files.";
        }, engine.AppName));
    }

    internal void StartOperation(OperationViewModel operation)
    {
        Operations.Insert(0, operation);
        _ = operation.RunAsync();
    }

    internal async Task OnOperationCompletedAsync(OperationViewModel operation)
    {
        await RefreshCoreAsync(includeOwned: false);
        if (Fab.HasLoaded)
            await Fab.LoadAsync(includeLibrary: false);
    }

    /// <summary>Opens a web page or a folder with the system default handler.</summary>
    internal void OpenURL(string? target)
    {
        if (!string.IsNullOrEmpty(target))
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }

    internal void Launch(EngineCardViewModel engine)
    {
        if (engine.Local is null)
            return;
        string? editor = new[] { "Engine/Binaries/Win64/UnrealEditor.exe", "Engine/Binaries/Win64/UE4Editor.exe" }
            .Select(relative => Path.Combine(engine.Local.Directory, relative))
            .FirstOrDefault(File.Exists);
        if (editor is null)
        {
            Notice = $"No editor executable found in {engine.Local.Directory}.";
            return;
        }
        Process.Start(new ProcessStartInfo(editor) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(editor) });
    }

    internal void OpenFolder(EngineCardViewModel engine)
    {
        if (engine.Local is not null && Directory.Exists(engine.Local.Directory))
            Process.Start(new ProcessStartInfo(engine.Local.Directory) { UseShellExecute = true });
    }
}
