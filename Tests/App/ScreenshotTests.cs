using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UnVault.App.Services;
using UnVault.App.ViewModels;
using UnVault.App.Views;
using UnVault.Core.Epic;
using UnVault.Core.Fab;
using UnVault.Core.Install;
using UnVault.Core.Manifests;
using UnVault.Core.Projects;
using UnVault.Core.Vault;

namespace UnVault.App.Tests;

/// <summary>
/// Renders the main screens with sample data and saves PNGs (to %UNVAULT_SCREENSHOTS% or a temp folder),
/// so the layout can be reviewed without running the app. Also catches XAML/binding crashes.
/// </summary>
public class ScreenshotTests
{
    private const long GB = 1L << 30;

    private static string OutputDirectory =>
        Environment.GetEnvironmentVariable("UNVAULT_SCREENSHOTS") ?? Path.Combine(Path.GetTempPath(), "unvault-screenshots");

    [AvaloniaFact]
    public void Engines_page()
    {
        var viewModel = SampleMainViewModel();
        var window = Show(viewModel);

        // Only what's on disk (plus the stale EGL record), newest major.minor first; the rest is under the + (install engine) button.
        Assert.Equal(["UE_5.8", "UE_5.7", "UE_5.6", "UE_5.5", "UE_4.27"], viewModel.Engines.Select(e => e.AppName));
        Assert.True(viewModel.Engines[0].IsStale);
        Assert.True(viewModel.Engines.Single(e => e.AppName == "UE_5.5").HasUpdate);
        Assert.Equal(["UE_5.8", "UE_5.4", "UE_4.26"], viewModel.InstallableEngines.Select(e => e.AppName));
        Save(window, "engines.png");
    }

    [AvaloniaFact]
    public void Refresh_icon_greys_out_while_refreshing()
    {
        var viewModel = SampleMainViewModel();
        viewModel.IsLoading = true;
        viewModel.LoadingText = "5 installed  ·  checking Epic for versions and updates…";
        var window = Show(viewModel);

        // The icon is drawn (no font glyph) in its button's text color, so it follows the disabled look too.
        var refresh = window.GetVisualDescendants().OfType<Button>().Single(b => ToolTip.GetTip(b) as string == "Refresh");
        var icon = refresh.GetVisualDescendants().OfType<PathIcon>().Single();
        Assert.False(refresh.IsEffectivelyEnabled);
        Assert.Same(icon.FindAncestorOfType<ContentPresenter>()!.Foreground, icon.Foreground);
        Save(window, "header-refreshing.png");
    }

