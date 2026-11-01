using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UnVault.App.Services;
using UnVault.App.ViewModels;
using UnVault.App.Views;
using UnVault.Core.Install;
using UnVault.Core.Projects;

namespace UnVault.App.Tests;

/// <summary>The Projects tab: each project with the editor's picture of it, opened in the engine it's for.</summary>
public sealed class ProjectsTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"unvault-projects-{Guid.NewGuid():N}")).FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>A project on disk; with a picture when given its colour, as the editor leaves one in Saved.</summary>
    private UnrealProject Project(string name, string association, DateTime? lastOpened = null, Color? picture = null)
    {
        string folder = Directory.CreateDirectory(Path.Combine(_dir, "Projects", name)).FullName;
        string file = Path.Combine(folder, name + ".uproject");
        File.WriteAllText(file, $$"""{ "FileVersion": 3, "EngineAssociation": "{{association}}" }""");
        if (picture is { } color)
            Picture(Path.Combine(folder, "Saved", "AutoScreenshot.png"), color);
        return new UnrealProject(name, file, association, lastOpened);
    }

    private static void Picture(string file, Color color)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using var bitmap = new RenderTargetBitmap(new PixelSize(512, 512)); // square, as the editor takes them
        using (var context = bitmap.CreateDrawingContext())
        {
            var sky = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(color, 0), new GradientStop(Colors.Black, 1) },
            };
            context.FillRectangle(sky, new Rect(0, 0, 512, 512));
            context.FillRectangle(new SolidColorBrush(Color.FromRgb(40, 46, 38)), new Rect(0, 360, 512, 152)); // ground
        }
        bitmap.Save(file, new PngBitmapEncoderOptions());
    }

    /// <summary>An engine folder with its editor, which is never started here.</summary>
    private string Engine(string name)
    {
        string engine = Path.Combine(_dir, name);
        string editor = Path.Combine(engine, "Engine", "Binaries", "Win64", "UnrealEditor.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(editor)!);
        File.WriteAllText(editor, "");
        return engine;
    }

    private static Dictionary<string, CustomEngine> SourceBuilds(params CustomEngine[] engines) =>
        engines.ToDictionary(e => e.ID.Trim('{', '}'), StringComparer.OrdinalIgnoreCase);

    [AvaloniaFact]
    public void A_project_opens_in_the_engine_it_is_for_and_the_card_says_when_that_is_missing()
    {
        var viewModel = ScreenshotTests.SampleMainViewModel();
        string ue56 = Engine("UE_5.6");
        viewModel.SetEngines([new LocalEngine("UE_5.6", ue56, "5.6.1-44394996+++UE5+Release-5.6-Windows", 1, LocalInstallKind.UnVault)], []);
        var started = new List<ProcessStartInfo>();
        var projects = viewModel.Projects;
        projects.StartProcess = started.Add;
        string sourceBuild = Engine("Source");
        projects.SetProjects(
            [
                Project("Hillside", "5.6", DateTime.UtcNow),
                Project("Ridgeback", "5.3"),
                Project("Quarry", "{6F1C2A3B-0D4E-4F5A-8B6C-7D8E9F0A1B2C}"),
                Project("Loose", ""),
            ],
            SourceBuilds(new CustomEngine("{6F1C2A3B-0D4E-4F5A-8B6C-7D8E9F0A1B2C}", sourceBuild, "5.4")));
        ProjectCardViewModel Card(string name) => projects.Items.Single(c => c.Name == name);

        // What each is for, and whether it can open: only an engine that's missing gets a chip. By name, not when opened.
        Assert.Equal(["Hillside", "Loose", "Quarry", "Ridgeback"], projects.Items.Select(c => c.Name));
        Assert.Equal(["UE 5.6  ·  Last opened: Today", "No engine set", "Source build 5.4", "UE 5.3"], projects.Items.Select(c => c.DetailsText));
        Assert.Equal([true, false, true, true], projects.Items.Select(c => c.CanOpen));
        Assert.Equal(["Open", "Open", "Open", "Choose Engine…"], projects.Items.Select(c => c.OpenText));
        Assert.Equal([false, false, false, true], projects.Items.Select(c => c.IsEngineMissing));
        Assert.Equal("Open anyway; UE 5.3 isn't installed on this PC.", Card("Ridgeback").OpenTip);
        Assert.True(Card("Ridgeback").OpenCommand.CanExecute(null));

        // Open starts an installed engine's editor with the project, its path passed as one argument. When the named
        // engine is missing, Windows opens the project so UnrealVersionSelector can choose an available editor.
        Card("Hillside").OpenCommand.Execute(null);
        Card("Quarry").OpenCommand.Execute(null);
        Card("Ridgeback").OpenCommand.Execute(null);
        Assert.Equal(
            [
                (Path.Combine(ue56, @"Engine\Binaries\Win64\UnrealEditor.exe"), Card("Hillside").Project.ProjectFile),
                (Path.Combine(sourceBuild, @"Engine\Binaries\Win64\UnrealEditor.exe"), Card("Quarry").Project.ProjectFile),
            ],
            started.Take(2).Select(s => (s.FileName, s.ArgumentList.Single())));
        Assert.Equal((Card("Ridgeback").Project.ProjectFile, true, 0),
            (started[2].FileName, started[2].UseShellExecute, started[2].ArgumentList.Count));

        // UE 5.3 installed since: Ridgeback can open now.
        var changed = new List<string?>();
        Card("Ridgeback").PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        viewModel.SetEngines([.. viewModel.LocalEngines, new LocalEngine("UE_5.3", Engine("UE_5.3"), "5.3.2-29314046+++UE5+Release-5.3-Windows", 1, LocalInstallKind.UnVault)], []);
        Assert.Equal((true, "Open"), (Card("Ridgeback").CanOpen, Card("Ridgeback").OpenText));
        Assert.Contains(nameof(ProjectCardViewModel.OpenText), changed);
    }

    [AvaloniaFact]
    public void The_search_finds_projects_by_name_engine_or_folder()
    {
        var projects = ScreenshotTests.SampleMainViewModel().Projects;
        projects.SetProjects([Project("Hillside", "5.6"), Project("Ridgeback", "5.7"), Project("OldTown", "4.27")], SourceBuilds());
        Assert.Equal("3 Projects", projects.Summary);

        projects.SearchText = "ridge";
        Assert.Equal(["Ridgeback"], projects.Items.Select(c => c.Name));
        Assert.Equal("1 of 3 Projects", projects.Summary);

        projects.SearchText = "UE 4";
        Assert.Equal(["OldTown"], projects.Items.Select(c => c.Name));

        projects.SearchText = "nothing like it";
        Assert.Equal((true, "No project matches your search."), (projects.IsEmpty, projects.EmptyText));

        projects.ClearSearchCommand.Execute(null);
        Assert.Equal(3, projects.Items.Count);
    }

    [AvaloniaFact]
    public async Task Pictures_come_from_the_projects_Saved_folder_and_a_missing_one_leaves_the_initial()
    {
        var projects = ScreenshotTests.SampleMainViewModel().Projects;
        projects.SetProjects([Project("Hillside", "5.6", picture: Colors.SteelBlue), Project("Ridgeback", "5.7")], SourceBuilds());

        await projects.LoadScreenshotsAsync(projects.Items);

        Assert.Equal(ProjectsViewModel.ScreenshotPixels, projects.Items[0].Screenshot?.PixelSize.Width);
        Assert.Null(projects.Items[1].Screenshot);
        Assert.Equal("R", projects.Items[1].Initial);
    }

    [AvaloniaFact]
    public async Task A_projects_size_is_counted_in_the_background_and_shown_after_its_folder()
    {
        var projects = ScreenshotTests.SampleMainViewModel().Projects;
        var hillside = Project("Hillside", "5.6");
        string map = Path.Combine(hillside.Directory, "Content", "Map.umap");
        Directory.CreateDirectory(Path.GetDirectoryName(map)!);
        File.WriteAllBytes(map, new byte[3 * 1024 * 1024]);
        projects.SetProjects([hillside], SourceBuilds());
        Assert.Null(projects.Items[0].SizeText); // until it's counted

        await projects.CountSizesAsync(projects.Items);
        Assert.Equal("3.0 MB", projects.Items[0].SizeText);

        // Found again: the size counted before shows at once, while it's counted again.
        projects.SetProjects([hillside], SourceBuilds());
        Assert.Equal("3.0 MB", projects.Items[0].SizeText);
    }

    /// <summary>A projects page whose categories are kept in this test's own file.</summary>
    private (MainViewModel Owner, ProjectsViewModel Projects) WithCategories(params UnrealProject[] found)
    {
        var owner = ScreenshotTests.SampleMainViewModel();
        owner.Projects.CategoriesPath = Path.Combine(_dir, "project-categories.json");
        owner.Projects.SetProjects(found, SourceBuilds());
        return (owner, owner.Projects);
    }

    /// <summary>Names a new category in the dialog New Category opens, for the project whose menu it was chosen from.</summary>
    private static void NewCategory(MainViewModel owner, ProjectCardViewModel card, string name)
    {
        card.NewCategoryCommand.Execute(null);
        var dialog = Assert.IsType<CategoryNameViewModel>(owner.Dialog);
        dialog.Name = name;
        dialog.ConfirmCommand.Execute(null);
        Assert.Null(owner.Dialog);
    }

    /// <summary>Each bundle shown: its heading (none without categories) and its projects.</summary>
    private static List<(string Title, string Projects)> Shown(ProjectsViewModel projects) =>
        [.. projects.Groups.Select(g => (g.HasHeading ? g.Title : "", string.Join(", ", g.Items.Select(c => c.Name))))];

    [AvaloniaFact]
    public void Without_categories_the_projects_are_one_list_by_name()
    {
        var (_, projects) = WithCategories(Project("Ridgeback", "5.7", DateTime.UtcNow), Project("hillside", "5.6"), Project("Lakeside", "5.5"));

        Assert.Equal([("", "hillside, Lakeside, Ridgeback")], Shown(projects)); // no heading, and the case doesn't count

        // Uncategorized folded before the last category went: with no heading to unfold it by, the list still shows.
        projects.Categories.UncategorizedFolded = true;
        projects.SearchText = " ";
        Assert.True(projects.Groups.Single().IsOpen);
    }

    /// <summary>
    /// Categories bundle projects, as Steam's collections: by name, each under its heading, a project in as many as it's put
    /// in; then the projects in none, under Uncategorized. They're kept for next time.
    /// </summary>
    [AvaloniaFact]
    public void Categories_bundle_projects_and_the_rest_come_after_them()
    {
        var (owner, projects) = WithCategories(Project("Hillside", "5.6"), Project("Lakeside", "5.5"), Project("OldTown", "4.26"), Project("Ridgeback", "5.7"));
        ProjectCardViewModel Card(string name) => projects.Items.Single(c => c.Name == name);

        NewCategory(owner, Card("Ridgeback"), "Plugins");
        NewCategory(owner, Card("Hillside"), "Demos");
        Card("Hillside").ToggleCategoryCommand.Execute(projects.Categories.Find("Plugins"));
        Assert.Equal(
            [("Demos", "Hillside"), ("Plugins", "Hillside, Ridgeback"), ("Uncategorized", "Lakeside, OldTown")],
            Shown(projects));
        Assert.Equal("4 Projects", projects.Summary); // each once
        var saved = ProjectCategories.Load(projects.CategoriesPath);
        Assert.Equal(["Plugins", "Demos"], saved.Categories.Select(c => c.Name));
        Assert.Equal([Card("Hillside").Project.ProjectFile], saved.Find("demos")!.Projects);

        // A name that's taken, in any case, can't be used again.
        Card("Lakeside").NewCategoryCommand.Execute(null);
        var taken = Assert.IsType<CategoryNameViewModel>(owner.Dialog);
        taken.Name = " demos ";
        Assert.Equal("There's already a category called demos.", taken.Error);
        Assert.False(taken.ConfirmCommand.CanExecute(null));
        taken.Name = "";
        Assert.False(taken.ConfirmCommand.CanExecute(null));
        taken.CancelCommand.Execute(null);

        // Taken out of Demos, Hillside leaves it empty; the heading stays, saying how to fill it.
        Card("Hillside").ToggleCategoryCommand.Execute(projects.Categories.Find("Demos"));
        var demos = projects.Groups.Single(g => g.Title == "Demos");
        Assert.Equal((0, true), (demos.Items.Count, demos.ShowsEmptyHint));

        // Searching shows only the bundles with a match.
        projects.SearchText = "ridge";
        Assert.Equal([("Plugins", "Ridgeback")], Shown(projects));
    }

    [AvaloniaFact]
    public void A_category_folds_and_can_be_renamed_or_deleted()
    {
        var (owner, projects) = WithCategories(Project("Hillside", "5.6"), Project("Ridgeback", "5.7"));
        NewCategory(owner, projects.Items[0], "Demos");
        ProjectGroupViewModel Group(string title) => projects.Groups.Single(g => g.Title == title);

        // Folded (and the Uncategorized ones too), kept so; a search still shows what matches inside. The page isn't built
        // anew for it: the same group folds, so its projects can slide shut.
        var demos = Group("Demos");
        demos.ToggleFoldCommand.Execute(null);
        Group("Uncategorized").ToggleFoldCommand.Execute(null);
        Assert.Same(demos, Group("Demos"));
        Assert.Equal([false, false], projects.Groups.Select(g => g.IsOpen));
        var saved = ProjectCategories.Load(projects.CategoriesPath);
        Assert.Equal((true, true), (saved.Categories.Single().Folded, saved.UncategorizedFolded));
        projects.SearchText = "hill";
        Assert.True(Group("Demos").IsOpen);
        projects.SearchText = "";

        // Renamed: its own name in other letters is fine.
        Group("Demos").RenameCommand.Execute(null);
        var rename = Assert.IsType<CategoryNameViewModel>(owner.Dialog);
        Assert.Equal("Demos", rename.Name);
        rename.Name = "DEMOS";
        Assert.Null(rename.Error);
        rename.Name = "Showcase";
        rename.ConfirmCommand.Execute(null);
        Assert.Equal(["Showcase", "Uncategorized"], projects.Groups.Select(g => g.Title));

        // Deleted, once confirmed: the bundle goes, its projects stay; with no categories left, no headings either.
        Group("Showcase").DeleteCommand.Execute(null);
        var confirm = Assert.IsType<ConfirmViewModel>(owner.Dialog);
        Assert.Equal("Delete the Showcase category?", confirm.Title);
        confirm.ConfirmCommand.Execute(null);
        Assert.Equal([("", "Hillside, Ridgeback")], Shown(projects));
        Assert.Empty(ProjectCategories.Load(projects.CategoriesPath).Categories);
    }

    /// <summary>
    /// A project's ⋯: its categories (each ticked when it's in it, a click putting it in or out), a new one, then Show in
    /// Folder; more of what can be done with a project will go there too.
    /// </summary>
    [AvaloniaFact]
    public void A_projects_More_menu_has_its_categories_and_Show_in_Folder()
    {
        var (owner, projects) = WithCategories(Project("Hillside", "5.6"));
        NewCategory(owner, projects.Items[0], "My_Plugins");
        NewCategory(owner, projects.Items[0], "Demos");
        projects.Items[0].ToggleCategoryCommand.Execute(projects.Categories.Find("Demos"));
        var view = new ProjectCardView { DataContext = projects.Items[0] };
        var window = new Window { Content = view };
        window.Show();

        var more = view.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "⋯");
        var menu = Assert.IsType<MenuFlyout>(more.Flyout);
        view.OnMoreOpening(menu, EventArgs.Empty);
        Assert.Equal(["Categories", "Show in Folder"], menu.Items.OfType<MenuItem>().Select(i => i.Header as string));
        var categories = menu.Items.OfType<MenuItem>().First().Items;
        Assert.Equal(
            [("Demos", false), ("My__Plugins", true), ("New Category…", false)], // _ doubled, or it'd mark an access key and vanish
            categories.OfType<MenuItem>().Select(i => (i.Header as string, i.IsChecked)));
        Assert.Single(categories.OfType<Separator>());
        window.Close();
    }

    [AvaloniaFact]
    public void Categories_page()
    {
        var viewModel = ScreenshotTests.SampleMainViewModel();
        var projects = viewModel.Projects;
        projects.CategoriesPath = Path.Combine(_dir, "project-categories.json");
        DateTime now = DateTime.UtcNow;
        var found = new[]
        {
            Project("Hillside", "5.6", now, Colors.SteelBlue),
            Project("Ridgeback", "5.7", now.AddDays(-1), Colors.IndianRed),
            Project("Lakeside", "5.5", now.AddDays(-3), Colors.CadetBlue),
            Project("OldTown", "4.26", now.AddDays(-40)),
            Project("Greybox", "5.7", null, Colors.DarkKhaki),
        };
        new ProjectCategories
        {
            Categories =
            [
                new ProjectCategory { Name = "Demos", Projects = [found[0].ProjectFile, found[2].ProjectFile] },
                new ProjectCategory { Name = "Plugin Tests", Projects = [found[1].ProjectFile, found[4].ProjectFile] },
                new ProjectCategory { Name = "Archive", Folded = true, Projects = [found[3].ProjectFile] },
            ],
        }.Save(projects.CategoriesPath);
        projects.FindProjects = () => found;
        projects.FindCustomEngines = () => SourceBuilds();
        var window = ScreenshotTests.Show(viewModel);

        viewModel.ShowProjectsCommand.Execute(null);
        ScreenshotTests.WaitFor(() => projects.HasLoaded && projects.Items.All(c => c.Size is not null));
        Assert.Equal(["Archive", "Demos", "Plugin Tests"], projects.Groups.Select(g => g.Title)); // all three in one: no Uncategorized
        Assert.Equal(["(1)", "(2)", "(2)"], projects.Groups.Select(g => g.CountText));
        Assert.False(projects.Groups[0].IsOpen);
        Dispatcher.UIThread.RunJobs();
        ScreenshotTests.Save(window, "projects-categories.png");

        // Under the pointer, a heading gets no box, as buttons do: it lights up instead, its name, arrow and count.
        var heading = window.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("groupHeading"));
        var texts = heading.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("title") || t.Classes.Contains("count")).ToList();
        var arrow = heading.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single();
        var before = (texts.Select(t => t.Foreground).ToList(), arrow.Stroke);
        window.MouseMove(heading.TranslatePoint(new Avalonia.Point(20, heading.Bounds.Height / 2), window)!.Value);
        Dispatcher.UIThread.RunJobs();
        Assert.True(heading.IsPointerOver);
        Assert.Equal(2, texts.Count);
        Assert.All(texts.Zip(before.Item1), pair => Assert.NotEqual(pair.Second, pair.First.Foreground));
        Assert.NotEqual(before.Stroke, arrow.Stroke);
        var presenter = heading.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First();
        Assert.Equal(Avalonia.Media.Colors.Transparent, (presenter.Background as Avalonia.Media.ISolidColorBrush)?.Color);
        ScreenshotTests.Save(window, "projects-heading-hover.png");
    }

    /// <summary>
    /// Uncategorized can hold most of a library, many screens of it. Folded when the page was built, it opens, and all of it
    /// shows; partway open, the part of it on screen is partway filled, so it slides as the eye sees it rather than filling
    /// at the slide's very start, as if it jumped open. Partway is set here, not timed: how many frames a machine draws in a
    /// slide of so many cards varies (the slide itself is timed in <see cref="FoldPanelTests"/>).
    /// </summary>
    [AvaloniaFact]
    public void A_big_group_slides_open_and_shut_on_screen()
    {
        var viewModel = ScreenshotTests.SampleMainViewModel();
        var projects = viewModel.Projects;
        projects.CategoriesPath = Path.Combine(_dir, "project-categories.json");
        var found = Enumerable.Range(1, 81).Select(i => Project($"Level{i:00}", "5.6")).ToArray();
        new ProjectCategories
        {
            UncategorizedFolded = true,
            Categories = [new ProjectCategory { Name = "Demos", Projects = [found[0].ProjectFile] }],
        }.Save(projects.CategoriesPath);
        projects.FindProjects = () => found;
        projects.FindCustomEngines = () => SourceBuilds();
        var window = ScreenshotTests.Show(viewModel);
        viewModel.ShowProjectsCommand.Execute(null);
        ScreenshotTests.WaitFor(() => projects.HasLoaded);
        Dispatcher.UIThread.RunJobs();
        var uncategorized = projects.Groups.Single(g => g.Title == "Uncategorized");
        Assert.Equal(80, uncategorized.Items.Count);
        var fold = window.GetVisualDescendants().OfType<FoldPanel>().Single(f => f.DataContext == uncategorized);
        double room = window.ClientSize.Height - fold.TranslatePoint(default, window)!.Value.Y; // from its top to the window's bottom
        double OnScreen() => Math.Min(fold.Bounds.Height, room);

        void Settled(double openness) => ScreenshotTests.WaitFor(() =>
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            return fold.Openness == openness;
        });

        uncategorized.ToggleFoldCommand.Execute(null);
        Settled(1);
        Dispatcher.UIThread.RunJobs();
        Assert.True(fold.Bounds.Height > window.ClientSize.Height * 3, "all of it, once open");

        fold.Transitions = null;
        foreach (double partway in new[] { 0.2, 0.4 })
        {
            fold.Openness = partway;
            Dispatcher.UIThread.RunJobs();
            Assert.InRange(OnScreen(), room * 0.1, room * 0.9);
        }

        uncategorized.ToggleFoldCommand.Execute(null);
        Settled(0);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, fold.Bounds.Height);
    }

    [AvaloniaFact]
    public void Projects_take_two_columns_and_one_when_the_window_is_narrow()
    {
        var viewModel = ScreenshotTests.SampleMainViewModel();
        var projects = viewModel.Projects;
        DateTime now = DateTime.UtcNow;
        projects.FindProjects = () =>
        [
            Project("Hillside", "5.6", now, Colors.SteelBlue),
            Project("Ridgeback", "5.7", now.AddDays(-1), Colors.IndianRed),
            Project("Lakeside", "5.5", now.AddDays(-3), Colors.CadetBlue),
            Project("OldTown", "4.26", now.AddDays(-40)),
            Project("Greybox", "5.7", null, Colors.DarkKhaki),
        ];
        projects.FindCustomEngines = () => SourceBuilds();
        var window = ScreenshotTests.Show(viewModel);

        viewModel.ShowProjectsCommand.Execute(null);
        ScreenshotTests.WaitFor(() => projects.HasLoaded && projects.Items.All(c => c.Size is not null));
        Assert.Equal("5 Projects", projects.Summary);
        Assert.True(projects.Items.Single(c => c.Name == "OldTown").IsEngineMissing); // UE 4.26 isn't among the sample engines
        // Said in grey, not as a warning: an engine not installed may well be on purpose.
        var notInstalled = window.GetVisualDescendants().OfType<ProjectCardView>().Single(c => c.DataContext is ProjectCardViewModel { Name: "OldTown" })
            .GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("chip"));
        Assert.Equal((true, false), (notInstalled.IsEffectivelyVisible, notInstalled.Classes.Contains("warning")));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, Columns(window));
        ScreenshotTests.Save(window, "projects.png");

        window.Width = 784;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, Columns(window));
        ScreenshotTests.Save(window, "projects-narrow.png");
    }

    /// <summary>How many cards share the first row.</summary>
    private static int Columns(Window window)
    {
        var tops = window.GetVisualDescendants().OfType<ProjectCardView>().Where(c => c.IsEffectivelyVisible)
            .Select(c => c.TranslatePoint(default, window)!.Value.Y).ToList();
        return tops.Count(top => Math.Abs(top - tops[0]) < 1);
    }
}
