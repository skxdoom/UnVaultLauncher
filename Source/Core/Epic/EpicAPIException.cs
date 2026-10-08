using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UnVault.Core.Epic;

public partial class EpicAPIException(string message, HttpStatusCode? statusCode = null, string errorCode = "")
    : Exception(message)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;

    /// <summary>Epic's error id, e.g. errors.com.epicgames.account.oauth.authorization_code_not_found.</summary>
    public string ErrorCode { get; } = errorCode;

    internal static async Task ThrowIfFailedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        string message = $"{(int)response.StatusCode} {response.ReasonPhrase} from {DescribeEndpoint(response.RequestMessage?.RequestUri)}";
        string errorCode = "";

        try
        {
            if (JsonSerializer.Deserialize(body, EpicJSONContext.Default.EpicErrorResponse) is { ErrorCode.Length: > 0 } error)
            {
                errorCode = error.ErrorCode;
                message = $"{MaskSecrets(error.ErrorMessage)} ({error.ErrorCode})";
            }
        }
        catch (JsonException)
        {
            // Not Epic's JSON error shape (e.g. a CDN error page); keep the status line.
        }

        throw new EpicAPIException(message, response.StatusCode, errorCode);
    }

    /// <summary>
    /// Epic's error messages can quote what was sent ("Sorry the refresh token '…' is invalid"), and messages end up on
    /// screen and in crash.log: anything that looks like a token or a code is left out.
    /// </summary>
    internal static string MaskSecrets(string message) => SecretRegex().Replace(message, "…");

    /// <summary>eg1~ tokens, and long runs of hex or base64-like characters (authorization codes, other tokens).</summary>
    [GeneratedRegex(@"eg1~[^\s'""]+|[A-Za-z0-9+/_=-]{32,}")]
    private static partial Regex SecretRegex();

    /// <summary>
    /// The endpoint for an error message, without query and with token-like path segments masked: Epic's logout
    /// puts the access token in the path (…/sessions/kill/{token}), and messages end up on screen and in crash.log.
    /// </summary>
    internal static string DescribeEndpoint(Uri? uri)
    {
        if (uri is null)
            return "Epic";
        var segments = uri.AbsolutePath.Split('/')
            .Select(s => s.StartsWith("eg1~", StringComparison.Ordinal) || s.Length > 64 ? "…" : s);
        return uri.GetLeftPart(UriPartial.Authority) + string.Join('/', segments);
    }
}

public sealed class NotLoggedInException(string message = "Not signed in.") : EpicAPIException(message);
