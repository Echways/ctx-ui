using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using CtxUi.Connect.Registration;
using CtxUi.Connect.Resources;
using CtxUi.Core.Data;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace CtxUi.Connect.Endpoints;

public sealed record ConnectTarget(AppIdentity App, string InboundName);

public sealed class ConnectTargets(RegisteredApps apps, ResourceHandlers resources, CtxUiDbContext db)
{
    public async Task<ConnectTarget?> FromReturnUrlAsync(string? returnUrl, CancellationToken ct)
    {
        if (returnUrl is null || !returnUrl.StartsWith(AuthorizeEndpoint.Path + "?", StringComparison.Ordinal))
            return null;

        var query = QueryHelpers.ParseQuery(returnUrl[AuthorizeEndpoint.Path.Length..]);
        return resources.ServedSlugOf(query[Parameters.Resource].ToString()) is { } slug
               && await apps.IdentityAsync(query[Parameters.ClientId].ToString(), ct) is { } app
            ? new ConnectTarget(app, await InboundNameAsync(slug, ct))
            : null;
    }

    public async Task<string> InboundNameAsync(string slug, CancellationToken ct) =>
        await db.Inbounds.AsNoTracking().Where(inbound => inbound.Slug == slug).Select(inbound => inbound.Name).SingleOrDefaultAsync(ct) ?? slug;
}
