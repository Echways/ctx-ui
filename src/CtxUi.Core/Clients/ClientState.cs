namespace CtxUi.Core.Clients;

public enum ClientStatus
{
    Active,
    Disabled,
    Expired,
    Exhausted,
}

public static class ClientState
{
    public static ClientStatus Of(Client client, DateTimeOffset now) =>
        !client.Enabled ? ClientStatus.Disabled
        : client.ExpiresAt <= now ? ClientStatus.Expired
        : QuotaPeriod.UsedCalls(client, now) >= client.QuotaCalls ? ClientStatus.Exhausted
        : ClientStatus.Active;

    public static string Text(ClientStatus status) => status switch
    {
        ClientStatus.Disabled => "отключён",
        ClientStatus.Expired => "истёк",
        ClientStatus.Exhausted => "исчерпан",
        ClientStatus.Active => "активен",
        _ => status.ToString(),
    };
}
