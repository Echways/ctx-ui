using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CtxUi.Builder.Servers;
using Enfilade;

namespace CtxUi.Builder.Tools;

public sealed record ToolParameter(
    string Name,
    string Type,
    ParameterPlace Place,
    bool Required,
    JsonNode? Default,
    string? Description);

public enum ParameterPlace
{
    Path,
    Query,
    Header,
    Body
}

public sealed record FormTool(
    string Name,
    string Method,
    string Path,
    string? Description,
    IReadOnlyList<ToolParameter> Parameters,
    JsonNode? ResponseSchema);

public static partial class OpenApiDraft
{
    public const string ComplexType = "complex";
    public const int MaxNameLength = 56;

    public static readonly IReadOnlyList<string> Methods = ["get", "post", "put", "patch", "delete"];

    public static readonly IReadOnlyList<string> Types = ["string", "integer", "number", "boolean", "array", "object"];

    private const string JsonMedia = "application/json";
    private const string ComponentsPrefix = "#/components/";
    private const string EmptyDocument = """{"openapi":"3.0.3","info":{"title":"API","version":"1.0.0"},"paths":{}}""";

    private static readonly string[] Combinators = ["$ref", "anyOf", "oneOf", "allOf", "not"];

    private static readonly JsonSerializerOptions Output = new()
    { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [GeneratedRegex("^(?=.{1,56}$)[A-Za-z0-9]+(?:_[A-Za-z0-9]+)*$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"\{([^{}]+)\}")]
    private static partial Regex Placeholder();

    public static string ToolFilePath(string toolName)
    {
        return $"mcp/{ProjectFiles.ToolFunctionName(toolName)}.py";
    }

    public static IReadOnlyList<FormTool> Read(string openApiJson)
    {
        if (string.IsNullOrWhiteSpace(openApiJson))
            return [];
        var root = Parse(openApiJson);
        var tools = new List<FormTool>();
        foreach (var (name, path, method, operation, pathItem) in Operations(root))
            tools.Add(ToTool(root, name, path, method, operation, pathItem));
        return tools;
    }

    public static string Upsert(string openApiJson, FormTool tool)
    {
        Validate(tool);
        var root = string.IsNullOrWhiteSpace(openApiJson)
            ? JsonNode.Parse(EmptyDocument)!.AsObject()
            : Parse(openApiJson);
        var paths = root["paths"] as JsonObject ?? (JsonObject)(root["paths"] = new JsonObject());

        JsonObject? previous = null;
        JsonObject? previousPathItem = null;
        var previousPath = string.Empty;
        foreach (var existing in Operations(root).Where(entry => entry.Name == tool.Name).ToList())
        {
            previous = existing.Operation;
            previousPathItem = (JsonObject)existing.PathItem.DeepClone();
            previousPath = existing.Path;
            Detach(paths, existing.Path, existing.Method);
        }

        var method = tool.Method.ToLowerInvariant();
        var pathItem = paths[tool.Path] as JsonObject ?? (JsonObject)(paths[tool.Path] = new JsonObject());
        if (pathItem[method] is JsonObject occupant && !IsSameOperation(occupant, tool.Name))
            throw new PanelException(string.Format(BuilderText.ToolRouteTaken, tool.Method.ToUpperInvariant(),
                tool.Path));

        pathItem[method] = Compose(root, previous, previousPathItem, previousPath == tool.Path, tool);
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
        var tool = Read(openApiJson).FirstOrDefault(candidate => candidate.Name == toolName) ??
                   throw new PanelException(BuilderText.ToolNotFound);
        return (Remove(openApiJson, toolName), PythonToolSource.Render(tool));
    }

    public static IReadOnlySet<string> Names(string openApiJson)
    {
        return string.IsNullOrWhiteSpace(openApiJson)
            ? new HashSet<string>()
            : Operations(Parse(openApiJson)).Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);
    }

    public static string Merge(string existingJson, string additionsJson, IReadOnlySet<string> reserved)
    {
        var root = string.IsNullOrWhiteSpace(existingJson)
            ? JsonNode.Parse(EmptyDocument)!.AsObject()
            : Parse(existingJson);
        var additions = Parse(additionsJson);
        var present = Operations(root).Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);
        var incoming = Operations(additions).ToList();

        var clashes = incoming.Select(entry => entry.Name)
            .Where(name => present.Contains(name) || reserved.Contains(name)).ToList();
        if (clashes.Count > 0)
            throw new PanelException(string.Format(BuilderText.ImportNameTaken, string.Join(", ", clashes)));

        if (present.Count == 0)
        {
            if (Str(additions["openapi"]) is { } version)
                root["openapi"] = version;
        }
        else if (Family(Str(root["openapi"])) != Family(Str(additions["openapi"])))
        {
            throw new PanelException(string.Format(BuilderText.OpenApiVersionMismatch, Str(root["openapi"]),
                Str(additions["openapi"])));
        }

        var paths = root["paths"] as JsonObject ?? (JsonObject)(root["paths"] = new JsonObject());
        foreach (var entry in incoming)
        {
            var pathItem = paths[entry.Path] as JsonObject ?? (JsonObject)(paths[entry.Path] = new JsonObject());
            if (pathItem[entry.Method] is not null)
                throw new PanelException(string.Format(BuilderText.ToolRouteTaken, entry.Method.ToUpperInvariant(),
                    entry.Path));
            pathItem[entry.Method] = entry.Operation.DeepClone();
            foreach (var (key, value) in entry.PathItem.Where(pair =>
                         !Methods.Contains(pair.Key) && pathItem[pair.Key] is null))
                pathItem[key] = value?.DeepClone();
        }

        if (additions["components"] is JsonObject components)
        {
            var target = root["components"] as JsonObject ?? (JsonObject)(root["components"] = new JsonObject());
            foreach (var (kind, group) in components)
            {
                if (group is not JsonObject members)
                    continue;
                var bucket = target[kind] as JsonObject ?? (JsonObject)(target[kind] = new JsonObject());
                foreach (var (name, value) in members.Where(member => bucket[member.Key] is null))
                    bucket[name] = value?.DeepClone();
            }
        }

        return Serialize(root);
    }

    public static string Serialize(JsonNode root)
    {
        return root.ToJsonString(Output);
    }

    internal static string? Str(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue(out string? text) ? text : null;
    }

    internal static bool Flag(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue(out bool flag) && flag;
    }

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

    internal static IEnumerable<(string Name, string Path, string Method, JsonObject Operation, JsonObject PathItem)>
        Operations(JsonObject root)
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
                yield return (OperationNames.Unique(Str(operation["operationId"]), method, path, used), path, method,
                    operation, pathItem);
            }
        }
    }

    private static string? Family(string? version)
    {
        return version is { Length: >= 3 } ? version[..3] : version;
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
        var declared = tool.Parameters.Where(parameter => parameter.Place == ParameterPlace.Path)
            .Select(parameter => parameter.Name).ToList();
        foreach (var name in declared.Where(name => !placeholders.Contains(name)))
            throw new PanelException(string.Format(BuilderText.ToolPathParameterMissing, name));
        foreach (var name in placeholders.Where(name => !declared.Contains(name)))
            throw new PanelException(string.Format(BuilderText.ToolPathPlaceholderUndeclared, name));

        foreach (var parameter in tool.Parameters)
        {
            if (string.IsNullOrWhiteSpace(parameter.Name))
                throw new PanelException(BuilderText.ParameterNameRequired);
            if (!Types.Contains(parameter.Type) && parameter.Type != ComplexType)
                throw new PanelException(string.Format(BuilderText.ParameterTypeInvalid, parameter.Name));
        }

        var duplicate = tool.Parameters.GroupBy(parameter => (parameter.Name, parameter.Place))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new PanelException(string.Format(BuilderText.ParameterDuplicate, duplicate.Key.Name));
    }

    private static bool IsSameOperation(JsonObject operation, string name)
    {
        return Str(operation["operationId"]) == name;
    }

    private static void Detach(JsonObject paths, string path, string method)
    {
        if (paths[path] is not JsonObject item)
            return;
        item.Remove(method);
        if (item.Count == 0)
            paths.Remove(path);
    }

    private static FormTool ToTool(JsonObject root, string name, string path, string method, JsonObject operation,
        JsonObject pathItem)
    {
        var parameters = new List<ToolParameter>();
        foreach (var node in Merged(pathItem["parameters"], operation["parameters"]))
        {
            var resolved = Resolve(root, node);
            if (resolved is null || !TryPlace(Str(resolved["in"]), out var place) ||
                Str(resolved["name"]) is not { } parameterName)
                continue;
            parameters.Add(Describe(resolved, place, parameterName));
        }

        if (FormBody(root, operation) is { } body)
        {
            var required = (body["required"] as JsonArray)?.Select(Str).OfType<string>().ToHashSet() ?? [];
            foreach (var (property, value) in body["properties"] as JsonObject ?? [])
            {
                var raw = value as JsonObject;
                var complex = IsComplex(raw, false);
                parameters.Add(new ToolParameter(property, complex ? ComplexType : TypeOf(raw), ParameterPlace.Body,
                    required.Contains(property), complex ? null : raw?["default"]?.DeepClone(),
                    Str(raw?["description"])));
            }
        }

        var description = Str(operation["description"]) ?? Str(operation["summary"]);
        return new FormTool(name, method.ToUpperInvariant(), path, description, parameters,
            ResponseOf(operation)?.DeepClone());
    }

    private static ToolParameter Describe(JsonObject resolved, ParameterPlace place, string name)
    {
        var raw = resolved["schema"] as JsonObject;
        var complex = IsComplex(raw, resolved["content"] is not null);
        return new ToolParameter(
            name,
            complex ? ComplexType : TypeOf(raw),
            place,
            place == ParameterPlace.Path || Flag(resolved["required"]),
            complex ? null : raw?["default"]?.DeepClone(),
            Str(resolved["description"]));
    }

    private static IEnumerable<JsonNode> Merged(JsonNode? inherited, JsonNode? own)
    {
        var overridden = (own as JsonArray ?? []).OfType<JsonObject>()
            .Select(node => (node["name"]?.ToString(), node["in"]?.ToString())).ToHashSet();
        foreach (var node in (inherited as JsonArray ?? []).OfType<JsonObject>().Where(node =>
                     !overridden.Contains((node["name"]?.ToString(), node["in"]?.ToString()))))
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

    private static string PlaceName(ParameterPlace place)
    {
        return place switch
        {
            ParameterPlace.Path => "path",
            ParameterPlace.Query => "query",
            _ => "header"
        };
    }

    private static bool IsComplex(JsonObject? schema, bool hasContent)
    {
        if (schema is null)
            return hasContent;
        if (Combinators.Any(key => schema.ContainsKey(key)))
            return true;
        return Str(schema["type"]) is not { } type || !Types.Contains(type);
    }

    private static string TypeOf(JsonObject? schema)
    {
        return Str(schema?["type"]) is { } type && Types.Contains(type) ? type : "string";
    }

    private static JsonObject? Resolve(JsonObject root, JsonNode? node)
    {
        for (var depth = 0; depth < 8 && node is JsonObject current; depth++)
        {
            if (current["$ref"] is null)
                return current;
            var pointer = Str(current["$ref"]);
            if (pointer is null || !pointer.StartsWith(ComponentsPrefix, StringComparison.Ordinal))
                return null;
            var target = root["components"];
            foreach (var part in pointer[ComponentsPrefix.Length..].Split('/'))
                target = (target as JsonObject)?[part.Replace("~1", "/").Replace("~0", "~")];
            node = target;
        }

        return null;
    }

    private static bool IsFormBody(JsonObject schema)
    {
        return schema["properties"] is JsonObject
               && !schema.ContainsKey("allOf") && !schema.ContainsKey("anyOf") && !schema.ContainsKey("oneOf")
               && (schema["type"] is null || Str(schema["type"]) == "object");
    }

    private static JsonObject? JsonSchemaOf(JsonObject root, JsonObject? operation)
    {
        var body = Resolve(root, operation?["requestBody"]);
        var media = (body?["content"] as JsonObject)?[JsonMedia] as JsonObject;
        return Resolve(root, media?["schema"]);
    }

    private static JsonObject? FormBody(JsonObject root, JsonObject operation)
    {
        return JsonSchemaOf(root, operation) is { } schema && IsFormBody(schema) ? schema : null;
    }

    private static JsonNode? ResponseOf(JsonObject operation)
    {
        var key = SuccessKey(operation);
        var response = key is null ? null : (operation["responses"] as JsonObject)?[key] as JsonObject;
        return ((response?["content"] as JsonObject)?[JsonMedia] as JsonObject)?["schema"];
    }

    private static string? SuccessKey(JsonObject operation)
    {
        if (operation["responses"] is not JsonObject responses)
            return null;
        return responses.ContainsKey("200")
            ? "200"
            : responses.Select(pair => pair.Key).FirstOrDefault(key => key.StartsWith('2'));
    }

    private static bool SameParameter(ToolParameter left, ToolParameter right)
    {
        return (left.Name, left.Type, left.Place, left.Required, left.Description) == (right.Name, right.Type,
                   right.Place, right.Required, right.Description)
               && left.Default?.ToJsonString() == right.Default?.ToJsonString();
    }

    private static JsonObject Compose(JsonObject root, JsonObject? previous, JsonObject? previousPathItem,
        bool samePath, FormTool tool)
    {
        var operation = previous is null ? [] : (JsonObject)previous.DeepClone();
        operation["operationId"] = tool.Name;
        if (string.IsNullOrWhiteSpace(tool.Description))
            operation.Remove("description");
        else
            operation["description"] = tool.Description;

        var existing = Merged(null, previous?["parameters"])
            .Select(node => (Node: node as JsonObject, Resolved: Resolve(root, node))).ToList();
        var inherited = Merged(previousPathItem?["parameters"], null).Select(node => Resolve(root, node))
            .OfType<JsonObject>().ToList();
        var parameters = new JsonArray();
        foreach (var parameter in tool.Parameters.Where(parameter => parameter.Place != ParameterPlace.Body))
        {
            var place = PlaceName(parameter.Place);
            var match = existing.FirstOrDefault(entry =>
                Str(entry.Resolved?["name"]) == parameter.Name && Str(entry.Resolved?["in"]) == place).Resolved;
            if (match is null)
            {
                match = inherited.FirstOrDefault(entry =>
                    Str(entry["name"]) == parameter.Name && Str(entry["in"]) == place);
                if (match is not null && samePath && TryPlace(Str(match["in"]), out var inheritedPlace) &&
                    SameParameter(parameter, Describe(match, inheritedPlace, parameter.Name)))
                    continue;
            }

            var node = match is null ? [] : (JsonObject)match.DeepClone();
            node.Remove("$ref");
            node["name"] = parameter.Name;
            node["in"] = place;
            var required = parameter.Required || parameter.Place == ParameterPlace.Path;
            if (Flag(node["required"]) != required)
                node["required"] = required;
            ApplyDescription(node, parameter.Description);
            if (parameter.Type == ComplexType)
            {
                if (node["schema"] is null && node["content"] is null)
                    node["schema"] = new JsonObject();
            }
            else
            {
                node["schema"] = SchemaOf(node["schema"] as JsonObject, parameter);
            }

            parameters.Add(node);
        }

        foreach (var entry in existing.Where(entry =>
                     entry.Node is not null && (entry.Resolved is null || !TryPlace(Str(entry.Resolved["in"]), out _))))
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
        var content = request?["content"] as JsonObject;
        var hasJson = content?.ContainsKey(JsonMedia) == true;
        var existingSchema = JsonSchemaOf(root, previous);

        if (existingSchema is not null && !IsFormBody(existingSchema))
        {
            if (bodyParameters.Count > 0)
                throw new PanelException(BuilderText.ToolBodyNotEditable);
            return;
        }

        if (bodyParameters.Count == 0)
        {
            if (hasJson)
                operation.Remove("requestBody");
            return;
        }

        var requestBody = request is null ? [] : (JsonObject)request.DeepClone();
        requestBody.Remove("$ref");
        var target = requestBody["content"] as JsonObject ?? (JsonObject)(requestBody["content"] = new JsonObject());
        var media = target[JsonMedia] as JsonObject ?? (JsonObject)(target[JsonMedia] = new JsonObject());
        var schema = existingSchema is null ? [] : (JsonObject)existingSchema.DeepClone();
        schema.Remove("$ref");
        schema["type"] = "object";
        var current = schema["properties"] as JsonObject ?? [];
        var properties = new JsonObject();
        foreach (var parameter in bodyParameters)
        {
            var property = current[parameter.Name] is JsonObject kept && parameter.Type == ComplexType
                ?
                (JsonObject)kept.DeepClone()
                : parameter.Type == ComplexType
                    ? []
                    : SchemaOf(current[parameter.Name] as JsonObject, parameter);
            ApplyDescription(property, parameter.Description);
            properties[parameter.Name] = property;
        }

        schema["properties"] = properties;
        var required = new JsonArray(bodyParameters.Where(parameter => parameter.Required)
            .Select(parameter => (JsonNode)JsonValue.Create(parameter.Name)!).ToArray());
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
            if (key is not null && responses[key] is JsonObject success && success["content"] is JsonObject media &&
                media[JsonMedia] is JsonObject json)
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
        var response = responses[key] as JsonObject ??
                       (JsonObject)(responses[key] = new JsonObject { ["description"] = "OK" });
        response.Remove("$ref");
        var content = response["content"] as JsonObject ?? (JsonObject)(response["content"] = new JsonObject());
        var entry = content[JsonMedia] as JsonObject ?? (JsonObject)(content[JsonMedia] = new JsonObject());
        entry["schema"] = schema.DeepClone();
    }
}

