using System.Text.RegularExpressions;

namespace CtxUi.Core.Admins;

public static partial class AdminName
{
    public const int MaxLength = 64;

    [GeneratedRegex(@"\A[A-Za-z0-9._-]{1,64}\z")]
    private static partial Regex Pattern();

    public static string Parse(string? value)
    {
        var name = value?.Trim() ?? "";
        if (!Pattern().IsMatch(name))
            throw new DomainException(string.Format(CoreText.AdminNameInvalid, name, MaxLength));

        return name;
    }

    public static string Key(string name) => name.ToUpperInvariant();
}
