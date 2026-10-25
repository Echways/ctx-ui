using Microsoft.AspNetCore.Http;
using CtxUi.Connect.SignIn;
using CtxUi.Core.Routing;
using CtxUi.Core.SignIn;

namespace CtxUi.Connect.Endpoints;

public sealed record SignedIn(ClientAccess Client, string SignedInWith)
{
    public static async Task<SignedIn?> ReadAsync(HttpContext http, ISnapshotSource snapshot)
    {
        if (await ConnectSession.ReadAsync(http) is not { } session)
            return null;

        if (snapshot.Current.ClientById(session.ClientId) is { } client)
            return new SignedIn(client, session.SignedInWith);

        await ConnectSession.SignOutAsync(http);
        return null;
    }

    public DeviceSeen Seen(HttpContext http) => new(
        DeviceNames.From(http.Request.Headers.UserAgent.ToString()),
        http.Connection.RemoteIpAddress?.ToString() ?? "",
        SignedInWith);
}
