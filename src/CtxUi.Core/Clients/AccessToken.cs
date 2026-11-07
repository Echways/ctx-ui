using System.Diagnostics.CodeAnalysis;

namespace CtxUi.Core.Clients;

public static class AccessToken
{
    public const string Prefix = "tnk_";

    private static readonly PrefixedSecret Secret = new(Prefix, 32);

    public static string Generate()
    {
        return Secret.Generate();
    }

    public static bool IsWellFormed([NotNullWhen(true)] string? value)
    {
        return Secret.IsWellFormed(value);
    }

    public static string Hash(string token)
    {
        return PrefixedSecret.Hash(token);
    }

    public static string DisplayPrefix(string token)
    {
        return Secret.DisplayPrefix(token);
    }
}
