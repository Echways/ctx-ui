using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace CtxUi.Core.ApiKeys;

public static class ApiKeySecret
{
    public const string Prefix = "ctxk_";

    private const int SecretLength = 40;
    private const int DisplayedSecretLength = 6;
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    public static string Generate() => Prefix + RandomNumberGenerator.GetString(Alphabet, SecretLength);

    public static bool IsWellFormed([NotNullWhen(true)] string? value)
    {
        if (value is null || value.Length != Prefix.Length + SecretLength || !value.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        foreach (var symbol in value.AsSpan(Prefix.Length))
        {
            if (!char.IsAsciiLetterOrDigit(symbol))
                return false;
        }

        return true;
    }

    public static string Hash(string secret) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    public static string DisplayPrefix(string secret) => secret[..(Prefix.Length + DisplayedSecretLength)];
}
