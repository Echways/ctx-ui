using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.WebEncoders;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Server;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenIddict.EntityFrameworkCore.Models;
using CtxUi.Connect.Endpoints;
using CtxUi.Connect.External;
using CtxUi.Connect.Metadata;
using CtxUi.Connect.Registration;
using CtxUi.Connect.Resources;
using CtxUi.Connect.SignIn;
using CtxUi.Connect.Tokens;
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
    private const string FramingPolicy = "default-src 'self'; img-src 'self' data:; frame-ancestors 'none'";
    public const int SignInAttempts = 10;

    public const string Favicon = "/_content/CtxUi.Panel/favicon.svg";

    public static readonly PathString[] OpenPaths = [Prefix, WellKnown, "/_content/CtxUi.Connect", "/_content/CtxUi.Panel/fonts", Favicon];

    public static IServiceCollection AddCtxUiConnect(this IServiceCollection services, IConfiguration section, string keysDir)
    {
        services.Configure<ConnectOptions>(section);
        var issuer = OAuthResource.Issuer((section.Get<ConnectOptions>() ?? new ConnectOptions()).PublicUrl);
        services.AddSingleton<OAuthGate>();
        services.AddSingleton<IAccessTokenValidator, AccessTokenValidator>();
        services.TryAddSingleton<IMetadataAddressPolicy, PublicAddressPolicy>();
        services.AddSingleton<ClientMetadataFetcher>();
        services.AddScoped<RegisteredApps>();
        services.AddScoped<ConnectTargets>();
        services.Configure<WebEncoderOptions>(options => options.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All));
        services.AddScoped<TokenReuseHandler>();
        services.AddSingleton<DeviceJanitor>();
        services.AddHostedService(provider => provider.GetRequiredService<DeviceJanitor>());
        services.PostConfigure<RequestLocalizationOptions>(options => options.RequestCultureProviders.Add(new BrowserLanguage()));
        services.AddAuthentication().AddConnectSession();
        services.AddExternalProviders();
        services.AddRateLimiter(limiter =>
        {
            limiter.OnRejected = (context, _) => new ValueTask(
                ConnectResults.Notice(ConnectText.TooManyAttempts, StatusCodes.Status429TooManyRequests).ExecuteAsync(context.HttpContext));
            limiter.AddPolicy(PageEndpoints.SignInPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = SignInAttempts, Window = TimeSpan.FromMinutes(1) }));
        });
        services.AddSingleton<ResourceHandlers>();
        services.AddHttpClient(ClientMetadataFetcher.HttpClientName, client => client.Timeout = ClientMetadataFetcher.Timeout)
            .ConfigurePrimaryHttpMessageHandler(provider => ClientMetadataFetcher.Handler(provider.GetRequiredService<IMetadataAddressPolicy>()));
        services.AddOpenIddict()
            .AddCore(core =>
            {
                core.UseEntityFrameworkCore().UseDbContext<CtxUiDbContext>();
                core.ReplaceApplicationManager<OpenIddictEntityFrameworkCoreApplication, CtxUiApplicationManager>();
            })
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
                    .AddEventHandler(IssuerResponseHandler.Descriptor(issuer))
                    .Configure(options => options.CodeChallengeMethods.Remove(CodeChallengeMethods.Plain));

                foreach (var descriptor in ClientMetadataHandler.Descriptors.Append(ResourceHandlers.Descriptor).Append(TokenReuseHandler.Descriptor))
                    server.AddEventHandler(descriptor);

                var aspNetCore = server.UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableTokenEndpointPassthrough()
                    .EnableErrorPassthrough();
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
            if (path.StartsWithSegments(Prefix))
            {
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers.ContentSecurityPolicy = FramingPolicy;
                    context.Response.Headers.XFrameOptions = "DENY";
                    context.Response.Headers.CacheControl = "no-store";
                    return Task.CompletedTask;
                });
            }

            if (gate.IsActive || !(path.StartsWithSegments(Prefix) || path.StartsWithSegments(WellKnown)))
                return next(context);

            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        });
    }

    public static WebApplication MapCtxUiConnect(this WebApplication app)
    {
        app.MapResourceMetadata();
        app.MapRegistration();
        app.MapAuthorize();
        app.MapToken();
        app.MapPages();
        app.MapExternal();
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
