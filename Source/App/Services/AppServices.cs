using System.Net.Http;
using UnVault.Core;
using UnVault.Core.Epic;
using UnVault.Core.Util;

namespace UnVault.App.Services;

/// <summary>Process-wide singletons: one HTTP client, the signed-in account, the API client, the download speed cap.</summary>
public sealed class AppServices
{
    public AppServices() : this(EpicHTTP.CreateClient(), SessionStore.Default)
    {
    }

    /// <summary>Tests give their own connection to Epic, and their own saved session.</summary>
    internal AppServices(HttpClient http, SessionStore sessions)
    {
        HTTP = http;
        Account = new EpicAccount(new EpicAuthClient(HTTP), sessions);
        API = new EpicAPIClient(HTTP, Account);
        Thumbnails = new ThumbnailCache(HTTP);
        Updates = new UpdateChecker(HTTP);
    }

    public HttpClient HTTP { get; }
    public EpicAccount Account { get; }
    public EpicAPIClient API { get; }
    public ThumbnailCache Thumbnails { get; }
    public UpdateChecker Updates { get; }

    /// <summary>Shared by every download, so the limit in Settings is for all of them together.</summary>
    public BandwidthLimiter Bandwidth { get; } = new();
}

/// <summary>Things view models need a window for. Implemented by the main window; faked in tests.</summary>
public interface IUserInteraction
{
    /// <summary>Lets the user sign in to Epic; returns the authorization code, or null if they gave up.</summary>
    Task<string?> SignInAsync();

    /// <summary>Clears the sign-in browser's cookies so the next sign-in can use another account. Returns whether they're gone.</summary>
    Task<bool> ForgetSignInAsync();

    Task<string?> PickFolderAsync(string title, string? startPath);

    /// <summary>Picks one file matching <paramref name="pattern"/> (e.g. "*.uproject"); null if cancelled.</summary>
    Task<string?> PickFileAsync(string title, string? startPath, string fileTypeName, string pattern);
}
