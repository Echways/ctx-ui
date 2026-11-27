using Microsoft.AspNetCore.WebUtilities;
using CtxUi.Connect.Registration;
using CtxUi.Core.Routing;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace CtxUi.Connect.Endpoints;

public sealed record ConnectTarget(AppIdentity App, string InboundName);

public sealed class ConnectTargets(RegisteredApps apps, ISnapshotSource snapshots)
{
    public async Task<ConnectTarget?> FromReturnUrlAsync(string? returnUrl, CancellationToken ct)
    {
        if (returnUrl is null || !returnUrl.StartsWith(AuthorizeEndpoint.Path + "?", StringComparison.Ordinal))
            return null;

        var query = QueryHelpers.ParseQuery(returnUrl[AuthorizeEndpoint.Path.Length..]);
        var snapshot = snapshots.Current;
        return query[Parameters.Resource].Count == 1
               && snapshot.PublicEndpoints.Resolve(snapshot, query[Parameters.Resource][0]) is { } resource
               && await apps.IdentityAsync(query[Parameters.ClientId].ToString(), ct) is { } app
            ? new ConnectTarget(app, resource.Inbound.Name ?? resource.Inbound.Slug)
            : null;
    }
}
