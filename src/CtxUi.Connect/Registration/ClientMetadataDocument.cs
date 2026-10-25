using System.Net.Http.Headers;
using System.Text.Json;

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

    public static ClientMetadataDocument? Parse(Uri url, ReadOnlySpan<byte> json, CacheControlHeaderValue? cache)
    {
        try
        {
            using var document = JsonDocument.Parse(json.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || Text(root, "client_id") != url.OriginalString
                || Text(root, "client_name") is not { } name
                || string.IsNullOrWhiteSpace(name)
                || !root.TryGetProperty("redirect_uris", out var redirects)
                || redirects.ValueKind != JsonValueKind.Array)
                return null;

            var uris = new List<string>();
            foreach (var redirect in redirects.EnumerateArray())
            {
                if (redirect.ValueKind != JsonValueKind.String || !Registration.RedirectUris.TryParse(redirect.GetString(), out _))
                    return null;

                uris.Add(redirect.GetString()!);
            }

            var method = Text(root, "token_endpoint_auth_method");
            if (uris.Count == 0 || method is not (null or "none"))
                return null;

            return new ClientMetadataDocument(url.OriginalString, name.Trim(), uris, method, CacheLifetime(cache));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static TimeSpan CacheLifetime(CacheControlHeaderValue? cache)
    {
        var maxAge = cache is { NoStore: false, NoCache: false } ? cache.MaxAge ?? ShortestCache : ShortestCache;
        return maxAge < ShortestCache ? ShortestCache : maxAge > LongestCache ? LongestCache : maxAge;
    }
}
