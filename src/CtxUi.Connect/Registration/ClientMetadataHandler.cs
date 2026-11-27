using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace CtxUi.Connect.Registration;

public sealed partial class ClientMetadataHandler(RegisteredApps apps) :
    IOpenIddictServerHandler<ExtractAuthorizationRequestContext>,
    IOpenIddictServerHandler<ExtractTokenRequestContext>,
    IOpenIddictServerHandler<ExtractRevocationRequestContext>
{
    public static IEnumerable<OpenIddictServerHandlerDescriptor> Descriptors =>
    [
        Order(OpenIddictServerHandlerDescriptor.CreateBuilder<ExtractAuthorizationRequestContext>()
            .UseScopedHandler<ClientMetadataHandler>()),
        Order(OpenIddictServerHandlerDescriptor.CreateBuilder<ExtractTokenRequestContext>()
            .UseScopedHandler<ClientMetadataHandler>()),
        Order(OpenIddictServerHandlerDescriptor.CreateBuilder<ExtractRevocationRequestContext>()
            .UseScopedHandler<ClientMetadataHandler>())
    ];

    public ValueTask HandleAsync(ExtractAuthorizationRequestContext context)
    {
        return EnsureAsync(context, context.Request?.ClientId);
    }

    public ValueTask HandleAsync(ExtractTokenRequestContext context)
    {
        return EnsureAsync(context, context.Request?.ClientId);
    }

    public ValueTask HandleAsync(ExtractRevocationRequestContext context)
    {
        return EnsureAsync(context, context.Request?.ClientId);
    }

    private static OpenIddictServerHandlerDescriptor Order<TContext>(
        OpenIddictServerHandlerDescriptor.Builder<TContext> builder)
        where TContext : BaseContext
    {
        return builder.SetOrder(int.MaxValue - 100_000).SetType(OpenIddictServerHandlerType.Custom).Build();
    }

    private async ValueTask EnsureAsync(BaseValidatingContext context, string? clientId)
    {
        if (!apps.IsDocumentClientId(clientId, out _))
            return;

        try
        {
            if (await apps.EnsureFromDocumentAsync(clientId!, context.CancellationToken))
                return;
        }
        catch (Exception exception) when (exception is OpenIddictExceptions.ValidationException
                                          or OpenIddictExceptions.ConcurrencyException or DbUpdateException)
        {
            RegistrationFailed(context.Logger, exception.GetType().Name);
        }

        context.Reject(Errors.InvalidClient, "the client metadata document could not be fetched or is not valid");
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Client metadata registration or update failed: {FailureType}")]
    private static partial void RegistrationFailed(ILogger logger, string failureType);
}
