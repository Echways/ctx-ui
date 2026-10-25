using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;
using OpenIddict.Server;

namespace CtxUi.Connect.Metadata;

public sealed class ServerMetadataHandler(string issuer) : IOpenIddictServerHandler<HandleConfigurationRequestContext>
{
    public const string RegistrationPath = "/connect/register";

    public static OpenIddictServerHandlerDescriptor Descriptor(string issuer) =>
        OpenIddictServerHandlerDescriptor.CreateBuilder<HandleConfigurationRequestContext>()
            .UseSingletonHandler(new ServerMetadataHandler(issuer))
            .SetOrder(int.MaxValue - 100_000)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    public ValueTask HandleAsync(HandleConfigurationRequestContext context)
    {
        context.Metadata[OpenIddictConstants.Metadata.Issuer] = issuer;
        context.Metadata["registration_endpoint"] = issuer + RegistrationPath;
        context.Metadata["client_id_metadata_document_supported"] = true;
        context.Metadata[OpenIddictConstants.Metadata.AuthorizationResponseIssParameterSupported] = true;
        context.TokenEndpointAuthenticationMethods.Clear();
        context.TokenEndpointAuthenticationMethods.Add(ClientAuthenticationMethods.None);
        context.RevocationEndpointAuthenticationMethods.Clear();
        context.RevocationEndpointAuthenticationMethods.Add(ClientAuthenticationMethods.None);
        return ValueTask.CompletedTask;
    }
}
