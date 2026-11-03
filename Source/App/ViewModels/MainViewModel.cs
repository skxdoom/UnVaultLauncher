using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnVault.App.Services;
using UnVault.Core;
using UnVault.Core.EGL;
using UnVault.Core.Epic;
using UnVault.Core.Fab;
using UnVault.Core.Install;
using UnVault.Core.Util;

namespace UnVault.App.ViewModels;

public enum UpdateCheckState { NotChecked, Checking, UpToDate, Available, Failed }

public enum AppTab { Engines, Projects, Library, Downloads }

public partial class MainViewModel : ViewModelBase
{
    private IReadOnlyList<LocalEngine> _local = [];
    private IReadOnlyList<EpicAsset> _owned = [];

    /// <param name="historyPath">Where the Downloads history is kept; tests give each a file of its own.</param>
    public MainViewModel(AppServices services, string? historyPath = null)
    {
        Services = services;
        _historyPath = historyPath ?? OperationHistory.FilePath;
        Fab = new FabLibraryViewModel(this);
        Projects = new ProjectsViewModel(this);
        Fab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FabLibraryViewModel.UpdateCount))
            {
                OnPropertyChanged(nameof(HasLibraryUpdates));
                OnPropertyChanged(nameof(LibraryUpdatesText));
            }
        };
        Operations.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasOperations));
            OperationsChanged();
        };
        History = [.. OperationHistory.Load(_historyPath).Select(record => new HistoryEntryViewModel(record))];
        History.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasHistory));
            GroupHistory();
        };
        GroupHistory();
        UpdateAccount();
    }

    public AppServices Services { get; }
    private readonly string _historyPath;
    public IUserInteraction? Interaction { get; set; }

    /// <summary>Shared with the CLI: %LOCALAPPDATA%\UnVaultLauncher\settings.json.</summary>
    public AppSettings Settings { get; } = AppSettings.Load();

    /// <summary>Closing the window keeps the app in the system tray, as last saved in Settings.</summary>
    public bool CloseToTray => Settings.CloseToTray;

    /// <summary>Settings were saved: what follows them right away catches up.</summary>
    internal void SettingsSaved()
    {
        OnPropertyChanged(nameof(CloseToTray));
        ApplyDownloadSpeedLimit(); // downloads already running slow down or speed up too
        PruneManifestCache(); // the limit may be lower now
        if (Projects.HasLoaded)
            _ = Projects.LoadAsync(); // the project folders may have changed
    }

    private void ApplyDownloadSpeedLimit() => Services.Bandwidth.BytesPerSecond = Settings.DownloadSpeedLimit;

    /// <summary>Applies the limit from Settings and makes room, in the background: it's only a cache.</summary>
    private void PruneManifestCache()
    {
        long limit = Settings.ManifestCacheLimit;
        Services.API.ManifestCacheLimit = limit;
        _ = Task.Run(() => ManifestCache.Prune(limit));
    }

    public FabLibraryViewModel Fab { get; }

    public ProjectsViewModel Projects { get; }

    /// <summary>Underlines the Library tab in yellow.</summary>
    public bool HasLibraryUpdates => Fab.UpdateCount > 0;

    public string? LibraryUpdatesText =>
        Fab.UpdateCount == 0 ? null : Localized.Plural(nameof(Strings.ItemsHaveUpdates_Other), Fab.UpdateCount);

    /// <summary>Engine installs found by the last refresh (also used by the Fab tab to pick plugin targets).</summary>
    public IReadOnlyList<LocalEngine> LocalEngines => _local;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnginesTab), nameof(IsProjectsTab), nameof(IsFabTab), nameof(IsDownloadsTab))]
    public partial AppTab Tab { get; set; }

    public bool IsEnginesTab => Tab == AppTab.Engines;

    public bool IsProjectsTab => Tab == AppTab.Projects;

    /// <summary>The Library tab; turning it off shows Engines.</summary>
    public bool IsFabTab
    {
        get => Tab == AppTab.Library;
        set => Tab = value ? AppTab.Library : AppTab.Engines;
    }

    public bool IsDownloadsTab => Tab == AppTab.Downloads;

    /// <summary>The engine cards: every engine on this PC, newest first.</summary>
    public ObservableCollection<EngineCardViewModel> Engines { get; } = [];

    /// <summary>Running operations, and paused or failed ones waiting to be resumed or put in the history.</summary>
    public ObservableCollection<OperationViewModel> Operations { get; } = [];
    public bool HasOperations => Operations.Count > 0;

    // Tab underlines: yellow while something there wants a look; the Downloads tab's moves while anything runs.

    /// <summary>Something is downloading, installing or otherwise working.</summary>
    public bool HasRunningOperations => Operations.Any(o => o.IsRunning);

    public bool HasEngineUpdates => Engines.Any(e => e.HasUpdate);

    public string? EngineUpdatesText => Engines.Count(e => e.HasUpdate) is > 0 and var count
        ? Localized.Plural(nameof(Strings.EnginesHaveUpdates_Other), count) : null;

    /// <summary>One failed and waits to be retried or put in the history.</summary>
    public bool HasFailedOperations => Operations.Any(o => o.IsFailed);

    public string? FailedOperationsText => Operations.Count(o => o.IsFailed) is > 0 and var count
        ? Localized.Plural(nameof(Strings.OperationsFailed_Other), count) : null;

    private void OperationsChanged()
    {
        OnPropertyChanged(nameof(HasRunningOperations));
        OnPropertyChanged(nameof(HasFailedOperations));
        OnPropertyChanged(nameof(FailedOperationsText));
    }

    /// <summary>Finished operations, newest first: this session's and earlier ones'.</summary>
    public ObservableCollection<HistoryEntryViewModel> History { get; }
    public bool HasHistory => History.Count > 0;

    /// <summary>The history by day, as the Downloads tab shows it.</summary>
    [ObservableProperty] public partial IReadOnlyList<HistoryDayViewModel> HistoryDays { get; set; } = [];

    /// <summary>Also when the tab is shown: Today may have become Yesterday since.</summary>
    private void GroupHistory() => HistoryDays = HistoryDayViewModel.Group(History, DateTime.Today);

    /// <summary>Done, or paused or failed and given up on: the operation leaves the list for the history.</summary>
    internal void MoveToHistory(OperationViewModel operation, OperationOutcome outcome)
    {
        Operations.Remove(operation);
        // Done: what came of it; failed: what went wrong. Paused says it all.
        string? message = outcome == OperationOutcome.Paused ? null : operation.Message;
        var record = new OperationRecord(operation.Subject, outcome, message, DateTimeOffset.Now)
            { Kind = operation.Action.Kind, Target = operation.Action.Target };
        History.Insert(0, new HistoryEntryViewModel(record)
        {
            FollowUpText = operation.FollowUp?.Text,
            FollowUp = operation.FollowUp?.Action,
        });
        while (History.Count > OperationHistory.MaxEntries)
            History.RemoveAt(History.Count - 1);
        OperationHistory.Save(History.Select(h => h.Record), _historyPath);
    }

    /// <summary>
    /// The app is closing: whatever is still running, paused or failed goes into the history, as nothing resumes it after a
    /// restart (installing it again continues it). The oldest first, so the newest ends up on top.
    /// </summary>
    internal void RecordUnfinished()
    {
        foreach (var operation in Operations.Reverse().ToList())
            MoveToHistory(operation, operation.IsFailed ? OperationOutcome.Failed : OperationOutcome.Paused);
    }

    [RelayCommand]
    private void ClearHistory()
    {
        History.Clear();
        OperationHistory.Save([], _historyPath);
    }

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
        Prompt = null; // a question belongs to the dialog it was asked over
        // These read build manifests (an engine's is large); give that memory back once they're closed.
        if (oldValue is ComponentPickerViewModel or FabActionViewModel)
            MemoryRelief.Release();
    }

    /// <summary>A question asked over the open dialog (Remove? in Installed Plugins); the dialog stays in sight behind it, dimmed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPrompt))]
    public partial ViewModelBase? Prompt { get; set; }

    public bool HasPrompt => Prompt is not null;

    /// <summary>Closes a dialog, or a question asked over one.</summary>
    internal void Close(ViewModelBase dialog)
    {
        if (Prompt == dialog)
            Prompt = null;
        else if (Dialog == dialog)
            Dialog = null;
    }

    /// <summary>Shown next to the name in the header, e.g. "0.5.0"; it opens About.</summary>
    public string VersionText { get; } = ProductInfo.Version;

    /// <summary>Whether GitHub has a newer release of the app (see <see cref="CheckForUpdatesAsync"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAppUpdate), nameof(UpdateStatusText))]
    public partial UpdateCheckState UpdateCheck { get; set; }

    [ObservableProperty] public partial NewerRelease? AppUpdate { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateStatusText))]
    public partial string? UpdateCheckError { get; set; }

    /// <summary>Shows the "Update available" pill beside the version.</summary>
    public bool HasAppUpdate => UpdateCheck == UpdateCheckState.Available;

    public string UpdateStatusText => UpdateCheck switch
    {
        UpdateCheckState.Checking => Strings.UpdateChecking,
        UpdateCheckState.UpToDate => Strings.UpdateUpToDate,
        UpdateCheckState.Available => Localized.Format(Strings.UpdateAvailableVersion, AppUpdate?.Version),
        UpdateCheckState.Failed => Localized.Format(Strings.UpdateCheckFailed, UpdateCheckError),
        _ => Settings.CheckForUpdates ? Strings.UpdateNotChecked : Strings.UpdateCheckOff,
    };

    /// <summary>Asks GitHub for the latest release; at startup (unless turned off in Settings) and from About.</summary>
    internal async Task CheckForUpdatesAsync()
    {
        if (UpdateCheck == UpdateCheckState.Checking)
            return;
        UpdateCheck = UpdateCheckState.Checking;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            AppUpdate = await Services.Updates.FindNewerAsync(ProductInfo.Version, timeout.Token);
            UpdateCheck = AppUpdate is null ? UpdateCheckState.UpToDate : UpdateCheckState.Available;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            UpdateCheckError = ex is OperationCanceledException ? Strings.GitHubNoAnswer : ex.Message;
            UpdateCheck = UpdateCheckState.Failed;
        }
    }

    [RelayCommand]
    private void OpenAbout() => Dialog = new AboutViewModel(this);

    /// <summary>
    /// Engine versions the account owns but hasn't installed (stale EGL records count as not installed), leaving out
    /// any an install in the downloads list is for, running or waiting to be retried: a second install would write into
    /// the same folder. Dismissing a paused one offers the version again.
    /// </summary>
    public IReadOnlyList<EpicAsset> InstallableEngines =>
        _owned.Where(o => !_local.Any(l => l.Exists && Same(l.AppName, o.AppName))
                          && !Operations.Any(op => op.State != OperationState.Completed && op.WorksOn(o.AppName)))
            .ToList();

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The signed-in account; a slow fetch compares it before and after, as the user may sign out meanwhile.</summary>
    internal string? CurrentAccountID => Services.Account.IsLoggedIn ? Services.Account.Session?.AccountID : null;

    /// <summary>First load: engines, then (signed in) the library in the background, so the Library tab shows pending updates before it's opened.</summary>
    public async Task StartAsync()
    {
        if (Settings.CheckForUpdates)
            _ = CheckForUpdatesAsync(); // alongside; it never throws
        ApplyDownloadSpeedLimit();
        PruneManifestCache();
        _ = Task.Run(PluginBuilder.ClearBuilds); // what a build cut short by closing the app left
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
        else if (IsProjectsTab)
            await Projects.LoadAsync();
    }

    [RelayCommand]
    private void ShowEngines() => Tab = AppTab.Engines;

    /// <summary>The first visit finds the projects; later ones show them as they were (the refresh button looks again).</summary>
    [RelayCommand]
    private void ShowProjects()
    {
        Tab = AppTab.Projects;
        if (!Projects.HasLoaded && !Projects.IsLoading)
            _ = Projects.LoadAsync();
    }

    [RelayCommand]
    private void ShowDownloads()
    {
        GroupHistory();
        Tab = AppTab.Downloads;
    }

    /// <summary>
    /// Switches at once; the first visit starts loading the library, which shows its own progress. Not awaited: an async
    /// command can't run again until it finishes, so the tab button stayed disabled for the whole first load.
    /// </summary>
    [RelayCommand]
    private void ShowFab()
    {
        Tab = AppTab.Library;
        if (!Fab.HasLoaded && !Fab.IsLoading)
            _ = Fab.LoadAsync(includeLibrary: true);
    }

    private async Task RefreshCoreAsync(bool includeOwned)
    {
        IsLoading = true;
        LoadingText = Strings.LookingForEngines;
        try
        {
            _local = await Task.Run(EngineLibrary.ScanLocal);
            string? accountID = CurrentAccountID;
            if (accountID is null)
                _owned = [];
            else if (_owned.Count == 0 && await Task.Run(() => OwnedEnginesCache.Load(accountID)) is { } saved && CurrentAccountID == accountID)
                _owned = saved.Engines; // Epic's list from last time, so update badges and installable versions are there at once

            // The installed engines show right away; the local scan is quick, Epic's list is not.
            SetEngines(_local, _owned);

            if (includeOwned && accountID is not null)
            {
                int installed = _local.Where(l => l.Exists).Select(l => l.AppName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                LoadingText = Localized.Plural(nameof(Strings.InstalledCheckingEpic_Other), installed);
                try
                {
                    var owned = (await Services.API.GetAssetsAsync()).Where(a => a.IsEngine).ToList();
                    // Signed out or into another account meanwhile: this list isn't theirs, and the change refreshes on its own.
                    if (CurrentAccountID != accountID)
                        return;
                    _owned = owned;
                    Notice = null;
                    await SaveOwnedEnginesAsync(accountID);
                }
                catch (NotLoggedInException ex)
                {
                    _owned = [];
                    SessionEnded(ex);
                }
                catch (Exception ex) when (ex is EpicAPIException or HttpRequestException or TaskCanceledException)
                {
                    if (CurrentAccountID == accountID)
                        Notice = Localized.Format(Strings.CouldNotReachEpic, ex.Message);
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

        var engines = localList.Select(l => l.AppName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(app => new EngineCardViewModel(this, app,
                localList.FirstOrDefault(l => Same(l.AppName, app) && l.Exists) ?? localList.First(l => Same(l.AppName, app)),
                ownedList.FirstOrDefault(o => Same(o.AppName, app)))
            {
                IsBusy = IsEngineBusy(app),
                IsUpdating = IsEngineUpdating(app),
            })
            .OrderByDescending(e => e.Version.Major)
            .ThenByDescending(e => e.Version.Minor)
            .ThenBy(e => e.AppName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Engines.Clear();
        foreach (var engine in engines)
            Engines.Add(engine);
        HasNoEngines = Engines.Count == 0;
        OnPropertyChanged(nameof(HasEngineUpdates));
        OnPropertyChanged(nameof(EngineUpdatesText));
        Projects.EnginesChanged();
    }

    /// <summary>An operation is working on the engine, so nothing else may start on it.</summary>
    internal bool IsEngineBusy(string appName) => Operations.Any(o => o.IsRunning && o.WorksOn(appName));

    /// <summary>The engine's own files, or a plugin's in it, are being replaced by a newer build's.</summary>
    internal bool IsEngineUpdating(string appName) => Operations.Any(o => o.IsRunning && o.WorksOn(appName) && o.Action.Kind == OperationKind.Update);

    /// <summary>The running operation that works on the same engine or Library item, if any.</summary>
    internal OperationViewModel? RunningOverlap(OperationViewModel operation) =>
        Operations.FirstOrDefault(o => o != operation && o.IsRunning && o.Overlaps(operation));

    /// <summary>
    /// An operation ended: the ones waiting for it start, in the order they began to wait. One that still can't (one just
    /// started works on the same files) says what it waits for now.
    /// </summary>
    internal void StartWaiting()
    {
        foreach (var operation in Operations.Where(o => o.IsWaiting).OrderBy(o => o.Turn).ToList())
            _ = operation.RunAsync();
    }

    /// <summary>
    /// An operation started or ended. Busy is worked out from all the running ones, not switched per operation: one ending
    /// mustn't free an engine another one is still working on.
    /// </summary>
    internal void UpdateBusy()
    {
        foreach (var engine in Engines)
            (engine.IsBusy, engine.IsUpdating) = (IsEngineBusy(engine.AppName), IsEngineUpdating(engine.AppName));
        Fab.UpdateBusy();
        (Dialog as IShowsBusy)?.UpdateBusy();
        OperationsChanged();
    }

    /// <summary>An operation is working on the Library item.</summary>
    internal bool IsItemBusy(string itemKey) => Operations.Any(o => o.IsRunning && o.FabItemKey == itemKey);

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
            Notice = Localized.Format(Strings.SignInFailed, ex.Message);
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
        catch (Exception ex) when (ex is EpicAPIException or HttpRequestException or OperationCanceledException)
        {
            // The local session is deleted regardless; Epic will expire the tokens on its own. No answer in time is one
            // of these too: what follows must still run, or the sign-in browser would sign the next person in as this one.
        }
        FabLibraryCache.Delete();
        OwnedEnginesCache.Delete();
        bool forgotten = Interaction is null || await Interaction.ForgetSignInAsync();

        UpdateAccount();
        if (!forgotten)
            Notice = Strings.SignInBrowserNotCleared;
        await RefreshAfterAccountChangeAsync();
    }

    private async Task RefreshAfterAccountChangeAsync()
    {
        await RefreshCoreAsync(includeOwned: true);
        if (Fab.HasLoaded || IsSignedIn)
            await Fab.LoadAsync(includeLibrary: true);
    }

    /// <summary>
    /// Epic ended the session (a password change, signed out everywhere) or it ran out: the app shows itself signed out,
    /// with the reason, and the library drops what was the account's.
    /// </summary>
    internal void SessionEnded(NotLoggedInException ex)
    {
        Notice = ex.Message;
        UpdateAccount();
        if (Fab.HasLoaded)
            _ = Fab.LoadAsync(includeLibrary: false);
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
        if (!IsSignedIn)
            return; // the session ended meanwhile; the page asks to sign in
        if (_owned.Count == 0)
        {
            // Epic couldn't be reached (the notice says why): the picker would claim every version is installed.
            Notice ??= Strings.NoEngineVersionsListed;
            return;
        }

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

    internal Installer CreateInstaller() => new(Services.HTTP) { MaxParallelDownloads = Settings.ParallelDownloads, Bandwidth = Services.Bandwidth };

    internal void StartInstall(string appName, string title, InstallSource source, IReadOnlySet<string> tags, string directory)
    {
        bool installed = false; // paused while the prerequisites install, it resumes with them
        StartOperation(new OperationViewModel(this, title, new OperationAction(OperationKind.InstallEngine), async (operation, status, cancellationToken) =>
        {
            if (!installed)
            {
                var plan = InstallWorkflow.PlanInstall(source, tags);
                operation.SetPhase(Strings.PhaseDownloading);
                await InstallWorkflow.InstallAsync(source, plan, tags, null, directory, CreateInstaller(), status, register: true, cancellationToken);
                installed = true;
            }

            // Then, as the Epic Games Launcher does, what the build names to run once installed (the Visual C++ runtime).
            // Not getting it doesn't undo the install: the line says so, with a button to try again.
            if (Prerequisite.Find(source.Manifest, directory) is not { } prerequisite)
                return directory;
            operation.SetPhase(Strings.PhaseInstallingPrerequisites);
            var (outcome, _) = await prerequisite.RunAsync(cancellationToken);
            if (outcome is PrerequisiteOutcome.Declined or PrerequisiteOutcome.Failed)
                operation.OfferFollowUp(Strings.InstallPrerequisites, () => StartPrerequisites(appName, title, () => prerequisite));
            return outcome switch
            {
                PrerequisiteOutcome.Installed => directory,
                PrerequisiteOutcome.RestartNeeded => $"{directory}  ·  {Strings.PrerequisitesNeedRestart}",
                _ => $"{directory}  ·  {Strings.PrerequisitesNotInstalled}",
            };
        }, [appName]));
    }

    /// <summary>Runs the prerequisite installer an engine's build names, for one installed without it.</summary>
    internal void StartPrerequisites(EngineCardViewModel engine)
    {
        if (engine.Local is not { } local)
            return;
        StartPrerequisites(engine.AppName, engine.Title, () =>
            local.Load() is { } install && Prerequisite.Find(install.Manifest, install.Directory) is { } found
                ? found
                : throw new InstallException(Strings.NoPrerequisites));
    }

    /// <param name="find">Finds the installer (on the work's thread, as it may read the manifest).</param>
    private void StartPrerequisites(string appName, string title, Func<Prerequisite> find)
    {
        StartOperation(new OperationViewModel(this, title, new OperationAction(OperationKind.InstallPrerequisites), async (operation, _, cancellationToken) =>
        {
            var prerequisite = find();
            operation.SetPhase(Strings.PhaseInstallingPrerequisites);
            var (outcome, exitCode) = await prerequisite.RunAsync(cancellationToken);
            return outcome switch
            {
                PrerequisiteOutcome.Installed => null,
                PrerequisiteOutcome.RestartNeeded => Strings.PrerequisitesNeedRestart,
                PrerequisiteOutcome.Declined => throw new InstallException(Strings.PrerequisitesDeclined),
                _ => throw new InstallException(Localized.Format(Strings.PrerequisitesFailed, exitCode)),
            };
        }, [appName]));
    }

    internal void StartModify(EngineCardViewModel engine, ModifyPlan plan)
    {
        StartOperation(new OperationViewModel(this, engine.Title, new OperationAction(OperationKind.ModifyEngine), async (operation, status, cancellationToken) =>
        {
            operation.SetPhase(plan.ToAdd is null ? Strings.PhaseRemovingFiles : Strings.PhaseDownloading);
            var cleaned = await InstallWorkflow.ApplyModifyAsync(plan, CreateInstaller(), status, cancellationToken);
            string freed = ByteSize.Format(cleaned.BytesFreed);
            return (cleaned.FilesDeleted > 0, plan.ToAdd) switch
            {
                (true, { } added) => Localized.Plural(nameof(Strings.ModifyDetailFreedAdded_Other), added.Files.Count, freed),
                (false, { } added) => Localized.Plural(nameof(Strings.ModifyDetailAdded_Other), added.Files.Count),
                (true, null) => Localized.Format(Strings.ModifyDetailFreed, freed),
                _ => null,
            };
        }, [engine.AppName]));
    }

    /// <summary>
    /// Brings an engine up to the build Epic has now, as the Epic Games Launcher does: with the same components, only the
    /// files that are new or changed are downloaded, and those the new build dropped are deleted.
    /// </summary>
    internal void StartEngineUpdate(EngineCardViewModel engine)
    {
        if (engine is not { Local: { } local, Owned: { } owned })
            return;
        StartOperation(new OperationViewModel(this, engine.Title, new OperationAction(OperationKind.Update), async (operation, status, cancellationToken) =>
        {
            operation.SetPhase(Strings.PhaseReadingManifest);
            var install = await Task.Run(local.Load, cancellationToken) ?? throw new InstallException(Strings.ManifestUnreadable);
            var source = await InstallWorkflow.FetchAsync(Services.API, owned, cancellationToken);
            var plan = await Task.Run(() => InstallWorkflow.PlanUpdate(install, source), cancellationToken);

            operation.SetPhase(Strings.PhaseDownloading);
            await InstallWorkflow.ApplyUpdateAsync(plan, CreateInstaller(), status, cancellationToken);
            return Localized.Plural(nameof(Strings.EngineUpdateDetail_Other), plan.Write.Files.Count,
                EngineLibrary.ShortBuildVersion(source.Manifest.Meta.BuildVersion));
        }, [engine.AppName]));
    }

    internal void StartVerify(EngineCardViewModel engine)
    {
        if (engine.Local is null)
            return;
        var local = engine.Local;
        StartOperation(new OperationViewModel(this, engine.Title, new OperationAction(OperationKind.VerifyEngine), async (operation, status, cancellationToken) =>
        {
            operation.SetPhase(Strings.PhaseReadingManifest);
            var install = local.Load() ?? throw new InvalidOperationException(Strings.ManifestUnreadable);
            var files = install.SelectFiles(null).ToList();

            operation.SetPhase(Strings.PhaseVerifying);
            var bad = await Verifier.FindBadFilesAsync(files, install.Directory, status, cancellationToken: cancellationToken);
            if (bad.Count == 0)
                return Localized.Plural(nameof(Strings.VerifyDetailAllOK_Other), files.Count);

            operation.OfferFollowUp(Strings.Repair, () => StartRepair(engine, install, bad)); // the line beside it says how many
            return Localized.Plural(nameof(Strings.VerifyDetailBad_Other), bad.Count, files.Count);
        }, [engine.AppName]));
    }

    private void StartRepair(EngineCardViewModel engine, ExistingInstall install, IReadOnlyList<BadFile> bad)
    {
        StartOperation(new OperationViewModel(this, engine.Title, new OperationAction(OperationKind.RepairEngine), async (operation, status, cancellationToken) =>
        {
            if (install.Sources.Count == 0)
                throw new InstallException(Strings.NoDownloadLocation);
            operation.SetPhase(Strings.PhaseDownloading);
            var plan = InstallPlan.Create(install.Manifest, bad.Select(b => b.File));
            await CreateInstaller().InstallAsync(plan, install.Directory, install.Sources, new Dictionary<string, string>(), status, cancellationToken);
            return Localized.Plural(nameof(Strings.RepairDetail_Other), plan.Files.Count);
        }, [engine.AppName]));
    }

    internal void StartOperation(OperationViewModel operation) => StartOperations([operation]);

    /// <summary>On top of the list in the order given, and started in that order: those on the same files wait their turn.</summary>
    internal void StartOperations(IReadOnlyList<OperationViewModel> operations)
    {
        // Each one listed as it starts: one not started yet counts as running, and an earlier one would wait for it.
        for (int i = 0; i < operations.Count; i++)
        {
            Operations.Insert(i, operations[i]);
            _ = operations[i].RunAsync();
        }
    }

    internal async Task OnOperationCompletedAsync(OperationViewModel operation)
    {
        await RefreshCoreAsync(includeOwned: false);
        if (Fab.HasLoaded)
            await Fab.LoadAsync(includeLibrary: false);
    }

    /// <summary>Opens a page of Fab, Epic or GitHub in the browser; anything else is refused (see <see cref="SafeOpen"/>).</summary>
    internal void OpenWebPage(string? url)
    {
        if (!string.IsNullOrEmpty(url) && !SafeOpen.WebPage(url))
            Notice = Localized.Format(Strings.CouldNotOpen, url);
    }

    /// <summary>Shows a folder, when it's there; never opens a file.</summary>
    internal void OpenFolder(string? folder) => SafeOpen.Folder(folder);

    /// <summary>An engine's editor: UnrealEditor.exe, or UE4Editor.exe in UE 4; null when neither is there.</summary>
    internal static string? EditorIn(string engineDirectory) =>
        new[] { @"Engine\Binaries\Win64\UnrealEditor.exe", @"Engine\Binaries\Win64\UE4Editor.exe" }
            .Select(relative => Path.Combine(engineDirectory, relative))
            .FirstOrDefault(File.Exists);

    internal void Launch(EngineCardViewModel engine)
    {
        if (engine.Local is null)
            return;
        if (EditorIn(engine.Local.Directory) is not { } editor)
        {
            Notice = Localized.Format(Strings.NoEditorFound, engine.Local.Directory);
            return;
        }
        Process.Start(new ProcessStartInfo(editor) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(editor) });
    }

    internal void OpenFolder(EngineCardViewModel engine) => OpenFolder(engine.Local?.Directory);

    internal async Task OpenInstalledPluginsAsync(EngineCardViewModel engine)
    {
        if (engine.Local is null)
            return;
        var dialog = new InstalledPluginsViewModel(this, engine.Local, engine.Title);
        Dialog = dialog;
        await dialog.LoadAsync();
    }
}
