using System.CommandLine;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using CtxUi.Core;
using CtxUi.Core.ApiKeys;
using CtxUi.Core.Clients;
using CtxUi.Core.Data;
using CtxUi.Core.Inbounds;
using CtxUi.Core.Settings;
using CtxUi.Core.Usage;
using CtxUi.Panel;
using CtxUi.Panel.Accounts;
using CtxUi.Server.Api;

namespace CtxUi.Server;

public static class Cli
{
    private const string Language = "ru";

    public static bool Handles(string[] args) => args.Length > 0 && args[0] is "inbound" or "client" or "usage" or "admin" or "apikey" or "backup";

    public static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        Action<HostApplicationBuilder>? configure = null)
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(Language);
        var builder = Host.CreateApplicationBuilder();
        configure?.Invoke(builder);
        builder.Logging.ClearProviders()
            .AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace)
            .AddCtxUiFilters()
            .SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddCtxUi(builder.Configuration);
        using var host = builder.Build();

        var root = new RootCommand("ctx-ui: inbound'ы, клиенты, учёт вызовов, вход в панель и ключи API")
        {
            InboundCommand(), ClientCommand(), UsageCommand(), AdminCommand(), ApiKeyCommand(), BackupCommand(),
        };
        return await root.Parse(args).InvokeAsync(new InvocationConfiguration { Output = output, Error = error });

        Command InboundCommand()
        {
            var slug = new Argument<string>("slug") { Description = "часть адреса: /mcp/<slug>" };
            var url = new Option<string>("--url") { Description = "адрес MCP-сервера, streamable HTTP", Required = true };
            var name = new Option<string?>("--name") { Description = "название для списка, по умолчанию slug" };
            var header = new Option<string[]>("--header") { Description = "заголовок для upstream: Имя=значение, можно повторять" };
            var identity = new Option<bool>("--forward-identity")
            {
                Description = "передавать upstream заголовки X-Mcp-Client-* и X-Mcp-Meta-*",
            };

            return new Command("inbound", "что отдаётся по адресу /mcp/<slug>")
            {
                Leaf("add", "добавить inbound на удалённый MCP-сервер", [slug, url, name, header, identity], async (parse, services, ct) =>
                {
                    var inbound = await services.GetRequiredService<InboundService>().AddHttpAsync(
                        parse.GetValue(slug)!,
                        parse.GetValue(name),
                        parse.GetValue(url)!,
                        PairLines.Parse(parse.GetValue(header)),
                        parse.GetValue(identity),
                        ct);
                    output.WriteLine($"Inbound «{inbound.Slug}» добавлен: {PublicUrl(services)}/mcp/{inbound.Slug}");
                }),
                Leaf("list", "показать inbound'ы", [], async (_, services, ct) =>
                {
                    foreach (var inbound in await services.GetRequiredService<InboundService>().ListAsync(ct))
                        output.WriteLine($"{inbound.Slug,-24} {(inbound.Enabled ? "включён" : "выключен"),-9} {inbound.Url}");
                }),
                Leaf("enable", "включить inbound", [slug], async (parse, services, ct) =>
                {
                    await services.GetRequiredService<InboundService>().SetEnabledAsync(parse.GetValue(slug)!, true, ct);
                    output.WriteLine($"Inbound «{parse.GetValue(slug)}» включён");
                }),
                Leaf("disable", "выключить inbound, не удаляя", [slug], async (parse, services, ct) =>
                {
                    await services.GetRequiredService<InboundService>().SetEnabledAsync(parse.GetValue(slug)!, false, ct);
                    output.WriteLine($"Inbound «{parse.GetValue(slug)}» выключен");
                }),
                Leaf("remove", "удалить inbound и доступ к нему у всех клиентов", [slug], async (parse, services, ct) =>
                {
                    await services.GetRequiredService<InboundService>().RemoveAsync(parse.GetValue(slug)!, ct);
                    output.WriteLine($"Inbound «{parse.GetValue(slug)}» удалён");
                }),
            };
        }

        Command ClientCommand()
        {
            var name = new Argument<string>("name") { Description = "имя клиента; с пробелами берётся в кавычки" };
            var inbound = new Option<string[]>("--inbound") { Description = "slug inbound'а, можно повторять" };
            var expires = new Option<string?>("--expires")
            {
                Description = "дата ГГГГ-ММ-ДД (UTC), с начала которой доступ закрыт; never снимает срок",
            };
            var meta = new Option<string[]>("--meta") { Description = "метаданные ключ=значение для upstream, можно повторять" };
            var rate = new Option<string?>("--rate") { Description = "запросов в минуту; none снимает ограничение" };
            var concurrency = new Option<string?>("--concurrency") { Description = "одновременных запросов; none снимает ограничение" };
            var quota = new Option<string?>("--quota") { Description = "вызовов tools/call за период; none снимает квоту" };
            var resetDays = new Option<string?>("--reset-days") { Description = "через сколько дней квота обновляется; none отменяет сброс" };

            ClientLimits? Limits(ParseResult parse, ClientLimits current)
            {
                int? Pick(Option<string?> option, int? kept) =>
                    parse.GetValue(option) is { } text ? LimitArgument.Parse(option.Name, text) : kept;

                var limits = new ClientLimits(
                    Pick(rate, current.RatePerMinute),
                    Pick(concurrency, current.MaxConcurrency),
                    Pick(quota, current.QuotaCalls),
                    Pick(resetDays, current.ResetEveryDays));
                return limits == current ? null : limits;
            }

            async Task<Guid> ClientIdAsync(ParseResult parse, IServiceProvider services, CancellationToken ct) =>
                (await services.GetRequiredService<ClientService>().GetAsync(parse.GetValue(name)!, ct)).Id;

            return new Command("client", "кому, на какой срок и с какими лимитами выдан доступ")
            {
                Leaf(
                    "add",
                    "выпустить клиента и показать токен",
                    [name, inbound, expires, meta, rate, concurrency, quota, resetDays],
                    async (parse, services, ct) =>
                    {
                        var clients = services.GetRequiredService<ClientService>();
                        var issued = await clients.AddAsync(
                            parse.GetValue(name)!,
                            parse.GetValue(inbound) ?? [],
                            Expiry(parse.GetValue(expires)),
                            PairLines.Parse(parse.GetValue(meta)),
                            Limits(parse, ClientLimits.None),
                            ct);
                        WriteConnection(services, await clients.GetAsync(issued.Client.Id, ct), issued.Token);
                    }),
                Leaf("list", "показать клиентов", [], async (_, services, ct) =>
                {
                    var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
                    foreach (var client in await services.GetRequiredService<ClientService>().ListAsync(ct))
                    {
                        var used = QuotaPeriod.UsedCalls(client, now);
                        var status = ClientState.Text(ClientState.Of(client, now));
                        var expiry = client.ExpiresAt is { } expiresAt ? Date(expiresAt, "yyyy-MM-dd") : "бессрочно";
                        var lastUsed = client.LastUsedAt is { } usedAt ? Date(usedAt, "yyyy-MM-dd HH:mm") : "не обращался";
                        var calls = $"{used}/{(client.QuotaCalls is { } size ? size : "∞")}";
                        var slugs = string.Join(",", client.Inbounds.Select(link => link.Inbound!.Slug).Order());
                        output.WriteLine($"{client.Name,-28} {status,-9} {expiry,-10} {lastUsed,-16} {calls,-12} {slugs}");
                    }
                }),
                Leaf("show", "показать токен и адреса подключения", [name], async (parse, services, ct) =>
                {
                    var clients = services.GetRequiredService<ClientService>();
                    var client = await clients.GetAsync(parse.GetValue(name)!, ct);
                    WriteConnection(services, client, clients.Reveal(client));
                }),
                Leaf(
                    "set",
                    "изменить срок, лимиты или набор inbound'ов",
                    [name, inbound, expires, rate, concurrency, quota, resetDays],
                    async (parse, services, ct) =>
                    {
                        var clients = services.GetRequiredService<ClientService>();
                        var client = await clients.GetAsync(parse.GetValue(name)!, ct);
                        var slugs = parse.GetValue(inbound);
                        var date = parse.GetValue(expires);
                        var update = new ClientUpdate
                        {
                            ChangeExpiry = date is not null,
                            ExpiresAt = Expiry(date),
                            InboundSlugs = slugs is { Length: > 0 } ? slugs : null,
                            Limits = Limits(parse, ClientLimits.Of(client)),
                        };
                        await clients.UpdateAsync(client.Id, update, ct);
                        output.WriteLine($"Клиент «{parse.GetValue(name)}» изменён");
                    }),
                Leaf("reset", "обнулить счётчик квоты и начать период заново", [name], async (parse, services, ct) =>
                {
                    await services.GetRequiredService<ClientService>().ResetQuotaAsync(await ClientIdAsync(parse, services, ct), ct);
                    output.WriteLine($"Счётчик квоты клиента «{parse.GetValue(name)}» обнулён, период начат заново");
                }),
                Leaf("reissue", "выпустить новый токен, старый перестаёт работать", [name], async (parse, services, ct) =>
                {
                    var clients = services.GetRequiredService<ClientService>();
                    var client = await clients.GetAsync(parse.GetValue(name)!, ct);
                    WriteConnection(services, client, await clients.ReissueAsync(client.Id, ct));
                }),
                Leaf("enable", "вернуть доступ", [name], async (parse, services, ct) =>
                {
                    await services.GetRequiredService<ClientService>().SetEnabledAsync(await ClientIdAsync(parse, services, ct), true, ct);
                    output.WriteLine($"Клиент «{parse.GetValue(name)}» включён");
                }),
                Leaf("disable", "закрыть доступ, не удаляя клиента", [name], async (parse, services, ct) =>
                {
                    await services.GetRequiredService<ClientService>().SetEnabledAsync(await ClientIdAsync(parse, services, ct), false, ct);
                    output.WriteLine($"Клиент «{parse.GetValue(name)}» отключён");
                }),
                Leaf("remove", "удалить клиента", [name], async (parse, services, ct) =>
                {
                    await services.GetRequiredService<ClientService>().RemoveAsync(await ClientIdAsync(parse, services, ct), ct);
                    output.WriteLine($"Клиент «{parse.GetValue(name)}» удалён");
                }),
            };
        }

        Command UsageCommand()
        {
            var client = new Option<string?>("--client") { Description = "только этот клиент" };
            var days = new Option<int>("--days") { Description = "за сколько последних дней, по умолчанию 7", DefaultValueFactory = _ => UsageStore.DefaultDays };
            var limit = new Option<int>("--limit") { Description = "сколько последних строк, по умолчанию 50", DefaultValueFactory = _ => UsageStore.DefaultLines };

            async Task<Guid?> ClientIdAsync(ParseResult parse, IServiceProvider services, CancellationToken ct) =>
                parse.GetValue(client) is { } wanted
                    ? (await services.GetRequiredService<ClientService>().GetAsync(wanted, ct)).Id
                    : null;

            return new Command("usage", "сколько и чего вызывали; время в UTC")
            {
                Leaf("days", "итоги по дням", [client, days], async (parse, services, ct) =>
                {
                    var span = InRange("--days", parse.GetValue(days), UsageStore.MaxDays);
                    var today = DateOnly.FromDateTime(services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime);
                    var totals = await services.GetRequiredService<UsageStore>()
                        .DailyAsync(await ClientIdAsync(parse, services, ct), today.AddDays(1 - span), ct);
                    foreach (var total in totals)
                    {
                        var average = total.Calls == 0 ? 0 : total.DurationMs / total.Calls;
                        output.WriteLine(
                            $"{total.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}  вызовов {total.Calls}, из них tools/call {total.ToolCalls}, " +
                            $"сбоев upstream {total.Errors}, отказов шлюза {total.Refused}, в среднем {average} мс");
                    }

                    if (totals.Count == 0)
                        output.WriteLine("За это время обращений не было");
                }),
                Leaf("log", "последние вызовы, новые сверху", [client, limit], async (parse, services, ct) =>
                {
                    var count = InRange("--limit", parse.GetValue(limit), UsageStore.MaxLines);
                    var lines = await services.GetRequiredService<UsageStore>()
                        .RecentAsync(await ClientIdAsync(parse, services, ct), count, ct);
                    var clients = await services.GetRequiredService<ClientService>().NamesAsync(ct);
                    var inbounds = await services.GetRequiredService<InboundService>().SlugsAsync(ct);
                    foreach (var line in lines)
                    {
                        var what = line.Tool is null ? line.Method : $"{line.Method} {line.Tool}";
                        output.WriteLine(
                            $"{Date(line.At, "yyyy-MM-dd HH:mm:ss")}  {clients.GetValueOrDefault(line.ClientId, Removed),-24} " +
                            $"{inbounds.GetValueOrDefault(line.InboundId, Removed),-16} {UsageOutcomes.Text(line.Outcome),-17} {line.DurationMs,6} мс  {what}");
                    }

                    if (lines.Count == 0)
                        output.WriteLine("Журнал пуст");
                }),
            };
        }

        Command AdminCommand()
        {
            var login = new Argument<string>("login") { Description = "логин: латинские буквы, цифры, точка, дефис, подчёркивание" };
            var renew = new Option<bool>("--new") { Description = "заменить адрес панели новым случайным" };

            async Task WriteEntranceAsync(IServiceProvider services, string name, string password, CancellationToken ct)
            {
                output.WriteLine($"Логин:   {name}");
                output.WriteLine($"Пароль:  {password}");
                output.WriteLine($"Панель:  {PanelUrl(services, await services.GetRequiredService<PanelSettings>().PathAsync(ct))}");
                output.WriteLine("Пароль показан один раз. Если он потерян, выпустите новый: ctx-ui admin password <логин>");
            }

            return new Command("admin", "кто входит в панель и по какому адресу она открывается")
            {
                Leaf("add", "добавить администратора и показать его пароль", [login], async (parse, services, ct) =>
                {
                    var issued = await services.GetRequiredService<AdminAccounts>().AddAsync(parse.GetValue(login)!, ct);
                    output.WriteLine($"Администратор «{issued.Admin.Name}» добавлен");
                    await WriteEntranceAsync(services, issued.Admin.Name, issued.Password, ct);
                }),
                Leaf("password", "выдать новый пароль, закрыть прежние сеансы и снять блокировку", [login], async (parse, services, ct) =>
                {
                    var issued = await services.GetRequiredService<AdminAccounts>().ResetPasswordAsync(parse.GetValue(login)!, ct);
                    output.WriteLine($"Пароль администратора «{issued.Admin.Name}» заменён, прежние сеансы закрыты");
                    await WriteEntranceAsync(services, issued.Admin.Name, issued.Password, ct);
                }),
                Leaf("list", "показать администраторов", [], async (_, services, ct) =>
                {
                    var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
                    var admins = await services.GetRequiredService<AdminAccounts>().ListAsync(ct);
                    foreach (var admin in admins)
                    {
                        var locked = admin.LockedUntil > now ? $"вход закрыт до {Date(admin.LockedUntil.Value, "yyyy-MM-dd HH:mm")} (UTC)" : "";
                        output.WriteLine($"{admin.Name,-24} добавлен {Date(admin.CreatedAt, "yyyy-MM-dd")}  {locked}".TrimEnd());
                    }

                    if (admins.Count == 0)
                        output.WriteLine("No admins, nobody can sign in to the panel. Add one: ctx-ui admin add <login>");
                }),
                Leaf("remove", "удалить администратора", [login], async (parse, services, ct) =>
                {
                    await services.GetRequiredService<AdminAccounts>().RemoveAsync(parse.GetValue(login)!, ct);
                    output.WriteLine($"Администратор «{parse.GetValue(login)!.Trim()}» удалён");
                }),
                Leaf("path", "показать адрес панели; --new заменяет его", [renew], async (parse, services, ct) =>
                {
                    var settings = services.GetRequiredService<PanelSettings>();
                    if (!parse.GetValue(renew))
                    {
                        output.WriteLine($"Панель:  {PanelUrl(services, await settings.PathAsync(ct))}");
                        return;
                    }

                    output.WriteLine($"Панель:  {PanelUrl(services, await settings.RenewPathAsync(ct))}");
                    output.WriteLine("Новый адрес начнёт работать после перезапуска сервиса, до тех пор действует прежний");
                }),
            };
        }

        Command ApiKeyCommand()
        {
            var name = new Argument<string>("name") { Description = "название: латинские буквы, цифры, точка, дефис, подчёркивание" };

            return new Command("apikey", "ключи для REST API; API и Swagger UI живут под адресом панели")
            {
                Leaf("add", "выпустить ключ и показать его", [name], async (parse, services, ct) =>
                {
                    var issued = await services.GetRequiredService<ApiKeyService>().AddAsync(parse.GetValue(name)!, ct);
                    var panel = PanelUrl(services, await services.GetRequiredService<PanelSettings>().PathAsync(ct));
                    output.WriteLine($"Ключ «{issued.Key.Name}» выпущен");
                    output.WriteLine($"Ключ:     {issued.Secret}");
                    output.WriteLine($"API:      {panel}{ApiServices.Prefix.TrimStart('/')}/");
                    output.WriteLine($"Swagger:  {panel}{ApiServices.SwaggerPath}/");
                    output.WriteLine("Ключ показан один раз. Потерянный ключ не восстановить: удалите его и выпустите новый.");
                }),
                Leaf("list", "показать ключи", [], async (_, services, ct) =>
                {
                    var keys = await services.GetRequiredService<ApiKeyService>().ListAsync(ct);
                    foreach (var key in keys)
                        output.WriteLine($"{key.Name,-24} {key.SecretPrefix}…  выпущен {Date(key.CreatedAt, "yyyy-MM-dd")}");

                    if (keys.Count == 0)
                        output.WriteLine("Ключей нет. Выпустите: ctx-ui apikey add <название>");
                }),
                Leaf("remove", "отозвать ключ; запросы с ним сразу получают 401", [name], async (parse, services, ct) =>
                {
                    await services.GetRequiredService<ApiKeyService>().RemoveAsync(parse.GetValue(name)!, ct);
                    output.WriteLine($"Ключ «{parse.GetValue(name)!.Trim()}» отозван");
                }),
            };
        }

        Command BackupCommand()
        {
            var directory = new Argument<string>("directory") { Description = "новый или пустой каталог для копии" };

            return Leaf(
                "backup",
                "согласованная копия базы и ключей, сервис можно не останавливать",
                [directory],
                async (parse, services, ct) =>
                {
                    var target = Path.GetFullPath(parse.GetValue(directory)!);
                    var dataDir = Path.GetFullPath(services.GetRequiredService<IOptions<CtxUiOptions>>().Value.DataDir);
                    await Backup.WriteAsync(services.GetRequiredService<CtxUiDbContext>(), dataDir, target, ct);
                    output.WriteLine($"Копия записана в {target}");
                });
        }

        Command Leaf(
            string name,
            string description,
            Symbol[] symbols,
            Func<ParseResult, IServiceProvider, CancellationToken, Task> run)
        {
            var command = new Command(name, description);
            foreach (var symbol in symbols)
            {
                switch (symbol)
                {
                    case Argument argument:
                        command.Add(argument);
                        break;
                    case Option option:
                        command.Add(option);
                        break;
                }
            }

            command.SetAction(async (parse, ct) =>
            {
                try
                {
                    await using var scope = host.Services.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<CtxUiDbContext>().PrepareAsync(ct);
                    await run(parse, scope.ServiceProvider, ct);
                    return 0;
                }
                catch (DomainException exception)
                {
                    error.WriteLine($"Ошибка: {exception.Message}");
                    return 1;
                }
            });
            return command;
        }

        void WriteConnection(IServiceProvider services, Client client, string? token)
        {
            output.WriteLine($"Клиент:  {client.Name}");
            output.WriteLine($"Срок:    {(client.ExpiresAt is { } expiresAt ? $"до {Date(expiresAt, "yyyy-MM-dd")} (UTC)" : "бессрочно")}");
            output.WriteLine($"Лимиты:  {DescribeLimits(client)}");
            output.WriteLine($"Квота:   {DescribeQuota(client, services.GetRequiredService<TimeProvider>().GetUtcNow())}");
            if (token is null)
            {
                output.WriteLine("Токен:   показать нельзя: сохранённая копия не расшифрована, потерян каталог keys.");
                output.WriteLine($"         Выпустите новый: ctx-ui client reissue \"{client.Name}\"");
                return;
            }

            output.WriteLine($"Токен:   {token}");
            foreach (var slug in ConnectionText.Slugs(client))
            {
                output.WriteLine($"Inbound «{slug}»:");
                output.WriteLine($"  с заголовком Authorization: Bearer <токен>   {ConnectionText.Address(PublicUrl(services), slug)}");
                output.WriteLine($"  адрес с токеном внутри                      {ConnectionText.AddressWithToken(PublicUrl(services), token, slug)}");
            }
        }
    }

    private const string Removed = "удалён";

    private static string DescribeLimits(Client client)
    {
        var parts = new List<string>();
        if (client.RatePerMinute is { } perMinute)
            parts.Add($"запросов в минуту: {perMinute}");
        if (client.MaxConcurrency is { } atOnce)
            parts.Add($"одновременно: {atOnce}");

        return parts.Count == 0 ? "нет" : string.Join(", ", parts);
    }

    private static string DescribeQuota(Client client, DateTimeOffset now)
    {
        var used = QuotaPeriod.UsedCalls(client, now);
        var size = client.QuotaCalls is { } quota ? $"{used} из {quota} вызовов" : $"нет, вызовов за период: {used}";
        return QuotaPeriod.NextReset(client.PeriodStartedAt, client.ResetEveryDays, now) is { } reset
            ? $"{size}, обновится {Date(reset, "yyyy-MM-dd HH:mm")} (UTC)"
            : size;
    }

    private static int InRange(string option, int value, int max) =>
        value >= 1 && value <= max ? value : throw new DomainException($"{option}: нужно число от 1 до {max}");

    private static string PublicUrl(IServiceProvider services) =>
        services.GetRequiredService<IOptions<CtxUiOptions>>().Value.PublicUrl.TrimEnd('/');

    private static string PanelUrl(IServiceProvider services, string path) => $"{PublicUrl(services)}/{path}/";

    private static string Date(DateTimeOffset value, string format) => value.ToString(format, CultureInfo.InvariantCulture);

    private static DateTimeOffset? Expiry(string? text) => text is null ? null : ExpiryArgument.Parse(text);
}
