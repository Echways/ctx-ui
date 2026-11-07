using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using CtxUi.Core.Routing;
using CtxUi.Core.SignIn;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace CtxUi.Connect.Resources;

public sealed class ResourceHandlers(ISnapshotSource snapshot, IOptions<ConnectOptions> options) :
    IOpenIddictServerHandler<ValidateTokenRequestContext>
{
    public const string Refusal = "resource must be the address of one enabled inbound: <public address>/mcp/<inbound>";

    public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
        OpenIddictServerHandlerDescriptor.CreateBuilder<ValidateTokenRequestContext>()
            .UseSingletonHandler<ResourceHandlers>()
            .SetOrder(OpenIddictServerHandlers.Exchange.ValidateAuthentication.Descriptor.Order - 500)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    public ValueTask HandleAsync(ValidateTokenRequestContext context)
    {
        if (SlugOf(context.Request) is null)
            context.Reject(Errors.InvalidTarget, Refusal);

        return ValueTask.CompletedTask;
    }

    public string? SlugOf(OpenIddictRequest request)
    {
        return request.GetResources() is [var resource] ? SlugOf(resource) : null;
    }

    public string? ServedSlugOf(OpenIddictRequest request)
    {
        return Served(SlugOf(request));
    }

    public string? ServedSlugOf(string? resource)
    {
        return Served(SlugOf(resource));
    }

    private string? SlugOf(string? resource)
    {
        return OAuthResource.TrySlug(options.Value.PublicUrl, resource, out var slug) ? slug : null;
    }

    private string? Served(string? slug)
    {
        return slug is not null && snapshot.Current.Route(slug) is not null ? slug : null;
    }

    public string Resource(string slug)
    {
        return OAuthResource.For(options.Value.PublicUrl, slug);
    }
}
