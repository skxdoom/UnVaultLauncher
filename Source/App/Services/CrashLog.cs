using UnVault.Core;

namespace UnVault.App.Services;

/// <summary>Unexpected errors go to %LOCALAPPDATA%\UnVaultLauncher\crash.log, so a problem can be diagnosed afterwards.</summary>
public static class CrashLog
{
    public static string FilePath => Path.Combine(AppPaths.DataDirectory, "crash.log");

    public static void Write(string context, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Logging must never be the thing that fails.
        }
    }
}
