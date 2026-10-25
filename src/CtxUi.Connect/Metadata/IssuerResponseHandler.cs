using OpenIddict.Server;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace CtxUi.Connect.Metadata;

public sealed class IssuerResponseHandler(string issuer) : IOpenIddictServerHandler<ApplyAuthorizationResponseContext>
{
    public static OpenIddictServerHandlerDescriptor Descriptor(string issuer) =>
        OpenIddictServerHandlerDescriptor.CreateBuilder<ApplyAuthorizationResponseContext>()
            .UseSingletonHandler(new IssuerResponseHandler(issuer))
            .SetOrder(OpenIddictServerHandlers.Authentication.AttachIssuer.Descriptor.Order + 1)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    public ValueTask HandleAsync(ApplyAuthorizationResponseContext context)
    {
        context.Response.Iss = issuer;
        return ValueTask.CompletedTask;
    }
}
