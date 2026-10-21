namespace CtxUi.Core.Tests;

public class OwnCookiesTests
{
    [Theory]
    [InlineData("ctx-ui", true)]
    [InlineData("ctx-uiC1", true)]
    [InlineData("ctx-uiC12", true)]
    [InlineData(".AspNetCore.Culture", true)]
    [InlineData(".AspNetCore.Antiforgery.Xy7", true)]
    [InlineData("ctx-ui-extra", false)]
    [InlineData("ctx-uiCx", false)]
    [InlineData("session", false)]
    public void Only_cookies_set_by_ctx_ui_are_its_own(string name, bool own) =>
        Assert.Equal(own, OwnCookies.Contains(name));
}
