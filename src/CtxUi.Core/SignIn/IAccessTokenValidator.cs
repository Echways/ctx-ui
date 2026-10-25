namespace CtxUi.Core.SignIn;

public sealed record OAuthGrant(Guid ClientId, Guid DeviceId, IReadOnlyCollection<string> Audiences);

public interface IAccessTokenValidator
{
    Task<OAuthGrant?> ValidateAsync(string token, CancellationToken ct);
}
