using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Styling;

namespace UnVault.App.Views;

/// <summary>
/// Gives a window Windows' dark title bar to go with the app's dark theme. Avalonia does this itself only on Windows 11;
/// Windows 10 has the same switch, under an older number before 20H1. Set before the window shows, as Windows 10
/// doesn't repaint the title bar for it until the window is next activated.
/// </summary>
internal static class DarkTitleBar
{
    private const int UseImmersiveDarkMode = 20;
    private const int UseImmersiveDarkModeBefore20H1 = 19;

    public static void Apply(Window window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) || window.TryGetPlatformHandle() is not { HandleDescriptor: "HWND" } handle)
            return; // older Windows has no dark title bar; headless tests have no window
        int dark = window.ActualThemeVariant == ThemeVariant.Dark ? 1 : 0;
        int attribute = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18985) ? UseImmersiveDarkMode : UseImmersiveDarkModeBefore20H1;
        DwmSetWindowAttribute(handle.Handle, attribute, ref dark, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
