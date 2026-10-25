using AspNet.Security.OAuth.Discord;
using AspNet.Security.OAuth.GitHub;
using AspNet.Security.OAuth.GitLab;
using AspNet.Security.OAuth.VkId;
using AspNet.Security.OAuth.Yandex;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.MicrosoftAccount;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using CtxUi.Core;
using CtxUi.Core.SignIn;

namespace CtxUi.Connect.External;

public sealed record ProviderInvalidator(string Name, Action<IServiceProvider> Invalidate);

public static class ProviderSchemes
{
    public const string ExternalScheme = OwnCookies.External;
    public const string ProviderItem = "ctx_provider";
    public const string TelegramAuthority = "https://oauth.telegram.org";

    public static readonly TimeSpan ExternalLifetime = TimeSpan.FromMinutes(10);

    public static string CallbackPath(string name) => ConnectServices.Prefix + "/callback/" + name;

    public static IServiceCollection AddExternalProviders(this IServiceCollection services)
    {
        var auth = services.AddAuthentication();
        auth.AddCookie(ExternalScheme, options =>
        {
            options.Cookie.Name = OwnCookies.External;
            options.Cookie.Path = ConnectServices.Prefix;
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.IsEssential = true;
            options.ExpireTimeSpan = ExternalLifetime;
            options.SlidingExpiration = false;
        });

        Add<GoogleOptions>(services, auth, "google", (name, configure) => auth.AddGoogle(name, configure));
        Add<MicrosoftAccountOptions>(services, auth, "microsoft", (name, configure) => auth.AddMicrosoftAccount(name, configure));
        Add<GitHubAuthenticationOptions>(services, auth, "github", (name, configure) => auth.AddGitHub(name, configure));
        Add<GitLabAuthenticationOptions>(services, auth, "gitlab", (name, configure) => auth.AddGitLab(name, configure));
        Add<YandexAuthenticationOptions>(services, auth, "yandex", (name, configure) => auth.AddYandex(name, configure));
        Add<VkIdAuthenticationOptions>(services, auth, "vkid", (name, configure) => auth.AddVkId(name, configure));
        Add<DiscordAuthenticationOptions>(services, auth, "discord", (name, configure) => auth.AddDiscord(name, configure));
        Add<OpenIdConnectOptions>(services, auth, "telegram", (name, configure) => auth.AddOpenIdConnect(name, configure), options =>
        {
            options.Authority = TelegramAuthority;
            options.Scope.Clear();
            options.Scope.Add("openid");
            options.Scope.Add("profile");
        });
        Add<OpenIdConnectOptions>(services, auth, ProviderCatalog.Oidc, (name, configure) => auth.AddOpenIdConnect(name, configure), options =>
        {
            options.Scope.Clear();
            options.Scope.Add("openid");
            options.Scope.Add("profile");
            options.Scope.Add("email");
        });
        services.AddSingleton<ProviderOptionsCache>();
        return services;
    }

    private static void Add<TOptions>(
        IServiceCollection services,
        AuthenticationBuilder auth,
        string name,
        Func<string, Action<TOptions>, AuthenticationBuilder> register,
        Action<OpenIdConnectOptions>? configureOidc = null)
        where TOptions : RemoteAuthenticationOptions, new()
    {
        register(name, options =>
        {
            options.SignInScheme = ExternalScheme;
            options.CallbackPath = CallbackPath(name);
            options.SaveTokens = false;
            options.CorrelationCookie.SameSite = SameSiteMode.Lax;
            options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            if (options is OpenIdConnectOptions oidc)
            {
                oidc.ResponseType = "code";
                oidc.ResponseMode = "query";
                oidc.NonceCookie.SameSite = SameSiteMode.Lax;
                oidc.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                oidc.UsePkce = true;
                oidc.GetClaimsFromUserInfoEndpoint = false;
                oidc.MapInboundClaims = true;
                configureOidc?.Invoke(oidc);
            }
        });
        services.AddSingleton<IConfigureOptions<TOptions>, ProviderOptionsSync<TOptions>>();
        services.AddSingleton(new ProviderInvalidator(name, provider => provider.GetRequiredService<IOptionsMonitorCache<TOptions>>().TryRemove(name)));
    }
}
