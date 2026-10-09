using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnVault.Core.EGL;
using UnVault.Core.Fab;
using UnVault.Core.Install;
using UnVault.Core.Util;

namespace UnVault.App.ViewModels;

/// <summary>
/// An engine's Installed Plugins, as in the Epic Games Launcher: every Fab plugin in it, whoever put it there, with the
/// version the plugin gives itself, to find on disk or remove.
/// </summary>
public partial class InstalledPluginsViewModel(MainViewModel owner, LocalEngine engine, string engineTitle) : ViewModelBase
{
    /// <summary>Icons show at 32 px; decoded for twice that, so they stay sharp on high-DPI screens.</summary>
    private const int IconPixels = 64;

    /// <summary>Names as people read them: case doesn't matter, and "Tools 2" comes before "Tools 10".</summary>
    private static readonly StringComparer ByName =
        StringComparer.Create(CultureInfo.CurrentCulture, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);

    public string Subtitle { get; } = $"{engineTitle}  ·  {engine.Directory}";

    public ObservableCollection<InstalledPluginViewModel> Plugins { get; } = [];

    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial string? EmptyText { get; set; }

    /// <param name="launcherInstalled">EGL's list of what it installed; read from this PC when not given (tests give their own).</param>
    public async Task LoadAsync(IReadOnlyList<LauncherInstalledEntry>? launcherInstalled = null)
    {
        IsLoading = true;
        try
        {
            var found = await Task.Run(() => EnginePlugins.Find(engine.Directory, launcherInstalled ?? EGLInstallations.ReadLauncherInstalled())
                .Select(p =>
                {
                    var descriptor = EnginePlugins.ReadDescriptor(p.Folder);
                    return (Plugin: p, Descriptor: descriptor, Size: SizeOf(p.Folder), Icon: LoadIcon(descriptor?.IconPath));
                })
                .ToList());
            foreach (var plugin in found.Select(f => Row(f.Plugin, f.Descriptor, f.Size, f.Icon)).OrderBy(p => p.Title, ByName))
                Plugins.Add(plugin);
            ShowIfEmpty();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            EmptyText = Localized.Format(Strings.PluginsLookupFailed, ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ShowIfEmpty() => EmptyText = Plugins.Count == 0 ? Strings.NoFabPlugins : null;

    private InstalledPluginViewModel Row(EnginePlugin plugin, PluginDescriptor? descriptor, long size, Bitmap? icon)
    {
        // The library's title is the one the Library tab shows; the plugin's own name is next best.
        var item = owner.Fab.FindItem(plugin.ArtifactID);
        string title = item?.Title ?? descriptor?.FriendlyName ?? plugin.ArtifactID;
        string version = descriptor?.VersionName is { } name ? Localized.Format(Strings.VersionValue, WithoutV(name)) : Strings.VersionUnknown;
        string details = !plugin.CanRemove ? Strings.InstalledByEGL
            : size > 0 ? $"{version}  ·  {ByteSize.Format(size)}"
            : version;
        var install = new FabInstall(engine.AppName, engine.Directory, plugin.ArtifactID, plugin.Source, plugin.Folder, plugin.CanRemove, plugin.BuildVersion);
        return new InstalledPluginViewModel(this, install, title, details, icon, item?.Key, isBusy: item?.IsBusy == true);
    }

    /// <summary>"v1.2" → "1.2", so it doesn't read "Version v1.2".</summary>
    private static string WithoutV(string version) =>
        version.Length > 1 && version[0] is 'v' or 'V' && char.IsDigit(version[1]) ? version[1..] : version;

    /// <summary>A folder that can't be read just shows no size.</summary>
    private static long SizeOf(string folder)
    {
        try
        {
            return EnginePlugins.FolderSize(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>A missing or broken icon file just shows the placeholder.</summary>
    private static Bitmap? LoadIcon(string? path)
    {
        if (path is null)
            return null;
        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, IconPixels);
        }
        catch (Exception)
        {
            return null; // a plugin's file, not ours: whatever is wrong with it, it only costs the picture
        }
    }

    /// <summary>Asked over this list, which stays in sight; Remove starts the removal and the row follows it.</summary>
    internal void ConfirmRemove(InstalledPluginViewModel plugin) =>
        owner.Prompt = new ConfirmViewModel(owner, Localized.Format(Strings.ConfirmRemoveTitle, plugin.Title),
            Localized.Format(Strings.ConfirmRemoveMessage, engineTitle), Strings.Remove, () => Remove(plugin));

    private void Remove(InstalledPluginViewModel plugin)
    {
        var removal = owner.Fab.StartRemove(plugin.Title, plugin.ItemKey, plugin.Install);
        plugin.IsRemoving = removal.IsRunning;
        removal.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(OperationViewModel.State))
                return;
            plugin.IsRemoving = removal.IsRunning; // a retry from the operations list runs again
            if (removal.State == OperationState.Completed && Plugins.Remove(plugin))
                ShowIfEmpty();
        };
    }

    internal void ShowInFolder(string folder) => owner.OpenURL(folder);

    [RelayCommand]
    private void Close() => owner.Close(this);
}

/// <summary>A plugin in an engine's Installed Plugins list.</summary>
public partial class InstalledPluginViewModel(InstalledPluginsViewModel owner, FabInstall install, string title, string details, Bitmap? icon,
    string? itemKey, bool isBusy) : ViewModelBase
{
    public FabInstall Install { get; } = install;
    public string Title { get; } = title;

    /// <summary>The plugin's own icon (Resources\Icon128.png); null shows a placeholder.</summary>
    public Bitmap? Icon { get; } = icon;
    public bool HasIcon => Icon is not null;

    /// <summary>The library item it belongs to (busy while it's removed); null when the library doesn't list it.</summary>
    public string? ItemKey { get; } = itemKey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Details), nameof(RemoveTip))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    public partial bool IsRemoving { get; set; }

    public string Details => IsRemoving ? Strings.Removing : details;

    public bool HasFolder => Directory.Exists(Install.Folder);

    /// <summary>Not while something else works on its files, nor when there's no telling its files from the engine's own.</summary>
    public bool CanRemove => Install.CanRemove && !isBusy && !IsRemoving;

    public string? RemoveTip =>
        !Install.CanRemove ? Strings.CantTellPluginFiles
        : isBusy || IsRemoving ? Strings.PluginBusy
        : null;

    [RelayCommand]
    private void ShowInFolder() => owner.ShowInFolder(Install.Folder);

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void Remove() => owner.ConfirmRemove(this);
}
