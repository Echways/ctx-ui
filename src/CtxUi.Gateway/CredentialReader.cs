using Microsoft.AspNetCore.Http;
using CtxUi.Core.Clients;
using CtxUi.Core.Routing;
using CtxUi.Core.SignIn;

namespace CtxUi.Gateway;

public sealed class CredentialReader(IAccessTokenValidator? validator)
{
    public async Task<Credential> ReadAsync(HttpContext context, string resource, bool oauthActive, CancellationToken ct)
    {
        if (TokenPath.Get(context) is { } fromPath)
            return Credential.FromKey(fromPath);

        var token = ClientCredentials.Read(context);
        if (string.IsNullOrEmpty(token))
            return new Credential(CredentialKind.Missing);
        if (AccessToken.IsWellFormed(token) || !oauthActive || validator is null)
            return Credential.FromKey(token);

        try
        {
            var grant = await validator.ValidateAsync(token, ct);
            return grant is not null && grant.Audiences.Contains(resource, StringComparer.Ordinal)
                ? new Credential(CredentialKind.Grant, Grant: grant)
                : new Credential(CredentialKind.Invalid);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new Credential(CredentialKind.Invalid);
        }
    }
}
