using CtxUi.Core.Inbounds;
using CtxUi.Core.Settings;

namespace CtxUi.Core.SignIn;

public static class OAuthResource
{
    private const string McpPath = "/mcp/";
    private const string MetadataPath = "/.well-known/oauth-protected-resource";

    public static string Issuer(string publicUrl)
    {
        return publicUrl.TrimEnd('/');
    }

    public static string For(string publicUrl, string slug)
    {
        return Issuer(publicUrl) + McpPath + slug;
    }

    public static string MetadataUrl(string publicUrl, string slug)
    {
        return Issuer(publicUrl) + MetadataPath + McpPath + slug;
    }

    public static bool TrySlug(string publicUrl, string? requested, out string slug)
    {
        slug = "";
        return PublicAddress.Parse(requested) is { } resource &&
               PublicAddress.Parse(publicUrl) is { } address &&
               PublicAddress.Authority(resource) == PublicAddress.Authority(address) &&
               TryPath(publicUrl, requested!, out slug);
    }

    internal static bool TryPath(string publicUrl, string requested, out string slug)
    {
        slug = "";
        var prefix = PublicAddress.PathOf(publicUrl).TrimEnd('/') + McpPath;
        var path = PublicAddress.PathOf(requested);
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var rest = path[prefix.Length..];
        if (rest.EndsWith('/'))
            rest = rest[..^1];
        if (!Slug.IsValid(rest))
            return false;

        slug = rest;
        return true;
    }
}
