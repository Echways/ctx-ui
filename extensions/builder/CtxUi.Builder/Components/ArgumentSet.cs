using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Enfilade;

namespace CtxUi.Builder.Components;

public sealed class ArgumentField
{
    public const string JsonType = "json";

    public required string Name { get; init; }

    public required string Type { get; init; }

    public bool Required { get; init; }

    public string? Description { get; init; }

    public string? Text { get; set; }

    public bool IsText => Type is "string" or "integer" or "number";

    public bool IsBoolean => Type == "boolean";

    public bool IsJson => Type == JsonType;
}

public sealed class ArgumentSet
{
    private static readonly string[] SimpleTypes = ["string", "integer", "number", "boolean"];

    public IReadOnlyList<ArgumentField> Fields { get; private init; } = [];

    public static ArgumentSet From(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            return new ArgumentSet();

        var required = schema.TryGetProperty("required", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()).ToHashSet()
            : [];
        return new ArgumentSet
        {
            Fields =
            [
                .. properties.EnumerateObject().Select(property => new ArgumentField
                {
                    Name = property.Name,
                    Type = SimpleTypeOf(property.Value) ?? ArgumentField.JsonType,
                    Required = required.Contains(property.Name),
                    Description = property.Value.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String ? description.GetString() : null,
                    Text = DefaultText(property.Value),
                }),
            ],
        };
    }

    public JsonObject ToArguments()
    {
        var arguments = new JsonObject();
        foreach (var field in Fields)
        {
            var text = field.Text ?? string.Empty;
            if (field.Type == "string" ? text.Length == 0 : text.Trim().Length == 0)
            {
                if (!field.Required)
                    continue;
                if (field.Type != "string")
                    throw new PanelException(string.Format(BuilderText.ToolArgumentRequired, field.Name));
                arguments[field.Name] = string.Empty;
                continue;
            }

            arguments[field.Name] = ValueOf(field, text.Trim()) ?? throw new PanelException(string.Format(BuilderText.ToolArgumentInvalid, field.Name));
        }

        return arguments;
    }

    private static JsonNode? ValueOf(ArgumentField field, string text) => field.Type switch
    {
        "string" => JsonValue.Create(field.Text),
        "integer" => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole) ? JsonValue.Create(whole) : null,
        "number" => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var fraction) && double.IsFinite(fraction) ? JsonValue.Create(fraction) : null,
        "boolean" => bool.TryParse(text, out var flag) ? JsonValue.Create(flag) : null,
        _ => ParseJson(text),
    };

    private static JsonNode? ParseJson(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? DefaultText(JsonElement property)
    {
        if (!property.TryGetProperty("default", out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
    }

    private static string? SimpleTypeOf(JsonElement property)
    {
        if (property.TryGetProperty("type", out var type))
        {
            if (type.ValueKind == JsonValueKind.String)
                return Simple(type.GetString());
            if (type.ValueKind == JsonValueKind.Array)
                return Single(type.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()));
        }

        if (property.TryGetProperty("anyOf", out var options) && options.ValueKind == JsonValueKind.Array)
            return Single(options.EnumerateArray().Select(option => option.ValueKind == JsonValueKind.Object && option.TryGetProperty("type", out var optionType) && optionType.ValueKind == JsonValueKind.String ? optionType.GetString() : "complex"));
        return null;
    }

    private static string? Single(IEnumerable<string?> types)
    {
        var present = types.Where(type => type != "null").Distinct().ToList();
        return present.Count == 1 ? Simple(present[0]) : null;
    }

    private static string? Simple(string? type) => type is not null && SimpleTypes.Contains(type) ? type : null;
}
