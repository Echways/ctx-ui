using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CtxUi.IntegrationTests;

public sealed class FakeOidcProvider : IAsyncDisposable
{
    private const string KeyId = "fake-key";

    private readonly WebApplication _app;
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly ConcurrentDictionary<string, (string ClientId, string? Nonce)> _codes = new();

    private FakeOidcProvider(WebApplication app) => _app = app;

    public string Subject { get; set; } = "user-1";

    public string Email { get; set; } = "ivanov@example.org";

    public string Issuer => _app.Urls.First();

    public static async Task<FakeOidcProvider> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var provider = new FakeOidcProvider(builder.Build());
        provider.Map();
        await provider._app.StartAsync();
        return provider;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        _rsa.Dispose();
    }

    private void Map()
    {
        _app.MapGet("/.well-known/openid-configuration", () => Results.Json(new Dictionary<string, object>
        {
            ["issuer"] = Issuer,
            ["authorization_endpoint"] = Issuer + "/authorize",
            ["token_endpoint"] = Issuer + "/token",
            ["jwks_uri"] = Issuer + "/jwks",
            ["response_types_supported"] = new[] { "code" },
            ["subject_types_supported"] = new[] { "public" },
            ["id_token_signing_alg_values_supported"] = new[] { SecurityAlgorithms.RsaSha256 },
        }));
        _app.MapGet("/jwks", () =>
        {
            var parameters = _rsa.ExportParameters(false);
            return Results.Json(new
            {
                keys = new[]
                {
                    new
                    {
                        kty = "RSA",
                        use = "sig",
                        alg = SecurityAlgorithms.RsaSha256,
                        kid = KeyId,
                        n = Base64UrlEncoder.Encode(parameters.Modulus),
                        e = Base64UrlEncoder.Encode(parameters.Exponent),
                    },
                },
            });
        });
        _app.MapGet("/authorize", (HttpRequest request) =>
        {
            var query = request.Query;
            var code = Guid.NewGuid().ToString("N");
            _codes[code] = (query["client_id"].ToString(), query["nonce"]);
            return Results.Redirect(QueryHelpers.AddQueryString(
                query["redirect_uri"].ToString(),
                new Dictionary<string, string?> { ["code"] = code, ["state"] = query["state"] }));
        });
        _app.MapPost("/token", async (HttpRequest request) =>
        {
            var form = await request.ReadFormAsync();
            if (!_codes.TryRemove(form["code"].ToString(), out var issued))
                return Results.BadRequest(new { error = "invalid_grant" });

            var idToken = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = Issuer,
                Audience = issued.ClientId,
                Expires = DateTime.UtcNow.AddMinutes(5),
                Claims = new Dictionary<string, object>
                {
                    ["sub"] = Subject,
                    ["email"] = Email,
                    ["nonce"] = issued.Nonce ?? "",
                },
                SigningCredentials = new SigningCredentials(new RsaSecurityKey(_rsa) { KeyId = KeyId }, SecurityAlgorithms.RsaSha256),
            });
            return Results.Json(new { access_token = "fake-access", token_type = "Bearer", expires_in = 300, id_token = idToken });
        });
    }
}
