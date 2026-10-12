using System.Text;

namespace CtxUi.Core.Clients;

public static class ClientName
{
    public const int MaxLength = 100;

    public static string Parse(string? value)
    {
        var name = value?.Trim() ?? "";
        if (name.Length == 0)
            throw new DomainException("имя клиента не может быть пустым");
        if (name.Length > MaxLength)
            throw new DomainException($"имя клиента длиннее {MaxLength} символов");
        if (name.Any(char.IsControl))
            throw new DomainException("имя клиента содержит управляющие символы");

        return name;
    }

    public static string Key(string name) => name.Normalize(NormalizationForm.FormKC).ToUpperInvariant();
}
