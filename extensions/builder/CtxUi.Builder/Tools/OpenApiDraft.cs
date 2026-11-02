using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CtxUi.Builder.Servers;
using Enfilade;

namespace CtxUi.Builder.Tools;

public sealed record ToolParameter(string Name, string Type, ParameterPlace Place, bool Required, JsonNode? Default, string? Description);

public enum ParameterPlace { Path, Query, Header, Body }

public sealed record FormTool(string Name, string Method, string Path, string? Description, IReadOnlyList<ToolParameter> Parameters, JsonNode? ResponseSchema);

public static partial class OpenApiDraft
{
    public static readonly IReadOnlyList<string> Methods = ["get", "post", "put", "patch", "delete"];

    public static readonly IReadOnlyList<string> Types = ["string", "integer", "number", "boolean", "array", "object"];

    private const string JsonMedia = "application/json";
    private const string ComponentsPrefix = "#/components/";
    private const string EmptyDocument = """{"openapi":"3.0.3","info":{"title":"API","version":"1.0.0"},"paths":{}}""";

    private static readonly JsonSerializerOptions Output = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"\{([^{}]+)\}")]
    private static partial Regex Placeholder();

    public static string ToolFilePath(string toolName) => $"mcp/{ProjectFiles.ToolFunctionName(toolName)}.py";

    public static bool IsValidName(string name) => NamePattern().IsMatch(name);

    public static IReadOnlyList<FormTool> Read(string openApiJson)
    {
        var root = Parse(openApiJson);
        var tools = new List<FormTool>();
        foreach (var (name, path, method, operation, pathItem) in Operations(root))
            tools.Add(ToTool(root, name, path, method, operation, pathItem));
        return tools;
    }

    public static string Upsert(string openApiJson, FormTool tool)
    {
        Validate(tool);
        var root = string.IsNullOrWhiteSpace(openApiJson) ? JsonNode.Parse(EmptyDocument)!.AsObject() : Parse(openApiJson);
        var paths = root["paths"] as JsonObject ?? (JsonObject)(root["paths"] = new JsonObject());

        JsonObject? previous = null;
        foreach (var existing in Operations(root).Where(entry => entry.Name == tool.Name).ToList())
        {
            previous = existing.Operation;
            Detach(paths, existing.Path, existing.Method);
        }

        var method = tool.Method.ToLowerInvariant();
        var pathItem = paths[tool.Path] as JsonObject ?? (JsonObject)(paths[tool.Path] = new JsonObject());
        if (pathItem[method] is JsonObject occupant && !IsSameOperation(occupant, tool.Name))
            throw new PanelException(string.Format(BuilderText.ToolRouteTaken, tool.Method.ToUpperInvariant(), tool.Path));

        pathItem[method] = Compose(root, previous, tool);
        return Serialize(root);
    }

    public static string Remove(string openApiJson, string toolName)
    {
        var root = Parse(openApiJson);
        var paths = root["paths"] as JsonObject;
        var found = Operations(root).Where(entry => entry.Name == toolName).ToList();
        if (paths is null || found.Count == 0)
            throw new PanelException(BuilderText.ToolNotFound);
        foreach (var entry in found)
            Detach(paths, entry.Path, entry.Method);
        return Serialize(root);
    }

    public static (string OpenApiJson, string PythonFile) ConvertToCode(string openApiJson, string toolName)
    {
        var tool = Read(openApiJson).FirstOrDefault(candidate => candidate.Name == toolName) ?? throw new PanelException(BuilderText.ToolNotFound);
        return (Remove(openApiJson, toolName), PythonToolSource.Render(tool));
    }

    public static string Merge(string existingJson, string additionsJson)
    {
        var root = string.IsNullOrWhiteSpace(existingJson) ? JsonNode.Parse(EmptyDocument)!.AsObject() : Parse(existingJson);
        var additions = Parse(additionsJson);
        var taken = Operations(root).Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);
        var paths = root["paths"] as JsonObject ?? (JsonObject)(root["paths"] = new JsonObject());

        foreach (var entry in Operations(additions))
        {
            if (taken.Contains(entry.Name))
                throw new PanelException(string.Format(BuilderText.ImportNameTaken, entry.Name));
            var pathItem = paths[entry.Path] as JsonObject ?? (JsonObject)(paths[entry.Path] = new JsonObject());
            if (pathItem[entry.Method] is not null)
                throw new PanelException(string.Format(BuilderText.ToolRouteTaken, entry.Method.ToUpperInvariant(), entry.Path));
            pathItem[entry.Method] = entry.Operation.DeepClone();
            foreach (var (key, value) in entry.PathItem.Where(pair => !Methods.Contains(pair.Key) && pathItem[pair.Key] is null))
                pathItem[key] = value?.DeepClone();
        }

        if (additions["components"] is JsonObject incoming)
        {
            var components = root["components"] as JsonObject ?? (JsonObject)(root["components"] = new JsonObject());
            foreach (var (kind, group) in incoming)
            {
                if (group is not JsonObject members)
                    continue;
                var target = components[kind] as JsonObject ?? (JsonObject)(components[kind] = new JsonObject());
                foreach (var (name, value) in members.Where(member => target[member.Key] is null))
                    target[name] = value?.DeepClone();
            }
        }

        return Serialize(root);
    }

    public static string Serialize(JsonNode root) => root.ToJsonString(Output);

    internal static JsonObject Parse(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject ?? throw new PanelException(BuilderText.OpenApiInvalid);
        }
        catch (JsonException)
        {
            throw new PanelException(BuilderText.OpenApiInvalid);
        }
    }

    internal static IEnumerable<(string Name, string Path, string Method, JsonObject Operation, JsonObject PathItem)> Operations(JsonObject root)
    {
        if (root["paths"] is not JsonObject paths)
            yield break;
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, item) in paths)
        {
            if (item is not JsonObject pathItem)
                continue;
            foreach (var method in Methods)
            {
                if (pathItem[method] is not JsonObject operation)
                    continue;
                var declared = operation["operationId"]?.GetValueKind() == JsonValueKind.String ? operation["operationId"]!.GetValue<string>() : null;
                var name = OperationNames.Unique(declared, method, path, used);
                yield return (name, path, method, operation, pathItem);
            }
        }
    }

    private static void Validate(FormTool tool)
    {
        if (!NamePattern().IsMatch(tool.Name))
            throw new PanelException(BuilderText.ToolNameInvalid);
        if (!Methods.Contains(tool.Method.ToLowerInvariant()))
            throw new PanelException(BuilderText.ToolMethodInvalid);
        if (!tool.Path.StartsWith('/') || tool.Path.Any(char.IsControl) || tool.Path.Any(char.IsWhiteSpace))
            throw new PanelException(BuilderText.ToolPathInvalid);

        var placeholders = Placeholder().Matches(tool.Path).Select(match => match.Groups[1].Value).ToList();
        var declared = tool.Parameters.Where(parameter => parameter.Place == ParameterPlace.Path).Select(parameter => parameter.Name).ToList();
        foreach (var name in declared.Where(name => !placeholders.Contains(name)))
            throw new PanelException(string.Format(BuilderText.ToolPathParameterMissing, name));
        foreach (var name in placeholders.Where(name => !declared.Contains(name)))
            throw new PanelException(string.Format(BuilderText.ToolPathPlaceholderUndeclared, name));

        foreach (var parameter in tool.Parameters)
        {
            if (string.IsNullOrWhiteSpace(parameter.Name))
                throw new PanelException(BuilderText.ParameterNameRequired);
            if (!Types.Contains(parameter.Type))
                throw new PanelException(string.Format(BuilderText.ParameterTypeInvalid, parameter.Name));
        }

        var duplicate = tool.Parameters.GroupBy(parameter => (parameter.Name, parameter.Place)).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new PanelException(string.Format(BuilderText.ParameterDuplicate, duplicate.Key.Name));
    }

    private static bool IsSameOperation(JsonObject operation, string name) =>
        operation["operationId"]?.GetValueKind() == JsonValueKind.String && operation["operationId"]!.GetValue<string>() == name;

    private static void Detach(JsonObject paths, string path, string method)
    {
        if (paths[path] is not JsonObject item)
            return;
        item.Remove(method);
        if (!Methods.Any(candidate => item[candidate] is not null) && item.All(pair => pair.Key is "parameters" or "summary" or "description" or "servers"))
            paths.Remove(path);
    }

    private static FormTool ToTool(JsonObject root, string name, string path, string method, JsonObject operation, JsonObject pathItem)
    {
        var parameters = new List<ToolParameter>();
        foreach (var node in Merged(pathItem["parameters"], operation["parameters"]))
        {
            var resolved = Resolve(root, node);
            if (resolved is null || !TryPlace(resolved["in"]?.GetValue<string>(), out var place) || resolved["name"] is not JsonValue parameterName)
                continue;
            var schema = Resolve(root, resolved["schema"]);
            parameters.Add(new ToolParameter(
                parameterName.GetValue<string>(),
                TypeOf(schema),
                place,
                place == ParameterPlace.Path || resolved["required"]?.GetValue<bool>() == true,
                schema?["default"]?.DeepClone(),
                resolved["description"]?.GetValue<string>()));
        }

        if (BodySchema(root, operation) is { } body)
        {
            var required = (body["required"] as JsonArray)?.Select(node => node?.GetValue<string>()).ToHashSet() ?? [];
            foreach (var (property, value) in body["properties"] as JsonObject ?? [])
            {
                var schema = Resolve(root, value);
                parameters.Add(new ToolParameter(property, TypeOf(schema), ParameterPlace.Body, required.Contains(property), schema?["default"]?.DeepClone(), schema?["description"]?.GetValue<string>()));
            }
        }

        var description = Text(operation["description"]) ?? Text(operation["summary"]);
        return new FormTool(name, method.ToUpperInvariant(), path, description, parameters, ResponseOf(operation)?.DeepClone());
    }

    private static string? Text(JsonNode? node) => node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;

    private static IEnumerable<JsonNode> Merged(JsonNode? inherited, JsonNode? own)
    {
        var overridden = (own as JsonArray ?? []).OfType<JsonObject>().Select(node => (node["name"]?.ToString(), node["in"]?.ToString())).ToHashSet();
        foreach (var node in (inherited as JsonArray ?? []).OfType<JsonObject>().Where(node => !overridden.Contains((node["name"]?.ToString(), node["in"]?.ToString()))))
            yield return node;
        foreach (var node in (own as JsonArray ?? []).OfType<JsonObject>())
            yield return node;
    }

    private static bool TryPlace(string? value, out ParameterPlace place)
    {
        switch (value)
        {
            case "path":
                place = ParameterPlace.Path;
                return true;
            case "query":
                place = ParameterPlace.Query;
                return true;
            case "header":
                place = ParameterPlace.Header;
                return true;
            default:
                place = default;
                return false;
        }
    }

    private static string PlaceName(ParameterPlace place) => place switch
    {
        ParameterPlace.Path => "path",
        ParameterPlace.Query => "query",
        _ => "header",
    };

    private static string TypeOf(JsonObject? schema)
    {
        var type = schema?["type"];
        if (type is JsonArray array)
            type = array.FirstOrDefault(node => node?.GetValue<string>() != "null");
        var name = type?.GetValueKind() == JsonValueKind.String ? type.GetValue<string>() : null;
        if (name is not null && Types.Contains(name))
            return name;
        return schema?["properties"] is not null ? "object" : "string";
    }

    private static JsonObject? Resolve(JsonObject root, JsonNode? node)
    {
        for (var depth = 0; depth < 8 && node is JsonObject current; depth++)
        {
            if (current["$ref"] is not JsonValue reference)
                return current;
            var pointer = reference.GetValue<string>();
            if (!pointer.StartsWith(ComponentsPrefix, StringComparison.Ordinal))
                return null;
            var parts = pointer[ComponentsPrefix.Length..].Split('/');
            JsonNode? target = root["components"];
            foreach (var part in parts)
                target = (target as JsonObject)?[part.Replace("~1", "/").Replace("~0", "~")];
            node = target;
        }

        return null;
    }

    private static JsonObject? BodySchema(JsonObject root, JsonObject operation)
    {
        var body = Resolve(root, operation["requestBody"]);
        return Resolve(root, (body?["content"] as JsonObject)?[JsonMedia]?["schema"]);
    }

    private static JsonNode? ResponseOf(JsonObject operation)
    {
        var key = SuccessKey(operation);
        return key is null ? null : (((operation["responses"] as JsonObject)?[key] as JsonObject)?["content"] as JsonObject)?[JsonMedia]?["schema"];
    }

    private static string? SuccessKey(JsonObject operation)
    {
        if (operation["responses"] is not JsonObject responses)
            return null;
        return responses.ContainsKey("200") ? "200" : responses.Select(pair => pair.Key).FirstOrDefault(key => key.StartsWith('2'));
    }

    private static JsonObject Compose(JsonObject root, JsonObject? previous, FormTool tool)
    {
        var operation = previous is null ? [] : (JsonObject)previous.DeepClone();
        operation["operationId"] = tool.Name;
        if (string.IsNullOrWhiteSpace(tool.Description))
            operation.Remove("description");
        else
            operation["description"] = tool.Description;

        var existing = Merged(null, previous?["parameters"]).Select(node => (Node: node as JsonObject, Resolved: Resolve(root, node))).ToList();
        var parameters = new JsonArray();
        foreach (var parameter in tool.Parameters.Where(parameter => parameter.Place != ParameterPlace.Body))
        {
            var place = PlaceName(parameter.Place);
            var match = existing.FirstOrDefault(entry => entry.Resolved?["name"]?.ToString() == parameter.Name && entry.Resolved?["in"]?.ToString() == place).Resolved;
            var node = match is null ? [] : (JsonObject)match.DeepClone();
            node.Remove("$ref");
            node["name"] = parameter.Name;
            node["in"] = place;
            node["required"] = parameter.Required || parameter.Place == ParameterPlace.Path;
            ApplyDescription(node, parameter.Description);
            node["schema"] = SchemaOf(node["schema"] as JsonObject, parameter);
            parameters.Add(node);
        }

        foreach (var entry in existing.Where(entry => entry.Node is not null && (entry.Resolved is null || !TryPlace(entry.Resolved["in"]?.ToString(), out _))))
            parameters.Add(entry.Node!.DeepClone());
        if (parameters.Count == 0)
            operation.Remove("parameters");
        else
            operation["parameters"] = parameters;

        ComposeBody(root, operation, previous, tool);
        ComposeResponse(operation, tool.ResponseSchema);
        return operation;
    }

    private static void ApplyDescription(JsonObject node, string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            node.Remove("description");
        else
            node["description"] = description;
    }

    private static JsonObject SchemaOf(JsonObject? existing, ToolParameter parameter)
    {
        var schema = existing is null ? [] : (JsonObject)existing.DeepClone();
        schema.Remove("$ref");
        schema["type"] = parameter.Type;
        if (parameter.Default is null)
            schema.Remove("default");
        else
            schema["default"] = parameter.Default.DeepClone();
        return schema;
    }

    private static void ComposeBody(JsonObject root, JsonObject operation, JsonObject? previous, FormTool tool)
    {
        var bodyParameters = tool.Parameters.Where(parameter => parameter.Place == ParameterPlace.Body).ToList();
        var request = Resolve(root, previous?["requestBody"]);
        var hasJson = (request?["content"] as JsonObject)?.ContainsKey(JsonMedia) == true;

        if (bodyParameters.Count == 0)
        {
            if (hasJson)
                operation.Remove("requestBody");
            return;
        }

        var requestBody = request is null ? [] : (JsonObject)request.DeepClone();
        requestBody.Remove("$ref");
        var content = requestBody["content"] as JsonObject ?? (JsonObject)(requestBody["content"] = new JsonObject());
        var media = content[JsonMedia] as JsonObject ?? (JsonObject)(content[JsonMedia] = new JsonObject());
        var schema = Resolve(root, media["schema"]) is { } resolved ? (JsonObject)resolved.DeepClone() : [];
        schema.Remove("$ref");
        schema["type"] = "object";
        var current = schema["properties"] as JsonObject ?? [];
        var properties = new JsonObject();
        foreach (var parameter in bodyParameters)
        {
            var property = SchemaOf(current[parameter.Name] as JsonObject, parameter);
            ApplyDescription(property, parameter.Description);
            properties[parameter.Name] = property;
        }

        schema["properties"] = properties;
        var required = new JsonArray(bodyParameters.Where(parameter => parameter.Required).Select(parameter => (JsonNode)JsonValue.Create(parameter.Name)!).ToArray());
        if (required.Count == 0)
            schema.Remove("required");
        else
            schema["required"] = required;
        media["schema"] = schema;
        requestBody["required"] = required.Count > 0;
        operation["requestBody"] = requestBody;
    }

    private static void ComposeResponse(JsonObject operation, JsonNode? schema)
    {
        var responses = operation["responses"] as JsonObject ?? (JsonObject)(operation["responses"] = new JsonObject());
        var key = SuccessKey(operation);
        if (schema is null)
        {
            if (key is not null && responses[key] is JsonObject success && success["content"] is JsonObject media && media[JsonMedia] is JsonObject json)
            {
                json.Remove("schema");
                if (json.Count == 0)
                    media.Remove(JsonMedia);
                if (media.Count == 0)
                    success.Remove("content");
            }

            if (responses.Count == 0)
                responses["200"] = new JsonObject { ["description"] = "OK" };
            return;
        }

        key ??= "200";
        var response = responses[key] as JsonObject ?? (JsonObject)(responses[key] = new JsonObject { ["description"] = "OK" });
        response.Remove("$ref");
        var content = response["content"] as JsonObject ?? (JsonObject)(response["content"] = new JsonObject());
        var entry = content[JsonMedia] as JsonObject ?? (JsonObject)(content[JsonMedia] = new JsonObject());
        entry["schema"] = schema.DeepClone();
    }
}

