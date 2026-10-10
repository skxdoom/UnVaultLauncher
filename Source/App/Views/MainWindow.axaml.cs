using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using UnVault.App.Services;
using UnVault.Core.Epic;

namespace UnVault.App.Views;

public partial class MainWindow : Window, IUserInteraction
{
    public MainWindow()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        // Fetch tile pictures at the size the display actually needs (it can change when moved to another monitor).
        Opened += (_, _) => UseScaling();
        ScalingChanged += (_, _) => UseScaling();
        DataContextChanged += (_, _) => WatchDialogs();
        Closing += (_, e) =>
        {
            if (HidesInsteadOfClosing(_watched?.CloseToTray == true, e.CloseReason, e.IsProgrammatic))
            {
                e.Cancel = true;
                Hide();
                MemoryRelief.Release(); // running in the background, it needn't hold on to what the window showed
            }
        };
    }

    /// <summary>
    /// With "keep running in the system tray" on, closing the window only hides it, however it's closed (✕, Alt+F4, the
    /// taskbar); Quit in the tray menu and Windows shutting down still end the app.
    /// </summary>
    internal static bool HidesInsteadOfClosing(bool closeToTray, WindowCloseReason reason, bool isProgrammatic) =>
        closeToTray && reason == WindowCloseReason.WindowClosing && !isProgrammatic;

    /// <summary>Back from the system tray, or to the front when the app is started again.</summary>
    public void ShowAgain()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private ViewModels.MainViewModel? _watched;
    private IInputElement? _focusBeforeDialog;
    private IInputElement? _focusBeforePrompt;

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

    /// <summary>
    /// A dialog takes the keyboard while it's open, and a question asked over it takes it from the dialog; afterwards focus
    /// goes back to where it was (the button that opened it), or to the dialog when that button is gone or disabled.
    /// </summary>
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModels.MainViewModel.Dialog))
            Follow(_watched?.HasDialog == true, ref _focusBeforeDialog, FocusDialog, fallback: null);
        else if (e.PropertyName == nameof(ViewModels.MainViewModel.Prompt))
            Follow(_watched?.HasPrompt == true, ref _focusBeforePrompt, FocusPrompt, fallback: FocusDialog);
    }

    private void Follow(bool open, ref IInputElement? before, Action focus, Action? fallback)
    {
        if (open)
        {
            before ??= FocusManager?.GetFocusedElement();
            Dispatcher.UIThread.Post(focus, DispatcherPriority.Loaded);
        }
        else if (before is { } previous)
        {
            before = null;
            Dispatcher.UIThread.Post(() =>
            {
                if (previous is Visual visual && TopLevel.GetTopLevel(visual) is not null && previous.IsEffectivelyEnabled)
                    previous.Focus();
                else
                    fallback?.Invoke();
            }, DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    /// Focus to the dialog itself, not to one of its fields: nothing looks selected after a click, Esc and Enter answer the
    /// dialog, and Tab goes to its first field. Left on the page, focus would let Enter press the button behind it again.
    /// </summary>
    internal void FocusDialog()
    {
        if (_watched?.HasDialog == true)
            DialogHost.Focus(NavigationMethod.Unspecified);
    }

    private void FocusPrompt() => PromptHost.Focus(NavigationMethod.Unspecified);

    private void UseScaling() => (DataContext as ViewModels.MainViewModel)?.Services.Thumbnails.UseScaling(RenderScaling);

    public Task<string?> SignInAsync() => new LoginWindow().ShowAndWaitAsync(this);

    public Task<bool> ForgetSignInAsync() => SignInBrowser.ClearAsync();

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
