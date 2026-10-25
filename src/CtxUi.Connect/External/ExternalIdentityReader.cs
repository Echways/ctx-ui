using System.Security.Claims;
using CtxUi.Core.SignIn;

namespace CtxUi.Connect.External;

public static class ExternalIdentityReader
{
    private static readonly string[] EmailClaims = [ClaimTypes.Email, "email"];
    private static readonly string[] NameClaims = [ClaimTypes.Name, "name", "preferred_username", "login"];

    public static ExternalIdentity? Read(string provider, ClaimsPrincipal principal)
    {
        var subject = First(principal, ClaimTypes.NameIdentifier, "sub");
        return subject is null ? null : new ExternalIdentity(provider, subject, First(principal, EmailClaims) ?? First(principal, NameClaims) ?? subject);
    }

    private static string? First(ClaimsPrincipal principal, params string[] types) =>
        types.Select(principal.FindFirstValue).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