internal static class PythonToolSource
{
    private static readonly JsonSerializerOptions Literal = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Render(FormTool tool)
    {
        var function = ProjectFiles.ToolFunctionName(tool.Name);
        var names = new Dictionary<ToolParameter, string>();
        var taken = new HashSet<string> { function, "json", "os", "quote", "httpx2", "tool", "path", "query", "headers", "body", "base_headers", "client", "response" };
        foreach (var parameter in tool.Parameters)
        {
            var identifier = ProjectFiles.ToolFunctionName(parameter.Name);
            while (!taken.Add(identifier))
                identifier += "_";
            names[parameter] = identifier;
        }

        var ordered = tool.Parameters.OrderBy(parameter => parameter.Required && ScalarDefault(parameter) is null ? 0 : 1).ToList();
        var signature = string.Join(", ", ordered.Select(parameter => Signature(parameter, names[parameter])));
        var builder = new StringBuilder();
        builder.Append("import json\nimport os\nfrom urllib.parse import quote\n\nimport httpx2\nfrom fastmcp.tools import tool\n\n\n");
        builder.Append(function == tool.Name ? "@tool\n" : $"@tool(name={Quote(tool.Name)})\n");
        builder.Append($"async def {function}({signature}):\n");
        if (!string.IsNullOrWhiteSpace(tool.Description))
            builder.Append($"    {Quote(tool.Description.Trim())}\n");
        builder.Append($"    path = {Quote(tool.Path)}\n");
        foreach (var parameter in tool.Parameters.Where(parameter => parameter.Place == ParameterPlace.Path))
            builder.Append($"    path = path.replace({Quote("{" + parameter.Name + "}")}, quote(str({names[parameter]}), safe=\"\"))\n");
        AppendMapping(builder, "query", tool.Parameters, ParameterPlace.Query, names);
        AppendMapping(builder, "headers", tool.Parameters, ParameterPlace.Header, names);
        var hasBody = tool.Parameters.Any(parameter => parameter.Place == ParameterPlace.Body);
        if (hasBody)
            AppendMapping(builder, "body", tool.Parameters, ParameterPlace.Body, names);
        builder.Append("    base_headers = json.loads(os.environ.get(\"BUILDER_API_HEADERS\") or \"{}\")\n");
        builder.Append("    async with httpx2.AsyncClient(base_url=os.environ[\"BUILDER_BASE_URL\"], headers=base_headers, timeout=30.0) as client:\n");
        builder.Append($"        response = await client.request({Quote(tool.Method.ToUpperInvariant())}, path, params=query, headers=headers{(hasBody ? ", json=body" : string.Empty)})\n");
        builder.Append("        response.raise_for_status()\n");
        builder.Append("        return response.json() if response.content else None\n");
        return builder.ToString();
    }

