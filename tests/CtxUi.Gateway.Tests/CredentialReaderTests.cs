using Microsoft.AspNetCore.Http;
using CtxUi.Core.Clients;
using CtxUi.Core.Routing;
using CtxUi.Core.SignIn;

namespace CtxUi.Gateway.Tests;

public class CredentialReaderTests
{
    private const string Resource = "https://mcp.example.org/mcp/echo";

    private static readonly string Key = AccessToken.Generate();

    private static readonly OAuthGrant Granted = new(Guid.NewGuid(), Guid.NewGuid(), [Resource]);

    private static Task<Credential> ReadAsync(HttpContext context, bool oauthActive = true, IAccessTokenValidator? validator = null) =>
        new CredentialReader(validator ?? new FakeValidator(Granted)).ReadAsync(context, Resource, oauthActive, CancellationToken.None);

    [Fact]
    public async Task Path_token_is_a_key()
    {
        var validator = new FakeValidator(Granted);

        var credential = await ReadAsync(await Pipeline.RunAsync($"/k/{Key}/mcp/echo"), validator: validator);

        Assert.Equal(new Credential(CredentialKind.Key, Key), credential);
        Assert.Equal(0, validator.Calls);
    }

    [Fact]
    public async Task Bearer_tnk_is_a_key_even_with_oauth_on()
    {
        var validator = new FakeValidator(Granted);

        var credential = await ReadAsync(await Pipeline.RunAsync("/mcp/echo", authorization: "Bearer " + Key), validator: validator);

        Assert.Equal(new Credential(CredentialKind.Key, Key), credential);
        Assert.Equal(0, validator.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer ")]
    [InlineData("Basic dXNlcjpwYXNz")]
    public async Task Nothing_usable_is_missing(string? header) =>
        Assert.Equal(CredentialKind.Missing, (await ReadAsync(await Pipeline.RunAsync("/mcp/echo", authorization: header))).Kind);

    [Fact]
    public async Task Other_bearer_with_oauth_on_is_a_grant()
    {
        var credential = await ReadAsync(await Pipeline.RunAsync("/mcp/echo", authorization: "Bearer eyJ.access.token"));

        Assert.Equal(new Credential(CredentialKind.Grant, Grant: Granted), credential);
    }

    [Fact]
    public async Task Other_bearer_with_oauth_off_is_a_key_so_it_stays_token_unknown()
    {
        var validator = new FakeValidator(Granted);

        var credential = await ReadAsync(
            await Pipeline.RunAsync("/mcp/echo", authorization: "Bearer eyJ.access.token"), oauthActive: false, validator: validator);

        Assert.Equal(new Credential(CredentialKind.Key, "eyJ.access.token"), credential);
        Assert.Equal(0, validator.Calls);
    }

    [Fact]
    public async Task Other_bearer_without_a_validator_is_a_key()
    {
        var reader = new CredentialReader(null);

        var credential = await reader.ReadAsync(
            await Pipeline.RunAsync("/mcp/echo", authorization: "Bearer eyJ.access.token"), Resource, true, CancellationToken.None);

        Assert.Equal(CredentialKind.Key, credential.Kind);
    }

    [Fact]
    public async Task Grant_with_another_audience_is_invalid()
    {
        var foreign = Granted with { Audiences = ["https://mcp.example.org/mcp/docs"] };

        var credential = await ReadAsync(
            await Pipeline.RunAsync("/mcp/echo", authorization: "Bearer eyJ.access.token"), validator: new FakeValidator(foreign));

        Assert.Equal(CredentialKind.Invalid, credential.Kind);
    }

    [Fact]
    public async Task Rejected_token_is_invalid()
    {
        var credential = await ReadAsync(
            await Pipeline.RunAsync("/mcp/echo", authorization: "Bearer eyJ.access.token"), validator: new FakeValidator(null));

        Assert.Equal(CredentialKind.Invalid, credential.Kind);
    }

    [Fact]
    public async Task Validator_failure_is_invalid()
    {
        var credential = await ReadAsync(
            await Pipeline.RunAsync("/mcp/echo", authorization: "Bearer eyJ.access.token"), validator: new FakeValidator(null, fail: true));

        Assert.Equal(CredentialKind.Invalid, credential.Kind);
    }

    private sealed class FakeValidator(OAuthGrant? grant, bool fail = false) : IAccessTokenValidator
    {
        public int Calls { get; private set; }

        public Task<OAuthGrant?> ValidateAsync(string token, CancellationToken ct)
        {
            Calls++;
            return fail ? throw new InvalidOperationException("validation failed") : Task.FromResult(grant);
        }
    }
}
