using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnVault.App.Services;
using UnVault.Core.Projects;

namespace UnVault.App.ViewModels;

/// <summary>
/// The Projects tab: the projects the editor has opened, and those in the project folders (Settings), alphabetically. Each
/// opens in the engine it's for when available, or through its .uproject association otherwise. The user can bundle them
/// in categories of their own, as Steam's collections: a project can be in several, and those in none come after them.
/// </summary>
public partial class ProjectsViewModel(MainViewModel owner) : ViewModelBase
{
    /// <summary>The picture's width in pixels: about twice its width on the card, so it stays sharp on a high-DPI display.</summary>
    internal const int ScreenshotPixels = 192;

    public MainViewModel Owner { get; } = owner;

    private List<ProjectCardViewModel> _all = [];
    private IReadOnlyDictionary<string, CustomEngine> _customEngines = new Dictionary<string, CustomEngine>();

    /// <summary>Pictures already read, by file, with the file's time: a reload doesn't read them again unless they changed.</summary>
    private Dictionary<string, (DateTime Written, Bitmap? Picture)> _screenshots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Sizes counted before, by project file: a reload shows them while it counts again.</summary>
    private readonly Dictionary<string, long> _sizes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Stops the counting of a load that's been replaced.</summary>
    private CancellationTokenSource? _counting;

    /// <summary>The projects the search leaves, each once.</summary>
    public ObservableCollection<ProjectCardViewModel> Items { get; } = [];

    /// <summary>What's shown: each category, then the projects in none; while there are no categories, just the projects.</summary>
    public ObservableCollection<ProjectGroupViewModel> Groups { get; } = [];

    private ProjectCategories? _categories;

    /// <summary>The user's categories, read on first use.</summary>
    internal ProjectCategories Categories => _categories ??= ProjectCategories.Load(CategoriesPath);

    /// <summary>Where the categories are kept; tests keep theirs in a file of their own.</summary>
    internal string? CategoriesPath { get; set; }

    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial bool HasLoaded { get; set; }
    [ObservableProperty] public partial string Summary { get; set; } = "";
    [ObservableProperty] public partial bool IsEmpty { get; set; }
    [ObservableProperty] public partial string EmptyText { get; set; } = "";
    [ObservableProperty] public partial string? EmptyHint { get; set; }

    /// <summary>Where projects are found; tests give their own.</summary>
    internal Func<IReadOnlyList<UnrealProject>> FindProjects { get; set; } =
        () => ProjectLocator.FindProjects(owner.Settings.ResolveProjectFolders().Paths);

    /// <summary>The source-built engines registered on this PC; tests give their own.</summary>
    internal Func<IReadOnlyDictionary<string, CustomEngine>> FindCustomEngines { get; set; } = CustomEngines.Registered;

    /// <summary>Starts the editor; tests only look at what it would start.</summary>
    internal Action<ProcessStartInfo> StartProcess { get; set; } = start => Process.Start(start)?.Dispose();