    [AvaloniaFact]
    public void Tabs_stay_clickable_while_the_library_is_updating()
    {
        var viewModel = SampleMainViewModel();
        viewModel.Fab.IsLoading = true; // the first load, still fetching from Fab
        var window = Show(viewModel);
        var library = window.GetVisualDescendants().OfType<Button>().Single(b => b.Command == viewModel.ShowFabCommand);

        viewModel.ShowFabCommand.Execute(null);
        viewModel.ShowEnginesCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewModel.ShowFabCommand.CanExecute(null));
        Assert.True(library.IsEffectivelyEnabled);
        viewModel.ShowFabCommand.Execute(null);
        Assert.True(viewModel.IsFabTab);
    }

    [AvaloniaFact]
    public void Notices_show_on_the_Library_tab_too()
    {
        var viewModel = SampleMainViewModel();
        viewModel.IsFabTab = true;
        viewModel.Notice = "Sign-in failed: the code has expired.";
        var window = Show(viewModel);

        var text = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == viewModel.Notice);
        Assert.True(text.IsEffectivelyVisible);
        Save(window, "fab-library-notice.png");
    }

    [AvaloniaFact]
    public void Dialogs_take_the_keyboard_Esc_cancels_and_Enter_confirms()
    {
        var viewModel = SampleMainViewModel();
        var window = (MainWindow)Show(viewModel);
        var settingsButton = window.GetVisualDescendants().OfType<Button>().Single(b => b.Command == viewModel.OpenSettingsCommand);
        var dialogHost = window.GetVisualDescendants().OfType<ContentControl>().Single(c => c.Name == "DialogHost");
        settingsButton.Focus();

        // Settings: focus moves into the dialog without selecting a field (nothing looks picked after a click)...
        viewModel.Dialog = new SettingsViewModel(viewModel, Core.EGL.EGLLauncherSettings.Empty);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(dialogHost, window.FocusManager?.GetFocusedElement());

        // ...Tab goes to its first field, and round the dialog without reaching the page behind.
        window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
        Assert.IsType<TextBox>(window.FocusManager?.GetFocusedElement());
        for (int i = 0; i < 20; i++)
        {
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            Assert.True(window.FocusManager?.GetFocusedElement() is Visual focused && dialogHost.IsVisualAncestorOf(focused),
                $"Tab {i + 1} left the dialog for {window.FocusManager?.GetFocusedElement()}");
        }

        // Esc cancels, and focus is back on the button that opened it.
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(viewModel.Dialog);
        Assert.Same(settingsButton, window.FocusManager?.GetFocusedElement());

        // Enter confirms, with the focus on the dialog (not on the page's button that opened it, which Enter would press again).
        bool confirmed = false;
        viewModel.Dialog = new ConfirmViewModel(viewModel, "Update Greybox Tools?", "Only files that changed are downloaded.", "Update", () => confirmed = true);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(dialogHost, window.FocusManager?.GetFocusedElement());
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.True(confirmed);
        Assert.Null(viewModel.Dialog);
    }

    [AvaloniaFact]
    public void A_drop_down_closed_from_the_keyboard_keeps_its_focus_ring()
    {
        var viewModel = SampleMainViewModel();
        viewModel.IsFabTab = true;
        SampleFab(viewModel);
        var window = Show(viewModel);
        var kinds = window.GetVisualDescendants().OfType<ComboBox>().Single(c => ReferenceEquals(c.ItemsSource, viewModel.Fab.KindOptions));
        kinds.Focus(NavigationMethod.Tab); // reached with Tab
        Assert.Contains(":focus-visible", kinds.Classes);

        // Opened, moved down one, picked with Enter: still marked as where the keyboard is.
        window.KeyPressQwerty(PhysicalKey.F4, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.True(kinds.IsDropDownOpen);
        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal((false, 1), (kinds.IsDropDownOpen, kinds.SelectedIndex));
        Assert.Contains(":focus-visible", kinds.Classes);
    }

    [AvaloniaFact]
    public void Esc_in_the_project_search_clears_it_first_then_closes_the_dialog()
    {
        var viewModel = SampleMainViewModel();
        viewModel.IsFabTab = true;
        SampleFab(viewModel);
        var dialog = new FabActionViewModel(viewModel.Fab, viewModel.Fab.Items.Single(i => i.Title == "Street Vehicles"), FabActionMode.AddToProject);
        dialog.ShowProjects([new UnrealProject("Ridgeback", @"D:\Projects\Ridgeback\Ridgeback.uproject", "5.7", null)]);
        viewModel.Dialog = dialog;
        var window = Show(viewModel);
        var search = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.PlaceholderText == "Search projects");
        search.Focus(); // clicked into

        dialog.ProjectSearch = "ridge";
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(("", true), (dialog.ProjectSearch, viewModel.HasDialog));

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.False(viewModel.HasDialog);
    }

    [AvaloniaFact]
    public void Icon_only_buttons_have_names_for_screen_readers()
    {
        var viewModel = SampleMainViewModel();
        viewModel.Notice = "Couldn't reach Epic.";
        SampleFab(viewModel);
        var window = Show(viewModel);
        viewModel.IsFabTab = true;
        Dispatcher.UIThread.RunJobs();

        // A button showing no text of its own (a symbol, an icon) needs a name a screen reader can say.
        var unnamed = window.GetVisualDescendants().OfType<Button>()
            .Where(b => b.TemplatedParent is null) // ours, not parts of a control's template (a scroll bar's arrows)
            .Where(b => b.Content is not string { Length: > 0 } text || !text.Any(char.IsLetter))
            .Where(b => b.Content is not TextBlock and not StackPanel and not Panel)
            .Where(b => string.IsNullOrEmpty(AutomationProperties.GetName(b)))
            .Select(b => ToolTip.GetTip(b) as string ?? b.Content?.ToString() ?? b.GetType().Name)
            .ToList();
        Assert.Empty(unnamed);
    }

    [AvaloniaFact]
    public void The_version_opens_About_and_tells_when_a_newer_release_is_out()
    {
        var viewModel = SampleMainViewModel();
        var window = Show(viewModel);
        var version = window.GetVisualDescendants().OfType<Button>().Single(b => b.Command == viewModel.OpenAboutCommand);
        var pill = version.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("chip"));
        Assert.False(pill.IsVisible);

        // GitHub has 0.6.0: the pill shows beside the version, and About says so, with the release to download.
        viewModel.AppUpdate = new Core.NewerRelease("0.6.0", "https://github.com/skxdoom/UnVaultLauncher/releases/tag/v0.6.0");
        viewModel.UpdateCheck = UpdateCheckState.Available;
        Dispatcher.UIThread.RunJobs();
        Assert.True(pill.IsVisible);

        version.Command!.Execute(null);
        var about = Assert.IsType<AboutViewModel>(viewModel.Dialog);
        Assert.Equal("Version 0.6.0 is available.", about.Owner.UpdateStatusText);
        Dispatcher.UIThread.RunJobs();
        Save(window, "about-update.png");

        viewModel.UpdateCheck = UpdateCheckState.UpToDate;
        Assert.Equal("You have the latest version.", about.Owner.UpdateStatusText);
        viewModel.UpdateCheckError = "GitHub didn't answer.";
        viewModel.UpdateCheck = UpdateCheckState.Failed;
        Assert.Equal("Couldn't check for updates: GitHub didn't answer.", about.Owner.UpdateStatusText);
        Dispatcher.UIThread.RunJobs();
        Save(window, "about.png");

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(viewModel.Dialog);
    }

    [AvaloniaFact]
    public void A_session_Epic_ended_shows_as_signed_out_with_the_reason()
    {
        var viewModel = SampleMainViewModel();
        const string Reason = "Epic ended this session, for example after a password change. Sign in again.";

        viewModel.SessionEnded(new NotLoggedInException(Reason));

        Assert.Equal((false, Reason), (viewModel.IsSignedIn, viewModel.Notice));
        Save(Show(viewModel), "session-ended.png");
    }

    [AvaloniaFact]
    public void An_empty_library_page_says_why()
    {
        var viewModel = SampleMainViewModel();
        var fab = viewModel.Fab;
        SampleFab(viewModel);

        fab.KindIndex = 3; // projects
        fab.DownloadedOnly = true;
        fab.UpdatesOnly = true;
        Assert.Equal((true, "Nothing matches. Try another search or filter."), (fab.IsEmpty, fab.EmptyText));

        fab.SetItems(null, [], []);
        Assert.Equal(viewModel.IsSignedIn ? "Your Fab library is empty." : "Your Vault Cache is empty.", fab.EmptyText);
        fab.Error = "Couldn't load your library from Fab: offline.";
        fab.SetItems(null, [], []);
        Assert.Equal("", fab.EmptyText); // the error banner says it
    }

    [AvaloniaFact]
    public void Library_shows_loading_progress_next_to_its_title()
    {
        var viewModel = SampleMainViewModel();
        viewModel.IsFabTab = true;
        SampleFab(viewModel);
        viewModel.Fab.IsLoading = true;
        viewModel.Fab.LoadingText = "Updating… 1,000 read";
        var window = Show(viewModel);

        var text = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "Updating… 1,000 read");
        Assert.True(text.IsEffectivelyVisible);
        Save(window, "fab-library-loading.png");
    }

    [AvaloniaFact]
    public void Downloads_tab()
    {
        var viewModel = SampleMainViewModel();
        var window = Show(viewModel);
        var tabs = window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("tab")).ToList();
        var placesBefore = tabs.Select(t => t.Bounds).ToList();

        // In progress: running, paused and failed, each with what can be done about it.
        static Task<string?> Nothing(OperationViewModel _, Core.Install.InstallStatus __, CancellationToken ___) => Task.FromResult<string?>(null);
        viewModel.Operations.Add(new OperationViewModel(viewModel, "Installing Unreal Engine 5.8.3", Nothing, ["UE_5.8"])
        {
            Phase = "Downloading",
            IsIndeterminate = false,
            Progress = 37,
            ProgressText = "4.1 GB of 10.9 GB  ·  61 204/206 255 files",
            SpeedText = "48.2 MB/s  ·  2m left",
        });
        viewModel.Operations.Add(new OperationViewModel(viewModel, "Downloading Garden Pack for UE 5.7", Nothing, fabItemKey: "fab:garden")
            { State = OperationState.Cancelled, Phase = "Paused", Message = "Resume continues where it left off." });
        viewModel.Operations.Add(new OperationViewModel(viewModel, "Installing Greybox Tools into UE 5.6", Nothing, ["UE_5.6"])
            { State = OperationState.Failed, Phase = "Failed", Message = "Couldn't reach Epic: the request timed out." });
        Assert.DoesNotContain("UE_5.8", viewModel.InstallableEngines.Select(e => e.AppName)); // already being installed
        Assert.Equal(["Resume", "Retry"], viewModel.Operations.Where(o => o.CanRetry).Select(o => o.RetryText));

        // History: newest first; a next step an operation offered is there for this session.
        HistoryEntryViewModel Entry(string title, OperationOutcome outcome, string message, TimeSpan ago) =>
            new(new OperationRecord(title, outcome, message, DateTimeOffset.Now - ago));
        viewModel.History.Add(Entry("Verifying Unreal Engine 5.6.1", OperationOutcome.Completed, "3 of 198 112 files are missing or damaged.", TimeSpan.FromMinutes(5)));
        viewModel.History[0].FollowUpText = "Repair";
        viewModel.History.Add(Entry("Adding Street Vehicles to Hillside", OperationOutcome.Completed, @"Added 214 files to D:\Projects\Hillside.", TimeSpan.FromHours(2)));
        viewModel.History.Add(Entry("Changing components of Unreal Engine 5.7", OperationOutcome.Paused, "Stopped before it finished.", TimeSpan.FromDays(1)));
        viewModel.History.Add(Entry("Removing Edge Smoother from UE 5.5", OperationOutcome.Failed, "Access to the path is denied.", TimeSpan.FromDays(3)));

        viewModel.ShowDownloadsCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        // The tab's dot shows while something runs, and no tab moves or grows for it.
        Assert.True(viewModel.HasRunningOperations);
        var dot = tabs[2].GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().Single();
        Assert.True(dot.IsVisible);
        Assert.Equal(placesBefore, tabs.Select(t => t.Bounds));

        // The ✕ beside Resume: a square as tall as Resume, inside the card.
        var paused = window.GetVisualDescendants().OfType<OperationView>().Single(v => v.DataContext is OperationViewModel { State: OperationState.Cancelled });
        var buttons = paused.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToList();
        var resume = buttons.Single(b => b.Content as string == "Resume");
        var moveToHistory = buttons.Single(b => AutomationProperties.GetName(b) == "Move to History");
        Assert.Equal(resume.Bounds.Height, moveToHistory.Bounds.Height);
        Assert.Equal(moveToHistory.Bounds.Height, moveToHistory.Bounds.Width);
        Save(window, "downloads.png");
    }

    [AvaloniaFact]
    public async Task Install_component_picker()
    {
        var viewModel = SampleMainViewModel();
        // Stand-in for asking Epic: a manifest per version, so switching versions visibly reloads.
        var fetched = new List<string>();
        Task<InstallSource> Fetch(EpicAsset asset, CancellationToken _)
        {
            fetched.Add(asset.AppName);
            var manifest = SampleManifest(asset.AppName, asset.BuildVersion);
            return Task.FromResult(new InstallSource(new DownloadedManifest(manifest, [], [], new Dictionary<string, string>()), "ue", "item"));
        }

        var picker = ComponentPickerViewModel.ForInstall(viewModel, viewModel.InstallableEngines, fetch: Fetch);
        viewModel.Dialog = picker;
        var window = Show(viewModel);
        await picker.LoadAsync();
        WaitFor(() => picker.SummaryText.Length > 0);

        Assert.Equal("UE_5.8", picker.SelectedVersion?.Asset.AppName); // newest preselected
        Assert.EndsWith("UE_5.8", picker.Directory);
        Assert.True(picker.ContentOptions.Single(o => o.Tag == "templates").IsSelected);
        Assert.False(picker.DebugOptions.Single().IsSelected);
        Save(window, "install-picker.png");

        picker.SelectedVersion = picker.VersionOptions.Single(o => o.Asset.AppName == "UE_5.4");
        WaitFor(() => picker.Subtitle.StartsWith("5.4"));
        Assert.EndsWith("UE_5.4", picker.Directory); // folder follows the version
        Assert.Equal(["UE_5.8", "UE_5.4"], fetched);
    }

    [AvaloniaFact]
    public async Task Modify_component_picker()
    {
        var viewModel = SampleMainViewModel();
        var engine = viewModel.Engines.First(e => e.AppName == "UE_5.5");
        var picker = ComponentPickerViewModel.ForModify(viewModel, engine);
        viewModel.Dialog = picker;
        var window = Show(viewModel);

        var manifest = SampleManifest();
        var installed = new HashSet<string> { "editor_symbols", "templates", "starter_content", "engine_source" };
        var install = new ExistingInstall("UE_5.5", @"E:\Epic Games\UE_5.5", manifest, "", installed, [], "Epic Games Launcher install",
            EGLItem: new Core.EGL.EGLItem { AppName = "UE_5.5" });
        await picker.ShowInstallAsync(install);
        WaitFor(() => picker.SummaryText == "No changes");
        Assert.Equal("Apply", picker.ConfirmText);
        picker.DebugOptions.Single().IsSelected = false;

        Assert.NotNull(picker.Note); // EGL-only installs get the "EGL may bring components back" note
        WaitFor(() => picker.SummaryText.StartsWith("Frees"));
        Assert.Equal("Apply", picker.ConfirmText); // the button keeps its name; the summary says what changes

        Save(window, "modify-picker.png");
    }

    [AvaloniaFact]
    public void Settings_dialog()
    {
        string vault = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "unvault-screenshot-vault")).FullName;
        var viewModel = SampleMainViewModel();
        viewModel.Settings.ProjectDirectories = [@"D:\Projects\Unreal", @"E:\Unreal Projects"]; // in memory only; never saved here
        var settings = new SettingsViewModel(viewModel, new Core.EGL.EGLLauncherSettings([vault], @"E:\Epic Games", [@"C:\Users\Me\Documents\Unreal Projects"]));
        viewModel.Dialog = settings;
        var window = Show(viewModel);

        WaitFor(() => settings.VaultSummary.Length > 0);
        Assert.Equal($"Automatic: {vault}", settings.VaultPlaceholder); // EGL's, with nothing set here
        Assert.Equal([true, false], settings.ProjectFolders.Select(f => f.IsDefault));
        Assert.Equal(@"Automatic: C:\Users\Me\Documents\Unreal Projects", settings.ProjectFoldersAutomatic);
        Save(window, "settings.png");
        var scroller = window.GetVisualDescendants().OfType<SettingsView>().Single().GetVisualDescendants().OfType<ScrollViewer>().First();
        scroller.ScrollToEnd();
        Dispatcher.UIThread.RunJobs();
        Save(window, "settings-end.png");
        scroller.ScrollToHome();

        // A folder's radio button makes it the default, in place; removing the default hands it to the first one left,
        // and removing every folder means automatic again.
        var radios = window.GetVisualDescendants().OfType<RadioButton>().ToList();
        Assert.Equal([true, false], radios.Select(r => r.IsChecked == true));
        radios[1].IsChecked = true;
        Assert.Equal([false, true], settings.ProjectFolders.Select(f => f.IsDefault));
        Assert.Equal([@"D:\Projects\Unreal", @"E:\Unreal Projects"], settings.ProjectFolders.Select(f => f.Path));
        Assert.False(radios[0].IsChecked);
        settings.ProjectFolders[1].RemoveCommand.Execute(null);
        Assert.True(settings.ProjectFolders[0].IsDefault);
        settings.ProjectFolders[0].RemoveCommand.Execute(null);
        Assert.True(settings.HasNoProjectFolders);
        Dispatcher.UIThread.RunJobs();
        Save(window, "settings-automatic.png");
    }

    /// <summary>Show in Folder opens a folder only when there is one; otherwise the dialog says why.</summary>
    [AvaloniaFact]
    public async Task Show_in_Folder_says_when_there_is_no_folder_to_show()
    {
        var settings = new SettingsViewModel(SampleMainViewModel(), Core.EGL.EGLLauncherSettings.Empty) { EngineInstallRoot = @"Z:\Fictional\Engines" };
        await settings.ShowEngineRootFolderCommand.ExecuteAsync(null);
        Assert.Equal(@"Z:\Fictional\Engines doesn't exist yet.", settings.Error);

        settings.VaultCacheDirectory = "VaultCache";
        await settings.ShowVaultFolderCommand.ExecuteAsync(null);
        Assert.Equal(@"""VaultCache"" isn't a full folder path (like E:\Epic Games).", settings.Error);
    }

    [Theory]
    [InlineData("UE_5.6,UE_5.7,UE_5.8", "5.6–5.8")]
    [InlineData("UE_5.7,UE_5.8", "5.7, 5.8")] // two in a row read better listed
    [InlineData("UE_4.27,UE_5.3,UE_5.5,UE_5.6,UE_5.7", "4.27, 5.3, 5.5–5.7")]
    [InlineData("UE_4.25,UE_4.26,UE_4.27,UE_5.0,UE_5.1", "4.25–5.1")] // 5.0 came right after 4.27
    [InlineData("UE_4.26,UE_5.0,UE_5.1", "4.26, 5.0, 5.1")]
    public void Engine_versions_are_listed_as_ranges(string appNames, string expected) =>
        Assert.Equal(expected, FabItemViewModel.CompactVersions(appNames.Split(',')));

    [AvaloniaFact]
    public void Fab_library_tab()
    {
        var viewModel = SampleMainViewModel();
        viewModel.IsFabTab = true;
        SampleFab(viewModel);
        var window = Show(viewModel);

        var fab = viewModel.Fab;
        Assert.Equal(8, fab.Items.Count);
        var blockout = fab.Items.Single(i => i.Title == "Greybox Tools");
        Assert.Equal("Installed in 4.27, 5.7", blockout.InstalledText);
        Assert.Equal("In Vault Cache for 5.6, 5.7", blockout.DownloadedText); // a mark beside "Installed in …"; versions on hover
        Assert.Equal("UE 4.27, 5.3, 5.5–5.7", blockout.EngineVersionsText);
        Assert.Equal("Northwind Tools", blockout.SellerText);
        Assert.Equal("Northwind Tools", blockout.BylineText);
        Assert.Equal("Project", fab.Items.Single(i => i.Title == "Lakeside Temple").BylineText); // only in the Vault Cache: no seller known
        Assert.Equal((true, true), (blockout.ShowsInstalledMark, blockout.ShowsDownloadedMark));

        // The tile doesn't name the kind, so the main button's tooltip does (or says what an update would refresh).
        Assert.StartsWith("Newer build on Fab", blockout.PrimaryToolTip);
        Assert.Equal("Install this plugin to Engine", fab.Items.Single(i => i.Title == "Edge Smoother").PrimaryToolTip);
        Assert.Equal("Add this asset pack to Project", fab.Items.Single(i => i.Title == "Hillside Village").PrimaryToolTip);
        Assert.Equal("Create Project", fab.Items.Single(i => i.Title == "Lakeside Temple").PrimaryToolTip);

        // Two products named "Garden Pack": told apart by seller, and busy state follows the product, not the name.
        var plants = fab.Items.Where(i => i.Title == "Garden Pack").ToList();
        Assert.Equal(["Contoso Art", "Fabrikam Studio"], plants.Select(i => i.SellerText).Order());
        var working = new OperationViewModel(fab.Owner, "Downloading Garden Pack", (_, _, _) => Task.FromResult<string?>(null), fabItemKey: plants[0].Key);
        fab.Owner.Operations.Add(working); // listed, not run: running is how an operation starts out
        fab.UpdateBusy();
        Assert.Equal([true, false], plants.Select(i => i.IsBusy));
        // Nothing else starts on a busy item's files: two operations on them would collide.
        Assert.Equal((false, false), (plants[0].DownloadCommand.CanExecute(null), plants[0].RemoveCommand.CanExecute(null)));
        Assert.True(plants[1].DownloadCommand.CanExecute(null));
        fab.Owner.Operations.Remove(working);
        fab.UpdateBusy();
        Assert.Single(fab.Items, i => i.Matches("Fabrikam")); // search covers the seller
        WaitFor(() => fab.Items.Single(i => i.Title.StartsWith("Modular Warehouse")).Thumbnail is not null); // local:// picture loaded
        Save(window, "fab-library.png");

        // Greybox Tools has a newer build on Fab than its 5.7 download and its 5.7 install: its main button becomes Update
        // (its usual action moves to the ⋯ menu), and the Library tab gets a dot.
        Assert.True(blockout.HasUpdate);
        Assert.Equal(("Update", "Install to Engine"), (blockout.PrimaryText, blockout.ActionText));
        var edge = fab.Items.Single(i => i.Title == "Edge Smoother");
        Assert.Equal(("Install to Engine", false), (edge.PrimaryText, edge.HasUpdate));
        Assert.True(edge.ShowsRemove); // plugins only: asset packs never go into an engine
        Assert.False(plants[0].ShowsRemove);
        Assert.Equal(["UE_5.7"], blockout.OutdatedDownloads.Select(v => v.EngineAppName));
        Assert.Equal(["UE_5.7"], blockout.OutdatedInstalls.Select(i => i.EngineAppName));
        Assert.Equal((1, true, "Update Available (1)"), (fab.UpdateCount, viewModel.HasLibraryUpdates, fab.UpdatesFilterText));
        fab.UpdatesOnly = true;
        Assert.Equal(["Greybox Tools"], fab.Items.Select(i => i.Title));
        fab.UpdatesOnly = false;

        fab.KindIndex = 1; // plugins
        Assert.Equal(["Edge Smoother", "Greybox Tools"], fab.Items.Select(i => i.Title));
        fab.KindIndex = 0;
        fab.DownloadedOnly = true;
        Assert.Equal(4, fab.Items.Count);
    }

    [AvaloniaFact]
    public void Fab_library_of_a_thousand_items_creates_tiles_only_for_rows_on_screen()
    {
        var viewModel = SampleMainViewModel();
        viewModel.IsFabTab = true;
        var library = Enumerable.Range(0, 1000).Select(i => new FabLibraryItem
        {
            Title = $"Item {i:D4}", Seller = "Some Seller", AssetID = $"asset{i}", AssetNamespace = "fab", DistributionMethod = "ASSET_PACK",
            ProjectVersions = [new FabProjectVersion { ArtifactID = $"Artifact{i}", EngineVersions = ["UE_5.7"] }],
        }).ToList();
        viewModel.Fab.SetItems(library, [], []);
        var window = Show(viewModel);

        var fab = viewModel.Fab;
        Assert.Equal(4, fab.Columns); // what fits in the 1100 px window
        Assert.Equal(250, fab.Rows.Count);
        Assert.InRange(window.GetVisualDescendants().OfType<FabItemView>().Count(), 4, 40);

        // Search applies once typing pauses.
        fab.SearchText = "Item 09";
        Assert.Equal(1000, fab.Items.Count);
        WaitFor(() => fab.Items.Count == 100); // Item 0900 … Item 0999
        Assert.Equal("Item 0900", fab.Rows[0].Items[0].Title);
        var clear = window.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("clear"));
        Assert.True(clear.IsVisible);
        Save(window, "fab-library-large.png");

        // Esc in the search box (or the × button) clears it, and the full list is back without the typing pause.
        window.GetVisualDescendants().OfType<TextBox>().Single(t => t.PlaceholderText == "Search your library").Focus();
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.Equal("", fab.SearchText);
        Assert.Equal(1000, fab.Items.Count);
        Dispatcher.UIThread.RunJobs();
        Assert.False(clear.IsVisible);
    }

    [AvaloniaFact]
    public void Engines_and_library_share_the_page_width_and_tile_gaps_fill_it()
    {
        var viewModel = SampleMainViewModel();
        SampleFab(viewModel);
        var window = Show(viewModel);

        foreach (int width in new[] { 900, 1180, 1920 })
        {
            window.Width = width;
            viewModel.IsFabTab = false;
            Dispatcher.UIThread.RunJobs();
            var engines = Edges(window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Unreal Engine" && t.FontSize == 22),
                window.GetVisualDescendants().OfType<EngineCardView>().First());
            Save(window, $"engines-{width}.png");

            viewModel.IsFabTab = true;
            Dispatcher.UIThread.RunJobs();
            var tiles = window.GetVisualDescendants().OfType<FabItemView>().Where(t => t.IsEffectivelyVisible).ToList();
            var firstRow = tiles.Where(t => Math.Abs(t.TranslatePoint(default, window)!.Value.Y - tiles[0].TranslatePoint(default, window)!.Value.Y) < 1).ToList();
            var library = Edges(window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Library" && t.FontSize == 22),
                firstRow.MaxBy(t => t.TranslatePoint(default, window)!.Value.X)!);
            Save(window, $"fab-library-{width}.png");

            // Same left and right edges and title height on both tabs; tiles keep their size and the gaps spread a full row to the page's right edge.
            Assert.Equal(engines.Left, library.Left, 1);
            Assert.Equal(engines.Right, library.Right, 1);
            Assert.Equal(engines.Top, library.Top, 1);
            Assert.Equal(viewModel.Fab.Columns, firstRow.Count);
            Assert.All(firstRow, t => Assert.Equal((FabRowView.TileWidth, FabRowView.TileHeight), (t.Bounds.Width, t.Bounds.Height)));
        }

        (double Left, double Right, double Top) Edges(Control heading, Control rightmost) =>
            (heading.TranslatePoint(default, window)!.Value.X, rightmost.TranslatePoint(new Point(rightmost.Bounds.Width, 0), window)!.Value.X,
             heading.TranslatePoint(default, window)!.Value.Y);
    }

    /// <summary>The tile height is fixed, so the fullest tile possible must still fit it: no line or button cut off.</summary>
    [AvaloniaFact]
    public void The_fullest_tile_fits_the_fixed_tile_height()
    {
        var viewModel = SampleMainViewModel();
        viewModel.IsFabTab = true;
        SampleFab(viewModel);
        var blockout = viewModel.Fab.Items.Single(i => i.Title == "Greybox Tools");
        var fullest = new FabItemViewModel(viewModel.Fab, "fullest", "\"Ultimate\" Procedural Rock Generator with Layered Materials and Erosion Masks",
            blockout.Kind, blockout.Library, blockout.Versions, blockout.Installs, null) { IsBusy = true };
        Assert.True(fullest is { IsInstalled: true, IsDownloaded: true });

        var tile = new FabItemView { DataContext = fullest };
        var window = new Window { Content = new StackPanel { Children = { tile } }, Width = 400, Height = 600 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        tile.Measure(new Size(FabRowView.TileWidth, double.PositiveInfinity));

        Assert.InRange(tile.DesiredSize.Height, FabRowView.TileHeight - 2, FabRowView.TileHeight);
        tile.Width = FabRowView.TileWidth;
        Save(window, "fab-tile-fullest.png");
    }

    [AvaloniaFact]
    public void Fab_update_confirmation()
    {
        var viewModel = SampleMainViewModel();
        viewModel.IsFabTab = true;
        SampleFab(viewModel);
        var item = viewModel.Fab.Items.Single(i => i.Title == "Greybox Tools");

        viewModel.Fab.OpenUpdate(item);
        var window = Show(viewModel);

        var confirm = Assert.IsType<ConfirmViewModel>(viewModel.Dialog);
        Assert.Equal(("Update Greybox Tools?", "Only files that changed are downloaded.", "Update"), (confirm.Title, confirm.Message, confirm.ConfirmText));
        Save(window, "fab-update-confirm.png");
    }

    [AvaloniaFact]
    public async Task Installed_plugins_lists_each_plugin_with_its_own_version()
    {
        // A made-up engine folder: a plugin the library knows, one it doesn't, and one without a .uplugin.
        string engineDir = Path.Combine(Path.GetTempPath(), "unvault-screenshot-engine");
        if (Directory.Exists(engineDir))
            Directory.Delete(engineDir, recursive: true);
        void Plugin(string artifact, string? uplugin, bool icon = false)
        {
            string folder = Path.Combine(EnginePlugins.MarketplaceDirectory(engineDir), artifact);
            Directory.CreateDirectory(Path.Combine(folder, @"Binaries\Win64"));
            File.WriteAllBytes(Path.Combine(folder, @"Binaries\Win64\Plugin.dll"), new byte[2 * 1024 * 1024]);
            if (uplugin is not null)
                File.WriteAllText(Path.Combine(folder, "Plugin.uplugin"), uplugin);
            if (icon)
            {
                Directory.CreateDirectory(Path.Combine(folder, "Resources"));
                File.WriteAllBytes(Path.Combine(folder, @"Resources\Icon128.png"), ThumbnailCacheTests.TinyPNG);
            }
        }
        Plugin("Greybox_57", """{ "FriendlyName": "Greybox Runtime", "VersionName": "v2.4" }""", icon: true);
        Plugin("TerrainBrushes9a8b7c6d5eV3", """{ "FriendlyName": "Terrain Brushes", "Version": 12 }""");
        Plugin("Loose4f3e2d1c0bV1", null);
        // Enough more that the list scrolls
        foreach (string name in new[] { "Cloud Layers", "Decal Painter", "Foliage Wind", "Mesh Tools", "Road Splines", "Water Edges" })
            Plugin(name.Replace(" ", "") + "0a1b2c3dV1", $$"""{ "FriendlyName": "{{name}}", "VersionName": "1.0" }""");

        // An install that was stopped part way: its resume journal, and part of its files.
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(PluginInstalls.StateDirectory(engineDir, "Unfinished7e6d5cV2")).FullName, "0123456789ABCDEF.journal"), "");
        Plugin("Unfinished7e6d5cV2", null);

        var viewModel = SampleMainViewModel();
        SampleFab(viewModel);
        var engine = new LocalEngine("UE_5.7", engineDir, "5.7.4-51494982+++UE5+Release-5.7-Windows", 26 * GB, LocalInstallKind.UnVault);
        var dialog = new InstalledPluginsViewModel(viewModel, engine, "Unreal Engine 5.7.4");
        viewModel.Dialog = dialog;
        var window = Show(viewModel);
        await dialog.LoadAsync(launcherInstalled: [], eglItems: []);

        // The library's title where it has one, else the plugin's own name, else its folder; the version as the plugin gives it.
        Assert.Equal(
            [("Greybox Tools", "Version 2.4", true), ("Loose4f3e2d1c0bV1", "Version unknown", false), ("Terrain Brushes", "Version 12", false)],
            dialog.Plugins.Where(p => p.Title is "Greybox Tools" or "Loose4f3e2d1c0bV1" or "Terrain Brushes")
                .Select(p => (p.Title, p.Details.Split("  ·  ")[0], p.HasIcon)));
        Assert.All(dialog.Plugins, p => Assert.True(p.RemoveCommand.CanExecute(null)));
        Assert.StartsWith("Not finished installing", dialog.Plugins.Single(p => p.Title == "Unfinished7e6d5cV2").Details);
        Save(window, "installed-plugins.png");

        // While an operation works on the engine nothing is removed, and once it ends Remove is back, list still open.
        var modify = new OperationViewModel(viewModel, "Changing components of Unreal Engine 5.7", (_, _, _) => Task.FromResult<string?>(null), ["UE_5.7"]);
        viewModel.Operations.Add(modify); // listed as running, as an operation starts out
        viewModel.UpdateBusy();
        Assert.All(dialog.Plugins, p => Assert.False(p.RemoveCommand.CanExecute(null)));
        modify.State = OperationState.Completed;
        viewModel.UpdateBusy();
        Assert.All(dialog.Plugins, p => Assert.True(p.RemoveCommand.CanExecute(null)));

        // Remove asks over the list, which stays in sight; Esc answers the question, not the list's own Close.
        dialog.Plugins.Single(p => p.Title == "Greybox Tools").RemoveCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var confirm = Assert.IsType<ConfirmViewModel>(viewModel.Prompt);
        Assert.Equal("Remove Greybox Tools?", confirm.Title);
        Assert.Same(dialog, viewModel.Dialog);
        Save(window, "installed-plugins-remove.png");
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(viewModel.Prompt);
        Assert.Same(dialog, viewModel.Dialog);
    }

    [AvaloniaFact]
    public async Task Fab_remove_plugin_dialog()
    {
        var viewModel = SampleMainViewModel();
        viewModel.IsFabTab = true;
        SampleFab(viewModel);
        var item = viewModel.Fab.Items.Single(i => i.Title == "Greybox Tools");
        var dialog = new FabActionViewModel(viewModel.Fab, item, FabActionMode.RemovePlugin);
        viewModel.Dialog = dialog;
        var window = Show(viewModel);
        await dialog.LoadAsync();

        // Every engine that has it, newest first, whoever installed it.
        Assert.Equal(["Unreal Engine 5.7", "Unreal Engine 4.27"], dialog.Targets.Select(t => t.Title));
        Assert.All(dialog.Targets, t => Assert.Null(t.Badge)); // who installed it is in the note
        Assert.Equal("Unreal Engine 5.7", dialog.SelectedTarget?.Title);
        Assert.StartsWith("Deletes the plugin installed by the Epic Games Launcher", dialog.Note);
        Assert.True(dialog.ConfirmCommand.CanExecute(null));
        Save(window, "fab-remove-plugin.png");

        dialog.SelectedTarget = dialog.Targets[1];
        Assert.StartsWith("Deletes the plugin copied in by hand", dialog.Note);
    }

    [AvaloniaFact]
    public async Task Fab_install_plugin_dialog()
    {
        var viewModel = SampleMainViewModel();
        viewModel.IsFabTab = true;
        SampleFab(viewModel);
        var item = viewModel.Fab.Items.Single(i => i.Title == "Greybox Tools");
        var dialog = new FabActionViewModel(viewModel.Fab, item, FabActionMode.InstallPlugin);
        viewModel.Dialog = dialog;
        var window = Show(viewModel);
        await dialog.LoadAsync();

        // 5.7 already has it; 5.6 can use the downloaded copy; 4.27 has a version; 5.5 and 5.8 depend on what's listed.
        Assert.Equal("Installed", dialog.Targets.Single(t => t.Title.EndsWith("5.7")).Badge);
        Assert.Equal("Unreal Engine 5.6", dialog.SelectedTarget?.Title);
        Assert.StartsWith("Already in your Vault Cache", dialog.Note);
        Save(window, "fab-install-plugin.png");

        // One that isn't downloaded yet just downloads into the engine: nothing to note.
        var notDownloaded = dialog.Targets.Single(t => t.Title.EndsWith("5.5"));
        Assert.Equal((true, false), (notDownloaded.IsAvailable, notDownloaded.Version?.IsDownloaded));
        dialog.SelectedTarget = notDownloaded;
        Assert.Null(dialog.Note);

        // An operation works on 5.5 meanwhile: it can't be installed into, the pick stays, and it's back once that ends.
        var modify = new OperationViewModel(viewModel, "Changing components of Unreal Engine 5.5", (_, _, _) => Task.FromResult<string?>(null), ["UE_5.5"]);
        viewModel.Operations.Add(modify); // listed as running, as an operation starts out
        viewModel.UpdateBusy();
        Assert.Equal(("Busy with another operation", false), (notDownloaded.Badge, notDownloaded.IsAvailable));
        Assert.Same(notDownloaded, dialog.SelectedTarget);
        Assert.False(dialog.ConfirmCommand.CanExecute(null));
        modify.State = OperationState.Completed;
        viewModel.UpdateBusy();
        Assert.Equal((null, true), (notDownloaded.Badge, notDownloaded.IsAvailable));
        Assert.True(dialog.ConfirmCommand.CanExecute(null));

        // Opened while every engine it fits is busy, nothing is picked; the first to be free is.
        var update = new OperationViewModel(viewModel, "Updating Greybox Tools", (_, _, _) => Task.FromResult<string?>(null),
            [.. dialog.Targets.Where(t => t.IsUsable).Select(t => t.EngineAppName!)]);
        viewModel.Operations.Add(update);
        var waiting = new FabActionViewModel(viewModel.Fab, item, FabActionMode.InstallPlugin);
        viewModel.Dialog = waiting;
        await waiting.LoadAsync();
        Assert.Null(waiting.SelectedTarget);
        Assert.Null(waiting.EmptyText); // they fit; they're only busy, as their badges say
        update.State = OperationState.Completed;
        viewModel.UpdateBusy();
        Assert.Equal("Unreal Engine 5.6", waiting.SelectedTarget?.Title);
        Assert.True(waiting.ConfirmCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void Add_to_project_lists_projects_by_name_with_a_search()
    {
        var viewModel = SampleMainViewModel();
        viewModel.IsFabTab = true;
        SampleFab(viewModel);
        var item = viewModel.Fab.Items.Single(i => i.Title == "Street Vehicles"); // for UE 5.3–5.7
        var dialog = new FabActionViewModel(viewModel.Fab, item, FabActionMode.AddToProject);
        viewModel.Dialog = dialog;
        var window = Show(viewModel);

        static UnrealProject Project(string name, string engine, int daysAgo) =>
            new(name, $@"D:\Projects\{name}\{name}.uproject", engine, daysAgo < 0 ? null : DateTime.UtcNow.AddDays(-daysAgo));
        const string Html5Engine = "{5C3B2A10-0000-4000-8000-000000000001}", MissingEngine = "{5C3B2A10-0000-4000-8000-000000000002}";
        dialog.ShowProjects(
        [
            Project("Ridgeback", "5.8", daysAgo: 2),
            Project("Project10", "5.6", daysAgo: 30),
            Project("arena_test", "5.7", daysAgo: -1),
            Project("Project2", "5.5", daysAgo: 1),
            Project("OldTown", "4.26", daysAgo: 0),
            Project("Html5Port", Html5Engine, daysAgo: 3),
            Project("Prototype", MissingEngine, daysAgo: 4),
        ],
        new Dictionary<string, CustomEngine>(StringComparer.OrdinalIgnoreCase)
        {
            [Html5Engine.Trim('{', '}')] = new(Html5Engine, @"E:\Engines\Html5", "5.4"),
        });

        // By name (case aside, numbers in order), and nothing chosen until the user picks a project.
        Assert.Equal(["arena_test", "Html5Port", "OldTown", "Project2", "Project10", "Prototype", "Ridgeback"], dialog.Targets.Select(t => t.Title));
        Assert.Null(dialog.SelectedTarget);
        Assert.False(dialog.ConfirmCommand.CanExecute(null));
        FabTargetViewModel Row(string name) => dialog.Targets.Single(t => t.Title == name);
        Assert.Equal(@"UE 5.5  ·  D:\Projects\Project2", Row("Project2").Subtitle); // no "opened" date
        Assert.Null(Row("Project2").Badge);

        // A custom engine by its version, like any other; one that isn't on this PC can't be chosen.
        Assert.Equal((@"Custom Engine 5.4  ·  D:\Projects\Html5Port", null, true), (Row("Html5Port").Subtitle, Row("Html5Port").Badge, Row("Html5Port").IsAvailable));
        Assert.Equal((@"Custom Engine  ·  D:\Projects\Prototype", "Engine version not installed", false), (Row("Prototype").Subtitle, Row("Prototype").Badge, Row("Prototype").IsAvailable));
        Assert.NotNull(Row("Prototype").BadgeTip);

        // No 5.8 version: the 5.7 one goes into a 5.8 project. Nothing for 4.26, as every version is newer.
        Assert.Equal(("Last available from UE 5.7", "This item has no 5.8 version, so its 5.7 version can be added.", true),
            (Row("Ridgeback").Badge, Row("Ridgeback").BadgeTip, Row("Ridgeback").IsAvailable));
        Assert.Equal(("Unsupported engine version", "This item is made for a newer 5.3 engine version.", false),
            (Row("OldTown").Badge, Row("OldTown").BadgeTip, Row("OldTown").IsAvailable));
        Save(window, "fab-add-to-project.png");
        dialog.SelectedTarget = Row("Project2");
        Assert.True(dialog.ConfirmCommand.CanExecute(null));

        // The search narrows by name. It keeps the user's pick while it's listed, and never picks one itself.
        dialog.ProjectSearch = "project";
        Assert.Equal(["Project2", "Project10"], dialog.Targets.Select(t => t.Title));
        Assert.Equal("Project2", dialog.SelectedTarget?.Title);
        dialog.ProjectSearch = "ridge";
        Assert.Equal("Ridgeback", Assert.Single(dialog.Targets).Title);
        Assert.Null(dialog.SelectedTarget);
        dialog.ProjectSearch = "nothing like it";
        Assert.Empty(dialog.Targets);
        Assert.Equal("No projects match the search.", dialog.EmptyText);
        dialog.ClearProjectSearchCommand.Execute(null);
        Assert.Equal(7, dialog.Targets.Count);
    }

    /// <summary>A small Fab library with vault copies on disk, so "downloaded" states are real.</summary>
    private static void SampleFab(MainViewModel viewModel)
    {
        string vaultRoot = Path.Combine(Path.GetTempPath(), "unvault-screenshot-fab-vault");
        // Fab's current build of everything is "build-2"; Greybox Tools' 5.7 copies below are still on "build-1".
        VaultEntry Vault(string artifact, string title, string version, string categories, string build = "build-2")
        {
            var entry = new VaultEntry { ArtifactID = artifact, Title = title, Version = version, Build = build, Categories = categories, Directory = Path.Combine(vaultRoot, artifact) };
            Directory.CreateDirectory(entry.DataDirectory);
            File.WriteAllBytes(entry.ManifestPath, [0]);
            return entry;
        }
        FabProjectVersion Version(string artifact, params string[] engines) =>
            new() { ArtifactID = artifact, EngineVersions = [.. engines], BuildVersions = [new FabBuildVersion { BuildVersion = "build-2", Platform = "Windows" }] };
        FabLibraryItem Item(string title, string seller, string method, params FabProjectVersion[] versions) =>
            new() { Title = title, Seller = seller, AssetID = title + seller, AssetNamespace = "fab", DistributionMethod = method, ProjectVersions = [.. versions], URL = "https://www.fab.com/listings/x" };

        var library = new List<FabLibraryItem>
        {
            Item("Edge Smoother", "Example Studio", "engine-plugin", Version("EdgeSmoother_53", "UE_5.3"), Version("EdgeSmoother_54", "UE_5.4"), Version("EdgeSmoother_57", "UE_5.7"), Version("EdgeSmoother_58", "UE_5.8")),
            Item("Greybox Tools", "Northwind Tools", "engine-plugin", Version("Greybox_427", "UE_4.27"), Version("Greybox_53", "UE_5.3"),
                Version("Greybox_55", "UE_5.5"), Version("Greybox_56", "UE_5.6"), Version("Greybox_57", "UE_5.7")),
            Item("Street Vehicles", "Example Studio", "asset-pack", Version("StreetVehicles", "UE_5.3", "UE_5.4", "UE_5.5", "UE_5.6", "UE_5.7")),
            Item("Hillside Village", "Sample Scans", "asset-pack", Version("Village_55", "UE_5.5"), Version("Village_57", "UE_5.7")),
            // Different products that share a name; the seller tells them apart.
            Item("Garden Pack", "Fabrikam Studio", "asset-pack", Version("GardenV2", "UE_5.1", "UE_5.2", "UE_5.3", "UE_5.4", "UE_5.5", "UE_5.6", "UE_5.7", "UE_5.8")),
            Item("Garden Pack", "Contoso Art", "asset-pack", Version("GardenPack", "UE_4.10", "UE_4.27", "UE_5.0", "UE_5.4")),
        };
        var vault = new List<VaultEntry>
        {
            Vault("Greybox_56", "Greybox Tools", "5.6.0-43254731", "|Engine Tools|plugins|"),
            Vault("Greybox_57", "Greybox Tools", "5.7.0-48201490", "|Engine Tools|plugins|", build: "build-1"),
            Vault("StreetVehicles", "Street Vehicles", "5.7.0-1", "|Vehicles & Transportation|assets|"),
            WithThumbnail(Vault("Warehous3c4d5e6f7a8bV5", "Modular Warehouse V. 2", "5.8.0-46051459", "|Industrial|projects|"), valid: true),
            WithThumbnail(Vault("Temple_4.27", "Lakeside Temple", "4.27.0-1", "|Unreal Engine|projects|"), valid: false),
        };

        // EGL writes local://… for pictures inside the download; one real, one missing (the scroll crash).
        VaultEntry WithThumbnail(VaultEntry entry, bool valid)
        {
            string picture = Path.Combine(entry.DataDirectory, "Thumb.png");
            if (valid)
                File.WriteAllBytes(picture, ThumbnailCacheTests.TinyPNG);
            else
                File.Delete(picture);
            entry.StoredThumbnail = "local://" + picture;
            return entry;
        }

        // One EGL install, and one folder nothing has a record of (as found on a real machine).
        var installs = new List<FabInstall>
        {
            new("UE_5.7", @"E:\Epic Games\UE_5.7", "Greybox_57", PluginSource.EGL, @"E:\Epic Games\UE_5.7\Engine\Plugins\Marketplace\Greybox_57", CanRemove: true, BuildVersion: "build-1"),
            new("UE_4.27", @"E:\Epic Games\UE_4.27", "Greybox_427", PluginSource.Unlisted, @"E:\Epic Games\UE_4.27\Engine\Plugins\Marketplace\Greybox_427", CanRemove: true),
        };

        viewModel.Fab.SetItems(library, vault, installs);
    }

    private static MainViewModel SampleMainViewModel()
    {
        // A history file nobody else writes: what other tests leave in the shared one isn't the screenshots' to show.
        string history = Path.Combine(Path.GetTempPath(), $"unvault-history-{Guid.NewGuid():N}.json");
        var viewModel = new MainViewModel(new AppServices(), history) { IsSignedIn = true, DisplayName = "Test Account" };
        viewModel.Settings.EngineInstallRoot = @"E:\Epic Games"; // set, so nothing asks this PC's Epic Games Launcher
        viewModel.SetEngines(
        [
            new LocalEngine("UE_5.7", @"E:\Epic Games\UE_5.7", "5.7.4-51494982+++UE5+Release-5.7-Windows", 26 * GB + 300 * (GB / 1024), LocalInstallKind.UnVault),
            new LocalEngine("UE_5.6", @"E:\Epic Games\UE_5.6", "5.6.1-44394996+++UE5+Release-5.6-Windows", 25 * GB + 400 * (GB / 1024), LocalInstallKind.AdoptedFromEGL),
            new LocalEngine("UE_5.5", @"E:\Epic Games\UE_5.5", "5.5.3-39772772+++UE5+Release-5.5-Windows", 72 * GB + 200 * (GB / 1024), LocalInstallKind.EGL),
            new LocalEngine("UE_4.27", @"E:\Epic Games\UE_4.27", "4.27.2-18319896+++UE4+Release-4.27-Windows", 54 * GB, LocalInstallKind.EGL),
            new LocalEngine("UE_5.8", @"E:\Epic Games\UE_5.8", "5.8.2-56702186+++UE5+Release-5.8-Windows", 28 * GB, LocalInstallKind.StaleEGLRecord),
        ],
        [
            Asset("UE_5.8", "5.8.3-58210709+++UE5+Release-5.8-Windows"),
            Asset("UE_5.7", "5.7.4-51494982+++UE5+Release-5.7-Windows"),
            Asset("UE_5.6", "5.6.1-44394996+++UE5+Release-5.6-Windows"),
            Asset("UE_5.5", "5.5.4-40574608+++UE5+Release-5.5-Windows"),
            Asset("UE_5.4", "5.4.4-35576357+++UE5+Release-5.4-Windows"),
            Asset("UE_4.27", "4.27.2-18319896+++UE4+Release-4.27-Windows"),
            Asset("UE_4.26", "4.26.2-15973114+++UE4+Release-4.26-Windows"),
        ]);
        return viewModel;
    }

    private static EpicAsset Asset(string app, string build) =>
        new() { AppName = app, BuildVersion = build, Namespace = "ue", CatalogItemID = "test" };

    /// <summary>A manifest with one big file per component, sized like the real UE 5.8 build.</summary>
    private static Manifest SampleManifest(string appName = "UE_5.8", string buildVersion = "5.8.3-58210709+++UE5+Release-5.8-Windows")
    {
        (string Tag, double DiskGB, double DownloadGB)[] parts =
        [
            ("", 28.0, 10.9), ("starter_content", 0.6, 0.4), ("templates", 1.0, 0.65), ("engine_source", 0.55, 0.11),
            ("metahuman_content", 5.8, 5.0), ("platform_Android", 9.5, 2.0), ("platform_IOS", 7.5, 2.0), ("platform_Linux", 18.5, 5.5),
            ("platform_WinARM64", 6.9, 1.5), ("platform_TVOS", 3.3, 0.9), ("platform_VOS", 0.11, 0.05), ("editor_symbols", 53.8, 13.8),
        ];

        var chunks = new List<ChunkInfo>();
        var files = new List<FileManifest>();
        uint id = 1;
        foreach (var (tag, disk, download) in parts)
        {
            var guid = new EpicGUID(id++, 0, 0, 0);
            chunks.Add(new ChunkInfo { GUID = guid, FileSize = (long)(download * GB) });
            files.Add(new FileManifest
            {
                Filename = $"Engine/{(tag.Length == 0 ? "Core" : tag)}.bin",
                InstallTags = tag.Length == 0 ? [] : [tag],
                ChunkParts = [new ChunkPart(guid, 0, 1)],
                FileSize = (long)(disk * GB),
            });
        }

        return new Manifest
        {
            Version = 21,
            Meta = new ManifestMeta { AppName = appName, BuildVersion = buildVersion, FeatureLevel = 21 },
            Chunks = chunks,
            Files = files,
            CustomFields = new Dictionary<string, string>(),
        };
    }

    private static Window Show(MainViewModel viewModel)
    {
        var window = new MainWindow { DataContext = viewModel, Width = 1100, Height = 760 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    /// <summary>Pumps the UI thread until background recalculations have landed.</summary>
    private static void WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }
        Assert.True(condition(), "Timed out waiting for the view model to update.");
    }

    private static void Save(Window window, string name)
    {
        Directory.CreateDirectory(OutputDirectory);
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered.");
        frame.Save(Path.Combine(OutputDirectory, name), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
