using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace CtxUi.Core;

public sealed class PrefixedSecret(string prefix, int length)
{
    private const int DisplayedLength = 6;
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    private static readonly SearchValues<char> Symbols = SearchValues.Create(Alphabet);

    public string Generate() => prefix + RandomNumberGenerator.GetString(Alphabet, length);

    public bool IsWellFormed([NotNullWhen(true)] string? value) =>
        value is not null &&
        value.Length == prefix.Length + length &&
        value.StartsWith(prefix, StringComparison.Ordinal) &&
        !value.AsSpan(prefix.Length).ContainsAnyExcept(Symbols);

    public static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    public string DisplayPrefix(string secret) => secret[..(prefix.Length + DisplayedLength)];
}
