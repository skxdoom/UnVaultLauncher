using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnVault.App.Services;
using UnVault.Core.Projects;
using UnVault.Core.Util;

namespace UnVault.App.ViewModels;

/// <summary>One project on the Projects tab: the editor's picture of it, the engine it's for, and opening it.</summary>
public partial class ProjectCardViewModel(ProjectsViewModel owner, UnrealProject project) : ViewModelBase
{
    public UnrealProject Project { get; } = project;

    public string Name => Project.Name;
    public string Directory => Project.Directory;

    /// <summary>Its first letter, in place of the picture until that loads, or when the editor hasn't taken one.</summary>
    public string Initial => Name.Length > 0 ? char.ToUpperInvariant(Name[0]).ToString() : "";

    [ObservableProperty] public partial Bitmap? Screenshot { get; set; }

    /// <summary>All its folder holds; null until counted, in the background.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    public partial long? Size { get; set; }

    public string? SizeText => Size is { } size ? ByteSize.Format(size) : null;

    /// <summary>The folder of the engine it opens in; null when that engine isn't on this PC (or it names none).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpen), nameof(IsEngineMissing), nameof(OpenText), nameof(OpenTip))]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    public partial string? EngineDirectory { get; set; }

    public bool CanOpen => EngineDirectory is not null || IsEngineMissing;

    public string OpenText => IsEngineMissing ? Strings.ChooseEngine : Strings.Open;

    /// <summary>The engine it names isn't installed: said, but not as a warning, as that may well be on purpose.</summary>
    public bool IsEngineMissing => EngineDirectory is null && Project.EngineAssociation.Length > 0;

    /// <summary>"UE 5.6"; a source build's, with its version when it can be read; or that it names no engine.</summary>
    public string EngineText =>
        Project.EngineAppName is not null ? Localized.Format(Strings.ProjectEngine, Project.EngineAssociation)
        : Project.EngineAssociation.Length == 0 ? Strings.ProjectNoEngine
        : owner.SourceBuildFor(Project) is { Version: { } version } ? Localized.Format(Strings.ProjectSourceBuildVersion, version)
        : Strings.ProjectSourceBuild;

    public string? LastOpenedText => Project.LastOpened is { } opened
        ? Localized.Format(Strings.ProjectLastOpened, HistoryDayViewModel.HeadingFor(opened.ToLocalTime().Date, DateTime.Today))
        : null;

    /// <summary>The engine, and when it was last opened, if the editor says.</summary>
    public string DetailsText => LastOpenedText is { } opened ? $"{EngineText}  ·  {opened}" : EngineText;

    /// <summary>What Open does, or why it can't.</summary>
    public string OpenTip =>
        IsEngineMissing ? Localized.Format(Strings.ProjectEngineMissing, EngineText)
        : CanOpen ? Localized.Format(Strings.OpenProjectIn, EngineText)
        : Strings.ProjectNamesNoEngine;

    public bool Matches(string search) =>
        search.Length == 0
        || Name.Contains(search, StringComparison.OrdinalIgnoreCase)
        || EngineText.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Directory.Contains(search, StringComparison.OrdinalIgnoreCase);

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private void Open() => owner.Open(this);

    [RelayCommand]
    private void ShowInFolder() => owner.ShowInFolder(this);

    /// <summary>The user's categories, for its menu: each ticked when it's in it.</summary>
    public IReadOnlyList<ProjectCategory> Categories => owner.SortedCategories;

    public bool IsIn(ProjectCategory category) => category.Contains(Project.ProjectFile);

    [RelayCommand]
    private void ToggleCategory(ProjectCategory category) => owner.ToggleCategory(category, this);

    [RelayCommand]
    private void NewCategory() => owner.AskNewCategory(this);
}
