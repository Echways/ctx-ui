using System.Text.RegularExpressions;

namespace CtxUi.Core.ApiKeys;

public static partial class ApiKeyName
{
    public const int MaxLength = 64;

    [GeneratedRegex(@"\A[A-Za-z0-9._-]{1,64}\z")]
    private static partial Regex Pattern();

    public static string Parse(string? value)
    {
        var name = value?.Trim() ?? "";
        if (!Pattern().IsMatch(name))
            throw new DomainException(
                $"название ключа «{name}» недопустимо: от 1 до {MaxLength} символов, латинские буквы, цифры, точка, дефис и подчёркивание");

        return name;
    }

    public static string Key(string name) => name.ToUpperInvariant();
}
