using Microsoft.Extensions.Options;
using CtxUi.Core.Settings;
using CtxUi.Core.Routing;

namespace CtxUi.Connect;

public sealed class OAuthGate(ISnapshotSource snapshot, IOptions<PublicEndpointOptions> options)
{
    public bool IsActive => snapshot.Current.SignIn.IsActive(options.Value.Issuer);
}
