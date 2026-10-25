using System.Text.Json;
using Microsoft.AspNetCore.Http;
using CtxUi.Core.Routing;

namespace CtxUi.Gateway.Tests;

public class RejectionTests
{
    private const string Metadata = "https://mcp.example.org/.well-known/oauth-protected-resource/mcp/learn";

    private static async Task<HttpContext> WriteAsync(AccessStatus status, string? metadata)
    {
        var context = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        await Rejection.WriteAsync(context, new AccessDecision(status), metadata);
        return context;
    }

    private static string Error(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var body = JsonDocument.Parse(context.Response.Body);
        return body.RootElement.GetProperty("error").GetString()!;
    }

    [Fact]
    public async Task Unauthorized_points_to_the_resource_metadata_when_oauth_is_on()
    {
        var context = await WriteAsync(AccessStatus.TokenMissing, Metadata);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal(
            "Bearer resource_metadata=\"https://mcp.example.org/.well-known/oauth-protected-resource/mcp/learn\", scope=\"mcp\"",
            context.Response.Headers.WWWAuthenticate.ToString());
    }

    [Theory]
    [InlineData(AccessStatus.TokenInvalid, "token_invalid")]
    [InlineData(AccessStatus.TokenUnknown, "token_unknown")]
    [InlineData(AccessStatus.DeviceRevoked, "device_revoked")]
    public async Task Invalid_token_adds_the_error_parameter(AccessStatus status, string error)
    {
        var context = await WriteAsync(status, Metadata);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal(
            $"Bearer resource_metadata=\"{Metadata}\", scope=\"mcp\", error=\"invalid_token\"",
            context.Response.Headers.WWWAuthenticate.ToString());
        Assert.Equal(error, Error(context));
    }

    [Fact]
    public async Task Direct_key_off_points_to_the_metadata_without_the_error_parameter()
    {
        var context = await WriteAsync(AccessStatus.DirectKeyOff, Metadata);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal($"Bearer resource_metadata=\"{Metadata}\", scope=\"mcp\"", context.Response.Headers.WWWAuthenticate.ToString());
        Assert.Equal("direct_key_off", Error(context));
    }

    [Theory]
    [InlineData(AccessStatus.TokenMissing)]
    [InlineData(AccessStatus.TokenUnknown)]
    [InlineData(AccessStatus.DirectKeyOff)]
    [InlineData(AccessStatus.TokenInvalid)]
    public async Task Without_oauth_the_challenge_stays_plain_bearer(AccessStatus status) =>
        Assert.Equal("Bearer", (await WriteAsync(status, null)).Response.Headers.WWWAuthenticate.ToString());

    [Fact]
    public async Task Not_found_has_no_challenge()
    {
        var context = await WriteAsync(AccessStatus.InboundNotFound, Metadata);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Headers.WWWAuthenticate.Count);
    }
}
