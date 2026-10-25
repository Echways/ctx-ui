namespace CtxUi.Core.SignIn;

public sealed record ProviderInfo(string Name, string Title, string ConsoleUrl, bool NeedsIssuer);

public static class ProviderCatalog
{
    public const string Oidc = "oidc";

    public static IReadOnlyList<ProviderInfo> All =>
    [
        new("google", "Google", "https://console.cloud.google.com/apis/credentials", false),
        new("microsoft", "Microsoft", "https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade", false),
        new("github", "GitHub", "https://github.com/settings/developers", false),
        new("gitlab", "GitLab", "https://gitlab.com/-/user_settings/applications", false),
        new("yandex", CoreText.ProviderYandex, "https://oauth.yandex.ru/client/new", false),
        new("vkid", "VK ID", "https://id.vk.com/about/business/go", false),
        new("discord", "Discord", "https://discord.com/developers/applications", false),
        new("telegram", "Telegram", "https://t.me/BotFather", false),
        new(Oidc, "OpenID Connect", "", true),
    ];

    public static ProviderInfo? Find(string? name)
    {
        var key = name?.Trim().ToLowerInvariant();
        return All.FirstOrDefault(provider => provider.Name == key);
    }

    public static ProviderInfo Get(string name) =>
        Find(name) ?? throw new DomainException(
            string.Format(CoreText.ProviderUnknown, name.Trim(), string.Join(", ", All.Select(provider => provider.Name))));

    public static int Order(string name) => All.ToList().FindIndex(provider => provider.Name == name);
}
