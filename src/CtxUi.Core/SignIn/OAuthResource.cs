using CtxUi.Core.Inbounds;

namespace CtxUi.Core.SignIn;

public static class OAuthResource
{
    private const string McpPath = "/mcp/";
    private const string MetadataPath = "/.well-known/oauth-protected-resource";

    public static string Issuer(string publicUrl) => publicUrl.TrimEnd('/');

    public static string For(string publicUrl, string slug) => Issuer(publicUrl) + McpPath + slug;

    public static string MetadataUrl(string publicUrl, string slug) => Issuer(publicUrl) + MetadataPath + McpPath + slug;

    public static bool TrySlug(string publicUrl, string? requested, out string slug)
    {
        slug = "";
        if (!Uri.TryCreate(requested, UriKind.Absolute, out var resource)
            || !Uri.TryCreate(Issuer(publicUrl), UriKind.Absolute, out var issuer)
            || resource.Query.Length > 0
            || resource.Fragment.Length > 0
            || !string.Equals(resource.GetLeftPart(UriPartial.Authority), issuer.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
            return false;

        var prefix = issuer.AbsolutePath.TrimEnd('/') + McpPath;
        var path = resource.AbsolutePath;
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
