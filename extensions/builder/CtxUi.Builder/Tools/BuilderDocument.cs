using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Enfilade;

namespace CtxUi.Builder.Tools;

public sealed record BuilderResource(string Uri, string Name, string MimeType, string Text);

public sealed record BuilderPrompt(string Name, string? Description, string Template);

public sealed record BuilderDocumentModel(
    IReadOnlyList<BuilderResource> Resources,
    IReadOnlyList<BuilderPrompt> Prompts)
{
    public static BuilderDocumentModel Empty { get; } = new([], []);

    public bool IsEmpty => Resources.Count == 0 && Prompts.Count == 0;
}

public static partial class BuilderDocument
{
    public const string DefaultMimeType = "text/plain";
    public const int MaxNameLength = 64;

    private static readonly JsonSerializerOptions Output = new()
    { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [GeneratedRegex(@"\{([A-Za-z_]\w*)\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^[A-Za-z0-9][\w.+-]*/[A-Za-z0-9][\w.+-]*$")]
    private static partial Regex MimeTypePattern();

    [GeneratedRegex(@"^\S+$")]
    private static partial Regex PromptNamePattern();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.-]*:")]
    private static partial Regex UriSchemePattern();

    public static IReadOnlyList<string> Arguments(string template)
    {
        return
        [
            .. Placeholder().Matches(template).Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal)
        ];
    }

    public static BuilderDocumentModel Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return BuilderDocumentModel.Empty;

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            throw Invalid();
        }

        if (parsed is not JsonObject root)
            throw Invalid();

        var model = new BuilderDocumentModel(
            [
                .. Entries(root, "resources").Select(entry => new BuilderResource(
                    Required(entry, "uri"),
                    Required(entry, "name"),
                    Optional(entry, "mimeType") ?? DefaultMimeType,
                    Optional(entry, "text") ?? string.Empty))
            ],
            [
                .. Entries(root, "prompts").Select(entry => new BuilderPrompt(
                    Required(entry, "name"),
                    Optional(entry, "description"),
                    Required(entry, "template")))
            ]);
        Validate(model);
        return model;
    }

    public static string Write(BuilderDocumentModel model)
    {
        Validate(model);
        var root = new JsonObject
        {
            ["resources"] = new JsonArray([
                .. model.Resources.Select(resource => (JsonNode)new JsonObject
                {
                    ["uri"] = resource.Uri,
                    ["name"] = resource.Name,
                    ["mimeType"] = resource.MimeType,
                    ["text"] = resource.Text
                })
            ]),
            ["prompts"] = new JsonArray([
                .. model.Prompts.Select(prompt => (JsonNode)new JsonObject
                {
                    ["name"] = prompt.Name,
                    ["description"] = prompt.Description,
                    ["template"] = prompt.Template
                })
            ])
        };
        return root.ToJsonString(Output);
    }

    public static BuilderDocumentModel WithResource(BuilderDocumentModel model, BuilderResource resource,
        string? replacing)
    {
        var resources = model.Resources.ToList();
        var index = replacing is null ? -1 : resources.FindIndex(existing => existing.Uri == replacing);
        if (replacing is not null && index < 0)
            throw new PanelException(string.Format(BuilderText.ResourceNotFound, replacing));
        if (resources.Where((existing, position) => position != index).Any(existing => existing.Uri == resource.Uri))
            throw new PanelException(string.Format(BuilderText.ResourceUriTaken, resource.Uri));

        if (index < 0)
            resources.Add(resource);
        else
            resources[index] = resource;
        var changed = model with { Resources = resources };
        Validate(changed);
        return changed;
    }

    public static BuilderDocumentModel WithoutResource(BuilderDocumentModel model, string uri)
    {
        if (model.Resources.All(resource => resource.Uri != uri))
            throw new PanelException(string.Format(BuilderText.ResourceNotFound, uri));
        return model with { Resources = [.. model.Resources.Where(resource => resource.Uri != uri)] };
    }

    public static BuilderDocumentModel WithPrompt(BuilderDocumentModel model, BuilderPrompt prompt, string? replacing)
    {
        var prompts = model.Prompts.ToList();
        var index = replacing is null ? -1 : prompts.FindIndex(existing => existing.Name == replacing);
        if (replacing is not null && index < 0)
            throw new PanelException(string.Format(BuilderText.PromptNotFound, replacing));
        if (prompts.Where((existing, position) => position != index).Any(existing => existing.Name == prompt.Name))
            throw new PanelException(string.Format(BuilderText.PromptNameTaken, prompt.Name));

        if (index < 0)
            prompts.Add(prompt);
        else
            prompts[index] = prompt;
        var changed = model with { Prompts = prompts };
        Validate(changed);
        return changed;
    }

    public static BuilderDocumentModel WithoutPrompt(BuilderDocumentModel model, string name)
    {
        if (model.Prompts.All(prompt => prompt.Name != name))
            throw new PanelException(string.Format(BuilderText.PromptNotFound, name));
        return model with { Prompts = [.. model.Prompts.Where(prompt => prompt.Name != name)] };
    }

    private static void Validate(BuilderDocumentModel model)
    {
        foreach (var resource in model.Resources)
        {
            if (!UriSchemePattern().IsMatch(resource.Uri) || !Uri.TryCreate(resource.Uri, UriKind.Absolute, out _) ||
                resource.Uri.Any(char.IsWhiteSpace))
                throw new PanelException(string.Format(BuilderText.ResourceUriInvalid, resource.Uri));
            if (string.IsNullOrWhiteSpace(resource.Name) || resource.Name.Length > MaxNameLength)
                throw new PanelException(string.Format(BuilderText.BuilderNameInvalid, resource.Name));
            if (!MimeTypePattern().IsMatch(resource.MimeType))
                throw new PanelException(string.Format(BuilderText.ResourceMimeTypeInvalid, resource.MimeType));
        }

        foreach (var prompt in model.Prompts)
        {
            if (!PromptNamePattern().IsMatch(prompt.Name) || prompt.Name.Length > MaxNameLength)
                throw new PanelException(string.Format(BuilderText.BuilderNameInvalid, prompt.Name));
            if (string.IsNullOrWhiteSpace(prompt.Template))
                throw new PanelException(string.Format(BuilderText.PromptTemplateRequired, prompt.Name));
        }

        if (model.Resources.GroupBy(resource => resource.Uri).FirstOrDefault(group => group.Count() > 1) is
            { } uriClash)
            throw new PanelException(string.Format(BuilderText.ResourceUriTaken, uriClash.Key));
        if (model.Prompts.GroupBy(prompt => prompt.Name).FirstOrDefault(group => group.Count() > 1) is { } nameClash)
            throw new PanelException(string.Format(BuilderText.PromptNameTaken, nameClash.Key));
    }

    private static IEnumerable<JsonObject> Entries(JsonObject root, string key)
    {
        if (root[key] is null)
            return [];
        if (root[key] is not JsonArray array)
            throw Invalid();
        return array.Select(item => item as JsonObject ?? throw Invalid());
    }

    private static string Required(JsonObject entry, string key)
    {
        return Optional(entry, key) ?? throw Invalid();
    }

    private static string? Optional(JsonObject entry, string key)
    {
        if (entry[key] is null)
            return null;
        return entry[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : throw Invalid();
    }

    private static PanelException Invalid()
    {
        return new PanelException(BuilderText.BuilderDocumentInvalid);
    }
}
