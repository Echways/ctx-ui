using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CtxUi.Core.Routing;
using CtxUi.Core.SignIn;

namespace CtxUi.Connect.External;

public sealed partial class ProviderOptionsSync<TOptions>(ISnapshotSource snapshot) : IConfigureNamedOptions<TOptions>
    where TOptions : RemoteAuthenticationOptions
{
    private const string Unset = "unset";
    private const string UnsetAuthority = "https://unset.invalid";

    public void Configure(string? name, TOptions options)
    {
        if (name is null || ProviderCatalog.Find(name) is null)
            return;

        var config = snapshot.Current.SignIn.Providers.FirstOrDefault(provider => provider.Name == name);
        var clientId = config?.ClientId ?? Unset;
        var secret = config?.Secret ?? Unset;
        switch (options)
        {
            case OAuthOptions oauth:
                oauth.ClientId = clientId;
                oauth.ClientSecret = secret;
                break;
            case OpenIdConnectOptions oidc:
                oidc.ClientId = clientId;
                oidc.ClientSecret = secret;
                if (name == ProviderCatalog.Oidc)
                    oidc.Authority = config?.Issuer ?? UnsetAuthority;

                oidc.RequireHttpsMetadata = !(Uri.TryCreate(oidc.Authority, UriKind.Absolute, out var authority) && authority.IsLoopback);
                break;
        }

        options.Events.OnRemoteFailure = context =>
        {
            RemoteFailed(context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("CtxUi.Connect"), name, context.Failure);
            context.Response.Redirect(context.Request.PathBase + ExternalEndpoints.FailedPath);
            context.HandleResponse();
            return Task.CompletedTask;
        };
    }

    public void Configure(TOptions options) => Configure(Options.DefaultName, options);

    [LoggerMessage(Level = LogLevel.Warning, Message = "sign-in through the provider {Provider} failed")]
    private static partial void RemoteFailed(ILogger logger, string provider, Exception? exception);
}

public sealed class ProviderOptionsCache(ISnapshotSource snapshot, IServiceProvider services, IEnumerable<ProviderInvalidator> invalidators)
{
    private readonly Dictionary<string, ProviderConfig?> _applied = [];
    private readonly Lock _gate = new();

    public void Refresh(string name)
    {
        var current = snapshot.Current.SignIn.Providers.FirstOrDefault(provider => provider.Name == name);
        lock (_gate)
        {
            if (_applied.TryGetValue(name, out var previous) && previous == current)
                return;

            foreach (var invalidator in invalidators.Where(invalidator => invalidator.Name == name))
                invalidator.Invalidate(services);

            _applied[name] = current;
        }
    }
}
