using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UnVault.App.Services;
using UnVault.Core.Epic;

namespace UnVault.App.Views;

public partial class MainWindow : Window, IUserInteraction
{
    public MainWindow()
    {
        InitializeComponent();
        // Fetch tile pictures at the size the display actually needs (it can change when moved to another monitor).
        Opened += (_, _) => UseScaling();
        ScalingChanged += (_, _) => UseScaling();
        DataContextChanged += (_, _) => WatchDialogs();
    }

    private ViewModels.MainViewModel? _watched;
    private IInputElement? _focusBeforeDialog;

    private void WatchDialogs()
    {
        if (_watched is not null)
            _watched.PropertyChanged -= OnViewModelChanged;
        _watched = DataContext as ViewModels.MainViewModel;
        if (_watched is not null)
            _watched.PropertyChanged += OnViewModelChanged;
        if (_watched?.HasDialog == true)
            Dispatcher.UIThread.Post(FocusDialog, DispatcherPriority.Loaded); // already open
    }

    /// <summary>A dialog takes the keyboard while it's open; afterwards focus goes back to where it was (e.g. the button that opened it).</summary>
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ViewModels.MainViewModel.Dialog))
            return;
        if (_watched?.HasDialog == true)
        {
            _focusBeforeDialog ??= FocusManager?.GetFocusedElement();
            Dispatcher.UIThread.Post(FocusDialog, DispatcherPriority.Loaded);
        }
        else if (_focusBeforeDialog is { } previous)
        {
            _focusBeforeDialog = null;
            Dispatcher.UIThread.Post(() =>
            {
                if (previous is Visual visual && TopLevel.GetTopLevel(visual) is not null && previous.IsEffectivelyEnabled)
                    previous.Focus();
            }, DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    /// Focus to the dialog's first field or list, ready to type or pick; in a dialog of buttons only (a confirmation),
    /// to its main button, so Enter confirms it.
    /// </summary>
    internal void FocusDialog()
    {
        var focusable = DialogHost.GetVisualDescendants().OfType<InputElement>()
            .Where(e => e.Focusable && e.IsEffectivelyEnabled && e.IsEffectivelyVisible && KeyboardNavigation.GetIsTabStop(e))
            .ToList();
        var target = focusable.FirstOrDefault(e => e is not Button) ?? focusable.OfType<Button>().FirstOrDefault(b => b.IsDefault) ?? focusable.FirstOrDefault();
        target?.Focus(NavigationMethod.Tab);
    }

    private void UseScaling() => (DataContext as ViewModels.MainViewModel)?.Services.Thumbnails.UseScaling(RenderScaling);

    public Task<string?> SignInAsync() => new LoginWindow().ShowAndWaitAsync(this);

    public Task ForgetSignInAsync() => SignInBrowser.ClearAsync();

    public async Task<string?> PickFolderAsync(string title, string? startPath)
    {
        var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
        if (startPath is not null && Directory.Exists(startPath))
            options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(startPath);

        var picked = await StorageProvider.OpenFolderPickerAsync(options);
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickFileAsync(string title, string? startPath, string fileTypeName, string pattern)
    {
        var options = new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(fileTypeName) { Patterns = [pattern] }],
        };
        if (startPath is not null && Directory.Exists(startPath))
            options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(startPath);

        var picked = await StorageProvider.OpenFilePickerAsync(options);
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }
}
