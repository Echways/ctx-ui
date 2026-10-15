using System.Text.RegularExpressions;

namespace CtxUi.Core.Clients;

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
                throw new DomainException(string.Format(CoreText.MetadataKeyInvalid, key));
            if (value.Length > MaxValueLength)
                throw new DomainException(string.Format(CoreText.MetadataValueTooLong, key, MaxValueLength));
            if (value.Any(char.IsControl))
                throw new DomainException(string.Format(CoreText.MetadataValueControl, key));
            if (!metadata.TryAdd(key, value))
                throw new DomainException(string.Format(CoreText.MetadataKeyDuplicate, key));
        }

        if (metadata.Count > MaxEntries)
            throw new DomainException(string.Format(CoreText.MetadataTooMany, MaxEntries));

        return metadata;
    }
}
