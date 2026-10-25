using System.CommandLine;
using System.Globalization;
using CtxUi.Core;
using CtxUi.Core.Clients;
using CtxUi.Core.SignIn;

namespace CtxUi.Server;

public delegate Command CliLeaf(string name, string description, Symbol[] symbols, Func<ParseResult, IServiceProvider, CancellationToken, Task> run);

public static partial class Cli
{
    private const string ProviderSecretVariable = "CTX_UI_PROVIDER_SECRET";

    private static Command OAuthCommand(CliLeaf leaf, TextWriter output, TextReader input)
    {
        var provider = new Argument<string>("provider") { Description = ServerText.ProviderNameArgument };
        var clientId = new Option<string>("--client-id") { Description = ServerText.ProviderClientIdOption, Required = true };
        var secret = new Option<string?>("--client-secret") { Description = ServerText.ProviderSecretOption };
        var secretFromInput = new Option<bool>("--client-secret-stdin") { Description = ServerText.ProviderSecretStdinOption };
        var issuer = new Option<string?>("--issuer") { Description = ServerText.ProviderIssuerOption };
        var title = new Option<string?>("--title") { Description = ServerText.ProviderTitleOption };
        var devices = new Option<string?>("--devices") { Description = ServerText.OAuthDevicesOption };
        var directKey = new Option<string?>("--direct-key") { Description = ServerText.OAuthDirectKeyOption };
        var idleDays = new Option<string?>("--idle-days") { Description = ServerText.OAuthIdleDaysOption };
        var selfRevoke = new Option<string?>("--self-revoke") { Description = ServerText.OAuthSelfRevokeOption };

        Task Change(IServiceProvider services, SignInSettingsUpdate update, CancellationToken ct) =>
            services.GetRequiredService<SignInSettingsService>().UpdateAsync(update, PublicUrl(services), ct);

        string? ProviderSecret(ParseResult parse)
        {
            if (parse.GetValue(secret) is { } given)
                return given;

            if (parse.GetValue(secretFromInput))
                return input.ReadLine()?.Trim() is { Length: > 0 } line ? line : throw new DomainException(ServerText.SecretEmpty);

            return Environment.GetEnvironmentVariable(ProviderSecretVariable);
        }

        return new Command("oauth", ServerText.OAuthCommand)
        {
            leaf("status", ServerText.OAuthStatusCommand, [], async (_, services, ct) =>
            {
                var settings = services.GetRequiredService<SignInSettingsService>();
                var current = await settings.GetAsync(ct);
                var publicUrl = PublicUrl(services);
                output.WriteLine(string.Format(ServerText.OAuthState, OnOff(current.Enabled)));
                if (!OAuthAvailability.IsAllowed(publicUrl))
                    output.WriteLine(string.Format(ServerText.OAuthUnavailable, publicUrl));
                else if (current.Enabled)
                    output.WriteLine(string.Format(ServerText.OAuthDiscoveryLine, $"{publicUrl}/.well-known/oauth-authorization-server"));

                output.WriteLine(string.Format(ServerText.OAuthKeySignIn, OnOff(current.KeySignIn)));
                output.WriteLine(string.Format(ServerText.OAuthIdleDays, current.IdleDays));
                output.WriteLine(string.Format(ServerText.OAuthDefaultDevices, DeviceLimitText(current.DefaultMaxDevices)));
                output.WriteLine(string.Format(ServerText.OAuthDefaultDirectKey, YesNo(current.DefaultDirectKey)));
                output.WriteLine(string.Format(ServerText.OAuthSelfRevoke, YesNo(current.SelfRevoke)));
                var providers = await settings.ProvidersAsync(ct);
                if (providers.Count == 0)
                {
                    output.WriteLine(ServerText.OAuthNoProviders);
                    return;
                }

                output.WriteLine(ServerText.OAuthProviders);
                foreach (var saved in providers)
                {
                    output.WriteLine(string.Format(
                        ServerText.OAuthProviderLine, saved.Name, OnOff(saved.Enabled), $"{publicUrl}/connect/callback/{saved.Name}"));
                }
            }),
            leaf("on", ServerText.OAuthOnCommand, [], async (_, services, ct) =>
            {
                await Change(services, new SignInSettingsUpdate { Enabled = true }, ct);
                output.WriteLine(ServerText.OAuthEnabledDone);
            }),
            leaf("off", ServerText.OAuthOffCommand, [], async (_, services, ct) =>
            {
                var withoutKey = await services.GetRequiredService<SignInSettingsService>().ClientsWithoutDirectKeyAsync(ct);
                if (withoutKey > 0)
                    output.WriteLine(string.Format(ServerText.OAuthOffWarning, withoutKey));

                await Change(services, new SignInSettingsUpdate { Enabled = false }, ct);
                output.WriteLine(ServerText.OAuthDisabledDone);
            }),
            leaf("set", ServerText.OAuthSetCommand, [devices, directKey, idleDays, selfRevoke], async (parse, services, ct) =>
            {
                var limit = parse.GetValue(devices);
                var direct = parse.GetValue(directKey);
                var idle = parse.GetValue(idleDays);
                var revoke = parse.GetValue(selfRevoke);
                if (limit is null && direct is null && idle is null && revoke is null)
                    throw new DomainException(ServerText.OAuthSetNothing);

                await Change(
                    services,
                    new SignInSettingsUpdate
                    {
                        ChangeDefaultMaxDevices = limit is not null,
                        DefaultMaxDevices = limit is null ? null : DevicesOption(limit, noneAllowed: true),
                        DefaultDirectKey = direct is null ? null : OnOffOption(directKey.Name, direct),
                        IdleDays = idle is null ? null : NumberOption(idleDays.Name, idle),
                        SelfRevoke = revoke is null ? null : OnOffOption(selfRevoke.Name, revoke),
                    },
                    ct);
                output.WriteLine(ServerText.OAuthSettingsSaved);
            }),
            new Command("key", ServerText.OAuthKeyCommand)
            {
                leaf("on", ServerText.OAuthKeyOnCommand, [], async (_, services, ct) =>
                {
                    await Change(services, new SignInSettingsUpdate { KeySignIn = true }, ct);
                    output.WriteLine(ServerText.KeySignInOnDone);
                }),
                leaf("off", ServerText.OAuthKeyOffCommand, [], async (_, services, ct) =>
                {
                    await Change(services, new SignInSettingsUpdate { KeySignIn = false }, ct);
                    output.WriteLine(ServerText.KeySignInOffDone);
                }),
            },
            new Command("provider", ServerText.OAuthProviderCommand)
            {
                leaf(
                    "set",
                    ServerText.ProviderSetCommand,
                    [provider, clientId, secret, secretFromInput, issuer, title],
                    async (parse, services, ct) =>
                    {
                        var info = ProviderCatalog.Get(parse.GetValue(provider)!);
                        await services.GetRequiredService<SignInSettingsService>().SetProviderAsync(
                            new ProviderSetup(info.Name, parse.GetValue(clientId)!, ProviderSecret(parse), parse.GetValue(issuer), parse.GetValue(title)), ct);
                        output.WriteLine(string.Format(ServerText.ProviderSaved, info.Name, $"{PublicUrl(services)}/connect/callback/{info.Name}"));
                    }),
                leaf("on", ServerText.ProviderOnCommand, [provider], async (parse, services, ct) =>
                {
                    var info = ProviderCatalog.Get(parse.GetValue(provider)!);
                    await services.GetRequiredService<SignInSettingsService>().SetProviderEnabledAsync(info.Name, true, ct);
                    output.WriteLine(string.Format(ServerText.ProviderEnabledDone, info.Name));
                }),
                leaf("off", ServerText.ProviderOffCommand, [provider], async (parse, services, ct) =>
                {
                    var info = ProviderCatalog.Get(parse.GetValue(provider)!);
                    await services.GetRequiredService<SignInSettingsService>().SetProviderEnabledAsync(info.Name, false, ct);
                    output.WriteLine(string.Format(ServerText.ProviderDisabledDone, info.Name));
                }),
                leaf("remove", ServerText.ProviderRemoveCommand, [provider], async (parse, services, ct) =>
                {
                    var info = ProviderCatalog.Get(parse.GetValue(provider)!);
                    await services.GetRequiredService<SignInSettingsService>().RemoveProviderAsync(info.Name, ct);
                    output.WriteLine(string.Format(ServerText.ProviderRemovedDone, info.Name));
                }),
                leaf("list", ServerText.ProviderListCommand, [], async (_, services, ct) =>
                {
                    var providers = await services.GetRequiredService<SignInSettingsService>().ProvidersAsync(ct);
                    foreach (var saved in providers)
                        output.WriteLine(string.Format(ServerText.ProviderListLine, saved.Name, OnOff(saved.Enabled), saved.ClientId, saved.Issuer).TrimEnd());

                    if (providers.Count == 0)
                        output.WriteLine(ServerText.NoProviderList);
                }),
            },
        };
    }

