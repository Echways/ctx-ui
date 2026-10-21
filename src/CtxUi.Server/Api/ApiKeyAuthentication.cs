using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using CtxUi.Core.ApiKeys;

namespace CtxUi.Server.Api;

public sealed class ApiKeyAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApiKeyService keys)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";

    private const string Bearer = "Bearer ";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith(Bearer, StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var key = await keys.FindAsync(header[Bearer.Length..].Trim(), Context.RequestAborted);
        if (key is null)
            return AuthenticateResult.Fail("API key did not match");

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, key.Name)], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }
}
