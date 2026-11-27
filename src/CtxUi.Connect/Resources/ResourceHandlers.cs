using OpenIddict.Abstractions;
using OpenIddict.Server;
using CtxUi.Core.Routing;
using CtxUi.Core.Settings;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace CtxUi.Connect.Resources;

public sealed class ResourceHandlers(ISnapshotSource snapshots) :
    IOpenIddictServerHandler<ValidateTokenRequestContext>
{
    public const string Refusal = "resource must be the address of one enabled inbound on a configured public URL";

    public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
        OpenIddictServerHandlerDescriptor.CreateBuilder<ValidateTokenRequestContext>()
            .UseSingletonHandler<ResourceHandlers>()
            .SetOrder(OpenIddictServerHandlers.Exchange.ValidateAuthentication.Descriptor.Order - 500)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    public ValueTask HandleAsync(ValidateTokenRequestContext context)
    {
        if (Match(snapshots.Current, context.Request) is null)
            context.Reject(Errors.InvalidTarget, Refusal);

        return ValueTask.CompletedTask;
    }

    public static (string Url, string Slug)? Match(RoutingSnapshot snapshot, OpenIddictRequest request)
    {
        return request.GetResources() is [var resource] ? snapshot.PublicEndpoints.Match(resource) : null;
    }

    public static PublicResource? Resolve(RoutingSnapshot snapshot, OpenIddictRequest request)
    {
        return request.GetResources() is [var resource] ? snapshot.PublicEndpoints.Resolve(snapshot, resource) : null;
    }
}
