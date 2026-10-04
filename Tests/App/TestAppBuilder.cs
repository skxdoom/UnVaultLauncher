using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(UnVault.App.Tests.TestAppBuilder))]

namespace UnVault.App.Tests;

/// <summary>Runs the real app styles headlessly, rendering with Skia so frames can be captured as images.</summary>
public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
