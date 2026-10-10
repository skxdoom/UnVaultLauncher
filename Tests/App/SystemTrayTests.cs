using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using UnVault.App.Services;
using UnVault.App.ViewModels;
using UnVault.App.Views;
using UnVault.Core;

namespace UnVault.App.Tests;

public class SystemTrayTests
{
    [Theory]
    [InlineData(true, WindowCloseReason.WindowClosing, false, true)]         // ✕, Alt+F4 or the taskbar, with the setting on
    [InlineData(false, WindowCloseReason.WindowClosing, false, false)]       // the setting off: closing quits, as before
    [InlineData(true, WindowCloseReason.ApplicationShutdown, true, false)]   // Quit in the tray menu
    [InlineData(true, WindowCloseReason.OSShutdown, false, false)]           // Windows shutting down or signing out
    public void Closing_the_window_hides_it_only_when_the_user_closes_it(bool closeToTray, WindowCloseReason reason, bool isProgrammatic, bool hides) =>
        Assert.Equal(hides, MainWindow.HidesInsteadOfClosing(closeToTray, reason, isProgrammatic));

    /// <summary>Started again, the app brings the running copy forward instead of becoming a second one.</summary>
    [Fact]
    public void Starting_the_app_again_shows_the_running_one()
    {
        string name = $"UnVaultLauncher.Test.{Guid.NewGuid():N}"; // never a launcher that's really running
        using var running = SingleInstance.Claim(name);
        Assert.NotNull(running);
        Assert.Null(SingleInstance.Claim(name));

        using var shown = new ManualResetEventSlim();
        running.WhenStartedAgain(shown.Set);
        SingleInstance.ShowRunning(name);
        Assert.True(shown.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    /// <summary>The tray icon follows the setting as soon as it's saved, not at the next start.</summary>
    [AvaloniaFact]
    public void Saving_the_setting_takes_effect_at_once()
    {
        var viewModel = new MainViewModel(new AppServices());
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        var settings = new SettingsViewModel(viewModel, Core.EGL.EGLLauncherSettings.Empty);
        viewModel.Dialog = settings;
        try
        {
            Assert.False(settings.CloseToTray);
            settings.CloseToTray = true;
            settings.SaveCommand.Execute(null);

            Assert.True(viewModel.CloseToTray);
            Assert.Contains(nameof(MainViewModel.CloseToTray), changed);
        }
        finally
        {
            File.Delete(AppSettings.FilePath); // other tests start from the defaults
        }
    }
}
