using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using CtxUi.Connect.Endpoints;
using CtxUi.Connect.Pages;
using CtxUi.Connect.SignIn;
using CtxUi.Core.Routing;
using CtxUi.Core.SignIn;

namespace CtxUi.Connect.External;

public static class ExternalEndpoints
{
    public const string ExternalPath = ConnectServices.Prefix + "/external";
    public const string DonePath = ExternalPath + "/done";
    public const string FailedPath = ExternalPath + "/failed";
    public const string InvitePath = ConnectServices.Prefix + "/invite";

    public static IEndpointRouteBuilder MapExternal(this IEndpointRouteBuilder app)
    {
        app.MapGet(ExternalPath + "/{provider}", ChallengeAsync);
        app.MapGet(DonePath, DoneAsync);
        app.MapGet(FailedPath, () => ConnectResults.Notice(ConnectText.ExternalFailed));
        app.MapGet(InvitePath + "/{code}", InviteAsync).RequireRateLimiting(PageEndpoints.SignInPolicy);
        return app;
    }

    public static string ChallengeUrl(HttpContext http, string provider, string? returnUrl = null, string? invite = null) =>
        http.Request.PathBase + ExternalPath + "/" + provider + QueryString.Create(Query(returnUrl, invite));

    private static IResult ChallengeAsync(
        HttpContext http,
        string provider,
        string? returnUrl,
        string? invite,
        ISnapshotSource snapshot,
        ProviderOptionsCache options)
    {
        if (ProviderCatalog.Find(provider) is not { } info || snapshot.Current.SignIn.Providers.All(configured => configured.Name != info.Name))
            return Results.NotFound();

        options.Refresh(info.Name);
        var properties = new AuthenticationProperties
        {
            RedirectUri = http.Request.PathBase + DonePath + QueryString.Create(Query(returnUrl, invite)),
            Items = { [ProviderSchemes.ProviderItem] = info.Name },
        };
        return Results.Challenge(properties, [info.Name]);
    }

    private static async Task<IResult> DoneAsync(
        HttpContext http,
        string? returnUrl,
        string? invite,
        ISnapshotSource snapshot,
        ClientLinkService links,
        TimeProvider clock)
    {
        var external = await http.AuthenticateAsync(ProviderSchemes.ExternalScheme);
        await http.SignOutAsync(ProviderSchemes.ExternalScheme);
        if (external.Principal is not { } principal
            || external.Properties?.GetString(ProviderSchemes.ProviderItem) is not { } provider
            || ExternalIdentityReader.Read(provider, principal) is not { } identity)
            return ConnectResults.Notice(ConnectText.ExternalFailed);

        Guid clientId;
        if (!string.IsNullOrEmpty(invite))
        {
            var linked = await links.LinkAsync(invite, identity, http.RequestAborted);
            switch (linked.Outcome)
            {
                case LinkOutcome.InviteInvalid:
                    return ConnectResults.Notice(ConnectText.InviteInvalid);
                case LinkOutcome.TakenByOtherClient:
                    return ConnectResults.Notice(ConnectText.AccountLinkedElsewhere);
            }

            clientId = linked.ClientId!.Value;
        }
        else if (await links.ClientOfAsync(identity, http.RequestAborted) is { } known)
        {
            clientId = known;
        }
        else
        {
            return ConnectResults.Notice(ConnectText.AccountNotLinked);
        }

        if (snapshot.Current.ClientById(clientId) is not { } client)
            return ConnectResults.Notice(ConnectText.AccountNotLinked);
        if (!client.Enabled)
            return ConnectResults.Notice(ConnectText.ClientDisabled);
        if (client.ExpiresAt is { } expiresAt && clock.GetUtcNow() >= expiresAt)
            return ConnectResults.Notice(ConnectText.ClientExpired);

        await ConnectSession.SignInAsync(http, clientId, provider);
        return string.IsNullOrEmpty(invite)
            ? ConnectResults.Back(http, ConnectLanguage.LocalPath(returnUrl, ConnectResults.DevicesPath))
            : ConnectResults.Page<DonePage>(new { ClientName = client.Name });
    }

    private static async Task<IResult> InviteAsync(
        HttpContext http,
        string code,
        ISnapshotSource snapshot,
        ClientLinkService links)
    {
        if (await links.InvitedClientAsync(code, http.RequestAborted) is not { } clientId
            || snapshot.Current.ClientById(clientId) is not { } client)
            return ConnectResults.Notice(ConnectText.InviteInvalid);

        return ConnectResults.Page<InvitePage>(new
        {
            ClientName = client.Name,
            Code = code,
            Providers = ProviderButton.All(snapshot.Current.SignIn),
        });
    }

    private static List<KeyValuePair<string, string?>> Query(string? returnUrl, string? invite)
    {
        var query = new List<KeyValuePair<string, string?>>();
        if (!string.IsNullOrEmpty(returnUrl))
            query.Add(KeyValuePair.Create("returnUrl", (string?)returnUrl));
        if (!string.IsNullOrEmpty(invite))
            query.Add(KeyValuePair.Create("invite", (string?)invite));

        return query;
    }
}