    /// <summary>Finds the projects again (first visit, the refresh button, changed project folders).</summary>
    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var (projects, customEngines) = await Task.Run(() => (FindProjects(), FindCustomEngines()));
            SetProjects(projects, customEngines);
        }
        finally
        {
            IsLoading = false;
            HasLoaded = true;
            UpdateEmpty();
        }
        _ = FillInAsync(_all); // not awaited: the refresh is done once the projects show
    }

    /// <summary>The pictures first, quick to read; then the sizes, which take longer for big projects.</summary>
    private async Task FillInAsync(IReadOnlyList<ProjectCardViewModel> cards)
    {
        await LoadScreenshotsAsync(cards);
        await CountSizesAsync(cards);
    }

    /// <summary>Shows these projects; their pictures and sizes follow (<see cref="FillInAsync"/>).</summary>
    internal void SetProjects(IReadOnlyList<UnrealProject> projects, IReadOnlyDictionary<string, CustomEngine> customEngines)
    {
        _customEngines = customEngines;
        _all = [.. projects.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new ProjectCardViewModel(this, p) { Size = _sizes.TryGetValue(p.ProjectFile, out long size) ? size : null })];
        HasLoaded = true;
        EnginesChanged();
        Filter();
    }

    /// <summary>The engines on this PC changed (a refresh, an install, a removal): which projects can open follows.</summary>
    internal void EnginesChanged()
    {
        foreach (var card in _all)
            card.EngineDirectory = EngineFor(card.Project);
    }

    /// <summary>The folder of the engine a project names: an installed version (UE_5.6), or a registered source build.</summary>
    internal string? EngineFor(UnrealProject project) =>
        project.EngineAppName is { } app
            ? Owner.LocalEngines.FirstOrDefault(e => e.Exists && string.Equals(e.AppName, app, StringComparison.OrdinalIgnoreCase))?.Directory
            : SourceBuildFor(project)?.Directory;

    internal CustomEngine? SourceBuildFor(UnrealProject project) => CustomEngines.Find(_customEngines, project.EngineAssociation);

    partial void OnSearchTextChanged(string value) => Filter();

    /// <summary>Empties the search box (the × button, or Esc).</summary>
    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    private void Filter()
    {
        string search = SearchText.Trim();
        Items.Clear();
        foreach (var card in _all.Where(c => c.Matches(search)))
            Items.Add(card);
        Groups.Clear();
        foreach (var group in GroupsOf([.. Items], searching: search.Length > 0))
            Groups.Add(group);
        Summary = Items.Count == _all.Count
            ? Localized.Plural(nameof(Strings.ProjectsSummary_Other), _all.Count)
            : Localized.Plural(nameof(Strings.ProjectsFiltered_Other), _all.Count, Items.Count);
        UpdateEmpty();
    }

    /// <summary>
    /// Each category, alphabetically, then the projects in none (when there are any). While searching, a category without a
    /// match isn't shown; otherwise an empty one is, so it can be filled.
    /// </summary>
    private IEnumerable<ProjectGroupViewModel> GroupsOf(IReadOnlyList<ProjectCardViewModel> cards, bool searching)
    {
        var categories = Categories.Categories;
        if (categories.Count == 0)
        {
            yield return new ProjectGroupViewModel(this, null, cards, hasHeading: false, searching);
            yield break;
        }
        foreach (var category in categories.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var items = cards.Where(card => card.IsIn(category)).ToList();
            if (items.Count > 0 || !searching)
                yield return new ProjectGroupViewModel(this, category, items, hasHeading: true, searching);
        }
        var uncategorized = cards.Where(card => !categories.Any(card.IsIn)).ToList();
        if (uncategorized.Count > 0)
            yield return new ProjectGroupViewModel(this, null, uncategorized, hasHeading: true, searching);
    }

    /// <summary>The categories by name, as the menus list them.</summary>
    internal IReadOnlyList<ProjectCategory> SortedCategories =>
        [.. Categories.Categories.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>Puts the project in the category, or takes it out.</summary>
    internal void ToggleCategory(ProjectCategory category, ProjectCardViewModel card)
    {
        string file = card.Project.ProjectFile;
        if (!category.Projects.Remove(category.Projects.FirstOrDefault(p => string.Equals(p, file, StringComparison.OrdinalIgnoreCase)) ?? ""))
            category.Projects.Add(file);
        CategoriesChanged();
    }

    /// <summary>Asks for a new category's name; it's made with <paramref name="card"/>'s project in it.</summary>
    internal void AskNewCategory(ProjectCardViewModel card) =>
        Owner.Dialog = new CategoryNameViewModel(Owner, Strings.NewCategoryTitle, Strings.Create, "",
            isTaken: name => Categories.Find(name) is not null,
            onConfirm: name =>
            {
                Categories.Categories.Add(new ProjectCategory { Name = name, Projects = [card.Project.ProjectFile] });
                CategoriesChanged();
            });

    internal void AskRename(ProjectGroupViewModel group)
    {
        if (group.Category is not { } category)
            return;
        Owner.Dialog = new CategoryNameViewModel(Owner, Strings.RenameCategoryTitle, Strings.Rename, category.Name,
            isTaken: name => Categories.Find(name) is { } other && other != category,
            onConfirm: name =>
            {
                category.Name = name;
                CategoriesChanged();
            });
    }

    /// <summary>Asks first: the bundle goes, though its projects stay (on disk, and in other categories).</summary>
    internal void AskDelete(ProjectGroupViewModel group)
    {
        if (group.Category is not { } category)
            return;
        Owner.Dialog = new ConfirmViewModel(Owner, Localized.Format(Strings.DeleteCategoryTitle, category.Name), Strings.DeleteCategoryMessage,
            Strings.Delete, () =>
            {
                Categories.Categories.Remove(category);
                CategoriesChanged();
            });
    }

    internal void ToggleFold(ProjectGroupViewModel group)
    {
        if (group.Category is { } category)
            category.Folded = !category.Folded;
        else
            Categories.UncategorizedFolded = !Categories.UncategorizedFolded;
        Categories.Save(CategoriesPath);
        group.FoldChanged(); // the page as it is, so the group slides shut or open
    }

    private void CategoriesChanged()
    {
        Categories.Save(CategoriesPath);
        Filter();
    }

    private void UpdateEmpty()
    {
        IsEmpty = !IsLoading && HasLoaded && Items.Count == 0;
        EmptyText = _all.Count == 0 ? Strings.NoProjects : Strings.NoProjectMatches;
        EmptyHint = _all.Count == 0 ? Strings.NoProjectsHint : null;
    }

    /// <summary>Each card's picture, one after another in the background; a missing or unreadable one keeps the initial.</summary>
    internal async Task LoadScreenshotsAsync(IReadOnlyList<ProjectCardViewModel> cards)
    {
        var known = _screenshots;
        var kept = new Dictionary<string, (DateTime Written, Bitmap? Picture)>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in cards)
        {
            string file = card.Project.ScreenshotFile;
            var (written, picture) = await Task.Run(() =>
            {
                DateTime time = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : default;
                return known.TryGetValue(file, out var had) && had.Written == time ? had : (time, time == default ? null : LoadScreenshot(file));
            });
            kept[file] = (written, picture);
            card.Screenshot = picture;
        }
        _screenshots = kept;
    }

    /// <summary>Each card's size, one project after another in the background; a newer load stops this one.</summary>
    internal async Task CountSizesAsync(IReadOnlyList<ProjectCardViewModel> cards)
    {
        _counting?.Cancel();
        var counting = _counting = new CancellationTokenSource();
        foreach (var card in cards)
        {
            try
            {
                long size = await Task.Run(() => ProjectLocator.SizeOf(card.Directory, counting.Token), counting.Token);
                _sizes[card.Project.ProjectFile] = size;
                card.Size = size;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Its folder went, or can't be read: no size, as before.
            }
        }
    }

    /// <summary>A project's file, not ours: whatever is wrong with it only costs the picture.</summary>
    internal static Bitmap? LoadScreenshot(string file)
    {
        try
        {
            if (!ThumbnailCache.IsPreviewSized(file))
                return null;
            using var stream = File.OpenRead(file);
            return Bitmap.DecodeToWidth(stream, ScreenshotPixels);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Opens the project in its engine, or through its .uproject association when that engine is missing.</summary>
    internal void Open(ProjectCardViewModel card)
    {
        ProcessStartInfo start;
        if (card.EngineDirectory is { } engine)
        {
            if (MainViewModel.EditorIn(engine) is not { } editor)
            {
                Owner.Notice = Localized.Format(Strings.NoEditorFound, engine);
                return;
            }
            start = new ProcessStartInfo(editor) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(editor) };
            start.ArgumentList.Add(card.Project.ProjectFile);
        }
        else if (card.IsEngineMissing)
        {
            // Let the .uproject shell association (normally UnrealVersionSelector) choose an available editor.
            start = new ProcessStartInfo(card.Project.ProjectFile) { UseShellExecute = true, WorkingDirectory = card.Directory };
        }
        else
            return;
        try
        {
            StartProcess(start);
        }
        catch (Win32Exception ex)
        {
            Owner.Notice = Localized.Format(Strings.CouldNotOpenProject, card.Name, ex.Message);
        }
    }

    internal void ShowInFolder(ProjectCardViewModel card) => Owner.OpenFolder(card.Directory);
}
