using UnVault.Core;
using UnVault.Core.Epic;
using UnVault.Core.Util;

namespace UnVault.CLI;

/// <summary>Process-wide singletons for the CLI.</summary>
internal static class Services
{
    /// <summary>The command as typed (the exe's name), for help texts and hints.</summary>
    public const string CommandName = "UnVaultLauncher-CLI";

    private static readonly Lazy<HttpClient> LazyHTTP = new(EpicHTTP.CreateClient);
    private static readonly Lazy<EpicAccount> LazyAccount = new(() => new EpicAccount(new EpicAuthClient(HTTP), SessionStore.Default));
    private static readonly Lazy<EpicAPIClient> LazyAPI = new(() => new EpicAPIClient(HTTP, Account) { ManifestCacheLimit = Settings.ManifestCacheLimit });
    private static readonly Lazy<AppSettings> LazySettings = new(() => AppSettings.Load());
    private static readonly Lazy<BandwidthLimiter> LazyBandwidth = new(() => new BandwidthLimiter { BytesPerSecond = Settings.DownloadSpeedLimit });

    public static HttpClient HTTP => LazyHTTP.Value;
    public static EpicAccount Account => LazyAccount.Value;
    public static EpicAPIClient API => LazyAPI.Value;

    /// <summary>Shared with the GUI: %LOCALAPPDATA%\UnVaultLauncher\settings.json.</summary>
    public static AppSettings Settings => LazySettings.Value;

    /// <summary>The download speed limit from Settings, shared by every download of the command.</summary>
    public static BandwidthLimiter Bandwidth => LazyBandwidth.Value;
}
