using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using CtxUi.Core.Data;
using CtxUi.Core.Inbounds;
using CtxUi.Core.SignIn;

namespace CtxUi.Connect.Metadata;

public sealed record ResourceMetadata(
    [property: JsonPropertyName("resource")] string Resource,
    [property: JsonPropertyName("authorization_servers")] IReadOnlyList<string> AuthorizationServers,
    [property: JsonPropertyName("scopes_supported")] IReadOnlyList<string> ScopesSupported,
    [property: JsonPropertyName("bearer_methods_supported")] IReadOnlyList<string> BearerMethodsSupported,
    [property: JsonPropertyName("resource_name")] string ResourceName);

public static class ResourceMetadataEndpoint
{
    public const string Path = "/.well-known/oauth-protected-resource/mcp/{slug}";

    public static WebApplication MapResourceMetadata(this WebApplication app)
    {
        app.MapGet(Path, ServeAsync);
        return app;
    }

    private static async Task<IResult> ServeAsync(string slug, CtxUiDbContext db, IOptions<ConnectOptions> options, CancellationToken ct)
    {
        if (!Slug.IsValid(slug))
            return Results.NotFound();

        var publicUrl = options.Value.PublicUrl;
        var name = await db.Inbounds.AsNoTracking()
            .Where(inbound => inbound.Slug == slug && inbound.Enabled)
            .Select(inbound => inbound.Name)
            .SingleOrDefaultAsync(ct);
        return Results.Json(new ResourceMetadata(
            OAuthResource.For(publicUrl, slug),
            [OAuthResource.Issuer(publicUrl)],
            [ConnectServices.McpScope],
            ["header"],
            name ?? slug));
    }
}