    private static Command[] ClientDeviceCommands(CliLeaf leaf, TextWriter output, Argument<string> name)
    {
        var deviceId = new Argument<string?>("id") { Description = ServerText.DeviceIdArgument, Arity = ArgumentArity.ZeroOrOne };
        var all = new Option<bool>("--all") { Description = ServerText.DeviceAllOption };
        var provider = new Argument<string>("provider") { Description = ServerText.UnlinkProviderArgument };

        return
        [
            leaf("devices", ServerText.ClientDevicesCommand, [name], async (parse, services, ct) =>
            {
                var client = await services.GetRequiredService<ClientService>().GetAsync(parse.GetValue(name)!, ct);
                var devices = await services.GetRequiredService<DeviceService>().ListAsync(client.Id, DateTimeOffset.MinValue, ct);
                if (devices.Count == 0)
                {
                    output.WriteLine(ServerText.NoDevices);
                    return;
                }

                output.WriteLine(ServerText.DeviceHeader);
                foreach (var device in devices)
                {
                    output.WriteLine(string.Format(
                        ServerText.DeviceLine,
                        device.Id.ToString()[..DeviceIdLength],
                        device.Name,
                        device.LastUsedAt is { } used ? Date(used, "yyyy-MM-dd HH:mm") : ServerText.DeviceNeverUsed,
                        device.SignInIp,
                        SignedInWithText(device.SignedInWith),
                        device.RevokedReason is { } reason
                            ? ServerText.ResourceManager.GetString("DeviceReason_" + reason, CultureInfo.CurrentUICulture)
                            : ServerText.DeviceActive));
                    if (device.Apps.Count > 0)
                        output.WriteLine(string.Format(ServerText.DeviceApps, string.Join(", ", device.Apps.Select(app => app.AppName))));
                }
            }),
            leaf("device-off", ServerText.ClientDeviceOffCommand, [name, deviceId, all], async (parse, services, ct) =>
            {
                var client = await services.GetRequiredService<ClientService>().GetAsync(parse.GetValue(name)!, ct);
                var devices = services.GetRequiredService<DeviceService>();
                if (parse.GetValue(all))
                {
                    output.WriteLine(string.Format(ServerText.DevicesOffDone, await devices.RevokeAllAsync(client.Id, DeviceRevocation.Admin, ct)));
                    return;
                }

                var prefix = parse.GetValue(deviceId) ?? throw new DomainException(ServerText.DeviceOffTarget);
                var matching = (await devices.ListAsync(client.Id, DateTimeOffset.MinValue, ct))
                    .Where(device => device.RevokedAt is null && device.Id.ToString().StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var target = matching.Count switch
                {
                    0 => throw new DomainException(string.Format(ServerText.DeviceNoMatch, prefix)),
                    1 => matching[0],
                    _ => throw new DomainException(string.Format(ServerText.DeviceAmbiguous, prefix)),
                };
                await devices.RevokeAsync(client.Id, target.Id, DeviceRevocation.Admin, ct);
                output.WriteLine(string.Format(ServerText.DeviceOffDone, target.Name));
            }),
            leaf("invite", ServerText.ClientInviteCommand, [name], async (parse, services, ct) =>
            {
                var client = await services.GetRequiredService<ClientService>().GetAsync(parse.GetValue(name)!, ct);
                var code = await services.GetRequiredService<ClientLinkService>().InviteAsync(client.Id, ct);
                var until = services.GetRequiredService<TimeProvider>().GetUtcNow() + InviteCode.Lifetime;
                output.WriteLine(string.Format(ServerText.InviteLine, $"{PublicUrl(services)}/connect/invite/{code}"));
                output.WriteLine(string.Format(ServerText.InviteUntil, Date(until, "yyyy-MM-dd HH:mm")));
            }),
            leaf("links", ServerText.ClientLinksCommand, [name], async (parse, services, ct) =>
            {
                var client = await services.GetRequiredService<ClientService>().GetAsync(parse.GetValue(name)!, ct);
                var links = await services.GetRequiredService<ClientLinkService>().ListAsync(client.Id, ct);
                foreach (var link in links)
                    output.WriteLine(string.Format(ServerText.LinkLine, link.Provider, link.Display, Date(link.LinkedAt, "yyyy-MM-dd")));

                if (links.Count == 0)
                    output.WriteLine(ServerText.NoLinks);
            }),
            leaf("unlink", ServerText.ClientUnlinkCommand, [name, provider], async (parse, services, ct) =>
            {
                var client = await services.GetRequiredService<ClientService>().GetAsync(parse.GetValue(name)!, ct);
                var info = ProviderCatalog.Get(parse.GetValue(provider)!);
                await services.GetRequiredService<ClientLinkService>().UnlinkAsync(client.Id, info.Name, ct);
                output.WriteLine(string.Format(ServerText.LinkRemovedDone, info.Name, client.Name));
            }),
        ];
    }

    private const int DeviceIdLength = 8;

    private static string OnOff(bool value) => value ? ServerText.StateOn : ServerText.StateOff;

    private static string YesNo(bool value) => value ? ServerText.Yes : ServerText.No;

    private static string DeviceLimitText(int? limit) => limit is { } value ? value.ToString(CultureInfo.InvariantCulture) : ServerText.DeviceLimitNone;

    private static string SignedInWithText(string method) =>
        method == Device.ByKey ? ServerText.DeviceByKey : ProviderCatalog.Find(method)?.Title ?? method;

    private static ClientAccessPolicy? AccessFrom(string? devices, string? directKey, ClientAccessPolicy current, bool noneAllowed)
    {
        if (devices is null && directKey is null)
            return null;

        return new ClientAccessPolicy(
            devices is null ? current.MaxDevices : DevicesOption(devices, noneAllowed),
            directKey is null ? current.DirectKey : OnOffOption("--direct-key", directKey));
    }

    private static int? DevicesOption(string text, bool noneAllowed) =>
        noneAllowed && text == LimitArgument.None ? null : NumberOption("--devices", text);

    private static int NumberOption(string option, string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new DomainException(string.Format(ServerText.NumberExpected, option, text));

    private static bool OnOffOption(string option, string text) => text switch
    {
        "on" => true,
        "off" => false,
        _ => throw new DomainException(string.Format(ServerText.OnOffExpected, option, text)),
    };
}
