using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using CtxUi.Core.Clients;
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
    string TokenPrefix);

public sealed record ConnectionAddress(string Inbound, string Url, string UrlWithToken);

public sealed record ConnectionView(string Token, IReadOnlyList<ConnectionAddress> Addresses, string Message);

public sealed record IssuedClientView(ClientView Client, ConnectionView Connection);

public sealed record NewClient(
    string? Name,
    IReadOnlyList<string>? Inbounds,
    string? Expires = null,
    ClientLimits? Limits = null,
    Dictionary<string, string>? Metadata = null);

public sealed record ClientChange(
    string? Expires = null,
    IReadOnlyList<string>? Inbounds = null,
    ClientLimits? Limits = null,
    Dictionary<string, string>? Metadata = null);

public static class ClientEndpoints
{
    public static void MapClients(this RouteGroupBuilder api)
    {
        var clients = api.MapGroup("/clients").WithTags("clients");
        clients.MapGet("/", ListAsync).WithSummary("Все клиенты; ?name= находит одного по имени без учёта регистра");
        clients.MapGet("/{id:guid}", GetAsync).WithSummary("Один клиент");
        clients.MapGet("/{id:guid}/connection", ConnectionAsync).WithSummary("Токен, адреса и сообщение для клиента");
        clients.MapPost("/", AddAsync).WithSummary("Выпустить клиента").WithDescription(
            "expires: дата ГГГГ-ММ-ДД (UTC), с начала которой доступ закрыт. В limits пустое поле снимает ограничение.");
        clients.MapPatch("/{id:guid}", ChangeAsync).WithSummary("Изменить срок, inbound'ы, лимиты или метаданные").WithDescription(
            "Неуказанное поле не меняется. expires: ГГГГ-ММ-ДД или never. inbounds, limits и metadata заменяются целиком.");
        clients.MapPost("/{id:guid}/enable", (Guid id, ClientService service, CancellationToken ct) => SwitchAsync(service, id, true, ct))
            .WithSummary("Вернуть доступ");
        clients.MapPost("/{id:guid}/disable", (Guid id, ClientService service, CancellationToken ct) => SwitchAsync(service, id, false, ct))
            .WithSummary("Закрыть доступ, не удаляя клиента");
        clients.MapPost("/{id:guid}/reset-quota", ResetQuotaAsync).WithSummary("Обнулить счётчик квоты и начать период заново");
        clients.MapPost("/{id:guid}/reissue", ReissueAsync).WithSummary("Выпустить новый токен; старый перестаёт работать");
        clients.MapDelete("/{id:guid}", RemoveAsync).WithSummary("Удалить клиента");
    }

    private static async Task<Ok<List<ClientView>>> ListAsync(string? name, ClientService clients, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var all = await clients.ListAsync(ct);
        var wanted = name is null ? all : all.Where(client => client.NameKey == ClientName.Key(name.Trim()));
        return TypedResults.Ok(wanted.Select(client => View(client, now)).ToList());
    }

    private static async Task<Ok<ClientView>> GetAsync(Guid id, ClientService clients, TimeProvider clock, CancellationToken ct) =>
        TypedResults.Ok(View(await clients.GetAsync(id, ct), clock.GetUtcNow()));

    private static async Task<Results<Ok<ConnectionView>, ProblemHttpResult>> ConnectionAsync(
        Guid id, ClientService clients, IOptions<CtxUiOptions> options, CancellationToken ct)
    {
        var client = await clients.GetAsync(id, ct);
        if (await clients.RevealAsync(client.Name, ct) is not { } token)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                detail: "Токен показать нельзя: сохранённая копия не расшифрована, потерян каталог keys. Выпустите новый: POST …/reissue");
        }

        return TypedResults.Ok(Connection(options.Value.PublicUrl, client, token));
    }

    private static async Task<Created<IssuedClientView>> AddAsync(
        NewClient body, ClientService clients, IOptions<CtxUiOptions> options, TimeProvider clock, HttpContext http, CancellationToken ct)
    {
        var issued = await clients.AddAsync(
            body.Name ?? "",
            body.Inbounds ?? [],
            body.Expires is null ? null : ExpiryArgument.Parse(body.Expires),
            body.Metadata,
            body.Limits,
            ct);
        var client = await clients.GetAsync(issued.Client.Id, ct);
        return TypedResults.Created(
            $"{http.Request.PathBase}{ApiServices.Prefix}/clients/{client.Id}",
            new IssuedClientView(View(client, clock.GetUtcNow()), Connection(options.Value.PublicUrl, client, issued.Token)));
    }

    private static async Task<Ok<ClientView>> ChangeAsync(
        Guid id, ClientChange body, ClientService clients, TimeProvider clock, CancellationToken ct)
    {
        var client = await clients.GetAsync(id, ct);
        await clients.UpdateAsync(
            client.Name,
            new ClientUpdate
            {
                ChangeExpiry = body.Expires is not null,
                ExpiresAt = body.Expires is null ? null : ExpiryArgument.Parse(body.Expires),
                InboundSlugs = body.Inbounds,
                Limits = body.Limits,
                Metadata = body.Metadata,
            },
            ct);
        return TypedResults.Ok(View(await clients.GetAsync(id, ct), clock.GetUtcNow()));
    }

    private static async Task<NoContent> SwitchAsync(ClientService clients, Guid id, bool enabled, CancellationToken ct)
    {
        await clients.SetEnabledAsync((await clients.GetAsync(id, ct)).Name, enabled, ct);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> ResetQuotaAsync(Guid id, ClientService clients, CancellationToken ct)
    {
        await clients.ResetQuotaAsync((await clients.GetAsync(id, ct)).Name, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<ConnectionView>> ReissueAsync(
        Guid id, ClientService clients, IOptions<CtxUiOptions> options, CancellationToken ct)
    {
        var token = await clients.ReissueAsync((await clients.GetAsync(id, ct)).Name, ct);
        return TypedResults.Ok(Connection(options.Value.PublicUrl, await clients.GetAsync(id, ct), token));
    }

    private static async Task<NoContent> RemoveAsync(Guid id, ClientService clients, CancellationToken ct)
    {
        await clients.RemoveAsync((await clients.GetAsync(id, ct)).Name, ct);
        return TypedResults.NoContent();
    }

    private static ClientView View(Client client, DateTimeOffset now) => new(
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
        client.TokenPrefix);

    private static ConnectionView Connection(string publicUrl, Client client, string token) => new(
        token,
        ConnectionText.Slugs(client)
            .Select(slug => new ConnectionAddress(
                slug, ConnectionText.Address(publicUrl, slug), ConnectionText.AddressWithToken(publicUrl, token, slug)))
            .ToList(),
        ConnectionText.Message(publicUrl, client, token));
}
