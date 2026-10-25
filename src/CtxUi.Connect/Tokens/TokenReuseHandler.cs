using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using CtxUi.Core.Routing;
using CtxUi.Core.SignIn;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace CtxUi.Connect.Tokens;

public sealed class TokenReuseHandler(
    IOpenIddictTokenManager tokens,
    DeviceService devices,
    ISnapshotSource snapshot,
    IOptionsMonitor<OpenIddictServerOptions> server,
    TimeProvider clock) : IOpenIddictServerHandler<ValidateTokenContext>
{
    public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
        OpenIddictServerHandlerDescriptor.CreateBuilder<ValidateTokenContext>()
            .UseScopedHandler<TokenReuseHandler>()
            .SetOrder(OpenIddictServerHandlers.Protection.ValidateTokenEntry.Descriptor.Order - 1)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    public async ValueTask HandleAsync(ValidateTokenContext context)
    {
        var ct = context.CancellationToken;
        if (context.Principal is not { } principal
            || principal.GetTokenType() != TokenTypeIdentifiers.RefreshToken
            || principal.GetTokenId() is not { } tokenId
            || !Guid.TryParse(principal.GetClaim(Claims.Subject), out var clientId)
            || !Guid.TryParse(principal.GetClaim(ConnectClaims.Device), out var deviceId)
            || await tokens.FindByIdAsync(tokenId, ct) is not { } token
            || await tokens.HasStatusAsync(token, Statuses.Redeemed, ct) is false
            || await tokens.GetRedemptionDateAsync(token, ct) is not { } redeemed
            || clock.GetUtcNow() < redeemed + server.CurrentValue.RefreshTokenReuseLeeway)
            return;

        await devices.RevokeAsync(clientId, deviceId, DeviceRevocation.TokenReused, CancellationToken.None);
        await snapshot.RefreshAsync(CancellationToken.None);
    }
}
