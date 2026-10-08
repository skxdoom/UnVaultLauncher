using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Web.WebView2.Core;
using UnVault.Core;
using UnVault.Core.Epic;

namespace UnVault.App.Views;

/// <summary>
/// Epic sign-in inside the app. On Windows an embedded WebView2 shows Epic's own page; when it reaches the
/// page that hands out the authorization code, we read the code and close — nothing to copy. The browser
/// profile is UnVault's own (separate from your normal browser), cleared on sign-out.
/// </summary>
public partial class LoginWindow : Window
{
    private const string CodePagePrefix = "https://www.epicgames.com/id/api/redirect";

    private CoreWebView2Controller? _controller;
    private string? _code;

    public LoginWindow()
    {
        InitializeComponent();
        Opened += async (_, _) => await StartAsync();
        SizeChanged += (_, _) => UpdateBrowserBounds();
        Closed += (_, _) => _controller?.Close();
    }


    /// <summary>Shows the window and returns the authorization code, or null if the user closed it.</summary>
    public async Task<string?> ShowAndWaitAsync(Window owner)
    {
        await ShowDialog(owner);
        return _code;
    }

    private async Task StartAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            ShowFallback("The built-in sign-in window needs Windows.");
            return;
        }

        try
        {
            var handle = TryGetPlatformHandle()?.Handle ?? throw new InvalidOperationException("No native window handle.");
            var environment = await CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder: SignInBrowser.DataDirectory);
            _controller = await environment.CreateCoreWebView2ControllerAsync(handle);
            _controller.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 14, 16, 20);
            UpdateBrowserBounds();

            var browser = _controller.CoreWebView2;
            browser.Settings.AreDevToolsEnabled = false;
            browser.Settings.IsStatusBarEnabled = false;
            // Social logins (Google, Steam…) open popups; keep them in this window.
            browser.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                browser.Navigate(e.Uri);
            };
            browser.NavigationCompleted += async (_, _) => await CheckForCodeAsync(browser);
            browser.Navigate(EpicEndpoints.LoginURL);
            LoadingPanel.IsVisible = false;
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            ShowFallback($"The built-in browser isn't available ({ex.Message}).");
        }
    }

    private async Task CheckForCodeAsync(CoreWebView2 browser)
    {
        if (!browser.Source.StartsWith(CodePagePrefix, StringComparison.OrdinalIgnoreCase))
            return;

        // The page body is the JSON with the code; ExecuteScriptAsync returns it as a JSON string literal.
        string result = await browser.ExecuteScriptAsync("document.body.innerText");
        string text = JsonDocument.Parse(result).RootElement.GetString() ?? "";
        string code = EpicAuthClient.ExtractAuthorizationCode(text);
        if (IsCode(code))
        {
            _code = code;
            Close();
        }
    }

    private void UpdateBrowserBounds()
    {
        if (_controller is null)
            return;
        double scale = RenderScaling;
        _controller.Bounds = new System.Drawing.Rectangle(0, 0, (int)(ClientSize.Width * scale), (int)(ClientSize.Height * scale));
    }

    private void ShowFallback(string reason)
    {
        LoadingPanel.IsVisible = false;
        FallbackReason.Text = reason;
        FallbackPanel.IsVisible = true;
    }

    private void OnOpenBrowserClick(object? sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(EpicEndpoints.LoginURL) { UseShellExecute = true });

    private void OnSubmitCodeClick(object? sender, RoutedEventArgs e)
    {
        string code = EpicAuthClient.ExtractAuthorizationCode(CodeBox.Text ?? "");
        if (!IsCode(code))
        {
            FallbackReason.Text = "That doesn't look like the code. Copy the whole text the page shows, including \"authorizationCode\".";
            return;
        }
        _code = code;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private static bool IsCode(string value) => Regex.IsMatch(value, "^[0-9a-fA-F]{32}$");
}
