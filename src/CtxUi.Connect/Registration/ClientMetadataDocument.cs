using System.Net.Http.Headers;
using System.Text.Json;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace CtxUi.Connect.Registration;

public sealed record ClientMetadataDocument(
    string ClientId,
    string ClientName,
    IReadOnlyList<string> RedirectUris,
    string? TokenEndpointAuthMethod,
    TimeSpan CacheFor)
{
    public static readonly TimeSpan ShortestCache = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan LongestCache = TimeSpan.FromDays(1);

    internal bool AllowsRefreshToken { get; init; }

    public static ClientMetadataDocument? Parse(Uri url, ReadOnlySpan<byte> json, CacheControlHeaderValue? cache)
    {
        return Parse(url, json, cache, out _);
    }

    internal static ClientMetadataDocument? Parse(Uri url, ReadOnlySpan<byte> json, CacheControlHeaderValue? cache,
        out string? rejection)
    {
        try
        {
            rejection = "invalid JSON document";
            using var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions
            {
                AllowDuplicateProperties = false
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            rejection = "client ID mismatch";
            if (Text(root, "client_id") != url.OriginalString)
                return null;

            rejection = "missing or invalid client name";
            if (AppNames.Clean(Text(root, "client_name")) is not { Length: > 0 } name)
                return null;

            rejection = "missing or invalid redirect URIs";
            if (!root.TryGetProperty("redirect_uris", out var redirects)
                || redirects.ValueKind != JsonValueKind.Array)
                return null;

            var uris = new List<string>();
            foreach (var redirect in redirects.EnumerateArray())
            {
                if (redirect.ValueKind != JsonValueKind.String
                    || redirect.GetString() is not { } uri
                    || !Registration.RedirectUris.IsAllowedForRegistration(uri))
                    return null;

                uris.Add(uri);
            }

            if (uris.Count == 0)
                return null;

            rejection = CheckAuthenticationMethods(root);
            if (rejection is not null)
                return null;

            rejection = "invalid or unsupported grant or response types";
            if (!ReadChoices(root, "grant_types", out var grants)
                || grants is not null && !grants.Contains(GrantTypes.AuthorizationCode)
                || !ReadChoices(root, "response_types", out var responses)
                || responses is not null && !responses.Contains(ResponseTypes.Code))
                return null;

            rejection = null;
            return new ClientMetadataDocument(url.OriginalString, name, uris, ClientAuthenticationMethods.None,
                CacheLifetime(cache))
            {
                AllowsRefreshToken = grants is null || grants.Contains(GrantTypes.RefreshToken)
            };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            rejection = "invalid JSON document";
            return null;
        }
    }

    private static string? CheckAuthenticationMethods(JsonElement root)
    {
        var preferred = Text(root, "token_endpoint_auth_method");
        if ((root.TryGetProperty("token_endpoint_auth_method", out _) && string.IsNullOrWhiteSpace(preferred))
            || !ReadChoices(root, "token_endpoint_auth_methods_supported", out var methods)
            || methods is not null && preferred is not null && !methods.Contains(preferred))
            return "malformed authentication method declaration";

        methods ??= [preferred ?? ClientAuthenticationMethods.None];
        if (methods.Overlaps([ClientAuthenticationMethods.ClientSecretBasic, ClientAuthenticationMethods.ClientSecretPost,
                ClientAuthenticationMethods.ClientSecretJwt])
            || root.TryGetProperty("client_secret", out _) || root.TryGetProperty("client_secret_expires_at", out _))
            return "shared-secret authentication is prohibited in client metadata documents";

        return methods.Contains(ClientAuthenticationMethods.None) ? null : "no mutually supported authentication method";
    }

    private static bool ReadChoices(JsonElement root, string name, out HashSet<string>? choices)
    {
        choices = null;
        if (!root.TryGetProperty(name, out var values))
            return true;
        if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() == 0)
            return false;

        choices = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text
                                                        || string.IsNullOrWhiteSpace(text))
                return false;

            choices.Add(text);
        }

        return true;
    }

    private static string? Text(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static TimeSpan CacheLifetime(CacheControlHeaderValue? cache)
    {
        var maxAge = cache is { NoStore: false, NoCache: false } ? cache.MaxAge ?? ShortestCache : ShortestCache;
        return maxAge < ShortestCache ? ShortestCache : maxAge > LongestCache ? LongestCache : maxAge;
    }
}
