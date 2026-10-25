using System.Net;

namespace CtxUi.Server.Tests;

public class ProxyTrustTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" , ")]
    public void Loopback_is_always_trusted(string? value)
    {
        var (proxies, networks) = ProxyTrust.Parse(value);

        Assert.Contains(IPAddress.IPv6Loopback, proxies);
        Assert.Contains(IPNetwork.Parse("127.0.0.0/8"), networks);
    }

    [Fact]
    public void Addresses_and_networks_are_parsed()
    {
        var (proxies, networks) = ProxyTrust.Parse("10.0.0.1, 172.16.0.0/12,fd00::/8 ,192.168.1.10");

        Assert.Contains(IPAddress.Parse("10.0.0.1"), proxies);
        Assert.Contains(IPAddress.Parse("192.168.1.10"), proxies);
        Assert.Contains(IPNetwork.Parse("172.16.0.0/12"), networks);
        Assert.Contains(IPNetwork.Parse("fd00::/8"), networks);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0.1,proxy.example.org")]
    public void Garbage_is_refused(string value) =>
        Assert.Throws<FormatException>(() => ProxyTrust.Parse(value));
}