internal static class PythonToolSource
{
    private static readonly JsonSerializerOptions Literal = new()
    { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Render(FormTool tool)
    {
        var function = ProjectFiles.ToolFunctionName(tool.Name);
        var names = new Dictionary<ToolParameter, string>();
        var taken = new HashSet<string>
        {
            function, "json", "os", "quote", "httpx2", "tool", "path", "query", "headers", "body", "base_headers",
            "client", "response"
        };
        foreach (var parameter in tool.Parameters)
        {
            var identifier = ProjectFiles.ToolFunctionName(parameter.Name);
            while (!taken.Add(identifier))
                identifier += "_";
            names[parameter] = identifier;
        }

        var ordered = tool.Parameters
            .OrderBy(parameter => parameter.Required && ScalarDefault(parameter) is null ? 0 : 1).ToList();
        var signature = string.Join(", ", ordered.Select(parameter => Signature(parameter, names[parameter])));
        var builder = new StringBuilder();
        builder.Append(
            "import json\nimport os\nfrom urllib.parse import quote\n\nimport httpx2\nfrom fastmcp.tools import tool\n\n\n");
        builder.Append(function == tool.Name ? "@tool\n" : $"@tool(name={Quote(tool.Name)})\n");
        builder.Append($"async def {function}({signature}):\n");
        if (!string.IsNullOrWhiteSpace(tool.Description))
            builder.Append($"    {Quote(tool.Description.Trim())}\n");
        builder.Append($"    path = {Quote(tool.Path)}\n");
        foreach (var parameter in tool.Parameters.Where(parameter => parameter.Place == ParameterPlace.Path))
            builder.Append(
                $"    path = path.replace({Quote("{" + parameter.Name + "}")}, quote(str({names[parameter]}), safe=\"\"))\n");
        AppendMapping(builder, "query", tool.Parameters, ParameterPlace.Query, names);
        AppendMapping(builder, "headers", tool.Parameters, ParameterPlace.Header, names);
        var hasBody = tool.Parameters.Any(parameter => parameter.Place == ParameterPlace.Body);
        if (hasBody)
            AppendMapping(builder, "body", tool.Parameters, ParameterPlace.Body, names);
        builder.Append("    base_headers = json.loads(os.environ.get(\"BUILDER_API_HEADERS\") or \"{}\")\n");
        builder.Append(
            "    async with httpx2.AsyncClient(base_url=os.environ[\"BUILDER_BASE_URL\"], headers=base_headers, timeout=30.0) as client:\n");
        builder.Append(
            $"        response = await client.request({Quote(tool.Method.ToUpperInvariant())}, path, params=query, headers=headers{(hasBody ? ", json=body" : string.Empty)})\n");
        builder.Append("        response.raise_for_status()\n");
        builder.Append("        return response.json() if response.content else None\n");
        return builder.ToString();
    }

    private static string Signature(ToolParameter parameter, string identifier)
    {
        if (parameter.Type == OpenApiDraft.ComplexType)
            return parameter.Required ? identifier : $"{identifier}=None";
        var type = parameter.Type switch
        {
            "integer" => "int",
            "number" => "float",
            "boolean" => "bool",
            "array" => "list",
            "object" => "dict",
            _ => "str"
        };
        if (ScalarDefault(parameter) is { } value)
            return $"{identifier}: {type} = {PythonValue(value)}";
        return parameter.Required ? $"{identifier}: {type}" : $"{identifier}: {type} | None = None";
    }

    private static JsonNode? ScalarDefault(ToolParameter parameter)
    {
        return parameter.Default?.GetValueKind() is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True
            or JsonValueKind.False
            ? parameter.Default
            : null;
    }

    private static string PythonValue(JsonNode node)
    {
        return node.GetValueKind() switch
        {
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            _ => node.ToJsonString(Literal)
        };
    }

    private static void AppendMapping(StringBuilder builder, string variable, IReadOnlyList<ToolParameter> parameters,
        ParameterPlace place, Dictionary<ToolParameter, string> names)
    {
        var entries = string.Join(", ",
            parameters.Where(parameter => parameter.Place == place)
                .Select(parameter => $"{Quote(parameter.Name)}: {names[parameter]}"));
        var value = place == ParameterPlace.Header
            ? "(str(value).lower() if isinstance(value, bool) else str(value))"
            : "value";
        builder.Append(
            $"    {variable} = {{key: {value} for key, value in {{{entries}}}.items() if value is not None}}\n");
    }

    private static string Quote(string value)
    {
        return JsonSerializer.Serialize(value, Literal);
    }
}
