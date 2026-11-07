using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CtxUi.Builder.Tools;

namespace CtxUi.Builder.Components;

public sealed class ParameterRow
{
    public string Name { get; set; } = string.Empty;

    public string Type { get; set; } = "string";

    public ParameterPlace Place { get; set; } = ParameterPlace.Query;

    public bool Required { get; set; }

    public string? DefaultText { get; set; }

    public string? Description { get; set; }

    public static ParameterRow From(ToolParameter parameter)
    {
        return new ParameterRow
        {
            Name = parameter.Name,
            Type = parameter.Type,
            Place = parameter.Place,
            Required = parameter.Required,
            DefaultText = DefaultTextOf(parameter.Default),
            Description = parameter.Description
        };
    }

    public ToolParameter ToParameter()
    {
        return new ToolParameter(Name.Trim(), Type, Place, Required || Place == ParameterPlace.Path, DefaultOf(),
            string.IsNullOrWhiteSpace(Description) ? null : Description.Trim());
    }

    private static string? DefaultTextOf(JsonNode? value)
    {
        return value?.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value?.ToJsonString();
    }

    private JsonNode? DefaultOf()
    {
        if (string.IsNullOrWhiteSpace(DefaultText) || Type == OpenApiDraft.ComplexType)
            return null;
        var text = DefaultText.Trim();
        var parsed = Type switch
        {
            "string" => JsonValue.Create(DefaultText),
            "integer" when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole) =>
                JsonValue.Create(whole),
            "number" when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var fraction) =>
                JsonValue.Create(fraction),
            "boolean" when bool.TryParse(text, out var flag) => JsonValue.Create(flag),
            "array" or "object" => ParseStructure(text),
            _ => null
        };
        return parsed ?? throw new BuilderException(string.Format(BuilderText.ParameterDefaultInvalid, Name));
    }

    private JsonNode? ParseStructure(string text)
    {
        try
        {
            var node = JsonNode.Parse(text);
            return node?.GetValueKind() == (Type == "array" ? JsonValueKind.Array : JsonValueKind.Object) ? node : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed class ToolFormDraft
{
    [Required] public string Name { get; set; } = string.Empty;

    [Required] public string Method { get; set; } = "GET";

    [Required] public string Path { get; set; } = "/";

    public string? Description { get; set; }

    public List<ParameterRow> Parameters { get; set; } = [];

    public string? ResponseExample { get; set; }

    public JsonNode? ResponseSchema { get; set; }

    public bool LacksDescription => string.IsNullOrWhiteSpace(Description);

    public static ToolFormDraft From(FormTool tool)
    {
        return new ToolFormDraft
        {
            Name = tool.Name,
            Method = tool.Method.ToUpperInvariant(),
            Path = tool.Path,
            Description = tool.Description,
            Parameters = [.. tool.Parameters.Select(ParameterRow.From)],
            ResponseSchema = tool.ResponseSchema?.DeepClone()
        };
    }

    public FormTool ToTool()
    {
        return new FormTool(
            Name.Trim(),
            Method,
            Path.Trim(),
            string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
            [.. Parameters.Select(row => row.ToParameter())],
            ResponseSchema?.DeepClone());
    }
}
