using Avalonia.Controls;
using Avalonia.Platform.Storage;
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
