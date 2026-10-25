namespace CtxUi.Core.SignIn;

public sealed record ProviderConfig(string Name, string ClientId, string Secret, string? Issuer, string? Title);

public sealed record SignInConfig(bool Enabled, bool KeySignIn, bool SelfRevoke, int IdleDays, IReadOnlyList<ProviderConfig> Providers)
{
    public static SignInConfig Off { get; } = new(false, true, true, SignInSettings.DefaultIdleDays, []);

    public bool IsActive(string publicUrl) => Enabled && OAuthAvailability.IsAllowed(publicUrl);
}
