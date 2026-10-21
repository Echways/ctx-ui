using Microsoft.AspNetCore.Http.HttpResults;
using CtxUi.Core.Inbounds;

namespace CtxUi.Server.Api;

public sealed record InboundView(
    string Slug,
    string Name,
    bool Enabled,
    string? Url,
    bool ForwardIdentity,
    int RequestTimeoutSeconds,
    int MaxBodyBytes,
    IReadOnlyList<string>? UpstreamHeaderNames,
    int Clients,
    DateTimeOffset CreatedAt);

public sealed record InboundBody(
    string? Url,
    string? Name = null,
    bool ForwardIdentity = false,
    int? RequestTimeoutSeconds = null,
    int? MaxBodyBytes = null,
    Dictionary<string, string>? UpstreamHeaders = null)
{
    public InboundSettings Settings() => new()
    {
        Name = Name,
        Url = Url ?? "",
        ForwardIdentity = ForwardIdentity,
        RequestTimeoutSeconds = RequestTimeoutSeconds ?? Inbound.DefaultRequestTimeoutSeconds,
        MaxBodyBytes = MaxBodyBytes ?? Inbound.DefaultMaxBodyBytes,
        UpstreamHeaders = UpstreamHeaders,
    };
}

public static class InboundEndpoints
{
    public static void MapInbounds(this RouteGroupBuilder api)
    {
        var inbounds = api.MapGroup("/inbounds").WithTags("inbounds");
        inbounds.MapGet("/", ListAsync).WithSummary("All inbounds");
        inbounds.MapGet("/{slug}", GetAsync).WithSummary("One inbound");
        inbounds.MapPut("/{slug}", PutAsync).WithSummary("Create or replace an inbound").WithDescription(
            "A timeout or body limit left out gets its default. upstreamHeaders are write-only: " +
            "without the field the headers stay as they are, {} removes them all.");
        inbounds.MapPost("/{slug}/enable", (string slug, InboundService service, CancellationToken ct) => SwitchAsync(service, slug, true, ct))
            .WithSummary("Enable the inbound");
        inbounds.MapPost("/{slug}/disable", (string slug, InboundService service, CancellationToken ct) => SwitchAsync(service, slug, false, ct))
            .WithSummary("Disable the inbound without removing it");
        inbounds.MapDelete("/{slug}", RemoveAsync).WithSummary("Remove the inbound and every client's access to it");
    }

    private static async Task<Ok<List<InboundView>>> ListAsync(InboundService inbounds, CancellationToken ct)
    {
        var counts = await inbounds.ClientCountsAsync(ct);
        return TypedResults.Ok((await inbounds.ListAsync(ct)).Select(inbound => View(inbounds, inbound, counts)).ToList());
    }

    private static async Task<Ok<InboundView>> GetAsync(string slug, InboundService inbounds, CancellationToken ct) =>
        TypedResults.Ok(await ViewAsync(inbounds, await inbounds.GetAsync(slug, ct), ct));

    private static async Task<Results<Created<InboundView>, Ok<InboundView>>> PutAsync(
        string slug, InboundBody body, InboundService inbounds, HttpContext http, CancellationToken ct)
    {
        if (await inbounds.FindAsync(slug, ct) is null)
        {
            var added = await inbounds.AddHttpAsync(slug, body.Settings(), ct);
            return TypedResults.Created(
                $"{http.Request.PathBase}{ApiServices.Prefix}/inbounds/{added.Slug}", await ViewAsync(inbounds, added, ct));
        }

        await inbounds.UpdateAsync(slug, body.Settings(), ct);
        return TypedResults.Ok(await ViewAsync(inbounds, await inbounds.GetAsync(slug, ct), ct));
    }

    private static async Task<NoContent> SwitchAsync(InboundService inbounds, string slug, bool enabled, CancellationToken ct)
    {
        await inbounds.SetEnabledAsync(slug, enabled, ct);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> RemoveAsync(string slug, InboundService inbounds, CancellationToken ct)
    {
        await inbounds.RemoveAsync(slug, ct);
        return TypedResults.NoContent();
    }

    private static async Task<InboundView> ViewAsync(InboundService inbounds, Inbound inbound, CancellationToken ct) =>
        View(inbounds, inbound, await inbounds.ClientCountsAsync(ct));

    private static InboundView View(InboundService inbounds, Inbound inbound, Dictionary<Guid, int> counts) => new(
        inbound.Slug,
        inbound.Name,
        inbound.Enabled,
        inbound.Url,
        inbound.ForwardIdentity,
        inbound.RequestTimeoutSeconds,
        inbound.MaxBodyBytes,
        inbounds.HeaderNames(inbound),
        counts.GetValueOrDefault(inbound.Id),
        inbound.CreatedAt);
}
