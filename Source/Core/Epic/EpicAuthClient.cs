using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Unvault.Core.Epic;

/// <summary>Talks to Epic's OAuth token endpoint as the Epic Games Launcher client.</summary>
public sealed partial class EpicAuthClient(HttpClient http)
{
    public Task<EpicAuthSession> ExchangeAuthorizationCodeAsync(string authorizationCode, CancellationToken cancellationToken = default) =>
        RequestTokenAsync(
        [
            new("grant_type", "authorization_code"),
            new("code", authorizationCode),
            new("token_type", "eg1"),
        ], cancellationToken);

    public Task<EpicAuthSession> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default) =>
        RequestTokenAsync(
        [
            new("grant_type", "refresh_token"),
            new("refresh_token", refreshToken),
            new("token_type", "eg1"),
        ], cancellationToken);

    /// <summary>Invalidates the session on Epic's side (logout).</summary>
    public async Task KillSessionAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{EpicEndpoints.AccountService}/account/api/oauth/sessions/kill/{accessToken}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await http.SendAsync(request, cancellationToken);
        await EpicAPIException.ThrowIfFailedAsync(response, cancellationToken);
    }

    /// <summary>
    /// Accepts what a user would paste after signing in: the whole JSON the page shows, a redirect URL
    /// containing ?code=…, or the bare code.
    /// </summary>
    public static string ExtractAuthorizationCode(string pasted)
    {
        string text = pasted.Trim().Trim('"');

        if (text.StartsWith('{'))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize(text, EpicJSONContext.Default.EpicAuthorizationCodeResponse);
                if (!string.IsNullOrEmpty(parsed?.AuthorizationCode))
                    return parsed.AuthorizationCode;
            }
            catch (JsonException)
            {
                // Fall through to the regex: partial copies of the JSON are common.
            }
        }

        var match = CodeInTextRegex().Match(text);
        return match.Success ? match.Groups[1].Value : text;
    }

    private async Task<EpicAuthSession> RequestTokenAsync(KeyValuePair<string, string>[] form, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, EpicEndpoints.TokenURL)
        {
            Content = new FormUrlEncodedContent(form),
        };
        string basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{EpicEndpoints.LauncherClientID}:{EpicEndpoints.LauncherClientSecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

        using var response = await http.SendAsync(request, cancellationToken);
        await EpicAPIException.ThrowIfFailedAsync(response, cancellationToken);

        var token = await response.Content.ReadFromJsonAsync(EpicJSONContext.Default.EpicTokenResponse, cancellationToken)
            ?? throw new EpicAPIException("Empty token response.");
        return token.ToSession();
    }

    [GeneratedRegex(@"(?:authorizationCode""?\s*:\s*""|[?&]code=)([0-9a-fA-F]{32})")]
    private static partial Regex CodeInTextRegex();
}
