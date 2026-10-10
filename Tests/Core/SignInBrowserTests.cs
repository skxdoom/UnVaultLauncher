using UnVault.Core.Epic;

namespace UnVault.Core.Tests;

public sealed class SignInBrowserTests
{
    /// <summary>The browser can still hold its files at sign-out; then the app's next start or sign-in finishes the job.</summary>
    [Fact]
    public async Task A_profile_still_in_use_at_sign_out_is_cleared_the_next_time()
    {
        string cookies = Path.Combine(Directory.CreateDirectory(SignInBrowser.DataDirectory).FullName, "Cookies");
        File.WriteAllText(cookies, "fictional");
        try
        {
            // Nothing pending: the signed-in account's profile is left alone.
            Assert.True(await SignInBrowser.ClearPendingAsync());
            Assert.True(File.Exists(cookies));

            using (File.Open(cookies, FileMode.Open, FileAccess.Read, FileShare.None)) // the browser still has it open
                Assert.False(await SignInBrowser.ClearAsync());
            Assert.True(File.Exists(cookies));

            Assert.True(await SignInBrowser.ClearPendingAsync());
            Assert.False(Directory.Exists(SignInBrowser.DataDirectory));

            // Done with: the profile of the next sign-in stays.
            Directory.CreateDirectory(SignInBrowser.DataDirectory);
            Assert.True(await SignInBrowser.ClearPendingAsync());
            Assert.True(Directory.Exists(SignInBrowser.DataDirectory));
        }
        finally
        {
            await SignInBrowser.ClearAsync();
        }
    }
}
