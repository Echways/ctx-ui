using System.Net;

namespace CtxUi.Server;

public static class ProxyTrust
{
    public static (IReadOnlyList<IPAddress> Proxies, IReadOnlyList<IPNetwork> Networks) Parse(string? value)
    {
        List<IPAddress> proxies = [IPAddress.IPv6Loopback];
        List<IPNetwork> networks = [IPNetwork.Parse("127.0.0.0/8")];
        foreach (var item in (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (item.Contains('/'))
                networks.Add(IPNetwork.Parse(item));
            else if (IPAddress.TryParse(item, out var address))
                proxies.Add(address);
            else
                throw new FormatException($"not an IP address or network: {item}");
        }

        return (proxies, networks);
    }

    public static bool IsValid(string? value)
    {
        try
        {
            Parse(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
