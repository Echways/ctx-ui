using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using CtxUi.Core.Clients;
using CtxUi.Core.SignIn;
using CtxUi.Panel;

namespace CtxUi.Server.Api;

public sealed record QuotaView(long Used, DateTimeOffset PeriodStartedAt, DateTimeOffset? NextReset);

public sealed record ClientView(
    Guid Id,
    string Name,
    ClientStatus Status,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? FirstUsedAt,
    DateTimeOffset? LastUsedAt,
    IReadOnlyList<string> Inbounds,
    ClientLimits Limits,
    QuotaView Quota,
    IReadOnlyDictionary<string, string> Metadata,
    string TokenPrefix,
    int? MaxDevices,
    bool DirectKey,
    int ActiveDevices);

public sealed record DeviceView(
    Guid Id,
    string Name,
    IReadOnlyList<string> Apps,
    string SignedInWith,
    string SignInIp,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt,
    DeviceRevocation? Reason);

public sealed record InviteView(string Url, DateTimeOffset ExpiresAt);

public sealed record LinkView(string Provider, string Display, DateTimeOffset LinkedAt);

public sealed record ConnectionAddress(string Inbound, string Url, string UrlWithToken);

public sealed record ConnectionView(string Token, IReadOnlyList<ConnectionAddress> Addresses, string Message, int DevicesRevoked = 0);

public sealed record IssuedClientView(ClientView Client, ConnectionView Connection);

public sealed record NewClient(
    string? Name,
    IReadOnlyList<string>? Inbounds,
    string? Expires = null,
    ClientLimits? Limits = null,
    Dictionary<string, string>? Metadata = null,
    ClientAccessPolicy? Access = null);

public sealed record ClientChange(
    string? Expires = null,
    IReadOnlyList<string>? Inbounds = null,
    ClientLimits? Limits = null,
    Dictionary<string, string>? Metadata = null,
    ClientAccessPolicy? Access = null);

public static class ClientEndpoints
{
    public static void MapClients(this RouteGroupBuilder api)
    {
        var clients = api.MapGroup("/clients").WithTags("clients");
        clients.MapGet("/", ListAsync).WithSummary("All clients; ?name= finds one by name, case-insensitive");
        clients.MapGet("/{id:guid}", GetAsync).WithSummary("One client");
        clients.MapGet("/{id:guid}/connection", ConnectionAsync).WithSummary("Token, addresses and a message for the client");
        clients.MapPost("/", AddAsync).WithSummary("Issue a client").WithDescription(
            "expires: date YYYY-MM-DD (UTC) from which access is closed. An empty field in limits removes that limit.");
        clients.MapPatch("/{id:guid}", ChangeAsync).WithSummary("Change the expiry, inbounds, limits or metadata").WithDescription(
            "A field left out is not changed. expires: YYYY-MM-DD or never. inbounds, limits, metadata and access (maxDevices, directKey) are replaced as a whole.");
        clients.MapPost("/{id:guid}/enable", (Guid id, ClientService service, CancellationToken ct) => SwitchAsync(service, id, true, ct))
            .WithSummary("Restore access");
        clients.MapPost("/{id:guid}/disable", (Guid id, ClientService service, CancellationToken ct) => SwitchAsync(service, id, false, ct))
            .WithSummary("Close access without removing the client");
        clients.MapPost("/{id:guid}/reset-quota", ResetQuotaAsync).WithSummary("Reset the quota counter and start a new period");
        clients.MapPost("/{id:guid}/reissue", ReissueAsync).WithSummary("Issue a new token; the old one stops working");
        clients.MapGet("/{id:guid}/devices", DevicesAsync).WithSummary("Devices of the client, including disconnected ones");
        clients.MapDelete("/{id:guid}/devices/{deviceId:guid}", DisconnectAsync).WithSummary("Disconnect one device");
        clients.MapDelete("/{id:guid}/devices", DisconnectAllAsync).WithSummary("Disconnect every device of the client");
        clients.MapPost("/{id:guid}/invite", InviteAsync).WithSummary("Issue an invite link that binds an outside account to the client")
            .WithDescription("The link works once and expires in 7 days; a new one replaces the previous.");
        clients.MapGet("/{id:guid}/links", LinksAsync).WithSummary("Accounts linked to the client");
        clients.MapDelete("/{id:guid}/links/{provider}", UnlinkAsync).WithSummary("Unlink the accounts of a provider");
        clients.MapDelete("/{id:guid}", RemoveAsync).WithSummary("Remove the client");
    }

