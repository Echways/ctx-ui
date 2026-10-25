using System.Net;
using System.Net.Sockets;

namespace CtxUi.Connect.Registration;

public sealed class PublicAddressPolicy : IMetadataAddressPolicy
{
    private const int HttpsPort = 443;

    private static readonly IPNetwork[] Blocked =
    [
        IPNetwork.Parse("0.0.0.0/8"),
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("100.64.0.0/10"),
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("169.254.0.0/16"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.0.0.0/24"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("198.18.0.0/15"),
        IPNetwork.Parse("224.0.0.0/3"),
        IPNetwork.Parse("::/127"),
        IPNetwork.Parse("64:ff9b:1::/48"),
        IPNetwork.Parse("fc00::/7"),
        IPNetwork.Parse("fe80::/10"),
        IPNetwork.Parse("ff00::/8"),
    ];

    public bool AllowsUri(Uri uri) =>
        uri.IsAbsoluteUri
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Port == HttpsPort
        && uri.UserInfo.Length == 0
        && uri.AbsolutePath != "/"
        && (uri.HostNameType != UriHostNameType.IPv4 && uri.HostNameType != UriHostNameType.IPv6
            || AllowsAddress(IPAddress.Parse(uri.IdnHost)));

    public bool AllowsAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        return address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6
            && !Blocked.Any(network => network.Contains(address));
    }
}
