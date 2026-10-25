using CtxUi.Core.Routing;

namespace CtxUi.Connect.SignIn;

public sealed record KeySignInResult(Guid? ClientId, string? Refusal);

public static class KeySignIn
{
    public static KeySignInResult Find(RoutingSnapshot snapshot, string? key, DateTimeOffset now) =>
        snapshot.ClientByToken(key?.Trim()) switch
        {
            null => new KeySignInResult(null, ConnectText.KeyUnknown),
            { Enabled: false } => new KeySignInResult(null, ConnectText.ClientDisabled),
            { ExpiresAt: { } expiresAt } when now >= expiresAt => new KeySignInResult(null, ConnectText.ClientExpired),
            var client => new KeySignInResult(client.Id, null),
        };
}
