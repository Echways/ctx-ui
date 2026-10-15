using System.Diagnostics.CodeAnalysis;

namespace CtxUi.Core.ApiKeys;

public static class ApiKeySecret
{
    public const string Prefix = "ctxk_";

    private static readonly PrefixedSecret Secret = new(Prefix, 40);

    public static string Generate() => Secret.Generate();

    public static bool IsWellFormed([NotNullWhen(true)] string? value) => Secret.IsWellFormed(value);

    public static string Hash(string secret) => PrefixedSecret.Hash(secret);

    public static string DisplayPrefix(string secret) => Secret.DisplayPrefix(secret);
}