    private static async Task<Ok<List<ClientView>>> ListAsync(
        string? name, ClientService clients, DeviceService devices, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var wanted = name is null
            ? await clients.ListAsync(ct)
            : await clients.FindAsync(name, ct) is { } found ? [found] : [];
        var counts = await devices.ActiveCountsAsync(ct);
        return TypedResults.Ok(wanted.Select(client => View(client, now, counts.GetValueOrDefault(client.Id))).ToList());
    }

    private static async Task<Ok<ClientView>> GetAsync(Guid id, ClientService clients, DeviceService devices, TimeProvider clock, CancellationToken ct) =>
        TypedResults.Ok(View(await clients.GetAsync(id, ct), clock.GetUtcNow(), await devices.ActiveCountAsync(id, ct)));

    private static async Task<Results<Ok<ConnectionView>, ProblemHttpResult>> ConnectionAsync(
        Guid id, ClientService clients, SignInSettingsService signIn, IOptions<CtxUiOptions> options, CancellationToken ct)
    {
        var client = await clients.GetAsync(id, ct);
        if (clients.Reveal(client) is not { } token)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                detail: ServerText.TokenLostApi);
        }

        return TypedResults.Ok(Connection(options.Value.PublicUrl, client, token, await ConnectionMode.OfAsync(signIn, client, options.Value.PublicUrl, ct)));
    }

    private static async Task<Created<IssuedClientView>> AddAsync(
        NewClient body,
        ClientService clients,
        SignInSettingsService signIn,
        IOptions<CtxUiOptions> options,
        TimeProvider clock,
        HttpContext http,
        CancellationToken ct)
    {
        var issued = await clients.AddAsync(
            body.Name ?? "",
            body.Inbounds ?? [],
            body.Expires is null ? null : ExpiryArgument.Parse(body.Expires),
            body.Metadata,
            body.Limits,
            body.Access,
            ct);
        var client = await clients.GetAsync(issued.Client.Id, ct);
        return TypedResults.Created(
            $"{http.Request.PathBase}{ApiServices.Prefix}/clients/{client.Id}",
            new IssuedClientView(
                View(client, clock.GetUtcNow(), 0),
                Connection(options.Value.PublicUrl, client, issued.Token, await ConnectionMode.OfAsync(signIn, client, options.Value.PublicUrl, ct))));
    }

    private static async Task<Ok<ClientView>> ChangeAsync(
        Guid id, ClientChange body, ClientService clients, DeviceService devices, TimeProvider clock, CancellationToken ct)
    {
        await clients.UpdateAsync(
            id,
            new ClientUpdate
            {
                ChangeExpiry = body.Expires is not null,
                ExpiresAt = body.Expires is null ? null : ExpiryArgument.Parse(body.Expires),
                InboundSlugs = body.Inbounds,
                Limits = body.Limits,
                Metadata = body.Metadata,
                Access = body.Access,
            },
            ct);
        return TypedResults.Ok(View(await clients.GetAsync(id, ct), clock.GetUtcNow(), await devices.ActiveCountAsync(id, ct)));
    }

    private static async Task<NoContent> SwitchAsync(ClientService clients, Guid id, bool enabled, CancellationToken ct)
    {
        await clients.SetEnabledAsync(id, enabled, ct);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> ResetQuotaAsync(Guid id, ClientService clients, CancellationToken ct)
    {
        await clients.ResetQuotaAsync(id, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<ConnectionView>> ReissueAsync(
        Guid id, ClientService clients, SignInSettingsService signIn, IOptions<CtxUiOptions> options, CancellationToken ct)
    {
        var reissued = await clients.ReissueAsync(id, ct);
        var client = await clients.GetAsync(id, ct);
        var mode = await ConnectionMode.OfAsync(signIn, client, options.Value.PublicUrl, ct);
        return TypedResults.Ok(Connection(options.Value.PublicUrl, client, reissued.Token, mode) with { DevicesRevoked = reissued.DevicesRevoked });
    }

    private static async Task<Ok<List<DeviceView>>> DevicesAsync(Guid id, ClientService clients, DeviceService devices, CancellationToken ct)
    {
        await clients.GetAsync(id, ct);
        var listed = await devices.ListAsync(id, DateTimeOffset.MinValue, ct);
        return TypedResults.Ok(listed.Select(device => new DeviceView(
            device.Id,
            device.Name,
            device.Apps.Select(app => app.AppName).ToList(),
            device.SignedInWith,
            device.SignInIp,
            device.CreatedAt,
            device.LastUsedAt,
            device.RevokedAt,
            device.RevokedReason)).ToList());
    }

    private static async Task<NoContent> DisconnectAsync(Guid id, Guid deviceId, DeviceService devices, CancellationToken ct)
    {
        await devices.RevokeAsync(id, deviceId, DeviceRevocation.Admin, ct);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> DisconnectAllAsync(Guid id, ClientService clients, DeviceService devices, CancellationToken ct)
    {
        await clients.GetAsync(id, ct);
        await devices.RevokeAllAsync(id, DeviceRevocation.Admin, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<InviteView>> InviteAsync(
        Guid id, ClientLinkService links, IOptions<CtxUiOptions> options, TimeProvider clock, CancellationToken ct)
    {
        var code = await links.InviteAsync(id, ct);
        return TypedResults.Ok(new InviteView($"{OAuthResource.Issuer(options.Value.PublicUrl)}/connect/invite/{code}", clock.GetUtcNow() + InviteCode.Lifetime));
    }

    private static async Task<Ok<List<LinkView>>> LinksAsync(Guid id, ClientService clients, ClientLinkService links, CancellationToken ct)
    {
        await clients.GetAsync(id, ct);
        return TypedResults.Ok((await links.ListAsync(id, ct)).Select(link => new LinkView(link.Provider, link.Display, link.LinkedAt)).ToList());
    }

    private static async Task<NoContent> UnlinkAsync(Guid id, string provider, ClientLinkService links, CancellationToken ct)
    {
        await links.UnlinkAsync(id, provider, ct);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> RemoveAsync(Guid id, ClientService clients, CancellationToken ct)
    {
        await clients.RemoveAsync(id, ct);
        return TypedResults.NoContent();
    }

    private static ClientView View(Client client, DateTimeOffset now, int activeDevices) => new(
        client.Id,
        client.Name,
        ClientState.Of(client, now),
        client.Enabled,
        client.CreatedAt,
        client.ExpiresAt,
        client.FirstUsedAt,
        client.LastUsedAt,
        ConnectionText.Slugs(client),
        ClientLimits.Of(client),
        new QuotaView(
            QuotaPeriod.UsedCalls(client, now),
            QuotaPeriod.StartAt(client.PeriodStartedAt, client.ResetEveryDays, now),
            QuotaPeriod.NextReset(client.PeriodStartedAt, client.ResetEveryDays, now)),
        client.Metadata,
        client.TokenPrefix,
        client.MaxDevices,
        client.DirectKey,
        activeDevices);

    private static ConnectionView Connection(string publicUrl, Client client, string token, ConnectionMode mode) => new(
        token,
        ConnectionText.Slugs(client)
            .Select(slug => new ConnectionAddress(
                slug, ConnectionText.Address(publicUrl, slug), ConnectionText.AddressWithToken(publicUrl, token, slug)))
            .ToList(),
        ConnectionText.Message(publicUrl, client, token, mode));
}
