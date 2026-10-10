using Avalonia.Headless.XUnit;
using UnVault.App.Services;
using UnVault.App.ViewModels;
using UnVault.App.Views;

namespace UnVault.App.Tests;

public class SignInTests
{
    [Theory]
    [InlineData("https://www.epicgames.com/id/login", true)]
    [InlineData("http://www.epicgames.com/id/login", false)]
    [InlineData("com.epicgames.launcher://store", false)]
    [InlineData("file:///C:/Users/Someone/page.html", false)]
    [InlineData("about:blank", false)]
    [InlineData("not an address", false)]
    public void The_sign_in_window_goes_to_secure_pages_only(string address, bool secure) =>
        Assert.Equal(secure, LoginWindow.IsSecure(address));

    [Theory]
    [InlineData("https://www.epicgames.com/id/login?lang=en", "www.epicgames.com")]
    [InlineData("https://accounts.google.com/o/oauth2/auth", "accounts.google.com")]
    [InlineData("https://www.epicgames.com@lookalike.example/id/login", "lookalike.example")] // the site is after the @
    public void The_address_bar_shows_the_site_a_page_is_from(string address, string site) =>
        Assert.Equal(site, LoginWindow.SiteName(address));

    /// <summary>The code is only read from Epic's own page, which is hidden while it's read.</summary>
    [Theory]
    [InlineData("https://www.epicgames.com/id/api/redirect?clientId=0123&responseType=code", true)]
    [InlineData("https://www.epicgames.com/id/login", false)]
    [InlineData("https://www.epicgames.com.lookalike.example/id/api/redirect", false)]
    public void The_code_comes_from_Epics_own_page(string address, bool codePage) =>
        Assert.Equal(codePage, LoginWindow.IsCodePage(address));

    /// <summary>A Cyrillic "е" looks just like a Latin "e"; spelled out, the site can't pass for Epic's.</summary>
    [Fact]
    public void A_site_with_look_alike_letters_is_spelled_out()
    {
        string site = LoginWindow.SiteName("https://www.\u0435picgames.com/id/login");
        Assert.StartsWith("www.xn--", site, StringComparison.Ordinal);
        Assert.NotEqual("www.epicgames.com", site);
    }

    [AvaloniaFact]
    public async Task Signing_out_says_when_the_sign_in_browser_kept_its_data()
    {
        var viewModel = new MainViewModel(new AppServices()) { Interaction = new FakeInteraction(forgets: false) };
        await viewModel.SignOutCommand.ExecuteAsync(null);
        Assert.Equal(Strings.SignInBrowserNotCleared, viewModel.Notice);

        viewModel.Notice = null;
        viewModel.Interaction = new FakeInteraction(forgets: true);
        await viewModel.SignOutCommand.ExecuteAsync(null);
        Assert.Null(viewModel.Notice);
    }

    private sealed class FakeInteraction(bool forgets) : IUserInteraction
    {
        public Task<string?> SignInAsync() => Task.FromResult<string?>(null);
        public Task<bool> ForgetSignInAsync() => Task.FromResult(forgets);
        public Task<string?> PickFolderAsync(string title, string? startPath) => Task.FromResult<string?>(null);
        public Task<string?> PickFileAsync(string title, string? startPath, string fileTypeName, string pattern) => Task.FromResult<string?>(null);
    }
}
