using Microsoft.AspNetCore.Http;

namespace CtxUi.Gateway;

public static class ClientCredentials
{
    private const string Scheme = "Bearer ";

    public static string? Read(HttpContext context)
    {
        if (TokenPath.Get(context) is { } fromPath)
            return fromPath;

        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase) ? header[Scheme.Length..].Trim() : null;
    }
}
