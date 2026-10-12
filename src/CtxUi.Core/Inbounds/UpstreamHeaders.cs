using System.Text.Json;
using System.Text.RegularExpressions;
using CtxUi.Core.Secrets;

namespace CtxUi.Core.Inbounds;

public static partial class UpstreamHeaders
{
    public const string ClientPrefix = "X-Mcp-Client-";
    public const string MetaPrefix = "X-Mcp-Meta-";

    private static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Content-Length", "Content-Type", "Transfer-Encoding", "Upgrade",
    };

    [GeneratedRegex(@"\A[A-Za-z0-9!#$%&'*+.^_`|~-]+\z")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"\A[\x20-\x7E]+\z")]
    private static partial Regex ValuePattern();

    public static Dictionary<string, string> Parse(IEnumerable<KeyValuePair<string, string>>? pairs)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in pairs ?? [])
        {
            if (!NamePattern().IsMatch(name))
                throw new DomainException($"имя заголовка «{name}» недопустимо");
            if (Forbidden.Contains(name) || IsIdentity(name))
                throw new DomainException($"заголовок «{name}» шлюз выставляет сам, задать его нельзя");
            if (!ValuePattern().IsMatch(value))
                throw new DomainException($"значение заголовка «{name}» должно быть непустой строкой из печатных символов ASCII");
            if (!headers.TryAdd(name, value))
                throw new DomainException($"заголовок «{name}» задан дважды");
        }

        return headers;
    }

    public static bool IsIdentity(string name) =>
        name.StartsWith(ClientPrefix, StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith(MetaPrefix, StringComparison.OrdinalIgnoreCase);

    public static string? Seal(ISecretVault vault, IReadOnlyDictionary<string, string> headers) =>
        headers.Count == 0 ? null : vault.Seal(JsonSerializer.Serialize(headers));

    public static IReadOnlyDictionary<string, string>? Open(ISecretVault vault, string? sealedHeaders)
    {
        if (sealedHeaders is null)
            return new Dictionary<string, string>();

        var json = vault.Open(sealedHeaders);
        return json is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(json);
    }
}
