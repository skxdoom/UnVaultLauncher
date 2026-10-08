namespace UnVault.Core.Epic;

/// <summary>
/// The app signs in through an embedded browser with a profile of its own in the data folder, which keeps Epic's web
/// sign-in cookies. Signing out, from the app or the CLI, clears it, so the next sign-in can pick another account.
/// </summary>
public static class SignInBrowser
{
    public static string DataDirectory => Path.Combine(AppPaths.DataDirectory, "WebView2");

    /// <summary>Deletes the browser's profile; best effort. Returns whether it's gone.</summary>
    public static async Task<bool> ClearAsync()
    {
        // The browser's helper processes can hold files for a moment after its window closes.
        for (int attempt = 0; attempt < 10 && Directory.Exists(DataDirectory); attempt++)
        {
            try
            {
                Directory.Delete(DataDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(300);
            }
        }
        return !Directory.Exists(DataDirectory);
    }
}
