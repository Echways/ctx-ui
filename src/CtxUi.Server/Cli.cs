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
    public static bool Handles(string[] args) => args.Length > 0 && args[0] is "inbound" or "client" or "usage" or "admin" or "apikey" or "backup";

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
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(host.Services.GetRequiredService<IOptions<CtxUiOptions>>().Value.Language);
        }
        catch (OptionsValidationException exception)
        {
            error.WriteLine(exception.Message);
            return 1;
        }

        var root = new RootCommand(ServerText.RootDescription)
        {
            InboundCommand(), ClientCommand(), UsageCommand(), AdminCommand(), ApiKeyCommand(), BackupCommand(),
        };
        return await root.Parse(args).InvokeAsync(new InvocationConfiguration { Output = output, Error = error });

        Command InboundCommand()
        {
            var slug = new Argument<string>("slug") { Description = ServerText.InboundSlugArgument };
            var url = new Option<string>("--url") { Description = ServerText.InboundUrlOption, Required = true };
            var name = new Option<string?>("--name") { Description = ServerText.InboundNameOption };
            var header = new Option<string[]>("--header") { Description = ServerText.InboundHeaderOption };
            var identity = new Option<bool>("--forward-identity")
            {
                Description = ServerText.InboundIdentityOption,
            };

            return new Command("inbound", ServerText.InboundCommand)
            {
                Leaf("add", ServerText.InboundAddCommand, [slug, url, name, header, identity], async (parse, services, ct) =>
                {
                    var inbound = await services.GetRequiredService<InboundService>().AddHttpAsync(
                        parse.GetValue(slug)!,
                        parse.GetValue(name),
                        parse.GetValue(url)!,
                        PairLines.Parse(parse.GetValue(header)),
                        parse.GetValue(identity),
                        ct);
                    output.WriteLine(string.Format(ServerText.InboundAdded, inbound.Slug, $"{PublicUrl(services)}/mcp/{inbound.Slug}"));
                }),
                Leaf("list", ServerText.InboundListCommand, [], async (_, services, ct) =>
                {
                    foreach (var inbound in await services.GetRequiredService<InboundService>().ListAsync(ct))
                        output.WriteLine($"{inbound.Slug,-24} {(inbound.Enabled ? ServerText.InboundOn : ServerText.InboundOff),-9} {inbound.Url}");
                }),
                Leaf("enable", ServerText.InboundEnableCommand, [slug], async (parse, services, ct) =>
                {
                    await services.GetRequiredService<InboundService>().SetEnabledAsync(parse.GetValue(slug)!, true, ct);
                    output.WriteLine(string.Format(ServerText.InboundEnabled, parse.GetValue(slug)));
                }),
                Leaf("disable", ServerText.InboundDisableCommand, [slug], async (parse, services, ct) =>
                {
                    await services.GetRequiredService<InboundService>().SetEnabledAsync(parse.GetValue(slug)!, false, ct);
                    output.WriteLine(string.Format(ServerText.InboundDisabled, parse.GetValue(slug)));
                }),
                Leaf("remove", ServerText.InboundRemoveCommand, [slug], async (parse, services, ct) =>
                {
                    await services.GetRequiredService<InboundService>().RemoveAsync(parse.GetValue(slug)!, ct);
                    output.WriteLine(string.Format(ServerText.InboundRemoved, parse.GetValue(slug)));
                }),
            };
        }

        Command ClientCommand()
        {
            var name = new Argument<string>("name") { Description = ServerText.ClientNameArgument };
            var inbound = new Option<string[]>("--inbound") { Description = ServerText.ClientInboundOption };
            var expires = new Option<string?>("--expires")
            {
                Description = ServerText.ClientExpiresOption,
            };
            var meta = new Option<string[]>("--meta") { Description = ServerText.ClientMetaOption };
            var rate = new Option<string?>("--rate") { Description = ServerText.ClientRateOption };
            var concurrency = new Option<string?>("--concurrency") { Description = ServerText.ClientConcurrencyOption };
            var quota = new Option<string?>("--quota") { Description = ServerText.ClientQuotaOption };
            var resetDays = new Option<string?>("--reset-days") { Description = ServerText.ClientResetDaysOption };

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

            return new Command("client", ServerText.ClientCommand)
            {
                Leaf(
                    "add",
                    ServerText.ClientAddCommand,
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
                Leaf("list", ServerText.ClientListCommand, [], async (_, services, ct) =>
                {
                    var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
                    foreach (var client in await services.GetRequiredService<ClientService>().ListAsync(ct))
                    {
                        var used = QuotaPeriod.UsedCalls(client, now);
                        var status = ClientState.Text(ClientState.Of(client, now));
                        var expiry = client.ExpiresAt is { } expiresAt ? Date(expiresAt, "yyyy-MM-dd") : ServerText.NoExpiry;
                        var lastUsed = client.LastUsedAt is { } usedAt ? Date(usedAt, "yyyy-MM-dd HH:mm") : ServerText.NeverUsed;
                        var calls = $"{used}/{(client.QuotaCalls is { } size ? size : "∞")}";
                        var slugs = string.Join(",", client.Inbounds.Select(link => link.Inbound!.Slug).Order());
                        output.WriteLine($"{client.Name,-28} {status,-9} {expiry,-10} {lastUsed,-16} {calls,-12} {slugs}");
                    }
                }),
                Leaf("show", ServerText.ClientShowCommand, [name], async (parse, services, ct) =>
                {
                    var clients = services.GetRequiredService<ClientService>();
                    var client = await clients.GetAsync(parse.GetValue(name)!, ct);
                    WriteConnection(services, client, clients.Reveal(client));
                }),
                Leaf(
                    "set",
                    ServerText.ClientSetCommand,
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
                        output.WriteLine(string.Format(ServerText.ClientChanged, parse.GetValue(name)));
                    }),
                Leaf("reset", ServerText.ClientResetCommand, [name], async (parse, services, ct) =>
                {
                    await services.GetRequiredService<ClientService>().ResetQuotaAsync(await ClientIdAsync(parse, services, ct), ct);
                    output.WriteLine(string.Format(ServerText.QuotaReset, parse.GetValue(name)));
                }),
                Leaf("reissue", ServerText.ClientReissueCommand, [name], async (parse, services, ct) =>
                {
                    var clients = services.GetRequiredService<ClientService>();
                    var client = await clients.GetAsync(parse.GetValue(name)!, ct);
                    WriteConnection(services, client, await clients.ReissueAsync(client.Id, ct));
                }),
                Leaf("enable", ServerText.ClientEnableCommand, [name], async (parse, services, ct) =>
                {
                    await services.GetRequiredService<ClientService>().SetEnabledAsync(await ClientIdAsync(parse, services, ct), true, ct);
                    output.WriteLine(string.Format(ServerText.ClientEnabled, parse.GetValue(name)));
                }),
                Leaf("disable", ServerText.ClientDisableCommand, [name], async (parse, services, ct) =>
                {
                    await services.GetRequiredService<ClientService>().SetEnabledAsync(await ClientIdAsync(parse, services, ct), false, ct);
                    output.WriteLine(string.Format(ServerText.ClientDisabled, parse.GetValue(name)));
                }),
                Leaf("remove", ServerText.ClientRemoveCommand, [name], async (parse, services, ct) =>
                {
                    await services.GetRequiredService<ClientService>().RemoveAsync(await ClientIdAsync(parse, services, ct), ct);
                    output.WriteLine(string.Format(ServerText.ClientRemoved, parse.GetValue(name)));
                }),
            };
        }

        Command UsageCommand()
        {
            var client = new Option<string?>("--client") { Description = ServerText.UsageClientOption };
            var days = new Option<int>("--days") { Description = ServerText.UsageDaysOption, DefaultValueFactory = _ => UsageStore.DefaultDays };
            var limit = new Option<int>("--limit") { Description = ServerText.UsageLimitOption, DefaultValueFactory = _ => UsageStore.DefaultLines };

            async Task<Guid?> ClientIdAsync(ParseResult parse, IServiceProvider services, CancellationToken ct) =>
                parse.GetValue(client) is { } wanted
                    ? (await services.GetRequiredService<ClientService>().GetAsync(wanted, ct)).Id
                    : null;

            return new Command("usage", ServerText.UsageCommand)
            {
                Leaf("days", ServerText.UsageDaysCommand, [client, days], async (parse, services, ct) =>
                {
                    var span = InRange("--days", parse.GetValue(days), UsageStore.MaxDays);
                    var today = DateOnly.FromDateTime(services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime);
                    var totals = await services.GetRequiredService<UsageStore>()
                        .DailyAsync(await ClientIdAsync(parse, services, ct), today.AddDays(1 - span), ct);
                    foreach (var total in totals)
                    {
                        var average = total.Calls == 0 ? 0 : total.DurationMs / total.Calls;
                        output.WriteLine(string.Format(
                            ServerText.UsageDay,
                            total.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                            total.Calls,
                            total.ToolCalls,
                            total.Errors,
                            total.Refused,
                            average));
                    }

                    if (totals.Count == 0)
                        output.WriteLine(ServerText.NoCalls);
                }),
                Leaf("log", ServerText.UsageLogCommand, [client, limit], async (parse, services, ct) =>
                {
                    var count = InRange("--limit", parse.GetValue(limit), UsageStore.MaxLines);
                    var lines = await services.GetRequiredService<UsageStore>()
                        .RecentAsync(await ClientIdAsync(parse, services, ct), count, ct);
                    var clients = await services.GetRequiredService<ClientService>().NamesAsync(ct);
                    var inbounds = await services.GetRequiredService<InboundService>().SlugsAsync(ct);
                    foreach (var line in lines)
                    {
                        var what = line.Tool is null ? line.Method : $"{line.Method} {line.Tool}";
                        output.WriteLine(string.Format(
                            ServerText.UsageLine,
                            Date(line.At, "yyyy-MM-dd HH:mm:ss"),
                            clients.GetValueOrDefault(line.ClientId, ServerText.Removed),
                            inbounds.GetValueOrDefault(line.InboundId, ServerText.Removed),
                            UsageOutcomes.Text(line.Outcome),
                            line.DurationMs,
                            what));
                    }

                    if (lines.Count == 0)
                        output.WriteLine(ServerText.JournalEmpty);
                }),
            };
        }

        Command AdminCommand()
        {
            var login = new Argument<string>("login") { Description = ServerText.AdminLoginArgument };
            var renew = new Option<bool>("--new") { Description = ServerText.AdminNewOption };

            async Task WriteEntranceAsync(IServiceProvider services, string name, string password, CancellationToken ct)
            {
                output.WriteLine(string.Format(ServerText.EntranceLogin, name));
                output.WriteLine(string.Format(ServerText.EntrancePassword, password));
                output.WriteLine(string.Format(ServerText.EntrancePanel, PanelUrl(services, await services.GetRequiredService<PanelSettings>().PathAsync(ct))));
                output.WriteLine(ServerText.PasswordShownOnce);
            }

            return new Command("admin", ServerText.AdminCommand)
            {
                Leaf("add", ServerText.AdminAddCommand, [login], async (parse, services, ct) =>
                {
                    var issued = await services.GetRequiredService<AdminAccounts>().AddAsync(parse.GetValue(login)!, ct);
                    output.WriteLine(string.Format(ServerText.AdminAdded, issued.Admin.Name));
                    await WriteEntranceAsync(services, issued.Admin.Name, issued.Password, ct);
                }),
                Leaf("password", ServerText.AdminPasswordCommand, [login], async (parse, services, ct) =>
                {
                    var issued = await services.GetRequiredService<AdminAccounts>().ResetPasswordAsync(parse.GetValue(login)!, ct);
                    output.WriteLine(string.Format(ServerText.AdminPasswordReset, issued.Admin.Name));
                    await WriteEntranceAsync(services, issued.Admin.Name, issued.Password, ct);
                }),
                Leaf("list", ServerText.AdminListCommand, [], async (_, services, ct) =>
                {
                    var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
                    var admins = await services.GetRequiredService<AdminAccounts>().ListAsync(ct);
                    foreach (var admin in admins)
                    {
                        var locked = admin.LockedUntil > now ? string.Format(ServerText.AdminLocked, Date(admin.LockedUntil.Value, "yyyy-MM-dd HH:mm")) : "";
                        output.WriteLine(string.Format(ServerText.AdminLine, admin.Name, Date(admin.CreatedAt, "yyyy-MM-dd"), locked).TrimEnd());
                    }

                    if (admins.Count == 0)
                        output.WriteLine(ServerText.NoAdmins);
                }),
                Leaf("remove", ServerText.AdminRemoveCommand, [login], async (parse, services, ct) =>
                {
                    await services.GetRequiredService<AdminAccounts>().RemoveAsync(parse.GetValue(login)!, ct);
                    output.WriteLine(string.Format(ServerText.AdminRemoved, parse.GetValue(login)!.Trim()));
                }),
                Leaf("path", ServerText.AdminPathCommand, [renew], async (parse, services, ct) =>
                {
                    var settings = services.GetRequiredService<PanelSettings>();
                    if (!parse.GetValue(renew))
                    {
                        output.WriteLine(string.Format(ServerText.EntrancePanel, PanelUrl(services, await settings.PathAsync(ct))));
                        return;
                    }

                    output.WriteLine(string.Format(ServerText.EntrancePanel, PanelUrl(services, await settings.RenewPathAsync(ct))));
                    output.WriteLine(ServerText.PanelPathRenewed);
                }),
            };
        }

        Command ApiKeyCommand()
        {
            var name = new Argument<string>("name") { Description = ServerText.ApiKeyNameArgument };

            return new Command("apikey", ServerText.ApiKeyCommand)
            {
                Leaf("add", ServerText.ApiKeyAddCommand, [name], async (parse, services, ct) =>
                {
                    var issued = await services.GetRequiredService<ApiKeyService>().AddAsync(parse.GetValue(name)!, ct);
                    var panel = PanelUrl(services, await services.GetRequiredService<PanelSettings>().PathAsync(ct));
                    output.WriteLine(string.Format(ServerText.ApiKeyIssued, issued.Key.Name));
                    output.WriteLine(string.Format(ServerText.ApiKeySecret, issued.Secret));
                    output.WriteLine(string.Format(ServerText.ApiKeyApi, $"{panel}{ApiServices.Prefix.TrimStart('/')}/"));
                    output.WriteLine(string.Format(ServerText.ApiKeySwagger, $"{panel}{ApiServices.SwaggerPath}/"));
                    output.WriteLine(ServerText.ApiKeyShownOnce);
                }),
                Leaf("list", ServerText.ApiKeyListCommand, [], async (_, services, ct) =>
                {
                    var keys = await services.GetRequiredService<ApiKeyService>().ListAsync(ct);
                    foreach (var key in keys)
                        output.WriteLine(string.Format(ServerText.ApiKeyLine, key.Name, key.SecretPrefix, Date(key.CreatedAt, "yyyy-MM-dd")));

                    if (keys.Count == 0)
                        output.WriteLine(ServerText.NoApiKeys);
                }),
                Leaf("remove", ServerText.ApiKeyRemoveCommand, [name], async (parse, services, ct) =>
                {
                    await services.GetRequiredService<ApiKeyService>().RemoveAsync(parse.GetValue(name)!, ct);
                    output.WriteLine(string.Format(ServerText.ApiKeyRevoked, parse.GetValue(name)!.Trim()));
                }),
            };
        }

        Command BackupCommand()
        {
            var directory = new Argument<string>("directory") { Description = ServerText.BackupDirectoryArgument };

            return Leaf(
                "backup",
                ServerText.BackupCommand,
                [directory],
                async (parse, services, ct) =>
                {
                    var target = Path.GetFullPath(parse.GetValue(directory)!);
                    var dataDir = Path.GetFullPath(services.GetRequiredService<IOptions<CtxUiOptions>>().Value.DataDir);
                    await Backup.WriteAsync(services.GetRequiredService<CtxUiDbContext>(), dataDir, target, ct);
                    output.WriteLine(string.Format(ServerText.BackupWritten, target));
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
                    error.WriteLine(string.Format(ServerText.Error, exception.Message));
                    return 1;
                }
            });
            return command;
        }

        void WriteConnection(IServiceProvider services, Client client, string? token)
        {
            output.WriteLine(string.Format(ServerText.ConnectionClient, client.Name));
            output.WriteLine(string.Format(
                ServerText.ConnectionExpiry,
                client.ExpiresAt is { } expiresAt ? string.Format(ServerText.ExpiresOn, Date(expiresAt, "yyyy-MM-dd")) : ServerText.NoExpiry));
            output.WriteLine(string.Format(ServerText.ConnectionLimits, DescribeLimits(client)));
            output.WriteLine(string.Format(ServerText.ConnectionQuota, DescribeQuota(client, services.GetRequiredService<TimeProvider>().GetUtcNow())));
            if (token is null)
            {
                output.WriteLine(ServerText.TokenLost);
                output.WriteLine(string.Format(ServerText.TokenLostHint, client.Name));
                return;
            }

            output.WriteLine(string.Format(ServerText.ConnectionToken, token));
            foreach (var slug in ConnectionText.Slugs(client))
            {
                output.WriteLine(string.Format(ServerText.ConnectionInbound, slug));
                output.WriteLine(string.Format(ServerText.AddressWithHeader, ConnectionText.Address(PublicUrl(services), slug)));
                output.WriteLine(string.Format(ServerText.AddressWithToken, ConnectionText.AddressWithToken(PublicUrl(services), token, slug)));
            }
        }
    }

    private static string DescribeLimits(Client client)
    {
        var parts = new List<string>();
        if (client.RatePerMinute is { } perMinute)
            parts.Add(string.Format(ServerText.LimitRate, perMinute));
        if (client.MaxConcurrency is { } atOnce)
            parts.Add(string.Format(ServerText.LimitConcurrency, atOnce));

        return parts.Count == 0 ? ServerText.NoLimits : string.Join(", ", parts);
    }

    private static string DescribeQuota(Client client, DateTimeOffset now)
    {
        var used = QuotaPeriod.UsedCalls(client, now);
        var size = client.QuotaCalls is { } quota ? string.Format(ServerText.QuotaUsedOf, used, quota) : string.Format(ServerText.QuotaNotSet, used);
        return QuotaPeriod.NextReset(client.PeriodStartedAt, client.ResetEveryDays, now) is { } reset
            ? string.Format(ServerText.QuotaResets, size, Date(reset, "yyyy-MM-dd HH:mm"))
            : size;
    }

    private static int InRange(string option, int value, int max) =>
        value >= 1 && value <= max ? value : throw new DomainException(string.Format(ServerText.NumberInRange, option, max));

    private static string PublicUrl(IServiceProvider services) =>
        services.GetRequiredService<IOptions<CtxUiOptions>>().Value.PublicUrl.TrimEnd('/');

    private static string PanelUrl(IServiceProvider services, string path) => $"{PublicUrl(services)}/{path}/";

    private static string Date(DateTimeOffset value, string format) => value.ToString(format, CultureInfo.InvariantCulture);

    private static DateTimeOffset? Expiry(string? text) => text is null ? null : ExpiryArgument.Parse(text);
}
