namespace Unvault.Core.Epic;

/// <summary>
/// Epic's launcher-facing services. None of this is a documented public API: it's what the Epic Games
/// Launcher itself calls, as mapped by the community (Legendary, Heroic). Expect occasional changes.
/// </summary>
public static class EpicEndpoints
{
    /// <summary>
    /// The Epic Games Launcher's own OAuth client. Not a secret in practice: it ships inside EGL and is
    /// published in open-source launchers. Signing in with it is what makes Epic treat us as the launcher.
    /// </summary>
    public const string LauncherClientID = "34a02cf8f4414e29b15921876da36f9a";
    public const string LauncherClientSecret = "daafbccc737745039dffe53d94fc76cf";

    /// <summary>Sent on every request; EGL-shaped so the services don't treat us as an unknown client.</summary>
    public const string UserAgent = "UELauncher/11.0.1-14907503+++Portal+Release-Live Windows/10.0.19041.1.256.64bit";

    public const string AccountService = "https://account-public-service-prod03.ol.epicgames.com";
    public const string LauncherService = "https://launcher-public-service-prod06.ol.epicgames.com";
    public const string CatalogService = "https://catalog-public-service-prod06.ol.epicgames.com";
    public const string LibraryService = "https://library-service.live.use1a.on.epicgames.com";
    public const string FabService = "https://www.fab.com";

    public static string TokenURL => $"{AccountService}/account/api/oauth/token";

    /// <summary>Once signed in on epicgames.com, this page returns JSON with a one-time authorizationCode.</summary>
    public static string AuthorizationCodeURL =>
        $"https://www.epicgames.com/id/api/redirect?clientId={LauncherClientID}&responseType=code";

    /// <summary>Epic's normal sign-in page, which then forwards to <see cref="AuthorizationCodeURL"/>.</summary>
    public static string LoginURL =>
        "https://www.epicgames.com/id/login?redirectUrl=" + Uri.EscapeDataString(AuthorizationCodeURL);
}
