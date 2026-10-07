using System.Runtime.CompilerServices;

namespace UnVault.Core.Tests;

/// <summary>Runs before any test: the app's data folder (sign-in, settings, caches) is a temporary one, never the user's.</summary>
internal static class TestSetup
{
#pragma warning disable CA2255 // a test assembly: setting up before the first test is the point
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void UseTemporaryDataFolder()
    {
        string folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"unvault-tests-{Guid.NewGuid():N}")).FullName;
        AppPaths.UseDataDirectory(folder);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left in the temp folder.
            }
        };
    }
}

public class TestSetupTests
{
    [Fact]
    public void Tests_never_touch_the_real_data_folder() =>
        Assert.StartsWith(Path.GetTempPath(), AppPaths.DataDirectory, StringComparison.OrdinalIgnoreCase);
}
