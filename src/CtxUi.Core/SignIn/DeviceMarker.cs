using System.Diagnostics.CodeAnalysis;

namespace CtxUi.Core.SignIn;

public static class DeviceMarker
{
    public const string Prefix = "dvc_";

    private static readonly PrefixedSecret Secret = new(Prefix, 43);

    public static string Generate()
    {
        return Secret.Generate();
    }

    public static bool IsWellFormed([NotNullWhen(true)] string? value)
    {
        return Secret.IsWellFormed(value);
    }

    public static string Hash(string marker)
    {
        return PrefixedSecret.Hash(marker);
    }
}
