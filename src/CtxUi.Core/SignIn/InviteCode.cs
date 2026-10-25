using System.Diagnostics.CodeAnalysis;

namespace CtxUi.Core.SignIn;

public static class InviteCode
{
    public const string Prefix = "inv_";

    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    private static readonly PrefixedSecret Secret = new(Prefix, 32);

    public static string Generate() => Secret.Generate();

    public static bool IsWellFormed([NotNullWhen(true)] string? value) => Secret.IsWellFormed(value);

    public static string Hash(string code) => PrefixedSecret.Hash(code);
}
