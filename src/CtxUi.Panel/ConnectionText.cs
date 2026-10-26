using System.Text;
using Enfilade;
using CtxUi.Core.Clients;

namespace CtxUi.Panel;

public static class ConnectionText
{
    public static string Address(string publicUrl, string slug) => $"{publicUrl.TrimEnd('/')}/mcp/{slug}";

    public static string AddressWithToken(string publicUrl, string token, string slug) =>
        $"{publicUrl.TrimEnd('/')}/k/{token}/mcp/{slug}";

    public static IReadOnlyList<string> Slugs(Client client) =>
        client.Inbounds.Select(link => link.Inbound!.Slug).Order(StringComparer.Ordinal).ToList();

    public static string Message(string publicUrl, Client client, string token, ConnectionMode? mode = null, string? inviteUrl = null)
    {
        mode ??= ConnectionMode.Off;
        var text = new StringBuilder();
        text.Append($"{string.Format(PanelText.MessageIntro, client.Name)}\n\n");
        if (mode.ShowsToken)
            text.Append($"{string.Format(PanelText.MessageToken, token)}\n\n");

        foreach (var slug in Slugs(client))
        {
            text.Append($"{slug}\n");
            text.Append($"  {string.Format(PanelText.MessageAddress, Address(publicUrl, slug))}\n");
            if (!mode.OAuth || mode.DirectKey)
            {
                text.Append($"  {string.Format(PanelText.MessageHeader, token)}\n");
                text.Append($"  {PanelText.MessageNoHeader}\n");
                text.Append($"  {AddressWithToken(publicUrl, token, slug)}\n");
            }

            text.Append('\n');
        }

        if (mode.OAuth)
        {
            text.Append($"{PanelText.MessageBrowserSignIn}\n");
            if (mode.KeySignIn)
                text.Append($"{string.Format(PanelText.MessageSignInKey, token)}\n");
            if (inviteUrl is not null)
                text.Append($"{string.Format(PanelText.MessageInvite, inviteUrl)}\n");

            text.Append('\n');
        }

        var validity = client.ExpiresAt is { } expiresAt
            ? string.Format(PanelText.MessageExpires, Words.Day(expiresAt))
            : PanelText.MessageNoExpiry;
        text.Append($"{validity}\n");
        if (client.QuotaCalls is { } quota)
        {
            var period = client.ResetEveryDays is { } days ? string.Format(PanelText.MessageQuotaEvery, days) : PanelText.MessageQuotaOnce;
            text.Append($"{string.Format(PanelText.MessageQuota, quota, period)}\n");
        }

        text.Append(PanelText.MessageWarning);
        return text.ToString();
    }
}
