using CtxUi.Connect.SignIn;

namespace CtxUi.Connect.Tests;

public class DeviceNamesTests
{
    [Fact]
    public void Chrome_on_windows() => Assert.Equal(
        "Chrome · Windows",
        DeviceNames.From("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36"));

    [Fact]
    public void Firefox_on_linux() => Assert.Equal(
        "Firefox · Ubuntu",
        DeviceNames.From("Mozilla/5.0 (X11; Ubuntu; Linux x86_64; rv:131.0) Gecko/20100101 Firefox/131.0"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unrecognized-agent")]
    public void Empty_agent_is_unknown(string? agent) => Assert.Equal("Неизвестное устройство", DeviceNames.From(agent));
}
