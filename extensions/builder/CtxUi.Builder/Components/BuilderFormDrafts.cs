using System.ComponentModel.DataAnnotations;
using CtxUi.Builder.Tools;

namespace CtxUi.Builder.Components;

public sealed class ResourceFormDraft
{
    [Required]
    public string Uri { get; set; } = string.Empty;

    [Required]
    public string Name { get; set; } = string.Empty;

    [Required]
    public string MimeType { get; set; } = BuilderDocument.DefaultMimeType;

    public string? Text { get; set; }

    public static ResourceFormDraft From(BuilderResource resource) => new()
    {
        Uri = resource.Uri,
        Name = resource.Name,
        MimeType = resource.MimeType,
        Text = resource.Text,
    };

    public BuilderResource ToResource() => new(Uri.Trim(), Name.Trim(), MimeType.Trim(), Text ?? string.Empty);
}

public sealed class PromptFormDraft
{
    [Required]
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    [Required]
    public string Template { get; set; } = string.Empty;

    public IReadOnlyList<string> Arguments => BuilderDocument.Arguments(Template);

    public static PromptFormDraft From(BuilderPrompt prompt) => new()
    {
        Name = prompt.Name,
        Description = prompt.Description,
        Template = prompt.Template,
    };

    public BuilderPrompt ToPrompt() => new(Name.Trim(), string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(), Template);
}
