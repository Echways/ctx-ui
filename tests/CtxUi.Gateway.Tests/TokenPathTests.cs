using Microsoft.AspNetCore.Http.Features;

namespace CtxUi.Gateway.Tests;

public class TokenPathTests
{
    [Theory]
    [InlineData("/k/tnk_abc/mcp/echo", "tnk_abc", "/mcp/echo")]
    [InlineData("/k/tnk_abc", "tnk_abc", "/")]
    [InlineData("/k/tnk_abc/", "tnk_abc", "/")]
    [InlineData("/k//mcp/echo", "", "/mcp/echo")]
    public void Path_with_the_marker_is_split(string path, string token, string rest)
    {
        Assert.True(TokenPath.TrySplit(path, out var actualToken, out var actualRest));
        Assert.Equal(token, actualToken);
        Assert.Equal(rest, actualRest);
    }

    [Theory]
    [InlineData("/mcp/echo")]
    [InlineData("/kk/x/mcp/echo")]
    [InlineData("/K/x/mcp/echo")]
    [InlineData("/")]
    [InlineData(null)]
    public void Other_paths_are_left_alone(string? path) =>
        Assert.False(TokenPath.TrySplit(path, out _, out _));

    [Fact]
    public async Task Middleware_hides_the_token_from_everything_downstream()
    {
        var context = await Pipeline.RunAsync("/k/tnk_SECRET/mcp/echo", "?x=1");

        Assert.Equal("/mcp/echo", context.Request.Path.Value);
        Assert.Equal("/mcp/echo?x=1", context.Features.GetRequiredFeature<IHttpRequestFeature>().RawTarget);
        Assert.Equal("?x=1", context.Request.QueryString.Value);
        Assert.Equal("tnk_SECRET", TokenPath.Get(context));
    }

    [Fact]
    public async Task Middleware_leaves_ordinary_requests_untouched()
    {
        var context = await Pipeline.RunAsync("/mcp/echo", "?x=1");

        Assert.Equal("/mcp/echo", context.Request.Path.Value);
        Assert.Equal("/mcp/echo?x=1", context.Features.GetRequiredFeature<IHttpRequestFeature>().RawTarget);
        Assert.Null(TokenPath.Get(context));
    }
}
