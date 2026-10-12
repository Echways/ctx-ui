using System.Text.RegularExpressions;

namespace CtxUi.Core.Inbounds;

public static partial class Slug
{
    [GeneratedRegex(@"\A[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?\z")]
    private static partial Regex Pattern();

    public static string Parse(string? value)
    {
        var slug = value?.Trim() ?? "";
        if (!Pattern().IsMatch(slug))
            throw new DomainException(
                $"slug «{value}» недопустим: от 1 до 64 символов, строчные латинские буквы, цифры и дефис, дефис не по краям");

        return slug;
    }
}
