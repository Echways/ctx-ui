namespace CtxUi.Core.SignIn;

public sealed class SignInProvider
{
    public required string Name { get; set; }
    public bool Enabled { get; set; } = true;
    public required string ClientId { get; set; }
    public required string SecretSealed { get; set; }
    public string? Issuer { get; set; }
    public string? Title { get; set; }
}
