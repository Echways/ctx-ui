using System.Net;
using CtxUi.Connect.Registration;

namespace CtxUi.Connect.Tests;

public class PublicAddressPolicyTests
{
    private readonly PublicAddressPolicy _policy = new();

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.5.4")]
    [InlineData("192.168.1.1")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("fe80::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:127.0.0.1")]
    public void Private_and_loopback_addresses_are_refused(string address) =>
        Assert.False(_policy.AllowsAddress(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("160.79.104.10")]
    [InlineData("2607:6bc0::10")]
    [InlineData("::ffff:8.8.8.8")]
    public void Public_addresses_are_allowed(string address) =>
        Assert.True(_policy.AllowsAddress(IPAddress.Parse(address)));

    [Theory]
    [InlineData("https://claude.ai/oauth/metadata", true)]
    [InlineData("https://claude.ai:443/oauth/metadata", true)]
    [InlineData("http://claude.ai/oauth/metadata", false)]
    [InlineData("https://claude.ai:8443/oauth/metadata", false)]
    [InlineData("https://user@claude.ai/oauth/metadata", false)]
    [InlineData("https://10.0.0.1/oauth/metadata", false)]
    [InlineData("https://claude.ai/", false)]
    [InlineData("https://claude.ai", false)]
    public void Http_and_other_ports_are_refused(string uri, bool allowed) =>
        Assert.Equal(allowed, _policy.AllowsUri(new Uri(uri)));
}
