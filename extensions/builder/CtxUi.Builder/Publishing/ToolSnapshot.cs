using System.Text.Json;

namespace CtxUi.Builder.Publishing;

public static class ToolSnapshot
{
    public static string Serialize(IReadOnlyList<ToolInfo> tools) => JsonSerializer.Serialize(tools, JsonSerializerOptions.Web);

    public static IReadOnlyList<string> Names(string? snapshot)
    {
        if (string.IsNullOrEmpty(snapshot))
            return [];
        using var document = JsonDocument.Parse(snapshot);
        return [.. document.RootElement.EnumerateArray().Select(tool => tool.GetProperty("name").GetString()!)];
    }
}
