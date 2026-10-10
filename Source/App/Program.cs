using Avalonia;
using System;

namespace UnVault.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        using var instance = Services.SingleInstance.Claim();
        if (instance is null)
        {
            Services.SingleInstance.ShowRunning();
            return;
        }
        Instance = instance;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>This copy, the running one; App shows the window when the app is started again.</summary>
    internal static Services.SingleInstance? Instance { get; private set; }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
