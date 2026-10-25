using CtxUi.Core.Clients;
using CtxUi.Core.SignIn;

namespace CtxUi.Panel;

public sealed record ConnectionMode(bool OAuth, bool KeySignIn, bool DirectKey)
{
    public static ConnectionMode Off { get; } = new(false, true, true);

    public bool ShowsToken => !OAuth || DirectKey || KeySignIn;

    public static async Task<ConnectionMode> OfAsync(SignInSettingsService settings, Client client, string publicUrl, CancellationToken ct = default)
    {
        var current = await settings.GetAsync(ct);
        return new ConnectionMode(current.Enabled && OAuthAvailability.IsAllowed(publicUrl), current.KeySignIn, client.DirectKey);
    }
}
