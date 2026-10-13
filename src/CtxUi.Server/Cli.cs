using System.CommandLine;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using CtxUi.Core;
using CtxUi.Core.Clients;
using CtxUi.Core.Data;
using CtxUi.Core.Inbounds;

namespace CtxUi.Server;

public static class Cli
{
    public static bool Handles(string[] args) => args.Length > 0 && args[0] is "inbound" or "client";

    public static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        Action<HostApplicationBuilder>? configure = null)
    {
        var builder = Host.CreateApplicationBuilder();
        configure?.Invoke(builder);
        builder.Logging.ClearProviders()
            .AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace)
            .AddCtxUiFilters()
            .SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddCtxUi(builder.Configuration);
        using var host = builder.Build();

        var root = new RootCommand("ctx-ui: inbound'ы и клиенты") { InboundCommand(), ClientCommand() };
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
                        Pairs(parse.GetValue(header)),
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

            return new Command("client", "кому и на какой срок выдан доступ")
            {
                Leaf("add", "выпустить клиента и показать токен", [name, inbound, expires, meta], async (parse, services, ct) =>
                {
                    var clients = services.GetRequiredService<ClientService>();
                    var issued = await clients.AddAsync(
                        parse.GetValue(name)!,
                        parse.GetValue(inbound) ?? [],
                        Expiry(parse.GetValue(expires)),
                        Pairs(parse.GetValue(meta)),
                        ct: ct);
                    WriteConnection(services, await clients.GetAsync(issued.Client.Name, ct), issued.Token);
                }),
                Leaf("list", "показать клиентов", [], async (_, services, ct) =>
                {
                    var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
                    foreach (var client in await services.GetRequiredService<ClientService>().ListAsync(ct))
                    {
                        var status = !client.Enabled ? "отключён" : client.ExpiresAt <= now ? "истёк" : "активен";
                        var expiry = client.ExpiresAt is { } expiresAt ? Date(expiresAt, "yyyy-MM-dd") : "бессрочно";
                        var lastUsed = client.LastUsedAt is { } usedAt ? Date(usedAt, "yyyy-MM-dd HH:mm") : "не обращался";
                        var slugs = string.Join(",", client.Inbounds.Select(link => link.Inbound!.Slug).Order());
                        output.WriteLine($"{client.Name,-28} {status,-9} {expiry,-10} {lastUsed,-16} {slugs}");
                    }
                }),
                Leaf("show", "показать токен и адреса подключения", [name], async (parse, services, ct) =>
                {
                    var clients = services.GetRequiredService<ClientService>();
                    var client = await clients.GetAsync(parse.GetValue(name)!, ct);
                    WriteConnection(services, client, await clients.RevealAsync(client.Name, ct));
                }),
                Leaf("set", "изменить срок или набор inbound'ов", [name, inbound, expires], async (parse, services, ct) =>
                {
                    var slugs = parse.GetValue(inbound);
                    var date = parse.GetValue(expires);
                    var update = new ClientUpdate
                    {
                        ChangeExpiry = date is not null,
                        ExpiresAt = Expiry(date),
                        InboundSlugs = slugs is { Length: > 0 } ? slugs : null,
                    };
                    await services.GetRequiredService<ClientService>().UpdateAsync(parse.GetValue(name)!, update, ct);
                    output.WriteLine($"Клиент «{parse.GetValue(name)}» изменён");
                }),
                Leaf("reissue", "выпустить новый токен, старый перестаёт работать", [name], async (parse, services, ct) =>
                {
                    var clients = services.GetRequiredService<ClientService>();
                    var token = await clients.ReissueAsync(parse.GetValue(name)!, ct);
                    WriteConnection(services, await clients.GetAsync(parse.GetValue(name)!, ct), token);
                }),
                Leaf("enable", "вернуть доступ", [name], async (parse, services, ct) =>
                {
                    await services.GetRequiredService<ClientService>().SetEnabledAsync(parse.GetValue(name)!, true, ct);
                    output.WriteLine($"Клиент «{parse.GetValue(name)}» включён");
                }),
                Leaf("disable", "закрыть доступ, не удаляя клиента", [name], async (parse, services, ct) =>
                {
                    await services.GetRequiredService<ClientService>().SetEnabledAsync(parse.GetValue(name)!, false, ct);
                    output.WriteLine($"Клиент «{parse.GetValue(name)}» отключён");
                }),
                Leaf("remove", "удалить клиента", [name], async (parse, services, ct) =>
                {
                    await services.GetRequiredService<ClientService>().RemoveAsync(parse.GetValue(name)!, ct);
                    output.WriteLine($"Клиент «{parse.GetValue(name)}» удалён");
                }),
            };
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
                    await scope.ServiceProvider.GetRequiredService<CtxUiDbContext>().Database.MigrateAsync(ct);
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
            if (token is null)
            {
                output.WriteLine("Токен:   показать нельзя: сохранённая копия не расшифрована, потерян каталог keys.");
                output.WriteLine($"         Выпустите новый: ctx-ui client reissue \"{client.Name}\"");
                return;
            }

            output.WriteLine($"Токен:   {token}");
            foreach (var slug in client.Inbounds.Select(link => link.Inbound!.Slug).Order())
            {
                output.WriteLine($"Inbound «{slug}»:");
                output.WriteLine($"  с заголовком Authorization: Bearer <токен>   {PublicUrl(services)}/mcp/{slug}");
                output.WriteLine($"  адрес с токеном внутри                      {PublicUrl(services)}/k/{token}/mcp/{slug}");
            }
        }
    }

    private static string PublicUrl(IServiceProvider services) =>
        services.GetRequiredService<IOptions<CtxUiOptions>>().Value.PublicUrl.TrimEnd('/');

    private static string Date(DateTimeOffset value, string format) => value.ToString(format, CultureInfo.InvariantCulture);

    private static DateTimeOffset? Expiry(string? text) => text is null ? null : ExpiryArgument.Parse(text);

    private static List<KeyValuePair<string, string>> Pairs(string[]? values)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        foreach (var value in values ?? [])
        {
            var separator = value.IndexOf('=');
            if (separator <= 0)
                throw new DomainException($"«{value}»: ожидается вид ключ=значение");

            pairs.Add(KeyValuePair.Create(value[..separator], value[(separator + 1)..]));
        }

        return pairs;
    }
}
