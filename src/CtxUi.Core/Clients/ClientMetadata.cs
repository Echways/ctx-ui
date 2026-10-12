using System.Text.RegularExpressions;

namespace CtxUi.Core.Clients;

/// <summary>Пары ключ-значение клиента. Шлюз передаёт их upstream заголовками X-Mcp-Meta-{ключ}.</summary>
public static partial class ClientMetadata
{
    public const int MaxEntries = 32;
    public const int MaxValueLength = 256;

    [GeneratedRegex(@"\A[A-Za-z0-9](?:[A-Za-z0-9-]{0,62}[A-Za-z0-9])?\z")]
    private static partial Regex KeyPattern();

    public static Dictionary<string, string> Parse(IEnumerable<KeyValuePair<string, string>>? pairs)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in pairs ?? [])
        {
            if (!KeyPattern().IsMatch(key))
                throw new DomainException(
                    $"ключ метаданных «{key}» недопустим: до 64 латинских букв, цифр и дефисов, дефис не по краям");
            if (value.Length > MaxValueLength)
                throw new DomainException($"значение метаданных «{key}» длиннее {MaxValueLength} символов");
            if (value.Any(char.IsControl))
                throw new DomainException($"значение метаданных «{key}» содержит управляющие символы");
            if (!metadata.TryAdd(key, value))
                throw new DomainException($"ключ метаданных «{key}» задан дважды");
        }

        if (metadata.Count > MaxEntries)
            throw new DomainException($"метаданных больше {MaxEntries} пар");

        return metadata;
    }
}
