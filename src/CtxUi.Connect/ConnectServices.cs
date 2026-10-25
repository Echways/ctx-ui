using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Server;
using CtxUi.Connect.Metadata;
using CtxUi.Core.Data;
using CtxUi.Core.Routing;
using CtxUi.Core.SignIn;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace CtxUi.Connect;

public static partial class ConnectServices
{
    public const string Prefix = "/connect";
    public const string WellKnown = "/.well-known";
    public const string McpScope = "mcp";

    public static readonly PathString[] OpenPaths = [Prefix, WellKnown, "/_content/CtxUi.Connect", "/_content/CtxUi.Panel/fonts"];

    public static IServiceCollection AddCtxUiConnect(this IServiceCollection services, IConfiguration section, string keysDir)
    {
        services.Configure<ConnectOptions>(section);
        var issuer = OAuthResource.Issuer((section.Get<ConnectOptions>() ?? new ConnectOptions()).PublicUrl);
        services.AddSingleton<OAuthGate>();
        services.AddSingleton<IAccessTokenValidator, AccessTokenValidator>();
        services.AddOpenIddict()
            .AddCore(core => core.UseEntityFrameworkCore().UseDbContext<CtxUiDbContext>())
            .AddServer(server =>
            {
                var issuerUri = Uri.TryCreate(issuer, UriKind.Absolute, out var parsed) ? parsed : null;
                if (issuerUri is not null)
                    server.SetIssuer(issuerUri);

                server.SetAuthorizationEndpointUris("connect/authorize")
                    .SetTokenEndpointUris("connect/token")
                    .SetRevocationEndpointUris("connect/revoke")
                    .SetConfigurationEndpointUris(".well-known/oauth-authorization-server", ".well-known/openid-configuration")
                    .AllowAuthorizationCodeFlow()
                    .AllowRefreshTokenFlow()
                    .RequireProofKeyForCodeExchange()
                    .RegisterScopes(McpScope, Scopes.OfflineAccess)
                    .DisableResourceValidation()
                    .IgnoreResourcePermissions()
                    .SetAuthorizationCodeLifetime(TimeSpan.FromMinutes(1))
                    .SetAccessTokenLifetime(TimeSpan.FromHours(1))
                    .AddEventHandler(ServerMetadataHandler.Descriptor(issuer))
                    .Configure(options => options.CodeChallengeMethods.Remove(CodeChallengeMethods.Plain));

                var aspNetCore = server.UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableTokenEndpointPassthrough();
                if (issuerUri?.Scheme == Uri.UriSchemeHttp)
                    aspNetCore.DisableTransportSecurityRequirement();
            })
            .AddValidation(validation => validation.UseLocalServer());
        services.AddOptions<OpenIddictServerOptions>().Configure<IDataProtectionProvider>((server, protection) =>
        {
            var (signing, encryption) = TokenKeys.LoadOrCreate(keysDir, protection);
            server.SigningCredentials.Add(new SigningCredentials(signing, SecurityAlgorithms.RsaSha256));
            server.EncryptionCredentials.Add(
                new EncryptingCredentials(encryption, SecurityAlgorithms.RsaOAEP, SecurityAlgorithms.Aes256CbcHmacSha512));
        });
        return services;
    }

    public static IApplicationBuilder UseCtxUiConnectGate(this IApplicationBuilder app)
    {
        var gate = app.ApplicationServices.GetRequiredService<OAuthGate>();
        return app.Use((context, next) =>
        {
            var path = context.Request.Path;
            if (gate.IsActive || !(path.StartsWithSegments(Prefix) || path.StartsWithSegments(WellKnown)))
                return next(context);

            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        });
    }

    public static WebApplication MapCtxUiConnect(this WebApplication app)
    {
        app.MapResourceMetadata();
        return app;
    }

    public static void LoadCtxUiConnect(this IServiceProvider services)
    {
        _ = services.GetRequiredService<IOptionsMonitor<OpenIddictServerOptions>>().CurrentValue;
        var publicUrl = services.GetRequiredService<IOptions<ConnectOptions>>().Value.PublicUrl;
        if (services.GetRequiredService<ISnapshotSource>().Current.SignIn.Enabled && !OAuthAvailability.IsAllowed(publicUrl))
            OAuthUnavailable(services.GetRequiredService<ILoggerFactory>().CreateLogger("CtxUi.Connect"), publicUrl);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "browser sign-in (OAuth) is on but does not work: the public address {PublicUrl} must start with https://, set CtxUi__PublicUrl")]
    private static partial void OAuthUnavailable(ILogger logger, string publicUrl);
}
