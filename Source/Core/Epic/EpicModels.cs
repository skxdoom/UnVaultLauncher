using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnVault.Core.Epic;

/// <summary>A signed-in Epic account session (EG1 tokens), as we persist it.</summary>
public sealed record EpicAuthSession
{
    public required string AccessToken { get; init; }
    public required DateTimeOffset AccessExpiresAt { get; init; }
    public required string RefreshToken { get; init; }
    public required DateTimeOffset RefreshExpiresAt { get; init; }
    public required string AccountID { get; init; }
    public string DisplayName { get; init; } = "";
}

/// <summary>Response of POST /account/api/oauth/token.</summary>
internal sealed class EpicTokenResponse
{
    [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
    [JsonPropertyName("expires_in")] public long ExpiresIn { get; set; }
    [JsonPropertyName("expires_at")] public DateTimeOffset ExpiresAt { get; set; }
    [JsonPropertyName("refresh_token")] public string RefreshToken { get; set; } = "";
    [JsonPropertyName("refresh_expires")] public long RefreshExpiresIn { get; set; }
    [JsonPropertyName("refresh_expires_at")] public DateTimeOffset RefreshExpiresAt { get; set; }
    [JsonPropertyName("account_id")] public string AccountID { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";

    /// <summary>
    /// Expiry counted from now on this PC, from how long the tokens last: compared later with this PC's clock, Epic's
    /// own expiry times would be off by however far this clock is (after dual-booting, say). Those are the fallback.
    /// </summary>
    public EpicAuthSession ToSession(DateTimeOffset now) => new()
    {
        AccessToken = AccessToken,
        AccessExpiresAt = ExpiresIn > 0 ? now.AddSeconds(ExpiresIn) : ExpiresAt,
        RefreshToken = RefreshToken,
        RefreshExpiresAt = RefreshExpiresIn > 0 ? now.AddSeconds(RefreshExpiresIn) : RefreshExpiresAt,
        AccountID = AccountID,
        DisplayName = DisplayName,
    };
}

/// <summary>Epic's standard error body.</summary>
internal sealed class EpicErrorResponse
{
    [JsonPropertyName("errorCode")] public string ErrorCode { get; set; } = "";
    [JsonPropertyName("errorMessage")] public string ErrorMessage { get; set; } = "";
    [JsonPropertyName("numericErrorCode")] public int NumericErrorCode { get; set; }
}

/// <summary>What the authorization-code page returns after sign-in.</summary>
internal sealed class EpicAuthorizationCodeResponse
{
    [JsonPropertyName("authorizationCode")] public string? AuthorizationCode { get; set; }
}

/// <summary>An app the account owns, from the launcher assets list (engines, plugins, games).</summary>
public sealed class EpicAsset
{
    [JsonPropertyName("appName")] public string AppName { get; set; } = "";
    [JsonPropertyName("labelName")] public string LabelName { get; set; } = "";
    [JsonPropertyName("buildVersion")] public string BuildVersion { get; set; } = "";
    [JsonPropertyName("catalogItemId")] public string CatalogItemID { get; set; } = "";
    [JsonPropertyName("namespace")] public string Namespace { get; set; } = "";
    [JsonPropertyName("assetId")] public string AssetID { get; set; } = "";
    [JsonPropertyName("metadata")] public JsonElement? Metadata { get; set; }

    /// <summary>Unreal Engine builds live in the "ue" namespace and are named UE_x.y.</summary>
    [JsonIgnore]
    public bool IsEngine => Namespace == "ue" && AppName.StartsWith("UE_", StringComparison.Ordinal);
}

/// <summary>One build of an app, from the launcher assets v2 endpoint: where to fetch its manifest.</summary>
public sealed class EpicBuildInfo
{
    [JsonPropertyName("appName")] public string AppName { get; set; } = "";
    [JsonPropertyName("labelName")] public string LabelName { get; set; } = "";
    [JsonPropertyName("buildVersion")] public string BuildVersion { get; set; } = "";

    /// <summary>SHA-1 (hex) of the manifest file.</summary>
    [JsonPropertyName("hash")] public string Hash { get; set; } = "";

    [JsonPropertyName("useSignedUrl")] public bool UseSignedURL { get; set; }
    [JsonPropertyName("manifests")] public List<EpicManifestLocation> Manifests { get; set; } = [];

    /// <summary>Feature level 22+: AES keys (hex) by secret GUID, for encrypted manifests/chunks.</summary>
    [JsonPropertyName("secrets")] public Dictionary<string, string>? Secrets { get; set; }
}

public sealed class EpicManifestLocation
{
    [JsonPropertyName("uri")] public string URI { get; set; } = "";
    [JsonPropertyName("queryParams")] public List<EpicQueryParam> QueryParams { get; set; } = [];

    public string GetDownloadURL() =>
        QueryParams.Count == 0
            ? URI
            : URI + "?" + string.Join("&", QueryParams.Select(p => $"{Uri.EscapeDataString(p.Name)}={Uri.EscapeDataString(p.Value)}"));

    /// <summary>The CloudDir the manifest sits in; chunk paths are relative to it.</summary>
    public string GetBaseURL() => URI[..URI.LastIndexOf('/')];
}

public sealed class EpicQueryParam
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("value")] public string Value { get; set; } = "";
}

internal sealed class EpicBuildInfoResponse
{
    [JsonPropertyName("elements")] public List<EpicBuildInfo> Elements { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = false, NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(EpicAuthSession))]
[JsonSerializable(typeof(EpicTokenResponse))]
[JsonSerializable(typeof(EpicErrorResponse))]
[JsonSerializable(typeof(EpicAuthorizationCodeResponse))]
[JsonSerializable(typeof(List<EpicAsset>))]
[JsonSerializable(typeof(EpicBuildInfoResponse))]
[JsonSerializable(typeof(OwnedEnginesSnapshot))]
internal sealed partial class EpicJSONContext : JsonSerializerContext;
