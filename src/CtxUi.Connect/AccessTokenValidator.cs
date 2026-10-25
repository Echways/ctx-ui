using OpenIddict.Abstractions;
using OpenIddict.Validation;
using CtxUi.Core.SignIn;

namespace CtxUi.Connect;

public sealed class AccessTokenValidator(OpenIddictValidationService validation) : IAccessTokenValidator
{
    public async Task<OAuthGrant?> ValidateAsync(string token, CancellationToken ct)
    {
        try
        {
            var principal = await validation.ValidateAccessTokenAsync(token, ct);
            return Guid.TryParse(principal.GetClaim(OpenIddictConstants.Claims.Subject), out var clientId)
                && Guid.TryParse(principal.GetClaim(ConnectClaims.Device), out var deviceId)
                    ? new OAuthGrant(clientId, deviceId, principal.GetAudiences())
                    : null;
        }
        catch (OpenIddictExceptions.ValidationException)
        {
            return null;
        }
    }
}
