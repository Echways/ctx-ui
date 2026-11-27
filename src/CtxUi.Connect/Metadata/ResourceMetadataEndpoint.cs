using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using CtxUi.Core.Inbounds;
using CtxUi.Core.Routing;
using CtxUi.Core.Settings;
using CtxUi.Core.SignIn;

namespace CtxUi.Connect.Metadata;

public sealed record ResourceMetadata(
    [property: JsonPropertyName("resource")]
    string Resource,
    [property: JsonPropertyName("authorization_servers")]
    IReadOnlyList<string> AuthorizationServers,
    [property: JsonPropertyName("scopes_supported")]
    IReadOnlyList<string> ScopesSupported,
    [property: JsonPropertyName("bearer_methods_supported")]
    IReadOnlyList<string> BearerMethodsSupported,
    [property: JsonPropertyName("resource_name")]
    string ResourceName);

public static class ResourceMetadataEndpoint
{
    public const string Path = "/.well-known/oauth-protected-resource/mcp/{slug}";

    public static WebApplication MapResourceMetadata(this WebApplication app)
    {
        app.MapGet(Path, Serve);
        return app;
    }

    private static IResult Serve(string slug, HttpRequest request, ISnapshotSource snapshots,
        IOptions<PublicEndpointOptions> options)
    {
        if (!Slug.IsValid(slug))
            return Results.NotFound();

        var snapshot = snapshots.Current;
        var endpoints = snapshot.PublicEndpoints;
        if (endpoints.ForRequest(request.Scheme, request.Host.Value) is not { } publicUrl ||
            !OAuthAvailability.IsAllowed(publicUrl) || (!endpoints.Legacy && snapshot.Route(slug) is null))
            return Results.NotFound();

        request.HttpContext.Response.Headers.CacheControl = "no-store";
        return Results.Json(new ResourceMetadata(
            OAuthResource.For(publicUrl, slug),
            [options.Value.Issuer],
            [ConnectServices.McpScope],
            ["header"],
            snapshot.Route(slug)?.Name ?? slug));
    }
}
