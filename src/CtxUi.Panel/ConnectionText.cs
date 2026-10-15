using System.Text;
using CtxUi.Core.Clients;

namespace CtxUi.Panel;

public static class ConnectionText
{
    public static string Address(string publicUrl, string slug) => $"{publicUrl.TrimEnd('/')}/mcp/{slug}";

    public static string AddressWithToken(string publicUrl, string token, string slug) =>
        $"{publicUrl.TrimEnd('/')}/k/{token}/mcp/{slug}";

    public static IReadOnlyList<string> Slugs(Client client) =>
        client.Inbounds.Select(link => link.Inbound!.Slug).Order(StringComparer.Ordinal).ToList();

    public static string Message(string publicUrl, Client client, string token)
    {
        var text = new StringBuilder();
        text.Append($"Доступ к MCP-серверам для «{client.Name}».\n\n");
        text.Append($"Токен: {token}\n\n");
        foreach (var slug in Slugs(client))
        {
            text.Append($"{slug}\n");
            text.Append($"  адрес: {Address(publicUrl, slug)}\n");
            text.Append($"  заголовок: Authorization: Bearer {token}\n");
            text.Append("  если заголовок задать нельзя, адрес с токеном внутри:\n");
            text.Append($"  {AddressWithToken(publicUrl, token, slug)}\n\n");
        }

        text.Append(client.ExpiresAt is { } expiresAt ? $"Доступ действует до {Words.Day(expiresAt)} (UTC).\n" : "Доступ бессрочный.\n");
        if (client.QuotaCalls is { } quota)
        {
            var period = client.ResetEveryDays is { } days ? $"каждые {days} дн." : "на всё время.";
            text.Append($"Вызовов инструментов: {quota} {period}\n");
        }

        text.Append("Токен и адрес с токеном равносильны паролю: не передавайте их дальше.");
        return text.ToString();
    }
}
