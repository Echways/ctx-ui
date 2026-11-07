using System.Text.Json;
using System.Text.Json.Nodes;
using NJsonSchema;

namespace CtxUi.Builder.Tools;

public static class ResponseSchema
{
    private const string DefinitionsPrefix = "#/definitions/";
    private const int MaxDepth = 64;

    public static JsonNode FromExample(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new BuilderException(BuilderText.ResponseExampleEmpty);
        try
        {
            using var document = JsonDocument.Parse(json);
            var schema = JsonSchema.FromSampleJson(json);
            var node = JsonNode.Parse(schema.ToJson())!.AsObject();
            var definitions = node["definitions"] as JsonObject ?? [];
            node.Remove("definitions");
            node.Remove("$schema");
            node.Remove("title");
            return Inlined(node, definitions, 0);
        }
        catch (Exception failure) when (failure is JsonException or InvalidOperationException or NotSupportedException)
        {
            throw new BuilderException(BuilderText.ResponseExampleInvalid);
        }
    }

    private static JsonNode Inlined(JsonNode node, JsonObject definitions, int depth)
    {
        if (depth > MaxDepth)
            throw new InvalidOperationException();
        switch (node)
        {
            case JsonObject members when members["$ref"] is JsonValue reference &&
                                         reference.GetValue<string>()
                                             .StartsWith(DefinitionsPrefix, StringComparison.Ordinal):
                var target = definitions[reference.GetValue<string>()[DefinitionsPrefix.Length..]] ??
                             throw new InvalidOperationException();
                return Inlined(target.DeepClone(), definitions, depth + 1);
            case JsonObject members:
                foreach (var (key, value) in members.ToList())
                    members[key] = value is null ? null : Inlined(value, definitions, depth + 1);
                return members;
            case JsonArray items:
                for (var index = 0; index < items.Count; index++)
                    items[index] = items[index] is { } item ? Inlined(item, definitions, depth + 1) : null;
                return items;
            default:
                return node;
        }
    }
}
