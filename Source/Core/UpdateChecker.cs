using System.Text.Json;

namespace UnVault.Core;

/// <summary>A published release newer than this build.</summary>
public sealed record NewerRelease(string Version, string PageURL);

/// <summary>Asks GitHub whether a newer UnVault Launcher has been released. Only reads; it never downloads the app.</summary>
public sealed class UpdateChecker(HttpClient http)
{
    public const string Repository = "skxdoom/UnVaultLauncher";
    public const string RepositoryURL = "https://github.com/" + Repository;

    /// <summary>
    /// The latest published release (drafts and pre-releases aren't "latest"), if it's newer than <paramref name="currentVersion"/>;
    /// null when this build is current. Fails with <see cref="HttpRequestException"/>, or a cancellation on a timeout.
    /// </summary>
    public async Task<NewerRelease?> FindNewerAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        // GitHub wants a user agent; this app's own, not the Epic Games Launcher's the shared client sends to Epic.
        request.Headers.UserAgent.ParseAdd($"UnVaultLauncher/{currentVersion}");

        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        string tag, page;
        try
        {
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            tag = json.RootElement.GetProperty("tag_name").GetString() ?? "";
            page = json.RootElement.TryGetProperty("html_url", out var url) && url.GetString() is { Length: > 0 } link ? link : RepositoryURL + "/releases/latest";
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new HttpRequestException("GitHub's answer couldn't be read.", ex);
        }

        // Tags are "v0.5.0"; a tag that isn't a version says nothing about being newer.
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest) || !Version.TryParse(currentVersion, out var current))
            return null;
        return latest > current ? new NewerRelease(latest.ToString(), page) : null;
    }
}
