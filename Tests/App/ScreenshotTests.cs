using Avalonia;
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
    public void Engines_page_with_downloads()
    {
        var viewModel = SampleMainViewModel();
        viewModel.Operations.Add(new OperationViewModel(viewModel, "Installing Unreal Engine 5.8.3", (_, _, _) => Task.FromResult<string?>(null), "UE_5.8")
        {
            Phase = "Downloading",
            IsIndeterminate = false,
            Progress = 37,
            ProgressText = "4.1 GB of 10.9 GB  ·  61 204/206 255 files",
            SpeedText = "48.2 MB/s  ·  2m left",
        });
        Assert.DoesNotContain("UE_5.8", viewModel.InstallableEngines.Select(e => e.AppName)); // already being installed
        viewModel.Operations.Add(new OperationViewModel(viewModel, "Verifying Unreal Engine 5.6.1", (_, _, _) => Task.FromResult<string?>(null))
        {
            State = OperationState.Completed,
            Phase = "Done",
            Message = "3 of 198 112 files are missing or damaged.",
            FollowUpText = "Repair",
        });

        Save(Show(viewModel), "engines-downloads.png");
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
        Assert.Equal("Installed: 4.27, 5.7", blockout.InstalledText);
        Assert.Equal("Downloaded: 5.6, 5.7", blockout.DownloadedText);
        Assert.Equal("Downloaded", blockout.DownloadedPlateText); // beside "Installed: …"; versions on hover
        Assert.Equal("Available for 4.27, 5.3, 5.5–5.7", blockout.EngineVersionsText);
        Assert.Equal("Northwind Tools", blockout.SellerText);

        // Two products named "Garden Pack": told apart by seller, and busy state follows the product, not the name.
        var plants = fab.Items.Where(i => i.Title == "Garden Pack").ToList();
        Assert.Equal(["Contoso Art", "Fabrikam Studio"], plants.Select(i => i.SellerText).Order());
        fab.SetBusy(plants[0].Key, true);
        Assert.Equal([true, false], plants.Select(i => i.IsBusy));
        // Nothing else starts on a busy item's files: two operations on them would collide.
        Assert.Equal((false, false), (plants[0].DownloadCommand.CanExecute(null), plants[0].RemoveCommand.CanExecute(null)));
        Assert.True(plants[1].DownloadCommand.CanExecute(null));
        fab.SetBusy(plants[0].Key, false);
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

            // Same left and right edges on both tabs; tiles keep their size and the gaps spread a full row to the page's right edge.
            Assert.Equal(engines.Left, library.Left, 1);
            Assert.Equal(engines.Right, library.Right, 1);
            Assert.Equal(viewModel.Fab.Columns, firstRow.Count);
            Assert.All(firstRow, t => Assert.Equal((FabRowView.TileWidth, FabRowView.TileHeight), (t.Bounds.Width, t.Bounds.Height)));
        }

        (double Left, double Right) Edges(Control heading, Control rightmost) =>
            (heading.TranslatePoint(default, window)!.Value.X, rightmost.TranslatePoint(new Point(rightmost.Bounds.Width, 0), window)!.Value.X);
    }

    /// <summary>The tile height is fixed, so the fullest tile possible must still fit it: no plate or button cut off.</summary>
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
            entry.Thumbnail = "local://" + picture;
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
        var viewModel = new MainViewModel(new AppServices()) { IsSignedIn = true, DisplayName = "Test Account" };
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
