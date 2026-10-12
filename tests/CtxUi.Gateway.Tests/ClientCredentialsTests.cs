namespace CtxUi.Gateway.Tests;

public class ClientCredentialsTests
{
    [Theory]
    [InlineData("Bearer tnk_abc", "tnk_abc")]
    [InlineData("bearer tnk_abc", "tnk_abc")]
    [InlineData("Bearer   tnk_abc  ", "tnk_abc")]
    [InlineData("Bearer ", "")]
    public async Task Bearer_header_gives_the_token(string header, string expected) =>
        Assert.Equal(expected, ClientCredentials.Read(await Pipeline.RunAsync("/mcp/echo", authorization: header)));

    [Theory]
    [InlineData(null)]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("tnk_abc")]
    public async Task Missing_or_foreign_scheme_gives_nothing(string? header) =>
        Assert.Null(ClientCredentials.Read(await Pipeline.RunAsync("/mcp/echo", authorization: header)));

    [Fact]
    public async Task Token_in_the_path_wins_over_the_header() =>
        Assert.Equal("tnk_path", ClientCredentials.Read(
            await Pipeline.RunAsync("/k/tnk_path/mcp/echo", authorization: "Bearer tnk_header")));
}
