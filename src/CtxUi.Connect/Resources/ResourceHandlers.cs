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
        var resources = request.GetResources();
        return resources.Length == 1
               && OAuthResource.TrySlug(options.Value.PublicUrl, resources[0], out var slug)
               && snapshot.Current.Route(slug) is not null
            ? slug
            : null;
    }
}