    private static string Signature(ToolParameter parameter, string identifier)
    {
        var type = parameter.Type switch
        {
            "integer" => "int",
            "number" => "float",
            "boolean" => "bool",
            "array" => "list",
            "object" => "dict",
            _ => "str",
        };
        if (ScalarDefault(parameter) is { } value)
            return $"{identifier}: {type} = {PythonValue(value)}";
        return parameter.Required ? $"{identifier}: {type}" : $"{identifier}: {type} | None = None";
    }

    private static System.Text.Json.Nodes.JsonNode? ScalarDefault(ToolParameter parameter) =>
        parameter.Default?.GetValueKind() is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? parameter.Default : null;

    private static string PythonValue(System.Text.Json.Nodes.JsonNode node) => node.GetValueKind() switch
    {
        JsonValueKind.True => "True",
        JsonValueKind.False => "False",
        _ => node.ToJsonString(Literal),
    };

    private static void AppendMapping(StringBuilder builder, string variable, IReadOnlyList<ToolParameter> parameters, ParameterPlace place, Dictionary<ToolParameter, string> names)
    {
        var entries = string.Join(", ", parameters.Where(parameter => parameter.Place == place).Select(parameter => $"{Quote(parameter.Name)}: {names[parameter]}"));
        builder.Append($"    {variable} = {{key: value for key, value in {{{entries}}}.items() if value is not None}}\n");
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value, Literal);
}
