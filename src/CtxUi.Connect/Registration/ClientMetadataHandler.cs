using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace CtxUi.Connect.Registration;

public sealed class ClientMetadataHandler(RegisteredApps apps) :
    IOpenIddictServerHandler<ExtractAuthorizationRequestContext>,
    IOpenIddictServerHandler<ExtractTokenRequestContext>,
    IOpenIddictServerHandler<ExtractRevocationRequestContext>
{
    public static IEnumerable<OpenIddictServerHandlerDescriptor> Descriptors =>
    [
        Order(OpenIddictServerHandlerDescriptor.CreateBuilder<ExtractAuthorizationRequestContext>().UseScopedHandler<ClientMetadataHandler>()),
        Order(OpenIddictServerHandlerDescriptor.CreateBuilder<ExtractTokenRequestContext>().UseScopedHandler<ClientMetadataHandler>()),
        Order(OpenIddictServerHandlerDescriptor.CreateBuilder<ExtractRevocationRequestContext>().UseScopedHandler<ClientMetadataHandler>()),
    ];

    public ValueTask HandleAsync(ExtractAuthorizationRequestContext context) => EnsureAsync(context, context.Request?.ClientId);

    public ValueTask HandleAsync(ExtractTokenRequestContext context) => EnsureAsync(context, context.Request?.ClientId);

    public ValueTask HandleAsync(ExtractRevocationRequestContext context) => EnsureAsync(context, context.Request?.ClientId);

    private static OpenIddictServerHandlerDescriptor Order<TContext>(OpenIddictServerHandlerDescriptor.Builder<TContext> builder)
        where TContext : BaseContext =>
        builder.SetOrder(int.MaxValue - 100_000).SetType(OpenIddictServerHandlerType.Custom).Build();

    private async ValueTask EnsureAsync(BaseValidatingContext context, string? clientId)
    {
        if (!apps.IsDocumentClientId(clientId, out _))
            return;

        if (!await apps.EnsureFromDocumentAsync(clientId!, context.CancellationToken))
            context.Reject(Errors.InvalidClient, "the client metadata document could not be fetched or is not valid");
    }
}
