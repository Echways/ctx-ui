using Microsoft.AspNetCore.Http.HttpResults;
using CtxUi.Core;
using CtxUi.Core.Clients;
using CtxUi.Core.Inbounds;
using CtxUi.Core.Usage;

namespace CtxUi.Server.Api;

public sealed record UsageLine(
    long Id,
    DateTimeOffset At,
    Guid ClientId,
    string? Client,
    Guid InboundId,
    string? Inbound,
    string Method,
    string? Tool,
    int DurationMs,
    UsageOutcome Outcome);

public static class UsageEndpoints
{
    public static void MapUsage(this RouteGroupBuilder api)
    {
        api.MapGet("/overview", OverviewAsync).WithTags("usage").WithSummary("То же, что страница «Обзор»: сутки и последние 7 дней");
        var usage = api.MapGroup("/usage").WithTags("usage");
        usage.MapGet("/days", DaysAsync).WithSummary("Итоги по дням, UTC; days от 1 до 3650, по умолчанию 7");
        usage.MapGet("/log", LogAsync).WithSummary("Последние вызовы, новые сверху; limit от 1 до 1000, по умолчанию 50");
    }

    private static async Task<Ok<Overview>> OverviewAsync(OverviewService overview, CancellationToken ct) =>
        TypedResults.Ok(await overview.LoadAsync(ct));

    private static async Task<Ok<List<DailyTotal>>> DaysAsync(
        Guid? clientId, int? days, UsageStore usage, ClientService clients, TimeProvider clock, CancellationToken ct)
    {
        var span = InRange("days", days ?? UsageStore.DefaultDays, UsageStore.MaxDays);
        await EnsureClientAsync(clients, clientId, ct);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        return TypedResults.Ok(await usage.DailyAsync(clientId, today.AddDays(1 - span), ct));
    }

    private static async Task<Ok<List<UsageLine>>> LogAsync(
        Guid? clientId, int? limit, UsageStore usage, ClientService clients, InboundService inbounds, CancellationToken ct)
    {
        var count = InRange("limit", limit ?? UsageStore.DefaultLines, UsageStore.MaxLines);
        await EnsureClientAsync(clients, clientId, ct);
        var names = await clients.NamesAsync(ct);
        var slugs = await inbounds.SlugsAsync(ct);
        var lines = await usage.RecentAsync(clientId, count, ct);
        return TypedResults.Ok(lines
            .Select(line => new UsageLine(
                line.Id,
                line.At,
                line.ClientId,
                names.GetValueOrDefault(line.ClientId),
                line.InboundId,
                slugs.GetValueOrDefault(line.InboundId),
                line.Method,
                line.Tool,
                line.DurationMs,
                line.Outcome))
            .ToList());
    }

    private static async Task EnsureClientAsync(ClientService clients, Guid? clientId, CancellationToken ct)
    {
        if (clientId is { } id)
            await clients.GetAsync(id, ct);
    }

    private static int InRange(string name, int value, int max) =>
        value >= 1 && value <= max ? value : throw new DomainException(string.Format(ServerText.NumberInRange, name, max));
}
