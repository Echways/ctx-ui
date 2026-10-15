using System.Text;

namespace CtxUi.Core.Clients;

public static class ClientName
{
    public const int MaxLength = 100;

    public static string Parse(string? value)
    {
        var name = value?.Trim() ?? "";
        if (name.Length == 0)
            throw new DomainException(CoreText.ClientNameEmpty);
        if (name.Length > MaxLength)
            throw new DomainException(string.Format(CoreText.ClientNameTooLong, MaxLength));
        if (name.Any(char.IsControl))
            throw new DomainException(CoreText.ClientNameControl);

        return name;
    }

    public static string Key(string name) => name.Normalize(NormalizationForm.FormKC).ToUpperInvariant();
}
