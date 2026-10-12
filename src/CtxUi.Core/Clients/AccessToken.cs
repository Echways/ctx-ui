using System.Security.Cryptography;
using System.Text;

namespace CtxUi.Core.Clients;

public static class AccessToken
{
    public const string Prefix = "tnk_";

    private const int SecretLength = 32;
    private const int DisplayedSecretLength = 6;
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    public static string Generate() => Prefix + RandomNumberGenerator.GetString(Alphabet, SecretLength);

    public static bool IsWellFormed(string? value)
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

    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static string DisplayPrefix(string token) => token[..(Prefix.Length + DisplayedSecretLength)];
}
