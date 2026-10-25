using Microsoft.Extensions.Options;
using CtxUi.Core.Routing;

namespace CtxUi.Connect;

public sealed class OAuthGate(ISnapshotSource snapshot, IOptions<ConnectOptions> options)
{
    public bool IsActive => snapshot.Current.SignIn.IsActive(options.Value.PublicUrl);
}
