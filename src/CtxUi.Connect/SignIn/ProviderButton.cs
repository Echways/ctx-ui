using CtxUi.Core.SignIn;

namespace CtxUi.Connect.SignIn;

public sealed record ProviderButton(string Name, string Title)
{
    public static IReadOnlyList<ProviderButton> All(SignInConfig config) => config.Providers
        .Select(provider => new ProviderButton(provider.Name, provider.Title ?? ProviderCatalog.Get(provider.Name).Title))
        .ToList();
}
