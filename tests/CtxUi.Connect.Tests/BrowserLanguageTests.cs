using Microsoft.AspNetCore.Http;
using CtxUi.Connect.SignIn;

namespace CtxUi.Connect.Tests;

public class BrowserLanguageTests
{
    private static async Task<string?> CultureAsync(string path)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = path;
        http.Request.Headers.AcceptLanguage = "ru-RU,ru;q=0.9,en;q=0.8";
        var result = await new BrowserLanguage().DetermineProviderCultureResult(http);
        return result?.UICultures.FirstOrDefault().Value;
    }

    [Fact]
    public async Task Sign_in_pages_follow_the_browser() => Assert.Equal("ru-RU", await CultureAsync("/connect/signin"));

    [Theory]
    [InlineData("/mcp/learn")]
    [InlineData("/connector")]
    [InlineData("/")]
    public async Task Other_paths_ignore_the_browser(string path) => Assert.Null(await CultureAsync(path));
}
