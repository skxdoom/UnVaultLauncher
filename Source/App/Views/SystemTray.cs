using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using UnVault.App.ViewModels;

namespace UnVault.App.Views;

/// <summary>
/// The app's icon in the system tray, there while Settings has closing the window keep the app running: a click opens
/// the window again, and its menu opens it or quits.
/// </summary>
internal static class SystemTray
{
    public static void Add(Application app, IClassicDesktopStyleApplicationLifetime desktop, MainWindow window, MainViewModel viewModel)
    {
        var open = new NativeMenuItem(Strings.OpenUnVaultLauncher);
        open.Click += (_, _) => window.ShowAgain();
        var quit = new NativeMenuItem(Strings.Quit);
        quit.Click += (_, _) => desktop.Shutdown();

        var icon = new TrayIcon
        {
            Icon = window.Icon,
            ToolTipText = "UnVault Launcher",
            Menu = new NativeMenu { Items = { open, new NativeMenuItemSeparator(), quit } },
            IsVisible = viewModel.CloseToTray,
        };
        icon.Clicked += (_, _) => window.ShowAgain();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CloseToTray))
                icon.IsVisible = viewModel.CloseToTray;
        };
        TrayIcon.SetIcons(app, new TrayIcons { icon });
    }
}
