using CtxUi.Core.SignIn;

namespace CtxUi.Core.Settings;

public sealed class PublicEndpointOptions
{
    public const string DefaultUrl = "http://localhost:8080";

    public string PublicUrl { get; set; } = DefaultUrl;
    public string? OAuthIssuer { get; set; }

    public string Issuer => OAuthResource.Issuer(string.IsNullOrWhiteSpace(OAuthIssuer) ? PublicUrl : OAuthIssuer.Trim());
}
