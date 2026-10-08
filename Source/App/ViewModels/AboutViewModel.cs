using CommunityToolkit.Mvvm.Input;
using UnVault.Core;

namespace UnVault.App.ViewModels;

/// <summary>The About dialog: version and whether it's the latest, links, and thanks. Opened from the version in the header.</summary>
public partial class AboutViewModel(MainViewModel owner) : ViewModelBase
{
    /// <summary>Update status and the newer release live on the main view model, which checks at startup.</summary>
    public MainViewModel Owner => owner;

    public string VersionText { get; } = "Version " + ProductInfo.Version;

    public string RepositoryURL => UpdateChecker.RepositoryURL;
    public string ReleasesURL => UpdateChecker.RepositoryURL + "/releases";
    public string IssuesURL => UpdateChecker.RepositoryURL + "/issues";
    public string LicenseURL => UpdateChecker.RepositoryURL + "/blob/main/LICENSE";
    public string LegendaryURL => "https://github.com/legendary-gl/legendary";
    public string EGSAPIURL => "https://github.com/AchetaGames/egs-api-rs";

    [RelayCommand]
    private Task CheckAgainAsync() => owner.CheckForUpdatesAsync();

    [RelayCommand]
    private void Download() => owner.OpenURL(owner.AppUpdate?.PageURL ?? ReleasesURL);

    [RelayCommand]
    private void OpenLink(string url) => owner.OpenURL(url);

    /// <summary>Sign-in, settings, caches and crash.log: what's useful when reporting a problem.</summary>
    [RelayCommand]
    private void OpenDataFolder()
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        owner.OpenURL(AppPaths.DataDirectory);
    }

    [RelayCommand]
    private void Close() => owner.CloseDialog();
}
