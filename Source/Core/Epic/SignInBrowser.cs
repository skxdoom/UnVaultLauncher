namespace UnVault.Core.Epic;

/// <summary>
/// The app signs in through an embedded browser with a profile of its own in the data folder, which keeps Epic's web
/// sign-in cookies. Signing out, from the app or the CLI, clears it, so the next sign-in can pick another account.
/// </summary>
public static class SignInBrowser
{
    public static string DataDirectory => Path.Combine(AppPaths.DataDirectory, "WebView2");

    /// <summary>Left by a clear that couldn't finish (the browser still had its files open), for <see cref="ClearPendingAsync"/>.</summary>
    private static string PendingFile => Path.Combine(AppPaths.DataDirectory, "WebView2.clear");

    /// <summary>Deletes the browser's profile; best effort. Returns whether it's gone; if not, <see cref="ClearPendingAsync"/> tries again later.</summary>
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

        bool gone = !Directory.Exists(DataDirectory);
        try
        {
            if (gone)
                File.Delete(PendingFile);
            else
                File.WriteAllText(PendingFile, "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the reminder, the profile stays until the next sign-out.
        }
        return gone;
    }

    /// <summary>
    /// Finishes a clear that couldn't be done at sign-out: run when the app starts and before the sign-in window opens,
    /// so the next sign-in doesn't find the previous account still signed in. Leaves the profile alone otherwise.
    /// </summary>
    public static Task<bool> ClearPendingAsync() => File.Exists(PendingFile) ? ClearAsync() : Task.FromResult(true);
}
